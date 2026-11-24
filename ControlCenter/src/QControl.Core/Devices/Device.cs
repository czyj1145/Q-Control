using System.Text.Json.Nodes;

namespace QControl.Core.Devices;

/// <summary>
/// 一台机器的「能做什么」判定 —— **全项目唯一的写许可判定口**。
///
/// HAL / 界面 / HTTP 三处都问 <see cref="WritesAllowed"/>，不各自推。
/// 机型档案的只读视图：一份档案解析完就不再改，**不可变**。
/// 用户总闸（<see cref="EcOverride"/>）作为构造参数，改总闸 = 造一个新 Device。
///
/// 为什么做成不可变：总闸与 caps 分开缓存时，缓存必须同时记住"建缓存时的总闸值"，
/// 否则会出现「设置页关了 EC、散热页还在」（2026-10-18 踩过）。
/// 不可变以后这个坑从根上消失。
/// </summary>
public sealed class Device
{
    public JsonObject Raw { get; }
    public MatchResult? Reason { get; }
    /// <summary>用户总闸：null = 跟随档案，true/false = 用户在设置页的明确选择。</summary>
    public bool? EcOverride { get; }

    public string Id { get; }
    public string Name { get; }
    public string Vendor { get; }
    public string Mold { get; }
    public bool Verified { get; }
    /// <summary>档案层面的写入许可（未写则跟随 verified）。**不是**最终判据。</summary>
    public bool ProfileWrites { get; }
    public EcSpec Ec { get; }
    public IReadOnlyList<string> Notes { get; }
    public IReadOnlyList<string> MatchedBy { get; }

    /// <summary>
    /// 键盘之外的独立灯珠（`ec.kb_extra_lights`，名字 → EC 0x0100 槽位地址）。
    /// 目前只认 `logo` / `tail`；**当前只有猎刃16 声明了 Logo**，没有机型声明尾灯。
    /// 它们**不属于键盘 A/B/C 分区**，不进 `caps.kb_zones`；没声明的机型返回空表。
    /// <para>
    /// ⚠️ 档案里写了地址 ≠ 这台机器一定有那颗灯，还要过 <see cref="ExtraLightsByProfile"/>：
    /// 按 SKU 升级过来的档案不算"识别到有灯"，见那里的说明。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, int> ExtraLightAddrs
        => ExtraLightsByProfile ? _extraLightsDeclared : EmptyExtraLights;

    private static readonly IReadOnlyDictionary<string, int> EmptyExtraLights
        = new Dictionary<string, int>(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, int> _extraLightsDeclared;

    /// <summary>
    /// 这台机器**按身份判定**该不该有独立灯珠 —— 也就是最终结论，没有用户开关覆盖。
    ///
    /// <para>
    /// 为什么要跟风扇路数分开：`ec.kb_extra_lights` 写在机型档案里，而机型档案可能是
    /// **按 EC SKU 升级**过来的（三风扇 ⇒ 猎刃16）。但同模具的三风扇机不一定都有 Logo ——
    /// 让「风扇几路」连带决定「有没有灯」，就会给没灯的机器画出一个 Logo 色块。
    /// 所以这里单独一个位：只有名字（SMBIOS 品牌+型号）直接命中档案时才为 true。
    /// </para>
    /// <para>
    /// 2026-11-18 用户要求去掉设置页那个「自动 / 强制开 / 关」三态：识别到就该有效果，
    /// 识别不到就不该出现 —— 不该让用户去纠正软件的判断。真机名字没认出来但确实有灯的，
    /// 靠档案修（改 `match` 正则或放 `%ProgramData%\Q-Control\devices\*.json`），不靠开关。
    /// </para>
    /// </summary>
    public bool ExtraLightsByProfile { get; }

    /// <summary>
    /// 所有写灯/画灯路径的判据。现在**就等于身份判定结果**（保留这个名字是为了让调用点
    /// 一眼看出"这是生效值、不是档案里写了什么"）。
    /// </summary>
    public bool ExtraLightsAllowed => ExtraLightsByProfile;

    /// <summary>档案声明了灯珠、但这次没给 —— 诊断用（"不是没做，是没敢给"）。</summary>
    public bool ExtraLightsGated => _extraLightsDeclared.Count > 0 && !ExtraLightsAllowed;

    public Device(JsonObject raw, MatchResult? reason = null, bool? ecOverride = null,
                  bool extraLightsByProfile = true)
    {
        Raw = raw ?? new JsonObject();
        Reason = reason;
        EcOverride = ecOverride;

        Id = Str(Raw["id"]) ?? "unsupported";
        Name = Str(Raw["name"]) ?? Id;
        Vendor = Str(Raw["vendor"]) ?? "";
        Mold = Str(Raw["mold"]) ?? "";
        Verified = Truthy(Raw["verified"]);
        // 档案没写 writes_allowed 就跟随 verified —— 未验证的档案一律只读
        ProfileWrites = Raw.ContainsKey("writes_allowed")
            ? Truthy(Raw["writes_allowed"])
            : Verified;
        Ec = new EcSpec(Raw["ec"]);
        _extraLightsDeclared = ParseExtraLights(Raw["ec"] as JsonObject);
        // 没声明灯珠的档案，"有灯"这件事根本不成立 ⇒ 别把 ByProfile 记成 true 误导诊断
        ExtraLightsByProfile = extraLightsByProfile && _extraLightsDeclared.Count > 0;
        MatchedBy = reason?.Fields ?? [];
        Notes = Raw["notes"] is JsonArray na
            ? na.Select(n => Str(n) ?? "").Where(s => s.Length > 0).ToList()
            : [];
    }

    /// <summary>换一个用户总闸值，返回新实例（当前实例不变）。</summary>
    public Device WithEcOverride(bool? value)
        => new(Raw, Reason, value, ExtraLightsByProfile);

    /// <summary>这颗开关有没有意义：档案里得有命令表，没有就写不出去。</summary>
    public bool EcCapable => Ec.Has;

    /// <summary>开关的默认态：已验证机型 = 开，未适配 = 关。</summary>
    public bool EcDefault => ProfileWrites;

    /// <summary>
    /// 最终生效的写许可 = 有命令表 ∧（用户没选过 ? 档案允许 : 用户的选择）。
    /// </summary>
    public bool WritesAllowed
    {
        get
        {
            if (!Ec.Has) return false;          // 没有命令表：开关也救不了
            return EcOverride ?? ProfileWrites;
        }
    }

    /// <summary>被拦时给用户看的一句话（三种原因必须能分清，别糊成"未验证"）。</summary>
    public string DenyHint()
    {
        if (!Ec.Has)
            return $"本机没有可用的 EC 命令表（识别为「{Name}」），"
                 + "控制中心不会向 EC 写入任何东西。";
        if (ProfileWrites)
            return "EC 写入已在设置页关闭（「EC 控制」开关），风扇/背光/模式都不会下发。";
        var why = MatchedBy.Count > 0 ? string.Join("、", MatchedBy) : "无";
        return $"当前机型「{Name}」未适配，EC 写入默认关闭（识别依据：{why}）。"
             + "确认风险后可在设置页「EC 控制」里手动打开。";
    }

    /// <summary>
    /// 机型档案声明的键盘分区数（`caps.kb_zones`，0 = 没声明 / 未知）。
    ///
    /// 这是"这台机器身上有几个分区"的权威答案：`presets.backlight.zones` 里超出这个数的条目
    /// 都不是它的分区 —— 探针往别的地址（本机 8 / 7）试色会把表撑大，
    /// 界面就按撑大的条数画了（2026-11-17 "怎么变成五区了"）。裁剪规则见 BacklightZones.Limit。
    /// </summary>
    public int KbZones => IntOf((Raw["caps"] as JsonObject)?["kb_zones"]);

    /// <summary>
    /// 对外能力表：只表达"这台机器能不能做这件事"，不含任何机型私有数值。
    /// 界面按它做减法。
    /// </summary>
    public JsonObject Caps()
    {
        var explicitCaps = Raw["caps"] as JsonObject ?? new JsonObject();
        var w = WritesAllowed;
        var kbZones = IntOf(explicitCaps["kb_zones"]);

        bool Pick(string key, bool derived)
            => explicitCaps.ContainsKey(key) ? Truthy(explicitCaps[key]) : derived;

        // 路数的真源是 `ec.fans`：采集包两处一起写，SKU 探测改的也是它。
        // `caps.fan_count` 只在档案**没写** `ec.fans` 时兜底 —— 否则 SKU 判出来的
        // 三路会被一个写死的 2 盖掉，那台机器的 SYS 风扇就一直不显示。
        int? fanCount = Ec.Fans > 0
            ? Ec.Fans
            : (explicitCaps.ContainsKey("fan_count") ? IntOrNull(explicitCaps["fan_count"]) : null);

        var caps = new JsonObject
        {
            ["ec"] = Ec.Has,
            // 这颗「EC 控制」开关要不要出现：没有命令表就别摆假开关
            ["ec_switch"] = Ec.Has,
            ["ec_read"] = Ec.LayoutVerified,
            ["writes"] = w,
            ["fan_manual"] = Pick("fan_manual", w && Ec.Sub("fan") >= 0),
            ["fan_curve"] = Pick("fan_curve", w && Ec.Sub("fan") >= 0),
            ["fan_count"] = fanCount is { } fc ? JsonValue.Create(fc) : null,
            ["perf_modes"] = explicitCaps["perf_modes"] is JsonArray pm
                ? new JsonArray(pm.Select(x => x?.DeepClone()).ToArray())
                : new JsonArray(),
            ["gfx_switch"] = Pick("gfx_switch", w && Ec.Sub("gfx") >= 0),
            // 分区数未知（未适配机型）⇒ 背光页画不出 A/B/C 页签，整页不给。
            // "不知道就别装作知道"：写错 LED 地址最多亮错一个区，
            // 但界面上摆三个不存在的分区等于骗人。
            ["keyboard_rgb"] = Pick("keyboard_rgb", w && Ec.Sub("backlight") >= 0) && kbZones > 0,
            ["kb_zones"] = kbZones,
            // ⚠️ 独立灯珠**不放进 Caps**：`caps` 与旧版逐条对齐（见 GoldenDeviceTests
            //    的 device_caps.json），多一个键就等于把"迁移等价"这条证据打掉。
            //    灯的判定走 `/api/device` 的 `lights` 段。
        };

        // 写总闸关掉时：不管档案里写了什么，写相关能力一律关（双保险）
        if (!w)
        {
            foreach (var k in (string[])["fan_manual", "fan_curve", "gfx_switch", "keyboard_rgb"])
                caps[k] = false;
            caps["perf_modes"] = new JsonArray();
        }
        return caps;
    }

    /// <summary>`/api/device` 的完整视图。</summary>
    public JsonObject View() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["vendor"] = Vendor,
        ["mold"] = Mold,
        ["verified"] = Verified,
        ["writes_allowed"] = WritesAllowed,
        // EC 控制开关三要素（设置页画开关就用这三个，别自己推默认值）
        ["ec_capable"] = EcCapable,
        ["ec_default"] = EcDefault,
        ["ec_user"] = EcOverride,
        ["caps"] = Caps(),
        ["ec"] = Ec.View(),
        ["matched_by"] = new JsonArray(MatchedBy.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        ["notes"] = new JsonArray(Notes.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
        // 独立灯珠（Logo / 尾灯）的判定结果。**故意不放进 caps** —— 那是与旧版逐条对齐的
        // 契约表，多一个键就打掉了"迁移等价"这条证据（见 GoldenDeviceTests）。
        // `gated` 说明「档案声明了但这次没给」（按 SKU 升级过来的同模具机就是这样），
        // 与「机器压根没有这颗灯」区分开，排查时不用猜。
        ["lights"] = new JsonObject
        {
            ["logo"] = ExtraLightAddrs.ContainsKey("logo"),
            ["tail"] = ExtraLightAddrs.ContainsKey("tail"),
            ["gated"] = ExtraLightsGated,
            ["by_profile"] = ExtraLightsByProfile,
        },
    };

    /// <summary>塞进每秒快照的精简版（`/api/status` 每次都带，别放大对象）。</summary>
    public JsonObject Summary() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["verified"] = Verified,
        ["writes_allowed"] = WritesAllowed,
        ["ec_capable"] = EcCapable,
        ["ec_default"] = EcDefault,
        ["ec_user"] = EcOverride,
        ["caps"] = Caps(),
    };

    // ---------------------------------------------------------------- 小工具

    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>「真值」判定：null/false/0/空串/空集合 全是假。</summary>
    public static bool Truthy(JsonNode? n)
    {
        if (n is null) return false;
        switch (n.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.True: return true;
            case System.Text.Json.JsonValueKind.False: return false;
            case System.Text.Json.JsonValueKind.Null: return false;
            case System.Text.Json.JsonValueKind.String:
                return n is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length > 0;
            case System.Text.Json.JsonValueKind.Number:
                // ⚠️ 不能用 TryGetValue<double>()：它**不做跨数值类型转换** ——
                //    `JsonValue.Create(1)` 底层是 Int32，取 double 会返回 false，
                //    于是 1 被当成"假"，用户手改的 `"enabled": 1` 会被静默忽略。
                //    走文本解析才稳（和 tests/Golden.AsDouble 是同一个坑）。
                return double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture,
                                       out var d) && d != 0;
            case System.Text.Json.JsonValueKind.Array:
                return n is JsonArray a && a.Count > 0;
            case System.Text.Json.JsonValueKind.Object:
                return n is JsonObject o && o.Count > 0;
            default: return false;
        }
    }

    private static int IntOf(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out var p)) return p;
        }
        return 0;
    }

    /// <summary>只认档案明确列出的独立灯珠；野名字 / 越界地址一概丢弃（不替机器猜硬件）。</summary>
    private static IReadOnlyDictionary<string, int> ParseExtraLights(JsonObject? ec)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ec?["kb_extra_lights"] is not JsonObject obj) return map;
        foreach (var (name, node) in obj)
        {
            if (name is not ("logo" or "tail")) continue;
            var addr = IntOf(node);
            if (addr is >= 1 and <= 255) map[name] = addr;
        }
        return map;
    }

    private static int? IntOrNull(JsonNode? n)
        => n is null ? null : IntOf(n);
}
