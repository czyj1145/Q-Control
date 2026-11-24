namespace QControl.Core.Devices;

/// <summary>
/// 「加负载看风扇」的结论。<see cref="Decided"/> = 这一轮能不能说清"哪一路是 CPU 风扇"。
///
/// 为什么要有这一段：`fan1_rpm` / `fan2_rpm` 一直是**按偏移升序**假设的
/// （内置档案里 fan1=CPU / fan2=GPU，别的机型照抄这个假设）。猜错的症状是
/// 散热页两个转速对调、按 CPU 温度调的那条曲线其实在指挥 GPU 风扇 —— 肉眼很难归因。
///
/// 现在换一种证据：**给 CPU 加一段可控负载，看哪一路转速跟着起来**。
/// 这不是相关性，是因果 —— 我们制造了唯一变量（CPU 功耗），风扇的响应只有它一个来源。
/// 但有一条必须承认的边界：很多模具的固件是"按机内最高温同时驱动两路"，
/// 那种机器上两路会一起升，这个方法就分不出来 —— 分不出来就明说，不硬判。
/// </summary>
public sealed record FanRoleAdvice(
    IReadOnlyDictionary<string, (int Offset, int Len)> Layout,
    IReadOnlyList<LayoutGuess> Guesses,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Missing,
    bool Decided)
{
    public static FanRoleAdvice Empty { get; } = new(
        new Dictionary<string, (int, int)>(StringComparer.Ordinal), [], [], [], false);
}

/// <summary>
/// 用一段 CPU 负载把「哪一路风扇对 CPU」认出来 —— 纯逻辑（给定三段采样，不碰 EC）。
///
/// 输入是三段帧：负载前（基线）/ 负载中 / 负载后。判据只有三条：
///   · **负载真的发生了**：CPU 功耗或温度确实抬起来了（否则后面的响应无从谈起）；
///   · **有一路涨得明显更多**：转速相对基线抬升 ≥120 转，且第二路的涨幅不到它的 75%
///     （或差距 ≥300 转）；
///   · **响应发生在负载窗口里**：用负载后半段的均值（风扇有惯性，前几秒还在爬）。
///
/// 两路涨幅**接近**时**不判**：那种固件是按最高温同时驱动两路，谁先动、谁动得多都
/// 说明不了映射，硬判等于把猜测包装成证据。
///
/// 但"接近"的门槛不能太松（2026-11-15 真机 + 用户观察修正）：本机 CPU 负载下
/// 两路涨幅是 +1770 与 +1128（64%），旧门槛（第二路 ≥50% 就判"一起升"）把这条
/// **可用证据**丢掉了 —— 用户一眼就能看出 CPU 那路更快。现在只有涨幅 ≥75% 或差距
/// 不到 300 转才叫分不清；差得开时判涨幅最大的那路，但置信度封顶 medium
/// （固件毕竟同时驱动了两路）。
///
/// ⚠️ 只加 CPU 负载、不加 GPU：程序里没有可用的 GPU 计算/渲染通道（界面是软件渲染）。
///    所以结论只到"哪一路是 CPU 风扇"，另一路按"剩下的那路"推定 —— 报里写明这一点。
/// </summary>
public static class FanRoleAdvisor
{
    /// <summary>负载前至少几帧才够算基线。</summary>
    public const int MinBaseline = 3;

    /// <summary>负载中至少几帧（250ms 一帧 ⇒ 5 帧约 1.2 秒，正常给 30 帧以上）。</summary>
    public const int MinLoaded = 5;

    /// <summary>
    /// 转速相对基线的抬升门槛（转/分）。低于它只能说明"抖动"。
    ///
    /// 2026-11-14 真机两轮对比定下来的：机器凉的时候 +277，热过一轮之后只有 +203
    /// （风扇本来就在高转速，负载推不动多少）—— 卡 250 会把第二轮判成"没响应"，
    /// 而那 203 转对另一路的 +5 转仍然是压倒性的。120 是"读数抖动（±20~50）"之上、
    /// 真抬升之下的保守值；再低就分不清风扇和噪声了。
    /// </summary>
    private const double MinResponse = 120;

    /// <summary>第一路至少是第二路的几倍，才敢说"只有它在响应"。</summary>
    private const double Dominance = 2.0;

    /// <summary>
    /// 偏移 → 帧数组下标（DAT2..DAT8 = 4/8/12/16/20/24/28）。
    /// 尾部两个槽是给别的模具的兜底（转速放在数据区尾部）；判据（峰值四位数）挡住了它们的 0。
    /// </summary>
    private static readonly (int Offset, int Index)[] Slots =
        [(4, 0), (8, 1), (12, 2), (16, 3), (20, 4), (24, 5), (28, 6)];

    /// <summary>一路的统计：基线均值 / 负载尾段均值 / 响应量。</summary>
    private sealed record Response(string Cmd, int Offset, double Baseline, double Loaded, double Peak)
    {
        public double Gain => Loaded - Baseline;
    }

    /// <summary>
    /// 认出哪一路风扇对 CPU。
    /// </summary>
    /// <param name="baseline">负载前的帧（空闲）。</param>
    /// <param name="loaded">负载中的帧（CPU 满载）。</param>
    /// <param name="recovery">负载后的帧（可选，只用于判断"降回去了"这条旁证）。</param>
    /// <param name="known">已经认出来的风扇偏移（上一段采样 / 引导那一步的结论）。用来补"另一路"。</param>
    /// <param name="taken">已经认给温度/背光的偏移 —— 那些不再当转速。</param>
    public static FanRoleAdvice Advise(
        IReadOnlyList<LayoutSample> baseline,
        IReadOnlyList<LayoutSample> loaded,
        IReadOnlyList<LayoutSample>? recovery = null,
        IReadOnlyDictionary<string, (int Offset, int Len)>? known = null,
        IEnumerable<(string Cmd, int Offset)>? taken = null)
    {
        var notes = new List<string>();
        var missing = new List<string>();
        var layout = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var guesses = new List<LayoutGuess>();

        if (baseline.Count < MinBaseline || loaded.Count < MinLoaded)
        {
            missing.Add($"风扇归属：样本不足（负载前 {baseline.Count} 帧 / 负载中 {loaded.Count} 帧）");
            notes.Add("这段负载采样太短，没能判断哪一路风扇对 CPU —— 档案里风扇顺序仍按偏移升序。");
            return new FanRoleAdvice(layout, guesses, notes, missing, false);
        }

        // 1) 负载真的发生了吗
        var basePower = Mean(baseline, s => s.CpuPowerW);
        var loadPower = Mean(loaded, s => s.CpuPowerW);
        var baseTemp = Mean(baseline, s => s.CpuTempC);
        var loadTemp = Mean(loaded, s => s.CpuTempC);
        var powerGain = double.IsNaN(basePower) || double.IsNaN(loadPower) ? double.NaN : loadPower - basePower;
        var tempGain = double.IsNaN(baseTemp) || double.IsNaN(loadTemp) ? double.NaN : loadTemp - baseTemp;

        var loadedOk = (powerGain >= 8) || (tempGain >= 3);
        if (!loadedOk)
        {
            missing.Add("风扇归属：这段负载没把 CPU 功耗/温度拉起来（离电、限频或电源策略压着）");
            notes.Add("加负载那段时间里 CPU 功耗只变了 "
                      + (double.IsNaN(powerGain) ? "读不到" : $"{powerGain:0}W")
                      + "、温度只变了 " + (double.IsNaN(tempGain) ? "读不到" : $"{tempGain:0.0}°C")
                      + "：风扇没被驱动，这段不能用来判断归属（插上电源再采一次更稳）。");
            return new FanRoleAdvice(layout, guesses, notes, missing, false);
        }

        // 2) 每个偏移的响应量（用负载**后半段**：风扇有惯性，开头几秒还在爬）
        var used = new HashSet<(string, int)>(taken ?? []);
        var responses = new List<Response>();
        var tail = loaded.Skip(loaded.Count / 2).ToList();

        foreach (var cmd in LayoutFrames.Commands(baseline))
        {
            foreach (var (offset, idx) in Slots)
            {
                if (used.Contains((cmd, offset))) continue;

                var b = Values(baseline, cmd, idx);
                var l = Values(tail, cmd, idx);
                if (b.Count < MinBaseline || l.Count < 3) continue;

                var baseMean = b.Average();
                var loadMean = l.Average();
                var peak = loaded.Select(s => Value(s, cmd, idx))
                                 .Where(v => !double.IsNaN(v))
                                 .DefaultIfEmpty(double.NaN).Max();

                // 转速是四位数：峰值不到 600 的多半是档位/功耗，不参与
                if (double.IsNaN(peak) || peak < 600 || peak > 9000) continue;

                responses.Add(new Response(cmd, offset, baseMean, loadMean, peak));
            }
        }

        var ranked = responses.OrderByDescending(r => r.Gain).ToList();
        var top = ranked.FirstOrDefault();
        if (top is null || top.Gain < MinResponse)
        {
            missing.Add("风扇归属：加负载后没有一路转速明显抬升（风扇拐点更高，或本来就转得快）");
            // 这条要能指导下一步，不能只说"没判出来"：真机上连续采集时最常见的原因是
            // **上一轮刚把风扇拉高过**，基线本来就高 ⇒ 13 秒负载推不动多少。
            notes.Add("负载期间转速最多只变了 "
                      + (top is null ? "0" : $"{top.Gain:0}") + " 转"
                      + (top is null ? "" : $"（{top.Cmd}@{top.Offset}，基线 {top.Baseline:0} 转）")
                      + "：这段负载没把温度推到风扇的拐点上（很多模具 70°C 以上才明显加速），"
                      + "或者风扇本来就在高转速。**凉机（闲置几分钟后）再采一次最容易看到响应**；"
                      + "两次都这样，就说明这台不按温度调转速，归属只能按原厂界面核对。");
            return new FanRoleAdvice(layout, guesses, notes, missing, false);
        }

        var second = ranked.Skip(1).FirstOrDefault();
        var ratio = second is null || top.Gain <= 0 ? 0 : second.Gain / top.Gain;

        // 「分不清」的门槛（2026-11-15 两次真机 + 用户观察修正）：
        //   · 第二路涨幅 ≥ 第一路的 75% ⇒ 涨幅接近，同一个温度在驱动两路，看不出谁是谁；
        //   · 或者两路涨幅都很小（第一路不到 300 转）而另一路也在 >120 转地涨 ——
        //     小涨幅下的比例不可靠，宁可说"分不清"。
        //
        // 以前只要第二路 ≥50% 就判"一起升"。本机 CPU 负载下 **+1770 vs +1128**（64%）
        // 也被判成"分不出"，可人眼看着 CPU 那路明显更快 —— 门槛太松等于把可用证据丢掉。
        // 也不能只看差值：2026-11-15 第二次采集是 **+153 vs −78**（差 231 转），
        // 一路升一路降，差值不到 300 就被判"接近"同样是错的 —— 方向相反本来就说明得了问题。
        var nearSame = second is not null
                       && (ratio >= 0.75 || (top.Gain < 300 && second.Gain >= MinResponse));

        if (nearSame)
        {
            // 两路一起升：固件按最高温同时驱动。此时"谁先动"也说明不了映射，不判。
            missing.Add("风扇归属：两路转速一起升（固件按整机温度同时驱动），负载法分不出哪路对 CPU");
            notes.Add($"加负载后两路都在升（{top.Cmd}@{top.Offset} +{top.Gain:0} 转 / "
                      + $"{second!.Cmd}@{second.Offset} +{second.Gain:0} 转）："
                      + "两路被同一个温度驱动、涨幅又接近，谁对 CPU 从转速上看不出来 —— "
                      + "顺序仍按偏移升序，需要作者按原厂界面复核。");
            return new FanRoleAdvice(layout, guesses, notes, missing, false);
        }

        // 3) 变化最大的那一路必须落在"已知的转速偏移"里。
        //    这一条是防假阳性的：有些机器把温度按 ×10 / ×100 存在 status 帧里，
        //    加负载时那个字段涨得比转速还猛（45°C → 82°C 在 ×100 下就是 +3700）。
        //    已知转速是从值域+形状里认出来的，对不上就说明"涨得最多的那个不是风扇"。
        var knownOffsets = (known ?? new Dictionary<string, (int, int)>())
            .Where(kv => kv.Key is "fan1_rpm" or "fan2_rpm" or "fan3_rpm")
            .Select(kv => kv.Value.Offset)
            .Distinct()
            .ToList();

        if (knownOffsets.Count > 0 && !knownOffsets.Contains(top.Offset))
        {
            missing.Add("风扇归属：负载里变化最大的偏移不在已认出的转速里（可能是温度×10/×100 一类字段）");
            notes.Add($"加负载后变化最大的是 {top.Cmd}@{top.Offset}（+{top.Gain:0}），"
                      + $"但它不在已知转速 [{string.Join("/", knownOffsets)}] 里 —— 不据此改风扇顺序。");
            return new FanRoleAdvice(layout, guesses, notes, missing, false);
        }

        // 4) 涨幅明显更大 ⇒ 它就是 CPU 风扇
        layout["fan1_rpm"] = (top.Offset, 4);
        var conf = knownOffsets.Count == 0 ? "medium" : top.Gain >= 800 ? "high" : "medium";

        // 两路都在升、只是差得开：结论仍成立（这段负载只有 CPU 一个热源），但固件同时
        // 驱动了另一路，所以只给 medium —— 散热页 / 原厂界面核一眼就能确认。
        if (ratio >= 0.5) conf = "medium";

        guesses.Add(new LayoutGuess("fan1_rpm", top.Cmd, top.Offset, conf,
            $"加了 {loaded.Count} 帧的 CPU 负载（功耗 {Signed(powerGain, "0", "W")} / "
            + $"温度 {Signed(tempGain, "0.0", "°C")}），"
            + $"这一路从 {top.Baseline:0} 升到 {top.Loaded:0} 转（+{top.Gain:0}）"
            + SecondFanText(second)));

        if (ratio >= 0.5 && second is not null)
            notes.Add($"加负载后两路都在升，但这一路（{top.Cmd}@{top.Offset}）涨得明显更多"
                      + $"（+{top.Gain:0} vs +{second.Gain:0} 转，{top.Gain / Math.Max(1, second.Gain):0.0} 倍）—— "
                      + "这段负载只有 CPU 一个热源，所以按「涨得最多的是 CPU 风扇」判；"
                      + "固件同时驱动了两路，置信度只给 medium，请按原厂界面核一眼。");

        // 剩下的那几路：一路 ⇒ GPU 风扇；两路 ⇒ GPU / 系统风扇，但**分不出谁是谁**
        // （要分辨得给 GPU 单独加负载，程序里没有可用的 GPU 计算通道）。
        var others = (known ?? new Dictionary<string, (int, int)>())
            .Where(kv => kv.Key is "fan1_rpm" or "fan2_rpm" or "fan3_rpm")
            .Select(kv => kv.Value.Offset)
            .Where(o => o != top.Offset)
            .Distinct()
            .OrderBy(o => o)
            .ToList();

        if (knownOffsets.Count == 0)
            notes.Add($"这一轮之前没有认出的转速偏移，CPU 风扇是直接按「加负载后升得最多」认的"
                      + $"（{top.Cmd}@{top.Offset}）：如果散热页显示的转速明显不对，先怀疑这一条。");

        if (others.Count == 1)
        {
            layout["fan2_rpm"] = (others[0], 4);
            notes.Add($"负载法认定：CPU 风扇在 {top.Cmd}@{top.Offset}（加 CPU 负载后 +{top.Gain:0} 转），"
                      + $"另一路 @{others[0]} 按「剩下的那路」记作 GPU 风扇（本轮只加了 CPU 负载）。");
        }
        else if (others.Count >= 2)
        {
            var names = new[] { "fan2_rpm", "fan3_rpm" };
            for (var i = 0; i < others.Count && i < names.Length; i++) layout[names[i]] = (others[i], 4);

            notes.Add($"负载法认定：CPU 风扇在 {top.Cmd}@{top.Offset}（加 CPU 负载后 +{top.Gain:0} 转）；"
                      + $"另有 {others.Count} 路（{string.Join(" / ", others.Select(o => "@" + o))}）没被 CPU 负载带动，"
                      + "按偏移升序记作 fan2 / fan3 —— **哪一路是 GPU、哪一路是系统风扇分不出来**："
                      + "要分辨得单独给 GPU 加负载，而本程序没有可用的 GPU 计算通道。");
        }
        else
        {
            notes.Add($"负载法认定 CPU 风扇在 {top.Cmd}@{top.Offset}（加 CPU 负载后 +{top.Gain:0} 转）；"
                      + "这一轮只认出一路转速，其余没有证据就没写。");
        }

        return new FanRoleAdvice(layout, guesses, notes, missing, true);
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>
    /// 「另一路」的变化要跟着**方向**说。
    ///
    /// <see cref="Response.Gain"/> 是 `负载尾段均值 − 基线均值`，**有符号**。这段文案原来
    /// 是写死的"另一路只升 {gain} 转"，另一路反而降的时候就会印成
    /// "另一路只升 -353 转"（2026-11-23 21:54 真机报告 fan1_rpm 那行：这一路 +192、
    /// 另一路 −353）。同一句里"只升"和负号直接打架，作者读到会以为程序算错了方向；
    /// 而这段 Why 同时进 txt 报告、采集包 json、档案草案的 guesses 和界面回执，一处错四处错。
    ///
    /// 抖动按 ±50 转算（同 <see cref="MinResponse"/> 注释里"读数抖动 ±20~50"）：
    /// 这个量级说"基本没动"比报一个假涨幅诚实 —— 与"宁慢也不显示错的"同一个口径。
    /// </summary>
    private static string SecondFanText(Response? second)
    {
        if (second is null) return "，另一路没动";

        var gain = second.Gain;
        if (gain <= -50) return $"，另一路反而降了 {-gain:0} 转";
        if (gain < 50) return "，另一路基本没动（在读数抖动范围内）";
        return $"，另一路只升 {gain:0} 转";
    }

    /// <summary>
    /// 带符号的数值：负号由数值自己带。原来写成 `$"+{v:0}W"`，v 为负时会印出 "+-5W"
    /// （负载期间温度比基线低一点就会发生）—— 与 <see cref="SecondFanText"/> 同一个毛病。
    /// </summary>
    private static string Signed(double v, string format, string unit)
        => (v < 0 ? "-" : "+")
           + Math.Abs(v).ToString(format, System.Globalization.CultureInfo.InvariantCulture)
           + unit;

    private static double Value(LayoutSample s, string cmd, int idx)
        => s.Frames.TryGetValue(cmd, out var vals) && idx < vals.Length ? vals[idx] : double.NaN;

    private static List<double> Values(IReadOnlyList<LayoutSample> samples, string cmd, int idx)
    {
        var list = new List<double>(samples.Count);
        foreach (var s in samples)
        {
            var v = Value(s, cmd, idx);
            if (!double.IsNaN(v)) list.Add(v);
        }
        return list;
    }

    /// <summary>参照一律 NaN 表示"没读到"，均值时跳过；全是 NaN 就返回 NaN（不折成 0）。</summary>
    private static double Mean(IReadOnlyList<LayoutSample> samples, Func<LayoutSample, double> pick)
    {
        var vals = samples.Select(pick).Where(v => !double.IsNaN(v)).ToList();
        return vals.Count == 0 ? double.NaN : vals.Average();
    }
}
