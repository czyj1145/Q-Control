namespace QControl.Core.Devices;

/// <summary>
/// 「一键采集」里**写入验证**那一半的纯逻辑（不碰 EC、不碰磁盘）。
///
/// 为什么采集要写：只读采样只能证明"读得出来"。一份要让我们把这台机器适配到能用的
/// 采集包，必须还回答另一件事 —— **写进去的东西真的落到 EC 了吗**。
/// 而"写了没反应"有四种完全不同的根因（没管理员 / 总闸没开 / 命令号不对 / 语义不对），
/// 用户一个都分不清；能分开它们的只有**写后读回**。
///
/// 这里只做两件**可以离线复算**的判断：
/// <list type="number">
///   <item>背光档位到底落在 status 帧的哪个 u32 偏移上 —— 写完再读回，比"值域 0~2 猜"硬得多；</item>
///   <item>该写哪几个 APMD 值 —— 只写配置里已经在用的那几个，绝不发明新值。</item>
/// </list>
/// 真正的下发在 <c>App/MachineProbe.cs</c>（那边要过守卫、要恢复原值）。
/// </summary>
public static class EcWriteProbe
{
    /// <summary>status 帧里的标准槽：5 个 u32（DAT2..DAT6 = 偏移 4/8/12/16/20）。</summary>
    public static readonly IReadOnlyList<int> Slots = [4, 8, 12, 16, 20];

    /// <summary>
    /// 数据区后半段的两个 u32（DAT7/DAT8 = @24/@28）—— **只在标准槽一个都没对上时才扫**。
    ///
    /// 别的模具可能把背光档位放在尾部（32 字节帧的数据区一共 7 个 u32，本机只用了前 5 个）。
    /// 标准槽已经对上的机型不会走到这一遍，所以它对已验证机型的影响是零。
    /// </summary>
    public static readonly IReadOnlyList<int> TailSlots = [24, 28];

    /// <summary>
    /// 档位所在那个 u32 的低 8 位（普通实现里整字就是档位，取低 8 位只是为了容忍高位噪声）。
    /// </summary>
    public static int LowByte(uint v) => (int)(v & 0xFF);

    /// <summary>
    /// 这个值像不像"我们刚写进去的那个档位"。两种实测见过的存法都要认：
    /// <list type="bullet">
    ///   <item>整字就是档位（0 / 1 / 2）—— 低 8 位等于它；</item>
    ///   <item>`(effect &lt;&lt; 4) | level`（本机 0x0100 的 DT3D 就是这个形状）——
    ///     档位在**低 4 位**，高 4 位是灯效码，所以 2 档读回来是 0x12 而不是 0x02。</item>
    /// </list>
    /// ⚠️ 早先只比低 8 位，于是 `(effect&lt;&lt;4)|level` 那种机器**永远判不出来** ——
    ///    灯明明亮了，包里却写"没对上档位"，比不判更糟。
    /// </summary>
    public static bool LooksLikeLevel(uint v, int level)
        => (v & 0xFF) == (uint)level || (v & 0x0F) == (uint)level;

    /// <summary>
    /// 从一个已知是"档位字段"的值里把档位抠出来（写回去时要用它恢复原档）。
    /// 认不出就返回 null —— 那时候调用方会退到"最亮档"，而不是瞎猜一个值写进 EC。
    /// </summary>
    public static int? LevelOf(uint v)
    {
        if (v <= 2) return (int)v;                          // 整字就是档位
        if (v <= 0xFF && (v & 0x0F) <= 2) return (int)(v & 0x0F);   // (effect<<4)|level
        return null;
    }

    /// <summary>
    /// 背光档位字段的定位结论。
    /// <paramref name="Offset"/> 有值才敢写进 layout —— <paramref name="Matched"/> 多于一个
    /// 就说明"这么判分不清"，那是 <see cref="Offset"/> 为 null 的理由，不是沉默。
    /// </summary>
    public sealed record LevelScan(int? Offset, IReadOnlyList<int> Matched, string Why);

    /// <summary>
    /// 从"写前 / 写入低档后 / 写入高档后"三帧状态里找出背光档位的偏移。
    ///
    /// 判据（两条同时成立才算候选）：
    /// <list type="number">
    ///   <item>两帧在该偏移上的**差值恰好等于两个档位之差**（写 0 与写 2 ⇒ 差 2）——
    ///     这一条同时挡住了"温度在这 1 秒里自己动了 1 度"和"这个字段压根没变"；</item>
    ///   <item>高档那一帧的值像那个档位（<see cref="LooksLikeLevel"/>）。</item>
    /// </list>
    /// 满足条件的偏移**只有一个**才认；两个都满足就说不清是哪一个，不猜。
    ///
    /// ⚠️ 不比对整段 u32 相等：EC 把档位、灯效、分区塞在同一个字里，
    ///    要求整字等于 0/2 会一个都认不出来。
    /// </summary>
    public static LevelScan ScanLevel(
        IReadOnlyDictionary<int, uint> before,
        IReadOnlyDictionary<int, uint> afterLow, int levelLow,
        IReadOnlyDictionary<int, uint> afterHigh, int levelHigh,
        IReadOnlyList<int>? slots = null)
    {
        var wantDelta = (long)levelHigh - levelLow;
        var scanSlots = slots ?? Slots;

        var matched = Find(before, afterLow, afterHigh, levelHigh, wantDelta, scanSlots);
        if (matched.Count == 1)
            return new LevelScan(matched[0], matched,
                $"写入 {levelLow}→{levelHigh} 档后 status@{matched[0]} 跟着变了 {wantDelta}，值也对得上档位");

        // 调用方指定了槽位 / 标准槽已经有（多个）候选 ⇒ 不再往后扫，结论与以前一字不差。
        if (slots is not null || matched.Count > 0)
            return Explain(matched, wantDelta);

        // 标准槽一个都没对上 ⇒ 这台可能把档位放在数据区后半段（别的广模常见放法）。
        var tail = Find(before, afterLow, afterHigh, levelHigh, wantDelta, TailSlots);
        if (tail.Count == 0) return Explain(matched, wantDelta);

        return tail.Count == 1
            ? new LevelScan(tail[0], tail,
                $"写入 {levelLow}→{levelHigh} 档后 status@{tail[0]} 跟着变了 {wantDelta}，值也对得上档位"
                + "（标准槽 @4~@20 没有，档位在数据区后半段）")
            : new LevelScan(null, tail,
                $"{tail.Count} 个尾部偏移（{string.Join("/", tail)}）都跟着变，分不清哪个是档位");
    }

    /// <summary>在给定槽位里找"差值恰好等于档位差、且高档那一帧像档位"的偏移。</summary>
    private static List<int> Find(IReadOnlyDictionary<int, uint> before,
                                  IReadOnlyDictionary<int, uint> afterLow,
                                  IReadOnlyDictionary<int, uint> afterHigh,
                                  int levelHigh, long wantDelta,
                                  IReadOnlyList<int> slots)
    {
        var matched = new List<int>();
        foreach (var off in slots)
        {
            if (!afterLow.TryGetValue(off, out var lo) || !afterHigh.TryGetValue(off, out var hi)) continue;
            if ((long)hi - lo != wantDelta) continue;
            if (!LooksLikeLevel(hi, levelHigh)) continue;

            // 写前那一帧若与两帧都相同，说明它没被我们改动过（差值判据已排除，这里只是明说）
            if (before.TryGetValue(off, out var b) && b == lo && b == hi) continue;

            matched.Add(off);
        }
        return matched;
    }

    /// <summary>0 个 / 多个候选时的结论文案（与早先实现一字不差）。</summary>
    private static LevelScan Explain(List<int> matched, long wantDelta)
    {
        if (matched.Count == 0)
            return new LevelScan(null, matched,
                "写进去的档位没在任何偏移上读回 —— 命令可能被 SW 选择子/分区地址吃掉，"
                + "或档位不在 status 帧里");

        return new LevelScan(null, matched,
            $"{matched.Count} 个偏移（{string.Join("/", matched)}）都跟着变，分不清哪个是档位");
    }

    /// <summary>
    /// 该往 APMD 里写哪几个值。
    ///
    /// 只取**配置里已经在用的**（`presets.perf.modes[*].apmd`）：那几个值本来就是我们
    /// 每天在往这台机器上写的，再写一遍不引入任何新风险；反过来"顺手试一下 3/4/5"
    /// 就是在拿别人的功耗墙做实验。配置里一个都没有时才退回 0/1/2（广达这三档的通行取值）。
    ///
    /// 上限 15：一个 u32 槽里的低字节区域，写更大的值没有任何依据。
    /// </summary>
    public static IReadOnlyList<int> ApmdCandidates(IEnumerable<int?> configured)
    {
        var set = new SortedSet<int>();
        foreach (var c in configured)
            if (c is >= 0 and <= 15) set.Add(c.Value);

        if (set.Count == 0)
        {
            set.Add(0);
            set.Add(1);
            set.Add(2);
        }
        return set.ToList();
    }
}
