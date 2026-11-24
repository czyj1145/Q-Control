using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QControl.Hal.Sys;

/// <summary>
/// 跑一次命令行工具，拿回文本。**仅限系统自带工具**（powercfg / sc / shutdown …）。
///
/// 为什么不用 `Process.StandardOutput.ReadToEnd()`：
///   它按 `Console.OutputEncoding` 解码，而控制台编码取决于启动我们的人用的是什么
///   （chcp 65001 就是 UTF-8）。`powercfg /list`、`sc query` 往管道里写的是**系统
///   ANSI/OEM 代码页**（简中 = 936/GBK），两边对不上就是一堆乱码 ——
///   而且乱码只出现在中文计划名上，看着像"读到了奇怪的盘符"。
///   所以这里**直接读字节流**，再用 MultiByteToWideChar 按 936 解。
///   早先实现是 `out.stdout.decode("gbk", errors="replace")`，同一个口径。
///
/// 顺带：`CreateNoWindow` + 不重定向 stdin，避免无窗口/托盘态弹黑框。
/// </summary>
public static class Shell
{
    // public：App 层的重启计划（shutdown.exe）也要用它。
    // 只有"跑一次系统命令并拿回文本"这一件事，没有状态，暴露出去没有副作用。
    /// <summary>系统代码页。简中 Windows 是 936；换语言系统时会跟着变。</summary>
    private static readonly uint AnsiCp = GetACP();

    /// <summary>执行并返回 (退出码, 标准输出, 标准错误)。启动失败时退出码为 null。</summary>
    public static (int? Code, string Out, string Err) Run(
        string exe, string args, int timeoutMs = 6000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,      // 立刻关掉，别让子进程等 stdin
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? p = null;
        try
        {
            p = Process.Start(psi);
            if (p is null) return (null, "", "进程没起来");

            p.StandardInput.Close();           // 有些工具会等 stdin 关闭才输出

            // ⚠️ 必须用 BaseStream 读**原始字节**：StandardOutput 是 StreamReader，
            //    它一读就按错误的编码把字节转成字符串了，再也救不回来。
            var outTask = Task.Run(() => ReadAllBytes(p.StandardOutput.BaseStream));
            var errTask = Task.Run(() => ReadAllBytes(p.StandardError.BaseStream));

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return (null, "", $"超时（{timeoutMs} ms）");
            }

            // WaitForExit(int) 不保证管道读干净；再等一次无参版本。
            p.WaitForExit();

            var bytes = outTask.GetAwaiter().GetResult();
            var errBytes = errTask.GetAwaiter().GetResult();
            return (p.ExitCode, Decode(bytes).Trim(), Decode(errBytes).Trim());
        }
        catch (Exception e)
        {
            return (null, "", e.Message);
        }
        finally
        {
            p?.Dispose();
        }
    }

    /// <summary>只要标准输出，失败就回空串（与早先实现 `_run()` 的默认行为一致）。</summary>
    public static string RunText(string exe, string args, int timeoutMs = 6000)
        => Run(exe, args, timeoutMs).Out;

    private static byte[] ReadAllBytes(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>按系统代码页解码；解不出来的字节用替换符，绝不抛。</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        var n = MultiByteToWideChar(AnsiCp, 0, bytes, bytes.Length, null, 0);
        if (n <= 0) return Encoding.UTF8.GetString(bytes);   // 兜底：至少给点东西
        var sb = new StringBuilder(n);
        MultiByteToWideChar(AnsiCp, 0, bytes, bytes.Length, sb, n);
        return sb.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint flags, byte[] lpMultiByteStr, int cbMultiByte,
        StringBuilder? lpWideCharStr, int cchWideChar);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
