using QControl.Core.Logs;

namespace QControl.Tests;

/// <summary>
/// 采集包（`logs\adopt-*.json` / `.txt`）的枚举与清理。
///
/// 为什么值得测：这同样是**删除**路径，而且躲在一堆日志文件中间 ——
/// 规则放松一点（比如"删 logs 下所有 .json"）就会把用户自己搁那儿的东西一起带走；
/// 收紧一点（只认 `.json`）就会出现"点清理之后目录里还剩一半 .txt"。
/// 2026-11-08 用户报的"清理清不掉采集档案"就是这一类文件从来没被认过。
/// </summary>
public class AdoptPackStoreTests : IDisposable
{
    private readonly string _root;

    public AdoptPackStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qcontrol-packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string name, int bytes = 100)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void Scan_counts_json_and_txt_pairs_of_adopt_prefix()
    {
        Write("adopt-G16-Pro-20261108-150117.json", 200);
        Write("adopt-G16-Pro-20261108-150117.txt", 100);
        Write("adopt-G16-Pro-20261108-150118.TXT", 50);   // 扩展名大小写不敏感

        var stats = AdoptPackStore.Scan(_root);

        Assert.Equal(3, stats.Files);
        Assert.Equal(350, stats.Bytes);
        Assert.Equal(
            new[]
            {
                "adopt-G16-Pro-20261108-150117.json",
                "adopt-G16-Pro-20261108-150117.txt",
                "adopt-G16-Pro-20261108-150118.TXT",
            },
            stats.Names);
    }

    [Fact]
    public void Scan_ignores_logs_and_other_files_in_the_same_dir()
    {
        Write("cc-2026-11-08.log", 500);
        Write("cc-2026-11-08.old.log", 400);
        Write("capture-20261108-1400.log", 300);
        Write("notes.txt", 20);                    // 别的 .txt
        Write("adopt-notes.json", 0);              // 空文件也算（名字对就算）
        Write("my-adopt-x.json", 10);              // 前缀不匹配

        var stats = AdoptPackStore.Scan(_root);

        Assert.Equal(1, stats.Files);
        Assert.Equal(new[] { "adopt-notes.json" }, stats.Names);
    }

    [Fact]
    public void Scan_is_empty_when_dir_missing()
    {
        var stats = AdoptPackStore.Scan(Path.Combine(_root, "nope"));
        Assert.Equal(0, stats.Files);
        Assert.Empty(stats.Names);
    }

    [Fact]
    public void Delete_removes_only_adopt_pairs_and_keeps_everything_else()
    {
        var keepLog = Write("cc-2026-11-08.log", 10);
        var keepCap = Write("capture-20261108-1400.log", 10);
        var keepNotes = Write("readme.txt", 10);
        var keepMine = Write("quanta-nl5.json", 10);       // 别的 json 不许动
        Write("adopt-A.json", 100);
        Write("adopt-A.txt", 50);

        var (deleted, freed, failed) = AdoptPackStore.Delete(_root);

        Assert.Equal(2, deleted);
        Assert.Equal(150, freed);
        Assert.Equal(0, failed);
        Assert.False(File.Exists(Path.Combine(_root, "adopt-A.json")));
        Assert.False(File.Exists(Path.Combine(_root, "adopt-A.txt")));
        Assert.True(File.Exists(keepLog));
        Assert.True(File.Exists(keepCap));
        Assert.True(File.Exists(keepNotes));
        Assert.True(File.Exists(keepMine));
    }

    [Fact]
    public void Delete_does_not_recurse_into_subdirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        var deep = Path.Combine(_root, "sub", "adopt-deep.json");
        File.WriteAllBytes(deep, new byte[10]);
        Write("adopt-top.json", 10);

        var (deleted, _, _) = AdoptPackStore.Delete(_root);

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(deep));
    }

    [Fact]
    public void Delete_on_missing_dir_is_a_noop()
    {
        var (deleted, freed, failed) = AdoptPackStore.Delete(Path.Combine(_root, "nope"));
        Assert.Equal(0, deleted);
        Assert.Equal(0, freed);
        Assert.Equal(0, failed);
    }

    [Theory]
    [InlineData("adopt-G16-Pro-20261031-230720.json", true)]
    [InlineData("adopt-G16-Pro-20261031-230720.txt", true)]
    [InlineData("ADOPT-x.TXT", true)]
    [InlineData("adopt-.json", true)]                 // 空机型：名字对就算，宁可多认
    [InlineData("my-adopt-x.json", false)]
    [InlineData("adopt-x.log", false)]
    [InlineData("adopt-x.json.bak", false)]
    [InlineData("cc-2026-11-08.log", false)]
    public void IsPack_follows_prefix_and_extension_only(string name, bool expected)
        => Assert.Equal(expected, AdoptPackStore.IsPack(name));
}
