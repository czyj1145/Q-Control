using System.Text.Json;

namespace QControl.Core.Updates;

/// <summary>更新源。两家 API 形状不同，但都归结成一份 <see cref="ReleaseInfo"/>。</summary>
public enum FeedKind
{
    /// <summary>api.github.com/repos/{owner}/{repo}/releases/latest</summary>
    GitHub,

    /// <summary>gitee.com/api/v5/repos/{owner}/{repo}/releases/latest</summary>
    Gitee,
}

/// <summary>发布附件（安装包 / 便携包）。第一版只用于显示体积，不参与判断。</summary>
public sealed record ReleaseAsset(string Name, long Size, string? Url);

/// <summary>一次发布的摘要 —— 界面需要的全部字段，不多一个。</summary>
public sealed record ReleaseInfo(
    string Tag,
    string? Name,
    string? Notes,
    string Url,
    string? PublishedAt,
    bool Prerelease,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>tag 去掉前导 v：`v0.2.0` → `0.2.0`（界面给用户看的是版本号，不是 tag）。</summary>
    public string Version => Tag.TrimStart('v', 'V');
}

/// <summary>
/// 把 GitHub / Gitee 的 release JSON 解析成 <see cref="ReleaseInfo"/>。
///
/// 纯函数、不联网 —— 于是"两家返回格式不同"这件最容易被网络问题掩盖的事，
/// 可以直接用两段 JSON 字面量单测掉（见 SemVerTests）。
///
/// ⚠️ 宽容但不猜：缺 tag_name 就当失败（一个没有版本号的 release 无法比较，
///    硬编成 0.0.0 会立刻变成"有新版"的误报）。附件缺失不算失败 —— 下载页仍然可用。
/// </summary>
public static class ReleaseFeed
{
    /// <summary>
    /// 解析一份 release JSON。
    /// </summary>
    /// <param name="kind">哪家的格式（决定字段回退顺序）。</param>
    /// <param name="json">响应体原文。</param>
    /// <param name="repoUrl">仓库主页（不带尾斜杠），用于上游没给 html_url 时自己拼发布页地址。</param>
    /// <param name="info">解析结果；失败时为 null。</param>
    /// <param name="error">失败原因（带上游原文，便于区分"没发布"和"被代理拦截"）。</param>
    public static bool TryParse(FeedKind kind, string json, string repoUrl,
                                out ReleaseInfo? info, out string? error)
    {
        info = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "更新源返回了空内容";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // 公司代理 / 运营商劫持最典型的表现：200 + 一段 HTML 登录页。
            // 文案要短：这句会出现在半幅卡的一行说明里，太长会在末尾甩出孤字。
            error = "返回的不是 JSON（可能被代理或防火墙拦截）";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "更新源返回的 JSON 不是对象";
                return false;
            }

            // 上游的拒绝理由（GitHub: {"message":"Not Found"}；Gitee: {"error":"..."}）
            var upstream = Str(root, "message") ?? Str(root, "error") ?? Str(root, "error_description");

            var tag = Str(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                error = string.IsNullOrWhiteSpace(upstream)
                    ? "更新源里没有版本号（tag_name 缺失）"
                    : $"更新源拒绝：{upstream}";
                return false;
            }
            tag = tag.Trim();

            // 发布页地址：GitHub 给 html_url；Gitee 不给，得自己拼
            var url = Str(root, "html_url");
            if (string.IsNullOrWhiteSpace(url))
                url = $"{repoUrl.TrimEnd('/')}/releases/tag/{tag}";

            var published = Str(root, "published_at") ?? Str(root, "created_at");

            var prerelease = Bool(root, "prerelease") ?? Bool(root, "is_prerelease") ?? false;

            info = new ReleaseInfo(
                Tag: tag,
                Name: Str(root, "name"),
                Notes: Str(root, "body") ?? Str(root, "description"),
                Url: url!,
                PublishedAt: published,
                Prerelease: prerelease,
                Assets: ReadAssets(root));
            return true;
        }
    }

    private static IReadOnlyList<ReleaseAsset> ReadAssets(JsonElement root)
    {
        // GitHub 与 Gitee 的附件都在 assets[]，元素键名也一致；老 Gitee 版本用 attach_files[]。
        // 少读一个附件不影响"有没有新版本"，所以这里不严格。
        foreach (var key in new[] { "assets", "attach_files" })
        {
            if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            var list = new List<ReleaseAsset>();
            foreach (var a in arr.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                var name = Str(a, "name") ?? Str(a, "filename");
                if (string.IsNullOrWhiteSpace(name)) continue;
                list.Add(new ReleaseAsset(
                    Name: name!,
                    Size: Long(a, "size") ?? 0,
                    Url: Str(a, "browser_download_url") ?? Str(a, "download_url") ?? Str(a, "url")));
            }
            if (list.Count > 0) return list;
        }
        return [];
    }

    private static string? Str(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static long? Long(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt64(out var n)
            ? n
            : null;
}
