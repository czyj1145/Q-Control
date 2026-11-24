using System.Text.Json.Nodes;
using QControl.Core.Data;

namespace QControl.Core.Devices;

/// <summary>
/// 给定身份字段 → 选出最合适的机型档案。
///
/// 匹配顺序：`priority` 降序、同 priority 按 id 升序（专用档案必须在通用档案前面）。
/// 全军覆没时回 <c>device.unsupported.json</c> —— 那份档案**连命令表都没有**，
/// 所以开关也救不了，永远不写 EC。
///
/// 档案来源有两处：打包时嵌进程序的 `devices/*.json`，以及**本机采集出来**的
/// `%ProgramData%\Q-Control\devices\*.json`（见 <see cref="UseUserProfiles"/>）。
/// 用户目录必须在第一次 <see cref="Resolve"/> 之前设置 —— AppState 构造里就是这么做的。
/// </summary>
public static class DeviceResolver
{
    private static readonly object _lock = new();
    private static string? _userDir;
    private static IReadOnlyList<DeviceProfile>? _cache;

    private static readonly Lazy<JsonObject> UnsupportedRaw =
        new(() => EmbeddedData.ReadObject("device.unsupported.json"));

    /// <summary>
    /// 指定用户档案目录（`null` = 只用嵌入档案）。
    ///
    /// ⚠️ 设置后清缓存，但**已经在跑的 Device 不会自己变** —— 那要重建 GmWmi / EcHal /
    ///    风扇引擎整条链（当前 Device 被 EcHal、FanCurveEngine、HotkeyService 一起持有）。
    ///    所以采集器写完档案必须告诉用户"重启控制中心后生效"，不能假装已经热生效。
    /// </summary>
    public static void UseUserProfiles(string? dir)
    {
        lock (_lock)
        {
            if (string.Equals(_userDir, dir, StringComparison.OrdinalIgnoreCase) && _cache is not null)
                return;
            _userDir = dir;
            _cache = null;
        }
    }

    /// <summary>当前生效的用户档案目录（诊断用；没设过是 null）。</summary>
    public static string? UserProfileDir
    {
        get { lock (_lock) return _userDir; }
    }

    /// <summary>全部已加载的档案（按匹配优先级排序）。</summary>
    public static IReadOnlyList<DeviceProfile> AllProfiles
    {
        get
        {
            lock (_lock)
                return _cache ??= DeviceProfile.LoadAll(_userDir);
        }
    }

    /// <summary>未识别机型时的空档案原文。</summary>
    public static JsonObject Unsupported => (JsonObject)UnsupportedRaw.Value.DeepClone();

    /// <summary>
    /// 调试/验收用：**强制**选用某个内置档案（`--device &lt;id&gt;` / 环境变量 `CC_DEVICE`）。
    ///
    /// 为什么要有它（2026-11-02）：未适配机型的界面（温度/转速/风扇曲线该整块消失、
    /// 写入口该关着）在本机根本看不到 —— 本机命中的永远是 nl5。没有这个口子，
    /// 每一次改 UI 都在"猜别的机型会怎样"，而第一批用户恰恰是别的机型。
    ///
    /// ⚠️ 强制来的档案**一律按未验证处理**（`verified` / `writes_allowed` 都压成 false）：
    ///    这只能用来"预览界面"，绝不能变成"给没标定的机器解锁写 EC"的旁路。
    /// ⚠️ 只认 <see cref="AllProfiles"/> 里真实存在的 id；写错就当没设，照常匹配。
    /// </summary>
    public static string? ForceId { get; set; }

    /// <summary>
    /// 解析机型。<paramref name="ident"/> 由 Hal 层从注册表/SMBIOS 采集，
    /// 形状如 <c>{"system_vendor": "...", "vendor_registry": {"NL5": true}}</c>。
    /// </summary>
    public static Device Resolve(JsonObject ident, bool? ecOverride = null)
        => Resolve(ident, ecOverride, ForceId);

    /// <summary>
    /// 按 **EC 自报的型号 SKU** 找档案 —— 不看机型名、不看注册表、不看 SMBIOS。
    ///
    /// <para>
    /// 干什么用的：同一模具换牌子（七彩虹 隐星 G16 Pro / 雷神 猎刃 16）时，SMBIOS 和
    /// 原厂注册表键都会变，指纹可能一个都不中；但 EC 自己还是会老老实实回答"我是哪个型号"。
    /// 第三方控制台（FanControlPortable）就是这么干的 —— 它的二进制里**没有任何品牌串**，
    /// 只按 SKU 判"这台是不是三风扇"。
    /// </para>
    /// <para>
    /// 认领规则：**只有档案自己在 `ec.fans_by_sku` 里写了这个 SKU，才可能被选中**。
    /// 没写这张表的档案永远不会被认领，所以别的模具一台都不会被误认。
    /// 多份档案都声明同一个 SKU 时按 priority 取高者（同分取已验证的）。
    /// </para>
    /// </summary>
    public static DeviceProfile? ProfileBySku(uint sku)
    {
        DeviceProfile? best = null;

        foreach (var p in AllProfiles)
        {
            if (p.Raw["ec"] is not JsonObject ec) continue;
            if (!new EcSpec(ec).FansBySku.ContainsKey(sku)) continue;   // 没认领这个 SKU
            if (best is null || p.Priority > best.Priority
                || (p.Priority == best.Priority && p.Verified && !best.Verified))
                best = p;
        }

        return best;
    }

    /// <summary>
    /// 已认出机型之后，能不能按 EC SKU **升级**到更专用的档案。
    ///
    /// <para>
    /// 干什么用的：雷神猎刃16 与七彩虹 G16 Pro 同模具，SMBIOS / 原厂注册表键可能都认不出
    /// 「雷神 + 猎刃16」这组名字，于是先落到 <c>quanta-nl5</c> 家族档案。但 EC 的 SKU
    /// 会如实报「我是三风扇」—— 这份档案的 <c>sku_claim_fans: 3</c> 就是说「三路那台归我」。
    /// </para>
    /// <para>
    /// 三重收口：目标档案必须①自己认领这个 SKU（<see cref="ProfileBySku"/>）、②写明
    /// <c>sku_claim_fans</c> 且比当前路数多、③和当前档案同一模具且优先级更高。
    /// 没写这个键的档案一个字节都不动；别的模具、更低的优先级也一律不抢。
    /// 未验证档案沿用旧的 SKU 兜底行为（直接认领，不要求 claim 键）。
    /// </para>
    /// </summary>
    public static DeviceProfile? BetterBySku(Device current, uint sku)
    {
        var best = ProfileBySku(sku);
        if (best is null || best.Id == current.Id) return null;
        if (current.Ec.Sub("sku") < 0) return null;

        // 旧行为：一台都没认出来时，按 SKU 认到任何声明它的档案（不要求 claim 键）。
        if (!current.Verified) return best;

        // 已验证机型：只有目标档案显式认领「更多路风扇」才升级。
        var claim = ClaimFans(best.Raw["sku_claim_fans"]);
        if (claim is not > 0 || claim <= current.Ec.Fans) return null;

        // 目标档案自己要把这个 SKU 判成「够路数」才许升级：
        // `sku_claim_fans: 3` 只对目标档案 `fans_by_sku` 里写了 ≥3 的 SKU 生效。
        //
        // 2026-11-22 外部样本（control-center-lite，隐星 G16 2024 / SKU 52 = 0x34）实测
        // 0x0207 的 a3=0、无第三风扇 ⇒ 0x34 在我们的表里改成 2 路。于是 SKU 52 的机器
        // 不会再被"升"到带 Logo 控件的猎刃16 档案上（那台既没第三风扇也没 Logo）。
        if (best.Raw["ec"] is JsonObject bestEc
            && new EcSpec(bestEc).FansBySku.TryGetValue(sku, out var skuFans)
            && skuFans < claim) return null;

        var bestMold = best.Raw["mold"] is System.Text.Json.Nodes.JsonValue mv
                       && mv.TryGetValue<string>(out var s) ? s : "";
        // ⚠️ 比"模具家族键"，不是整串相等：档案里的 `mold` 是给人看的自由文本 ——
        //    内置 NL5 写 "Quanta NL5（NLXB / NLZD / NLZE / NLY）"，一键采集的草案写
        //    "Quanta NL5"，generic 写 "Quanta / RW_GMWMI 家族"。整串比的话，
        //    同一台机器的草案永远升不到正式档案（2026-11-22 发现）。
        if (!string.Equals(MoldKey(current.Mold), MoldKey(bestMold), StringComparison.Ordinal)) return null;

        var currentPriority = DeviceProfile.TryLoad(current.Raw)?.Priority ?? 0;
        if (best.Priority <= currentPriority) return null;
        return best;
    }

    /// <summary>
    /// 模具家族键：把档案里那句**给人看的** `mold` 归一成可比的键。
    ///
    /// 只取第一个括号 / 斜杠之前的部分，折叠空白、转小写：
    /// <c>"Quanta NL5（NLXB / NLZD / NLZE / NLY）"</c> 与 <c>"Quanta NL5"</c> 都得到
    /// <c>"quanta nl5"</c>；<c>"Quanta / RW_GMWMI 家族"</c> 得到 <c>"quanta"</c>。
    ///
    /// 为什么不用"包含"或"前缀"判：模具名是自由文本，`"Quanta NL5"` 是 `"Quanta NL50"`
    /// 的前缀 —— 那种误判会把一台陌生模具升到别人的档案上，代价是写错 EC。
    /// </summary>
    private static string MoldKey(string? mold)
    {
        var s = (mold ?? "").Trim();
        var cut = s.IndexOfAny(['(', '（', '/']);
        if (cut >= 0) s = s[..cut];
        var sb = new System.Text.StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static int? ClaimFans(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null) return null;
        return double.TryParse(node.ToJsonString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v is > 0 and <= 4
            ? (int)v
            : null;
    }

    /// <summary>
    /// 带强制档案的解析。<paramref name="forceId"/> 为 null 就是正常匹配。
    /// 单独开这个重载是为了**可测**：静态开关会让并行跑的其它机型测试互相污染。
    /// </summary>
    public static Device Resolve(JsonObject ident, bool? ecOverride, string? forceId)
    {
        if (Forced(forceId) is { } forced)
            return new Device(forced, new MatchResult(["forced"], new JsonObject()), ecOverride);

        foreach (var p in AllProfiles)
        {
            if (DeviceMatcher.Match(p, ident) is { } why)
                return new Device(p.Raw, why, ecOverride);
        }
        return new Device(Unsupported, null, ecOverride);
    }

    /// <summary>
    /// 强制档案的**只读副本**：`id` 对上才生效，且把两个写许可字段压成 false。
    /// 档案原文不动（`AllProfiles` 是共享缓存，改它会污染后续正常匹配）。
    /// </summary>
    private static JsonObject? Forced(string? forceId)
    {
        if (string.IsNullOrWhiteSpace(forceId)) return null;

        foreach (var p in AllProfiles)
        {
            if (!string.Equals(p.Id, forceId, StringComparison.OrdinalIgnoreCase)) continue;

            var raw = (JsonObject)p.Raw.DeepClone();
            raw["verified"] = false;
            raw["writes_allowed"] = false;

            // 界面上要能一眼看出"这不是本机真实机型"，否则截图会骗人
            // ⚠️ 不许裸 GetValue<string>()：档案是用户手改的 json，`name` 写成数字/对象时
            //    会抛 InvalidOperationException，--device/CC_DEVICE 下每次启动都闪退。
            var shownName = raw["name"] is JsonValue nv && nv.TryGetValue<string>(out var nm)
                            && nm.Length > 0 ? nm : p.Id;
            raw["name"] = shownName + "（强制预览）";
            if (raw["notes"] is not JsonArray notes)
                raw["notes"] = notes = new JsonArray();
            notes.Add("调试用：--device 强制预览，写入口已关闭");
            return raw;
        }
        return null;
    }
}
