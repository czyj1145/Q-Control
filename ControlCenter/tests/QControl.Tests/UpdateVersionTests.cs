using QControl.Core.Updates;

namespace QControl.Tests;

/// <summary>
/// 版本比较与 release 解析 —— 检查更新的全部"判断逻辑"都在这里，不联网。
///
/// 这些用例防的是**误报**：把测试版当正式版推、把没发布的仓库当新版本、
/// 把代理返回的 HTML 登录页当 JSON。误报的代价是用户点进一个空发布页，
/// 之后就再也不信这个提示了。
/// </summary>
public sealed class UpdateVersionTests
{
    // ---------------------------------------------------------------- SemVer

    [Theory]
    [InlineData("0.1.0", 0, 1, 0, null)]
    [InlineData("v0.2.0", 0, 2, 0, null)]
    [InlineData("V1.2.3", 1, 2, 3, null)]
    [InlineData("0.2", 0, 2, 0, null)]
    [InlineData("3", 3, 0, 0, null)]
    [InlineData("1.0.0-beta.2", 1, 0, 0, "beta.2")]
    [InlineData("1.0.0-rc-1", 1, 0, 0, "rc-1")]
    // 构建元数据不参与比较（那是源码链接，不是版本语义）
    [InlineData("0.1.0+617ceb3", 0, 1, 0, null)]
    public void 解析_认得各种写法(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(pre, v.Pre);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("0.1.0.0")]          // 四段是 Windows 文件版本，不是语义版本
    [InlineData("1.0.0-beta..1")]    // 空段非法
    [InlineData("-1.0.0")]
    public void 解析_不合法就老实说不知道(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0")]
    [InlineData("0.2.0", "0.2.0-beta")]
    [InlineData("0.2.0-beta", "0.2.0-alpha")]
    [InlineData("0.2.0-beta.2", "0.2.0-beta")]
    [InlineData("0.2.0-rc.1", "0.2.0-beta.2")]
    [InlineData("0.2.1", "0.2.0")]
    [InlineData("1.0.0", "0.99.99")]
    public void 比较_新的那个更大(string newer, string older)
    {
        Assert.True(SemVer.TryParse(newer, out var a));
        Assert.True(SemVer.TryParse(older, out var b));
        Assert.True(a.IsNewerThan(b), $"{newer} 应该比 {older} 新");
        Assert.False(b.IsNewerThan(a));
    }

    [Theory]
    [InlineData("0.2", "0.2.0")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1.0+abc123", "0.1.0")]
    public void 比较_写法不同但语义相同(string a, string b)
    {
        Assert.True(SemVer.TryParse(a, out var x));
        Assert.True(SemVer.TryParse(b, out var y));
        Assert.Equal(0, x.CompareTo(y));
        Assert.False(x.IsNewerThan(y));
    }

    // ---------------------------------------------------------------- release 解析

    private const string Repo = "https://github.com/czyj1145/Q-Control";

    /// <summary>GitHub 的真实形状（只留我们用到的键）。</summary>
    private const string GitHubJson = """
    {
      "tag_name": "v0.2.0",
      "name": "0.2.0",
      "body": "散热页重做\n修复曲线回弹",
      "html_url": "https://github.com/czyj1145/Q-Control/releases/tag/v0.2.0",
      "published_at": "2026-10-20T12:00:00Z",
      "prerelease": false,
      "draft": false,
      "assets": [
        { "name": "QControl-0.2.0-setup-win-x64.exe", "size": 131072000,
          "browser_download_url": "https://github.com/czyj1145/Q-Control/releases/download/v0.2.0/QControl-0.2.0-setup-win-x64.exe" }
      ]
    }
    """;

    [Fact]
    public void GitHub_能认出新版与附件()
    {
        Assert.True(ReleaseFeed.TryParse(FeedKind.GitHub, GitHubJson, Repo, out var info, out var err));
        Assert.Null(err);
        Assert.NotNull(info);
        Assert.Equal("v0.2.0", info!.Tag);
        Assert.Equal("0.2.0", info.Version);          // 界面上不该出现前导 v
        Assert.Equal("2026-10-20T12:00:00Z", info.PublishedAt);
        Assert.False(info.Prerelease);
        Assert.Single(info.Assets);
        Assert.Equal(131072000, info.Assets[0].Size);
        Assert.StartsWith("https://github.com/", info.Assets[0].Url);
        Assert.Equal("https://github.com/czyj1145/Q-Control/releases/tag/v0.2.0", info.Url);
    }

    /// <summary>Gitee 的形状：没有 html_url（要自己拼），时间字段叫 created_at。</summary>
    private const string GiteeJson = """
    {
      "id": 123456,
      "tag_name": "v0.2.0",
      "target_commitish": "main",
      "prerelease": false,
      "name": "0.2.0",
      "body": "散热页重做",
      "created_at": "2026-10-20T12:00:00+08:00"
    }
    """;

    [Fact]
    public void Gitee_没有html_url_时自己拼发布页()
    {
        Assert.True(ReleaseFeed.TryParse(FeedKind.Gitee, GiteeJson,
            "https://gitee.com/czyj1145/Q-Control", out var info, out _));
        Assert.NotNull(info);
        Assert.Equal("0.2.0", info!.Version);
        Assert.Equal("2026-10-20T12:00:00+08:00", info.PublishedAt);
        Assert.Equal("https://gitee.com/czyj1145/Q-Control/releases/tag/v0.2.0", info.Url);
        Assert.Empty(info.Assets);
    }

    [Fact]
    public void 没发布过_返回失败并带上游原话()
    {
        // GitHub 私有仓库匿名访问就是这个响应 —— 必须说清"没发布"，而不是"解析失败"
        Assert.False(ReleaseFeed.TryParse(FeedKind.GitHub, """{"message":"Not Found"}""",
            Repo, out var info, out var err));
        Assert.Null(info);
        Assert.Contains("Not Found", err);
    }

    [Fact]
    public void 代理返回的_HTML不被当成JSON()
    {
        Assert.False(ReleaseFeed.TryParse(FeedKind.GitHub,
            "<html><body>请先登录企业网关</body></html>", Repo, out _, out var err));
        Assert.Contains("不是 JSON", err);
    }

    [Fact]
    public void 缺tag_name_算失败而不是猜成0点0点0()
    {
        // 猜成 0.0.0 会立刻变成"有新版"的误报 —— 宁可不显示
        Assert.False(ReleaseFeed.TryParse(FeedKind.GitHub, """{"name":"某个版本"}""",
            Repo, out _, out var err));
        Assert.Contains("tag_name", err);
    }
}
