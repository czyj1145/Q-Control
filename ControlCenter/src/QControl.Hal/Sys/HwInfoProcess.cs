using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using QControl.Hal.HwInfo;
using Microsoft.Win32;

namespace QControl.Hal.Sys;

/// <summary>
/// HWiNFO64 进程管理：找进程 / 拉起 / 强重启 / 卡住自愈 / 找机器上别的安装 /
/// 改 INI 开共享内存 / 写 INI 预设。
///
/// **为什么启动这么麻烦（2026-10-13 实测结论，别"简化"）**：
///   `HWiNFO64.exe` 的清单是 `requireAdministrator` + `uiAccess="true"`。
///   uiAccess 应用**不能**用 CreateProcess 起 —— 即使父进程已经是管理员，
///   也返回 `ERROR_ELEVATION_REQUIRED(740)`。必须走 ShellExecute（AppInfo 代启）。
///   再叠加系统默认策略 `EnableSecureUIAPaths=1`：uiAccess 应用还必须待在
///   安全位置（Program Files / Windows / System32），否则照样起不来
///   ⇒ 所以有「复制到 %ProgramFiles%\ControlCenter-HWiNFO」这一级兜底。
///
/// **归属规则（2026-10-14 用户定）**：用户自己开的 HWiNFO 与控制中心互相独立 ——
///   平时不杀、不藏、不删托盘图标。只有 **我们自己拉起的**那只（<see cref="_pid"/>）
///   才归我们管；「修复」按钮（<see cref="ForceRestart"/>）是唯一会杀掉全部实例的
///   入口，且只由用户点击触发。
/// </summary>
public static class HwInfoProcess
{
    public const string ExeName = "HWiNFO64.exe";
    public const string IniName = "HWiNFO64.INI";

    /// <summary>兜底副本目录名（`%ProgramFiles%\ControlCenter-HWiNFO`）。</summary>
    private const string StageName = "ControlCenter-HWiNFO";

    /// <summary>起完等这么久再确认进程真活着（有些启动方式会「秒退」）。</summary>
    private const int AliveWaitMs = 2500;

    /// <summary>收进程后等它真退干净的上限。</summary>
    private const int KillWaitMs = 12_000;

    // ---- 自救的节流（照搬早先实现：12 小时过期是**周期性**事件）----
    private const int RestartMax = 2;
    private const double RestartWindowSec = 86_400;
    private const double RestartCooldownSec = 1_800;

    private static readonly object Gate = new();
    private static readonly object RestartGate = new();

    private static int? _pid;             // 我们拉起的那只（退出时按 PID 收，别误杀用户的）
    private static double _launchAt;      // 最近一次成功拉起（秒）
    private static double _launchTryAt;   // 最近一次**发起**拉起（秒，含还在进行中的那次）
    private static int _restartTries;     // 本进程内已自救次数
    private static double _restartLast;   // 上次自救时刻（秒）

    // ---- 拉起退避（2026-10-22：按需模式把「一发 UAC」变成「一小时好几发」）----
    //
    // HWiNFO64.exe 的清单是 requireAdministrator，所以**每次 ShellExecute 拉起都是
    // 一发 UAC**（父进程已经提权时不弹 —— 凌晨日志里 2.6 秒拉起成功就是证据）。
    // 常驻时代开机只弹一次，问题不明显；改成按需（默认）之后「收掉 → 再拉起」是
    // 家常便饭，再叠上看门狗的失败阶梯（6s / 20s / 2min / 10min，而每次"重新需要"
    // 都会把计数清零重来）⇒ **一次打开界面最多能弹四五发**。所以立规矩：
    //   ① 用户点了「否」（winerror=1223）→ 长冷却，且**立刻结束本次回退链**，
    //      不许再换条路弹第二发；
    //   ② 别的失败 → 指数退避，上限 10 分钟；
    //   ③ 成功 → 立刻清零。
    // 「修复 HWiNFO」走 force=true 突破：那是用户明确要求再试一次。
    private const double RefuseCooldownSec = 600;
    private const double BackoffBaseSec = 60;
    private const double BackoffMaxSec = 600;

    private static double _blockUntil;    // 这个时刻之前不再尝试拉起
    private static string? _blockWhy;     // 暂缓原因（人话，诊断口与日志用）
    private static int _failStreak;       // 连续失败次数（成功即清零）

    /// <summary>我们拉起的那只的 PID（从没拉过 / 已退出为 null）。诊断口 `ours_pid` 用。</summary>
    public static int? OurPid { get { lock (Gate) return _pid; } }

    /// <summary>最近一次启动走通/失败的方式（诊断口 `launch` 字段）。</summary>
    public static string? LastLaunchDetail { get; private set; }

    /// <summary>
    /// 此刻是不是「先别再试」。(LeftSec=0, Why=null) = 没有暂缓。
    /// 诊断口用：用户问「为什么这一行空了」时，得能答出"10 分钟前你点了否"。
    /// </summary>
    public static (double LeftSec, string? Why) LaunchBlock
    {
        get
        {
            lock (Gate)
            {
                var left = _blockUntil - Now();
                return left > 0 ? (left, _blockWhy) : (0, null);
            }
        }
    }

    /// <summary>拉起成功：退避清零。</summary>
    private static void NoteLaunchOk()
    {
        lock (Gate) { _blockUntil = 0; _blockWhy = null; _failStreak = 0; }
    }

    /// <summary>拉起失败：按原因设一段「别再打扰」的时间。</summary>
    private static void NoteLaunchBlocked(string why, double sec)
    {
        lock (Gate) { _blockUntil = Now() + sec; _blockWhy = why; }
    }

    // ---- 拉起耗时测量（2026-10-21：为「要不要改成按需拉起」提供数字）----
    private static readonly Stopwatch ReadySw = new();

    /// <summary>
    /// 最近一次「发起拉起 → 共享内存出数」的耗时（毫秒）。
    /// 判断按需拉起时用户要等多久就靠它；没拉过 / 一直没就绪为 null。
    /// </summary>
    public static double? LastReadyMs { get; private set; }

    /// <summary>开始计时（在真正 ShellExecute 之前调）。</summary>
    public static void NoteLaunchStarted()
    {
        lock (Gate)
        {
            LastReadyMs = null;
            _launchTryAt = Now();
            ReadySw.Restart();
        }
    }

    /// <summary>
    /// 是不是「刚拉起、共享内存还来不及出数」的那一段。
    ///
    /// 依据是实测的拉起耗时：ShellExecute → 共享内存出数要 **3.6 ~ 7.4 秒**
    /// （`/api/debug/hwinfo` 的 `launch_ms`，本机 2026-11-22 连测 6 次）。在这段里
    /// 进程已经在跑了、共享内存却还没数据，任何"它在跑但没数据"的判据都必然成立 ——
    /// 设置页那行提示于是每次开窗都冒出来、过几秒又消失，用户看到的就是
    /// 「总有一行 HWiNFO 没读数」。
    ///
    /// 窗口给 25 秒：够覆盖慢盘 / 冷启动的拉起（最慢一次实测 7.4 s），
    /// 又不至于真出问题时一直不说。**只按时间算，不看进程状态** ——
    /// 拉起失败时 <see cref="_launchAt"/> 不会更新，但 <see cref="_launchTryAt"/> 会，
    /// 那 25 秒里同样不该报"没数据"（那时是"没起来"，另一条路会报）。
    /// </summary>
    public static bool InLaunchWarmup(double sec = 25)
    {
        lock (Gate)
        {
            var mark = Math.Max(_launchAt, _launchTryAt);
            return mark > 0 && Now() - mark < sec;
        }
    }

    /// <summary>
    /// 共享内存第一次出数时调一下，停表。重复调用只认第一次（后续是自愈/看门狗的检测）。
    /// </summary>
    public static void NoteReady()
    {
        lock (Gate)
        {
            if (!ReadySw.IsRunning) return;
            ReadySw.Stop();
            LastReadyMs = ReadySw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// 所有 HWiNFO 实例的内存/CPU 实测（`debug/hwinfo` 的 `mem` 段）。
    ///
    /// 为什么单列：判断「整体内存」必须拿**真进程**的数字，不能靠估算。
    /// `ws_mb`=工作集（任务管理器「内存」列）、`priv_mb`=专用工作集（更接近真实占用）。
    /// </summary>
    public static JsonObject MemoryReport()
    {
        var arr = new JsonArray();
        double ws = 0, priv = 0;

        foreach (var pid in Pids())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Refresh();
                var w = p.WorkingSet64 / 1048576.0;
                var v = p.PrivateMemorySize64 / 1048576.0;
                ws += w;
                priv += v;
                arr.Add(new JsonObject
                {
                    ["pid"] = pid,
                    ["ours"] = pid == _pid,
                    ["ws_mb"] = Math.Round(w, 1),
                    ["priv_mb"] = Math.Round(v, 1),
                    ["cpu_s"] = Math.Round(p.TotalProcessorTime.TotalSeconds, 1),
                });
            }
            catch { /* 进程刚退：跳过 */ }
        }

        return new JsonObject
        {
            ["instances"] = arr,
            ["n"] = arr.Count,
            ["total_ws_mb"] = Math.Round(ws, 1),
            ["total_priv_mb"] = Math.Round(priv, 1),
        };
    }

    // ================================================================ 路径

    /// <summary>
    /// HWiNFO 目录（含 `HWiNFO64.exe`）。找不到返回 null。
    ///
    /// 顺序（2026-10-30 用户定，别再加"共享位置"之类的花活）：
    ///   ① <b>程序自己这边优先</b>：从程序目录向上找 `HWiNFO`（安装器建的、就在安装目录里）
    ///      或 `tools/HWiNFO`（仓库开发结构）；
    ///   ② 再从 cwd 向上找同样的两级（从命令行/脚本起时 cwd 才是用户眼里的"这里"）；
    ///   ③ `CC_HWI_DIR` 环境变量（自检 / 换机部署，显式覆盖）；
    ///   ④ 都没有 → 返回 null，由界面如实说"没找到"。
    ///
    /// ⚠️ **为什么不做跨副本共享目录**（用户 2026-10-30 明确否掉）：那种位置要用户自己
    ///    建文件夹、还得知道路径怎么写，而安装器本来就会在安装目录里建好 `HWiNFO\` 并放
    ///    一张说明 —— 拖进去就行。多一个位置只是多一个会失配的地方。
    /// ⚠️ 仍然**不许**去"认另一个副本"（比如把绿色版指到安装目录那份）：删掉那个副本，
    ///    这一份立刻少六项数据（PCH 温度、CPU/GPU 电压、显存结温、PL1·PL2、硬盘 SMART），
    ///    而界面上只是"那几行不见了"，用户根本看不出原因。
    /// </summary>
    public static string? Dir()
    {
        // ① 程序自己这边（安装目录 / 绿色版 app\ 旁边的 HWiNFO）
        if (ProbeUpward(AppContext.BaseDirectory) is { } mine) return mine;

        // ② cwd（从仓库或脚本启动时，用户眼里的"当前目录"）
        string cwd;
        try { cwd = Directory.GetCurrentDirectory(); } catch { cwd = ""; }
        if (cwd.Length > 0 && ProbeUpward(cwd) is { } here) return here;

        // ③ 显式覆盖（自检 / 换机部署）
        var env = Environment.GetEnvironmentVariable("CC_HWI_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, ExeName)))
            return Path.GetFullPath(env);

        return null;
    }

    /// <summary>
    /// 从 <paramref name="start"/> 往上最多 10 层，每层试 `HWiNFO\` 与 `tools\HWiNFO\`。
    ///
    /// ⚠️ 层数要够：开发时程序跑在 `src\App\bin\Release\net10.0-windows` 下，到仓库根有
    ///    6~8 层。只找 5 层会"找不到"，看起来像 exe 丢了。
    /// </summary>
    private static string? ProbeUpward(string start)
    {
        DirectoryInfo? d;
        try { d = new DirectoryInfo(Path.GetFullPath(start)); }
        catch { return null; }

        for (var i = 0; i < 10 && d is not null; i++, d = d.Parent)
        {
            var flat = Path.Combine(d.FullName, "HWiNFO");
            if (File.Exists(Path.Combine(flat, ExeName))) return flat;

            var nested = Path.Combine(d.FullName, "tools", "HWiNFO");
            if (File.Exists(Path.Combine(nested, ExeName))) return nested;
        }
        return null;
    }

    private static string StagedDir()
        => Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", StageName);

    private static string StagedExe() => Path.Combine(StagedDir(), ExeName);

    // ================================================================ 进程枚举

    /// <summary>
    /// 所有 `HWiNFO64.exe` 的 PID。
    ///
    /// ⚠️ 不用 `tasklist /FI`（早先实现实测漏报）；这里用 `Process.GetProcessesByName`
    ///     —— 它读的是系统进程快照，不做过滤，口径与全量 CSV 一致。
    /// </summary>
    public static int[] Pids()
    {
        try
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName));
            var ids = new int[procs.Length];
            for (var i = 0; i < procs.Length; i++)
            {
                ids[i] = procs[i].Id;
                procs[i].Dispose();
            }
            return ids;
        }
        catch
        {
            return [];
        }
    }

    public static bool IsRunning() => Pids().Length > 0;

    /// <summary>现在跑着的是不是**我们自己拉起的**那只。用户那份不算。</summary>
    public static bool OursRunning() => _pid is { } id && Pids().Contains(id);

    /// <summary>共享内存**真能出数**（段在 **且** 有新鲜读数）。残留段不算。</summary>
    public static bool SmReady()
    {
        try { return SharedMemoryFrame.Read(requireFresh: true) is not null; }
        catch { return false; }
    }

    // ================================================================ 启动

    /// <summary>
    /// 起 HWiNFO。返回 (ok, detail, pid)。
    ///
    /// 顺序（每级失败原因都带出来，方便从日志定位）：
    ///   1. **安全目录副本已存在** → 直接 ShellExecute 它（uiAccess 策略下最稳）；
    ///   2. 源目录**不在**安全位置（绿色版解压在 Downloads 之类的地方）→
    ///      先复制到 `%ProgramFiles%\ControlCenter-HWiNFO` 再起（只在已提权时做，
    ///      否则写 Program Files 注定失败）；
    ///   3. 原地 CreateProcess → 原地 ShellExecute（安全检查通过时才走得到）；
    ///   4. 全不行 → 如实返回，把每一级的原因串起来。
    ///
    /// ⚠️ 启动前**强制回写预置 INI**：HWiNFO 退出时会按自己的键集重写它，
    ///    `SensorsSM` / `ShowWelcomeAndProgress` 一旦被删，下次就会弹窗口 + 共享内存不出数。
    /// </summary>
    public static (bool Ok, string Detail, int? Pid) Launch(bool force = false)
    {
        // ⚠️ 必须互斥：两个请求同时点「修复」会各起一个实例，第二个白忙一场
        //    （HWiNFO 单实例），还会把 `_pid` 指到先退出的那个上。
        //    早先实现用 `_proc_lock` 干同一件事；这里只管**启动**，不等共享内存。
        lock (Gate)
        {
            // ⚠️ 暂缓期内一律不尝试。没有它就是「UAC 弹个不停」：
            //    看门狗的失败阶梯 6s/20s/2min/10min 每次"重新需要数据"都会清零重来。
            if (!force && _blockUntil > Now())
            {
                var left = _blockUntil - Now();
                ReadySw.Stop(); LastReadyMs = null;
                return (false,
                    $"{_blockWhy} —— {left:0}s 内不再尝试拉起 HWiNFO"
                    + "（点「修复 HWiNFO」可立刻重试）", null);
            }

            var (ok, detail, pid) = LaunchCore();
            LastLaunchDetail = detail;
            if (ok)
            {
                NoteLaunchOk();
            }
            else
            {
                // 拉起失败就停表：之后即便别处的实例让共享内存出数，也不算「拉起耗时」
                ReadySw.Stop(); LastReadyMs = null;
                // 指数退避：60 / 120 / 240 / … 上限 10 分钟（取消的冷却已在回退链里设过，
                // 这里会被它的更长值覆盖前先判断一下，别把 10 分钟缩成 60 秒）
                _failStreak++;
                var backoff = Math.Min(BackoffBaseSec * Math.Pow(2, _failStreak - 1), BackoffMaxSec);
                if (Now() + backoff > _blockUntil)
                    NoteLaunchBlocked("上次拉起失败：" + detail, backoff);
            }

            // ⚠️ 归属**必须在这里记**（2026-10-20 修）。以前只有「修复」和自救两条
            //    路径会 `_pid = pid`，而正常的开机 ensure 把 launch 的返回值直接丢掉了
            //    ⇒ `_pid` 永远是 null ⇒ 那只恒等于「别人的」⇒ 隐身看门狗一次也不会动手
            //    （真机现象：HWiNFO 明明是我们拉起的，托盘图标却一直在）。
            //    标志放在唯一出口赋值，更不容易漏。
            if (ok && pid is { } newPid)
            {
                _pid = newPid;
                _launchAt = Now();
            }
            return (ok, detail, pid);
        }
    }

    private static (bool Ok, string Detail, int? Pid) LaunchCore()
    {
        NoteLaunchStarted();     // 拉起耗时从这一刻开始算（含 INI 回写 + ShellExecute）
        var dir = Dir();
        if (dir is null)
            return (false,
                "没找到 " + ExeName + "（把 HWiNFO64.exe 放进程序目录下的 HWiNFO 文件夹即可）", null);

        var exe = Path.Combine(dir, ExeName);
        if (!File.Exists(exe))
            return (false, "缺少 " + exe, null);

        var errs = new List<string>();
        // 启动前的 PID 快照：`runas` 提权启动时 `ShellExecuteEx` 常常**不给 hProcess**
        // （AppInfo 代启，SEE_MASK_NOCLOSEPROCESS 拿到的可能是 0），于是 pid 为 null、
        // `_pid` 记不上 ⇒ 那只永远算「别人的」，隐身看门狗一辈子不动手。
        // 用「启动前后差集」认领：只在**启动前一个都没有**时才成立（正常路径就是这样）。
        var prevPids = Pids();
        var (okIni, iniDetail) = WriteIniPreset(dir);
        if (!okIni) errs.Add(iniDetail);

        // ---- ① 安全目录副本已存在：刷新 exe / INI 后直接起它 ----
        var stagedDir = StagedDir();
        var stagedExe = StagedExe();
        if (File.Exists(stagedExe))
        {
            try
            {
                if (new FileInfo(stagedExe).Length != new FileInfo(exe).Length)
                    File.Copy(exe, stagedExe, overwrite: true);
                var (okIni2, d2) = WriteIniPreset(stagedDir);
                if (!okIni2) errs.Add(d2);
            }
            catch (Exception e)
            {
                errs.Add("刷新安全目录副本失败：" + e.Message);
            }

            var (ok, pid, err, cancelled) = ShellExec(stagedExe, stagedDir);
            pid ??= AdoptNewPid(prevPids, stagedExe);
            // `Alive(null)` 退化成"机器上有任意一只 HWiNFO 在跑"，所以拿不到句柄时
            // 只能说"确认到它在跑"，不能说"这是我拉起的那只" —— 否则日志里会留下
            // 一条查不下去的假记录，看门狗 / StopOurs 也都用不了（2026-11-23 审查）。
            var pidKnown = pid is not null;
            if (ok && Alive(pid))
                return (true,
                        "ShellExecute(runas)@" + stagedDir
                        + (pidKnown ? "" : "（拿不到进程句柄，只确认到有 HWiNFO 在跑）"),
                        pid);

            // ⚠️ 用户点了「否」就该到此为止：后面的 ②③④ 每一级都是再来一发 UAC。
            if (cancelled) return Refused();

            errs.Add((err ?? "进程没活下来") + "（安全目录副本）");
            return (false, string.Join(" | ", errs), null);
        }

        // ---- ② 源不在安全位置 → 先复制到安全目录再起（uiAccess 的硬要求）----
        // HWiNFO64.exe 的清单是 requireAdministrator + uiAccess：系统默认策略
        // （EnableSecureUIAPaths=1）只允许它从 Program Files / Windows / System32 起来。
        // 安装目录本来就在安全位置（走不到这里）；绿色版解压在 Downloads 之类的地方时，
        // 原地的 Popen 是 740、ShellExecute 也白给 —— **注定失败**。这里先跳板一次，
        // 别让下面两级白等 5 秒，还在日志里留下两条误导性的失败原因。
        //
        // ⚠️ 未提权时**整段跳过**：把文件写进 %ProgramFiles% 本身就要管理员，
        //    这一步注定失败 —— 而它前面还挂着一次 runas，等于白弹一发 UAC。
        //    已提权才走（那时从头到尾一发 UAC 都不会弹，回退链随便走完）。
        if (!Elevation.IsElevated())
        {
            errs.Add("未提权：跳过「复制到安全目录」（写 %ProgramFiles% 需要管理员）");
        }
        else if (!IsSecure(dir))
        {
            try
            {
                var dst = StageToSecure(dir);
                var (ok4, pid4, err4, cancelled4) = ShellExec(Path.Combine(dst, ExeName), dst);
                pid4 ??= AdoptNewPid(prevPids, dst);
                if (ok4 && Alive(pid4))
                    return (true, "ShellExecute(runas)@" + dst, pid4);
                if (cancelled4) return Refused();
                errs.Add((err4 ?? "进程没活下来") + "（安全目录）");
            }
            catch (Exception e)
            {
                errs.Add("复制到安全目录失败：" + e.Message);
            }
        }

        // ---- ③ CreateProcess（需要「父进程管理员 + exe 在安全目录」，本机不满足）----
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var p = Process.Start(psi);
            if (p is not null)
            {
                var pid = p.Id;
                // ⚠️ 这里不能调 Alive()：它复用 Pids() 按 PID 反查，而 Popen 路径
                //    的判据是 `poll() is None`（早先实现同口径）。秒退的进程
                //    HasExited 立刻为 true，不必等满 2.5s。
                Thread.Sleep(AliveWaitMs);
                var alive = !p.HasExited;
                p.Dispose();
                if (alive) return (true, "Popen", pid);
                errs.Add("Popen：进程没活下来");
            }
            else
            {
                errs.Add("Popen：进程没起来");
            }
        }
        catch (Exception e)
        {
            errs.Add("Popen：" + e.Message);
        }

        // ---- ④ 原地 ShellExecute(runas)：uiAccess 清单**只能**走这条 ----
        var (ok3, pid3, err3, cancelled3) = ShellExec(exe, dir);
        pid3 ??= AdoptNewPid(prevPids, exe);
        if (ok3 && Alive(pid3))
            return (true, "ShellExecute(runas)", pid3);
        if (cancelled3) return Refused();          // 不再往下弹第二发
        errs.Add(err3 ?? "ShellExecute(runas)：进程没活下来");

        return (false, string.Join(" | ", errs), null);
    }

    /// <summary>
    /// 从「启动后新出现的 PID」里认领我们刚拉起的那只。
    ///
    /// ⚠️ 先按**恰好一个新增**认领（最常见）；多出来说明期间有别的会话也在起（例如用户
    ///    自己双击了 HWiNFO），这时按"映像就是我们刚放的那份、启动时刻就在刚才"再筛一遍，
    ///    仍要求**恰好一只**命中 —— 两只都命中同样说不清哪只是我们的，宁可记 null
    ///    （当作"别人的"，什么都不碰），也不猜。那违反归属铁律。
    /// </summary>
    private static int? AdoptNewPid(int[] before, string expectedExe)
    {
        try
        {
            var added = Pids().Except(before).ToArray();
            if (added.Length == 1) return added[0];
            if (added.Length == 0) return null;

            var hits = added.Where(pid => LaunchedFrom(pid, expectedExe)).ToArray();
            return hits.Length == 1 ? hits[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 这只进程是不是"刚由我们这条路径起起来的"：启动时刻在最近 20 秒内，且映像就是
    /// 我们复制/指定的那个 exe。读不到映像（跨权限）就当不是 —— 认错比认不到更糟。
    /// </summary>
    private static bool LaunchedFrom(int pid, string expectedExe)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (DateTime.Now - p.StartTime > TimeSpan.FromSeconds(20)) return false;
            var path = p.MainModule?.FileName;
            if (path is null) return false;
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedExe),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>起完等一会儿，确认进程真的活着。有 pid 就必须验 pid（别只看"有同名进程"）。</summary>
    private static bool Alive(int? pid)
    {
        Thread.Sleep(AliveWaitMs);
        if (pid is null) return IsRunning();          // 拿不到 hProcess 时的旧行为
        return Pids().Contains(pid.Value);
    }

    // ================================================================ 重启 / 收尾

    /// <summary>
    /// **用户点了「修复」才走这里**：杀掉**所有** HWiNFO 实例（含用户手动开的），
    /// 再起一个我们自己的。绝不要自动调用。
    ///
    /// 为什么需要它：HWiNFO 同时只允许一个实例真正工作 —— 别的份在跑时
    /// （比如图吧工具箱里那份，没开共享内存），我们并排拉起的那只会起不来 /
    /// 不出共享内存（2026-10-14 实测）。这时只有用户授权我们杀掉它，数据才回得来。
    ///
    /// 返回 `{ok, killed:[pid…], detail}`（与 `hwinfo.force_restart` 同构）。
    /// </summary>
    public static JsonObject ForceRestart(int waitSec = 60)
    {
        var before = Pids();
        foreach (var pid in before)
            KillTree(pid);

        var t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < KillWaitMs)
        {
            if (Pids().Length == 0) break;
            Thread.Sleep(500);
        }

        // ⚠️ 收不干净就**不要**再起第二个（2026-10-20 实测踩到）：HWiNFO 是
        //    requireAdministrator 的，非管理员会话调 `taskkill` 会被拒（拒绝访问）。
        //    以前这里不管死活都往下走 Launch，结果是「第二份起不来」→ 返回
        //    `进程没活下来`，而**第一份被这次调用唤起了传感器窗口**（单实例程序收到
        //    二次启动会把自己的主窗口调出来）—— 用户看到一个凭空冒出来的 HWiNFO 窗口。
        //    宁可如实报「没权限」，也不要制造一个露脸的实例。
        var survivors = Pids();
        if (survivors.Length > 0)
        {
            var alive = new JsonArray();
            foreach (var s in survivors) alive.Add(JsonValue.Create(s));
            return new JsonObject
            {
                ["ok"] = false,
                ["killed"] = new JsonArray(),          // 一个都没结束掉，就别报"已杀"
                ["survivors"] = alive,
                ["detail"] = "结束不了现有实例（需要管理员权限），已放弃重启"
                             + "—— 再起一个只会把它自己的窗口唤出来。请用 run-tray.cmd 以管理员身份运行。",
            };
        }

        _pid = null;                                   // 旧实例全没了，归属清零

        // force=true：用户点了「修复」，那就是明确要现在再试一次，
        //            不受 UAC 取消/失败退避的暂缓约束（见 _blockUntil）。
        var (ok, detail, pid2) = Launch(force: true);   // Launch 内部已记录 LastLaunchDetail

        var killedArr = new JsonArray();
        foreach (var k in before) killedArr.Add(JsonValue.Create(k));

        if (!ok)
            return new JsonObject
            {
                ["ok"] = false,
                ["killed"] = killedArr,
                ["detail"] = detail,
            };

        _pid = pid2;
        _launchAt = Now();

        // 新实例又会弹窗口 + 挂托盘图标 ⇒ 起完立刻收（只看我们自己那只；归属规则见 HwInfoUi）
        HwInfoUi.HideWatchdog();

        var t1 = Environment.TickCount64;
        while (Environment.TickCount64 - t1 < waitSec * 1000L)
        {
            if (SmReady())
                return new JsonObject
                {
                    ["ok"] = true,
                    ["killed"] = killedArr,
                    ["detail"] = $"重启成功（{(Environment.TickCount64 - t1) / 1000.0:0.0}s 后共享内存就绪）",
                };
            Thread.Sleep(1000);
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["killed"] = killedArr,
            ["detail"] = $"{waitSec}s 内没等到共享内存",
        };
    }

    /// <summary>
    /// 「HWiNFO 在跑，但共享内存持续读不到」时的自救：杀掉**我们自己那只**再起一次。
    ///
    /// 已知成因：**免费版共享内存只连续导出 12 小时**，之后自动关闭，
    /// 只有重启 HWiNFO 能恢复。诊断口 `/api/debug/hwinfo/restart` 也走这条。
    ///
    /// 返回 (ok, detail)。
    /// </summary>
    public static (bool Ok, string Detail) RestartIfStalled(int waitSec = 60)
    {
        lock (RestartGate)
        {
            // ⚠️ 刚刚才拉起过（SM 还在枚举硬件）→ 不杀不重启。两个重叠的
            //    失败回调曾互相把对方刚拉起的实例当"停滞"杀掉（2026-10-14 修）。
            var now = Now();
            var sinceLaunch = now - _launchAt;
            if (_launchAt > 0 && sinceLaunch < RestartCooldownSec)
                return (false, $"{sinceLaunch:0}s 前才拉起过新实例，先等共享内存就绪，不急于重启");

            // ⚠️ 计数必须带时间窗：12 小时过期是**周期性**事件，只增不减的计数
            //    会让第二天再次过期时直接"已自救 2 次不再重启"，数据全空到手动重启。
            if (_restartLast > 0 && now - _restartLast > RestartWindowSec)
                _restartTries = 0;
            if (_restartTries >= RestartMax)
                return (false, $"本进程已连续自救 {_restartTries} 次，不再重启");

            var gap = now - _restartLast;
            if (_restartLast > 0 && gap < RestartCooldownSec)
                return (false, $"冷却中（还要等 {RestartCooldownSec - gap:0}s）");

            _restartTries++;
            _restartLast = now;
        }

        int? killed = null;
        if (OursRunning())
        {
            killed = _pid;
            KillTree(_pid!.Value);
            var t0 = Environment.TickCount64;
            while (Environment.TickCount64 - t0 < KillWaitMs)
            {
                if (_pid is not { } id || !Pids().Contains(id)) break;
                Thread.Sleep(500);
            }
            // ⚠️ 收不掉就别往下走（非管理员时 taskkill 会被拒）：再起一个只会让
            //    已有的这只把自己的窗口唤出来，用户凭空看到一个 HWiNFO 窗口。
            //    `_pid` 这时候**必须保留** —— 那只还是我们拉起的，归属没变。
            if (killed is { } still && Pids().Contains(still))
                return (false, $"结束不了自己拉起的实例 {still}（需要管理员权限），已放弃重启");

            _pid = null;
        }

        var (ok, detail, pid) = Launch();
        LastLaunchDetail = (killed is { } k ? $"restart:杀掉自己的 {k} 后 " : "restart:") + detail;

        if (!ok) return (false, detail);

        _pid = pid;
        _launchAt = Now();

        HwInfoUi.HideWatchdog();        // 同上：重启后不许它露脸

        var t1 = Environment.TickCount64;
        while (Environment.TickCount64 - t1 < waitSec * 1000L)
        {
            if (SmReady()) return (true, $"重启成功（{(Environment.TickCount64 - t1) / 1000.0:0.0}s 后 SM 就绪）");
            Thread.Sleep(1000);
        }
        return (false, $"重启后 {waitSec}s 内仍读不到共享内存");
    }

    /// <summary>
    /// 收掉**我们自己拉起的**那个 HWiNFO。⚠️ 按 PID 收（`/T` 连子进程），
    /// 绝不按镜像名 —— 用户自己开的那个会被一起带走。没拉过就什么都不做。
    /// </summary>
    public static bool StopOurs()
    {
        if (_pid is not { } pid) return false;
        // ⚠️ 先确认它还在：进程早退了却继续 taskkill，PID 被系统复用时
        //    会把**别人的**进程带走（概率极低，代价却是不可逆的）。
        if (!Pids().Contains(pid))
        {
            _pid = null;
            return false;
        }
        KillTree(pid);
        _pid = null;
        return true;
    }

    private static void KillTree(int pid)
        => _ = Shell.Run(TaskkillExe(), $"/PID {pid} /T /F", 8_000);

    private static string TaskkillExe()
        => Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                        "System32", "taskkill.exe");

    private static double Now() => Environment.TickCount64 / 1000.0;

    // ================================================================ INI

    /// <summary>
    /// 必须为真的预置键（每次启动前强制回写，保留 HWiNFO 自己的其它键）。
    /// 键名与取值就是下面那张表。
    /// </summary>
    private static readonly Dictionary<string, string> IniPreset = new(StringComparer.Ordinal)
    {
        ["SensorsOnly"] = "1",
        ["OpenSensors"] = "1",
        ["OpenSystemSummary"] = "0",
        ["ShowWelcomeAndProgress"] = "0",
        ["ShowWelcomeScreen"] = "0",
        ["StartMinimized"] = "1",
        ["MinimalizeMainWnd"] = "1",          // HWiNFO 自己的拼写就是 Minimalize
        ["MinimizeMainWindow"] = "1",
        ["MinimalizeSensors"] = "1",
        ["MinimizeSensors"] = "1",
        ["MinimalizeSensorsClose"] = "1",
        ["AutoUpdateBetaDisable"] = "1",
        // ⚠️ 关「检查更新」的就是这个键（2026-11-12 查实）：HWiNFO 自己写出来的 INI 里
        //    它叫 **AutoUpdate**，社区里常见的 `AutoUpdate=0` 就是关掉启动时查新版本。
        //    早先这里写的是 `UpdateCheckEnabled` / `CheckForUpdates` —— 那两个键名是
        //    自己编的，HWiNFO 根本不认（注册表那边写同名的 DWORD 也没用，本机实测
        //    `HKCU\SOFTWARE\HWiNFO64\UpdateCheckEnabled=0` 挂着，弹窗照弹）⇒ 已删掉，
        //    别再写回去：未知键只会把 INI 弄脏，还会让人以为已经关上了。
        ["AutoUpdate"] = "0",
        ["SensorsSM"] = "1",                  // 共享内存导出 ← 没有它就读不到数
        ["SMMemSize"] = "262144",
    };

    /// <summary>
    /// 把 <see cref="IniPreset"/> 强制写回 INI。返回 (ok, detail)。
    ///
    /// ⚠️ 为什么每次启动都写：HWiNFO 退出时按**自己的已知键集**回写这个 INI，
    ///    我们的键有可能被删；`SensorsOnly` / `SensorsSM` 一丢，下次就弹窗口 + 不出数。
    /// ⚠️ 写 UTF-8 **无 BOM**：写了 BOM 会让第一行 `[Settings]` 认不出来。
    /// </summary>
    public static (bool Ok, string Detail) WriteIniPreset(string exeDir)
        => PatchIni(Path.Combine(exeDir, IniName), IniPreset, backup: false,
                    alreadyOpenText: "INI 已是预置值", createIfMissing: true);

    // ⚠️ 2026-11-12 删掉了 `EnsureUpdateCheckOff()`（往 HKCU\SOFTWARE\HWiNFO64 写
    //    `UpdateCheckEnabled=0`）。当初判断「检查更新属于机器相关项、只认注册表」——
    //    **实测证伪**：本机这个 DWORD 明明挂着 0，21:45 那次拉起照样弹了更新窗。
    //    真正管用的是 INI 里的 `AutoUpdate=0`（见 IniPreset）；自编的键名别再写回去，
    //    也别再往用户注册表里塞。

    /// <summary>
    /// 给**别人的** HWiNFO INI 打开共享内存导出 + 跳过开始界面。
    /// 这是「两边都不用杀」的关键一步：那份自己就能当数据源。
    ///
    /// 补的键分两批（2026-10-15 用户实测后扩的）：
    ///   **必须键**（值不对就纠正）：`SensorsSM` / `SMMemSize` /
    ///     `ShowWelcomeAndProgress` / `ShowWelcomeScreen` ——
    ///     少了后两个，HWiNFO 会停在「欢迎 / 硬件分析」界面等用户点 Start，
    ///     不点就永远不采集（用户原话：「打开 hwinfo，停留在第一个界面」）。
    ///   **仅在缺失时添加**：`SensorsOnly` / `OpenSensors` / `OpenSystemSummary`
    ///     —— 人家可能自己设过，别覆盖。
    ///
    /// ⚠️ 故意**不动**最小化/隐藏类的键：那是人家的界面习惯。
    /// ⚠️ **必须在该 HWiNFO 没运行**时调用（运行中改会被退出时的回写冲掉）。
    /// </summary>
    public static (bool Ok, string Detail) EnableSmInIni(string iniPath)
    {
        var must = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SensorsSM"] = "1",
            ["SMMemSize"] = "262144",
            ["ShowWelcomeAndProgress"] = "0",
            ["ShowWelcomeScreen"] = "0",
        };
        var soft = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SensorsOnly"] = "1",
            ["OpenSensors"] = "1",
            ["OpenSystemSummary"] = "0",
        };

        var (ok, detail) = PatchIni(iniPath, must, backup: true,
                                    alreadyOpenText: "已经是开着的", soft: soft,
                                    createIfMissing: false);
        if (!ok) return (ok, detail);
        return detail.StartsWith("已经是开着的", StringComparison.Ordinal)
            ? (true, detail)
            : (true, $"已开启共享内存导出（{iniPath}）");
    }

    /// <summary>
    /// 通用 INI 补键：`must` 强制改值，`soft` 只在**整份文件里没有这个键**时追加。
    /// 原子写（.tmp + File.Move）；`backup` 时先留一份 `.bak`（只留一次）。
    ///
    /// `createIfMissing` = 文件不存在时按 `[Settings]` 空模板新建。
    /// 自带那份用 true（第一次跑也能写）；**别人的**那份必须 false ——
    /// 把路径算错时凭空造一个 INI 会让用户以为"改了他那份"，实际什么都没改。
    /// </summary>
    private static (bool Ok, string Detail) PatchIni(
        string path, Dictionary<string, string> must, bool backup, string alreadyOpenText,
        bool createIfMissing, Dictionary<string, string>? soft = null)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, Encoding.UTF8);
        }
        catch (Exception e) when (createIfMissing && e is FileNotFoundException or DirectoryNotFoundException)
        {
            lines = ["[Settings]"];
        }
        catch (Exception e)
        {
            return (false, $"读不到 {path}：{e.Message}");
        }

        var list = lines.ToList();
        if (!list.Any(ln => ln.Trim().Equals("[settings]", StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, "[Settings]");

        var keys = new Dictionary<string, string>(must, StringComparer.Ordinal);
        if (soft is not null)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ln in list)
            {
                if (!ln.Contains('=')) continue;
                if (ln.TrimStart().StartsWith("[", StringComparison.Ordinal)
                    || ln.TrimStart().StartsWith(";", StringComparison.Ordinal)) continue;
                present.Add(ln.Split('=', 2)[0].Trim());
            }
            foreach (var (k, v) in soft)
                if (!present.Contains(k)) keys[k] = v;
        }

        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<string>();

        foreach (var ln in list)
        {
            if (ln.Contains('=')
                && !ln.TrimStart().StartsWith("[", StringComparison.Ordinal)
                && !ln.TrimStart().StartsWith(";", StringComparison.Ordinal))
            {
                var k = ln.Split('=', 2)[0].Trim();
                if (keys.TryGetValue(k, out var want))
                {
                    if (!seen.Add(k)) continue;              // 去掉重复键
                    var wantLine = $"{k}={want}";
                    if (ln != wantLine) changed.Add(k);
                    outp.Add(wantLine);
                    continue;
                }
            }
            outp.Add(ln);
        }

        var missing = keys.Keys.Where(k => !seen.Contains(k)).ToList();
        if (missing.Count > 0)
        {
            // 插到 [Settings] 段末尾（就在第一段里，别落到别的 section 去）
            var idx = -1;
            for (var i = outp.Count - 1; i >= 0; i--)
                if (outp[i].Trim().Equals("[settings]", StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            for (var i = missing.Count - 1; i >= 0; i--)
                outp.Insert(idx + 1, $"{missing[i]}={keys[missing[i]]}");
            changed.AddRange(missing);
        }

        try
        {
            if (changed.Count > 0 && backup && !File.Exists(path + ".bak"))
                File.WriteAllText(path + ".bak", string.Join("\n", list) + "\n", new UTF8Encoding(false));

            if (changed.Count == 0) return (true, alreadyOpenText);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", outp) + "\n", new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e)
        {
            return (false, $"写 {path} 失败：{e.Message}");
        }

        return (true, $"已强制回写 {changed.Count} 个键：{string.Join(",", changed)}");
    }

    // ================================================================ 别人的安装位置

    /// <summary>
    /// 机器上**其它** HWiNFO64.exe 的安装位置（从 `NotifyIconSettings` 的
    /// `ExecutablePath` 读）。
    ///
    /// 用途（2026-10-14 用户「不想冲突，又想要 PCH 温度」）：用户手动开的那份
    /// （典型是图吧工具箱里那份）之所以让我们拿不到数据，只是它**没开共享内存导出**。
    /// 把键补上它自己就能当数据源 —— 两边都不用杀、也不冲突。
    /// 返回绝对路径列表（不含我们自己那份）。
    /// </summary>
    public static List<string> ForeignInstalls()
    {
        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Dir() is { } dir) mine.Add(Path.GetFullPath(Path.Combine(dir, ExeName)));
        mine.Add(Path.GetFullPath(StagedExe()));

        var outList = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is null) return outList;

            foreach (var subName in root.GetSubKeyNames())
            {
                string? exe = null;
                try
                {
                    using var sub = root.OpenSubKey(subName);
                    exe = sub?.GetValue("ExecutablePath")?.ToString();
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(exe)) continue;
                if (!exe.EndsWith("\\" + ExeName, StringComparison.OrdinalIgnoreCase)) continue;

                string full;
                try { full = Path.GetFullPath(exe); }
                catch { continue; }

                if (mine.Contains(full)) continue;
                if (!File.Exists(full)) continue;
                if (outList.Contains(exe, StringComparer.OrdinalIgnoreCase)) continue;
                outList.Add(exe);
            }
        }
        catch
        {
            // 键不存在 / 读不了：当作"没找到"，由调用方给出人话提示
        }
        return outList;
    }

    // ================================================================ ShellExecute / 安全目录

    /// <summary>
    /// 用 ShellExecuteEx 起进程（uiAccess 清单只能这样起）。返回 (ok, pid, err)。
    ///
    /// 结构体与 P/Invoke 在 <see cref="ShellEx"/>：那份也要给「自我提权」用，
    /// 两份手写的 `SHELLEXECUTEINFO` 迟早会漂移，而这类 ABI 错误编译不报错。
    /// 这里的参数一个都不能改：`MinNoActive` 是为了不抢焦点（HWiNFO 有自己的托盘图
    /// 标），`NoCloseProcess` 是为了拿回 PID（`runas` 下 shell 常常不给，见下）。
    /// </summary>
    private static (bool Ok, int? Pid, string? Error, bool Cancelled) ShellExec(string path, string workDir)
    {
        // ⚠️ verb 保持 "runas"：HWiNFO 的清单是 requireAdministrator，不给 runas
        //    非管理员的调用方根本起不来（CreateProcess 直接返 740）。
        //    父进程**已经提权**时这一发不弹 UAC（凌晨日志 2.6 秒拉起成功可证）；
        //    没提权时就会弹 —— 那是 HWiNFO 自己的要求，绕不过去，只能靠退避少弹。
        var r = ShellEx.Launch(path, "runas", parameters: null, workDir,
                               ShellEx.MaskNoCloseProcess | ShellEx.MaskNoAsync | ShellEx.MaskNoUi,
                               ShellEx.ShowMinNoActive);
        return (r.Ok, r.Pid, r.Error, r.Cancelled);
    }

    /// <summary>
    /// UAC 被取消：记一段长冷却并如实返回失败。
    ///
    /// ⚠️ 设置**长**冷却（10 分钟）而不是跟着指数退避走：取消是用户明确表态，
    ///    60 秒后再问一遍就是骚扰。要立刻重试只能走「修复 HWiNFO」（force）。
    /// </summary>
    private static (bool Ok, string Detail, int? Pid) Refused()
    {
        NoteLaunchBlocked("UAC 弹窗被取消（HWiNFO 自己的清单要求管理员）", RefuseCooldownSec);
        return (false,
            "UAC 弹窗被取消 —— HWiNFO64 的清单是 requireAdministrator，不给管理员就起不来。"
            + $"已暂停 {RefuseCooldownSec / 60:0} 分钟不再打扰；想立刻再试请点「修复 HWiNFO」。",
            null);
    }

    /// <summary>路径是否落在系统定义的安全位置里（uiAccess 策略要求）。</summary>
    private static bool IsSecure(string path)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        foreach (var key in new[] { "ProgramFiles", "ProgramFiles(x86)", "SystemRoot", "WinDir" })
        {
            var v = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(v)) continue;
            var root = Path.GetFullPath(v).TrimEnd('\\') + "\\";
            if (p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 把 HWiNFO 复制到安全目录。返回目标目录。
    /// ⚠️ 只在「原地起不来」时才做；源文件不齐就**不建目录**，免得留下空壳。
    /// </summary>
    private static string StageToSecure(string srcDir)
    {
        var srcs = new[] { ExeName, IniName }
            .Where(n => File.Exists(Path.Combine(srcDir, n)))
            .ToList();
        if (!srcs.Contains(ExeName))
            throw new InvalidOperationException("源目录里没有 " + ExeName + "：" + srcDir);

        var dst = StagedDir();
        Directory.CreateDirectory(dst);
        foreach (var n in srcs)
        {
            var s = Path.Combine(srcDir, n);
            var d = Path.Combine(dst, n);
            if (!File.Exists(d) || new FileInfo(d).Length != new FileInfo(s).Length)
                File.Copy(s, d, overwrite: true);
        }
        // 副本的 INI 也强制回写预置键：它从安全目录跑完同样会重写它
        WriteIniPreset(dst);
        return dst;
    }
}
