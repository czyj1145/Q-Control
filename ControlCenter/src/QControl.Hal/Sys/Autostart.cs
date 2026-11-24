using Microsoft.Win32;
using QControl.Core.Startup;

namespace QControl.Hal.Sys;

/// <summary>开机自启要拉起的「命令行三件套」。计划任务要分开填 Path / Arguments / WorkingDirectory。</summary>
public sealed record AutostartCommand(string Exe, string Args, string WorkingDir)
{
    /// <summary>完整命令行（写注册表 Run 项用，与 `tray.autostart_cmd()` 同格式）。</summary>
    public string CommandLine
        => string.IsNullOrEmpty(Args) ? $"\"{Exe}\"" : $"\"{Exe}\" {Args}";
}

/// <summary>
/// 本程序自己的开机自启：查状态 + 开 + 关。
///
/// 两套机制并存，任一在就算"开着"（`tray.is_autostart()` 同口径）：
///   ① 计划任务 `\QControl`（登录触发 + HighestAvailable）
///      —— 正常形态，能直接以管理员启动，不弹 UAC、不弹控制台；
///   ② 老机制 `HKCU\...\Run\QControl`
///      —— 早先实现在「非管理员建任务失败」时才退回这里（历史包袱）。
///
/// ⚠️ 关的时候必须**两处都清**，否则残留的那个照样会在开机时拉起 ——
///    这就是"关了自启但开机还在"的经典原因。
///
/// ⚠️ C# 版**不**走「非管理员 → 写 Run 项」那条降级路：Run 项拉起来的是非管理员进程
///    （Run 项没法带 HighestAvailable），EC 根本写不出去，用户会看到"自启成功"却
///    什么都不能调。非管理员就如实失败并说明原因。
///    （自我提权只解决"人手动启动"那一路 —— 开机时没人能点 UAC，见 <see cref="Elevation"/>。）
///
/// ⚠️ 自启入口是**全机唯一**的：C# 与早先实现共用同一个任务名/注册表值。
///    在 C# 界面里开一次 = 开机拉起 C#；在早先实现界面里开一次 = 又变回早先实现。
///    这是 P7 共存期的固有行为，不是 bug（P8 之后早先实现归档就只剩一套）。
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "QControl";
    public const string TaskName = "QControl";

    /// <summary>
    /// 崩溃后自动重启的次数（2026-11-02）。
    ///
    /// 为什么需要：任务原来只有「登录时启动」一个触发器，进程**崩溃或被任务管理器结束**之后
    /// 就什么都不剩了 —— 风扇停在 EC 最后一次写入的 UFA 上，自启也没了，只能等用户下次重启。
    /// 而这正是最需要它的场景（后台跑着不管它，几小时后挂了没人发现）。
    ///
    /// ⚠️ 只对**失败**生效：`POST /api/app/quit` / 托盘退出走 `return 0`，任务计划服务
    ///    认为正常结束，不会重启。用户主动退出绝不能被强行拉回来。
    /// ⚠️ 次数用完就放弃（3 次），避免"启动即崩"变成每 10 分钟一次的无限循环。
    /// </summary>
    public const int TaskRestartCount = 3;

    /// <summary>重启间隔（分钟）。</summary>
    public const int TaskRestartIntervalMin = 1;
    /// <summary>
    /// 改名前的旧标识（2026-10-23：ColorfulControlCenter → QControl）。
    /// 旧任务/旧 Run 项指向的 exe 已经不存在，留着会让开机时静默失败，
    /// 所以在启动维护里清一次。**只在存在时动**，幂等。
    /// </summary>
    private static readonly string[] LegacyTaskNames = ["ColorfulControlCenter"];
    private const string LegacyRunName = "ColorfulControlCenter";

    /// <summary>自启是否开着。计划任务优先，读不到再看注册表。</summary>
    public static bool IsOn()
        => TaskScheduler.Query(@"\", TaskName).Exists
           || ReadRun() is { Length: > 0 };

    /// <summary>老的 Run 项值（诊断用；正常形态下应该为空）。</summary>
    public static string? RunValue() => ReadRun();

    /// <summary>当前进程是不是管理员（注册 HighestAvailable 任务的前提）。</summary>
    public static bool IsAdmin() => Elevation.IsElevated();

    // ------------------------------------------------------------ 默认命令

    /// <summary>
    /// 自启应该拉起什么：**主程序自己** + `--tray`，就一个 exe。
    ///
    /// 主程序是 **WinExe**（GUI 子系统），计划任务拉起它不会弹控制台 ——
    ///
    /// ⚠️ 不要再往这里加第二个 exe：一个 PE 只有一个子系统，而主程序已经是
    ///    "双击不出黑框 + 需要时用 ConsoleHost 接回父控制台"的那个形态了。
    /// </summary>
    public static AutostartCommand TrayCommand(int port)
    {
        var dir = BuildDir();

        // 端口显式带上：用 `--port 9000` 跑过一次，自启也该回到同一个端口
        return new AutostartCommand(Path.Combine(dir, MainExeName), $"--tray --port {port}", dir);
    }

    /// <summary>主程序文件名。全项目只有这一个入口 exe（安装器/引导器也认这个名字）。</summary>
    public const string MainExeName = CopyIdentity.MainExeName;

    /// <summary>
    /// 「本程序自己」的入口命令（不带端口参数）—— 只用来回答"任务里那条指向的是不是我这份"。
    ///
    /// 走 <see cref="BuildDir"/> 同一条归一化规则：开发机上从 `bin\Release` 跑起来时，
    /// 答案仍然是 `bin\R2R`（自启该装的那份），否则会把自己的任务判成"别人的"，
    /// 每跑一次 Release 就白弹一发 UAC。
    /// </summary>
    public static AutostartCommand SelfCommand()
    {
        var dir = BuildDir();
        return new AutostartCommand(Path.Combine(dir, MainExeName), "", dir);
    }

    /// <summary>
    /// 自启该拉起**哪份构建**：优先 ReadyToRun。
    ///
    /// 为什么不能只看 `AppContext.BaseDirectory`：同一个程序有两份产物 ——
    /// `bin\Release\`（`dotnet build` / `dotnet test` 的副产物）和
    /// `bin\R2R\`（`publish-r2r.cmd`，启动链与文档里的"标准构建"）。
    /// 只按"当前是谁在跑"决定，就会得到「任务指向 Release、wsf 指向 R2R」这种
    /// 两份目录各跑一半的局面（2026-10-22 实测就是这个状态）。
    /// 这里定成一条规则：**从 Release 跑起来时要装自启，就装到 R2R**。
    ///
    /// ⚠️ R2R 必须**成套在**（`QControl.exe` + `QControl.dll`）才算数：只搬 exe 的那份
    ///    一启动就死（"The application to execute does not exist"），日志都不会有。
    ///    缺一样就退回当前目录，不做猜测。
    /// </summary>
    private static string BuildDir()
    {
        var dir = AppContext.BaseDirectory;                       // 结尾自带 '\'
        var leaf = Path.GetFileName(dir.TrimEnd('\\'));           // net10.0-windows
        var bin = Path.GetDirectoryName(dir.TrimEnd('\\'));       // ...\bin\Release
        if (bin is null || leaf.Length == 0) return dir;

        if (!Path.GetFileName(bin).Equals("Release", StringComparison.OrdinalIgnoreCase))
            return dir;

        var binRoot = Path.GetDirectoryName(bin);
        if (binRoot is null) return dir;

        var r2r = Path.Combine(binRoot, "R2R", leaf);
        if (File.Exists(Path.Combine(r2r, MainExeName))
            && File.Exists(Path.Combine(r2r, "QControl.dll")))
            return r2r + "\\";

        return dir;
    }

    /// <summary>
    /// 自启入口是不是"主程序自己、而且文件在"。
    ///
    /// 两条都要：主程序是 WinExe ⇒ 开机不弹控制台；文件不在 ⇒ 开机静默失败，
    /// 用户只会看到"自启没生效"（最没用的一种故障现象）。
    /// 2026-10-30 之前这里判的是"指不指向静默启动器"，那个 exe 已经并进主程序。
    /// </summary>
    public static bool SelfEntryPresent(AutostartCommand cmd)
        => Path.GetFileName(cmd.Exe).Equals(MainExeName, StringComparison.OrdinalIgnoreCase)
           && File.Exists(cmd.Exe);

    // ------------------------------------------------------------ 零 UAC 启动

    /// <summary>
    /// 用**已有的自启任务**启动一次（零 UAC）。这是"手动双击"那条路的正确走法：
    /// 任务里存的是 HighestAvailable 令牌，任务计划服务代启 ⇒ 不弹 UAC。
    ///
    /// 为什么不让调用方直接 `runas`：UAC 只在"需要提权且当前没提权"时出现，
    /// 而任务启动**跳过整个提权协商**。装任务的第一次要一次 UAC，之后每次都不弹 ——
    /// 这正是用户要的「只在首次弹，之后关机重启也不弹」。
    ///
    /// 返回 `InstallNeeded=true` 表示"没有可用的任务"（首次 / 用户关过自启），
    /// 此时调用方才该退回 `runas`，并且要用这一次提权把任务装好。
    /// </summary>
    public static (bool Ok, bool AlreadyRunning, bool InstallNeeded, string Detail) RunNow(
        AutostartCommand? expect = null)
    {
        var q = TaskScheduler.Query(@"\", TaskName);
        if (!q.Exists)
            return (false, false, true, "没有自启任务（第一次启动需要一次 UAC 来创建它）");
        if (q.Enabled == false)
            return (false, false, true, "自启任务被禁用了（在任务计划程序里重新启用，或关一次/开一次自启）");

        // ⚠️ 任务指向**别的副本**时不许"将就着拉起来"（2026-10-30 真机实测）：
        //    任务里的 exe 不是用户双击的这一份时，拉起来的是那一份 —— 用户看到窗口
        //    正常打开，实际跑的是另一个目录（配置、HWiNFO、日志全跟着跑偏），
        //    而且他自己那份永远是"点不开的"。这时按"没有可用任务"处理：调用方退回
        //    runas，让当前这份以管理员起来，再由 RepairDeadEntry 把入口改回自己。
        //
        //    读不到任务动作（COM 拒绝访问）时不猜，保持旧行为。
        if (expect is not null)
        {
            var (target, _) = TaskScheduler.ActionCommand(@"\", TaskName);
            if (target is not null && !CopyIdentity.SamePath(target, expect.Exe))
            {
                return (false, false, true,
                    $"自启任务指向另一份副本（{target}），不拿它启动，改为让当前这份提权启动");
            }
        }

        var (ok, already, err) = TaskScheduler.Run(@"\", TaskName);
        if (ok) return (true, false, false, "已用最高权限任务启动（无 UAC）");
        if (already) return (false, true, false, "任务已在运行");
        return (false, false, false, "任务启动失败：" + err);
    }

    /// <summary>
    /// 「换代」用：让 Highest 任务起一个**提权**实例，并且让它把界面开到 <paramref name="page"/>。
    ///
    /// 这是「⚠ 需管理员」徽章零 UAC 的那条路。它和 <see cref="RunNow"/> 的区别只有一点：
    /// 任务传不了参数，所以"要开窗 / 开在哪一页"写进 <see cref="OpenRequest"/>。
    /// 新实例起来后：
    ///   · 端口还被本进程（只读）占着 → 走 HandoverPort 请我们退出，然后按纸条开窗；
    ///   · 端口已经空了（本进程刚好退了）→ 直接绑，按纸条开窗。
    /// 调用方**不要**自己退出：交接由新实例发起（与 `--takeover` 同一条路），
    /// 这样"新进程还没绑上端口"的那段空窗不会被暴露成一次启动失败。
    ///
    /// 返回 false 时调用方应退回 `runas`（任务不存在 / 被禁用 / 启动失败）。
    /// </summary>
    public static bool HandoverByTask(string? page, out string detail, AutostartCommand? expect = null)
    {
        var q = TaskScheduler.Query(@"\", TaskName);
        if (!q.Exists)
        {
            detail = "没有自启任务（首次需要一次 UAC 来创建）";
            return false;
        }
        if (q.Enabled == false)
        {
            detail = "自启任务被禁用了";
            return false;
        }

        // 同 <see cref="RunNow"/>：任务指向别的副本时，代启起来的是那一份，
        // 不是眼前这个只读实例。这里必须让调用方退回 runas，否则会「提权了但换了个目录」。
        if (expect is not null)
        {
            var (target, _) = TaskScheduler.ActionCommand(@"\", TaskName);
            if (target is not null && !CopyIdentity.SamePath(target, expect.Exe))
            {
                detail = $"自启任务指向另一份副本（{target}），改用 runas 提权当前这份";
                return false;
            }
        }

        if (!OpenRequest.Write(page))
        {
            detail = "写不了开窗请求文件（%LOCALAPPDATA% 不可写）";
            return false;
        }

        var (ok, already, err) = TaskScheduler.Run(@"\", TaskName);
        if (!ok && !already)
        {
            OpenRequest.Discard();   // 回滚：别留下"下次启动莫名开窗"
            detail = "任务启动失败：" + err;
            return false;
        }

        detail = already
            ? "已有一个提权实例在跑，正在请它接管并开窗（无 UAC）"
            : "已请最高权限任务接管（无 UAC），新实例会把界面开回来";
        return true;
    }

    // ------------------------------------------------------------ 开 / 关

    /// <summary>
    /// 开或关自启。返回 `(成功, 说明)` —— 说明是给用户看的，成功也要说清用了哪套机制。
    ///
    /// 开：只认计划任务。失败就把**真实原因**带回去（"需要管理员" / COM 报错），
    ///     绝不悄悄退化成 Run 项假装成功。
    /// 关：任务 + Run 项都清。
    /// </summary>
    public static (bool Ok, string Detail) Set(bool on, AutostartCommand cmd)
    {
        if (!on)
        {
            var taskOk = TaskScheduler.Delete(@"\", TaskName, out var taskErr);
            var regOk = DeleteRun();
            if (taskOk && regOk)
                return (true, "已关闭：计划任务与 Run 项都已清除");

            var why = new List<string>();
            if (!taskOk) why.Add("计划任务删除失败：" + taskErr);
            if (!regOk) why.Add("Run 项删除失败（可能被安全软件保护）");
            return (false, string.Join("；", why));
        }

        if (!IsAdmin())
            return (false, "需要管理员权限：自启任务必须以「最高权限」注册，否则开机拉起来的后端"
                           + "写不了 EC。重启一次控制中心并同意 UAC（程序会自己请求提权），"
                           + "或双击 run-as-admin.cmd，然后再开自启。");

        if (!TaskScheduler.Register(@"\", TaskName, BuildTaskXml(cmd),
                                    $"{Environment.UserDomainName}\\{Environment.UserName}",
                                    out var err))
            return (false, "计划任务注册失败：" + err);

        // 回读确认：COM 回 S_OK 但任务不存在的情况真出现过
        if (!TaskScheduler.Query(@"\", TaskName).Exists)
            return (false, "注册返回成功但查不到任务（计划任务服务异常？）");

        DeleteRun();          // 任务已经能拉起，别留 Run 项造成双启
        return (true, "已开启：登录时以最高权限静默启动（计划任务 · 无控制台窗口）");
    }

    /// <summary>
    /// 启动时的自启维护：
    ///   · 用户从没关过（`ui.autostart_off` 不存在）且当前没开 → 打开；
    ///   · 已是计划任务但还留着 Run 项 → 清掉（避免双启）。
    ///
    /// ⚠️ 用户**关过一次**就永不再自动打开 —— 那是明确意图，不能被"默认开启"推翻。
    /// ⚠️ 这里只处理"没开"和"清理遗留"，不重写已有的任务 —— 否则每次启动都会把
    ///    入口改指向本程序，早先实现还活着的时候等于偷偷换了开机程序。
    /// </summary>
    public static (bool Changed, string Detail) EnsureDefault(bool userTurnedOff,
                                                              AutostartCommand cmd)
    {
        var legacy = CleanupLegacy();
        var (changed, detail) = EnsureDefaultCore(userTurnedOff, cmd);
        if (legacy is null) return (changed, detail);
        return (true, legacy + "；" + detail);
    }

    /// <summary>
    /// 清掉改名前的自启残留（旧计划任务 + 旧 Run 项）。返回 null = 本来就没有。
    /// 删任务要管理员，非管理员下失败就让它留着（下次以管理员启动再清）。
    /// </summary>
    internal static string? CleanupLegacy()
    {
        var cleaned = new List<string>();

        if (ReadRun(LegacyRunName) is not null && DeleteRun(LegacyRunName))
            cleaned.Add("旧 Run 项");

        foreach (var name in LegacyTaskNames)
        {
            if (!TaskScheduler.Query(@"\", name).Exists) continue;
            if (TaskScheduler.Delete(@"\", name, out _))
                cleaned.Add($"旧计划任务 {name}");
        }

        return cleaned.Count == 0 ? null : "已清理改名前的自启残留：" + string.Join("、", cleaned);
    }

    private static (bool Changed, string Detail) EnsureDefaultCore(bool userTurnedOff,
                                                                    AutostartCommand cmd)
    {
        if (TaskScheduler.Query(@"\", TaskName).Exists)
        {
            // 任务在，不代表它还起得来 —— 升级可能把它的目标 exe 删了（见 RepairDeadEntry）
            var repaired = RepairDeadEntry(cmd);
            if (repaired is not null) return (true, repaired);

            // 老任务缺「崩溃后自动重启」（2026-11-02 加的设置）—— 补齐一次，之后幂等跳过
            var restartFixed = RepairRestartPolicy(cmd);
            if (restartFixed is not null) return (true, restartFixed);

            if (ReadRun() is not null)
            {
                DeleteRun();
                return (true, "计划任务已在，清掉遗留的 Run 项（避免双启）");
            }
            return (false, "已是计划任务，无需改动");
        }

        if (userTurnedOff) return (false, "用户关过自启，跳过");

        if (ReadRun() is not null)
        {
            // 老的 Run 项：只清理不接管 —— 它现在指向早先实现，改它 = 偷换开机程序
            return (false, "发现旧的 Run 项（属于早先实现），保留不动");
        }

        return Set(true, cmd);
    }

    /// <summary>
    /// 修一次"任务还在、但入口已经不该用"的自启入口。返回 null = 不用管。
    ///
    /// 为什么需要：旧版本的计划任务可能指向一个**已经不存在**的入口 —— 任务照样
    /// "存在、已启用"，开机却什么都不会起。用户看不到任何报错，只会觉得"自启莫名其妙坏了"。
    /// 而 <see cref="EnsureDefault"/> 的原则是**不重写已有任务**，两边一撞，就只能
    /// 在这里开一个很窄的口子。
    ///
    /// 判定边界（都是有意为之）：
    ///   · 认三种目标：① 那个文件已经不存在；② 文件名是已删除的静默启动器；
    ///     ③ **同一个程序的另一份副本**（见 <see cref="CopyIdentity.IsSameProductCopy"/>，
    ///     典型是安装版与绿色版并存 —— 先启动的那份会把入口写成自己，之后装的这份
    ///     每次双击都被顶掉）。指向别的**程序**一律不动 —— 那可能是用户自己改的。
    ///   · 要管理员：重新注册 HighestAvailable 任务只有管理员能成；失败就原样留着，
    ///     下次以管理员身份启动再试（幂等，不会每秒钟重试）。
    ///   · 必须回读：COM 回 S_OK 但任务没变的情况出现过，那时报"已修复"就是撒谎。
    /// </summary>
    public static string? RepairDeadEntry(AutostartCommand cmd)
    {
        if (!IsAdmin()) return null;

        var (exe, _) = TaskScheduler.ActionCommand(@"\", TaskName);
        if (string.IsNullOrEmpty(exe)) return null;                       // 读不到就别猜
        if (CopyIdentity.SamePath(exe, cmd.Exe)) return null;

        var gone = !File.Exists(exe);
        var legacyLauncher = Path.GetFileName(exe)
                                 .Equals("QControl.Silent.exe", StringComparison.OrdinalIgnoreCase);
        // 同产品的另一份副本：文件活得好好的，但那份不是"我"。
        // 不认这一条的话，先启动的那份会**永久**占住入口（另一份永远拿不回来）。
        var otherCopy = CopyIdentity.IsSameProductCopy(exe, cmd.Exe);
        if (!gone && !legacyLauncher && !otherCopy) return null;

        if (!TaskScheduler.Register(@"\", TaskName, BuildTaskXml(cmd), Who(), out var err))
            return $"自启任务指向 {exe}（不是本程序这份），但改不回来：{err}";

        var (now, _) = TaskScheduler.ActionCommand(@"\", TaskName);
        if (!CopyIdentity.SamePath(now, cmd.Exe))
            return $"自启任务改指向 {cmd.Exe} 后回读不一致（现值：{now ?? "读不到"}）";

        return otherCopy
            ? $"自启任务原本指向另一份副本（{exe}），已改指向当前这份（{cmd.Exe}）"
            : $"自启任务原本指向已废弃的 {Path.GetFileName(exe)}，已改指向 {Path.GetFileName(cmd.Exe)}";
    }

    /// <summary>
    /// 给**已有的**自启任务补上「崩溃后自动重启」（2026-11-02）。
    ///
    /// 为什么需要单开一条：这次加的是 XML 里的 `<RestartOnFailure>`，而 <see cref="EnsureDefault"/>
    /// 的原则是**不重写已有任务**（见上面 <see cref="RepairDeadEntry"/> 的注释）。10-31 之前
    /// 注册过任务的机器（包括本机）永远拿不到这条设置，用户也看不到任何提示 ——
    /// 只会觉得"崩了一次之后就再也没自启了"。
    ///
    /// 判定边界：
    ///   · 只在**管理员**下动手 —— 重注册 HighestAvailable 任务只有管理员能成；
    ///   · 读不到设置的（普通权限拿不到 `Definition`）当作"不用管"，不猜、不整节重写；
    ///   · 已达标（`RestartCount >= TaskRestartCount`）直接跳过，**幂等**，成功一次就不再碰；
    ///   · 必须回读确认：COM 回 S_OK 但设置没变的情况本项目在 `RegisterTask` 上踩过。
    /// </summary>
    internal static string? RepairRestartPolicy(AutostartCommand cmd)
    {
        if (!IsAdmin()) return null;

        var (count, _) = TaskScheduler.RestartPolicy(@"\", TaskName);
        if (count is null) return null;                    // 读不到 ≠ 需要修
        if (count >= TaskRestartCount) return null;        // 已经是新策略

        // 注册失败就静默放弃：下次以管理员启动会再试一次（这里的"静默"是有意的，
        // 否则每次开机都要在日志里刷一行"补设置失败"，而它并不影响任何现有功能）。
        if (!TaskScheduler.Register(@"\", TaskName, BuildTaskXml(cmd), Who(), out _))
            return null;

        var (now, _) = TaskScheduler.RestartPolicy(@"\", TaskName);
        if (now is null || now < TaskRestartCount) return null;

        return $"自启任务补上「失败自动重启」（{TaskRestartCount} 次 / {TaskRestartIntervalMin} 分钟）";
    }

    // ------------------------------------------------------------ 计划任务 XML
    /// <summary>
    /// 生成任务 XML。字段与 `tray._TASK_XML` 一字不差（除了变量），
    /// 两个版本注册出来的任务必须长得一样，否则 P7 对比时会出现"行为不同但说不清哪里不同"。
    ///
    /// `runLevel` 只给自检用：非管理员不可能注册 HighestAvailable，而"XML 能不能被
    /// Windows 解析"这件事又必须验证，所以自检会用 LeastPrivilege 注册一次临时任务。
    /// 生产路径永远传默认值。
    /// </summary>
    public static string BuildTaskXml(AutostartCommand cmd,
                                      string runLevel = "HighestAvailable") => $"""
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Q-Control 控制中心（登录自启，静默进托盘）</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{Xml(Who())}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{Xml(Who())}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>{runLevel}</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <RestartOnFailure>
      <Interval>PT{TaskRestartIntervalMin}M</Interval>
      <Count>{TaskRestartCount}</Count>
    </RestartOnFailure>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Xml(cmd.Exe)}</Command>
      <Arguments>{Xml(cmd.Args)}</Arguments>
      <WorkingDirectory>{Xml(cmd.WorkingDir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
""";

    private static string Who()
    {
        var user = Environment.UserName;
        var domain = Environment.UserDomainName;
        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    private static string Xml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;")
        .Replace(">", "&gt;").Replace("\"", "&quot;");

    // ------------------------------------------------------------ 注册表 Run 项
    //
    // ⚠️ 本文件是唯一允许**写**注册表的地方（Reg.cs 保持只读，好让
    //    "那个文件永远不会改系统"这件事一眼可查）。

    /// <summary>读 Run 项（默认值名 = QControl）。</summary>
    public static string? ReadRun(string valueName = AppName)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(valueName)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写 Run 项（只在诊断/自检里用；正常路径走计划任务）。</summary>
    public static bool SetRun(AutostartCommand cmd, string valueName = AppName)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            k.SetValue(valueName, cmd.CommandLine, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>删 Run 项。本来就不存在也算成功（达成目的）。</summary>
    public static bool DeleteRun(string valueName = AppName)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(valueName, throwOnMissingValue: false);
            return ReadRun(valueName) is null;
        }
        catch
        {
            return ReadRun(valueName) is null;
        }
    }
}
