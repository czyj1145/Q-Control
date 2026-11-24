using System.Runtime.InteropServices;
using System.Text;

namespace QControl.App;

/// <summary>
/// 控制台"按需出现"—— 主程序已经是 **GUI 子系统**（`OutputType=WinExe`，见 csproj），
/// 所以双击 exe **不会**凭空弹一个黑框；需要看文字的时候（从 cmd / PowerShell / bash
/// 启动、或跑 `--selftest` 这类诊断）再把输出接回一个控制台。
///
/// 为什么要有这个文件（2026-10-29 用户拍板：「运行程序改为默认没有 cmd 窗口的启动」）：
/// 控制台子系统的 exe 只要被双击，Windows 就会给它配一个控制台窗口，那个窗口会**一直
/// 挂在桌面上**直到程序退出 —— 一个托盘常驻的硬件控制软件带着一个黑框显然不对。
/// 而子系统是编进 PE 的，运行时改不了，只能改成 WinExe + 这里补回文字输出。
///
/// 三种情况，优先级从高到低：
///   ① **std 句柄本来就有效**（管道 / 重定向 / 继承来的控制台）：直接绑上去。
///      从 cmd、PowerShell、Git Bash 起进程都走这条 —— `QControl.exe --selftest > log.txt`
///      这种被重定向的用法必须照常工作，否则打包脚本的体检（pack-release.ps1）就瞎了。
///   ② **父进程有控制台**（用户在 cmd 里敲 QControl.exe）：`AttachConsole` 借它的。
///      借来的控制台里 Ctrl+C 照样能送达（<c>Console.CancelKeyPress</c> 那条退出路径不受影响）。
///   ③ **命令行要文字输出**（`--selftest` / `--help` 之类，见 <see cref="WantsConsole"/>）：
///      自己 `AllocConsole` 开一个。双击起界面时不会走这条 ⇒ 桌面上永远是干净的。
///
/// ⚠️ 必须排在 <see cref="Logging.FileLog.Attach(string?)"/> **之前**调用：那个 tee 是
///    包在"当时的 <c>Console.Out</c>"外面的，先 tee 再 attach 的话，控制台拿到的是
///    一个包在空 writer 外面的壳，打字一个字都看不见。
/// </summary>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;

    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    /// <summary>`GetStdHandle` 的失败值（(IntPtr)(-1)）。句柄为 0 同样表示"没有"。</summary>
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    /// <summary>进程入口第一件事。没有任何副作用：拿不到控制台就静默继续（界面照开）。</summary>
    public static void Ensure(string[] argv)
    {
        try
        {
            // ① 已经有了可用输出句柄：不管它从哪来，直接用
            if (HasUsable(StdOutputHandle) || HasUsable(StdErrorHandle))
            {
                Bind();
                return;
            }

            // ② 父进程（cmd / PowerShell / 终端）自己有控制台：借来用
            if (AttachConsole(AttachParentProcess))
            {
                Bind();
                return;
            }

            // ③ 双击启动 + 命令行又要文字：开一个只为这次诊断存在的窗口
            if (WantsConsole(argv) && AllocConsole())
            {
                Bind();
                return;
            }
        }
        catch
        {
            // 控制台是**附加品**：拿不到也不该影响后端、托盘和界面
        }

        // 走到这里 = 真的没有控制台（典型：双击 exe、开机自启、快捷方式）。
        // 什么都不做 —— Console.Write* 会进 .NET 的空 writer，文件日志照常（见 FileLog）。
    }

    /// <summary>
    /// 这条命令行要不要一个可见的控制台窗口。
    ///
    /// 只列**纯文字**的用法：诊断自检、帮助。`--ui-shot` / `--tray-menu-shot` 这类
    /// 出图的不算（窗口闪一下没有意义），`--tray` / `--show` 更不算。
    /// </summary>
    private static bool WantsConsole(string[] argv)
        => argv.Any(a =>
            a is "--help" or "-h" or "-?"
            || a is "--selftest" or "--autostart-selftest" or "--gpu-oc-selftest"
            || a is "--import-pack"
            || a.StartsWith("--ui-", StringComparison.OrdinalIgnoreCase) && a.EndsWith("-selftest", StringComparison.OrdinalIgnoreCase));

    private static bool HasUsable(int which)
    {
        var h = GetStdHandle(which);
        return h != IntPtr.Zero && h != InvalidHandle;
    }

    /// <summary>
    /// 把 <c>Console.Out/Error</c> 绑到刚拿到的控制台（或原有句柄）上，并统一成 UTF-8。
    ///
    /// 为什么要重新 <c>SetOut</c>：`Console.Out` 是在**类型首次使用时**按当时那套句柄
    /// 定下来的；我们可能是在它之后才 attach 的，不重新绑就仍然往空气里写。
    /// </summary>
    private static void Bind()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            var stdout = Console.OpenStandardOutput();
            if (stdout != Stream.Null)
                Console.SetOut(new StreamWriter(stdout, utf8) { AutoFlush = true });
        }
        catch { /* 只读管道之类的怪情况：保持原样 */ }

        try
        {
            var stderr = Console.OpenStandardError();
            if (stderr != Stream.Null)
                Console.SetError(new StreamWriter(stderr, utf8) { AutoFlush = true });
        }
        catch { }

        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
    }

    /// <summary>
    /// 有没有一个真的控制台（不是重定向）。给需要"别往管道里刷屏"的地方用。
    /// </summary>
    public static bool IsRealConsole()
        => HasUsable(StdOutputHandle) && GetConsoleWindowSafe() != IntPtr.Zero;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static IntPtr GetConsoleWindowSafe()
    {
        try { return GetConsoleWindow(); } catch { return IntPtr.Zero; }
    }
}
