using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QControl.Core.Data;

/// <summary>
/// 嵌入资源里的数据文件（`Data/*.json`）。
///
/// 这些 json 是从早先那份实现的字面量**一次抽出**后冻结的，不是手抄的 ——
/// 手抄 200 行默认值必然出错。
/// 现在这里就是默认值的真源：要改默认值直接改 json 并重编译。
/// </summary>
public static class EmbeddedData
{
    private const string Prefix = "ccdata/";

    private static readonly Assembly Asm = typeof(EmbeddedData).Assembly;
    private static readonly Lazy<Dictionary<string, string>> Index = new(Build);

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var full in Asm.GetManifestResourceNames())
        {
            // 资源名由 csproj 的 LogicalName 决定：ccdata/devices/quanta-nl5.json
            // （见 Core.csproj 里那段注释 —— 不显式命名的话文件名里的点会被误当目录分隔）
            if (!full.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            map[full[Prefix.Length..].Replace('\\', '/')] = full;
        }
        return map;
    }

    /// <summary>读原始文本。名字用相对路径，如 `limits.json` / `devices/quanta-nl5.json`。</summary>
    public static string ReadText(string relativePath)
    {
        var key = relativePath.Replace('\\', '/');
        if (!Index.Value.TryGetValue(key, out var full))
        {
            throw new FileNotFoundException(
                $"嵌入数据缺失：{relativePath}。已嵌入 [{string.Join(", ", Index.Value.Keys)}]。"
                + "检查 Core.csproj 的 EmbeddedResource / LogicalName 配置。");
        }
        using var s = Asm.GetManifestResourceStream(full)
            ?? throw new FileNotFoundException($"资源流打不开：{full}");
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static JsonNode ReadNode(string relativePath)
        => JsonNode.Parse(ReadText(relativePath))
           ?? throw new InvalidDataException($"{relativePath} 解析为 null");

    public static JsonObject ReadObject(string relativePath)
        => ReadNode(relativePath) as JsonObject
           ?? throw new InvalidDataException($"{relativePath} 根不是对象");

    /// <summary>列出某个前缀下的所有文件（用于发现 devices/*.json）。</summary>
    public static IEnumerable<string> List(string prefix)
    {
        var p = prefix.Replace('\\', '/').TrimEnd('/') + "/";
        return Index.Value.Keys.Where(k => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                               .OrderBy(k => k, StringComparer.Ordinal);
    }

    /// <summary>对外序列化选项：不转义中文（原样输出中文，不转义）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}
