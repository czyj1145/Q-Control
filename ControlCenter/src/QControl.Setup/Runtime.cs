using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using QControl.Shared;

namespace QControl.Setup;

/// <summary>
/// 主程序要的 .NET 10 桌面运行时：检测 / 下载 / 静默安装。
///
/// 为什么安装器管这件事（2026-10-28 两次定调后的最终形态）：
///   · **安装版**：用户按下「安装」就什么都不用管 —— 缺运行时自动下载 + 静默装好，
///     进度直接画在向导的进度条上。
///   · **绿色版**：由 `QControl.Boot`（zip 里叫 `Q-Control.exe`）管同一件事，
///     它复用本文件的安装流程思路、共用同一份检测实现。绿色版**不带安装器**，
///     所以那个引导器就是它的"最小安装器"。
///   主程序是框架依赖发布（约 2 MB），机器上必须有 `Microsoft.WindowsDesktop.App 10.x`。
///   安装器自己跑在 .NET Framework 4.8 上（Windows 自带），所以有能力在装之前把它补齐。
///
/// 检测为什么读目录、不跑 `dotnet --list-runtimes`：
///   ① 那条命令要求 dotnet.exe 在 PATH 上，而 PATH 被改坏的机器恰恰是最需要兜底的；
///   ② 我们要判断的是"**x64 的 WindowsDesktop 共享框架**在不在"，目录结构就是答案，
///      不需要经过任何外部进程（也就不会因为杀软拦子进程而误判）。
///
/// ⚠️ 2026-10-29 起**检测部分搬到 `src\Shared\DotnetDesktopRuntime.cs`**，与绿色版引导器
///    （QControl.Boot）共用同一份实现：两个入口都在回答"这台机器能不能跑主程序"，
///    各抄一份的话迟早出现"安装器说缺、引导器说不缺"的自相矛盾。下面只剩安装那半边。
/// </summary>
internal static class DotnetRuntime
{
    /// <summary>界面上怎么称呼它。</summary>
    public const string DisplayName = RuntimeDownload.DisplayName;

    /// <summary>
    /// 官网下载页（中文）。写进错误提示与绿色版的使用说明。
    ///
    /// 自动下载的**地址清单**不在这里 —— 见 `src\Shared\RuntimeDownload.cs`：
    /// 2026-10-30 起改成多源回退（aka.ms → 版本清单 → Azure CDN 直链），与绿色版引导器共用一份。
    /// </summary>
    public const string DownloadPageUrl = RuntimeDownload.DownloadPageUrl;


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

    /// <summary>
    /// 运行时的**默认安装位置**：`%ProgramFiles%\dotnet`。
    ///
    /// 微软的安装器只认 `/install /quiet /norestart`（官方文档里就这三个 + `/uninstall`、
    /// `/repair`、`/?`），**没有**自定义目录的开关：不带 `--install-dir` 时它自己就装到
    /// 这里。所以"装在默认路径"这件事的正确做法是"不加任何多余参数"，而不是我们编一个
    /// 它不认的参数（未知参数在静默模式下会直接失败）。
    ///
    /// 唯一的例外是机器上**原本就装在别处**：微软安装器只做原位升级，不会搬家。
    /// 那种情况我们也不搬 —— 报告里记一笔、照常继续，因为 .NET host 认的是注册表登记的
    /// 位置，程序照样能起来（见 <see cref="RootOf"/> 的日志）。
    /// </summary>
    public static string DefaultRoot => DotnetDesktopRuntime.DefaultRoot;

    // ================================================================ 检测

    /// <summary>
    /// 已安装的版本（如 `10.0.12`），没装返回 null。
    ///
    /// 实现见 <see cref="DotnetDesktopRuntime.InstalledVersion"/>（与绿色版引导器共用）。
    /// </summary>
    public static string? InstalledVersion() => DotnetDesktopRuntime.InstalledVersion(Log.Warn);

    public static bool IsInstalled => InstalledVersion() is not null;

    /// <summary>这个版本实际落在哪个根目录（给日志用：确认是不是装在默认位置）。</summary>
    public static string? RootOf(string version) => DotnetDesktopRuntime.RootOf(version);

    // ================================================================ 安装

    /// <summary>
    /// 保证运行时可用：已装就直接过，没装就下载 + 静默安装。
    ///
    /// 百分比落在 [fromPercent, toPercent] 里（下载占前 75%，安装占后 25%），
    /// 这样调用方的进度条不用为"要不要装运行时"分两种情况。
    /// </summary>
    public static void EnsureInstalled(IProgress<SetupProgress>? progress, int fromPercent, int toPercent,
                                       CancellationToken ct)
    {
        progress?.Report(new SetupProgress(fromPercent, "正在检查运行环境…"));

        var installed = InstalledVersion();
        if (installed is not null)
        {
            Log.Info($"{DisplayName} 已安装：{installed}");
            progress?.Report(new SetupProgress(fromPercent, $"{DisplayName} {installed} 已就绪"));
            return;
        }

        Log.Info($"{DisplayName} 未安装，准备下载：" + RuntimeDownload.LatestChannelUrl);
        Log.Info("安装目标：默认位置 " + DefaultRoot + "（不传自定义目录 —— 微软安装器不认那个开关）");

        var (setup, downloaded) = AcquireInstaller(progress, fromPercent,
                                                   fromPercent + (toPercent - fromPercent) * 3 / 4, ct);

        try
        {
            installed = Install(setup, progress, fromPercent + (toPercent - fromPercent) * 3 / 4);

            Log.Info($"{DisplayName} 安装完成：{installed}");

            // 落点复核：正常情况下就在 %ProgramFiles%\dotnet。装在别处只可能是"机器上原本
            // 就装在别处"（微软安装器只做原位升级、不会搬家）—— 记一笔，但不算失败：
            // .NET host 认的是注册表登记的位置，程序照样起得来。
            var root = RootOf(installed);
            if (root is null)
                Log.Warn($"检测到 {installed}，但找不到它的目录 —— 有多个 dotnet 根目录？");
            else if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(DefaultRoot), StringComparison.OrdinalIgnoreCase))
                Log.Info($"运行时落在默认位置：{root}");
            else
                Log.Warn($"运行时不在默认位置（{root}）—— 本机原本就装在别处，微软安装器只做原位升级");

            progress?.Report(new SetupProgress(toPercent, $"{DisplayName} {installed} 已就绪"));
        }
        finally
        {
            // ⚠️ 只删**我们自己下载的**。`setup` 也可能是用户按提示放在安装包旁边的那份
            //    （见 AcquireInstaller：离线/内网逃生通道）—— 那是用户自己的 57 MB 文件，
            //    装完就删掉的话，他拿同一个包装第二台机器时又要重新准备。
            if (downloaded) try { if (File.Exists(setup)) File.Delete(setup); } catch { /* 临时目录会被清理 */ }
        }
    }

    /// <summary>
    /// 拿到运行时安装包：优先用**放在安装包旁边的**那份（离线/内网场景），否则下载。
    ///
    /// 本地优先是有意设计的逃生通道：下载失败时用户只要把微软那个 exe 丢到
    /// **安装包旁边**（`QControl-<版本>-setup-win-x64.exe` 同目录）再跑一次就行，不用等我们发新版。
    /// </summary>
    /// <returns>
    /// `Path` = 安装包路径；`Downloaded` = true 表示这是我们刚下到临时目录的（可以删），
    /// false 表示是用户自己的那份（**绝不能删**）。
    /// </returns>
    private static (string Path, bool Downloaded) AcquireInstaller(
        IProgress<SetupProgress>? progress, int from, int to, CancellationToken ct)
    {
        var local = FindLocalInstaller();
        if (local is not null)
        {
            Log.Info("使用安装包旁边的运行时安装文件：" + local);
            progress?.Report(new SetupProgress(to, $"使用本地 {DisplayName} 安装包…"));
            return (local, false);
        }

        var target = Path.Combine(Path.GetTempPath(), "qcontrol-windowsdesktop-runtime.exe");
        if (File.Exists(target)) { try { File.Delete(target); } catch { /* 换个名字 */ } }
        if (File.Exists(target))
            target = Path.Combine(Path.GetTempPath(), "qcontrol-windowsdesktop-runtime-" + Guid.NewGuid().ToString("N") + ".exe");

        Download(target, progress, from, to, ct);
        return (target, true);
    }

    private static string? FindLocalInstaller()
    {
        try
        {
            var dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName ?? "");
            if (string.IsNullOrEmpty(dir)) return null;
            foreach (var pattern in new[] { "windowsdesktop-runtime-*-x64.exe", "dotnet-desktop-runtime.exe" })
            {
                var hit = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                                   .OrderByDescending(f => new FileInfo(f).Length)
                                   .FirstOrDefault();
                // 太小的多半是个下载失败的残片
                if (hit is not null && new FileInfo(hit).Length > 20L * 1024 * 1024) return hit;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("查找本地运行时安装包失败：" + ex.Message);
        }
        return null;
    }

    /// <summary>
    /// 流式下载（要进度 + 要能取消），写进 <paramref name="target"/>。
    ///
    /// 真正干活的是 `src\Shared\RuntimeDownload.cs`（与绿色版引导器同一份）：
    /// 多源回退 + 连接阶段限时 + 内层异常翻译。这里只负责把 0~100 的百分比
    /// 映射到向导进度条的 [from, to] 区间里。
    /// </summary>
    private static void Download(string target, IProgress<SetupProgress>? progress, int from, int to, CancellationToken ct)
    {
        var current = from;

        try
        {
            RuntimeDownload.Fetch(
                target,
                (percent, text) =>
                {
                    // percent < 0 = 正在换源，不知道总体走到哪了 ⇒ 保持上一次的刻度
                    if (percent >= 0) current = from + (int)((to - from) * percent / 100.0);
                    progress?.Report(new SetupProgress(current, text));
                },
                "QControl.Setup/" + SetupOptions.Version + " (+https://github.com/czyj1145/Q-Control)",
                Log.Info,
                ct);

            Log.Info("下载完成 → " + target);
            progress?.Report(new SetupProgress(to, "下载完成，准备安装…"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("下载失败：" + ex);
            throw new SetupException(RuntimeDownload.FailureText(ex,
                "也可以把下载到的 exe 放到安装包旁边，安装器会自动使用它。"));
        }
    }

    /// <summary>
    /// 真正去装：先按常规 `/install`，装不上再 `/repair`（与绿色版引导器同一套策略）。
    ///
    /// ⚠️ 2026-10-30 的现场：MSI 登记还在、`%ProgramFiles%\dotnet` 里的文件却没了
    ///    （目录里只剩 swidtag）。bootstrapper 据此把四个包全判成 Present、计划成
    ///    `execute: None`，一秒内以退出码 0 退出、一个文件都没装 —— `/install` 再点多少次
    ///    都是空操作，只有 `/repair` 会重跑 MSI 修复把文件补回去。
    ///    认出这个状态就先 repair（省一次 UAC），否则先 install 后 repair。
    /// </summary>
    private static string Install(string setup, IProgress<SetupProgress>? progress, int panicFrom)
    {
        var halfInstalled = DotnetDesktopRuntime.IsHalfInstalled(Log.Warn);
        if (DotnetDesktopRuntime.RebootPending)
            Log.Warn("系统有挂起的重启标记，MSI 可能跳过安装或要求重启后才生效");

        var actions = halfInstalled
            ? new[] { UninstallAction, InstallAction }
            : new[] { InstallAction, RepairAction };

        var codes = new List<(string Action, int Code)>(actions.Length);

        foreach (var action in actions)
        {
            progress?.Report(new SetupProgress(panicFrom,
                $"{Text(action)} {DisplayName}…（约 1~3 分钟，请不要关闭窗口）"));

            // 卸载那一步不严判：它只是"清场"，清不掉也要接着往下装，由最后的检测说了算
            var code = RunInstaller(setup, action, strict: action != UninstallAction);
            codes.Add((action, code));
            Log.Info($"运行时{Text(action).Replace("正在", "")}程序退出码 {code}");

            // 退出码只是参考，**真正作数的是"能不能检测到"**（同上：0 也可能什么都没做）
            if (InstalledVersion() is { } ok) return ok;
        }

        throw new SetupException(FailureMessage(codes, halfInstalled));
    }

    /// <summary>动作的人话名字（进度条、日志、错误信息共用一套措辞）。</summary>
    private static string Text(string action) => action switch
    {
        InstallAction => "正在安装",
        RepairAction => "正在修复",
        UninstallAction => "正在清理残留的",
        _ => "正在处理",
    };

    private static string FailureMessage(List<(string Action, int Code)> codes, bool halfInstalled)
    {
        var detail = string.Join("、", codes.Select(c => $"{Text(c.Action).Replace("正在", "")}退出码 {c.Code}"));

        var text = $"{DisplayName} 安装未完成（{detail}）。";

        if (halfInstalled)
        {
            text += "\n\n这台机器上有残留的安装登记：控制面板里还显示装着 10.x，但 %ProgramFiles%\\dotnet 下的文件已经不在了，" +
                    "所以安装程序每次都认为「无需安装」而直接退出。";
            text += "\n\n程序已经尝试过先卸载那份残留登记再重装，仍然不行。" +
                    "请到「设置 → 应用 → 已安装的应用」里手动卸载所有 Microsoft Windows Desktop Runtime 10.x，再重新运行本安装包。";
        }
        else
        {
            text += $"\n\n可手动下载安装后重试：{DownloadPageUrl}";
        }

        if (DotnetDesktopRuntime.RebootPending)
            text += "\n\n另外：系统有挂起的重启，建议先重启一次再试。";

        return text;
    }

    /// <summary>静默跑微软的 bootstrapper。返回退出码。</summary>
    /// <param name="action">`/install`、`/repair` 或 `/uninstall`（见 <see cref="Install"/>）。</param>
    /// <param name="strict">
    /// true = 明显的失败码直接抛异常（安装/修复）；false = 只返回码（卸载清场时用）。
    /// </param>
    private static int RunInstaller(string exe, string action, bool strict = true)
    {
        var psi = new ProcessStartInfo(exe, action + " /quiet /norestart")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var p = Process.Start(psi)
            ?? throw new SetupException($"{DisplayName} 安装程序启动失败（拿不到进程句柄）。");

        // 装共享框架最慢见过两分多钟（机械盘 + 杀软扫描），留足 15 分钟再说超时
        if (!p.WaitForExit(15 * 60 * 1000))
        {
            try { p.Kill(); } catch { /* 已经退了 */ }
            throw new SetupException($"{DisplayName} 安装超时（超过 15 分钟）。请手动安装运行时后重试。\n{DownloadPageUrl}");
        }

        // 3010/1641 = 装好了但要重启；5100 = 已经有更高补丁。都算成功，最终看检测结果。
        var code = p.ExitCode;
        if (code == 0 || code == 3010 || code == 1641 || code == 5100 || !strict) return code;

        throw new SetupException(
            $"{DisplayName} 安装失败（安装程序退出码 {code}）。\n\n" +
            $"可手动下载安装后重试：{DownloadPageUrl}");
    }

    /// <summary>
    /// 给 `/selftest /net` 用：只跑下载（不安装），用来证明"干净机器上能不能把运行时拉下来"。
    ///
    /// 为什么单独开这个口子：自动下载是这套分发方式的命门（安装包只有 1 MB，全靠它补
    /// 57 MB 运行时），但它平时在开发机上永远走不到（开发机必然装着 .NET 10）。
    /// 让它可被单独触发，才能在发版前真的验一次 —— 否则这段代码只能等用户装机时才第一次运行。
    /// </summary>
    internal static void DownloadForTest(string target, CancellationToken ct)
        => Download(target, null, 0, 100, ct);

    /// <summary>给界面显示的一行状态（"已就绪 10.0.12" / "安装时自动获取"）。</summary>
    public static string StatusLine()
    {
        var v = InstalledVersion();
        return v is not null
            ? $"{DisplayName} {v} 已就绪"
            : $"本机没有 {DisplayName}，安装时会自动下载安装（约 57 MB，需联网）";
    }
}
