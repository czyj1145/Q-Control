using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QControl.Core.Devices;

/// <summary>命中说明：命中规则块 + 具体命中了哪些字段（报障时能说清"凭什么认成这个机型"）。</summary>
public sealed record MatchResult(IReadOnlyList<string> Fields, JsonObject Block);

/// <summary>
/// 机型匹配 —— 纯逻辑，只吃一个 `ident` 字典，不碰注册表。
///
/// 注册表读取在 `Hal` 层（P3），这里只负责"给定身份字段，哪份档案最合适"。
/// 这样整套匹配规则可以在 Linux 上、无真机、无管理员下跑单元测试。
///
/// 匹配语义（按顺序逐条试，命中即停）：
/// <code>
///   match.any 里**任一** block 命中即算命中；      ← 块之间是"或"
///   block 内**所有**字段都命中才算；               ← 字段之间是"与"
///   同一字段的多条正则**任一**匹配即可；           ← 字段内是"或"
///   正则一律 IgnoreCase + search（非全匹配）。
/// </code>
/// </summary>
public static class DeviceMatcher
{
    /// <summary>取一个身份字段的可比较值列表。</summary>
    /// <remarks>
    /// 对应 `_field_values`：
    /// 对象 → 值为真的键（`vendor_registry` 就是这种「项目号 → 是否存在」的形状）；
    /// 数组 → 各元素；null → 空；标量 → 单个字符串。
    /// </remarks>
    public static List<string> FieldValues(JsonObject ident, string name)
    {
        var node = ident[name];
        switch (node)
        {
            case null:
                return [];
            case JsonObject o:
                var keys = new List<string>();
                foreach (var (k, v) in o)
                    if (v is JsonValue vv && vv.TryGetValue<bool>(out var b) && b)
                        keys.Add(k);
                return keys;
            case JsonArray a:
                var items = new List<string>();
                foreach (var x in a)
                    if (Scalar(x) is { } s) items.Add(s);
                return items;
            default:
                return Scalar(node) is { } one ? [one] : [];
        }
    }

    private static string? Scalar(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => v.TryGetValue<string>(out var s) ? s : null,
            System.Text.Json.JsonValueKind.Number => v.ToJsonString(),
            System.Text.Json.JsonValueKind.True => "True",     // 规则里写的就是大写 True，别改成 true
            System.Text.Json.JsonValueKind.False => "False",
            _ => null,
        };
    }

    /// <summary>一个 block 内**所有**字段都命中才返回命中字段名列表，否则 null。</summary>
    public static List<string>? MatchBlock(JsonObject block, JsonObject ident)
    {
        var hit = new List<string>();
        foreach (var (field, patsNode) in block)
        {
            var values = FieldValues(ident, field);
            if (values.Count == 0) return null;

            var ok = false;
            if (patsNode is JsonArray pats)
            {
                foreach (var p in pats)
                {
                    if (p is not JsonValue pv || !pv.TryGetValue<string>(out var pattern)) continue;
                    Regex rx;
                    try { rx = new Regex(pattern, RegexOptions.IgnoreCase); }
                    catch (ArgumentException) { continue; }      // 坏正则跳过，不炸
                    if (values.Any(values => rx.IsMatch(values))) { ok = true; break; }
                }
            }
            else if (patsNode is JsonValue single && single.TryGetValue<string>(out var one))
            {
                // 容错：规则写成字符串而不是数组时也认（不能因此崩掉整次识别）
                try { if (values.Any(v => Regex.IsMatch(v, one, RegexOptions.IgnoreCase))) ok = true; }
                catch (ArgumentException) { }
            }

            if (!ok) return null;
            hit.Add(field);
        }
        return hit.Count > 0 ? hit : null;
    }

    /// <summary>`match.any` 里任一 block 命中即算命中。</summary>
    public static MatchResult? Match(DeviceProfile profile, JsonObject ident)
    {
        if (profile.Raw["match"] is not JsonObject match) return null;
        if (match["any"] is not JsonArray blocks) return null;

        foreach (var b in blocks)
        {
            if (b is not JsonObject block) continue;
            if (MatchBlock(block, ident) is { } fields)
                return new MatchResult(fields, block);
        }
        return null;
    }

    /// <summary>
    /// 规则里 `vendor_registry` 的正则 → 候选项目号（去掉 `^$` 锚点和反斜杠）。
    /// 给探针/诊断用：告诉用户"我去注册表里找过哪几个键"。
    /// </summary>
    public static IReadOnlyList<string> CodesFromRules(DeviceProfile profile)
    {
        var outList = new List<string>();
        if (profile.Raw["match"] is not JsonObject match) return outList;
        if (match["any"] is not JsonArray blocks) return outList;

        foreach (var b in blocks)
        {
            if (b is not JsonObject block) continue;
            if (block["vendor_registry"] is not JsonArray pats) continue;
            foreach (var p in pats)
            {
                if (p is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
                outList.Add(s.Trim('^', '$').Replace("\\", ""));
            }
        }
        return outList;
    }
}
