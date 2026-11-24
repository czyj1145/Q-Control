using System.Text.Json.Nodes;
using QControl.Core.Ec;
using QControl.Core.Model;

namespace QControl.Core.Devices;

/// <summary>
/// 一条机型档案里的 EC 传输规格。**所有命令号都在这里** ——
/// `hal` 里不再有硬编码命令（那是 2026-10-18 之前的旧样子）。
///
/// 档案里的 EC 规格。默认值是"什么都没配"时的兜底，
/// 不是给本机用的 —— 本机走 `devices/quanta-nl5.json`。
/// </summary>
public sealed class EcSpec
{
    public string Namespace { get; }
    public string Class { get; }
    public string Instance { get; }
    public ushort ReadGroup { get; }
    public ushort WriteGroup { get; }
    public double MinGap { get; }

    /// <summary>
    /// 两条**读**命令之间的间隔（秒），档案键 `read_gap`。
    ///
    /// 与 <see cref="MinGap"/> 是两件事：`min_gap` 是"写 / 写之后至少隔多久"，
    /// 这个只管"读接读"。实测往返 12 ms，0.45 s 的保守值是整帧慢的唯一原因
    /// （见 `GmWmi.ReadGap` 的注释）。
    ///
    /// ⚠️ **档案不写就退回 <see cref="MinGap"/>**（= 今天的行为不变）。
    ///    这是刻意的：别的广达模具没测过，不该替它把间隔压到 50 ms；
    ///    要快就在那份档案里显式写 `read_gap`（本机 `quanta-nl5.json` 写了 0 = 不等）。
    /// </summary>
    public double ReadGap { get; }

    /// <summary>
    /// 一次「写批」内部两条写命令之间的间隔（秒），档案键 `burst_gap`。
    ///
    /// 什么叫写批：一次用户操作要发多条写命令 —— 典型是三区同色/神光同步，
    /// 一条 `0x0100` 只能写一个地址，三个区就是三条。
    ///
    /// 为什么可以比 <see cref="MinGap"/> 小一个数量级（本机 0.02s vs 0.2s）：
    /// `0x0100` 分支内部对 EC 有两次忙等 `While(ECRD(LKBE) ≠ …)`，
    /// **固件自己会把命令串起来**，上一条没落地时后一条的 WMI Put 就阻塞在那里 ——
    /// 原厂软件调一次色连发三条也是这么走的（用户看到的"三个区同时变"就是它）。
    /// 0.45s 那条写间隔是给"写完之后别立刻读"用的，不是给背靠背的写用的。
    ///
    /// ⚠️ **档案不写就退回逐条写**（= 今天的行为不变）。别的广达模具没测过，
    ///    不该替它把间隔压下去；要快就在那份档案里显式写 `burst_gap`。
    /// </summary>
    public double BurstGap { get; }

    /// <summary>
    /// 「整条灯带」的 LED 地址（档案键 `ec.kb_trunk_addr`），**null = 没有验证过整条地址**。
    ///
    /// 2026-11-15 真机标定（`recon/led_all_probe.py`，本机 NL5）：往 `addr = 6` 写一条
    /// `0x0100`，**三个分区同时**变成同一个颜色；而 5/4/3 各写一条时是一条一条地变。
    /// 这就是原厂「神光同步」能做到"同时"的机制 —— 不是批量写，是整条地址。
    ///
    /// ⚠️ 只在**所有分区要同一个颜色**时才能用它（分区异色必须逐区写）。
    /// ⚠️ 档案不写就别用 —— 别的模具整条地址未必是 6（历史注释里甚至有"发 6 不会覆盖
    ///    B/C"的反面记录），写错可能只亮一块或直接灭灯。
    /// </summary>
    public int? KbTrunkAddr { get; }

    /// <summary>
    /// 风扇路数（档案 `ec.fans`）。**会被 SKU 探测改**（见 <see cref="ApplySku"/>），
    /// 所以是私有 set —— 外部只读，改只有一条路。
    ///
    /// 为什么允许运行期改：三风扇机型（广达 NLY，SKU 0x33/0x34）和两风扇机型共用
    /// 一模一样的命令表，唯一的区别就是"有没有第三路"。原厂控制台是读 SKU 判的；
    /// 我们原来是靠这份 json 手写 `fans` —— 换一台机器就得手改档案，改错还会把
    /// SYS 槽写进一个不存在的位置。
    /// </summary>
    public int Fans { get; private set; }

    /// <summary>
    /// SKU → 风扇路数（档案 `ec.fans_by_sku`），键可写 `0x33` 或 `51`。
    /// **空 = 不做自动判定**，`fans` 就是最终值（未标定的模具行为一个字都不变）。
    /// </summary>
    public IReadOnlyDictionary<uint, int> FansBySku { get; }

    /// <summary>
    /// 允不允许用**实测读数**把两风扇升级成三风扇（档案 `ec.sys_probe`）。
    ///
    /// <para>
    /// 默认值：档案声明了 `cmd.sys_info` **且** 有 `sys_layout` 就为 true —— 也就是说
    /// "这条命令我能读"，那就值得拿它自己报的转速判一次；没声明 SYS 读命令的模具
    /// （通用档案）一律不探，行为一个字都不变。
    /// </para>
    /// <para>
    /// 判据与节奏见 <see cref="ThirdFanProbe"/>：只有 `0x0207` 的 a3（SYS 转速）
    /// 连续两次落在 300~8000 才升级。两风扇机上这个字段实测恒 0 ⇒ 永远不升级。
    /// 想关掉就在档案里写 `"sys_probe": false`。
    /// </para>
    /// </summary>
    public bool SysProbe { get; }

    /// <summary>
    /// <see cref="Fans"/> 这次是怎么定下来的：`profile`（档案写死）/ `sku`（0x0201 查表）
    /// / `sys_probe`（运行时读到 SYS 转速）。只为诊断 —— 用户报"我这台是三风扇啊"时，
    /// 一眼能看出是档案判错还是探测器没拿到读数。
    /// </summary>
    public string FansSource { get; private set; } = "profile";

    /// <summary>
    /// SYS 那一路的读数偏移表（档案 `ec.sys_layout`），字段名 → (偏移, 长度)。
    ///
    /// ⚠️ **必须和 <see cref="Layout"/> 分开**：`layout` 的偏移是相对 `0x0200` 那一帧的，
    /// 而 SYS 路在**另一条子命令**的帧里（本机家族 = `0x0207` 的 a2=温度 / a3=转速）。
    /// 混在一张表里，等于把 0x0207 的偏移套到 0x0200 上读 —— 读出来是别的寄存器。
    /// </summary>
    public IReadOnlyDictionary<string, (int Offset, int Length)> SysLayout { get; }

    /// <summary>
    /// SYS 路读数用的子命令号（档案 `ec.cmd.sys_info`），**-1 = 没有**。
    /// 没有就不读第三路 —— 宁可不显示，也不拿别的命令的帧当 SYS 数据。
    /// </summary>
    public int SysSub => Cmd.TryGetValue("sys_info", out var v) && v is >= 0 and <= 0xFFFF ? v : -1;

    /// <summary>读数偏移表：字段名 → (偏移, 长度)。**没标定过就为空**。</summary>
    public IReadOnlyDictionary<string, (int Offset, int Length)> Layout { get; }
    /// <summary>
    /// 布局是否已实测标定。为 false 时上层只允许回 `raw` 字节，
    /// 绝不吐 cpu_temp/fan*_rpm —— 偏移猜错等于把某个寄存器当温度显示。
    /// </summary>
    public bool LayoutVerified { get; }
    public IReadOnlyDictionary<string, int> Cmd { get; }
    public IReadOnlyList<string> FanSlots { get; }
    public IReadOnlySet<ushort> ReadOnly { get; }
    public IReadOnlyDictionary<ushort, string> Dangerous { get; }
    public IReadOnlySet<ushort> ConfirmRequired { get; }

    /// <summary>
    /// 哪些子命令的应答会**回显 group+sub**（前 4 字节）。
    ///
    /// 为什么需要这张表：P0 只在 `0x0200` 上验证过回显，P3 实测发现
    /// `0x0202` / `0x0203` / `0x0207` **不回显**（应答直接是数据，如 `14 01 02 02`，
    /// 有的命令甚至回全 0）。对它们做回显校验会把正常应答判成"被抢占"。
    ///
    /// 所以校验范围必须**由档案标定**，不能凭"EC 大概都回显"去推广 ——
    /// 那正是 P3 第一版犯的错：读状态 5/5 通过，其余三条全被误杀。
    /// </summary>
    public IReadOnlySet<ushort> EchoVerified { get; }

    private static readonly string[] DefaultFanSlots = ["d0", "a", "d2"];

    public EcSpec(JsonNode? data)
    {
        var o = data as JsonObject ?? new JsonObject();

        Namespace = Str(o["wmi_namespace"]) ?? @"root\WMI";
        Class = Str(o["wmi_class"]) ?? "RW_GMWMI";
        Instance = Str(o["wmi_instance"]) ?? "";
        ReadGroup = (ushort)Num(o["read_group"], 0xFA00);
        WriteGroup = (ushort)Num(o["write_group"], 0xFB00);

        // 0 / null / 空串都回落 0.2（按「取值失败就用默认值」处理）
        var gap = NumDouble(o["min_gap"]);
        MinGap = gap is > 0 ? gap!.Value : 0.2;

        // 读间隔：档案写了才敢放开（见 ReadGap 的 ⚠️）。0 = **完全不等**（本机这种情况：
        // 一发读的真实耗时 ~10 ms 本身就是天然间隔），缺失 = 跟 min_gap 一样。
        var rgap = NumDouble(o["read_gap"]);
        ReadGap = rgap is not null ? Math.Max(0, rgap.Value) : MinGap;

        // 写批间隔：档案写了才敢连发（见 BurstGap 的 ⚠️）。0 / 缺失 = 关闭，逐条按写间隔。
        var bgap = NumDouble(o["burst_gap"]);
        BurstGap = bgap is > 0 ? bgap!.Value : 0;

        // 整条地址：档案写了一个合法地址才认（见 KbTrunkAddr 的 ⚠️）。0 / 缺失 = 没有。
        var trunk = Num(o["kb_trunk_addr"], -1);
        KbTrunkAddr = trunk is > 0 and <= 0xFF ? trunk : null;

        Fans = Num(o["fans"], 0);

        var bySku = new Dictionary<uint, int>();
        if (o["fans_by_sku"] is JsonObject fbo)
        {
            foreach (var (k, v) in fbo)
            {
                // 路数只认 1..4：0 或负数写进来当作"没写"，别把风扇数改成 0 让整行消失
                if (TrySku(k, out var sku) && TryInt(v, out var n) && n is >= 1 and <= 4)
                    bySku[sku] = n;
            }
        }
        FansBySku = bySku;

        Layout = ParseLayout(o["layout"]);
        LayoutVerified = Bool(o["layout_verified"]) && Layout.Count > 0;

        // SYS 路：单独的偏移表 + 单独的子命令（见 SysLayout 的 ⚠️）
        SysLayout = ParseLayout(o["sys_layout"]);

        var cmd = new Dictionary<string, int>(StringComparer.Ordinal);
        if (o["cmd"] is JsonObject co)
            foreach (var (k, v) in co)
                cmd[k] = Num(v, -1);
        Cmd = cmd;

        var slots = new List<string>();
        if (o["fan_slots"] is JsonArray sa)
            foreach (var n in sa) if (Str(n) is { Length: > 0 } s) slots.Add(s);
        FanSlots = slots.Count > 0 ? slots : DefaultFanSlots;

        var ro = new HashSet<ushort>();
        if (o["read_only"] is JsonArray roa)
            foreach (var n in roa) if (CmdTable.Parse(Str(n)) is { } c) ro.Add(c);
        ReadOnly = ro;

        var dg = new Dictionary<ushort, string>();
        if (o["dangerous"] is JsonObject dgo)
            foreach (var (k, v) in dgo)
                if (CmdTable.Parse(k) is { } c) dg[c] = Str(v) ?? "";
        Dangerous = dg;

        var cf = new HashSet<ushort>();
        if (o["confirm_required"] is JsonArray cfa)
            foreach (var n in cfa) if (CmdTable.Parse(Str(n)) is { } c) cf.Add(c);
        ConfirmRequired = cf;

        var ev = new HashSet<ushort>();
        if (o["echo_verified"] is JsonArray eva)
            foreach (var n in eva) if (CmdTable.Parse(Str(n)) is { } c) ev.Add(c);
        EchoVerified = ev;

        // `sys_probe` 默认值要在 Cmd / SysLayout 解析完之后才能算（见属性注释）。
        SysProbe = o.ContainsKey("sys_probe")
            ? Bool(o["sys_probe"])
            : Cmd.ContainsKey("sys_info") && SysLayout.Count > 0;
    }

    /// <summary>子命令号；未配置返回 <paramref name="fallback"/>（默认 -1 = "不支持"）。</summary>
    public int Sub(string name, int fallback = -1)
        => Cmd.TryGetValue(name, out var v) ? v : fallback;

    /// <summary>档案里有没有命令表 —— 没有就什么都写不出去，别摆个假开关。</summary>
    public bool Has => Cmd.Count > 0;

    /// <summary>
    /// 按固件型号 SKU 定风扇路数（档案 `ec.fans_by_sku`）。命中且与当前值不同才改，
    /// 返回是否改了 —— 调用方只在 true 时才值得写一行日志。
    ///
    /// <para>
    /// 只在档案**显式**列了这张表时才生效：别的广达模具没标过 SKU，不该替它猜
    /// "SKU 51 就是三风扇"那套结论（那是 NLY 的实证，不是通用规律）。
    /// </para>
    /// </summary>
    public bool ApplySku(uint sku)
    {
        if (!FansBySku.TryGetValue(sku, out var n) || n <= 0 || n == Fans) return false;
        Fans = n;
        FansSource = "sku";
        return true;
    }

    /// <summary>
    /// 运行时把风扇路数**往上**调（目前只有一条路：`0x0207` 读到可信 SYS 转速，
    /// 见 <see cref="ThirdFanProbe"/>）。**只升不降** —— 已经是 3 的不会再被调回 2，
    /// 免得探测器某一拍没读到转速就把用户的风扇行拆掉。
    /// </summary>
    /// <param name="n">目标路数（1..4）。</param>
    /// <param name="source">写进 <see cref="FansSource"/> 的来源标记。</param>
    /// <returns>真的改了才 true（调用方只在这时才该打日志）。</returns>
    public bool RaiseFans(int n, string source)
    {
        if (n is < 1 or > 4 || n <= Fans) return false;
        Fans = n;
        FansSource = source;
        return true;
    }

    /// <summary>`/api/device` 里的 ec 块（十六进制大写，与早先实现一字不差）。</summary>
    public JsonObject View()
    {
        var cmd = new JsonObject();
        foreach (var (k, v) in Cmd.Where(kv => kv.Value >= 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            cmd[k] = $"0x{v:X4}";

        return new JsonObject
        {
            ["wmi_class"] = Class,
            ["wmi_instance"] = Instance,
            ["read_group"] = $"0x{ReadGroup:X4}",
            ["write_group"] = $"0x{WriteGroup:X4}",
            ["fans"] = Fans,
            // 这个路数是"怎么定下来的"（profile / sku / sys_probe），排查"我这台明明三风扇"用
            ["fans_source"] = FansSource,
            ["sys_probe"] = SysProbe,
            ["layout_verified"] = LayoutVerified,
            ["cmd"] = cmd,
        };
    }

    // ---------------------------------------------------------------- 解析小工具
    // 档案里的数字既可能是 512 也可能是 "0x0200"（十六进制更可读），两种都要吃。

    /// <summary>
    /// `{"cpu_temp":[4,4], …}` → 偏移表。形状不对的项**跳过**（不整份档案报废）：
    /// 一份写错一个键的 json 不该让整台机器变成"未适配"。
    /// </summary>
    private static Dictionary<string, (int Offset, int Length)> ParseLayout(JsonNode? node)
    {
        var layout = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        if (node is not JsonObject lo) return layout;

        foreach (var (k, v) in lo)
        {
            if (v is not JsonArray arr || arr.Count < 2) continue;
            // 长度只认 1..4（字节 / 半字 / 三字节 / 字）；越界按 4 处理，免得读越界
            if (TryInt(arr[0], out var off) && TryInt(arr[1], out var len)
                && off >= 0 && off < EcFrame.Size)
            {
                var l = len is >= 1 and <= 4 ? len : 4;
                // ⚠️ 必须夹到帧尾：`[30,4]` 只判 off<32 会通过，而 EcFrame.U32(30) 因越界
                //    **返回 0 且不抛** ⇒ 界面"温度恒为 0"、风扇那一路永远不写，还宣称布局已验证。
                if (off + l > EcFrame.Size) l = EcFrame.Size - off;
                if (l > 0) layout[k] = (off, l);
            }
        }
        return layout;
    }

    /// <summary>`"0x33"` / `"51"` → SKU 数值。解析不了返回 false（这一项跳过）。</summary>
    private static bool TrySku(string? key, out uint sku)
    {
        sku = 0;
        var s = (key ?? "").Trim();
        if (s.Length == 0) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber,
                                 System.Globalization.CultureInfo.InvariantCulture, out sku);
        return uint.TryParse(s, System.Globalization.NumberStyles.Integer,
                             System.Globalization.CultureInfo.InvariantCulture, out sku);
    }

    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// 取值规则：bool / null 一律回默认值，数字直接用，
    /// 字符串按 base 0 解析（`0x` / `0o` / `0b` / 十进制都认），失败回默认值。
    /// ⚠️ bool 不算数字是刻意的 —— 在动态类型语言里 true 会被当成 1，
    ///    若不先排除，`"fans": true` 会被当成 1。
    /// </summary>
    private static int Num(JsonNode? n, int fallback)
    {
        if (n is not JsonValue v) return fallback;
        var kind = n.GetValueKind();
        if (kind == System.Text.Json.JsonValueKind.True || kind == System.Text.Json.JsonValueKind.False)
            return fallback;
        if (kind == System.Text.Json.JsonValueKind.Number)
            return v.TryGetValue<int>(out var i) ? i : fallback;
        if (kind == System.Text.Json.JsonValueKind.String && v.TryGetValue<string>(out var s))
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : fallback;
            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(s[2..], System.Globalization.NumberStyles.BinaryNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out var b2) ? b2 : fallback;
            return int.TryParse(s, out var d) ? d : fallback;
        }
        return fallback;
    }

    private static int Num(JsonNode? n, double fallback)
        => Num(n, (int)fallback);

    /// <summary>浮点版：`min_gap` 这类允许小数的字段用它（`"0.2"` 字符串也要吃）。</summary>
    private static double? NumDouble(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        switch (n.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.Number:
                return v.TryGetValue<double>(out var d) ? d : null;
            case System.Text.Json.JsonValueKind.String:
                return double.TryParse(v.GetValue<string>().Trim(),
                                       System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out var s)
                    ? s : null;
            default:
                return null;      // bool 同样不算数值（见 Num 的注释）
        }
    }

    private static bool TryInt(JsonNode? n, out int value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return v.TryGetValue(out value);
        if (n.GetValueKind() == System.Text.Json.JsonValueKind.String)
            return int.TryParse(v.GetValue<string>().Trim(), out value);
        return false;
    }
}
