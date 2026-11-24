using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace QControl.Boot;

/// <summary>
/// 把控制权交给主程序：找 exe、拼命令行、起进程。
///
/// 只有一个入口 `app\QControl.exe`（WinExe）：托盘图标一起，全程没有黑框。
///
/// 参数**原样透传**：引导器自己认的开关都以 `--boot-` 打头并被提前摘掉了，
/// 剩下的（`--elevate`、`--port` 之类）统统是主程序的。
/// </summary>
internal static class Launcher
{
    public static bool TryLaunch(AppLayout layout, IReadOnlyList<string> args, out string error)
    {
        if (!layout.Found)
        {
            error = "找不到主程序（QControl.exe）。\n" +
                    "请把整个 zip 重新解压到一个新目录，不要只挑其中几个文件拷出来。";
            return false;
        }

        var exe = layout.MainExe!;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = layout.AppDir,
            Arguments = JoinArguments(args),
        };

        try
        {
            Process.Start(psi);
            Log.Info("已启动主程序：" + exe + (args.Count > 0 ? " " + psi.Arguments : ""));
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("启动主程序失败：" + exe, ex);
            error = "启动主程序失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 拼命令行。net48 的 <see cref="ProcessStartInfo"/> **没有** ArgumentList
    /// （那是 .NET Core 才有的），所以参数得自己按 Windows 的规则加引号 ——
    /// 路径里有空格时不加引号会直接当成两个参数，主程序会去读一个不存在的开关。
    /// </summary>
    private static string JoinArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return "";

        var sb = new StringBuilder();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Quote(args[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 按 `CommandLineToArgvW` 的反解析规则加引号：反斜杠只在「后面紧跟引号」或
    /// 「位于结尾」时才需要翻倍。照抄那份规则而不是简单 `\"` 转义，
    /// 是因为参数里真可能出现 `C:\a b\` 这种以反斜杠结尾的路径。
    /// </summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\n', '\v' }) < 0) return arg;

        var sb = new StringBuilder();
        sb.Append('"');

        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (i == arg.Length)
            {
                // 结尾的反斜杠会去转义我们刚加的收尾引号 ⇒ 全部翻倍
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
