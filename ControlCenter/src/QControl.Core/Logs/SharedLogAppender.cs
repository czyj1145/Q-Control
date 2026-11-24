using System.Security.Cryptography;
using System.Text;

namespace QControl.Core.Logs;

/// <summary>
/// 多个进程**同时**往同一个日志文件追加一行时用的写入器。
///
/// 为什么不能直接 <c>new FileStream(path, FileMode.Append, ...)</c> 各写各的：
/// 每个进程拿到的文件指针是**自己那一瞬间**的末尾，之后别人追加了多少它并不知道。
/// 于是 B 从旧位置接着写，就把 A 刚写的那几行盖掉 —— 2026-11-08 的运行日志里出现过
/// 乱序（<c>19:16:26</c> 排在 <c>19:16:17</c> 前面）和半截行
/// （<c>ve] 已接管风扇…</c>）：提权跳板进程与正式实例重叠的那十几秒正是这种局面。
/// 日志是开机排障唯一的证据，行被覆盖等于"显示错的"，比少一行更糟。
///
/// 两条措施缺一不可：
///   · 每次写前 <c>Seek(0, End)</c> —— 补上别人刚追加的字节，指针不再落在旧位置；
///   · 命名 Mutex 把「Seek → 写 → Flush」包成跨进程临界区 —— 光 Seek 不够，
///     两个进程可能同时 Seek 到同一个末尾，再各自写入仍会互相覆盖。
///
/// ⚠️ 锁名按文件全路径取哈希：同一目录同一份日志的进程才会互相等，
///    不同日志（<c>--log</c> 指到别处 / 临时调试目录）互不干扰。
///    <c>Local\</c> 是会话作用域 —— 自启任务与桌面进程属于同一用户会话，够了；
///    <c>Global\</c> 要 SeCreateGlobalPrivilege，非管理员实例建不出来。
/// </summary>
public sealed class SharedLogAppender : IDisposable
{
    private FileStream _fs;
    private readonly Mutex? _gate;
    private bool _disposed;
    private int _writes;

    /// <summary>
    /// 等跨进程锁的上限。**不能无限等**：对端（提权跳板 / 绿色版副本）持锁后挂住的话，
    /// 这里会把采样线程一起冻住 —— 采样一停，`Temps` 10 秒后变"未知"、曲线一个字节都不写、
    /// 卡转自救也卡在同一线程上。日志可以少一行，控制线程不能停（2026-11-23 审查）。
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(2);

    public SharedLogAppender(string path)
    {
        FilePath = System.IO.Path.GetFullPath(path);

        // 带 Delete 共享：外部（资源管理器 / 清理按钮）能在我们写的时候删掉或改名，
        // 这一条不能丢 —— 否则「清理日志」会连当天那份都删不掉。
        _fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                             FileShare.ReadWrite | FileShare.Delete);
        _gate = TryCreateGate(FilePath);
    }

    public string FilePath { get; }

    /// <summary>磁盘上这份日志此刻的真实大小（含别的进程刚写的字节）。</summary>
    public long Length => _fs.Length;

    /// <summary>追加一行（自带换行）。false = 写坏了，调用方该停用这个写入器、如实报原因。</summary>
    public bool WriteLine(string line)
    {
        if (_disposed) return false;      // 已释放：如实报失败，调用方该换/停用这个写入器

        var held = false;
        try
        {
            if (_gate is not null)
            {
                try
                {
                    held = _gate.WaitOne(GateTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个写者被强杀：OS 已经把所有权交给本线程，照常写并归还即可，
                    // 不 Release 的话后面每次 WaitOne 都会一直抛 abandoned。
                    held = true;
                }
                catch
                {
                    held = false;      // 锁建不起来/被拒绝：退化成只靠 Seek，不为此丢日志
                }

                // 没拿到锁就**别写**：不持锁的 Seek+Write 正是这个类要消灭的互相覆盖。
                // 少一行日志，换采样线程不被冻住（见 GateTimeout 注释）。
                if (!held) return true;
            }

            // 路径可能已经被**别的进程**轮转/清理掉了（它们 Dispose + Move + 新建），
            // 而我们的句柄还指着旧文件 —— 之后每一行都写进"已无目录项"的文件，
            // 界面上看得见的那份日志里一行都没有，`Length` 上限也失效。
            // 每 16 行核对一次，不对就重开（本进程这一侧等价于也跟着换了一份）。
            if (++_writes % 16 == 0 && PathReplaced()) Reopen();

            _fs.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            _fs.Write(bytes, 0, bytes.Length);
            _fs.Flush();                  // 开机阶段随时可能被强杀，每行都要真落盘
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ObjectDisposedException) { return false; }
        finally
        {
            if (held)
            {
                try { _gate!.ReleaseMutex(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _fs.Dispose(); } catch { }
        try { _gate?.Dispose(); } catch { }
    }

    /// <summary>路径还指着我这个句柄吗（被删除 / 轮转成新的小文件 = 不是了）。</summary>
    private bool PathReplaced()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists) return true;               // 被「清理日志」删掉了
            return info.Length < _fs.Length;             // 轮转/清空：路径指向的是新的小文件
        }
        catch
        {
            return false;                                // 查不动就照旧写，别把日志弄停
        }
    }

    private void Reopen()
    {
        try { _fs.Dispose(); } catch { }
        _fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                             FileShare.ReadWrite | FileShare.Delete);
    }

    private static Mutex? TryCreateGate(string path)
    {
        try
        {
            var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];
            return new Mutex(false, "Local\\QControl-Log-" + hash);
        }
        catch
        {
            return null;      // 没有命名对象权限：仍写日志，只是少了跨进程互斥
        }
    }
}
