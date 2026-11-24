using QControl.Core.Startup;

namespace QControl.Tests;

/// <summary>
/// 「这份 exe 是不是本程序的另一份副本」的判据测试。
///
/// 为什么值得单测：这条判据一旦放宽，程序就会去改用户的开机入口（把自启任务
/// 从别的程序抢过来）—— 后果是"开机起了个不相干的东西"。所以三个条件
/// （同名 / 不同目录 / 目录里有 QControl.dll）各自都要证明"少一条就不成立"。
/// </summary>
public sealed class CopyIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
                                                 "qcontrol-copyid-" + Guid.NewGuid().ToString("N"));

    public CopyIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    /// <summary>造一份"看起来像本程序"的布局：`<目录>\QControl.exe` + 可选 `QControl.dll`。</summary>
    private string Layout(string dirName, bool withDll = true)
    {
        var dir = Path.Combine(_root, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, CopyIdentity.MainExeName), "stub");
        if (withDll) File.WriteAllText(Path.Combine(dir, CopyIdentity.CoreAssemblyName), "stub");
        return Path.Combine(dir, CopyIdentity.MainExeName);
    }

    [Fact]
    public void SameName_DifferentDir_WithAssembly_IsCopy()
    {
        var install = Layout("ProgramFiles");
        var portable = Layout("Downloads");
        Assert.True(CopyIdentity.IsSameProductCopy(portable, install));
        Assert.True(CopyIdentity.IsSameProductCopy(install, portable));   // 对称
    }

    [Fact]
    public void SameDirectory_IsNotCopy()
    {
        var a = Layout("one");
        Assert.False(CopyIdentity.IsSameProductCopy(a, a));
        // 同一目录换个写法也算"自己"
        Assert.False(CopyIdentity.IsSameProductCopy(
            Path.Combine(_root, "one", ".", CopyIdentity.MainExeName), a));
    }

    [Fact]
    public void ForeignDirWithoutAssembly_IsNotCopy()
    {
        var mine = Layout("mine");
        var other = Layout("someone-else", withDll: false);   // 只有同名 exe，没有 QControl.dll
        Assert.False(CopyIdentity.IsSameProductCopy(other, mine));
    }

    [Fact]
    public void DifferentExeName_IsNotCopy()
    {
        var mine = Layout("mine");
        var dir = Path.Combine(_root, "tool");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, CopyIdentity.CoreAssemblyName), "stub");
        var other = Path.Combine(dir, "SomeOtherTool.exe");
        File.WriteAllText(other, "stub");

        Assert.False(CopyIdentity.IsSameProductCopy(other, mine));
    }

    [Fact]
    public void MineMustAlsoBeMainExe()
    {
        var other = Layout("other");
        var mine = Path.Combine(_root, "x", "not-main.exe");
        Assert.False(CopyIdentity.IsSameProductCopy(other, mine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInput_IsNotCopy(string? other)
    {
        Assert.False(CopyIdentity.IsSameProductCopy(other, Path.Combine(_root, "QControl.exe")));
    }

    [Fact]
    public void InjectedProbe_IsUsedInsteadOfDisk()
    {
        // 不落盘也能判定：给 exists 注入"dll 在"
        var mine = Path.Combine(_root, "a", CopyIdentity.MainExeName);
        var other = Path.Combine(_root, "b", CopyIdentity.MainExeName);
        Assert.True(CopyIdentity.IsSameProductCopy(other, mine, _ => true));
        Assert.False(CopyIdentity.IsSameProductCopy(other, mine, _ => false));
    }

    [Fact]
    public void SamePath_IgnoresTrailingSlashAndCase()
    {
        var a = Path.Combine(_root, "Dir", CopyIdentity.MainExeName);
        Assert.True(CopyIdentity.SamePath(a, a.ToUpperInvariant() + "\\"));
        Assert.True(CopyIdentity.SamePath(a, Path.Combine(_root, "Dir", ".", CopyIdentity.MainExeName)));
        Assert.False(CopyIdentity.SamePath(a, Path.Combine(_root, "Other", CopyIdentity.MainExeName)));
        Assert.False(CopyIdentity.SamePath(null, a));
        Assert.False(CopyIdentity.SamePath(a, ""));
    }
}
