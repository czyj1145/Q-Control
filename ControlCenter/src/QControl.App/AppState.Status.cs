using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Ec;
using QControl.Core.FanCurve;
using QControl.Core.History;
using QControl.Core.Logs;
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
/// AppState 分片：**状态快照与历史曲线**。
/// `/api/status` 每帧拼装（BuildSnapshot）、150 点历史环形缓冲、日志占用统计都在这里。
/// </summary>
public sealed partial class AppState
{
    /// <summary>`GET /api/app/settings` 里那段"日志占用"的实数（设置页清理按钮旁边显示）。</summary>
    public JsonObject LogUsage()
    {
        var st = Logging.FileLog.DirStats();

        // 机型档案与日志是两码事（一个在 logs\、一个在 model.json 同级的 devices\），
        // 但都会被"清理"这件事牵到 —— 分开报，界面上才说得清"清的是哪一个"。
        var dir = ProfileDir;
        var prof = UserProfileStore.Scan(dir);

        // 采集包也在 logs\ 里，但**不是日志**（见 AdoptPackStore）：不单列的话
        // "占用空间"会报 0 B、清理也一个不动，用户只会看到"清不掉的东西越积越多"。
        var packs = AdoptPackStore.Scan(Logging.FileLog.DirectoryPath);

        return new JsonObject
        {
            ["ok"] = true,
            ["dir"] = Logging.FileLog.DirectoryPath,
            ["files"] = st.Files,
            ["bytes"] = st.Bytes,
            ["cc_files"] = st.CcFiles,
            ["cc_bytes"] = st.CcBytes,
            ["old_files"] = st.OldFiles,
            ["old_bytes"] = st.OldBytes,
            ["cap_files"] = st.CapFiles,
            ["cap_bytes"] = st.CapBytes,
            // 本机采集出来的机型档案（一键采集的产物），**默认不在"清理日志"范围内** ——
            // 要清必须显式带上 profiles:true（见 ClearLogs）。
            ["profile_dir"] = dir,
            ["profile_files"] = prof.Files,
            ["profile_bytes"] = prof.Bytes,
            ["profile_names"] = new JsonArray(
                prof.Names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            // 源码仓库里的 devices\（调试实例常见）不给清：那是手改真源，删了没处找回
            ["profile_cleanable"] = !UserProfileStore.LooksLikeSourceTree(dir),
            // 采集包（logs\adopt-*.json/.txt）：同样不在"清理日志"范围内，要清带 packs:true
            ["pack_files"] = packs.Files,
            ["pack_bytes"] = packs.Bytes,
            ["pack_names"] = new JsonArray(
                packs.Names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
        };
    }

    /// <summary>用户机型档案目录（= `model.json` 同级的 `devices\`，安装版即 ProgramData 那份）。</summary>
    private string ProfileDir => MachineAdopt.UserProfileDir(this);

    /// <summary>
    /// `POST /api/app/log/clear`：清理日志文件（当天日志 + 轮转旧档 + 区间导出），
    /// 可选顺带清**采集包**（logs\adopt-*）与**机型档案**（devices\*.json）。
    ///
    /// ⚠️ 删除不可逆，所以：① 只删 logs 目录里我们自己那几类文件（见 FileLog.ClearLogs /
    ///    AdoptPackStore）；② 界面必须先弹确认框，把"删几个文件、多大"摆出来再让用户点；
    ///    ③ 删不掉就如实报 failed，不假装删了。
    /// ⚠️ 当天那份**自己是删得掉的**（2026-10-29 实测修正）：writer 用
    ///    `FileShare.ReadWrite | FileShare.Delete` 打开，`File.Delete` 会成功
    ///    （文件从目录里消失、句柄继续写进没有目录项的那份）—— 所以紧接着必须
    ///    `Reopen()`，否则"清理成功、之后再没有日志文件"。真正会 failed 的是
    ///    **别的进程**把它独占打开了（杀毒/编辑器），说法别再指到自己头上。
    ///
    /// <paramref name="profiles"/> = 顺带清掉**本机采集出来的机型档案**
    /// （`devices\*.json`，见 <see cref="UserProfileStore"/>）。默认 false：
    /// 档案比日志"重"得多（删掉就没有本机的温度/转速偏移了），必须由用户在
    /// 确认框里单独勾选，不能因为"清个日志"顺手带走。
    /// ⚠️ 删档案**不会热生效** —— 当前 Device 是构造时解析好的，重启才回落内置档案。
    ///
    /// <paramref name="packs"/> = 顺带清掉**采集包**（`logs\adopt-*.json/.txt`，
    /// 见 <see cref="AdoptPackStore"/>）。默认 false，理由同上：那是要发给作者的交付物。
    /// </summary>
    public JsonObject ClearLogs(bool profiles = false, bool packs = false)
    {
        var (deleted, freed, failed) = Logging.FileLog.ClearLogs();

        // 删完必须重开：Windows 上删掉正开着的文件后，writer 还握着旧句柄，
        // 之后的日志会写进一个"没有目录项"的文件里 —— 用户看到的就是"清理后没日志了"。
        Logging.FileLog.Reopen();

        // 「最近导出」那份 capture-*.log 刚被删掉了 —— 文件没了就别再在界面上指着它
        // （那行是内存里的状态，不跟着磁盘走；不清就会显示一个不存在的文件）。
        Logging.LogCapture.ForgetMissingLast();

        var outObj = new JsonObject
        {
            ["ok"] = failed == 0,
            ["deleted"] = deleted,
            ["freed"] = freed,
            ["failed"] = failed,
        };
        if (failed > 0)
            outObj["error"] = $"{failed} 个文件没删掉（被别的程序独占着，或权限不足）；"
                              + "关掉占用它的程序后再清一次";

        // ---- 采集包（可选）----
        // 注意顺序：必须在 FileLog.ClearLogs 之后、用同一个目录再扫一遍 ——
        // 采集包和日志混在 logs\ 里，两套规则各删各的，谁也不替谁兜底。
        if (packs)
        {
            var logDir = Logging.FileLog.DirectoryPath;
            var (pkd, pkfreed, pkfailed) = AdoptPackStore.Delete(logDir);
            outObj["packs_deleted"] = pkd;
            outObj["packs_freed"] = pkfreed;
            outObj["packs_failed"] = pkfailed;
            if (pkfailed > 0)
            {
                outObj["packs_error"] = $"{pkfailed} 个采集包没删掉（被别的程序占着，或权限不足）";
                // ok 说的是「这次清理整体成没成」，不能只看日志那一类
                outObj["ok"] = false;
                outObj["error"] ??= "采集包没清干净（见 packs_error）";
            }
        }

        if (!profiles) return outObj;

        // ---- 机型档案（可选）----
        var dir = ProfileDir;
        if (UserProfileStore.LooksLikeSourceTree(dir))
        {
            // 调试实例的 model.json 常在工作区 ⇒ 档案目录指到源码里的 devices\
            // 那是手改真源，删了没处找回：拒绝，并说清为什么。
            outObj["profiles_deleted"] = 0;
            outObj["profiles_freed"] = 0;
            outObj["profiles_failed"] = 0;
            outObj["profile_dir"] = dir;
            outObj["profile_error"] =
                "机型档案目录看着是源码仓库（旁边有 QControl.slnx / .git），这里不删档案；"
                + "要清请手动删该目录下的 .json。";
            // 请求里明确要清档案却一个没动 ⇒ 整次不能报「已清理」
            outObj["ok"] = false;
            outObj["error"] ??= "机型档案没清（见 profile_error）";
            return outObj;
        }

        var (pd, pfreed, pfailed) = UserProfileStore.Delete(dir);
        outObj["profiles_deleted"] = pd;
        outObj["profiles_freed"] = pfreed;
        outObj["profiles_failed"] = pfailed;
        outObj["profile_dir"] = dir;
        if (pfailed > 0)
        {
            outObj["profile_error"] = $"{pfailed} 个机型档案没删掉（被别的程序占着，或权限不足）";
            outObj["ok"] = false;
            outObj["error"] ??= "机型档案没清干净（见 profile_error）";
        }
        if (pd > 0)
            // 内存里那份还在用，重启才回落 —— 界面要按这个提示用户
            outObj["profiles_needs_restart"] = true;
        return outObj;
    }

    /// <summary>取整数（四舍五入）；取不到给 null。</summary>
    private static int? I0(JsonNode? n) => Num(n) is { } v ? (int)Math.Round(v) : null;

    /// <summary>取一位小数。</summary>
    private static double? F1(JsonNode? n) => Num(n) is { } v ? Math.Round(v, 1) : null;

    /// <summary>
    /// `GET /api/status` 的响应体。**优先给采样线程刷出来的那一帧**；
    /// 还没开始采样（或直接从测试里调）时现算一帧，保证不会回空。
    /// </summary>
    public JsonObject CachedStatus() => _cache ?? Status();

    // ================================================================ 全量 / 精简（2026-11-02）
    //
    // 托盘常驻时"有人在看"吗？多数时候没有 —— 而全量快照里有一半的段
    // （memory / network / system / display / disk_info / hwmon / pch / vendor）
    // 只有界面才用得上。于是采样线程按需求窗口决定拼哪一档：
    //   · 界面每次刷新、`/api/status` 每次被请求、诊断端点 ⇒ `DemandFullSnapshot()`；
    //   · 窗口关着、也没人轮询 ⇒ 只拼"没有就出事"的那几段（EC / 档位 / CPU·GPU / 电池）。
    // ⚠️ 判据是**需求窗口**（5 秒），不是"窗口开着吗"：界面每 500~1000 ms 刷一次，
    //    它自己会把窗口续上；这样既不用去问 UiHost，也不会因为一次刷新抖动就掉回精简档。

    /// <summary>全量快照的需求窗口（`TickCount64` 毫秒）。0 = 从没人要过。</summary>
    private long _fullUntil;

    /// <summary>需求窗口长度。比界面刷新间隔长一个数量级，够容忍一次卡顿。</summary>
    private const int FullDemandSec = 5;

    /// <summary>
    /// 「首帧快路径」允许复用的 EC 读数年龄上限（见 <see cref="BuildSnapshot"/> 开头）。
    ///
    /// 2 秒的道理：精简档最长 2 秒一拍（电池上 <c>SampleIntervalBattMs</c>），
    /// 取 2 秒正好覆盖"上一拍刚读完 EC"这个前提；超过这个年龄就老老实实重读。
    /// ⚠️ 别放宽到 5 秒以上：那样界面首帧可能拿 5 秒前的温度当现在，属于"显示错的"。
    /// </summary>
    private static readonly TimeSpan EcFastReuseAge = TimeSpan.FromSeconds(2);

    /// <summary>
    /// EC 读失败那一拍沿用上次读数的年龄上限（2026-11-19）。
    ///
    /// 只给界面/历史用，风扇曲线等控制路径走各自的实时读数，不受影响。
    /// 5 秒 = 插电 10 拍：足以跨过插拔电写 0x0300、睡眠唤醒这类短暂读失败窗口；
    /// EC 真挂了超过 5 秒就如实让转速/温度变 "--"，不拿旧值冒充实时值。
    /// </summary>
    private static readonly TimeSpan EcStaleMaxAge = TimeSpan.FromSeconds(5);

    private int _fullTicks, _leanTicks;

    /// <summary>
    /// 上一次读系统模式（APMD）失败的原因。同一个原因只打一行日志 ——
    /// 每秒一帧，不这样节流会把日志刷满。
    ///
    /// 为什么要留这个痕迹：读档位失败时这一帧 `perf` 是 null，界面就一直停在
    /// 「读取中…」（`UpdateModes` 只在 mode 非 null 时动文案）。2026-11-15 之前
    /// 这里是个空 `catch`，于是"系统模式半天读不到"在日志里一个字都没有 ——
    /// 真相是 `Bounds.TryToDouble` 在 int 节点上抛异常（见那边的注释）。
    /// </summary>
    private string? _perfErrLogged;

    /// <summary>「我要看全量」的信号。界面每刷一次、每个 `/api/status` 请求都调一下。</summary>
    public void DemandFullSnapshot(int seconds = FullDemandSec)
    {
        Interlocked.Exchange(ref _fullUntil, Environment.TickCount64 + seconds * 1000L);

        // 缓存还是精简档 ⇒ 说明"刚才没人看"，采样线程很可能正睡在拍与拍之间。
        // 这一下把它叫醒，首帧不用再等满一个采样间隔（见 AppState._wake）。
        // ⚠️ 只在**不是全量帧**时叫：全量帧已经在手的话，每秒一次的需求续期
        //    不能让采样线程脱拍（那等于把 1 秒一拍偷偷变成连续跑，白烧 EC 往返）。
        if (!HasFullStatus) _wake.Set();
    }

    /// <summary>这一拍该按全量还是精简拼。窗口一过就自动回到精简。</summary>
    private bool FullSnapshotDue() => Environment.TickCount64 < Interlocked.Read(ref _fullUntil);

    /// <summary>缓存帧是不是全量拼的。界面靠它决定"要不要先显示读取中"。</summary>
    public bool HasFullStatus => _frame is { Full: true };

    /// <summary>采样线程统计：全量 / 精简各走了多少拍（诊断用）。</summary>
    public (int Full, int Lean) SnapshotTickCounts => (_fullTicks, _leanTicks);

    /// <summary>
    /// 给"必须拿到全量"的调用方（HTTP `/api/status`、诊断、报表）：
    /// 缓存是精简档就**现算一帧全量**，并顺手把需求窗口续上。
    ///
    /// 为什么不直接把精简帧发出去：那会让外部脚本看到"少了一半键"的响应 ——
    /// 与"取不到就整键不出现"的约定撞车，看起来像字段丢了。
    /// </summary>
    public JsonObject CachedFullStatus()
    {
        if (_frame is { Full: true } f) return f.Node;
        DemandFullSnapshot();
        return Status();
    }


    /// <summary>
    /// 采样线程是否已经产出了第一帧快照。
    ///
    /// 给界面用：<see cref="CachedStatus"/> 在缓存还空着时会**当场现算**（读 EC，含 0.45s 节流），
    /// 那是后台采样线程该干的活；在 UI 线程上被调一次，窗口就卡一下。
    /// WPF 那边先看这个标志，没有就显示"读取中…"，等下一拍。
    /// </summary>
    public bool HasCachedStatus => _cache is not null;

    /// <summary>`GET /api/history` 的响应体：`{"points": [...]}`。</summary>
    public JsonObject History()
    {
        var arr = new JsonArray();
        lock (_histLock)
        {
            foreach (var p in _history)
            {
                // ⚠️ **七个键永远都在**，取不到的写 null（不是省略键）。
                //    取不到就写 null，不省略键；
                //    drawHistory() 遇到 null 会断线。省略键在 JS 里读出来是
                //    undefined，虽然也能断线，但契约形状就不一致了 —— P7 对账时
                //    会平白多出一堆"字段数量不同"的假差异。
                arr.Add(new JsonObject
                {
                    ["t"] = Math.Round(p.T, 3),
                    ["cpu_t"] = p.CpuT,
                    ["gpu_t"] = p.GpuT,
                    ["cpu_p"] = p.CpuP,
                    ["gpu_p"] = p.GpuP,
                    ["fan"] = p.Fan,
                    // 第二路风扇：`fan` 是 CPU 风扇（UFA1）、`fan2` 是 GPU 风扇（UFA2），
                    // 与 ec.fan1_rpm / ec.fan2_rpm 同源同义。
                    ["fan2"] = p.Fan2,
                });
            }
        }
        return new JsonObject { ["points"] = arr };
    }

    private void AppendHistory(JsonObject snap)
    {
        var ec = snap["ec"] as JsonObject;
        var gpu = snap["gpu"] as JsonObject;
        var cpu = snap["cpu"] as JsonObject;

        // gpu 段不可用（available 为假）⇒ 温度/功耗记 null，
        // 曲线自动断开。**不能**把残值当有效值画出去。
        var gpuOk = gpu?["available"]?.GetValue<bool>() ?? false;

        var pt = new HistPoint(
            T: (snap["ts"]?.GetValue<double>()) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            CpuT: Num(ec?["cpu_temp"]),
            GpuT: gpuOk ? Num(gpu!["temp"]) : null,
            CpuP: Num(cpu?["power"]),
            GpuP: gpuOk ? Num(gpu!["power"]) : null,
            Fan: Num(ec?["fan1_rpm"]),
            Fan2: Num(ec?["fan2_rpm"]));

        lock (_histLock)
        {
            _history.Add(pt);

            // 按**时间**裁，不是按点数 —— 卡片写「最近 150 秒」，缓冲区就必须真的是 150 秒。
            // 用最新点的时间戳当基准（而不是 DateTime.Now）：墙钟被回拨时阈值跟着走，
            // 不会把整段历史误判成"过期"。
            var cut = _history[^1].T - HistoryWindowSec;
            var drop = 0;
            while (drop < _history.Count - 1 && _history[drop].T < cut) drop++;
            if (drop > 0) _history.RemoveRange(0, drop);

            // 安全阀：时间裁剪失效（墙钟被往回调、T 不再单调）时兜底，别让缓冲无限长。
            if (_history.Count > HistoryMaxPoints)
                _history.RemoveRange(0, _history.Count - HistoryMaxPoints);
        }
        Interlocked.Increment(ref _histVersion);

        // 落盘（内部按 5 秒节流，见 HistoryStore）。内存里只留 150 点给曲线，
        // 长期数据在 history\hist-*.csv 里 —— 两者互不影响。
        // ⚠️ 任何异常都不许影响采样：历史是附加物，不是采样的前提。
        try
        {
            HistoryStore.Append(new HistorySample(pt.T, pt.CpuT, pt.GpuT, pt.CpuP, pt.GpuP, pt.Fan, pt.Fan2));
        }
        catch
        {
            // 写历史失败已经由 HistoryStore 记过一次原因，这里不再刷屏
        }
    }

    /// <summary>取数值节点，取不到给 null。⚠️ 不能用 TryGetValue&lt;double&gt;（不跨数值类型）。</summary>
    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        var kind = v.GetValueKind();
        if (kind is not (JsonValueKind.Number)) return null;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    // ================================================================ 快照

    /// <summary>`/api/status` 的响应体。默认**全量**（自检、诊断、脚本都按全量对账）。</summary>
    public JsonObject Status() => Status(true);

    /// <summary>
    /// 拼一帧。<paramref name="full"/> = false 时只拼"没人看也必须准"的部分（见 <see cref="FullSnapshotDue"/>）。
    ///
    /// ⚠️ 精简档**不做**的事，以及为什么：
    ///   · `_hw` 的六处 Find（HWiNFO）—— 托盘态 HWiNFO 本来就不跑，问了也是白问；
    ///   · `device` / `env` / `reboot` / `vendor` / `pch` / `hwinfo_block`
    ///     —— 全是界面与诊断用的展示字段（`vendor` 还要枚举进程）；
    ///   · 磁盘档案（每帧 IOCTL 探盘 + SMART）—— 插拔检测失败最多"晚一秒到界面"。
    /// ⚠️ 精简档**必须**保留：`ec`（历史/熄灯/档位）、`perf`（拔电切档）、
    ///    `cpu` / `gpu` / `battery`（历史曲线与拔电判定）、`fan_hw_max` + `ApplyHwMax`（安全件）。
    /// </summary>
    public JsonObject Status(bool full)
    {
        // EC 是跨进程共享的单槽缓冲区，本进程内也不能两个线程同时读 —— 采样线程
        // 与"手工调一次 Status"会互相顶掉命令。GmWmi 内部虽也串行化，但 _tick
        // 的轮转与慢变量的取值必须和整帧一致，所以在整帧这一层再加一道锁。
        lock (_sampleLock) return BuildSnapshot(full);
    }

    private JsonObject BuildSnapshot(bool full)
    {
        _tick++;
        var dev = Current;
        var tStart = Stopwatch.GetTimestamp();

        // ---- 首帧快路径：打开界面时不为"同一份 EC 数据"再读一遍 ----
        // 需求到达之前的那一拍是精简档，它刚读过 EC（≤2 秒，见 EcFastReuseAge）；
        // 而这一拍多出来的段（hwmon / pch / vendor / device / env…）
        // 全是"只有界面看得见"的东西，跟 EC 读数没有关系。再读一遍 EC 要付
        // 两发 0.45 秒的人为节流 ≈ 0.9 秒，而这 0.9 秒**全额**落在
        // 「点开窗口 → 首帧出数」这条路径上（实测出数 1.4 秒就是这么来的）。
        // 所以：上一帧是精简档 + EC 缓存还新鲜 ⇒ 复用那份 EC 读数与档位，只补显示段。
        // ⚠️ 只影响**这一帧**：下一拍（≤1 秒后）照常实时读 EC，复用的数据最多老 2 秒。
        // ⚠️ 控制路径（风扇曲线 / 拔电切档 / 熄灯）走的是各自读到的实时 EC，不经这里。
        var prev = _frame;
        var fastFirst = full && prev is { Full: false }
                         && _ec.CachedStatus(EcFastReuseAge) is not null;

        var snap = _sensors.Read(full);      // 全量 8 段 / 精简 3 段，见 StatusSnapshot.Read
        var tSensors = Stopwatch.GetTimestamp();

        // ---- 供电形态 → 下一拍睡多久（见 AppState.SampleIntervalAcMs）----
        // 取不到就**保持上一拍的值**：没有电池的机器（台式机 / 虚拟机）`battery.present`
        // 为 false，那种机器一直按插电的 1 秒跑，不会莫名其妙掉成 2 秒。
        // ⚠️ 而且只认**确认过**的读数（2026-11-17）：本机偶发把 ACLineStatus 报成 0 一秒多，
        //    照单全收会让采样节奏跟着 500 → 2000 → 500 抖一下（日志里还会多两行
        //    「电池供电 / 已接电源」，看着像用户真的拔插过）。去抖的判据与
        //    `WatchPowerSource` 里那道确认门同源，见 `PowerFlipConfirmMs`。
        if (snap["battery"] is JsonObject batt
            && batt["plugged"] is JsonValue pv
            && pv.TryGetValue<bool>(out var plugged)
            && PowerReadingSettled(plugged))
            _onBattery = !plugged;

        // ---- CPU 功耗 / 电压的两个补充来源 ----
        // 功耗：PDH RAPL 要连续采集 3 次才有值（见 RaplPower 的注释），
        //       前两帧必然是 null —— 这几帧正好由 HWiNFO 顶上，界面不会空着。
        // 电压：Windows 没有这个接口、EC 也没有这个字段，**只有 HWiNFO 有**。
        if (snap["cpu"] is JsonObject cpu)
        {
            if (cpu["power"] is null && full && _hw.FindCpuPower().Value is { } pw)
                cpu["power"] = Math.Round(pw, 1);
            if (cpu["voltage"] is null && full && _hw.FindCpuVoltage().Value is { } cv)
                cpu["voltage"] = Math.Round(cv, 3);
        }

        // ---- hwmon：三项原生拿不到、只能靠 HWiNFO 的读数 ----
        if (full) snap["hwmon"] = Hwmon();


        // ---- EC ----
        // ⚠️ 「在线吗」不再每帧问一次 `Available()`（2026-11-02）：那一问本身是一次
        //    完整的 WMI 枚举，而 EC 在不在**开机后就不会变**。判据改成：
        //      · 读成功 ⇒ 在线（每次真读都比探测准，这就是主判据）；
        //      · 读失败 ⇒ 才回头探一次，并置 false；
        //      · 每 60 拍兜底探一次，覆盖"一直没读过、实例被拔掉"的死角。
        if (_ecAvailable is null || _tick % 60 == 1)
        {
            var (probe, _) = _ec.Transport.Available();
            _ecAvailable = probe;
        }
        // 首帧快路径（见方法开头）：上一拍刚读到的 EC 原始字节，直接拿来用。
        if (fastFirst && _ec.CachedStatus(EcFastReuseAge) is { } cachedEc)
        {
            snap["ec"] = (JsonObject)cachedEc.DeepClone();
            _ecAvailable = true;
        }
        else
        {
            try
            {
                snap["ec"] = _ec.ReadStatus();
                _ecAvailable = true;
            }
            catch (Exception e)
            {
                // ⚠️ 不能把 ec 段整个换成 error 帧就完事（2026-11-19 用户报"风扇圈突然变 0"）：
                // fan*_rpm 字段一消失，UI 的 SetRpm(null) 立刻把转速环归零、数字变 "--"，
                // 下一拍读到真值又扫回来。gfx / pch / perf 读失败都沿用上次值，这里对齐。
                // 用 CachedStatus(5s) 而不是上一帧快照：EC 真挂了不会一直显示旧温度。
                var ec = new JsonObject { ["error"] = e.Message };
                EcStaleFallback.Merge(ec, _ec.CachedStatus(EcStaleMaxAge));
                snap["ec"] = ec;
                var (probe, _) = _ec.Transport.Available();
                _ecAvailable = probe;
            }
        }
        snap["ec_available"] = _ecAvailable ?? false;

        // ---- EC 读不到的温度/转速：HWiNFO 兜底（2026-11-02）----
        // 未标定 layout 的机型（其他广达模具）EC 只回原始字节 ⇒ 这里把 CPU 温度、
        // 显卡温度、两路转速补上，界面与历史曲线才有东西画。
        // ⚠️ 必须在**历史采样之前**（AppendHistory 读的就是这一帧的 ec），
        //    也必须在 EC 之后（原生有值时这个函数一个字节都不动）。
        // ⚠️ 只补快照，不补 `EcHal.CachedStatus()` —— 风扇曲线的温度来源保持"只认 EC"。
        FillEcFromHwInfo(snap);

        var tEc = Stopwatch.GetTimestamp();

        // ---- 性能模式（读组 0x0300，无副作用）----
        // 首帧快路径**也省掉这一发**：上一拍刚读过（≤2 秒），再读就是纯等待。
        // 档位是分钟级慢变量，Fn+F5 改档最迟下一拍（≤1 秒）就对齐。
        if (fastFirst && Num(prev!.Node["perf"]?["apmd"]) is { } prevApmd)
        {
            var (prevKey, _) = _ec.PerfModeKey((uint)Math.Round(prevApmd));
            snap["perf"] = new JsonObject { ["mode"] = prevKey, ["apmd"] = (uint)Math.Round(prevApmd) };
            if (prevKey is not null) _fan.ObservePerfMode(prevKey);
            // 背光跟着模式走：与风扇曲线同一个观测点（外部改档 / Fn 键也生效）
            if (prevKey is not null) ObserveBacklightMode(prevKey, CurrentBacklightLevel(snap));
        }
        else
        {
            try
            {
                var apmd = _ec.ReadPerfApmd();
                var (key, value) = _ec.PerfModeKey(apmd);
                snap["perf"] = new JsonObject { ["mode"] = key, ["apmd"] = value };
                // 曲线绑模式：每秒把 EC 真值喂给引擎（原厂控制中心 / Fn 键改模式也生效）。
                // 放在这里而不是采样线程里，是因为这一帧已经读到了 APMD，没必要再读一次。
                _fan.ObservePerfMode(key);
                // 同上：背光的档位联动也只认 EC 真值（原厂软件 / Fn 键改的档一样算）
                ObserveBacklightMode(key, CurrentBacklightLevel(snap));
                _perfErrLogged = null;          // 读回来了 ⇒ 下次失败照常报一行
            }
            catch (Exception e)
            {
                // ⚠️ 退出流程 Stop() 会 Interrupt 采样线程：正在 WMI 等 APMD 时被中断
                //    是正常退出（2026-11-19 实证：00:07:41 那条 ERR 出现在 00:07:42 quit 之前），
                //    记成 ERR 会让人误以为系统模式读不到、界面卡在「读取中…」。
                if (e is ThreadInterruptedException)
                {
                    snap["perf"] = null;
                }
                else
                {
                    // ⚠️ 不许静默（见 _perfErrLogged 的注释）：这一帧 perf 是 null，
                    //    界面就停在「读取中…」，而空 catch 会让这个状态在日志里毫无痕迹。
                    var why = e.GetType().Name + ": " + e.Message;
                    if (why != _perfErrLogged)
                    {
                        _perfErrLogged = why;
                        Console.Error.WriteLine(
                            "[perf] 读系统模式失败（这一帧 perf 为空，界面会停在「读取中…」）：" + why);
                    }
                    snap["perf"] = null;
                }
            }
        }
        var tPerf = Stopwatch.GetTimestamp();

        // ---- 背光按档重放的现状（界面据此知道"键盘上现在实际是哪一档的配色"）----
        // 重放是后台线程干的，完成时间不确定 ⇒ 不给界面猜，直接把后端结论推过去。
        snap["bl_follow"] = BacklightFollowState();

        // ---- FN+1 飓风镜像（WMI2Event 监听；还没收到过事件时是 null）----
        snap["fan_hw_max"] = _hotkey.FanHwMax;
        // ---- 「最大转速」在未开自定义风扇时的临时满转态（见 EcHal.SetOemFanMax）----
        // 界面靠它决定按钮写「最大转速」还是「恢复自动」。EC 读不回 UFA，
        // 真源只能是进程内这一步状态；重启/睡眠唤醒后 EC 自己回原生，标志归零也正好对得上。
        snap["fan_oem_max"] = _ec.OemFanMax;
        // ⚠️ 让位标志必须**每帧重新对齐**，不能只在 FN+1 事件回调里设一次
        //    （早先实现 2026-10-17 踩过）：`Stop()` / 故障自停会把 YieldHw 清成 false，
        //    而镜像还停在 true ⇒ 曲线恢复运行后会重新每秒写 UFA，把固件的 max 压掉。
        //    开机自动接管 + 30 秒自愈让这条路径变得更容易走到，所以这里是必须的。
        // ⚠️ 传 nullable 镜像本身，不要 `== true`：没收到过事件时镜像是 null，
        //    转换会把它当成 false ⇒ 采样线程反复清掉 YieldHw，FN+1 进 max 后
        //    Step 下一轮不再钉 100、按温度写回 59% ⇒「max 升到一半就不升了」。
        //    null 在 ApplyHwMax 里是"没消息，什么都别做"。
        try { _fan.ApplyHwMax(_hotkey.FanHwMax); } catch { /* 采样线程不许抛 */ }

        // ---- 显卡模式：多一次 EC 往返，降频到每 10 轮读一次 ----
        if (_gfxMode is null || _tick % 10 == 1)
        {
            try { _gfxMode = (int)_ec.ReadGfxMode()["gfx_mode"]!.GetValue<uint>(); }
            catch { /* 读不到就沿用上次，与早先实现一致 */ }
        }
        snap["gfx_mode"] = _gfxMode;

        // ---- PCH：变化很慢，5 轮一次（HWiNFO 专属，精简档不问）----
        if (full)
        {
            if (_pch is null || _tick % 5 == 1)
            {
                try { _pch = _hw.FindPch().Value; }
                catch { /* 保持上次值 */ }
            }
            snap["pch"] = _pch;
        }

        // ---- 只给界面/诊断看的几段：精简档一个都不拼 ----
        if (full)
        {
            // 计划中的重启：有就报剩余秒数，没有就如实报 planned=false
            snap["reboot"] = RebootState();

            // 原厂软件是否还在跑：遍历进程约 10 ms，10 轮一次
            if (_vendor is null || _tick % 10 == 5)
                _vendor = VendorRunning();
            snap["vendor"] = _vendor?.DeepClone();

            // 六项 HWiNFO 专属读数有没有一项（CPU 电压 / 显存结温 / 电池功率 / 虚拟内存）。
            // 判据必须是**这一拍真读到**的读数，不能算 `pch` —— 那一项是 `_pch` 缓存、
            // 读不到会一直沿用上次值（见上面"变化很慢，5 轮一次"那段），拿它当证据
            // 会让提示永远不出现（2026-11-22 踩到）。
            var hwAny = snap["cpu"]?["voltage"] is not null
                        || (snap["hwmon"] is JsonObject hwmon
                            && (hwmon["gpu"] is not null || hwmon["battery"] is not null || hwmon["vmem"] is not null));

            // HWiNFO 没给数（没在跑 / 在跑但读不到）：只说一句，不自动修
            snap["hwinfo_block"] = HwinfoBlock(hwAny);

            snap["device"] = dev.Summary();
            snap["env"] = _ec.EnvInfo();
        }

        // ⚠️ 报的是**最终写许可**（机型 ∧ 用户总闸），不是进程内部那道 `_writesEnabled`
        //    （生产路径恒 true）。同名键在 `/api/app/settings` 与 `/api/app/about` 里
        //    一直是 `WritesAllowed`；这里以前给的是内部值，于是用户关掉 EC 总闸后
        //    区间记录的心跳仍写「EC写:开」（2026-11-26 审查）。
        snap["ec_writes"] = dev.WritesAllowed;
        snap["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        // 拆账（微秒）：`/api/debug/mem` 只用来回答"这一拍花在哪"，不参与任何判断。
        UsSensors = Us(tStart, tSensors);
        UsEc = Us(tSensors, tEc);
        UsPerf = Us(tEc, tPerf);
        UsTotal = Us(tStart, Stopwatch.GetTimestamp());

        return snap;
    }

    /// <summary>最近一帧里 EC / 性能模式 / 整帧的自耗时（微秒）。诊断专用。</summary>
    public long UsEc, UsPerf, UsTotal, UsSensors;

    private static long Us(long from, long to)
        => (long)((to - from) * 1_000_000.0 / Stopwatch.Frequency);

    /// <summary>
    /// `hwmon` 段：显存结温 / GPU 核心电压 / 电池充放电功率 / 虚拟内存。
    /// 前三项**没有原生替代**（见 `HwInfoReader` 各处注释）；虚拟内存那项
    /// 业务上其实有原生路（psapi），这里对齐早先实现的结构一并给。
    /// 取不到的键**整个不出现**，界面按段隐藏。
    /// </summary>
    private JsonObject Hwmon()
    {
        var gpu = new JsonObject();
        if (_hw.FindGpuMemTemp().Value is { } mt) gpu["mem_temp"] = Math.Round(mt, 1);
        if (_hw.FindGpuVoltage().Value is { } gv) gpu["voltage"] = Math.Round(gv, 3);

        var batt = new JsonObject();
        if (_hw.FindBattRate().Value is { } rate) batt["rate_w"] = Math.Round(rate, 2);

        var outObj = new JsonObject();
        if (gpu.Count > 0) outObj["gpu"] = gpu;
        if (batt.Count > 0) outObj["battery"] = batt;

        var (vmem, _) = _hw.FindVmem();
        if (vmem is not null)
        {
            var v = new JsonObject();
            if (vmem["percent"] is { } p) v["percent"] = p;
            if (vmem["used_gb"] is { } u) v["used_gb"] = u;
            if (v.Count > 0) outObj["vmem"] = v;
        }
        return outObj;
    }
}
