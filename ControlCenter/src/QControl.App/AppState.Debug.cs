using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.FanCurve;
using QControl.App.HotKey;
using QControl.App.Mem;
using QControl.App.Osd;
using QControl.Core.Model;
using QControl.Hal.Ec;
using QControl.Hal.HwInfo;
using QControl.Hal.Nvml;
using QControl.Hal.Sensors;
using QControl.Hal.Sys;
using QControl.Hal.Wmi;

namespace QControl.App;

/// <summary>
/// AppState 分片：**诊断与调试端点**。
/// HWiNFO 重启 / 共享内存接管 / 内存与工作集报表 / `/api/debug/*`。
/// </summary>
public sealed partial class AppState
{
    /// <summary>
    /// `POST /api/hwinfo/restart`：「HWiNFO 未就绪」警告上的「修复」——
    /// 杀掉**所有**实例（含用户手动开的那份）再起我们自己的。**绝不要自动调用。**
    /// </summary>
    public JsonObject RestartHwinfo()
    {
        var r = HwInfoProcess.ForceRestart(waitSec: 60);
        RefreshHwinfoFlag();
        // 缓存里可能还是重启前那一帧（甚至是冻结的残留）—— 作废，下次现读
        _hwCache.Invalidate();
        return r;
    }

    /// <summary>
    /// `POST /api/hwinfo/foreign-sm`：给用户手动开的那份 HWiNFO 打开共享内存导出。
    ///
    /// 这是**不冲突**的正解（2026-10-14 用户：「不想冲突，又想要 PCH 温度」）：
    /// 那份没开共享内存才导致我们拿不到数据；补上 `SensorsSM=1` 之后它自己
    /// 就能当数据源 —— 两边都不用杀。
    ///
    /// ⚠️ 运行中改会被 HWiNFO 退出时的回写冲掉 ⇒ 先要求用户关掉它（`need_close`）。
    /// </summary>
    public JsonObject EnableForeignSm()
    {
        var running = HwInfoProcess.Pids();
        if (running.Length > 0)
        {
            var need = new JsonArray();
            foreach (var id in running) need.Add(JsonValue.Create(id));
            return new JsonObject
            {
                ["ok"] = false,
                ["need_close"] = need,
                ["error"] = "请先关掉正在运行的那份 HWiNFO（右键它的托盘图标 → Quit），再点一次",
            };
        }

        var installs = HwInfoProcess.ForeignInstalls();
        if (installs.Count == 0)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "没找到你手动开的那份 HWiNFO 的位置"
                            + "（若它是绿色版/刚装上的，先让它跑一次）",
            };

        var patched = new JsonArray();
        var errs = new List<string>();
        foreach (var exe in installs)
        {
            // 扩展名换成**全大写** .INI（原厂只认这个形状），不是小写
            var ini = Path.ChangeExtension(exe, ".INI");
            var (ok, detail) = HwInfoProcess.EnableSmInIni(ini);
            if (ok) patched.Add(detail);
            else errs.Add(detail);
        }

        if (patched.Count == 0)
            return new JsonObject { ["ok"] = false, ["error"] = string.Join(" / ", errs) };

        // ⚠️ HWiNFO 同时只允许一个实例真正工作：我们那份还占着位，用户那份就打不开。
        //    所以要把**我们自己**那份停掉让位 —— 停的是我们拉的，绝不碰用户的实例。
        var stopped = new JsonArray();
        try
        {
            foreach (var id in HwInfoProcess.Pids()) stopped.Add(JsonValue.Create(id));
            HwInfoProcess.StopOurs();
            }
        catch (Exception e)
        {
            errs.Add("停掉自带实例失败：" + e.Message);
        }

        RefreshHwinfoFlag();
        _hwCache.Invalidate();   // 换成用户的实例供数了，旧帧作废
        return new JsonObject
        {
            // 停自己的实例失败时 errs 有内容：那是部分失败，不能回 ok:true
            ["ok"] = errs.Count == 0,
            ["patched"] = patched,
            ["stopped"] = stopped,
            ["error"] = errs.Count > 0 ? string.Join(" / ", errs) : null,
        };
    }

    /// <summary>
    /// `POST /api/debug/hwinfo/restart`：诊断——杀干净再拉起，看共享内存是否恢复。
    ///
    /// 真实场景只有一种：**免费版共享内存只连续导出 12 小时**，到期后段就没了，
    /// 只有重启 HWiNFO 能恢复（作者 Martin 在官方论坛的答复）。平时由自动自救处理，
    /// 这个口只在手动排查时用。
    ///
    /// ⚠️ 诊断口要看**原始**内容（含已冻结的残留段）⇒ `requireFresh: false`；
    ///    业务路径一律用默认值（冻结 = 无数据）。
    /// </summary>
    public JsonObject DebugHwinfoRestart()
    {
        var (ok, detail) = HwInfoProcess.RestartIfStalled(waitSec: 60);
        RefreshHwinfoFlag();
        _hwCache.Invalidate();   // 同上：别把重启前的旧帧继续端出去

        var raw = SharedMemoryFrame.Read(requireFresh: false);

        return new JsonObject
        {
            ["ok"] = ok,
            ["detail"] = detail,
            ["running"] = HwInfoProcess.IsRunning(),
            ["launch"] = HwInfoProcess.LastLaunchDetail,
            ["data"] = HwInfoData(),
            ["mem"] = MemReport(),
            ["shm"] = raw is null ? null : new JsonObject
            {
                ["layout"] = raw.Layout,
                ["version"] = raw.Version,
                ["revision"] = raw.Revision,
                ["sensor_elem"] = raw.SensorElemSize,
                ["reading_elem"] = raw.ReadingElemSize,
                ["n_sensors"] = raw.Sensors.Count,
                ["n_readings"] = raw.Readings.Count,
            },
            ["temps"] = TempsJson(),
        };
    }

    /// <summary>
    /// 「整体内存」实测（2026-10-21 加，只回答一个问题：**换成别的传感器源到底能省多少**）。
    ///
    /// 口径：控制中心自己 + 所有 HWiNFO 实例。`priv_mb`（专用工作集）才是真实占用，
    /// `ws_mb`（工作集）是任务管理器「内存」列那个数。`launch_ms` 是拉起到共享内存出数
    /// 的耗时 —— 判断「改成按需拉起用户要等多久」就靠它。
    /// </summary>
    private JsonObject MemReport()
    {
        var hw = HwInfoProcess.MemoryReport();
        double selfWs = 0, selfPriv = 0;
        try
        {
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            selfWs = me.WorkingSet64 / 1048576.0;
            selfPriv = me.PrivateMemorySize64 / 1048576.0;
        }
        catch { /* 理论上不会 */ }

        var hwWs = hw["total_ws_mb"]?.GetValue<double>() ?? 0;
        var hwPriv = hw["total_priv_mb"]?.GetValue<double>() ?? 0;

        return new JsonObject
        {
            ["mode"] = HwinfoMode(),
            ["needed"] = HwinfoNeeded(),
            ["idle_sec"] = HwinfoIdleSec(),
            ["self"] = new JsonObject
            {
                ["ws_mb"] = Math.Round(selfWs, 1),
                ["priv_mb"] = Math.Round(selfPriv, 1),
            },
            ["hwinfo"] = hw,
            ["launch_ms"] = HwInfoProcess.LastReadyMs is { } ms ? Math.Round(ms, 0) : null,
            ["total_priv_mb"] = Math.Round(selfPriv + hwPriv, 1),
            ["total_ws_mb"] = Math.Round(selfWs + hwWs, 1),
        };
    }

    /// <summary>
    /// `GET /api/debug/mem`：这个进程到底把内存花在哪（2026-10-27 加）。
    ///
    /// 为什么单独开一个口：`/api/debug/hwinfo` 里的 mem 段只回答"HWiNFO 值不值得留"，
    /// 给的是 ws / priv 两个总数。而用户问"还能不能再压"时需要的恰恰是**拆账**：
    /// 托管堆多少、还有多少碎片、工作集里有多少是能换出去的闲置页。没有这几个数，
    /// 任何"还能省 X MB"的说法都是猜。
    ///
    /// 口径说明（别和任务管理器混着看）：
    ///   · `process.ws_mb`      = 任务管理器「内存」那一列（含与其他进程共享的 dll 页）
    ///   · `process.priv_mb`    = 提交（Private Bytes），含**已换出到页面文件**的部分
    ///   · `gc.heap_mb`         = 托管堆实际存活对象；和 priv 的差就是运行时/原生/映射
    ///   · `trim`               = 最近一次归还工作集的成效（before → after）
    /// </summary>
    public JsonObject DebugMem()
    {
        double ws = -1, priv = -1, paged = -1, virt = -1;
        int threads = 0, handles = 0;
        try
        {
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            ws = Math.Round(me.WorkingSet64 / 1048576.0, 1);
            priv = Math.Round(me.PrivateMemorySize64 / 1048576.0, 1);
            paged = Math.Round(me.PagedMemorySize64 / 1048576.0, 1);
            virt = Math.Round(me.VirtualMemorySize64 / 1048576.0, 1);
            threads = me.Threads.Count;
            handles = me.HandleCount;
        }
        catch { /* 取不到就如实回 -1 */ }

        var gi = GC.GetGCMemoryInfo();
        // GenerationInfo 的顺序是固定的：Gen0 / Gen1 / Gen2 / LOH / POH。
        double Gen(int i) => i < gi.GenerationInfo.Length
            ? Math.Round(gi.GenerationInfo[i].SizeAfterBytes / 1048576.0, 2)
            : 0;

        int points;
        lock (_histLock) points = _history.Count;

        return new JsonObject
        {
            ["process"] = new JsonObject
            {
                ["ws_mb"] = ws,
                ["priv_mb"] = priv,
                ["paged_mb"] = paged,
                ["virtual_mb"] = virt,
                ["threads"] = threads,
                ["handles"] = handles,
                ["uptime_s"] = _startedAt == default
                    ? null
                    : (int)Math.Max(0, (DateTime.Now - _startedAt).TotalSeconds),
            },
            ["gc"] = new JsonObject
            {
                // 存活对象总字节（含各代 + LOH + POH）
                ["heap_mb"] = Math.Round(gi.HeapSizeBytes / 1048576.0, 2),
                ["committed_mb"] = Math.Round(gi.TotalCommittedBytes / 1048576.0, 2),
                ["fragmented_mb"] = Math.Round(gi.FragmentedBytes / 1048576.0, 2),
                ["pinned"] = gi.PinnedObjectsCount,
                ["gen0_mb"] = Gen(0),
                ["gen1_mb"] = Gen(1),
                ["gen2_mb"] = Gen(2),
                ["loh_mb"] = Gen(3),
                ["poh_mb"] = Gen(4),
                ["collections"] = new JsonObject
                {
                    ["gen0"] = GC.CollectionCount(0),
                    ["gen1"] = GC.CollectionCount(1),
                    ["gen2"] = GC.CollectionCount(2),
                },
                // 这三个是"档位是否真的生效"的证据（真源 runtimeconfig.template.json）
                ["server"] = System.Runtime.GCSettings.IsServerGC,
                ["latency"] = System.Runtime.GCSettings.LatencyMode.ToString(),
                ["total_available_mb"] = Math.Round(gi.TotalAvailableMemoryBytes / 1048576.0, 1),
                // 累计分配量（进程启动至今）。两次调用相减 ÷ 间隔秒 = 分配速率 ——
                // 这个是托管堆"提交 25 MB 但只活 3 MB"的根因量：分配越快，gen0 预算越大、
                // 段就越多。看内存别再只看存活堆，速率才是能改的那个数。
                ["allocated_mb"] = Math.Round(GC.GetTotalAllocatedBytes() / 1048576.0, 1),
            },
            ["ui_open"] = MemTrim.UiOpen,
            ["history_points"] = points,
            // 当前采样间隔（插电 1 s / 电池 2 s）。验证降采样有没有真生效就看这一行：
            // 拔掉电源后下一拍应当变成 2000，`history` 的点数增速随之减半。
            ["sample_interval_ms"] = _onBattery ? SampleIntervalBattMs : SampleIntervalAcMs,
            ["trim"] = MemTrim.Last,
            // 「托盘态那 30 MB 是谁的」只看两件事就够判方向：WPF 程序集进没进进程、
            // 显卡用户态驱动进没进进程。这两个是仅有的两块"一旦加载就下不来"的大头
            // （80 MB / 178 MB 量级），其余（coreclr、WMI）是必需底噪。
            ["modules"] = ModuleFlags(),
            // HWiNFO 帧解析的开销（累计值，两次调用相减 ÷ 秒 = 每秒贡献多少分配）。
            // 11-02 撤掉"poll_time 去重"后留的口子：只有量到它确实是大头，才值得做按需解析。
            ["hwinfo_frame"] = new JsonObject
            {
                ["parse_calls"] = QControl.Hal.HwInfo.SharedMemoryFrame.ParseCalls,
                ["parse_alloc_mb"] = Math.Round(
                    QControl.Hal.HwInfo.SharedMemoryFrame.ParseAllocMb, 1),
            },
            // 全量 / 精简分账（2026-11-02）：托盘态走精简档，这一节回答"省了多少、还剩什么"。
            // `us` 是**最近一帧**各子块的自耗时（微秒）；精简档里没跑的子块是 0。
            // `demand_in_s` = 还要多久掉回精简档（>0 说明有人在看）。
            ["snapshot"] = new JsonObject
            {
                ["mode"] = HasFullStatus ? "full" : "lean",
                ["full_ticks"] = SnapshotTickCounts.Full,
                ["lean_ticks"] = SnapshotTickCounts.Lean,
                ["demand_in_s"] = Math.Round(
                    (Interlocked.Read(ref _fullUntil) - Environment.TickCount64) / 1000.0, 1),
                ["us"] = new JsonObject
                {
                    ["total"] = UsTotal,
                    ["sensors"] = UsSensors,
                    ["cpu"] = _sensors.UsCpu,
                    ["gpu"] = _sensors.UsGpu,
                    ["memory"] = _sensors.UsMem,
                    ["battery"] = _sensors.UsBatt,
                    ["network"] = _sensors.UsNet,
                    ["system"] = _sensors.UsSys,
                    ["display"] = _sensors.UsDisp,
                    ["disk"] = _sensors.UsDisk,
                    ["ec"] = UsEc,
                    ["perf"] = UsPerf,
                },
            },
            ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
        };
    }

    /// <summary>
    /// 进程里有没有加载那两块"大头"（WPF / 显卡用户态驱动），外加模块总数。
    ///
    /// 为什么不用 `ProcessModule.WorkingSetSize`：.NET 的 `ProcessModule` **没有**这个属性
    /// （只有 `ModuleMemorySize` = 映像大小，不是常驻内存，拿它排"谁占内存"是错的）。
    /// 真正的逐模块工作集要走 psapi + QueryWorkingSetEx，代价与收益不成比例；
    /// 而"该不该加载"这个决策只需要知道**加载了没有**。
    /// </summary>
    private static JsonObject? ModuleFlags()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            var count = 0;
            var wpf = false;
            var gpu = false;
            var wpfNames = new JsonArray();
            foreach (ProcessModule m in me.Modules)
            {
                count++;
                var n = m.ModuleName;
                if (n.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("wpfgfx_cor3.dll", StringComparison.OrdinalIgnoreCase)
                    || n.Equals("WindowsBase.dll", StringComparison.OrdinalIgnoreCase))
                {
                    wpf = true;
                    wpfNames.Add(n);
                }
                else if (n.StartsWith("nvwgf2um", StringComparison.OrdinalIgnoreCase)
                         || n.StartsWith("nvoglv", StringComparison.OrdinalIgnoreCase)
                         || n.Equals("nvd3dumx.dll", StringComparison.OrdinalIgnoreCase))
                    gpu = true;
            }
            return new JsonObject
            {
                ["count"] = count,
                ["wpf_loaded"] = wpf,
                ["wpf_modules"] = wpfNames,     // 具体是哪几个（判"谁把它拉进来的"用）
                ["gpu_driver_loaded"] = gpu,
            };
        }
        catch
        {
            return null;   // 枚举不了就如实不给（极少数环境读映像头会抛）
        }
    }

    /// <summary>
    /// `POST /api/debug/mem/trim`：当场回收 + 归还工作集，回前后数字。
    ///
    /// 手动口的意义：验证"这台机器上 trim 到底值几 MB、花多少毫秒"不用改配置重启；
    /// 平时由关窗 / 空闲两个时机自动触发（<see cref="MemTrim"/>），这个端点只做诊断。
    /// ⚠️ 会阻塞这次 HTTP 几十到几百毫秒（两轮阻塞 GC），属预期 —— 它在独立连接线程上，
    ///    不拖采样也不拖界面。
    /// </summary>
    public JsonObject TrimWorkingSetNow()
    {
        var (before, after) = MemTrim.Run("手动");
        return new JsonObject
        {
            ["ok"] = before > 0 && after > 0,
            ["before_mb"] = before < 0 ? null : Math.Round(before, 1),
            ["after_mb"] = after < 0 ? null : Math.Round(after, 1),
            ["saved_mb"] = before < 0 || after < 0 ? null : Math.Round(before - after, 1),
            ["last"] = MemTrim.Last,
        };
    }

    /// <summary>
    /// `GET /api/debug/hwinfo`：现场问一次「HWiNFO 现在到底行不行」。
    ///
    /// 保留这个口的原因：**所有传感器都压到 HWiNFO 这一个第三方源上了**，一旦哪项
    /// 变空，用它一眼能看出是「进程没起」「共享内存没就绪（12 小时到期 / 残留段）」
    /// 还是「label 名字对不上」—— 不用猜。
    ///
    /// ⚠️ 没就绪时**当场 ensure 一次**（最多等 30 秒）：
    ///    诊断口按下去就是要答案，不能只回一句「没好」。
    ///    每个请求跑在自己的线程上（MiniHttpServer 每连接一个 Task），阻塞 45 秒
    ///    不会拖住采样和其它接口。
    /// ⚠️ `shm` 与 `temps` 用 `requireFresh:false` 读原始帧 —— 诊断就是要看
    ///    **冻结的残留段**长什么样（业务路径才用默认的新鲜度判据）。
    /// </summary>
    public JsonObject DebugHwinfo()
    {
        var ready = SharedMemoryFrame.Read(requireFresh: true) is not null;
        string detail;

        if (ready)
        {
            var n = HwInfoProcess.Pids().Length;
            detail = n > 0
                ? $"已有实例在跑（{n} 个），共享内存新鲜"
                : "共享内存新鲜（进程列表里没看到 HWiNFO，可能是别的会话刚拉起后退出）";
        }
        else if (HwinfoMode() == "off")
        {
            // 模式=off 时诊断口**也不许**拉起：它就是"不要这个进程"的意思
            detail = "模式=off：从不拉起（那 6 项整行隐藏，省约 82 MB）";
        }
        else
        {
            RequestHwinfo();     // 有人在诊断 = 有人在要数据，按需模式下续上窗口
            DemandFullSnapshot();
            EnsureHwinfo();
            ready = SharedMemoryFrame.Read(requireFresh: true) is not null;
            detail = ready
                ? "本次调用已拉起并等到共享内存"
                : "拉起失败或 45 秒内没有新鲜数据："
                  + (HwInfoProcess.LastLaunchDetail ?? "原因未知");
        }

        // ⚠️ 这里**不动** HwInfoHealth 的计数：它只反映采样路径的连续失败，
        //    诊断口偶尔来看一眼不该把「刚崩」的判定打乱（早先实现同样只在 _snap 里计数）。
        var (streak, healed, failedFor) = HwInfoHealth.State;
        var block = HwInfoProcess.LaunchBlock;
        var dir = HwInfoProcess.Dir();
        var raw = SharedMemoryFrame.Read(requireFresh: false);

        return new JsonObject
        {
            ["ok"] = ready,
            ["detail"] = detail,
            ["running"] = HwInfoProcess.IsRunning(),
            // 实际选中的是哪一份（2026-10-30）：用户问「我明明放了却读不到」时，
            // 唯一能一眼看穿的字段 —— 比如选中了上层某个不相干的 `HWiNFO\` 目录。
            ["dir"] = dir,
            ["exe_exists"] = dir is not null && File.Exists(Path.Combine(dir, HwInfoProcess.ExeName)),
            ["ini_exists"] = dir is not null && File.Exists(Path.Combine(dir, HwInfoProcess.IniName)),
            ["data"] = HwInfoData(),
            ["mem"] = MemReport(),
            ["stats"] = new JsonObject
            {
                ["fail_streak"] = streak,
                ["healed"] = healed,
                // 多给一项失败持续秒数：streak 是「看门狗 tick 数」（2 秒一次），
                // 单看数字判断不出到底是卡了 10 秒还是 10 分钟。
                ["failed_for"] = failedFor,
                ["running"] = HwInfoProcess.IsRunning(),
                ["launch"] = HwInfoProcess.LastLaunchDetail,
                ["ours_pid"] = HwInfoProcess.OurPid is { } op ? JsonValue.Create(op) : null,
                ["ours_running"] = HwInfoProcess.OursRunning(),
                // 拉起被暂缓时（UAC 被取消 / 连续失败退避）这里是唯一能答「为什么空着」的地方
                ["launch_blocked_for"] = block.LeftSec > 0 ? JsonValue.Create(Math.Round(block.LeftSec)) : null,
                ["launch_block_reason"] = block.Why,
                ["elevated"] = Elevation.IsElevated(),
            },
            ["ui"] = HwInfoUi.Report(),
            ["shm"] = raw is null ? null : new JsonObject
            {
                ["layout"] = raw.Layout,
                ["version"] = raw.Version,
                ["revision"] = raw.Revision,
                ["sensor_elem"] = raw.SensorElemSize,
                ["reading_elem"] = raw.ReadingElemSize,
                ["n_sensors"] = raw.Sensors.Count,
                // 只给前 40 组：整表能到几百行，但这几行就够判断「组名对不上」
                ["sensors"] = new JsonArray(raw.Sensors.Take(40)
                    .Select(s => (JsonNode?)new JsonArray(s.Id, s.Inst, s.Name)).ToArray()),
                ["n_readings"] = raw.Readings.Count,
            },
            ["temps"] = TempRowsJson(),
        };
    }

    // ================================================================ 采集包里的 hwinfo 段

    /// <summary>
    /// 采集包里的 `hwinfo` 段 —— 回答一个问题：**这台机器上 HWiNFO 到底行不行**。
    ///
    /// 为什么要有它（2026-11-08）：一台没有 EC 的台式机寄来采集包，里面全是"连不上 EC"，
    /// 而现场症状是"温度/转速一个都没有、只有 CPU 功耗"。这两件事的因果关系只能靠反推
    /// （功耗走 PDH 的 Energy Meter，根本不需要 HWiNFO）—— 因为包里一个字都没提 HWiNFO。
    /// 加上这一段之后，同一份包就能自证："进程在跑、段打不开"还是"段是残留的"还是
    /// "段是新的但标签对不上"。
    ///
    /// ⚠️ 与 `/api/debug/hwinfo` 刻意不同：那条会**当场拉起** HWiNFO 并最多等 45 秒
    ///    （诊断口按下去就是要答案）。采集要的是"此时此刻的真实状态"，所以这里**不 ensure**，
    ///    也不碰任何进程 —— 采集包不该改变被采集对象的状态。
    /// ⚠️ 帧用 `requireFresh:false` 读：残留段也必须能看见。"段里明明有温度、只是过期了"
    ///    与"段里压根没有温度这一项"是两种完全不同的病，结论差得很远。
    /// ⚠️ 组名可能带**硬盘序列号**（`S.M.A.R.T.: 型号 (序列号) [C:]`）⇒ 括号内容一律抹掉，
    ///    报告开头那句"不含硬盘序列号"必须继续成立。
    /// </summary>
    public JsonObject HwinfoDiag(int tempCap = 60)
    {
        var frame = SharedMemoryFrame.Read(requireFresh: false);
        var segment = SharedMemoryFrame.SegmentExists();
        var dir = HwInfoProcess.Dir();
        var running = HwInfoProcess.IsRunning();

        // 逐帧读一遍原始段（不经过 _hw 的 2 秒缓存）：要的是"现在段里有什么"。
        var reader = new HwInfoReader(() => frame);
        var fresh = frame is not null && SharedMemoryFrame.IsFresh(frame);
        var age = frame is null ? (double?)null : Math.Round(SharedMemoryFrame.AgeSeconds(frame), 1);

        var cpuTemp = reader.FindCpuTemp();
        var gpuTemp = reader.FindGpuTemp();

        string note;
        if (frame is null)
        {
            note = segment
                ? "段打得开但解析不出读数（HWiNFO 换了版本、元素布局对不上）"
                : "HWiNFO 没有导出共享内存：它没在跑，或设置里没勾「共享内存支持」"
                  + "（HWiNFO 7 起默认关闭，免费版连续跑满 12 小时会自动关）";
        }
        else if (!fresh)
        {
            // age < 0 = 段头里的 poll_time 无效（HWiNFO 没在轮询 / 段是别的进程留下的空壳）
            note = age is { } a && a >= 0
                ? $"共享内存是残留段（最后写入 {a.ToString("0", CultureInfo.InvariantCulture)} 秒前）："
                  + "HWiNFO 已退出，或共享内存被 12 小时上限关掉了"
                : "段打得开但时间戳无效（poll_time<=0）：HWiNFO 没在轮询传感器";
        }
        else if (cpuTemp.Value is null && gpuTemp.Value is null)
        {
            note = "段是新鲜的，但认不出 CPU/GPU 温度 —— 见 temps 与 groups（标签或读数组布局对不上）";
        }
        else
        {
            note = "共享内存新鲜，温度读得到";
        }

        var groups = new JsonArray();
        if (frame is not null)
            foreach (var s in frame.Sensors.Take(40))
                groups.Add(RedactSensorName(s.Name));

        var temps = new JsonArray();
        if (frame is not null)
            foreach (var (group, label, value, unit, type) in reader.ListTempsDetailed(tempCap))
                temps.Add(new JsonObject
                {
                    ["group"] = RedactSensorName(group),
                    ["label"] = label,
                    ["value"] = Math.Round(value, 1),
                    // HWiNFO 给温度的单位不是 UTF-8 的 `°C`，解出来是 `¡æ`（见 HwInfoReader
                    // 的 SMART 注释）。报告是给人看的 ⇒ 按类型标记还原成 `°C`，其它单位原样。
                    ["unit"] = type == HwInfoReader.TypeTemp ? "°C" : unit,
                    ["type"] = type,
                });

        return new JsonObject
        {
            ["ok"] = fresh && (cpuTemp.Value is not null || gpuTemp.Value is not null),
            ["note"] = note,
            // ⚠️ 不给完整路径：报告开头承诺"不含任何本地文件路径"。
            ["exe_found"] = dir is not null,
            ["running"] = running,
            ["ours_running"] = HwInfoProcess.OursRunning(),
            ["segment"] = segment,
            ["fresh"] = fresh,
            ["age_sec"] = age,
            ["layout"] = frame?.Layout,
            ["version"] = frame?.Version,
            ["revision"] = frame?.Revision,
            ["sensor_elem"] = frame?.SensorElemSize,
            ["reading_elem"] = frame?.ReadingElemSize,
            ["n_sensors"] = frame?.Sensors.Count,
            ["n_readings"] = frame?.Readings.Count,
            ["groups"] = groups,
            // 那几项 Find 的命中情况：`[值, 来源]`，来源以 `hwinfo:` 开头 ⇒ 是它给的。
            // ⚠️ 一律**四舍五入**：原始 double 会长成 `31.60487273541663`，报告里没法看
            //    （温度 1 位、功耗 1 位、电压 3 位，与原生的显示口径一致）。
            ["reads"] = new JsonObject
            {
                ["cpu_temp"] = PairR(cpuTemp, 1),
                ["gpu_temp"] = PairR(gpuTemp, 1),
                ["fans_rpm"] = FansPair(reader.FindFanRpm()),
                ["cpu_power"] = PairR(reader.FindCpuPower(), 1),
                ["cpu_voltage"] = PairR(reader.FindCpuVoltage(), 3),
                ["gpu_mem_temp"] = PairR(reader.FindGpuMemTemp(), 1),
                ["pch"] = PairR(reader.FindPch(), 1),
            },
            ["temps"] = temps,
        };
    }

    /// <summary>与 <see cref="Pair"/> 同形状，但把值四舍五入到 <paramref name="decimals"/> 位。</summary>
    private static JsonArray PairR((double? Value, string? Source) t, int decimals)
        => new(t.Value is { } v ? JsonValue.Create(Math.Round(v, decimals)) : null,
               JsonValue.Create(t.Source));

    /// <summary>
    /// 组名脱敏：`S.M.A.R.T.: 型号 (序列号) [C:, D:]` 里的括号内容就是硬盘序列号，
    /// 抹成 `(…)`。其它组名（`CPU [#0]: AMD Ryzen 5 5600`）原样保留 —— 那是诊断的关键。
    /// </summary>
    private static string RedactSensorName(string name)
    {
        if (!name.StartsWith("S.M.A.R.T", StringComparison.OrdinalIgnoreCase)) return name;
        var open = name.IndexOf('(');
        var close = open < 0 ? -1 : name.IndexOf(')', open + 1);
        return close > open ? name[..open] + "(…)" + name[(close + 1)..] : name;
    }

    /// <summary>诊断口用的温度清单：比 <see cref="TempsJson"/> 多带 `unit` / `type`。</summary>
    private JsonArray TempRowsJson()
    {
        var arr = new JsonArray();
        foreach (var (group, label, value, unit, type) in _hw.ListTempsDetailed(120))
            arr.Add(new JsonObject
            {
                ["group"] = group,
                ["label"] = label,
                ["value"] = value,
                ["unit"] = unit,
                ["type"] = type,
            });
        return arr;
    }

    /// <summary>HWiNFO 那一侧的全量读数：每项都是 `[值, 来源]`。</summary>
    private JsonObject HwInfoData()
    {
        var pch = _hw.FindPch();
        var vmem = _hw.FindVmem();

        var smart = new JsonObject();
        foreach (var rec in _hw.FindDriveSmart())
        {
            // 早先实现只把**取到的**键放进记录里，这里也照做（界面不会把 null 当 0）
            var o = new JsonObject();
            if (rec.Model.Length > 0) o["model"] = rec.Model;
            if (rec.Sid is not null) o["sid"] = rec.Sid;
            if (rec.Volumes is not null)
                o["volumes"] = new JsonArray(rec.Volumes.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            if (rec.Temp is { } t) o["temp"] = t;
            if (rec.Life is { } l) o["life"] = l;
            if (rec.Spare is { } sp) o["spare"] = sp;
            if (rec.WritesGb is { } w) o["writes_gb"] = w;
            if (rec.ReadsGb is { } rd) o["reads_gb"] = rd;

            // ⚠️ JSON 的键必须带 SID：两块同型号盘否则在这里又互相覆盖（2026-11-12）
            smart[rec.Sid is { Length: > 0 } sid ? $"{rec.Model} ({sid})" : rec.Model] = o;
        }

        return new JsonObject
        {
            ["pch"] = pch.Value,
            ["pch_src"] = pch.Source,
            ["cpu_power"] = Pair(_hw.FindCpuPower()),
            ["cpu_voltage"] = Pair(_hw.FindCpuVoltage()),
            // 兜底三项（2026-11-02）：未标定 layout 的机型，界面上的温度/转速就是这三个值。
            // 诊断时先看这里能不能读出来，再去看 `ec` 段 —— 不然分不清「HWiNFO 也没数」
            // 与「读到了但没接上」。
            ["cpu_temp"] = Pair(_hw.FindCpuTemp()),
            ["gpu_temp"] = Pair(_hw.FindGpuTemp()),
            ["fans_rpm"] = FansPair(_hw.FindFanRpm()),
            ["gpu_mem_temp"] = Pair(_hw.FindGpuMemTemp()),
            ["gpu_voltage"] = Pair(_hw.FindGpuVoltage()),
            ["batt_rate"] = Pair(_hw.FindBattRate()),
            ["cpu_power_limits"] = PairDict(_hw.FindCpuPowerLimits()),
            // 显卡功耗墙**只在这个诊断口里**还能看到（2026-11-12 起界面不再显示它）：
            // 那是 NVML 的 enforced limit，随 Dynamic Boost 连续浮动，没有确定值，
            // 用户"读不到就删了吧"。留着这个口是为了排查"驱动到底报多少"。
            ["gpu_power_limits"] = PairDict(_hw.FindGpuPowerLimits()),
            ["vmem"] = DictOfNullable(vmem.Value),
            ["vmem_src"] = vmem.Source,
            ["drive_smart"] = smart,
        };
    }

    private static JsonArray Pair((double? Value, string? Source) t)
        => new(JsonValue.Create(t.Value), JsonValue.Create(t.Source));

    /// <summary>`(cpu, gpu, 来源)` → `[{cpu, gpu}, 来源]`，形状与 <see cref="Pair"/> 一致。</summary>
    private static JsonArray FansPair((double? Cpu, double? Gpu, string? Source) t)
        => new(new JsonObject { ["cpu"] = t.Cpu, ["gpu"] = t.Gpu },
               JsonValue.Create(t.Source));

    /// <summary>`(dict, 来源)` → `[{…}, 来源]`；dict 为 null 时值位给 null。</summary>
    private static JsonArray PairDict((Dictionary<string, double>? Limits, string? Source) t)
    {
        if (t.Limits is null) return new JsonArray(null, JsonValue.Create(t.Source));
        var o = new JsonObject();
        foreach (var (k, v) in t.Limits) o[k] = v;
        return new JsonArray(o, JsonValue.Create(t.Source));
    }

    private static JsonObject? DictOfNullable(Dictionary<string, double?>? d)
    {
        if (d is null) return null;
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v is { } x ? JsonValue.Create(x) : null;
        return o;
    }

    private JsonArray TempsJson()
    {
        var arr = new JsonArray();
        foreach (var (group, label, value) in _hw.ListTemps(120))
            arr.Add(new JsonObject { ["group"] = group, ["label"] = label, ["value"] = value });
        return arr;
    }

    /// <summary>
    /// 「HWiNFO 段在不在」这个一次性判定要跟着进程动作刷新 ——
    /// 否则刚点完「修复」，`hwinfo_block` 还会按旧结论继续报。
    /// </summary>
    private void RefreshHwinfoFlag() => _hwinfoReady = SharedMemoryFrame.SegmentExists();


    private static bool Truthy(JsonNode? n) => Device.Truthy(n);
}
