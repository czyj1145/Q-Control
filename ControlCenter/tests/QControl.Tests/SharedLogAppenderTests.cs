using System.Text.RegularExpressions;
using QControl.Core.Logs;

namespace QControl.Tests;

/// <summary>
/// 多进程共写一份日志的追加器。
///
/// 为什么值得测：2026-11-08 的运行日志里出现过乱序行和半截行
/// （<c>19:16:26</c> 排在 <c>19:16:17</c> 前、<c>ve] 已接管风扇…</c>）——
/// 提权跳板进程与正式实例各开一个 FileStream，各自的文件指针停在旧位置，
/// 后写者把先写者刚落的字节盖掉了。日志是开机排障唯一的证据，覆盖比丢行更糟。
/// 这里用**两个独立实例**模拟两个进程（同进程内两个句柄同样各有自己的指针）。
/// </summary>
public class SharedLogAppenderTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public SharedLogAppenderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qcontrol-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "cc-2026-11-08.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Line(string who, int i)
        => $"{who}-{i:D4}-" + new string('x', 40);

    /// <summary>
    /// 日志正被写句柄开着，读方必须声明 ReadWrite|Delete 共享才打得开
    /// （产品里的 FileLog.Tail / LogCapture.ReadRange 也是这么读的）。
    /// </summary>
    private string[] ReadLines()
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        while (sr.ReadLine() is { } l) lines.Add(l);
        return lines.ToArray();
    }

    [Fact]
    public void Two_independent_writers_never_overwrite_each_other()
    {
        using var a = new SharedLogAppender(_path);
        using var b = new SharedLogAppender(_path);      // 第二个"进程"：另开句柄，指针独立

        Parallel.Invoke(
            () => { for (var i = 0; i < 250; i++) Assert.True(a.WriteLine(Line("A", i))); },
            () => { for (var i = 0; i < 250; i++) Assert.True(b.WriteLine(Line("B", i))); });

        var lines = ReadLines();
        Assert.Equal(500, lines.Length);                 // 一行都没被盖掉、也没多出半截行
        Assert.All(lines, l => Assert.Matches(new Regex(@"^[AB]-\d{4}-x{40}$"), l));
        Assert.Equal(250, lines.Count(l => l.StartsWith("A-", StringComparison.Ordinal)));
        Assert.Equal(250, lines.Count(l => l.StartsWith("B-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Writer_opened_earlier_keeps_lines_appended_after_the_other_one_opened()
    {
        using var a = new SharedLogAppender(_path);
        for (var i = 0; i < 10; i++) a.WriteLine(Line("A", i));

        using var b = new SharedLogAppender(_path);     // 此刻 b 的指针停在 a 写完的位置
        for (var i = 10; i < 20; i++) a.WriteLine(Line("A", i));   // a 又追加了 10 行
        b.WriteLine(Line("B", 0));                     // b 若从旧位置写，会盖掉 a 那 10 行

        var lines = ReadLines();
        Assert.Equal(21, lines.Length);
        Assert.Equal(20, lines.Count(l => l.StartsWith("A-", StringComparison.Ordinal)));
        Assert.Equal(Line("B", 0), lines[20]);           // 顺序也在：b 追加在真末尾
    }

    [Fact]
    public void Length_reflects_bytes_written_by_the_other_writer()
    {
        using var a = new SharedLogAppender(_path);
        using var b = new SharedLogAppender(_path);

        var before = b.Length;
        a.WriteLine(Line("A", 0));

        Assert.True(b.Length > before);      // 8MB 轮转判据不能只信自己的缓存
        Assert.Equal(b.Length, a.Length);
    }

    [Fact]
    public void Reopened_writer_appends_instead_of_truncating()
    {
        using (var a = new SharedLogAppender(_path))
        {
            a.WriteLine(Line("A", 0));
        }
        using var b = new SharedLogAppender(_path);
        b.WriteLine(Line("B", 0));

        Assert.Equal(new[] { Line("A", 0), Line("B", 0) }, ReadLines());
    }

    [Fact]
    public void Disposed_writer_reports_failure_instead_of_throwing()
    {
        var a = new SharedLogAppender(_path);
        a.Dispose();

        Assert.False(a.WriteLine(Line("A", 0)));
    }

    [Fact]
    public void 日志被别的进程删掉之后还能继续写()
    {
        // 另一个进程执行「清理日志」或轮转（Dispose + Move + 新建）之后，本进程的句柄
        // 还指着旧文件：不核对路径的话，后续每一行都写进"已无目录项"的文件，
        // 界面上看得见的那份日志里一行都没有。这里每 16 行核对一次。
        using var a = new SharedLogAppender(_path);
        Assert.True(a.WriteLine(Line("A", 0)));

        File.Delete(_path);
        for (var i = 0; i < 17; i++) Assert.True(a.WriteLine(Line("A", i + 1)));

        Assert.True(File.Exists(_path));
        Assert.Contains(Line("A", 17), ReadLines());
    }
}
