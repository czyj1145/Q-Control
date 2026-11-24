using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using QControl.Shared;

namespace QControl.Boot;

/// <summary>
/// 绿色版入口。三种走法：
///
///   ① **运行时已就绪**（绝大多数情况）：什么都不显示，直接把控制权交给主程序。
///      引导器在这一步必须是"看不见的"—— 多一个窗口就是多一次劝退。
///   ② **缺运行时**：弹一个小窗口，讲清楚缺什么、需要多大、装到哪，
///      给「自动下载并安装」和「打开官网下载页」两条路（见 Ui\BootWindow）。
///   ③ **主程序不在旁边**：弹一句人话（半拷贝状态，无处可修）。
///
/// 命令行开关（都以 `--boot-` 打头，避免和主程序的开关撞车；其余参数原样透传）：
///   --boot-selftest            自检：把目录/运行时探测结果写一份日志，不启动主程序
///   --boot-simulate-missing    假装没装运行时（验证提示窗口用，不真装）
///   --boot-ui-shot=&lt;前缀&gt;     把提示窗口的三种状态各渲一张 PNG（开发用）
/// </summary>
internal static class Program
{
    public const string ProductName = "Q-Control 控制中心";

    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitCancelled = 2;

    [STAThread]
    private static int Main(string[] argv)
    {
        var logPath = Log.Init();
        Log.Reset();
        Log.Info("QControl.Boot 启动 · 参数：" + string.Join(" ", argv));
        Log.Info("日志：" + logPath);

        var args = new List<string>(argv);
        var selftest = TakeFlag(args, "--boot-selftest");
        var simulateMissing = TakeFlag(args, "--boot-simulate-missing");
        var shotPrefix = TakeValue(args, "--boot-ui-shot");

        var layout = AppLayout.Detect();
        var installed = RuntimeInstaller.InstalledVersion();

        Log.Info($"绿色版根：{layout.RootDir}");
        Log.Info($"主程序目录：{layout.AppDir}（主程序：{layout.MainExe ?? "未找到"}）");
        Log.Info("运行时：" + (installed ?? "未安装"));

        if (selftest) return SelfTest(layout, installed);

        // 截图模式只渲染窗口，不检测、不启动（和安装器的 /ui-shot 一个思路：
        // 版式问题只能靠看图发现）
        if (shotPrefix is not null) return Shot(layout, args, shotPrefix);

        if (installed is not null && !simulateMissing)
        {
            if (Launcher.TryLaunch(layout, args, out var error)) return ExitOk;

            // 运行时没问题、主程序却在别的路上失败了（半拷贝、被杀软删掉……）：
            // 这时候弹一句比"双击没反应"强得多，但也仅此而已 —— 没有可自动修复的东西。
            Log.Error(error);
            MessageBox.Show(error, ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return ExitFailed;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Ui.BootWindow(layout, args, installed, simulateMissing);
        app.Run(window);
        return window.ExitCode;
    }

    // ---------------------------------------------------------------- 自检

    /// <summary>
    /// `--boot-selftest`：只验证"能不能认出这台机器的布局和运行时"，不启动主程序。
    /// 打包脚本用它证明 zip 里那套目录结构是对的（引导器找得到 app\ 里的主程序）。
    /// 返回 0 = 结构正常；1 = 找不到主程序。
    /// </summary>
    private static int SelfTest(AppLayout layout, string? installed)
    {
        string temp;
        try { temp = Path.GetTempPath(); }
        catch (Exception ex) { temp = "<异常：" + ex.Message + ">"; }

        var lines = new List<string>
        {
            $"{ProductName} · 绿色版引导器自检",
            $"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"绿色版根    : {layout.RootDir}",
            $"主程序目录  : {layout.AppDir}",
            $"主程序      : {layout.MainExe ?? "(未找到)"}",
            $"运行时      : {installed ?? "未安装（自检不要求安装）"}",
            // 这两个是"自动安装失败"的两大外因，自检里直接给结论，省得再翻注册表：
            // 半残留 = MSI 已登记但 %ProgramFiles%\dotnet 下的文件不见了（/install 会空转）
            $"半残留      : {(DotnetDesktopRuntime.IsHalfInstalled(null) ? "是（安装时会先走 /repair）" : "否")}",
            $"挂起重启    : {(DotnetDesktopRuntime.RebootPending ? "是（建议先重启一次）" : "否")}",
            $"程序目录    : {AppDomain.CurrentDomain.BaseDirectory}",
            $"工作目录    : {SafeCurrentDirectory()}",
            $"TEMP        : {temp}",
            $"日志        : {Log.LogPath}",
            $"结果        : {(layout.Found ? "OK" : "FAILED：找不到主程序")}",
        };

        // 报告写在日志旁边（exe 目录优先），不依赖 TEMP —— 这台机器上 TEMP 可不可写
        // 本身就是我们要排查的东西之一
        var written = Log.WriteSibling("boot-selftest.log", lines);
        if (written is null) Log.Error("自检报告写入失败（日志位置：" + Log.LogPath + "）");
        else Log.Info("自检结果写入 " + written);

        return layout.Found ? ExitOk : ExitFailed;
    }

    private static string SafeCurrentDirectory()
    {
        try { return Directory.GetCurrentDirectory(); }
        catch (Exception ex) { return "<异常：" + ex.Message + ">"; }
    }

    // ---------------------------------------------------------------- 截图（开发用）

    private static int Shot(AppLayout layout, IReadOnlyList<string> args, string prefix)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.BootWindow.RenderShots(layout, args, prefix);
        app.Shutdown();
        return ExitOk;
    }

    // ---------------------------------------------------------------- 参数

    private static bool TakeFlag(List<string> args, string name)
    {
        var hit = args.RemoveAll(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (hit) Log.Info("摘掉引导器开关：" + name);
        return hit;
    }

    private static string? TakeValue(List<string> args, string name)
    {
        var index = args.FindIndex(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;

        var value = args[index].Substring(name.Length + 1);
        args.RemoveAt(index);
        Log.Info($"摘掉引导器开关：{name}={value}");
        return string.IsNullOrWhiteSpace(value) ? Path.Combine(Path.GetTempPath(), "qcontrol-boot") : value;
    }
}
