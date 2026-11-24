using System.Globalization;

namespace QControl.Core.Devices;

/// <summary>
/// 一次采样里的一帧：EC 各读命令的 7 个 u32（DAT2..DAT8 = 偏移 4/8/12/16/20/24/28）
/// + **同一时刻**的原生参照读数。
///
/// 参照是自动标定的全部依据：EC 里哪个偏移在动不能说明它是什么，
/// 得看它跟谁一起动 —— CPU 功耗涨它就涨 ⇒ 多半是 CPU 温度；
/// 跟 NVML 的显卡温度一起涨 ⇒ 多半是 GPU 温度。没有参照就只剩"它在变"，
/// 那种结论不值得写进档案（项目规矩：宁可不显示，也不显示错的）。
///
/// 参照缺失用 <see cref="double.NaN"/>，不要用 0 顶 —— 0 W 的 CPU 功耗是合法读数。
/// </summary>
public sealed record LayoutSample(
    double CpuPowerW,
    double CpuTempC,
    double GpuTempC,
    IReadOnlyDictionary<string, uint[]> Frames);

/// <summary>某个 (命令, 偏移) 在整段采样里的统计。相关性取绝对值最大的那个参照。</summary>
public sealed record OffsetStat(
    string Cmd,
    int Offset,
    double Min,
    double Max,
    int Distinct,
    double CorrPower,
    double CorrCpu,
    double CorrGpu)
{
    public double Delta => Max - Min;

    /// <summary>绝对值最大的相关系数（与负载反向的字段也有信息量，所以取绝对值）。</summary>
    public double BestCorr => new[] { CorrPower, CorrCpu, CorrGpu }
        .Where(c => !double.IsNaN(c))
        .Select(Math.Abs)
        .DefaultIfEmpty(double.NaN)
        .Max();

    /// <summary>最强相关的参照叫什么（写进"为什么这么判"）。</summary>
    public string BestRef
    {
        get
        {
            var best = double.NaN;
            var name = "";
            foreach (var (n, c) in new[] { ("CPU 功耗", CorrPower), ("CPU 温度", CorrCpu), ("显卡温度", CorrGpu) })
                if (!double.IsNaN(c) && (double.IsNaN(best) || Math.Abs(c) > best)) { best = Math.Abs(c); name = n; }
            return name;
        }
    }
}

/// <summary>一条推断结果。<paramref name="Confidence"/> = high / medium / low。</summary>
public sealed record LayoutGuess(string Field, string Cmd, int Offset, string Confidence, string Why);

/// <summary>
/// 自动标定的结论。
///
/// <paramref name="Layout"/> 只放**够格填进档案**的项；<paramref name="LayoutVerified"/> 只有在
/// CPU 与 GPU 两路温度都高置信认出来时才为 true —— 那正是 `read_status()` 肯展示读数的门槛。
/// <paramref name="Missing"/> 是给用户看的一句话：还差什么、该怎么补。
/// </summary>
public sealed record LayoutAdvice(
    IReadOnlyList<OffsetStat> Stats,
    IReadOnlyList<LayoutGuess> Guesses,
    IReadOnlyDictionary<string, (int Offset, int Len)> Layout,
    IReadOnlyList<string> Notes,
    bool LayoutVerified,
    IReadOnlyList<string> Missing);

/// <summary>
/// EC 偏移自动推断 —— 纯逻辑（给定一串 <see cref="LayoutSample"/>，不碰 EC / 注册表）。
///
/// 它解决的是适配流程里唯一"必须人工看"的那一步（docs/DEVICES.md 第二步：空载/负载各采一次、
/// 逐行对比哪个偏移在动）。人做的判断其实只有一条：**这东西跟什么一起动**。
/// 那就用相关性替人做，并把"为什么这么判"（跟谁相关、相关多少）一并写出来，
/// 让作者能复核 —— 而不是给一个不带理由的结论。
///
/// 三条保守原则：
///   · 没有参照（功耗与显卡温度都读不到）就不判温度，只说"在变"；
///   · 两个温度都很相关、分不清谁是 CPU 谁是 GPU 时，**不猜**，降级成 medium 交给人；
///   · 采样期间温度压根没动 ⇒ 如实说"这次采不到温度证据"，绝不用别的证据凑。
/// </summary>
public static class EcLayoutAdvisor
{
    /// <summary>至少几帧才值得算相关性（再多也都是噪声）。</summary>
    public const int MinSamples = 6;

    /// <summary>
    /// 标准槽：偏移 → 档案里的字段长度。顺序即输出顺序。
    ///
    /// 这 5 个是本机 NL5（以及大部分广达模具）放温度/转速/背光的地方，**一切判据都以它为准**。
    /// 别的机型先跑这一遍；一路温度都认不出来时才轮到 <see cref="TailOffsets"/>。
    /// </summary>
    private static readonly (int Offset, int Len)[] StandardOffsets = [(4, 4), (8, 4), (12, 4), (16, 4), (20, 4)];

    /// <summary>
    /// 数据区后半段的两个 u32（@24/@28）—— **只做兜底**，不参与第一次判断。
    ///
    /// 为什么要有（2026-11-16）：32 字节帧去掉 4 字节命令头还剩 7 个 u32，早先只看前 5 个，
    /// 是照着 NL5 的放法定下来的。别的模具完全可能把温度/转速放在 @24/@28，
    /// 那样无论采多少帧都认不出来，报告只会写"这段采样里没有任何偏移像温度"。
    ///
    /// 为什么只在标准槽没结论时才扫：NL5 上这一遍一次都不会跑，
    /// 于是"多扫两个槽可能多出两个假候选"的风险完全不落在已验证机型上。
    /// </summary>
    private static readonly (int Offset, int Len)[] TailOffsets = [(24, 4), (28, 4)];

    /// <summary>
    /// 偏移自动推断。先按标准槽（@4~@20）判；**一路温度都没认出来**时才补扫数据区后半段。
    /// </summary>
    public static LayoutAdvice Advise(IReadOnlyList<LayoutSample> samples)
    {
        var primary = AdviseCore(samples, StandardOffsets);
        if (primary.Layout.ContainsKey("cpu_temp") || primary.Layout.ContainsKey("gpu_temp"))
            return primary;

        // 标准槽没温度 ⇒ 这台可能把温度放在数据区后半段。补扫一遍，
        // 只把标准槽**没有**的字段并进来（同名字段一律以标准槽为准）。
        var tail = AdviseCore(samples, TailOffsets);
        return MergeTail(primary, tail);
    }

    /// <summary>
    /// 把「数据区后半段（@24/@28）」那一遍的结论并进标准槽的结论里。
    ///
    /// 规则只有一条：**只补缺** —— 同名字段一律以标准槽为准（那是本机与多数模具的放法，
    /// 也是所有判据长期验证过的地方）。另外必须清掉标准槽那几句"没有偏移像温度 /
    /// 跑个负载再采一次"：它们描述的是"标准槽没认出来"那一刻的状态，
    /// 尾部槽已经认出来的话，同一份报告里既写"一个都没认出来"又写"认在 @24"就是自相矛盾。
    /// </summary>
    private static LayoutAdvice MergeTail(LayoutAdvice primary, LayoutAdvice tail)
    {
        if (tail.Layout.Count == 0) return primary;

        var layout = new Dictionary<string, (int, int)>(primary.Layout, StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var (field, slot) in tail.Layout)
        {
            if (layout.ContainsKey(field)) continue;
            layout[field] = slot;
            added.Add(field);
        }
        if (added.Count == 0) return primary;

        var guesses = primary.Guesses.ToList();
        guesses.AddRange(tail.Guesses.Where(g => added.Contains(g.Field)));

        var notes = primary.Notes.ToList();
        notes.RemoveAll(n => n.StartsWith("没有偏移落在 15~130", StringComparison.Ordinal)
                          || n.StartsWith("温度候选最多只变化了", StringComparison.Ordinal));
        notes.Add("标准槽（@4/@8/@12/@16/@20）没认出 " + string.Join("/", added)
                  + "，扫了数据区后半段（@24/@28）后认出来了 —— "
                  + "这台机器的字段放在数据区尾部，档案里记的就是那个偏移。");

        var missing = primary.Missing.ToList();
        foreach (var field in added)
        {
            var label = field switch
            {
                "cpu_temp" => "CPU 温度",
                "gpu_temp" => "显卡温度",
                "backlight_level" => "键盘背光",
                _ when field.StartsWith("fan", StringComparison.Ordinal) => "风扇转速",
                _ => "",
            };
            if (label.Length > 0) missing.RemoveAll(m => m.StartsWith(label, StringComparison.Ordinal));
        }
        if (added.Exists(f => f is "cpu_temp" or "gpu_temp"))
            missing.RemoveAll(m => m.StartsWith("温度偏移", StringComparison.Ordinal));

        // 与 AdviseCore 同一门槛：认出温度或背光就算"能显示读数"；转速不算（它不影响读数展示）。
        var verified = primary.LayoutVerified
                       || layout.ContainsKey("cpu_temp") || layout.ContainsKey("gpu_temp")
                       || layout.ContainsKey("backlight_level");

        // 「保持 false」/「只认出了 X，另一路没认出来」这两句描述的是**合并前**那一刻。
        // 只有这次合并真的补上了温度/背光（让"能不能显示读数"这件事变了的字段）才需要
        // 按新事实改写；只补了一路转速时它们仍然成立 —— 无条件撤掉是"该留的没留"（2026-11-23 审查）。
        if (added.Exists(f => f is "cpu_temp" or "gpu_temp" or "backlight_level"))
        {
            notes = WithoutStaleVerifiedNote(notes);
            notes.RemoveAll(n => n.StartsWith("只认出了", StringComparison.Ordinal));
            var hasCpu = layout.ContainsKey("cpu_temp");
            var hasGpu = layout.ContainsKey("gpu_temp");
            if (hasCpu ^ hasGpu) notes.Add(OnlyOneNote(hasCpu ? "CPU 温度" : "显卡温度"));
        }

        return primary with
        {
            Stats = primary.Stats.Concat(tail.Stats).ToList(),
            Guesses = guesses,
            Layout = layout,
            Notes = notes,
            Missing = missing,
            LayoutVerified = verified,
        };
    }

    /// <summary>按给定槽位跑一遍完整推断。</summary>
    private static LayoutAdvice AdviseCore(IReadOnlyList<LayoutSample> samples,
                                           IReadOnlyList<(int Offset, int Len)> offsets)
    {
        var notes = new List<string>();
        var guesses = new List<LayoutGuess>();
        var stats = new List<OffsetStat>();
        var layout = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var missing = new List<string>();

        if (samples.Count < MinSamples || samples[0].Frames.Count == 0)
        {
            notes.Add(samples.Count == 0 || samples[0].Frames.Count == 0
                ? "这一轮没采到任何 EC 帧。"
                : $"采样点只有 {samples.Count} 个（至少要 {MinSamples} 个才够算相关性）：这次只留帧快照。");
            return new LayoutAdvice(stats, guesses, layout, notes, false, ["温度偏移：采样不足，无法推断"]);
        }

        // ---------------- 逐个 (命令, 偏移) 统计 ----------------
        var temps = new List<OffsetStat>();
        var rpms = new List<OffsetStat>();
        var lights = new List<OffsetStat>();

        foreach (var cmd in LayoutFrames.Commands(samples))
        {
            for (var idx = 0; idx < offsets.Count; idx++)
            {
                // ⚠️ 帧数组下标 ≠ 本遍的槽序号：数组永远按 DAT2..DAT8（@4..@28）排，
                //    尾部那一遍传进来的 offsets 只有 (24,4)/(28,4)，直接拿 idx 会读到 @4/@8。
                var (slotOffset, _) = offsets[idx];
                var fi = slotOffset / 4 - 1;

                var series = new List<double>(samples.Count);
                // ⚠️ 与 series 一一对应的样本子集：某些样本缺这条命令的帧（EC 被原厂抢占时
                //    读失败就会缺键），直接拿完整 samples 去配 series 会整体错位一位，
                //    相关性算出来的就是另一条无关序列 ⇒ 把功耗/频率寄存器判成"温度"。
                var used = new List<LayoutSample>(samples.Count);
                foreach (var s in samples)
                {
                    if (!s.Frames.TryGetValue(cmd, out var vals) || vals.Length <= fi) continue;
                    series.Add(vals[fi]);
                    used.Add(s);
                }
                if (series.Count < MinSamples) continue;

                var stat = new OffsetStat(
                    cmd, slotOffset,
                    Min: series.Min(), Max: series.Max(),
                    Distinct: series.Distinct().Count(),
                    CorrPower: Corr(series, used, s => s.CpuPowerW),
                    CorrCpu: Corr(series, used, s => s.CpuTempC),
                    CorrGpu: Corr(series, used, s => s.GpuTempC));

                stats.Add(stat);
                Classify(stat, temps, rpms, lights);
            }
        }

        // ---------------- 温度：先看「同刻绝对对齐」，再看相关性 ----------------
        //
        // 顺序不能反。稳态采样（采集默认那 20 秒）里温度常常**一动不动**：方差为 0 ⇒
        // Pearson 返回 NaN ⇒ 相关性这条路一个候选都没有。但绝对值完全对得上 ——
        // EC 的 58 就是 HWiNFO 的 58，逐帧都是。2026-11-14 真机踩到的正是这个：
        // status@4 与 CPU Package 逐帧相等，报告却写"这段采样里没有任何偏移像温度，
        // 跑个负载再采一次"，用户白跑一轮还是同样的结论。
        //
        // 这条证据比相关性更硬（相等就是相等，不靠方差），代价是它只在参照**可信**时成立，
        // 所以另配三条守则：值域必须像温度、两路参照要分得开、参照不动时区间收窄。
        const double strongLine = 0.75;
        const double weakLine = 0.60;
        const double sep = 0.15;

        var aligned = AlignedTemps(samples, offsets);
        var taken = new HashSet<(string Cmd, int Offset)>();
        LayoutGuess? cpu = null, gpu = null;
        foreach (var g in aligned)
        {
            taken.Add((g.Cmd, g.Offset));
            guesses.Add(g);
            if (g.Field == "cpu_temp") cpu = g;
            else if (g.Field == "gpu_temp") gpu = g;
        }

        var scored = temps
            .Where(t => !taken.Contains((t.Cmd, t.Offset)))
            .Select(t => (Stat: t, C: Math.Max(Abs(t.CorrPower), Abs(t.CorrCpu)), G: Abs(t.CorrGpu)))
            .OrderByDescending(x => Math.Max(x.C, x.G))
            .ToList();

        foreach (var (t, c, g) in scored)
        {
            if (Math.Max(c, g) < weakLine)
            {
                guesses.Add(new("temp", t.Cmd, t.Offset, "low",
                    Why(t) + $"；相关不到 {weakLine:0.00} 的判定线，不填"));
                continue;
            }

            var conf = Math.Max(c, g) >= strongLine ? "high" : "medium";

            if (g > c + sep && gpu is null && g >= weakLine)
            {
                var guess = new LayoutGuess("gpu_temp", t.Cmd, t.Offset, conf, Why(t) + MediumHint(conf));
                gpu = guess;
                guesses.Add(guess);
            }
            else if (c > g + sep && cpu is null && c >= weakLine)
            {
                var guess = new LayoutGuess("cpu_temp", t.Cmd, t.Offset, conf, Why(t) + MediumHint(conf));
                cpu = guess;
                guesses.Add(guess);
            }
            else
            {
                guesses.Add(new("temp", t.Cmd, t.Offset, "medium",
                    Why(t) + (gpu is not null || cpu is not null
                        ? "；也像温度，但该路已经有了更确定的候选"
                        : "；与 CPU、显卡都相关，分不清是哪一路")));
            }
        }

        static string MediumHint(string conf)
            => conf == "medium" ? "；温度变化太小导致相关偏低，请核对散热页数值是否合理" : "";

        if (cpu is not null) layout["cpu_temp"] = (cpu.Offset, 4);
        if (gpu is not null) layout["gpu_temp"] = (gpu.Offset, 4);

        // ---------------- 风扇转速：靠值域认，按偏移升序排 CPU / GPU ----------------
        var rpmPicked = rpms.OrderBy(r => r.Offset).ThenBy(r => r.Cmd, StringComparer.Ordinal).Take(2).ToList();
        if (rpmPicked.Count > 0)
        {
            string[] names = rpmPicked.Count >= 2 ? ["fan1_rpm", "fan2_rpm"] : ["fan1_rpm"];
            for (var i = 0; i < names.Length; i++)
            {
                layout[names[i]] = (rpmPicked[i].Offset, 4);
                guesses.Add(new(names[i], rpmPicked[i].Cmd, rpmPicked[i].Offset,
                    rpmPicked.Count >= 2 ? "low" : "medium",
                    $"值域 {rpmPicked[i].Min:0}~{rpmPicked[i].Max:0}（幅度 {rpmPicked[i].Delta:0}）"
                    + (double.IsNaN(rpmPicked[i].BestCorr) ? "" : $"，与 {rpmPicked[i].BestRef} 相关 {rpmPicked[i].BestCorr:0.00}")));
            }
        }

        // ---------------- 键盘背光（只有采样期间真变过才敢认） ----------------
        if (lights.FirstOrDefault() is { } light)
        {
            layout["backlight_level"] = (light.Offset, 4);
            guesses.Add(new("backlight_level", light.Cmd, light.Offset, "medium",
                $"取值只在 {light.Min:0}~{light.Max:0} 之间（背光档位就是 0/1/2）"));
        }

        // ---------------- 结论 / 缺口 ----------------
        //
        // 门槛是「至少有一路温度被高置信认出来」，**不是**两路都齐。
        // 理由：很多机器在一键采集那十几秒里只有 CPU 在忙（用户没开游戏），显卡温度压根不动，
        // 硬要求两路齐等于"这功能在正常使用下永远不生效"。而 layout 里只放认出来的项，
        // 缺的那路读出来就是不存在 —— 界面本来就不会画它（NVML 那边还有原生显卡温度）。
        var verified = cpu is not null || gpu is not null || lights.Count > 0;
        var tempDelta = temps.Select(t => t.Delta).DefaultIfEmpty(0).Max();

        // 有了"同刻绝对对齐"认出来的温度，就**不许**再说"没有任何偏移像温度" ——
        // 这两句同时出现正是 2026-11-14 那份报告的样子：上面列着 gpu_temp status@8 [high]，
        // 下面却劝用户"跑个负载再采一次"。自相矛盾的结论比没有结论更糟。
        var anyTemp = cpu is not null || gpu is not null;

        if (!anyTemp)
        {
            if (temps.Count == 0)
            {
                missing.Add("温度偏移：这段采样里没有任何偏移像温度");
                notes.Add("没有偏移落在 15~130 且幅度 ≥3 的区间 —— 可能这段命令读不到温度，"
                          + "也可能采样期间机器一直空载。跑个负载再采一次。");
            }
            else if (tempDelta < 3)
            {
                missing.Add("温度偏移：采样期间温度几乎没动");
                notes.Add("温度候选最多只变化了 " + tempDelta.ToString("0.0", CultureInfo.InvariantCulture)
                          + "°C —— 平稳状态下**无法**判断哪个偏移是温度。"
                          + "请在采集那十几秒里让 CPU/GPU 跑起来（编译、游戏、烤机都行）再采一次。");
            }
        }

        var tempCandidates = temps.Count > 0 || aligned.Count > 0;
        if (cpu is null && tempCandidates) missing.Add("CPU 温度：没能高置信确认");
        if (gpu is null && tempCandidates)
            missing.Add("显卡温度：没能高置信确认"
                        + (temps.Count == 0 || double.IsNaN(temps.Max(t => t.CorrGpu)) || temps.All(t => Abs(t.CorrGpu) < 0.7)
                            ? "（显卡温度这段没变化 —— 采集时显卡多半在待机，不一定是问题）" : ""));

        if (!verified && tempCandidates)
            notes.Add("一路温度都没高置信认出来 ⇒ 档案里 layout_verified 保持 false："
                      + "程序只回原始字节、不显示温度。这是故意的 —— 偏移猜错就是把别的寄存器当温度显示。");
        else if (verified && (cpu is null || gpu is null))
            notes.Add(OnlyOneNote(cpu is not null ? "CPU 温度" : gpu is not null ? "显卡温度" : "键盘背光"));

        if (rpmPicked.Count == 2)
            notes.Add($"两路风扇（{rpmPicked[0].Cmd}@{rpmPicked[0].Offset} / {rpmPicked[1].Cmd}@{rpmPicked[1].Offset}）"
                      + "谁对 CPU 是按偏移顺序假设的；两路转速一直相同就分不出来 —— 看散热页两个数字跟原厂对不对得上。");

        return new LayoutAdvice(stats, guesses, layout, notes, verified, missing);
    }

    /// <summary>
    /// 把「写进去再读回」证实过的背光档位偏移并进标定结论。
    ///
    /// 为什么这条比 <see cref="Advise"/> 里那一路（"值域 0~2 且真的变过"）硬：
    /// 那是**猜**，这是**证** —— 我们写 2 档、读回 2，写 0 档、读回 0，同一个偏移两次对上。
    /// 所以它的置信度是 high，而且不需要用户"刚好在那十几秒里调过键盘灯"
    /// （那一路在绝大多数采集里都是空手而归，背光页于是永远是灰的）。
    ///
    /// 只改 layout / guesses / notes，**不动** missing 与 stats：前者说的是温度那几路的缺口，
    /// 后者是相关性统计表，混进一条"由写入证实的"记录会让复核的人以为它也是算出来的。
    /// </summary>
    public static LayoutAdvice WithBacklightProbe(LayoutAdvice advice, int offset, int len, string why)
    {
        var layout = new Dictionary<string, (int, int)>(advice.Layout, StringComparer.Ordinal)
        {
            ["backlight_level"] = (offset, len),
        };

        // 原来那条"靠值域猜"的（如果有）换成这一条：同一个字段只该有一个结论
        var guesses = advice.Guesses.Where(g => g.Field != "backlight_level").ToList();
        guesses.Add(new LayoutGuess("backlight_level", "status", offset, "high", why));

        var notes = WithoutStaleVerifiedNote(advice.Notes);
        notes.Add($"键盘背光档位在 status@{offset}：这是**写进去再读回来**证实的（不是靠值域猜的）。");

        // layout 里有背光就够格置 verified —— 与 Advise 里"lights 也算一路证据"同一个门槛
        return advice with
        {
            Layout = layout,
            Guesses = guesses,
            Notes = notes,
            LayoutVerified = true,
        };
    }

    /// <summary>
    /// 把「引导里把风扇拉起来那一段」认出来的转速并进标定结论。
    ///
    /// 为什么单独一步：转速不看相关看形状（见 <see cref="FanLayoutAdvisor"/>），
    /// 而 20 秒的稳态采样里风扇通常一动不动 —— 不并这一步，适配完的机器上
    /// **看不到转速、风扇曲线也没有反馈**，那是最容易被当成"适配没做完"的地方。
    ///
    /// <see cref="LayoutVerified"/> 不在这里抬：它管的是"温度/背光能不能显示"，
    /// 认出转速不等于认出温度，混着改会让一台没认出温度的机器显示出读错的温度。
    /// </summary>
    public static LayoutAdvice WithFans(LayoutAdvice advice, FanAdvice fans)
    {
        if (fans.Layout.Count == 0) return advice;

        var layout = new Dictionary<string, (int, int)>(advice.Layout, StringComparer.Ordinal);
        foreach (var (k, v) in fans.Layout) layout[k] = v;

        // 同一字段只留一个结论：相关性那条"顺带猜的"转速换成形状认出来的这条
        var guesses = advice.Guesses.Where(g => g.Field is not ("fan1_rpm" or "fan2_rpm" or "fan3_rpm")).ToList();
        guesses.AddRange(fans.Guesses);

        // 布局换了，那句「两路风扇（旧@12 / 旧@16）谁对 CPU 是按偏移顺序假设的」就指向了
        // 已不在 layout 里的偏移 —— 一起撤掉（新的结论由 fans.Notes 自己带）。
        var notes = advice.Notes.Where(n => !n.StartsWith("两路风扇（", StringComparison.Ordinal))
                         .Concat(fans.Notes).ToList();
        var missing = advice.Missing
            .Where(m => !m.StartsWith("风扇转速", StringComparison.Ordinal))
            .ToList();

        return advice with { Layout = layout, Guesses = guesses, Notes = notes, Missing = missing };
    }

    /// <summary>
    /// 用「加 CPU 负载那一段」的推断**补缺**温度字段（只补没认出来的，不覆盖已有结论）。
    ///
    /// 为什么要这一步：稳态采样经常在"机器闲着"的 20 秒里跑完 —— 温度几乎不动，
    /// 相关性算不出来（真机上表现为"温度偏移没认出，跑点负载重采更好"）。而加负载那一段
    /// 把 CPU 功耗拉高了几十瓦，哪怕风扇压住了温升，功耗与温度的动态范围也比空载清楚得多。
    /// 同一份采集里已经有这段数据，不用它等于让用户为了"有负载"再采一次。
    ///
    /// 只补缺的理由：已有结论是**更早、更完整**的一段采样算出来的，两者冲突时不该被覆盖；
    /// 补进来的字段会在 notes 里写明出处。
    /// </summary>
    public static LayoutAdvice WithMissingTemps(LayoutAdvice advice, LayoutAdvice extra, int loadFrames)
    {
        var layout = new Dictionary<string, (int, int)>(advice.Layout, StringComparer.Ordinal);
        var guesses = advice.Guesses.ToList();
        var notes = advice.Notes.ToList();
        var missing = advice.Missing.ToList();
        var added = new List<string>();

        foreach (var field in (string[])["cpu_temp", "gpu_temp"])
        {
            if (layout.ContainsKey(field)) continue;
            if (!extra.Layout.TryGetValue(field, out var slot)) continue;

            layout[field] = slot;
            added.Add(field);
            var g = extra.Guesses.FirstOrDefault(x => x.Field == field);
            if (g is not null)
            {
                // 同一个偏移原来可能被记成**泛字段** `temp`（"与 CPU、显卡都相关，分不清是哪一路"）。
                // 现在负载那段把它认成了具体某一路 ⇒ 泛结论已被取代，必须撤回 —— 否则同一份报告里
                // status@8 会同时印着"分不清是哪一路"和"[high] 显卡温度"（2026-11-23 23:03 真机）。
                // 只撤**同一个 (命令, 偏移)** 的那条：别的偏移上的"也像温度"是独立证据，留着。
                guesses.RemoveAll(x => x.Field == "temp" && x.Cmd == g.Cmd && x.Offset == g.Offset);
                guesses.Add(g with { Why = g.Why + "；由「加 CPU 负载」那一段补充" });
            }

            var label = field == "cpu_temp" ? "CPU 温度" : "显卡温度";
            missing.RemoveAll(m => m.StartsWith(label, StringComparison.Ordinal));
        }

        if (added.Count == 0) return advice;

        // 补上之后，稳态那段"没有任何偏移像温度 / 跑个负载再采一次"就不成立了 ——
        // 不清掉的话同一份报告里会同时写着「温度偏移：这段采样里没有任何偏移像温度」
        // 和「用加 CPU 负载那一段补上了 gpu_temp」，用户只能理解为程序坏了（2026-11-14 实测）。
        missing.RemoveAll(m => m.StartsWith("温度偏移", StringComparison.Ordinal));
        notes.RemoveAll(n => n.StartsWith("没有偏移落在 15~130", StringComparison.Ordinal)
                          || n.StartsWith("温度候选最多只变化了", StringComparison.Ordinal));
        // 补进来的温度会让 Verified 变 true ⇒「一路温度都没高置信认出来 ⇒ layout_verified
        // 保持 false」这句**立刻过期**（MergeTail 早就在撤，这里漏了：2026-11-23 23:03 那份
        // 报告一边写"保持 false、不显示温度"，一边在同一个包里把 layout_verified 记成 true）。
        notes = WithoutStaleVerifiedNote(notes);

        // 同理：「只认出了显卡温度…」这句是稳态采样那一刻的判断，现在那一路已经由负载段补上了。
        // 按**补完之后**的事实重写，别让结论停在旧状态。
        notes.RemoveAll(n => n.StartsWith("只认出了", StringComparison.Ordinal));
        var hasCpu = layout.ContainsKey("cpu_temp");
        var hasGpu = layout.ContainsKey("gpu_temp");
        if (hasCpu ^ hasGpu) notes.Add(OnlyOneNote(hasCpu ? "CPU 温度" : "显卡温度"));

        notes.Add("稳态采样没认出 " + string.Join(" / ", added)
                  + "，用「加 CPU 负载」那一段补上了（" + loadFrames + " 帧、负载期间 CPU 功耗明显抬高）："
                  + "那一段温度动起来了，EC 读数与外部参照逐帧对得上；稳态那 20 秒反而太恒、没有可用的变化。");

        var verified = advice.LayoutVerified
                       || layout.ContainsKey("cpu_temp") || layout.ContainsKey("gpu_temp");

        return advice with
        {
            Layout = layout,
            Guesses = guesses,
            Notes = notes,
            Missing = missing,
            LayoutVerified = verified,
        };
    }

    /// <summary>
    /// 用"加 CPU 负载看哪路转速起来"的结论**重排**风扇：`fan1_rpm` 恒为 CPU 风扇。
    ///
    /// 为什么必须重排而不是只在 notes 里记一句：界面/曲线读的是字段名，
    /// `fan1_rpm` 一直被当成 CPU —— 偏移写反了，转速会显示在对调的位置、按 CPU 温度调的
    /// 曲线其实在指挥另一路。重排是唯一能让结论真正生效的动作。
    ///
    /// 判不出归属时**不动布局**，只把原因并进 notes / missing —— 这条纪律和别处一致。
    /// </summary>
    public static LayoutAdvice WithFanRoles(LayoutAdvice advice, FanRoleAdvice roles)
    {
        var notes = advice.Notes.ToList();
        var missing = advice.Missing
            .Where(m => !m.StartsWith("风扇归属", StringComparison.Ordinal))
            .Concat(roles.Missing)
            .ToList();

        if (!roles.Decided)
            return advice with { Notes = notes.Concat(roles.Notes).ToList(), Missing = missing };

        // 判出了归属 ⇒ 布局要重排，那句「两路风扇（旧@12 / 旧@16）谁对 CPU 是按偏移顺序
        // 假设的」已经过期（它绑的两个偏移马上会被换掉），撤掉；新结论在 roles.Notes 里。
        notes.RemoveAll(n => n.StartsWith("两路风扇（", StringComparison.Ordinal));
        notes.AddRange(roles.Notes);

        var layout = new Dictionary<string, (int, int)>(advice.Layout, StringComparer.Ordinal);
        layout.Remove("fan1_rpm");
        layout.Remove("fan2_rpm");
        layout.Remove("fan3_rpm");
        foreach (var (k, v) in roles.Layout) layout[k] = v;

        var guesses = advice.Guesses.Where(g => g.Field is not ("fan1_rpm" or "fan2_rpm" or "fan3_rpm")).ToList();
        guesses.AddRange(roles.Guesses);

        return advice with { Layout = layout, Guesses = guesses, Notes = notes, Missing = missing };
    }

    /// <summary>
    /// 「同刻绝对对齐」：某个偏移与**外部**温度参照逐帧几乎相等 ⇒ 它就是那一路温度。
    ///
    /// 为什么需要这一条：<see cref="Advise"/> 原先只靠相关性，而相关性要求序列有方差。
    /// 机器闲着的时候温度一动不动（本机 20 秒里 58 → 58），方差为 0 ⇒ 相关系数 NaN ⇒
    /// 一路候选都没有 ⇒ 报告说"没有任何偏移像温度"。可绝对值明明白白对得上：
    /// EC 的 58 就是 HWiNFO CPU Package 的 58，十六帧一帧不差。
    ///
    /// 三条守则（都是为了"宁可不显示，也不显示错的"）：
    ///   · 值域必须像温度：参照在动 ⇒ 15~130 就够；参照压根不动 ⇒ 收窄到 30~100，
    ///     免得把 0/1/2 档位、5% 占空比这类恒定的小数字认成温度；
    ///   · 两路参照要分得开：与另一路的差也得比这一路大 4°C 以上，否则不填 ——
    ///     CPU 50 / 显卡 48 这种机器上，光看绝对对齐分不出谁是谁；
    ///   · **大部分帧**对得上就作数，不要求帧帧都对：EC 的温度是平滑值，HWiNFO 的是瞬时值，
    ///     负载一上来 HWiNFO 先跳、EC 后跟，中间会差十几度（2026-11-15 实测 16 帧里 4 帧差 11~12）。
    ///     要求"九成帧都对"就会把真温度判掉；要求"六成帧差 ≤4°C 且这些帧中位差 ≤3°C"
    ///     才容得下这种错位，又不至于让"偶尔撞一次"的字段混进来。3 而不是 2 是因为
    ///     EC 与外部读数常有 2~4°C 的固定偏差（2026-11-17 真机：恒定 68 vs 64~67）。
    /// </summary>
    private static List<LayoutGuess> AlignedTemps(IReadOnlyList<LayoutSample> samples,
                                                  IReadOnlyList<(int Offset, int Len)> offsets)
    {
        const double closeLimit = 4.0;  // 单帧算"对得上"的容差（°C）
        const double closeRatio = 0.6;  // 至少这么大比例的帧要对得上
        const double apart = 4.0;       // 与另一路参照至少差这么多，才分得清是哪一路
        const double refMoved = 3.0;    // 参照这段变化 ≥3°C 才算"在动"

        var result = new List<LayoutGuess>();
        var best = new Dictionary<string, (LayoutGuess Guess, double Score)>(StringComparer.Ordinal);

        // 两路参照必须都在：绝对对齐靠的是"它更像 CPU 还是更像显卡"这条互斥判断，
        // 只有一路参照时问不出这个问题 —— 那种场合一律不判（宁可少一路，也别标错）。
        var cpuRefCount = samples.Count(s => !double.IsNaN(s.CpuTempC));
        var gpuRefCount = samples.Count(s => !double.IsNaN(s.GpuTempC));
        if (cpuRefCount < MinSamples || gpuRefCount < MinSamples) return result;

        var cmds = LayoutFrames.Commands(samples);

        foreach (var cmd in cmds)
        {
            for (var idx = 0; idx < offsets.Count; idx++)
            {
                // 同 AdviseCore：帧数组下标永远按 @4..@28 排，不能拿本遍的槽序号当它。
                var (slotOffset, _) = offsets[idx];
                var fi = slotOffset / 4 - 1;

                var ec = new List<double>();
                var cpuRef = new List<double>();
                var gpuRef = new List<double>();

                foreach (var s in samples)
                {
                    if (!s.Frames.TryGetValue(cmd, out var vals) || vals.Length <= fi) continue;
                    ec.Add(vals[fi]);
                    cpuRef.Add(s.CpuTempC);
                    gpuRef.Add(s.GpuTempC);
                }
                if (ec.Count < MinSamples) continue;

                // 判据用"**大部分帧**对得上"而不是"九成帧对得上"：
                // EC 的 CPU 温度是平滑值、HWiNFO 的 CPU Package 是瞬时值 —— 负载一上来，
                // HWiNFO 先跳到 77，EC 还停在 65，误差 12°C；等 EC 追上来两边又完全相等。
                // 2026-11-15 实测：16 帧里 11 帧差 0、1 帧差 2、4 帧差 11~12（中位差 0.0），
                // 卡 90 分位会把这一整路判掉。取 60% 的帧"对得上"（单帧容差 4°C）、
                // 这些帧自己的中位差 ≤3°C，既容得下跳变，也不给"偶尔撞一次"的字段机会。
                //
                // 中位数从 2 放到 3 的理由（2026-11-17 真机）：这台 EC 的 CPU 温度恒定 68，
                // HWiNFO 报 64~67（还偶尔跳到 78/80），逐帧差 11 帧 ≤4°C、这些帧中位差 **3.0**。
                // 卡 2 会把整路判掉，于是一份完整采集写不出 cpu_temp —— 而它明明就是 CPU 温度。
                // EC 是平滑值，和外部读数有 2~4°C 的固定偏差是常态，不能要求它帧帧相等。
                var cpuMoved = Moved(cpuRef, refMoved);
                var gpuMoved = Moved(gpuRef, refMoved);

                var (cpuMed, cpuClose, cpuCloseMed, cpuN) = Diff(ec, cpuRef, closeLimit);
                var (gpuMed, gpuClose, gpuCloseMed, gpuN) = Diff(ec, gpuRef, closeLimit);

                var cpuOk = cpuN >= MinSamples
                            && InRange(ec, cpuMoved ? 15 : 30, cpuMoved ? 130 : 100)
                            && cpuClose >= Math.Max(MinSamples, (int)Math.Ceiling(cpuN * closeRatio))
                            && cpuCloseMed <= 3.0;
                var gpuOk = gpuN >= MinSamples
                            && InRange(ec, gpuMoved ? 15 : 30, gpuMoved ? 130 : 100)
                            && gpuClose >= Math.Max(MinSamples, (int)Math.Ceiling(gpuN * closeRatio))
                            && gpuCloseMed <= 3.0;

                // 分得清才填：另一路的差至少要比这一路大 apart
                var cpuOnly = cpuOk && (!gpuOk || cpuMed + apart <= gpuMed);
                var gpuOnly = gpuOk && (!cpuOk || gpuMed + apart <= cpuMed);

                if (cpuOnly)
                    Offer("cpu_temp", "CPU 温度", cpuMed, cpuClose, cpuCloseMed, cpuN);
                else if (gpuOnly)
                    Offer("gpu_temp", "显卡温度", gpuMed, gpuClose, gpuCloseMed, gpuN);

                void Offer(string field, string label, double allMed, int close, double closeMed, int n)
                {
                    // EC 这一路在整段采样里一次没变（值域 X~X）时，只能证明"数值对上"，
                    // 证明不了它会跟着温度走 —— 这种情况降成 medium 并在 why 里说清。
                    // 2026-11-22 真机：cpu_temp@4 的 16 帧全是 60、HWiNFO 也在 59~60，
                    // 报告标 high，作者会以为这一路做过"变化跟踪"，其实只是静态值匹配。
                    var moved = ec.Max() - ec.Min() >= 1.0;
                    var why = $"{label}逐帧对得上：{n} 帧里 {close} 帧差 ≤{closeLimit:0}°C、这些帧中位差 {closeMed:0.0}°C"
                              + $"（整体中位差 {allMed:0.0}°C，值域 {ec.Min():0}~{ec.Max():0}；"
                              + "这条不看相关性 —— 温度几乎不动的采样里相关系数不可靠）"
                              + (moved ? "" : "；但这段采样里 EC 这一路一次没变 —— 只证明数值对得上，没证明它会跟着温度走");
                    var guess = new LayoutGuess(field, cmd, offsets[idx].Offset, moved ? "high" : "medium", why);
                    if (!best.TryGetValue(field, out var cur) || closeMed < cur.Score)
                        best[field] = (guess, closeMed);
                }
            }
        }

        foreach (var field in (string[])["cpu_temp", "gpu_temp"])
            if (best.TryGetValue(field, out var b)) result.Add(b.Guess);

        return result;

        static bool InRange(List<double> v, double lo, double hi)
            => v.All(x => x >= lo && x <= hi);

        static bool Moved(List<double> v, double minDelta)
        {
            var ok = v.Where(x => !double.IsNaN(x)).ToList();
            return ok.Count >= 2 && ok.Max() - ok.Min() >= minDelta;
        }

        // 逐帧 |ec - ref|：整体中位数（分路用）、"对得上"的帧数（差 ≤ limit）、这些帧自己的中位数。
        // 参照是 NaN 的帧直接跳过 —— 不能算成"差 0"，否则一条读不到参照的采样会自己变成满分。
        static (double AllMed, int Close, double CloseMed, int N) Diff(List<double> ec, List<double> refs, double limit)
        {
            var d = new List<double>();
            for (var i = 0; i < ec.Count && i < refs.Count; i++)
                if (!double.IsNaN(refs[i])) d.Add(Math.Abs(ec[i] - refs[i]));
            if (d.Count == 0) return (double.NaN, 0, double.NaN, 0);

            var all = d.OrderBy(x => x).ToList();
            var close = all.Where(x => x <= limit).ToList();
            return (Median(all), close.Count, close.Count == 0 ? double.NaN : Median(close), all.Count);
        }

        static double Median(List<double> sorted)
            => sorted.Count == 0
                ? double.NaN
                : sorted.Count % 2 == 1
                    ? sorted[sorted.Count / 2]
                    : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>粗分类：背光 / 温度 / 转速 各进各的桶，其它只在统计表里留档。</summary>
    private static void Classify(OffsetStat s, List<OffsetStat> temps, List<OffsetStat> rpms, List<OffsetStat> lights)
    {
        if (s.Distinct >= 2 && s.Max <= 3) { lights.Add(s); return; }                 // 0/1/2 档位

        // ℃：值域 15~130，且"真的在动"——要么幅度 ≥5℃，要么至少 3 个不同取值。
        // 只有 2 个取值的字段（比如 gpu_info 那帧里的 58/59）算出来的相关系数会是 ±1 的假象，
        // 2026-10-31 本机实测就是这样把 GPU 信息帧认成了 CPU 温度。
        if (s.Min >= 15 && s.Max <= 130
            && (s.Delta >= 5 || s.Distinct >= 3)) { temps.Add(s); return; }

        // rpm：风扇有惯性，十几秒里只挪几十转是常态 —— 阈值放 20 是"真在转"而不是"读数抖动"
        if (s.Max is >= 200 and <= 9000 && s.Delta >= 20 && s.Distinct >= 3) rpms.Add(s);
    }

    private static string Why(OffsetStat s)
        => $"值域 {s.Min:0}~{s.Max:0}（幅度 {s.Delta:0}），与 {s.BestRef} 相关 {s.BestCorr:0.00}";

    /// <summary>
    /// 「只认出了 X，另一路没认出来」这句要说准。
    ///
    /// 推断层只看得到**本次采样**，看不到档案里是否已经有这一项 —— 只读采集不会覆盖已有
    /// 档案（本机 21:54 那份草案里就带着 cpu_temp@4）。原来断言"缺的那路读数不会出现"，
    /// 结果 2026-11-23 23:03 那份报告一边这么说，一边在同一个包里把 cpu_temp 写进
    /// profile_draft：用户读到的是"你说不显示，可散热页有数字"。
    /// </summary>
    private static string OnlyOneNote(string known)
        => "只认出了" + known + "，另一路这次没认出来 —— 不是坏了：档案里本来就有这一项的，读数照旧。";

    /// <summary>
    /// 撤掉「一路温度都没高置信认出来 ⇒ layout_verified 保持 false」那句。
    ///
    /// 它描述的是**合并前**那一刻。任何一个合并步骤（补数据区尾部 / 背光写探针 / 加负载补
    /// 温度）只要让 Verified 变 true，这句就立刻过期 —— 2026-11-23 23:03 的报告正是这样：
    /// 上面写"保持 false、程序不显示温度"，同一个包里 layout_verified = true、两路温度都在 layout。
    /// `MergeTail` 早就这么撤了，另外两个合并函数漏了。
    /// </summary>
    private static List<string> WithoutStaleVerifiedNote(IEnumerable<string> notes)
        => notes.Where(n => !n.StartsWith("一路温度都没高置信认出来", StringComparison.Ordinal)).ToList();

    private static double Abs(double v) => double.IsNaN(v) ? -1 : Math.Abs(v);

    /// <summary>
    /// Pearson 相关。任一序列方差为 0（含参照根本没采到）时返回 NaN ——
    /// "恒定不变"和"算不出来"是两件事，不能都折成 0。
    /// </summary>
    private static double Corr(List<double> series, IReadOnlyList<LayoutSample> matched, Func<LayoutSample, double> pick)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (var i = 0; i < matched.Count && i < series.Count; i++)
        {
            var y = pick(matched[i]);
            if (double.IsNaN(y)) continue;
            xs.Add(series[i]);
            ys.Add(y);
        }
        if (xs.Count < MinSamples) return double.NaN;

        var mx = xs.Average();
        var my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < xs.Count; i++)
        {
            var dx = xs[i] - mx;
            var dy = ys[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 1e-9 || syy <= 1e-9) return double.NaN;
        return sxy / Math.Sqrt(sxx * syy);
    }
}
