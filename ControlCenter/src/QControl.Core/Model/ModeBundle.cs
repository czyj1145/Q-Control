using System.Text.Json.Nodes;

namespace QControl.Core.Model;

/// <summary>一次 <see cref="ModeBundle.Migrate"/> 挪动了多少东西（0 = 没动，不必落盘）。</summary>
public readonly record struct ModeMigration(int Modes, int FanCurves, int Lights)
{
    public bool Changed => Modes > 0 || FanCurves > 0 || Lights > 0;
}

/// <summary>
/// 「档位 = 一整套状态」的唯一读写口 —— `presets.perf.modes[档]`。
///
/// 为什么要有这一层（2026-11-17 重构，照 G-Helper 的 `AppConfig.GetMode(...)` 学的）：
/// 以前「随档生效」的东西散在三处 —— `perf.modes[档].apmd/plan`、`fan.mode_curves_cpu[档]`、
/// `backlight.mode_profiles[档]`。每加一项（灯效、屏亮度、功耗墙…）就要再造一套 key + 一套迁移，
/// 而且"切档到底换了哪些东西"在代码里找不齐。现在统一成：
/// <code>
/// "perf": { "modes": {
///   "office": { "apmd": 2, "dat2": 2, "plan": "办公",
///               "fan":   { "cpu": [ {t,d} … ], "gpu": [ … ] },
///               "light": { "effect": 1, "level": 2, "zones": [ {addr,rgb} … ] } },
///   "game": { … }, "beast": { … } } }
/// </code>
///
/// 两条不变的分工（别混）：
///   · `perf.modes[档]` = **档案**（这一档应该是什么样）；
///   · `fan.curve_cpu/curve_gpu`、`backlight.effect/zones` = **当前生效**（EC 里现在是什么）。
///     切档 = 把档案抄进"当前生效"，再由各自的引擎下发 —— 与 G-Helper 把 bundle 应用到设备同构。
///
/// ⚠️ 风扇跟档走由 `fan.bind_mode` 决定；灯效按档重放是常驻的（2026-11-18 撤掉 follow_mode 开关，
///    它与第四套 custom 的作用冲突）。档案只描述"是什么"，不描述"要不要用"。
/// </summary>
public static class ModeBundle
{
    /// <summary>可绑定的档位键（与 `presets.perf.modes` 的键、`caps.perf_modes` 一致）。</summary>
    public static readonly string[] Keys = ["office", "game", "beast"];

    /// <summary>
    /// 可以存**灯效档案**的键 = 三个系统档 + 原厂的第四套 `custom`。
    /// ⚠️ `custom` 只在这里出现：它不是系统性能档，**风扇曲线 / 电源计划 / APMD 都不许认它**。
    /// </summary>
    public static readonly string[] LightKeys = ["office", "game", "beast", "custom"];

    /// <summary>档位键归一：认不出的（null / 空 / 手改的野值）一律 null。</summary>
    public static bool IsKey(string? key) => Array.IndexOf(Keys, key) >= 0;

    /// <summary>灯效档案键归一（比 <see cref="IsKey"/> 多认一个 `custom`）。</summary>
    public static bool IsLightKey(string? key) => Array.IndexOf(LightKeys, key) >= 0;

    /// <summary>档位短名（界面 chip / 日志 / toast 共用一处）。</summary>
    public static string Label(string? key) => key switch
    {
        "office" => "办公",
        "game" => "游戏",
        "beast" => "狂暴",
        _ => key ?? "未知",
    };

    /// <summary>四套灯效档案的名字（`custom` 是原厂那套「自定义」）。</summary>
    public static string LightLabel(string? key) => key == "custom" ? "自定义" : Label(key);

    /// <summary>`presets.perf.modes`（结构不对 / 传 null 返回 null —— 不替用户改坏的结构）。</summary>
    public static JsonObject? All(JsonObject? presets)
        => presets?["perf"] is JsonObject perf && perf["modes"] is JsonObject modes ? modes : null;

    /// <summary>某一档的档案节点（不创建）。</summary>
    public static JsonObject? Mode(JsonObject? presets, string? key)
        => IsKey(key) && All(presets)?[key!] is JsonObject mode ? mode : null;

    /// <summary>
    /// 某一档下的一个子块（`fan` / `light` / …），不创建。没有就返回 null ——
    /// 调用方据此判断"这一档还没有这项设置"，而不是拿别的档凑（同 BacklightModes 的规矩②）。
    /// </summary>
    public static JsonObject? Block(JsonObject? presets, string? key, string name)
        => Mode(presets, key)?[name] as JsonObject;

    /// <summary>
    /// 灯效专用：读某一档（含 `custom`）的 `light` 块。
    /// 走独立的 `IsLightKey`，别把 `custom` 加进 <see cref="Keys"/> —— 那会让风扇多出一档。
    /// </summary>
    public static JsonObject? LightBlock(JsonObject? presets, string? key)
        => IsLightKey(key) && All(presets)?[key!] is JsonObject mode ? mode["light"] as JsonObject : null;

    /// <summary>
    /// 取（必要时创建）某一档的档案节点。
    ///
    /// **只补缺，不改坏**：`perf` / `modes` / `档位` 不存在 ⇒ 建出来；存在但不是对象
    /// （手改坏了）⇒ 返回 null，一个字节都不动 —— 拿新对象盖掉用户的东西比报错更糟。
    /// </summary>
    public static JsonObject? EnsureMode(JsonObject presets, string key)
        => EnsureMode(presets, key, light: false);

    private static JsonObject? EnsureMode(JsonObject presets, string key, bool light)
    {
        if (!(light ? IsLightKey(key) : IsKey(key))) return null;

        if (presets["perf"] is null) presets["perf"] = new JsonObject();
        if (presets["perf"] is not JsonObject perf) return null;

        if (perf["modes"] is null) perf["modes"] = new JsonObject();
        if (perf["modes"] is not JsonObject modes) return null;

        if (modes[key] is null) modes[key] = new JsonObject();
        return modes[key] as JsonObject;
    }

    /// <summary>取（必要时创建）某一档下的子块。</summary>
    public static JsonObject? EnsureBlock(JsonObject presets, string key, string name)
    {
        if (EnsureMode(presets, key) is not { } mode) return null;
        if (mode[name] is not JsonObject block)
        {
            block = new JsonObject();
            mode[name] = block;
        }
        return block;
    }

    /// <summary>灯效专用：取（必要时创建）某一档的 `light` 块（含 `custom`）。</summary>
    public static JsonObject? EnsureLightBlock(JsonObject presets, string key)
    {
        if (EnsureMode(presets, key, light: true) is not { } mode) return null;
        if (mode["light"] is not JsonObject block)
        {
            block = new JsonObject();
            mode["light"] = block;
        }
        return block;
    }

    // ================================================================ 风扇曲线

    /// <summary>风扇槽位名（`fan` 子块里的键）。SYS 只有三风扇机型才有内容。</summary>
    public const string FanCpu = "cpu";
    public const string FanGpu = "gpu";
    public const string FanSys = "sys";

    /// <summary>某档某一路的曲线（JSON 数组；没有就 null）。</summary>
    public static JsonNode? FanCurve(JsonObject? presets, string? key, string slot)
        => Block(presets, key, "fan")?[slot];

    /// <summary>写某档的一条曲线（`curve` 为 null 表示删掉这一路）。</summary>
    public static void SetFanCurve(JsonObject presets, string key, string slot, JsonNode? curve)
    {
        if (EnsureBlock(presets, key, "fan") is not { } block) return;
        if (curve is null) block.Remove(slot);
        else block[slot] = curve.DeepClone();
    }

    // ================================================================ 迁移

    /// <summary>
    /// 把散落的老位置挪进 `perf.modes[档]`，**挪完就删老键**（不双写：两处各存一份迟早会漂）。
    ///
    /// 迁移的三条判据：
    ///   · 老位置有值 ⇒ 一定是用户数据（出厂默认里已经没有这些键了）⇒ **覆盖**档案里的同名项；
    ///   · 老位置没有 ⇒ 档案原样不动（所以重复执行是幂等的）；
    ///   · `perf` 结构坏到建不出档位（手改成标量）⇒ **一个字节都不删**，宁可留着老键。
    ///
    /// 老位置 → 新位置：
    ///   `fan.mode_curves_cpu[档]` / `fan.mode_curves_gpu[档]` → `perf.modes[档].fan.cpu/gpu`
    ///   `fan.mode_curves[档]`（更早的单曲线，两路共用）→ 同上两路
    ///   `backlight.mode_profiles[档]` → `perf.modes[档].light`
    /// </summary>
    public static ModeMigration Migrate(JsonObject presets)
    {
        var fan = presets["fan"] as JsonObject;
        var backlight = presets["backlight"] as JsonObject;

        var legacyCpu = fan?["mode_curves_cpu"] as JsonObject;
        var legacyGpu = fan?["mode_curves_gpu"] as JsonObject;
        var legacySys = fan?["mode_curves_sys"] as JsonObject;
        var legacyBoth = fan?["mode_curves"] as JsonObject;
        var legacyLight = backlight?["mode_profiles"] as JsonObject;

        if (legacyCpu is null && legacyGpu is null && legacySys is null
            && legacyBoth is null && legacyLight is null)
            return default;

        int modes = 0, curves = 0, lights = 0;
        foreach (var key in Keys)
        {
            if (EnsureMode(presets, key) is null) continue;

            // 更早的单曲线 `mode_curves[档]` 是"一条管两路"，所以 cpu/gpu 都退回它
            (string Slot, JsonNode? Curve)[] pairs =
            [
                (FanCpu, legacyCpu?[key] ?? legacyBoth?[key]),
                (FanGpu, legacyGpu?[key] ?? legacyBoth?[key]),
                (FanSys, legacySys?[key]),
            ];

            var wrote = 0;
            foreach (var (slot, curve) in pairs)
            {
                if (curve is null) continue;
                SetFanCurve(presets, key, slot, curve);
                curves++;
                wrote++;
            }
            if (wrote > 0) modes++;

            if (legacyLight?[key] is JsonObject light && EnsureBlock(presets, key, "light") is { } target)
            {
                // 整块替换：档案里可能残留上一个版本的键（比如 level 改名前的写法）
                foreach (var name in target.Select(kv => kv.Key).ToList()) target.Remove(name);
                foreach (var (name, value) in light.ToList()) target[name] = value?.DeepClone();
                if (wrote == 0) modes++;
                lights++;
            }
        }

        // 只有真的搬成了才删老键（`modes == 0` = perf 结构坏了，宁可什么都别动）
        if (modes > 0)
        {
            fan?.Remove("mode_curves");
            fan?.Remove("mode_curves_cpu");
            fan?.Remove("mode_curves_gpu");
            fan?.Remove("mode_curves_sys");
            backlight?.Remove("mode_profiles");
        }

        return new ModeMigration(modes, curves, lights);
    }
}
