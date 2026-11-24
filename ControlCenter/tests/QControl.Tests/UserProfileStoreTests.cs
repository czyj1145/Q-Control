using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 用户机型档案目录的枚举 / 清理。
///
/// 为什么值得测：这是**删除**路径。它多认一个文件名，用户少的就是一份采集结果；
/// 它少认一个，界面上就会一直挂着一个"清不掉的档案"。`devices\` 里还可能躺着
/// 用户自己放的东西，所以"只删 `*.json`、不递归"必须被测试钉住。
/// </summary>
public class UserProfileStoreTests : IDisposable
{
    private readonly string _root;

    public UserProfileStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qcontrol-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string ProfileDir => Path.Combine(_root, "devices");

    private string Write(string name, int bytes = 100)
    {
        Directory.CreateDirectory(ProfileDir);
        var path = Path.Combine(ProfileDir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Scan_counts_only_top_level_json()
    {
        Write("quanta-nl5.json", 200);
        Write("quanta-nl9.JSON", 300);          // 扩展名大小写不敏感
        Write("notes.txt", 50);
        Directory.CreateDirectory(Path.Combine(ProfileDir, "sub"));
        File.WriteAllBytes(Path.Combine(ProfileDir, "sub", "deep.json"), new byte[999]);  // 不递归

        var stats = UserProfileStore.Scan(ProfileDir);

        Assert.Equal(2, stats.Files);
        Assert.Equal(500, stats.Bytes);
        Assert.Equal(new[] { "quanta-nl5.json", "quanta-nl9.JSON" }, stats.Names);
    }

    [Fact]
    public void Scan_is_empty_when_dir_missing()
    {
        var stats = UserProfileStore.Scan(Path.Combine(_root, "nope"));
        Assert.Equal(0, stats.Files);
        Assert.Empty(stats.Names);
    }

    [Fact]
    public void Delete_removes_only_json_and_keeps_everything_else()
    {
        var keep1 = Write("readme.txt", 10);
        var keep2 = Path.Combine(ProfileDir, "keep.json.bak");
        File.WriteAllBytes(keep2, new byte[10]);
        Directory.CreateDirectory(Path.Combine(ProfileDir, "sub"));
        var keep3 = Path.Combine(ProfileDir, "sub", "deep.json");
        File.WriteAllBytes(keep3, new byte[10]);
        Write("a.json", 100);
        Write("b.json", 50);

        var (deleted, freed, failed) = UserProfileStore.Delete(ProfileDir);

        Assert.Equal(2, deleted);
        Assert.Equal(150, freed);
        Assert.Equal(0, failed);
        Assert.False(File.Exists(Path.Combine(ProfileDir, "a.json")));
        Assert.False(File.Exists(Path.Combine(ProfileDir, "b.json")));
        Assert.True(File.Exists(keep1));
        Assert.True(File.Exists(keep2));
        Assert.True(File.Exists(keep3));
    }

    [Fact]
    public void Delete_on_missing_dir_is_a_noop()
    {
        var (deleted, freed, failed) = UserProfileStore.Delete(Path.Combine(_root, "nope"));
        Assert.Equal(0, deleted);
        Assert.Equal(0, freed);
        Assert.Equal(0, failed);
    }

    [Fact]
    public void Source_tree_detected_by_solution_file_next_to_devices()
    {
        Write("quanta-nl5.json");
        // 仓库布局：ControlCenter\QControl.slnx + ControlCenter\devices\
        File.WriteAllText(Path.Combine(_root, "QControl.slnx"), "<Solution/>");

        Assert.True(UserProfileStore.LooksLikeSourceTree(ProfileDir));
    }

    [Fact]
    public void Source_tree_detected_by_git_dir_next_to_devices()
    {
        Write("quanta-nl5.json");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        Assert.True(UserProfileStore.LooksLikeSourceTree(ProfileDir));
    }

    [Fact]
    public void Install_layout_is_cleanable()
    {
        // 安装版：%ProgramData%\Q-Control\model.json + ProgramData\Q-Control\devices\
        Write("quanta-nl5.json");
        Assert.False(UserProfileStore.LooksLikeSourceTree(ProfileDir));
    }

    // ---- 采集写档案前的「能不能写」----
    // 清理路径早就拒绝动源码 devices\，写入路径却是 File.WriteAllText 直接覆盖：
    // 调试实例的 model.json 在工作区时，仓库里手改的 quanta-nl5.json 会被
    // verified=false 的采集草案盖掉（2026-11-07 踩过一次）。

    [Fact]
    public void CanWriteProfile_refuses_source_tree_devices()
    {
        Write("quanta-nl5.json");
        File.WriteAllText(Path.Combine(_root, "QControl.slnx"), "<Solution/>");
        var target = Path.Combine(ProfileDir, "quanta-nl5.json");

        var (allowed, why) = UserProfileStore.CanWriteProfile(ProfileDir, target);

        Assert.False(allowed);
        Assert.Contains("源码", why);
    }

    [Fact]
    public void CanWriteProfile_allows_a_plain_user_dir()
    {
        Directory.CreateDirectory(ProfileDir);
        var target = Path.Combine(ProfileDir, "quanta-nl5.json");

        var (allowed, why) = UserProfileStore.CanWriteProfile(ProfileDir, target);

        Assert.True(allowed);
        Assert.Null(why);
    }

    [Fact]
    public void CanWriteProfile_refuses_to_overwrite_a_verified_profile()
    {
        var target = Write("quanta-nl5.json");
        File.WriteAllText(target, "{\"id\":\"quanta-nl5\",\"verified\":true}");

        var (allowed, why) = UserProfileStore.CanWriteProfile(ProfileDir, target);

        Assert.False(allowed);
        Assert.Contains("已验证", why);
    }

    [Theory]
    [InlineData("{\"id\":\"x\",\"verified\":false}")]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("this is not json")]
    public void CanWriteProfile_allows_overwriting_a_draft_or_unreadable_file(string body)
    {
        var target = Write("quanta-nl5.json");
        File.WriteAllText(target, body);

        Assert.True(UserProfileStore.CanWriteProfile(ProfileDir, target).Allowed);
    }
}
