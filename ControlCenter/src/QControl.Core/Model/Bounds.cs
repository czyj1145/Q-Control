using System.Text.Json.Nodes;
using QControl.Core.Data;

namespace QControl.Core.Model;

/// <summary>一条数值边界。<c>Desc</c> 只用于报错/文档，不参与计算。</summary>
public sealed record Limit(double Lo, double Hi, string Desc);

/// <summary>数值越界，或者根本不是数值。</summary>
public sealed class OutOfBoundsException(string message) : Exception(message);

/// <summary>
/// 数值边界表 —— **所有写操作的安全入口**。
///
/// 语义：
/// <code>
///   if key not in LIMITS: return value          # ← C# 这里收紧，见下
///   lo, hi, _desc = LIMITS[key]
///   v = float(value)                            # 不是数值 ⇒ OutOfBounds
///   return int(round(max(lo, min(hi, v))))       # 银行家舍入
/// </code>
///
/// 两处刻意的差异：
/// 1. **未知 key 直接抛 <see cref="KeyNotFoundException"/>**。早先实现对未知 key
///    原样返回，是因为它的弱类型允许；C# 强类型下"原样返回"等于静默跳过边界检查 ——
///    一个拼错的 key 会让风扇跑满而无人察觉。宁可炸。
/// 2. `round()` 用 <see cref="Math.Round(double, MidpointRounding)"/> 的默认
///    `ToEven`，也就是银行家舍入（2.5 → 2、3.5 → 4）。
///    别改成 `AwayFromZero` —— 那会让 0.5 档的背光/转速与早先实现差 1。
/// </summary>
public static class Bounds
{
    private static readonly Lazy<Dictionary<string, Limit>> Table = new(Load);

    private static Dictionary<string, Limit> Load()
    {
        var root = EmbeddedData.ReadObject("limits.json");
        var map = new Dictionary<string, Limit>(StringComparer.Ordinal);
        foreach (var (key, node) in root)
        {
            if (node is not JsonObject o) continue;
            map[key] = new Limit(
                // 走 TryToDouble 而不是 GetValue<double>()：同 Bounds.TryNodeToDouble 的注释，
                // 数值节点的装箱类型不由我们决定，跨类型取值会抛。
                TryToDouble(o["lo"], out var lo) ? lo : 0,
                TryToDouble(o["hi"], out var hi) ? hi : 0,
                o["desc"]?.GetValue<string>() ?? "");
        }
        return map;
    }

    /// <summary>全部边界（只读视图）。</summary>
    public static IReadOnlyDictionary<string, Limit> All => Table.Value;

    public static bool TryGet(string key, out Limit limit) => Table.Value.TryGetValue(key, out limit!);

    /// <summary>把数值钳到 <paramref name="key"/> 的边界内，返回整数。</summary>
    /// <exception cref="KeyNotFoundException">key 不在边界表里（拼写错误必须暴露）。</exception>
    /// <exception cref="OutOfBoundsException">value 不是有效数值（NaN / 无穷）。</exception>
    public static int Clamp(string key, double value)
        => (int)Math.Round(ClampDouble(key, value), MidpointRounding.ToEven);

    /// <summary>
    /// 同上，但**不取整**。给 `min_interval`（秒，允许 2.5）/ `hysteresis`（°C，允许 0.5）
    /// 这类本来就是小数的边界用 —— 用 <see cref="Clamp"/> 会把 0.5 °C 的迟滞抹成 0。
    /// <para>
    /// 为什么必须有它：2026-11-12 审查发现风扇引擎把 `hysteresis` / `min_interval`
    /// 直接从配置读出来用，**完全没过边界表**（`limits.json` 里这两个键明明是有的）。
    /// 手改 model.json 写个 `hysteresis: 20` 就能让风扇首轮之后再不改速、
    /// 写个 `min_interval: 0` 就能每轮刷一次 EC。
    /// </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">key 不在边界表里（拼写错误必须暴露）。</exception>
    /// <exception cref="OutOfBoundsException">value 不是有效数值（NaN / 无穷）。</exception>
    public static double ClampDouble(string key, double value)
    {
        if (!Table.Value.TryGetValue(key, out var lim))
            throw new KeyNotFoundException(
                $"未知的边界键 '{key}'。已知：{string.Join(", ", Table.Value.Keys.Order())}");

        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new OutOfBoundsException($"{key} 不是有效数值：{value}");

        return Math.Max(lim.Lo, Math.Min(lim.Hi, value));
    }

    /// <summary>
    /// 宽松版：字符串也能当数字用（`"2"` 与 `2` 等价）。
    ///
    /// 为什么需要它：请求体来自 JSON，`{"level": "2"}` 和 `{"level": 2}` 都得能
    /// 用得上（`"2"` 也能当 2）。界面传字符串的地方是真实存在的，C# 不能直接
    /// 要求 double。
    /// </summary>
    public static int ClampLoose(string key, object? value)
    {
        if (!TryToDouble(value, out var v))
            throw new OutOfBoundsException($"{key} 不是数值：{value ?? "null"}");
        return Clamp(key, v);
    }

    /// <summary>`float(value)` 的等价物：数字 / 数字字符串 / 布尔（True=1）都能转。</summary>
    public static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case null: result = 0; return false;
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case short s: result = s; return true;
            case byte b: result = b; return true;
            case decimal m: result = (double)m; return true;
            case bool bo: result = bo ? 1 : 0; return true;     // true 当 1 用
            case string str:
                return double.TryParse(str.Trim(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out result);
            case JsonNode n:
                return TryNodeToDouble(n, out result);
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>
    /// JsonNode → double。**任何数值节点都必须转得过去，一个异常都不许抛。**
    ///
    /// ⚠️ 2026-11-15 真机踩过：这里原来写的是 `n.GetValue<double>()`，而
    /// `JsonValue` 的数值类型是"装箱时是什么就是什么"—— 从文件读进来的节点包着
    /// `JsonElement`（`GetValue&lt;double&gt;` 能转），但**代码里 `JsonValue.Create(0)`
    /// 建出来的包着 `Int32`**，跨类型取值直接抛
    /// `InvalidOperationException: A value of type 'System.Int32' cannot be converted to
    /// a 'System.Double'`。
    ///
    /// 后果不是"少一个数"，而是一整条链路塌掉：一键适配用
    /// `Model.Set(["perf","modes",key,"apmd"], JsonValue.Create(apmd))` 把 APMD 以 int
    /// 写进**内存里的** model（见 MachineAdopt.SeedPresets），之后每一拍采样
    /// `EcHal.PerfModeKey` 都在这里抛 ⇒ `status.perf` 永远是 null ⇒
    /// 性能页那个「系统模式」一直停在"读取中…"，点切换也报那句看不懂的类型错误，
    /// **重启才恢复**（重启后节点是从 model.json 重新解析的 JsonElement）。
    /// 用户报的"有时候系统模式半天都读不到"就是这个。
    /// </summary>
    private static bool TryNodeToDouble(JsonNode n, out double result)
    {
        switch (n.GetValueKind())
        {
            case System.Text.Json.JsonValueKind.Number:
                if (n is JsonValue v)
                {
                    if (v.TryGetValue<double>(out var d)) { result = d; return true; }
                    if (v.TryGetValue<long>(out var l)) { result = l; return true; }
                    if (v.TryGetValue<ulong>(out var ul)) { result = ul; return true; }
                    if (v.TryGetValue<decimal>(out var m)) { result = (double)m; return true; }
                }
                // 兜底：按 JSON 文本解析（数字节点不管装箱成什么类型，文本都是规范数字）
                return double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out result);

            case System.Text.Json.JsonValueKind.String:
                return double.TryParse(
                    n is JsonValue sv && sv.TryGetValue<string>(out var s) ? s.Trim() : n.ToJsonString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out result);

            case System.Text.Json.JsonValueKind.True: result = 1; return true;
            case System.Text.Json.JsonValueKind.False: result = 0; return true;
            default: result = 0; return false;
        }
    }
}
