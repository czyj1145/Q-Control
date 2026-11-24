using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QControl.Setup;

/// <summary>
/// 安装日志：`%TEMP%\QControl.Setup.log`。
///
/// 为什么要有：安装器的界面是"点一下就没有了"，装失败时用户手里什么都没有。
/// 有了这个文件，装机现场的每一步（停实例、解包、授 ACL、写注册表）都有痕迹，
/// 支持的时候让人把这一份发过来就够了。
///
/// 同时镜像到控制台 —— 但本程序是 WinExe，只有被 `cmd` 直接调用、或者自检模式
/// 时才会 AttachConsole 到父进程；双击启动时 Console 输出无声丢弃，不留副作用。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    private static bool _console;

    public static string? Path => _path;

    public static void Init(string suffix = "")
    {
        try
        {
            _console = AttachParentConsole();
            var dir = System.IO.Path.GetTempPath();
            _path = System.IO.Path.Combine(dir, "QControl.Setup" + (suffix.Length > 0 ? "." + suffix : "") + ".log");
            // 每次运行截断：日志只服务"这一次装/卸"，旧的不该混进来
            File.WriteAllText(_path, "", Encoding.UTF8);
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(Exception ex)
        => Write("ERROR", ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            try { if (_path is not null) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* 日志写不进去不该影响安装本身 */ }
            if (_console)
            {
                try { Console.WriteLine(line); } catch { }
            }
            else
            {
                Debug.WriteLine(line);
            }
        }
    }

    // ---------------------------------------------------------------- 控制台

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    private static bool AttachParentConsole()
    {
        // WinExe 默认没有控制台。父进程如果是 cmd/PowerShell，就借它的用一下，
        // 这样 `/selftest`、`/extract` 这类命令行用法能看到输出。
        if (!AttachConsole(AttachParentProcess)) return false;
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            return true;
        }
        catch
        {
            FreeConsole();
            return false;
        }
    }
}
