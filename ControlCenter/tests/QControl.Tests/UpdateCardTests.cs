using QControl.Core.Updates;

namespace QControl.Tests;

/// <summary>
/// 「检查更新」卡片的判定。原先这些都散在 <c>AboutView.RenderNewer</c> 里，
/// 界面项目不进测试工程 ⇒ "拿旧包去装"这种错只能靠人看出来。
/// </summary>
public class UpdateCardTests
{
    // ---- SameRelease：必须比 tag，且忽略大小写 ----

    [Fact]
    public void 同一个tag算同一版()
        => Assert.True(UpdateCard.SameRelease("v0.2.0", "v0.2.0"));

    [Fact]
    public void tag大小写不同也算同一版()
        => Assert.True(UpdateCard.SameRelease("V0.2.0", "v0.2.0"));

    [Fact]
    public void 版本不同就是旧包()
        => Assert.False(UpdateCard.SameRelease("v0.2.0", "v0.3.0"));

    [Theory]
    [InlineData(null, "v0.2.0")]
    [InlineData("v0.2.0", null)]
    [InlineData("v0.2.0", "")]
    [InlineData("v0.2.0", "   ")]
    public void 缺一边就不认(string? dlVersion, string? latestTag)
        => Assert.False(UpdateCard.SameRelease(dlVersion, latestTag));

    // ---- Stage：子状态 ----

    [Theory]
    [InlineData("downloading", UpdateCardStage.Downloading)]
    [InlineData("cancelling", UpdateCardStage.Cancelling)]
    [InlineData("verifying", UpdateCardStage.Verifying)]
    [InlineData("ready", UpdateCardStage.Ready)]
    [InlineData("installing", UpdateCardStage.Installing)]
    [InlineData("error", UpdateCardStage.DownloadFailed)]
    [InlineData("idle", UpdateCardStage.Newer)]
    [InlineData(null, UpdateCardStage.Newer)]
    [InlineData("某个以后才加的状态", UpdateCardStage.Newer)]
    public void 同一版的下载状态映射(string? state, UpdateCardStage want)
        => Assert.Equal(want, UpdateCard.Stage(sameRelease: true, state));

    [Fact]
    public void 旧包不管什么状态都回到还没下()
    {
        Assert.Equal(UpdateCardStage.Newer, UpdateCard.Stage(false, "downloading"));
        Assert.Equal(UpdateCardStage.Newer, UpdateCard.Stage(false, "ready"));
    }

    // ---- PickInstallerIndex：优先 setup，其次第一个 exe ----

    [Fact]
    public void 线上那三个附件选中setup包()
    {
        var names = new List<string?>
        {
            "QControl-0.1.0-setup-win-x64.exe",
            "QControl-0.1.0-portable-win-x64.zip",
            "SHA256.txt",
        };
        Assert.Equal(0, UpdateCard.PickInstallerIndex(names));
    }

    [Fact]
    public void setup不在第一位也优先选它()
    {
        var names = new List<string?> { "portable.exe", "QControl-0.2.0-Setup.exe" };
        Assert.Equal(1, UpdateCard.PickInstallerIndex(names));
    }

    [Fact]
    public void 没有setup就取第一个exe()
    {
        var names = new List<string?> { "a.zip", "first.exe", "second.exe" };
        Assert.Equal(1, UpdateCard.PickInstallerIndex(names));
    }

    [Fact]
    public void 没有exe返回负一()
    {
        Assert.Equal(-1, UpdateCard.PickInstallerIndex(new List<string?> { "a.zip", "SHA256.txt" }));
        Assert.Equal(-1, UpdateCard.PickInstallerIndex(new List<string?>()));
    }

    [Fact]
    public void 空名字与大小写后缀都要认()
    {
        var names = new List<string?> { null, "", "SETUP.EXE" };
        Assert.Equal(2, UpdateCard.PickInstallerIndex(names));
    }

    [Fact]
    public void 文件名里带exe但不是exe后缀的不算()
    {
        var names = new List<string?> { "setup.exe.txt", "real.exe" };
        Assert.Equal(1, UpdateCard.PickInstallerIndex(names));
    }

    // ---- ReadyTip：悬停详情的拼法 ----

    [Fact]
    public void 四条信息齐全时按顺序各占一行()
    {
        var tip = UpdateCard.ReadyTip(
            target: @"C:\Program Files\Q-Control",
            portable: false,
            file: "QControl-0.2.0-setup-win-x64.exe",
            sha256: "abc123",
            tag: "v0.2.0",
            version: "0.2.0");

        var lines = tip.Split('\n');
        Assert.Equal(3, lines.Length);   // 目标 / 安装包 / 校验和（非便携版不追加提醒）
        Assert.StartsWith("安装到：", lines[0]);
        Assert.Equal("安装包：QControl-0.2.0-setup-win-x64.exe", lines[1]);
        Assert.Equal("SHA256：abc123", lines[2]);
    }

    [Fact]
    public void 便携版多一句提醒()
    {
        var tip = UpdateCard.ReadyTip(null, portable: true, null, null, "v0.2.0", "0.2.0");
        Assert.Contains("不是安装版", tip);
    }

    [Fact]
    public void 什么都没有时退回发布号_tag优先()
    {
        Assert.Equal("发布 v0.2.0", UpdateCard.ReadyTip(null, false, null, null, "v0.2.0", "0.2.0"));
        Assert.Equal("发布 0.2.0", UpdateCard.ReadyTip(null, false, null, null, null, "0.2.0"));
    }

    [Fact]
    public void 空字符串与null等价_都当没有()
    {
        Assert.Equal("发布 v0.2.0",
            UpdateCard.ReadyTip("", false, "", "", "v0.2.0", null));
    }
}
