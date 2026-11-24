using System.Text.Json.Nodes;
using QControl.Core.Data;

namespace QControl.Core.Model;

/// <summary>
/// EC 命令分级表 —— 只读 / 危险 / 需二次确认。
///
/// 三张表是静态数据，改它等于改安全边界 —— 唯一入口是 <see cref="Risk"/>。
/// 判断"这条命令危险不危险"只有一个入口（<see cref="Risk"/>），
/// 分散到各处写 `if (cmd == 0x0300)` 就是下一个事故。
/// </summary>
public static class CmdTable
{
    private static readonly Lazy<Tables> Loaded = new(Load);

    private sealed record Tables(
        HashSet<ushort> ReadOnly,
        Dictionary<ushort, string> Dangerous,
        HashSet<ushort> ConfirmRequired);

    private static Tables Load()
    {
        var root = EmbeddedData.ReadObject("cmd-table.json");

        var readOnly = new HashSet<ushort>();
        if (root["read_only"] is JsonArray ro)
            foreach (var n in ro)
                if (Parse(n) is { } c) readOnly.Add(c);

        var dangerous = new Dictionary<ushort, string>();
        if (root["dangerous"] is JsonObject dg)
            foreach (var (k, v) in dg)
                if (Parse(k) is { } c) dangerous[c] = v?.GetValue<string>() ?? "";

        var confirm = new HashSet<ushort>();
        if (root["confirm_required"] is JsonArray cf)
            foreach (var n in cf)
                if (Parse(n) is { } c) confirm.Add(c);

        return new Tables(readOnly, dangerous, confirm);
    }

    private static ushort? Parse(JsonNode? node)
        => Parse(node?.GetValue<string>());

    /// <summary>解析 "0x0300" / "768" / "0X0300" 三种写法。</summary>
    public static ushort? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
                                   System.Globalization.CultureInfo.InvariantCulture, out var h)
                ? h : null;
        return ushort.TryParse(text, out var d) ? d : null;
    }

    public static IReadOnlySet<ushort> ReadOnly => Loaded.Value.ReadOnly;
    public static IReadOnlyDictionary<ushort, string> Dangerous => Loaded.Value.Dangerous;
    public static IReadOnlySet<ushort> ConfirmRequired => Loaded.Value.ConfirmRequired;

    public static bool IsReadOnly(ushort cmd) => Loaded.Value.ReadOnly.Contains(cmd);

    /// <summary>风险描述；只读命令返回 null（与早先实现 `cmd_risk` 一致）。</summary>
    public static string? Risk(ushort cmd)
        => Loaded.Value.Dangerous.TryGetValue(cmd, out var d) ? d : null;

    /// <summary>是否需要先 <c>arm()</c> 二次确认。</summary>
    public static bool NeedsConfirm(ushort cmd) => Loaded.Value.ConfirmRequired.Contains(cmd);

    /// <summary>给日志/报错用的十六进制写法。</summary>
    public static string Hex(ushort cmd) => $"0x{cmd:X4}";
}
