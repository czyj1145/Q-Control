using System.Text.Json.Nodes;

namespace QControl.Tests;

/// <summary>
/// 黄金向量的加载与比较工具。
///
/// 向量由**初版旧版**算出后冻结（生成器已于 2026-10-24 随旧版删除），
/// 经测试项目 csproj 的 <c>golden/</c> 链接复制到输出目录。它们是验收基准，不再重新生成。
/// </summary>
internal static class Golden
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "golden");

    public static JsonNode Load(string name)
    {
        var path = Path.Combine(Dir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"黄金向量缺失：{path}。检查测试 csproj 的 golden/ 链接是否还在。");
        return JsonNode.Parse(File.ReadAllText(path))
               ?? throw new InvalidDataException($"{name} 解析为 null");
    }

    /// <summary>
    /// 返回第一个差异的路径描述，null = 完全相等。深度比较：对象比键集合、
    /// 数组比顺序、数值按 double 比（`2` 与 `2.0` 算相等）。
    /// </summary>
    /// <remarks>
    /// ⚠️ null 有两种形态：C# 的 <c>JsonNode</c> 空引用，和 JSON 的 <c>JsonValueKind.Null</c>。
    ///    `new JsonObject { ["x"] = null }` 存的是前者，从文本 parse 出来的是后者。
    ///   只看 ValueKind 会把它们判成不等 —— 第一次跑测试就栽在这里，
    ///   而 ToJsonString() 两边打印出来是一模一样的 "null"，极难查。
    /// </remarks>
    public static string? FirstDiff(JsonNode? a, JsonNode? b, string path)
    {
        var aNull = a is null || a.GetValueKind() == System.Text.Json.JsonValueKind.Null;
        var bNull = b is null || b.GetValueKind() == System.Text.Json.JsonValueKind.Null;
        if (aNull && bNull) return null;
        if (aNull || bNull) return $"{path}: null 与非 null（{Describe(a)} vs {Describe(b)}）";

        var ka = a!.GetValueKind();
        var kb = b!.GetValueKind();
        if (ka != kb) return $"{path}: 类型不同 {ka} vs {kb}";

        switch (ka)
        {
            case System.Text.Json.JsonValueKind.Object:
            {
                var oa = a.AsObject();
                var ob = b.AsObject();
                foreach (var (k, v) in oa)
                {
                    if (!ob.TryGetPropertyValue(k, out var other))
                        return $"{path}/{k}: C# 有、旧版没有";
                    var d = FirstDiff(v, other, path + "/" + k);
                    if (d is not null) return d;
                }
                foreach (var k in ob.Select(kv => kv.Key))
                    if (!oa.ContainsKey(k))
                        return $"{path}/{k}: 旧版有、C# 没有";
                return null;
            }
            case System.Text.Json.JsonValueKind.Array:
            {
                var aa = a.AsArray();
                var ab = b.AsArray();
                if (aa.Count != ab.Count)
                    return $"{path}: 数组长度 {aa.Count} vs {ab.Count}";
                for (var i = 0; i < aa.Count; i++)
                {
                    var d = FirstDiff(aa[i], ab[i], $"{path}[{i}]");
                    if (d is not null) return d;
                }
                return null;
            }
            case System.Text.Json.JsonValueKind.Number:
                return AsDouble(a) == AsDouble(b)
                    ? null
                    : $"{path}: 数值 {AsDouble(a)} vs {AsDouble(b)}";
            case System.Text.Json.JsonValueKind.String:
                var sa = a.GetValue<string>();
                var sb = b.GetValue<string>();
                return sa == sb ? null : $"{path}: 字符串 \"{sa}\" vs \"{sb}\"";
            default:
                return null;   // True / False 走到这里且类型已比对过
        }
    }

    /// <summary>
    /// 数值节点一律按 double 取，不论背后是 Int32 / Int64 / Double / decimal。
    /// </summary>
    /// <remarks>
    /// ⚠️ 不能用 <c>JsonValue.TryGetValue&lt;double&gt;()</c>：它**不做跨类型转换** ——
    ///    `JsonValue.Create(2)` 底层是 Int32，取 double 会返回 false（不是抛，是 false），
    ///    于是拿到 NaN，比较结果是"不相等"但打印出来两边都写着 2。
    ///    走 ToJsonString 再 parse 是唯一稳的路子（Number 节点的文本一定可解析）。
    /// </remarks>
    private static double AsDouble(JsonNode n)
        => double.TryParse(n.ToJsonString(), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : double.NaN;

    /// <summary>差异描述用：把节点压成一行短文本。</summary>
    public static string Describe(JsonNode? n)
    {
        if (n is null) return "null";
        var s = n.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return s.Length <= 200 ? s : s[..200] + "…";
    }
}
