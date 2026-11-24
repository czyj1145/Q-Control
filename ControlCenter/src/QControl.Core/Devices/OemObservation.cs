using System.Text.Json.Nodes;

namespace QControl.Core.Devices;

/// <summary>
/// 一条观察到的原厂写命令（抓包去重后）。<see cref="Gwf0"/> 恒为 0xFB00（写组），
/// <see cref="Gwf1"/> 是子命令，<see cref="Raw"/> 是那 32 字节报文。
///
/// <see cref="At"/> = 这条命令第一次出现在缓冲区里的时刻（本地时间）。有了它，
/// 「引导适配」才能按用户点的标记把帧切成段、把语义绑到标签上（见 <see cref="AdoptGuide"/>）。
/// 老调用方（只做不分段的归纳）不传就留 <c>default</c>，不影响 <see cref="OemObservation"/>。
/// </summary>
public sealed record ObservedWrite(ushort Gwf0, ushort Gwf1, byte[] Raw, DateTimeOffset At = default);

/// <summary>
/// 从「观察原厂」抓到的写命令里**能自动确定什么** —— 纯逻辑，不碰 EC、不碰磁盘。
///
/// 为什么需要这一步：一键采集能证明「读得出来」、能证明「写进去落到 EC 了」，
/// 但**证明不了语义**（风扇几路、键盘灯分几个区、三档性能对应哪个 APMD、有没有 MUX）。
/// 那些只能从原厂自己的写命令里看 —— 原厂切档时发了哪条子命令、带什么参数，就是
/// 这台机器行为的唯一客观证据。这里把"观察到什么"翻译成"能确定什么"。
///
/// 一条铁律，与采集侧同源：**只填证据充分的**。看到 0x0100 就能确定有键盘灯；
/// 但「几个区」只能从地址值域**推断** —— 那是"大概"，归进 <c>Summary</c> 和 Notes，
/// 由作者在 <c>--import-pack</c> 复核时确认，**不写死进 caps**（填错分区 = 灯亮错位置）。
/// </summary>
public static class OemObservation
{
    // ---- 子命令语义（RW_GMWMI 的 GWF1，协议知识，与机型无关）----
    /// <summary>键盘背光（LKBA/LKBB/…/WKCL）。</summary>
    public const int SubBacklight = 0x0100;
    /// <summary>显卡模式（GFXT）。</summary>
    public const int SubGfx = 0x0203;
    /// <summary>屏幕高刷开关（HSRE）。</summary>
    public const int SubHsre = 0x0204;
    /// <summary>手动风扇转速（UFA）。</summary>
    public const int SubFan = 0x0205;
    /// <summary>原厂曲线 / 标志位（OMCC）。</summary>
    public const int SubOmcc = 0x0206;
    /// <summary>性能模式（APMD）。</summary>
    public const int SubPerf = 0x0300;

    // ---- 报文字段偏移（与 EcFrame 一致）----
    private const int Dat2 = 4;     // GWD0：u32（性能档 APMD / 显卡模式 / 背光地址的低字节）

    /// <summary>背光帧里「分区地址」在 GWD0 的低字节（capture_oem 实证：地址 3/4/5）。</summary>
    private const int BacklightAddr = Dat2;
    /// <summary>背光帧里 B/G/R 在 DT3A..C，灯效+档位在 DT3D（高 4 位灯效、低 4 位档位）。</summary>
    private const int Dt3a = 8, Dt3b = 9, Dt3c = 10, Dt3d = 11;

    public sealed record Result(JsonObject Caps, JsonObject Summary, IReadOnlyList<string> Notes);

    /// <summary>
    /// 把一组观察到的写命令**并进档案**：caps 增量写进 <c>profile.caps</c>，
    /// 结论与证据写进 <c>profile.adopt.observation</c> 和报告顶层的 <c>observation</c>。
    ///
    /// ⚠️ **每一处赋值都必须 DeepClone**。JSON DOM 一个节点只能有一个父：
    ///    同一个 <c>Summary</c> 先给 <c>observation</c> 再给 <c>adopt.observation</c>，
    ///    第二次会抛 `InvalidOperationException: The node already has a parent`，
    ///    整次采集直接失败（这正是 11-10 用户报「验证失败」的真因 —— 只读采集不经过这段，
    ///    所以只在带观察的完整采集里炸）。
    /// </summary>
    public static Result Apply(JsonObject profile, JsonObject observation, IEnumerable<ObservedWrite> writes)
    {
        var r = Analyze(writes);

        if (profile["caps"] is JsonObject caps)
            foreach (var (k, v) in r.Caps)
                caps[k] = v?.DeepClone();

        observation["conclusion"] = r.Caps.DeepClone();
        observation["summary"] = r.Summary.DeepClone();

        var adopt = profile["adopt"] as JsonObject ?? new JsonObject();
        if (profile["adopt"] is null) profile["adopt"] = adopt;

        adopt["observation"] = new JsonObject
        {
            ["write_cmds"] = (observation["write_cmds"] as JsonArray)?.DeepClone(),
            ["conclusion"] = r.Caps.DeepClone(),
            ["summary"] = r.Summary.DeepClone(),
            ["notes"] = StrArr(r.Notes),
        };

        if (adopt["self_check"] is JsonArray sc)
            foreach (var n in r.Notes) sc.Add(n);

        return r;
    }

    /// <summary>
    /// 分析一组（去重后的）原厂写命令，输出 caps 增量、证据摘要、给作者看的判断依据。
    /// </summary>
    public static Result Analyze(IEnumerable<ObservedWrite> writes)
    {
        var backlightAddr = new SortedSet<int>();
        var backlightEffect = new SortedSet<int>();
        var backlightLevel = new SortedSet<int>();
        var perfApmd = new SortedSet<int>();
        var gfxValues = new SortedSet<int>();
        var fanSlots = new SortedSet<int>();      // 0x0205 里出现过非零的字节位置（相对 DT3A）

        bool sawBacklight = false, sawGfx = false, sawFan = false, sawOmcc = false, sawHsre = false;

        foreach (var w in writes)
        {
            if (w.Gwf0 != 0xFB00) continue;       // 抓包侧已经过滤，这里再守一道
            var raw = w.Raw;
            switch (w.Gwf1)
            {
                case SubBacklight:
                    sawBacklight = true;
                    var addr = ByteAt(raw, BacklightAddr);
                    if (addr != 0) backlightAddr.Add(addr);
                    backlightEffect.Add(ByteAt(raw, Dt3d) >> 4);
                    backlightLevel.Add(ByteAt(raw, Dt3d) & 0x0F);
                    break;

                case SubGfx:
                    sawGfx = true;
                    gfxValues.Add(LowByteU32(raw, Dat2));
                    break;

                case SubPerf:
                    perfApmd.Add(LowByteU32(raw, Dat2));
                    break;

                case SubFan:
                    sawFan = true;
                    for (var i = 0; i < 4; i++)
                        if (ByteAt(raw, Dt3a + i) != 0) fanSlots.Add(i);
                    break;

                case SubOmcc:
                    sawOmcc = true;
                    break;

                case SubHsre:
                    sawHsre = true;
                    break;
            }
        }

        var caps = new JsonObject();
        if (sawBacklight) caps["keyboard_rgb"] = true;
        if (sawGfx) caps["gfx_switch"] = true;
        if (sawFan) caps["fan_manual"] = true;
        if (sawOmcc) caps["fan_curve"] = true;

        var summary = new JsonObject
        {
            ["perf_apmd_values"] = Arr(perfApmd),
            ["kb_zone_addrs"] = Arr(backlightAddr),
            ["kb_effects"] = Arr(backlightEffect),
            ["kb_levels"] = Arr(backlightLevel),
            ["gfx_values"] = Arr(gfxValues),
            ["fan_slots"] = Arr(fanSlots),
        };

        var notes = new List<string>();
        if (sawBacklight)
            notes.Add(backlightAddr.Count > 0
                ? $"观察到原厂写键盘背光（0x0100）：地址 {Join(backlightAddr)}，"
                  + $"档位 {Join(backlightLevel)}、灯效 {Join(backlightEffect)}。"
                  + "kb_zones 按「非零分区地址数」推断，需复核（0 地址 = 整键盘，已排除）。"
                : "观察到原厂写键盘背光（0x0100），但地址全是 0（整键盘）—— 分区数无法从本次观察推断。");
        if (sawGfx)
            notes.Add($"观察到原厂切显卡模式（0x0203）：值 {Join(gfxValues)} ⇒ 有 MUX/显卡切换。");
        if (perfApmd.Count > 0)
            notes.Add($"观察到原厂切性能档（0x0300）：APMD 值 {Join(perfApmd)}。"
                      + "「哪个值对应哪一档」名称仍需按原厂界面顺序确认。");
        if (sawFan)
            notes.Add($"观察到原厂写手动风扇（0x0205）：非零 UFA 槽位 {Join(fanSlots)}（相对 DT3A）。"
                      + "风扇路数按槽位数推断，需复核。");
        if (sawOmcc)
            notes.Add("观察到原厂写 0x0206（OMCC / 原厂曲线标志位）⇒ 有风扇曲线能力。");
        if (sawHsre)
            notes.Add("观察到原厂写 0x0204（HSRE，屏幕高刷开关）—— 与散热无关，仅记录。");

        return new Result(caps, summary, notes);
    }

    private static int ByteAt(byte[] raw, int offset)
        => offset >= 0 && offset < raw.Length ? raw[offset] : 0;

    private static int LowByteU32(byte[] raw, int offset)
        => ByteAt(raw, offset);

    private static JsonArray Arr(IEnumerable<int> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(v);
        return a;
    }

    private static JsonArray StrArr(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(v);
        return a;
    }

    private static string Join(IEnumerable<int> values)
        => string.Join("/", values.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
