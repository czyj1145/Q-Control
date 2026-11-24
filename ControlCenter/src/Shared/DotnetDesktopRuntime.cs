using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QControl.Shared;

/// <summary>
/// 检测「.NET 10 桌面运行时」在不在 —— **安装器和绿色版引导器共用的唯一一份实现**。
///
/// 这个文件被 `QControl.Setup`（安装器）和 `QControl.Boot`（绿色版引导器）以
/// Link 方式编进各自产物（见两个 csproj 的 Compile Include）。
/// 为什么必须共用：两处都在回答同一个问题"这台机器能不能跑主程序"，
/// 抄成两份的话，哪天改了判断规则只会改一边 —— 于是安装器说"缺，我给你装"，
/// 引导器说"有不缺，你自己去开"，用户看到的两个入口自相矛盾。
///
/// 为什么读目录而不跑 `dotnet --list-runtimes`：
///   ① 那条命令要求 dotnet.exe 在 PATH 上，而 PATH 被改坏的机器恰恰最需要兜底；
///   ② 要判断的是"x64 的 WindowsDesktop 共享框架在不在"，目录结构就是答案，
///      不必经过外部进程（也就不会因为杀软拦子进程而误判）。
/// </summary>
internal static class DotnetDesktopRuntime
{
    /// <summary>主程序的 TFM 是 net10.0-windows，只认 10.x（跨大版本不会自动 roll forward）。</summary>
    public const int RequiredMajor = 10;

    /// <summary>共享框架的目录名（`&lt;dotnet root&gt;\shared\Microsoft.WindowsDesktop.App\&lt;版本&gt;`）。</summary>
    public const string SharedFramework = "Microsoft.WindowsDesktop.App";

    /// <summary>
    /// 运行时的**默认安装位置**：`%ProgramFiles%\dotnet`。
    ///
    /// 微软的安装器只认 `/install /quiet /norestart`，**没有**自定义目录的开关：
    /// 不带 `--install-dir` 时它自己就装到这里。所以"装在默认路径"的正确做法
    /// 是"不加任何多余参数"，而不是编一个它不认的参数（未知参数在静默模式下直接失败）。
    /// </summary>
    public static string DefaultRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

    /// <summary>
    /// 已安装的版本（如 `10.0.12`），没装返回 null。
    ///
    /// 找两处：默认的 `%ProgramFiles%\dotnet`，以及注册表登记的 dotnet 根目录
    /// （有人把它装到 D 盘）。两处都看，纯粹是为了"装过但装在别处"的机器不误判。
    /// </summary>
    /// <param name="warn">读目录/注册表失败时的告警出口（两边各有各的日志，所以由调用方给）。</param>
    public static string? InstalledVersion(Action<string>? warn = null)
    {
        string? best = null;

        foreach (var root in CandidateRoots(warn))
        {
            string[] dirs;
            try
            {
                var shared = Path.Combine(root, "shared", SharedFramework);
                if (!Directory.Exists(shared)) continue;
                dirs = Directory.GetDirectories(shared);
            }
            catch (Exception ex)
            {
                warn?.Invoke($"读取运行时目录失败（{root}）：{ex.Message}");
                continue;
            }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (!Version.TryParse(Normalize(name), out var ver)) continue;
                if (ver.Major != RequiredMajor) continue;

                // 目录在 ≠ 能用：`10.0.14` 这个文件夹完全可以只剩一半（被杀软/清理工具/手删动过）。
                // 那种机器上如果直接判"已就绪"，引导器就会把主程序拉起来，然后死在程序集加载上 ——
                // 症状从"安装失败"变成更难查的启动崩溃。所以这里要抽查文件。
                if (!IsComplete(root, name, warn)) continue;

                if (best is null || Version.Parse(Normalize(name)) > Version.Parse(Normalize(best))) best = name;
            }
        }

        return best;
    }

    /// <summary>
    /// 这个版本的运行时是不是**完整**的（文件抽查，不是逐个文件校对）。
    ///
    /// 只查两类东西，都是"缺了必然起不来"的：
    ///   ① 版本目录里的 4 个 WPF 入口程序集 —— 主程序是 WPF，`PresentationFramework.dll`
    ///      之类不见了的机器，`dotnet --list-runtimes` 照样报得好好的；
    ///   ② `host\fxr\*\hostfxr.dll` —— 框架依赖程序的 apphost 就是靠它引导的。
    ///
    /// 不做全量校验（63 个 dll 逐个比对哈希太重，这段代码跑在每次双击的启动路径上）：
    /// 抽查足够覆盖"整目录被删 / 删了一部分 / 只留空壳目录"这几种真实会遇到的破坏。
    /// </summary>
    public static bool IsComplete(string root, string version, Action<string>? warn = null)
    {
        var dir = Path.Combine(root, "shared", SharedFramework, version);

        foreach (var file in CriticalFiles)
        {
            if (File.Exists(Path.Combine(dir, file))) continue;
            warn?.Invoke($"运行时 {version} 目录存在但缺 {file} —— 当作没装：{dir}");
            return false;
        }

        if (!HasHostFxr(root))
        {
            warn?.Invoke($"运行时 {version} 缺 hostfxr（{Path.Combine(root, "host", "fxr")}）—— 当作没装");
            return false;
        }

        return true;
    }

    /// <summary>WPF 的四个入口程序集：主程序只要跑起来就一定会加载它们。</summary>
    private static readonly string[] CriticalFiles =
    {
        "PresentationFramework.dll",
        "PresentationCore.dll",
        "WindowsBase.dll",
        "System.Xaml.dll",
    };

    /// <summary>`host\fxr\&lt;版本&gt;\hostfxr.dll` 在不在（版本号不固定，扫一层）。</summary>
    private static bool HasHostFxr(string root)
    {
        try
        {
            var fxr = Path.Combine(root, "host", "fxr");
            if (!Directory.Exists(fxr)) return false;

            foreach (var dir in Directory.GetDirectories(fxr))
                if (File.Exists(Path.Combine(dir, "hostfxr.dll"))) return true;

            return false;
        }
        catch
        {
            // 读不动就当没有：宁可多修一次，也不要让缺文件的运行时混过去
            return false;
        }
    }

    /// <summary>这个版本实际落在哪个根目录（确认是不是装在默认位置）。</summary>
    public static string? RootOf(string version)
    {
        foreach (var root in CandidateRoots(null))
            if (IsComplete(root, version)) return root;
        return null;
    }

    /// <summary>`10.0.12` → 可直接喂给 <see cref="Version.TryParse"/> 的三段形式。</summary>
    public static string Normalize(string version)
    {
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => parts[0] + ".0.0",
            2 => parts[0] + "." + parts[1] + ".0",
            _ => string.Join(".", parts.Take(3)),
        };
    }

    // ================================================================ 异常状态识别

    /// <summary>
    /// ARP（控制面板「应用和功能」）里登记着的 WindowsDesktop 运行时条目，只挑 <paramref name="major"/> 大版本。
    ///
    /// 为什么需要它：2026-10-30 用户报障「绿色版自动装 .NET 10 在最后总是失败」，
    /// 现场是 **MSI 登记还在、`%ProgramFiles%\dotnet` 里的文件却被删了**
    /// （目录里只剩一个 swidtag）。这种状态下微软的 bootstrapper 会把 4 个包全部
    /// 检测成 `Present`、计划成 `execute: None`，一秒内以**退出码 0 退出却什么都没装** ——
    /// 于是"重试一百次都是失败"。识别出这个状态，才有资格走 `/repair` 自愈。
    /// </summary>
    public static IReadOnlyList<string> RegisteredRuntimes(int major, Action<string>? warn = null)
    {
        var hits = new List<string>();

        foreach (var path in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (root is null) continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(name);
                        if (sub?.GetValue("DisplayName") is not string display) continue;

                        // 只认运行时本体（"Microsoft Windows Desktop Runtime - 10.0.14 (x64)"，
                        // 以及 bundle 那条不带连字符的）——别把 SDK、Host、我们的程序算进来
                        if (!display.StartsWith("Microsoft Windows Desktop Runtime", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (display.IndexOf(" " + major + ".", StringComparison.Ordinal) < 0) continue;

                        hits.Add(display);
                    }
                    catch
                    {
                        // 单个子键读不动不算事
                    }
                }
            }
            catch (Exception ex)
            {
                warn?.Invoke("读卸载登记失败（" + path + "）：" + ex.Message);
            }
        }

        return hits;
    }

    /// <summary>
    /// 「MSI 说装了、文件却不在」的半残留状态：ARP 有登记，但读目录读不到 10.x。
    ///
    /// 这种机器上 `/install` 会变成空操作（见 <see cref="RegisteredRuntimes"/> 的注释），
    /// 必须先 `/repair` 把文件补回去，或者先卸载掉残留登记再重装。
    /// </summary>
    public static bool IsHalfInstalled(Action<string>? warn = null)
    {
        var registered = RegisteredRuntimes(RequiredMajor, warn);
        if (registered.Count == 0) return false;
        if (InstalledVersion(warn) is not null) return false;

        warn?.Invoke("检测到半残留：" + string.Join("、", registered) +
                     " 已登记，但 " + DefaultRoot + " 下读不到 " + RequiredMajor + ".x 共享框架");
        return true;
    }

    /// <summary>
    /// 系统是否有挂起的重启。有这个标记时 MSI 安装/修复可能直接被跳过或返回 3010，
    /// 是"装到最后一刻失败"的经典外因之一 —— 只用来写日志和提示，不作为失败判据。
    /// </summary>
    public static bool RebootPending
    {
        get
        {
            var keys = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootInProgress",
            };

            foreach (var path in keys)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    if (key is not null) return true;
                }
                catch
                {
                    // 读不到就当没有
                }
            }

            return false;
        }
    }

    // ================================================================ 候选根目录

    /// <summary>候选根目录：默认位置优先，其次注册表登记的位置。</summary>
    public static IEnumerable<string> CandidateRoots(Action<string>? warn)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seen.Add(DefaultRoot)) yield return DefaultRoot;

        string? registered = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            registered = key?.GetValue("InstallLocation") as string;
        }
        catch (Exception ex)
        {
            warn?.Invoke("读 dotnet 安装位置失败：" + ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(registered) && seen.Add(registered!)) yield return registered!;
    }
}
