namespace QControl.Core.Devices;

/// <summary>
/// 风扇转速标定的结论。<see cref="Count"/> = 认出了几路（0 = 这段没有证据）。
///
/// 与 <see cref="LayoutAdvice"/> 分开的原因：温度靠**相关**认（跟 CPU 功耗/显卡温度一起动），
/// 转速靠**形状**认（一段平滑的升速/降速过程）。两条判据完全不同，混在一个函数里
/// 会让阈值互相牵制 —— 温度那套阈值是按 ℃ 定的，套到 0~9000 的转速上一条都不成立。
/// </summary>
public sealed record FanAdvice(
    IReadOnlyDictionary<string, (int Offset, int Len)> Layout,
    IReadOnlyList<LayoutGuess> Guesses,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Missing,
    int Count)
{
    public static FanAdvice Empty { get; } = new(
        new Dictionary<string, (int, int)>(StringComparer.Ordinal), [], [], [], 0);
}

/// <summary>
/// 风扇转速偏移的自动标定 —— 纯逻辑（给定一串带时间戳的帧，不碰 EC）。
///
/// 为什么需要单独一段：`LayoutAdvisor` 认转速的条件是「值域 200~9000 ∧ 幅度 ≥20 ∧ 至少 3 个不同值」，
/// 那要求**采样窗口里风扇刚好在变速**。而一键采集的 20 秒采样多数发生在空闲状态，
/// 风扇定在某个转速一动不动 ⇒ 包里永远没有 `fan1_rpm` ⇒ 适配完的机器上看不到转速、
/// 风扇曲线也没有任何反馈。这里的输入就是**引导里「风扇」那一步**期间采到的帧
/// （用户正好在那时候把风扇拉起来 / 切 Max），变速过程天然发生在窗口里。
///
/// 判据（三条一起成立才认）：
///   · 值域：峰值 600~9000 —— 转速是四位数，低于 600 的多半是档位或计数；
///   · 变化量：≥200 且至少 4 个不同取值 —— 风扇从怠速到半速就是这个量级；
///   · **平滑**：最大单步变化不超过总变化量的 55%（且不超过 90）。这一条是专门用来
///     挡显卡频率的：GPU 时钟在 210 / 1800 之间一步跳过去，而风扇有惯性，
///     同一个采样间隔里只会挪一两百转。
///
/// 两路都在动时再看它们**是否同步**：真风扇是一起升一起降（相关 ≥0.5）；
/// 不同步的两路里必有一路不是转速，那就只认变化更大的那一路 —— 认错一路的代价是
/// 界面上多画一个假的转速表，比少画一个糟得多。
/// </summary>
public static class FanLayoutAdvisor
{
    /// <summary>至少几个样本才看形状（少于这个数，"平滑"这件事本身就是噪声）。</summary>
    public const int MinSamples = 5;

    /// <summary>
    /// 最多认几路转速。三风扇机型存在（EC 的 status 帧有 7 个 u32，放得下），
    /// 但界面与曲线仍是两路 —— 第三路只记录、不显示（见认到三路时的那条 note）。
    /// </summary>
    public const int MaxFans = 3;

    /// <summary>
    /// 偏移 → 在帧里的下标（DAT2..DAT8 = 4/8/12/16/20/24/28）。
    ///
    /// 前 5 个是标准槽；@24/@28 是给别的模具的兜底（温度/转速放在数据区尾部的情况）。
    /// 判据（四位数、>200 转、逐帧平滑）本身就把这两个槽上的 0 / 小整数挡住了，
    /// 所以这里不用像 <see cref="EcLayoutAdvisor"/> 那样分两遍扫。
    /// </summary>
    private static readonly (int Offset, int Index)[] Offsets =
        [(4, 0), (8, 1), (12, 2), (16, 3), (20, 4), (24, 5), (28, 6)];

    /// <summary>一条候选：某个 (命令, 偏移) 的整段序列。</summary>
    private sealed record Candidate(string Cmd, int Offset, List<double> Series,
                                   double Min, double Max, int Distinct, double MaxJump)
    {
        public double Delta => Max - Min;
    }

    /// <summary>
    /// 从一段帧里认风扇转速。
    /// </summary>
    /// <param name="samples">风扇那一步采到的帧（参照可以是 NaN —— 这里一条都不用）。</param>
    /// <param name="taken">已经认给别的字段的 (命令, 偏移)。温度/背光占掉的偏移不再当转速。</param>
    /// <param name="preferCmd">优先从哪条命令里找（一般是认出 CPU 温度的那条：转速和温度通常在同一帧）。</param>
    public static FanAdvice Advise(IReadOnlyList<LayoutSample> samples,
                                   IEnumerable<(string Cmd, int Offset)>? taken = null,
                                   string? preferCmd = null)
    {
        var notes = new List<string>();
        var missing = new List<string>();
        var layout = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var guesses = new List<LayoutGuess>();

        if (samples.Count < MinSamples)
        {
            notes.Add($"风扇转速标定只拿到 {samples.Count} 个样本（要 {MinSamples} 个以上）：这一段没标定。");
            missing.Add("风扇转速：采样不足（引导里「风扇」那一步可能没走到，或观察提前结束）");
            return new FanAdvice(layout, guesses, notes, missing, 0);
        }

        var used = new HashSet<(string, int)>(taken ?? []);
        var cands = new List<Candidate>();

        var cmds = LayoutFrames.Commands(samples);
        foreach (var cmd in cmds)
        {
            foreach (var (offset, idx) in Offsets)
            {
                if (used.Contains((cmd, offset))) continue;

                var series = new List<double>(samples.Count);
                foreach (var s in samples)
                    if (s.Frames.TryGetValue(cmd, out var vals) && idx < vals.Length)
                        series.Add(vals[idx]);
                if (series.Count < MinSamples) continue;

                var min = series.Min();
                var max = series.Max();
                var delta = max - min;
                var distinct = series.Distinct().Count();
                var jump = MaxJump(series);

                if (max is < 600 or > 9000) continue;          // 转速是四位数
                if (delta < 200 || distinct < 4) continue;      // 没真变速
                if (jump > Math.Max(90, delta * 0.55)) continue; // 一步跳过去 = 频率/时钟，不是风扇

                cands.Add(new Candidate(cmd, offset, series, min, max, distinct, jump));
            }
        }

        // 同一条命令里的优先：转速和温度共用 `status` 帧是本项目的既有事实，
        // 别的命令里出现的"像转速"的字段多半是电压/频率。
        if (preferCmd is { Length: > 0 } && cands.Any(c => c.Cmd == preferCmd))
        {
            var dropped = cands.Where(c => c.Cmd != preferCmd).ToList();
            cands = cands.Where(c => c.Cmd == preferCmd).ToList();
            if (dropped.Count > 0 && cands.Count > 0)
                notes.Add($"另有 {dropped.Count} 个偏移的值域也像转速，但不在 `{preferCmd}` 帧里（转速与温度通常同帧），"
                          + "按同帧那条认。");
        }

        cands = cands.OrderByDescending(c => c.Delta).ToList();

        // 最多认三路（三风扇机型存在，EC 的 status 帧也放得下 7 个 u32）。
        // 多出来的不认，但要说一声 —— 也可能是别的字段混进来了。
        if (cands.Count > MaxFans)
            notes.Add($"这段有 {cands.Count} 个偏移像转速（{string.Join("、", cands.Select(c => c.Cmd + "@" + c.Offset))}）："
                      + $"只认变化最大的 {MaxFans} 路，其余留给作者按原厂界面复核。");

        var picked = new List<Candidate>();
        var sync = double.NaN;
        foreach (var c in cands)
        {
            if (picked.Count >= MaxFans) break;
            if (picked.Count == 0) { picked.Add(c); continue; }

            // 每一路都要和**第一路**同步：真风扇是一起升一起降的（同一套散热逻辑），
            // 不同步的那一路多半是别的字段（频率、计数器），认进来就是假转速。
            var corr = Corr(picked[0].Series, c.Series);
            if (double.IsNaN(corr) || corr >= 0.5)
            {
                picked.Add(c);
                if (!double.IsNaN(corr)) sync = double.IsNaN(sync) ? corr : Math.Min(sync, corr);
            }
            else if (picked.Count == 1)
            {
                notes.Add($"另有一路（{c.Cmd}@{c.Offset}）值域也像转速，但和这一路不同步（相关 {corr:0.00}）："
                          + "只认变化更大的那一路，另一路不当转速。");
            }
            else
            {
                notes.Add($"{c.Cmd}@{c.Offset} 与前两路不同步（相关 {corr:0.00}），不当转速。");
            }
        }

        if (picked.Count == 0)
        {
            notes.Add("这段帧里没有像转速的字段在变：风扇可能压根没转起来（原厂那一步没拉到 Max，"
                      + "或者这台的风扇转速不在 `status` 帧里）。");
            missing.Add("风扇转速：这段没有平滑变速的字段（去原厂把风扇拉到 Max 再采一次）");
            return new FanAdvice(layout, guesses, notes, missing, 0);
        }

        picked = picked.OrderBy(c => c.Offset).ToList();
        var names = new[] { "fan1_rpm", "fan2_rpm", "fan3_rpm" }.Take(picked.Count).ToArray();
        // 多路同步（相关 ≥0.8）时给 high：那是同一套散热逻辑在驱动多个风扇的形状；
        // 单路只能给 medium —— 形状对得上，但没有第二路交叉印证。
        var conf = picked.Count >= 2 && !double.IsNaN(sync) && sync >= 0.8 ? "high" : "medium";

        for (var i = 0; i < picked.Count; i++)
        {
            var c = picked[i];
            layout[names[i]] = (c.Offset, 4);
            guesses.Add(new LayoutGuess(names[i], c.Cmd, c.Offset, conf,
                $"值域 {c.Min:0}~{c.Max:0}（幅度 {c.Delta:0}）逐帧平滑变化，最大单步 {c.MaxJump:0}"
                + "；由「风扇拉起来那一段」的形状认出，不是按值域瞎猜"));
        }

        notes.Add("风扇转速认在 " + string.Join("、", picked.Select((c, i) =>
            $"{names[i]} ← {c.Cmd}@{c.Offset}（{c.Min:0}~{c.Max:0} 转）")) + "。");
        if (picked.Count == 2)
            notes.Add("哪一路对 CPU、哪一路对 GPU 是按偏移顺序假设的（与内置档案同一口径）："
                      + "散热页两个数字跟原厂对不上就说明反了。");
        else if (picked.Count >= 3)
            notes.Add($"认到 {picked.Count} 路转速：按偏移升序记作 fan1/fan2/fan3，"
                      + "**但界面与曲线目前只画 CPU/GPU 两路** —— 第三路的价值是让作者知道这台是三风扇，"
                      + "以及它在 EC 里的位置；要真正显示/调速得先做三风扇的界面与曲线。");

        return new FanAdvice(layout, guesses, notes, missing, picked.Count);
    }

    /// <summary>相邻两帧差值的最大值 —— "一步跳到底"和"一点点挪上去"就靠它分开。</summary>
    private static double MaxJump(IReadOnlyList<double> series)
    {
        double max = 0;
        for (var i = 1; i < series.Count; i++)
            max = Math.Max(max, Math.Abs(series[i] - series[i - 1]));
        return max;
    }

    /// <summary>Pearson 相关；方差为 0（一路压根没动）时返回 NaN，不当 0 用。</summary>
    private static double Corr(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n < MinSamples) return double.NaN;

        double ma = 0, mb = 0;
        for (var i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;

        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = a[i] - ma;
            var dy = b[i] - mb;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 1e-9 || syy <= 1e-9) return double.NaN;
        return sxy / Math.Sqrt(sxx * syy);
    }
}
