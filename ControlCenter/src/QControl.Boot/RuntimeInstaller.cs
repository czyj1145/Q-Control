using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using QControl.Shared;

namespace QControl.Boot;

/// <summary>绿色版引导器里要显示给用户的可读错误（消息本身就够用，不需要堆栈）。</summary>
internal sealed class BootException : Exception
{
    public BootException(string message) : base(message)
    {
    }
}

/// <summary>一条安装进度（文案 + 0~100 的百分比）。</summary>
internal sealed class InstallerProgress
{
    public InstallerProgress(string text, double percent)
    {
        Text = text;
        Percent = percent;
    }

    public string Text { get; }

    public double Percent { get; }
}

/// <summary>
/// 补 .NET 10 桌面运行时：下载 → 提权静默安装 → 复检。
///
/// 与安装器（QControl.Setup\Runtime.cs）的分工：
///   · **检测**两边共用 `src\Shared\DotnetDesktopRuntime.cs`，判断标准完全一致；
///   · **安装**这段是绿色版自己的 —— 安装器那边跑在"自己就是管理员"的前提下，
///     绿色版则是一个普通用户双击的 exe，所以安装那一步必须用 runas **只提权安装进程**
///     （整个引导器提权会让每次开绿色版都弹 UAC，那是另一种劝退）。
///
/// 下载地址与安装参数刻意与安装器保持一致：下载走 `src\Shared\RuntimeDownload.cs`
/// （同一份多源回退），安装同样是 `/install|/repair|/uninstall` + `/quiet /norestart`。
/// 写死补丁号（10.0.14 之类）半年后必然失效，所以版本号是从微软的清单里现取的。
/// </summary>
internal static class RuntimeInstaller
{
    public const string DisplayName = RuntimeDownload.DisplayName;

    /// <summary>官网下载页（中文）：自动下载失败时的兜底出口。</summary>
    public const string DownloadPageUrl = RuntimeDownload.DownloadPageUrl;

    /// <summary>用户在 UAC 上点「否」时 Win32Exception 的 NativeErrorCode。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>微软 bootstrapper 的动作开关（官方文档里就 /install、/repair、/uninstall 这几个）。</summary>
    private const string InstallAction = "/install";

    /// <summary>重跑 4 个 MSI 的修复：对"文件被改坏"的机器有用。</summary>
    private const string RepairAction = "/repair";

    /// <summary>
    /// 清掉那份假登记。2026-10-30 实测：文件被整目录删掉时 `/repair` **不会**把文件补回来 ——
    /// MSI 每个组件都记着 `Installed: Local`，修复时全部 `Action: Null`，一个文件都不复制，
    /// 照样退出 0。只有先卸载（把 ARP/MSI 登记一起清掉）再装，才会真正落盘。
    /// </summary>
    private const string UninstallAction = "/uninstall";

    /// <summary>已装好的版本，没装返回 null。</summary>
    public static string? InstalledVersion() => DotnetDesktopRuntime.InstalledVersion(Log.Info);

    /// <summary>
    /// 保证运行时可用。已经装好就直接返回（不下载、不提权）。
    /// </summary>
    public static string EnsureInstalled(IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallerProgress("正在检查运行环境…", 0));

        if (InstalledVersion() is { } already)
        {
            Log.Info($"{DisplayName} 已就绪：{already}");
            progress?.Report(new InstallerProgress($"{DisplayName} {already} 已就绪", 100));
            return already;
        }

        var (setup, downloaded) = Acquire(progress, ct);
        try
        {
            var installed = Install(setup, progress);
            Log.Info($"{DisplayName} 安装完成：{installed}");
            progress?.Report(new InstallerProgress($"{DisplayName} {installed} 已就绪", 100));
            return installed;
        }
        finally
        {
            // ⚠️ 只删我们下载的那份。`setup` 可能是用户按提示放进目录里的安装包
            //    （离线逃生通道，报错文案正是让他这么做）—— 删掉的话他重试一次必然再失败，
            //    而且那是他自己准备的 57 MB 文件。
            if (downloaded) try { if (File.Exists(setup)) File.Delete(setup); } catch { /* 临时目录会被清理 */ }
        }
    }

    /// <summary>
    /// 真正去装，按机器状态选动作顺序。
    ///
    /// ⚠️ 2026-10-30 用户报障「自动装 .NET 10 在最后总是失败」，现场日志是：
    ///      i101: Detected package: WindowsDesktopRuntime, state: Present, cached: Yes
    ///      i201: Planned package: ..., execute: None
    ///      i500: Shutting down, exit code: 0x0
    ///    一秒内以退出码 0 退出、一个文件都没装 —— 因为这台机器上 **MSI 登记还在，
    ///    `%ProgramFiles%\dotnet` 里的文件却被删了**（目录里只剩 swidtag）。bootstrapper
    ///    据此认为"已经装过"，四个包全标成不执行。这种情况下 `/install` 点一万次都是空操作。
    ///
    ///    试过 `/repair`（同样被列进这里）：MSI 认为每个组件都 `Installed: Local`，
    ///    修复时全部 `Action: Null`，**复制 0 个文件**、仍然退出 0。所以走得通的路只有
    ///    "先 `/uninstall` 抹掉假登记，再 `/install` 真装一遍"。
    ///
    /// 顺序：半残留 → uninstall + install；正常缺 → install，不行再 repair。
    /// </summary>
    private static string Install(string setup, IProgress<InstallerProgress>? progress)
    {
        var halfInstalled = DotnetDesktopRuntime.IsHalfInstalled(Log.Info);
        if (DotnetDesktopRuntime.RebootPending)
            Log.Info("注意：系统有挂起的重启标记，MSI 可能跳过安装或要求重启后才生效");

        var actions = halfInstalled
            ? new[] { UninstallAction, InstallAction }
            : new[] { InstallAction, RepairAction };

        var codes = new List<(string Action, int Code)>(actions.Length);

        foreach (var action in actions)
        {
            progress?.Report(new InstallerProgress(
                $"{Text(action)} {DisplayName}…（若弹出管理员授权请点「是」）",
                action == UninstallAction ? 90 : action == RepairAction ? 93 : 88));

            var code = RunInstaller(setup, action, strict: action != UninstallAction);
            codes.Add((action, code));
            Log.Info($"运行时{Text(action)}程序退出码 {code}");

            // 退出码只是参考，**真正作数的是能不能检测到** ——
            // 上面那种"退出码 0 却什么都没装"就是活例子。卸载之后检测不到是正常的，
            // 所以这里只判断"装上了没有"，不判断"是不是失败"。
            if (InstalledVersion() is { } ok) return ok;
        }

        throw new BootException(FailureMessage(codes, halfInstalled));
    }

    /// <summary>动作的人话名字（进度条、日志、错误信息共用一套措辞）。</summary>
    private static string Text(string action) => action switch
    {
        InstallAction => "正在安装",
        RepairAction => "正在修复",
        UninstallAction => "正在清理残留的",
        _ => "正在处理",
    };

    /// <summary>
    /// 所有动作都试过还是检测不到时，给一句能照着做的话。
    ///
    /// 重点是把"为什么装了却不生效"讲明白：用户看到的只是"安装失败"，
    /// 而真实原因是这台机器上有一份删不干净的旧登记。
    /// </summary>
    private static string FailureMessage(List<(string Action, int Code)> codes, bool halfInstalled)
    {
        var detail = string.Join("、", codes.Select(c => $"{Text(c.Action).Replace("正在", "")}退出码 {c.Code}"));

        var text = $"{DisplayName} 没有装成功（{detail}）。";

        if (halfInstalled)
        {
            text += "\n\n这台机器上有残留的安装登记：控制面板的「已安装的应用」里还显示装着 10.x，" +
                    "但 %ProgramFiles%\\dotnet 下的文件已经不在了（被手工删过或被清理工具删过），" +
                    "所以安装程序每次都认为「无需安装」、一秒就退出。";
            text += "\n\n程序已经尝试过先卸载那份残留登记再重装，仍然不行。" +
                    "请到「设置 → 应用 → 已安装的应用」里手动卸载所有 Microsoft Windows Desktop Runtime 10.x，" +
                    "然后重新双击本程序。";
        }

        if (DotnetDesktopRuntime.RebootPending)
            text += "\n\n另外：系统有挂起的重启，建议先重启一次再试。";

        if (!halfInstalled)
            text += $"\n\n可以手动下载安装后再双击一次：\n{DownloadPageUrl}";

        return text;
    }

    // ================================================================ 获取安装包

    /// <summary>
    /// 拿到运行时安装包：优先用**用户自己放在绿色版目录里的**那份（内网 / 下载慢的逃生通道），
    /// 否则走网络下载。
    ///
    /// 本地优先为什么重要：这台机器正是"没有 .NET 10"的机器，往往也连不上 aka.ms。
    /// 只要把微软那个 exe 丢到 `Q-Control\` 或 `Q-Control\app\` 下，再双击引导器即可 ——
    /// 不必等我们发新版。
    /// </summary>
    /// <returns>`Downloaded` = true 才是我们自己下到临时目录的（可以删）。</returns>
    private static (string Path, bool Downloaded) Acquire(
        IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        if (FindLocalInstaller() is { } local)
        {
            Log.Info("使用绿色版目录里的运行时安装包：" + local);
            progress?.Report(new InstallerProgress("使用目录里已有的运行时安装包…", 60));
            return (local, false);
        }

        var target = Path.Combine(Path.GetTempPath(), "qcontrol-windowsdesktop-runtime.exe");
        try { if (File.Exists(target)) File.Delete(target); } catch { /* 换个名字 */ }
        if (File.Exists(target))
            target = Path.Combine(Path.GetTempPath(),
                "qcontrol-windowsdesktop-runtime-" + Guid.NewGuid().ToString("N") + ".exe");

        Download(target, progress, ct);
        return (target, true);
    }

    private static string? FindLocalInstaller()
    {
        try
        {
            var layout = AppLayout.Detect();
            foreach (var dir in new[] { layout.RootDir, layout.AppDir })
            {
                foreach (var pattern in new[] { "windowsdesktop-runtime-*-x64.exe", "dotnet-desktop-runtime.exe" })
                {
                    var hit = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                                       .OrderByDescending(f => new FileInfo(f).Length)
                                       .FirstOrDefault();
                    // 太小的多半是下载失败的残片（完整包约 57 MB）
                    if (hit is not null && new FileInfo(hit).Length > 20L * 1024 * 1024) return hit;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("查找本地运行时安装包失败：" + ex.Message);
        }
        return null;
    }

    // ================================================================ 下载

    private static void Download(string target, IProgress<InstallerProgress>? progress, CancellationToken ct)
    {
        // 真正的下载在 `src\Shared\RuntimeDownload.cs`（与安装器同一份）：多源回退、连接限时、
        // 内层异常翻译。这里只把 0~100 压进进度条前 85%（剩下 15% 留给安装）。
        var current = 0d;

        try
        {
            RuntimeDownload.Fetch(
                target,
                (percent, text) =>
                {
                    // percent < 0 = 正在换源，不知道总体走到哪了 ⇒ 保持上一次的刻度
                    if (percent >= 0) current = percent * 0.85;
                    progress?.Report(new InstallerProgress(text, current));
                },
                "QControl.Boot/1.0 (+https://github.com/czyj1145/Q-Control)",
                Log.Info,
                ct);

            Log.Info("下载完成 → " + target);
            progress?.Report(new InstallerProgress("下载完成，准备安装…", 85));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("下载失败", ex);
            throw new BootException(RuntimeDownload.FailureText(ex,
                "也可以把下载到的 windowsdesktop-runtime-10.x.x-win-x64.exe 放到绿色版目录里，再试一次。"));
        }
    }

    // ================================================================ 安装

    /// <summary>用 runas 提权跑微软的 bootstrapper（只有这一步需要管理员）。</summary>
    /// <param name="action">`/install`、`/repair` 或 `/uninstall`（见 <see cref="Install"/>）。</param>
    /// <param name="strict">
    /// true = 明显的失败码直接抛异常（安装/修复）；false = 只返回码（卸载清场时用，
    /// 清不干净也要接着往下装，由最后的检测说了算）。
    /// </param>
    private static int RunInstaller(string exe, string action, bool strict = true)
    {
        var psi = new ProcessStartInfo(exe, action + " /quiet /norestart")
        {
            // runas 动词只有 ShellExecute 认；/quiet 让它自己保持静默
            UseShellExecute = true,
            Verb = "runas",
        };

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Log.Info("用户在 UAC 上取消了授权");
            throw new BootException(
                "已取消管理员授权，运行时没有安装。\n\n" +
                "再点一次「自动下载并安装」并允许授权即可；\n" +
                "也可以自己到官网下载安装（不需要重装 Q-Control）。");
        }

        if (p is null) throw new BootException($"{DisplayName} 安装程序启动失败（拿不到进程句柄）。");

        using (p)
        {
            // 装共享框架最慢见过两分多钟（机械盘 + 杀软扫描），留足 15 分钟再说超时
            if (!p.WaitForExit(15 * 60 * 1000))
            {
                try { p.Kill(); } catch { /* 已经退了 */ }
                throw new BootException($"{DisplayName} 安装超时（超过 15 分钟）。请手动安装：\n{DownloadPageUrl}");
            }

            // 3010/1641 = 装好了但要重启；5100 = 已经有更高补丁。都算成功，最终看检测结果。
            var code = p.ExitCode;
            if (code == 0 || code == 3010 || code == 1641 || code == 5100 || !strict) return code;

            throw new BootException(
                $"{DisplayName} 安装失败（安装程序退出码 {code}）。\n\n" +
                $"手动下载安装后可再试：\n{DownloadPageUrl}");
        }
    }
}
