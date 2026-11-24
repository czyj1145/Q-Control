using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.FanCurve;
using QControl.Core.History;
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
/// 进程级状态：把 ModelStore / Device / EcHal / 传感器 / HWiNFO 串成一份快照。
///
/// 采样循环（后台线程按固定间隔把各段拼成一帧缓存）：
/// `Start()` 起一个每秒跑一次的线程，`/api/status` 直接读它刷出来的那一帧。
///
/// ⚠️ P6-a 曾经是"请求来了现算"，读数是准的，但 `/api/history` 会变成
///    "只有开着页面时才有点"。现在改成真正的采样循环（2026-10-19）。
/// </summary>
public sealed partial class AppState : IDisposable
{
    /// <summary>原厂软件进程名（小写比较）。关掉界面后守护进程仍驻留，会和我们抢 FN 键。</summary>
    private static readonly string[] VendorProcs =
    [
        "controlcenter.exe", "controlcenterdaemon.exe", "cdsosrv.exe",
        "hkeyosdsvis.exe", "hkeyuserps.exe",
    ];

    private readonly ModelStore _model;
    private readonly StatusSnapshot _sensors;
    private readonly HwInfoReader _hw;
    /// <summary>
    /// NVML（显卡超频 + **GPU 读数**）。懒加载：nvml.dll 是 NVIDIA 驱动库，
    /// 加载 + 初始化要占一份工作集，所以字段初始化就写 <c>Instance</c> 等于在构造期
    /// 就把整个库拖进常驻内存。
    /// ⚠️ 2026-11-03 起**读数也走它**（<see cref="NvmlSensors"/>，经 GpuSensors 的懒路径）：
    ///    第一条采样就会初始化，所以"不玩超频就白养着"这句不再成立 —— 换来的是
    ///    每秒少起一个 nvidia-smi 进程。超频端点仍然复用同一个实例。
    /// </summary>
    private NvmlOc? _nvml;
    private NvmlOc Nvml => _nvml ??= NvmlOc.Instance;
    private readonly EcHal _ec;
    private readonly FanCurveEngine _fan;

    /// <summary>
    /// 曲线引擎的硬件适配层（把 EcHal 的 JsonObject 快照翻成强类型读数）。
    /// 单独留一份引用，方便以后在这里挂诊断；引擎本身只认 IFanHardware。
    /// </summary>
    private readonly EcFanHardware _fanHw;
    private readonly OsdWindow _osd;
    private readonly HotkeyService _hotkey;

    /// <summary>
    /// 系统开关类提示（CapsLock / NumLock / ScrollLock / 触控板 / 麦克风）。
    /// 走**轮询系统状态**，不碰 EC —— 这几个键在 EC 事件里只有一个笼统的 `evid=0x06`，
    /// 分不出按的是哪个（2026-11-25 实测，见 <see cref="Osd.KeyWatch"/>）。
    /// </summary>
    private readonly Osd.KeyWatch _keyWatch;

    /// <summary>
    /// 媒体键（播放/暂停、停止、上一首、下一首）的低级键盘钩子。
    /// **只在「全我们」/「两边都弹」模式下装**，其余模式不装 —— 常驻全局钩子不该白挂在系统上。
    /// </summary>
    private readonly Osd.MediaKeyWatch _mediaKeys;
    /// <summary>锁定 Win 键（低级键盘钩子，进程内，退出即自动恢复）。见 <see cref="Input.WinKeyLock"/>。</summary>
    private readonly Input.WinKeyLock _winLock = new();
    private readonly object _lock = new();

    /// <summary>
    /// HWiNFO 共享内存帧的进程级 TTL 缓存（1 秒）。**采样一帧里的 6 处
    /// Find* 调用 + 硬盘 SMART + 看门狗都走它** —— 否则每秒要整段解析 6 次。
    /// 刚重启完 HWiNFO 时 <see cref="HwFrameCache.Invalidate"/> 一下即可。
    /// </summary>
    private readonly HwFrameCache _hwCache;

    /// <summary>
    /// 历史曲线的**时间窗（秒）**。缓冲区保留"最近这么久"的采样，**不是固定 150 个点**。
    ///
    /// ⚠️ 2026-11-01 之前限的是点数（`HistoryLen = 150`），于是卡片上写「最近 150 秒」、
    ///    悬停却能查到 154 秒前 —— 采样是"睡到下一拍"（`Thread.Sleep(1000 - spent)`），
    ///    每拍实际 1.000~1.9 秒（读 EC 0.45s 节流的拍子必然超时且不补偿），
    ///    150 个点的时间跨度天然大于 150 秒。实测：150 点 / 152.077 秒 / 27 拍超时。
    ///    限秒之后点数会浮动（约 145~152），文案和悬停数字才对得上。
    /// </summary>
    // 2026-11-15 注：插电采样提到 0.5 秒后这个窗口里是约 300 点（电池上仍约 75 点），
    // 而 HistoryMaxPoints 的安全阀留了 600 —— 仍是一倍余量，不用跟着改。
    public const int HistoryWindowSec = 150;

    /// <summary>
    /// 缓冲点数的**安全阀**（不是语义上限）。
    /// 正常情况下 150 秒窗里只有约 150 点；只有墙钟被往回调（NTP / 手动对时）
    /// 时间裁剪会失效，没有这道闸就会一直堆下去。
    /// </summary>
    private const int HistoryMaxPoints = 600;

    /// <summary>
    /// 采样间隔：**插电 0.5 秒，电池上 2 秒**。
    ///
    /// 插电这一档 2026-11-15 从 1 秒提到 0.5 秒。能提的前提是同一轮把 EC 的人为节流
    /// 去掉了（读→读不再等 50 ms、读→写不再等 450 ms，见 `GmWmi.Throttle`）：
    /// 整帧从 76 ms 掉到 20 ms 上下，翻倍采样率之后每秒的 CPU 占用仍只有约 4% 单核，
    /// 而界面上的温度/转速/占用从"一秒一跳"变成"半秒一跳"，拔插电源、开负载的反应都跟手了。
    ///
    /// 为什么电池上**不提**：托盘常驻的采样是这台机器上唯一"永远在跑"的定时活动，
    /// 拔电时那份开销是纯浪费 —— 曲线的可读性在 2 秒一档完全不变。
    ///
    /// 不受这条影响的东西（有意为之）：
    ///   · **风扇曲线**在 `FanCurveEngine` 自己的线程里每秒一轮，不等采样拍 ——
    ///     降采样只让它的输入温度变旧，写 EC 的节奏不变；
    ///   · 拔电自动切办公按**墙钟**（`TickCount64`）计时，不数拍；
    ///   · 历史落盘由 `HistoryStore` 按 5 秒节流，与拍频无关。
    ///
    /// ⚠️ 按**拍**轮转的那些慢变量（显卡模式 10 拍 / PCH 5 拍 / 厂商进程 10 拍 /
    ///    EC 探活 60 拍 / 曲线自愈 30 拍 / trim 60 拍）跟着拍频一起变快或变慢：
    ///    插电时它们分别是 5 s / 2.5 s / 5 s / 30 s / 15 s / 30 s，电池上是它们的四倍。
    ///    这些都是分钟级的慢变量，**别再为此把它们改成按时间计数**。
    /// </summary>
    private const int SampleIntervalAcMs = 500;

    /// <summary>电池上的采样间隔。见 <see cref="SampleIntervalAcMs"/>。</summary>
    private const int SampleIntervalBattMs = 2000;

    /// <summary>
    /// 当前是否靠电池供电（`battery.plugged` 取反）。由采样线程每拍刷新，
    /// 只用来决定下一拍睡多久 —— **不是一个"状态"，别拿它代替快照里的 battery 段**。
    /// </summary>
    private volatile bool _onBattery;

    /// <summary>上一次记录过的供电形态，只在翻转时打一行日志。</summary>
    private bool _onBatteryLogged;

    private Device _device;
    private bool? _ecOverride;
    private int _tick;

    /// <summary>本实例起来的那一刻（「关于」页显示"启动时间 / 已运行"）。</summary>
    private readonly DateTime _startedAt = DateTime.Now;

    /// <summary>
    /// 同一起点的**单调时钟**（毫秒）。「已运行」用它算 —— 墙钟会被对时/NTP 往回调，
    /// 算出来会是负数（被钳成 0）或者凭空跳几小时；`started` 那行仍用墙钟，那是给人看的时间点。
    /// </summary>
    private readonly long _startedTick = Environment.TickCount64;
    private int? _gfxMode;
    private double? _pch;
    private JsonArray? _vendor;

    /// <summary>
    /// EC 在不在（`ec_available`）。null = 还没判过。由采样帧维护：
    /// 读成功置 true、读失败才回头探测 —— 别每帧调 `Available()`，那是一次完整 WMI 枚举。
    /// </summary>
    private bool? _ecAvailable;

    /// <summary>
    /// 共享内存此刻能出数。**跨线程读写的**（看门狗线程 / 拉起线程 / HTTP 诊断口），
    /// 所以要 volatile —— 不是修饰习惯，是 2026-10-31 日志里那条重复的
    /// 「共享内存已就绪」的成因：拉起线程写它、看门狗线程读不到，于是又打了一条。
    /// </summary>
    private volatile bool _hwinfoReady;

    /// <summary>本轮「就绪」是否已经打过日志（0/1，Interlocked）。翻回未就绪时清零。</summary>
    private int _hwReadyLogged;
    private int _hwinfoEnsuring;          // 单飞标志（Interlocked）

    // ---- HWiNFO 按需模式（2026-10-21）----
    /// <summary>"需要 HWiNFO" 到期的时刻（`Environment.TickCount64`，毫秒）。按需模式专用。</summary>
    private long _hwNeedUntil;

    /// <summary>上一拍是不是"需要"状态 —— 只在这个状态**翻转**的那一拍主动拉起。</summary>
    private bool _hwNeededPrev;

    /// <summary>「非管理员：不收进程」这句话只说一次，别每 2 秒刷一行。</summary>
    private bool _hwKeptLogged;
    private int _curveHealTick;           // 曲线自愈计数（每 30 拍试一次，与 _tick 分开）
    private JsonObject? _ident;

    // ---- 硬件档案：后台采一次 + 长期缓存 ----
    private readonly object _hwLock = new();
    private JsonObject? _hwProfile;
    private volatile bool _hwBusy;

    // ---- 采样循环 ----
    private readonly object _sampleLock = new();     // Status 是独占地读 EC 的，不能被两个线程同时进
    private readonly object _histLock = new();
    private readonly List<HistPoint> _history = new();

    /// <summary>
    /// 历史缓冲的**版本号**：每追加一个点 +1。
    ///
    /// 为什么需要它：<see cref="History"/> 每次调用都会按当前缓冲**重新拼一个**
    /// JsonObject（150 点 × 6 键 ≈ 一千个节点），而界面每秒要它一次 —— 缓冲里
    /// 90% 的拍子是没变化的，却照样重拼 + 反序列化一遍。
    /// 有了版本号，调用方就能"版本没变就复用上次的 DTO"，把这条每秒路径上唯一
    /// 一份无谓开销去掉（见 <see cref="LocalApi.History"/>）。
    /// </summary>
    private long _histVersion;

    public long HistoryVersion => Interlocked.Read(ref _histVersion);

    /// <summary>
    /// 最近一帧快照。**请求只读它，不自己现算** —— 与早先实现同构：
    /// 后台线程每秒刷一次缓存，`status()` 直接把那一帧给出去。
    /// 采样循环独立于请求，所以关掉页面再打开，历史曲线是接着的而不是从空开始。
    /// 用 volatile 是因为读者不拿锁：引用赋值是原子的，发布后不再改内容。
    ///
    /// ⚠️ 2026-11-02 起这一帧带"全量/精简"标记，所以包成 <see cref="SnapshotFrame"/>：
    ///    「节点」和「是不是全量」必须是**同一次原子发布**，否则读者可能拿到
    ///    "精简的内容 + 全量的标记"，把缺段的快照当成完整的发出去。
    /// </summary>
    private volatile SnapshotFrame? _frame;

    /// <summary>当前帧的 JSON 节点（只读）。</summary>
    private JsonObject? _cache => _frame?.Node;

    /// <summary>一帧快照 + 它是按哪一档拼的。见 <see cref="AppState.Status"/> 的说明。</summary>
    private sealed record SnapshotFrame(JsonObject Node, bool Full);


    private Thread? _sampler;
    private volatile bool _running;

    /// <summary>
    /// 「有需求了，别睡到下个整拍」的唤醒信号（<see cref="Loop"/> 的睡眠用它等）。
    ///
    /// 为什么需要：一拍要花 ~0.9 秒（两发 EC 往返各等 0.45s 节流），而拍与拍之间
    /// 还要睡到下一个 1 秒整点（电池上 2 秒）。用户点开窗口时采样线程多半正在睡，
    /// 于是首帧白白多等 0~1（电池 0~2）秒，界面上就是干看着「读取中…」。
    /// 现在 <see cref="DemandFullSnapshot"/> 在"缓存不是全量帧"时 Set 一下，
    /// 采样线程立刻结束睡眠进入下一拍 —— 只省等待，**不改采样频率**：全量帧一到手
    /// 就不再 Set，之后仍按原节奏走。
    /// </summary>
    private readonly AutoResetEvent _wake = new(false);

    /// <summary>
    /// 一条历史采样点。字段名对齐 `/api/history` 的 JSON 键。
    ///
    /// ⚠️ 2026-10-23 起风扇是**两路**（`fan` / `fan2`）：早先实现只记了 `fan1_rpm`，
    ///    于是曲线上只有一条「风扇」线，用户看不出是哪一个 —— 现在跟散热页两个图案、
    ///    顶栏「风扇」chip 一样两路都记（`fan` = CPU 风扇 / `fan2` = GPU 风扇）。
    /// </summary>
    private readonly record struct HistPoint(
        double T, double? CpuT, double? GpuT, double? CpuP, double? GpuP,
        double? Fan, double? Fan2);

    public AppState(string modelPath, int port = 8765)   // 8765 = 主端口（P8 起）
    {
        Port = port;
        _model = new ModelStore(modelPath);
        // 功耗墙功能已删（2026-11-12）⇒ 老配置里那一段一并清掉，别让 /api/model 继续吐死数据
        PurgePowerWallPresets();
        // 锁定 Win 键改成"不跨重启"（2026-11-21）⇒ 之前落盘的 presets.keyboard 没人读了，
        // 留着只会让人以为它还在生效
        PurgeWinLockPreset();
        // 历史采样落在 model.json 同级的 history\（与 logs\ 同一套规则；安装版在 ProgramData）
        HistoryStore.Init(modelPath);

        // 本机采集出来的机型档案：与 model.json 同级的 devices\（安装版 = %ProgramData%\Q-Control\devices）。
        // ⚠️ 必须在下面 Resolve 之前设置 —— Device 一旦造好就跟着 EcHal / 风扇引擎一路下去，
        //    之后再改档案目录只会改到"下一份档案"，不会改到这台机器正在用的那份。
        DeviceResolver.UseUserProfiles(
            Path.GetDirectoryName(modelPath) is { Length: > 0 } dataDir
                ? Path.Combine(dataDir, "devices")
                : null);

        var ident = MachineIdent.Collect();
        _device = DeviceResolver.Resolve(ident, _model.EcEnabled);
        _ecOverride = _model.EcEnabled;

        // SKU 认家族 / 升级：名字没认出来的机器按 EC 自报 SKU 认；已经被家族档案认出的机器，
        // 如果 SKU 说它是三风扇、而更专用的档案写了 `sku_claim_fans`，也在这里升级过去
        // （雷神猎刃16 就是这种情况：SMBIOS 可能是营销名，但 SYS 风扇不会骗人）。
        // ⚠️ 必须在下面造 GmWmi/EcHal **之前**换：档案一进 EcHal 就跟着风扇引擎一路下去，
        //    之后再换只能重建整个实例。认到了会在 matched_by 里追加 `ec_sku`。
        if (FamilyFallback.TryResolveBySku(_device, _model) is { } family)
            _device = family;

        var io = new GmWmi(_device.Ec);
        _ec = new EcHal(io, () => Current, _model);
        _fanHw = new EcFanHardware(_ec);
        _fan = new FanCurveEngine(_fanHw, _model);
        // 一份共享帧缓存：_hw（快照里的 hwmon 段）与 _sensors（硬盘 SMART）
        // 的 Find* 全部复用它，采样每帧只解析一次段。
        _hwCache = new HwFrameCache(() => SharedMemoryFrame.Read());
        _hw = new HwInfoReader(_hwCache.Get);
        _sensors = new StatusSnapshot(_hwCache);
        _hwinfoReady = SharedMemoryFrame.SegmentExists();

        // OSD 是"锦上添花"：窗口建不起来也照跑后端（Available=false 会如实反映到 /api/osd）
        _osd = new OsdWindow
        {
            // 模式决定"谁弹"（见 OsdWindow.Mode）：全原厂 / 原厂+背光 / 全我们 / 两边都弹。
            // 由 ApplyOsdMode 在 Start 里统一应用（含原厂 OSD 服务的启停与媒体键钩子）。
            Mode = ReadOsdMode(),
            Enabled = ReadOsdMode() != OsdWindow.ModeVendor,
            // 配色跟界面主题走：交一个**现读**的委托，每次弹出时读一次 presets.ui.theme，
            // auto 的"跟随系统"由 OsdPalette/ThemeIntent 现算（与托盘菜单同一套路子）。
            ThemeToken = () => Str(_model.Get("ui", "theme")),
        };
        // 原厂**界面**进程在跑时，FN+3 由它把 PERM 同步成 APMD —— 我们不能抢着写。
        // ⚠️ 只看 ControlCenter.exe，不看 daemon：daemon 不做同步（实测：它在跑时
        //    APMD 照样不动），按 daemon 判断会让 FN+3 永远让位给一个不干活的进程。
        _hotkey = new HotkeyService(_ec, _fan, _model, _osd,
            () => VendorRunning().Any(n => string.Equals(
                n?.GetValue<string>(), "ControlCenter.exe", StringComparison.OrdinalIgnoreCase)));

        _keyWatch = new Osd.KeyWatch(_osd);
        _mediaKeys = new Osd.MediaKeyWatch(_osd);

        // 自动记录：开机按 presets.log 恢复（默认关）。真源在 model.json，
        // 与"曲线开机无条件接管"不同 —— 这个会自己往磁盘写文件，默认不许默默开着。
        Logging.LogCapture.SetAuto(
            Truthy(_model.Get(JsonValue.Create(false), "log", "auto")),
            (int)(Num(_model.Get(JsonValue.Create(90), "log", "auto_temp")) ?? 90));
    }

    public ModelStore Model => _model;

    /// <summary>本实例的监听端口。自启命令要把它写进去 —— 用 `--port 9000` 跑过一次，
    /// 开机自启也该回到同一个端口，否则界面会连到别人的页面上。</summary>
    public int Port { get; }

    /// <summary>当前机型（总闸变了会自动重解析）。</summary>
    public Device Current
    {
        get
        {
            lock (_lock)
            {
                if (_model.EcEnabled != _ecOverride)
                {
                    _ecOverride = _model.EcEnabled;
                    _device = _device.WithEcOverride(_ecOverride);
                }
                return _device;
            }
        }
    }

    public EcHal Ec => _ec;

    /// <summary>风扇曲线引擎（`/api/fancurve` 与 `/api/fan/mode` 的唯一入口）。</summary>
    public FanCurveEngine Fan => _fan;

    // ================================================================ 采样循环

    /// <summary>
    /// 起后台采样线程。**必须在开始服务前调**，否则第一帧要等 1 秒。
    ///
    /// 为什么要有这个循环，而不是"请求来了现算"（P6-a 的做法）：
    ///   ① `/api/history` 需要连续的点。请求驱动的采集等于"只有你盯着屏幕时才记"，
    ///     关掉页面再打开曲线就是从空开始 —— 早先实现不是这样。
    ///   ② 风扇曲线 / OSD 差分 / 重启倒计时都要有一个"每秒走一次"的时钟，
    ///     这个循环就是它，后续直接挂上去即可。
    /// </summary>
    public void Start()
    {
        if (_sampler is not null) return;

        // 背光分区表自愈：要机型的 `caps.kb_zones` 才知道该有几个分区，
        // 所以放在 Start（构造函数里 Device 还没解析完）。
        PurgeExtraKbZones();

        _running = true;
        _sampler = new Thread(Loop)
        {
            IsBackground = true,
            Name = "sampler",
            // 采样会读 EC（含 0.45s 人为节流），给点余量但别和 UI 抢
            Priority = ThreadPriority.BelowNormal,
        };
        _sampler.Start();

        // FN 热键监听：两个 WMI 事件类，纯只读，不影响原厂软件
        _hotkey.Start();

        // 系统开关类提示（CapsLock/NumLock/ScrollLock/触控板/麦克风）：250 ms 轮询系统状态，
        // 只在"真的翻转"时弹一条。第一轮只建基线，开机不会连弹三条。
        _keyWatch.Start();

        // 提示条归属 + 「启动时自动退出原厂软件」**必须在同一条线程里按顺序做**（2026-11-25 修）：
        // 退出原厂会把 `hkosdservice` 一起停掉，如果两条线程各干一半（一条拉、一条停），
        // 最终状态取决于谁最后跑 —— 用户实测到的就是"开了自动退出原厂，提示条跟着哑掉"。
        // 所以这里只起一条线程：先按开关退出原厂，再按 osd_mode 把 OSD 服务评估一次。
        var killVendorOnStart = Truthy(_model.Get(JsonValue.Create(false), "vendor", "kill_on_start"));
        new Thread(() =>
        {
            if (killVendorOnStart) StopVendorOnStart();     // 内部：Stop → ApplyOsdMode
            else ApplyOsdMode();
        })
        {
            IsBackground = true,
            Name = "osd-mode",
        }.Start();

        // 锁定 Win 键：EC 方案下锁**不随退出消失**，所以启动时要按注册表把状态对上
        // （2026-11-22 用户改成走 EC）。读不到记录就什么都不做 —— 默认永远是不锁。
        // 位置在采样线程起来之后：这条要写 EC（走写组），不能在 EC 还没就绪时抢跑。
        RestoreWinKeyLock();

        // 「启动时自动退出原厂软件」（设置页那颗开关，配置键 `vendor.kill_on_start`）：
        // 开关名说的就是"启动时"，真动作必须在这里 —— 只写配置没人读，等于一句空话。
        // 后台跑：VendorControl 停完服务要等 1 秒给 SCM 收宿主进程，不能挡启动。
        // 「启动时自动退出原厂软件」的线程已并入上面那条 `osd-mode`：
        // 退出会把 OSD 服务一起停掉，必须先停、再按模式把它按需拉回来，
        // 分成两条线程会互相踩（谁最后跑，提示条就听谁的）。

        // 自动风扇曲线：**只在用户打开「自定义风扇」后接管**（2026-11-18 用户定，默认交回 EC 固件）。
        //
        // ⚠️ 不接管反而危险：EC 一旦拿到过手动转速就**永不自己接管**（实测 84 °C 下
        //    30% 锁死数小时），所以上次退出时我们写下的转速会被一直保持着 ——
        //    界面写着「自动」，风扇却停在退出那一刻的值上。
        // ⚠️ 位置：排在采样/热键之后、托盘之前的后台线程里，失败只记日志不抛 ——
        //    非管理员（调试）时这里必然失败，那是权限的物理边界，不是缺陷。
        new Thread(TakeOverFans) { IsBackground = true, Name = "fancurve-takeover" }.Start();

        // 确保 HWiNFO 在跑 —— PCH / CPU 电压 / 显存结温 / 电池功率 / SMART 只有它有。
        // 不这么做的话这六项会**整段缺失**（P7 A/B 实测：不预热时一条都取不到，
        // 因为这几项要等 HWiNFO 真正跑起来、轮询过一轮之后才有值）。
        // 后台跑：首次枚举硬件要几秒~几十秒，不能挡在启动路径上。
        // 同时它还是个**持续**看门狗：HWiNFO 会在跑满 12 小时后静默停止轮询
        // （进程还在、数据冻住），只藏一次/只启一次都不够（见 HwInfoHealth）。
        new Thread(HwinfoSupervisor) { IsBackground = true, Name = "hwinfo-ensure" }.Start();
    }

    /// <summary>
    /// 一次性清理（2026-11-17）：分区表里超出 `caps.kb_zones` 的条目 + 同名重复条目。
    ///
    /// 由来：探针往本机不存在的地址（8 / 7）试过红 / 绿，当时 <see cref="BacklightZones.Apply"/>
    /// 会把这些陌生地址**追加**进表，于是 `5/4/3` 涨成 5 条 —— 背光页冒出 A~E 五个 tab、
    /// 键盘示意图按五等分画（用户："怎么变成五区了"）。追加现在受机型声明约束
    /// （见 `PostHandlers.EcBacklight`），这里把已经写脏的表收干净。
    ///
    /// 幂等：一条都不多就**一个字节都不写**（别每次启动都刷 model.json）。
    /// </summary>
    private void PurgeExtraKbZones()
    {
        if (_model.Get("backlight", "zones") is not JsonArray zones || zones.Count == 0) return;
        var max = BacklightZones.Sanitize(Current.KbZones);
        var (kept, removed) = BacklightZones.Limit(zones, max);
        if (removed <= 0) return;

        _model.Set(["backlight", "zones"], kept);
        _model.Save();
        Console.WriteLine($"[backlight] 分区表清掉 {removed} 条多余条目"
                          + $"（机型声明 {max} 区，原表 {zones.Count} 条）");
    }

    /// <summary>
    /// 一次性清理（2026-11-12）：把三档 `presets.perf.modes[*].power` 整段删掉。
    ///
    /// **功耗墙功能已经整体移除**（用户："CPU 的功耗墙也删了"）。界面不显示、接口不输出、
    /// 学习逻辑也删了，但老配置里还留着这段死数据 —— 不清掉的话 `/api/model` 仍会吐出来，
    /// 而且打包时的契约自测（JSON→DTO→JSON 逐字段比对）会因为 DTO 里没有 `power`
    /// 而报"多出 N 个字段"，自测直接不通过。
    ///
    /// 幂等：一个 `power` 都没有就**一个字节都不写**（别每次启动都刷 model.json）。
    /// </summary>
    private void PurgePowerWallPresets()
    {
        if (_model.Get("perf", "modes") is not JsonObject modes) return;

        var dirty = false;
        foreach (var (_, v) in modes)
            if (v is JsonObject cfg && cfg.Remove("power")) dirty = true;
        if (!dirty) return;

        _model.Save();
        Console.WriteLine("[perf] 已清掉三档预设里的功耗墙段（该功能已移除）");
    }

    /// <summary>
    /// 一次性清理（2026-11-21）：删掉老版本留下的 `presets.keyboard`。
    ///
    /// Win 键锁改成"不跨重启、开机默认关闭"之后，这个配置键再没人读 ——
    /// 留在 model.json 里只会让下一个看配置的人以为它还在起作用。
    /// </summary>
    private void PurgeWinLockPreset()
    {
        if (_model.Presets is not JsonObject presets || !presets.Remove("keyboard")) return;

        _model.Save();
        Console.WriteLine("[winkey] 已清掉 presets.keyboard（锁定状态不再跨重启保留）");
    }

    /// <summary>
    /// 开机按 `presets.vendor.kill_on_start` 收一次原厂软件（顺序与边界全在 <see cref="VendorControl"/>）。
    ///
    /// 只碰**这一刻在跑**的进程：不动计划任务 —— 自启的抑制是 `vendor.block_autostart` 的事。
    /// ⚠️ 非管理员直接跳过：原厂进程是提权的，杀它必然 AccessDenied，试了只是刷一行红字。
    /// </summary>
    private void StopVendorOnStart()
    {
        try
        {
            if (!Elevation.IsElevated())
            {
                Console.WriteLine("[vendor] 启动：开关已开，但当前不是管理员，跳过（只读实例不动别人的进程）");
                return;
            }

            var r = VendorControl.Stop();
            var killed = Names(r["killed"]);
            var errors = Names(r["errors"]);
            _vendor = null;                     // 快照缓存：下一帧就按"已经不在了"报

            if (errors.Length > 0)
                Console.Error.WriteLine($"[vendor] 启动：退出原厂软件未完全成功 —— {errors}"
                                        + (killed.Length > 0 ? $"（已退出：{killed}）" : ""));
            else if (killed.Length > 0)
                Console.WriteLine($"[vendor] 启动：已退出原厂软件（{killed}）");
            else
                Console.WriteLine("[vendor] 启动：原厂软件本来就没在跑");

            static string Names(JsonNode? arr)
                => arr is JsonArray a ? string.Join(" / ", a.Select(n => n?.ToString() ?? "")) : "";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[vendor] 启动：退出原厂软件异常 —— " + ex.Message);
        }
        finally
        {
            // ⚠️ 退出原厂顺手停掉了 `hkosdservice`，而提示条可能正指着原厂画。
            //    不管上面成功与否，都按当前模式再评估一次：依赖原厂就把它拉回来，
            //    `只用我们` 则保持停。这是"两个开关不能互相打死"的那一半。
            //    只读实例（--ui-shot / 非管理员）本来就动不了服务，别去白试一遍。
            if (Elevation.IsElevated()) ApplyOsdMode();
        }
    }

    /// <summary>
    /// 开机接管风扇 —— **只在「软件曲线」总开关打开时**（2026-11-18 起默认不开，
    /// 默认交给 EC 固件；见 <see cref="FanCurveEngine.CurveEnabled"/>）。
    ///
    /// 开关打开时三件事，顺序不能换：
    ///   ① `_fan.Start()` 接管；
    ///   ② **接管成功之后**才把残留的 `mode: manual` 掰回 auto 并落盘。"开机默认自动"
    ///      必须是真的自动，而不是继续钉在昨天那个百分比上；
    ///   ③ 失败**只记日志**，30 秒后自愈线程会再试（见 <see cref="HealFanCurve"/>）。
    ///
    /// ⚠️ ①②的顺序是硬要求（2026-10-23 修）：以前是"先改配置再接管"，可这条路径对
    ///    **只读实例也照跑**（`--ui-shot` / `--selftest` / 非管理员启动），它们的 Start()
    ///    必然被拒，却已经把用户的 `mode: manual` 写成了 auto —— 于是用户设好的手动转速
    ///    被任何一次"启动看看"清掉，散热页滑块（只在 manual 下可用）随之变灰，
    ///    报障就是「手动风扇控制的滑块用不了了」。
    ///    接管不了就不该动配置：谁都没资格替用户改他没能生效的选择。
    ///
    /// ⚠️ 这条路**不看 `presets.fan.enabled`**：那个字段的"上次点过暂停就不接管"语义
    ///    已于 2026-10-18 作废（界面上也没有暂停入口了），`State()` 里它恒为 true。
    /// </summary>
    private void TakeOverFans()
    {
        try
        {
            // 2026-11-18 用户定：**默认让风扇听 EC 固件**，软件曲线是"自定义"功能，
            // 要用户在散热页自己打开（`presets.fan.curve_enabled`，默认 false）。
            //
            // 这条 if 不是"少做一件事"，而是整个"固件优先"的关键：
            // 只要没人写过 UFA，固件就按自己的曲线调速；一旦写过非零值，EC 会一直照那个值转
            // （实测 7.5 小时纹丝不动），**要交回得显式写两条命令**（0x0206=0 + UFA=0xFF，
            // 见 EcHal.ReleaseToEc）。所以"默认固件控制"最可靠的实现方式仍是开机一个字节都不写；
            // 交回只在用户关开关 / 退出程序时做，不在开机时猜。
            if (!_fan.CurveEnabled)
            {
                Console.WriteLine("[fancurve] 软件曲线未开启（默认）：风扇由 EC 固件按温度自动调速，"
                                  + "本程序不写风扇；需要自定义曲线请在散热页打开「软件曲线」");
                return;
            }

            var r = _fan.Start();
            if (r["ok"]?.GetValue<bool>() == true)
            {
                // 真接管了才归位：曲线在下一次 Step 里就会按温度写转速
                if (_fan.FanMode != "auto")
                {
                    Console.WriteLine("[fancurve] 启动：上次停在手动模式，已被自动曲线接管");
                    lock (_model.SyncRoot)
                    {
                        if (_model.Presets["fan"] is JsonObject fan) fan["mode"] = "auto";
                        _model.Save();
                    }
                }
                Console.WriteLine("[fancurve] 已接管风扇（软件曲线按温度调速）");
            }
            else
            {
                Console.Error.WriteLine(
                    "[fancurve] 接管失败：" + (r["error"]?.GetValue<string>() ?? r.ToJsonString())
                    + "\n           风扇暂时由 EC 固件控制。这类拒绝（权限 / EC 总闸）自愈不会重试，"
                    + "\n           修好原因后重启即可接管。配置保持原样（没接管成功就不改用户的模式选择），"
                    + "\n           手动模式下散热页的转速滑块仍可用。");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[fancurve] 接管异常：" + e.Message);
        }
    }

    /// <summary>
    /// 曲线自愈：每 30 拍试一次重新接管。
    ///
    /// 为什么需要：曲线只可能因**故障**自停（连续异常 / 写入被拒），而界面上
    /// 「启用曲线」按钮早已随「暂停曲线」一起删掉 ⇒ 没人再点得回来。
    /// 自愈成功后 `Start()` 会清掉 `LastError`，界面自然从「已自停」回到「运行中」；
    /// 失败就静默等下一轮（原因留在 `LastError` 里，不会丢）。
    ///
    /// ⚠️ **只在 `mode == auto` 时重试**：`manual` 是用户当场做的选择（钉死转速），
    ///    自愈去抢就等于「用户设的手动自己变回曲线」。
    /// </summary>
    private void HealFanCurve()
    {
        // ⚠️ 观察原厂 / 一键采集期间**不许自愈**（2026-11-26 审查）：
        //    采集会自己 `Stop(release:false)` 把转速钉住、满载采样，那段时间"曲线没在跑"
        //    是**故意的**；接回来就等于在观察窗口里写 UFA —— `load_samples` 的转速序列
        //    被打断，"原厂在控还是我们在控"的因果证据当场作废。
        //    与 `TryFanStuckRescue` ②同一条闸门（那边 11-23 补过，这边当时漏了）。
        if (Observing || Adopting) return;

        if (++_curveHealTick % 30 != 0) return;
        if (_fan.Running) return;
        if (_fan.FanMode != "auto") return;
        // 软件曲线总开关关着 = 用户要的就是固件调速（默认态）。
        // 自愈在这里抢一次就等于"关掉的曲线自己又跑起来"，所以必须先看这个键。
        if (!_fan.CurveEnabled) return;
        // 总闸关着 = 用户自己的选择，别抢（关的时候曲线本来就是被我们停掉的）。
        // 重新打开后由设置页立刻接一次，这里只是兜底：万一那次没接上（设备缓存还没刷新、
        // 恰好在采样线程里等等），30 秒内一定能回来。
        if (!Current.WritesAllowed) return;
        if (!_ec.IsAdmin()) return;            // 非管理员写了也不生效，别每 30 秒叫一次

        // ⚠️ 不再要求 `LastError is not null`（2026-11-12 修）：`Stop(release:false)`
        //    （设置页关 EC / 切手动）本来就会把 LastError 清成 null —— 旧写法于是把
        //    "第一次因为别的原因没接上" 变成永久状态：running:false、last_error:null，
        //    界面停在"启动中…"，风扇定在最后写入的转速上，只能重启程序。
        //    现在的不变量很简单：**mode == auto 且写得进去 ⇒ 曲线就该在跑**。
        try { _fan.Start(); } catch { /* 自愈失败静默等下一轮 */ }
    }

    /// <summary>
    /// 起完先 ensure 一次，然后每 2 秒看一次「共享内存还在出数吗」——
    /// 失败按阶梯计数、逐级加重处理，成功一次就清零。
    ///
    /// ⚠️ 判据是**能解析出新鲜读数**，不是「段打得开」：HWiNFO 退出后段会残留，
    ///    结构完整、数据是它退出前最后一帧（见 SharedMemoryFrame）。
    /// ⚠️ 阶梯阈值只在**持续失败**时命中（成功即清零），所以正常的关掉-再打开
    ///    不会触发任何抢着重启的动作。
    /// </summary>
    private void HwinfoSupervisor()
    {
        // 第一轮**不睡**
        // ⚠️ 2026-11-03：以前是循环体开头无条件 `Thread.Sleep(2000)`，于是开机后
        //    头 2 秒这个看门狗什么都不做 —— HWiNFO 本来就还没起，等于白等；
        //    而 HWiNFO 从拉起到共享内存出数是它自己决定的 **5.9 秒**（实测），
        //    这 2 秒直接叠在"开机后 PCH / CPU 功耗电压 / 显存结温 / SMART 全空"的窗口上。
        //    改成"先干一次再睡"以后，拉起动作和主程序的初始化并行起来，空窗少了 2 秒。
        // ⚠️ 不能简单把 Sleep 挪到循环末尾：循环体里有多个 `continue`，那些分支会绕过
        //    Sleep 变成忙等。用这个标志位才两全 —— 之后每一轮（含 continue 之后）照样睡。
        var first = true;
        while (_running)
        {
            if (!first) Thread.Sleep(2000);
            first = false;
            try
            {
                // ---- 按需 / 关闭：不需要就收掉自己那只，且**不计失败** ----
                // （主动停掉不是故障；计了就会被阶梯阈值当成"数据没了"再拉起来，来回打架）
                if (!HwinfoNeeded())
                {
                    // ⚠️ 非管理员时**不收**自己拉起的那只（2026-10-22）：
                    //    收掉容易，可下次再要数据就得重新拉起，而拉起 HWiNFO 是一发
                    //    UAC（它的清单是 requireAdministrator，绕不过去）。
                    //    省那 82 MB 换来「每小时弹一发 UAC」，不值。
                    if (Elevation.IsElevated())
                    {
                        StopHwinfo(HwinfoMode() == "off" ? "模式=关闭" : "没人再看，空闲超时");
                    }
                    else if (!_hwKeptLogged && HwInfoProcess.OursRunning())
                    {
                        _hwKeptLogged = true;
                        Console.WriteLine(
                            "[hwinfo] 非管理员：自己拉起的那只不再收掉"
                            + "（收掉容易，重新拉起要再弹一次 UAC；想要按需省内存请以管理员身份运行）");
                    }
                    _hwNeededPrev = false;
                    continue;
                }

                // 刚从"不需要"翻成"需要"：立刻拉起，别等失败阶梯（那一套要 6 秒才动）
                if (!_hwNeededPrev)
                {
                    _hwNeededPrev = true;
                    KickEnsure();
                    continue;
                }

                // ⚠️ 这里只**读段头**判新鲜度，不整帧解析（2026-11-03）：本行每 2 秒走一次，
                //    而整帧解析是 194 KB/次、0.5 次/秒 —— 实测占托盘态全部分配量的一半以上，
                //    换来的只是"段还在吗"这一个布尔。判据不变（签名 / 条数 / poll_time 新鲜），
                //    见 SharedMemoryFrame.ProbeFresh。
                // ⚠️ **但第一次必须真解析**：段能读 ≠ 认得这个布局（未标定的机型 / 别的
                //    HWiNFO 大版本）。没验证过就先整帧读一次，验过之后才走段头快路 ——
                //    否则布局对不上时这里会一直报"就绪"，而那六项永远是空的、连自救都不触发。
                var fresh = _hwinfoReady
                    ? SharedMemoryFrame.ProbeFresh()
                    : SharedMemoryFrame.Read(requireFresh: true) is not null;
                if (fresh)
                {
                    MarkHwinfoReady();
                    HwInfoHealth.NoteOk();
                    continue;
                }

                MarkHwinfoNotReady();
                var streak = HwInfoHealth.NoteFail();

                // ① 进程还活着、却连续十来分钟一个数都没有 ⇒ 疑似 12 小时到期
                //    （免费版停止轮询），只有重启进程能恢复。
                if (HwInfoHealth.ShouldHeal(streak) && HwInfoProcess.IsRunning())
                {
                    Console.Error.WriteLine(
                        $"[hwinfo] 连续 {streak} 次读不到但进程仍在 ⇒ 疑似停止轮询，重启它");
                    KickEnsure();
                    continue;
                }

                // ② 阶梯点：交给 ensure（它会按归属规则决定「拉起」还是「自救」）
                if (HwInfoHealth.ShouldSpawn(streak)) KickEnsure();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[hwinfo] 看门狗异常：" + e.Message);
            }
        }
    }

    /// <summary>后台跑一次 ensure（单飞：同一时刻只允许一条，避免线程叠加）。</summary>
    private void KickEnsure()
    {
        if (Interlocked.CompareExchange(ref _hwinfoEnsuring, 1, 0) != 0) return;
        new Thread(() =>
        {
            try { EnsureHwinfo(); }
            finally { Volatile.Write(ref _hwinfoEnsuring, 0); }
        })
        { IsBackground = true, Name = "hwinfo-recover" }.Start();
    }

    /// <summary>
    /// 「共享内存能出数了」—— 置位 + 打**一条**日志（每次就绪只打一次）。
    ///
    /// 为什么要有这个函数而不是各处各写一遍：这件事**两条线程同时在看**
    /// （看门狗每 2 秒看一次、拉起线程在等首帧），各自 `if (!_hwinfoReady)` 会各打一条 ——
    /// 2026-10-31 13:37 的日志里就是两条一模一样、连耗时都相同的「已就绪」。
    /// 置位前后的判断必须是**原子的**（Interlocked.Exchange），否则窗口一开就是两条。
    /// </summary>
    private void MarkHwinfoReady()
    {
        _hwinfoReady = true;
        if (Interlocked.Exchange(ref _hwReadyLogged, 1) != 0) return;

        HwInfoProcess.NoteReady();      // 拉起耗时测量（诊断口 mem.launch_ms）
        Console.WriteLine($"[hwinfo] 共享内存已就绪（拉起耗时 {HwInfoProcess.LastReadyMs?.ToString("F0") ?? "?"} ms）");
    }

    /// <summary>共享内存又不出数了（或主动收掉了进程）：清标志，让下次就绪重新打一条。</summary>
    private void MarkHwinfoNotReady()
    {
        _hwinfoReady = false;
        Volatile.Write(ref _hwReadyLogged, 0);
    }

    /// <summary>
    /// <c>/api/status</c> 取快照：顺手续上按需模式的需求窗口 ——
    /// 有客户端在轮询就等于"有人在看着那个界面"。
    ///
    /// ⚠️ 同时要续**全量快照**的窗口：外部脚本按全量对账（`--selftest` 逐个字段数），
    ///    发一份精简档过去，那边的第一反应是"字段丢了"。
    /// </summary>
    public JsonObject RequestHwinfoAndStatus()
    {
        RequestHwinfo();
        return CachedFullStatus();
    }

    // ---------------------------------------------------------------- HWiNFO 按需模式

    /// <summary>
    /// `presets.hwinfo.mode`：`always`（常驻，旧行为）/ `ondemand`（按需，**默认**）/ `off`（不用）。
    /// 读不到或值不合法一律按 `ondemand`。
    ///
    /// 为什么默认改成按需（2026-10-21 实测）：HWiNFO 单只进程吃 **82 MB 专用内存**，
    /// 占整机 230 MB 的 36%，而拉起到出数只要 **2.68 秒** —— 常驻不值。
    /// 那 6 项（PCH / CPU 电压 / PL1·PL2 / 显存结温 / 显卡电压 / 硬盘 SMART）
    /// 只在**有人在看的那一会儿**才需要。
    /// </summary>
    public string HwinfoMode()
    {
        if (_model.Presets["hwinfo"] is JsonObject h
            && h["mode"] is JsonValue v && v.TryGetValue<string>(out var s)
            && s is "always" or "ondemand" or "off")
            return s;
        return "ondemand";
    }

    /// <summary>按需模式的空闲超时秒数（没人再要数据多久后收掉进程），默认 60。</summary>
    public int HwinfoIdleSec()
    {
        if (_model.Presets["hwinfo"] is JsonObject h
            && h["idle_sec"] is JsonValue v && v.TryGetValue<double>(out var n) && n >= 5)
            return (int)n;
        return 60;
    }

    /// <summary>
    /// 「我要看那些数据」的信号 —— WPF 界面每秒刷新时、`/api/status` 每次取快照时调一下。
    /// 按需模式下把需求窗口往后延 <c>idle_sec</c> 秒；`always` / `off` 下什么都不做。
    /// </summary>
    public void RequestHwinfo()
    {
        if (HwinfoMode() != "ondemand") return;
        Interlocked.Exchange(ref _hwNeedUntil, Environment.TickCount64 + HwinfoIdleSec() * 1000L);
    }

    /// <summary>这一拍到底需不需要 HWiNFO 在跑（看门狗每 2 秒判一次）。</summary>
    private bool HwinfoNeeded() => HwinfoMode() switch
    {
        "always" => true,
        "off" => false,
        _ => Environment.TickCount64 < Interlocked.Read(ref _hwNeedUntil),
    };

    /// <summary>
    /// 收掉自己拉起的那只（用户自己开的那份永远不碰）。
    ///
    /// ⚠️ 收完必须 `HwInfoHealth.Reset()`：之后共享内存当然是读不到的，
    ///    不清零就会在下次切回"需要"时带着旧 streak 直接触发阶梯 / 自愈（见那里的注释）。
    /// </summary>
    private void StopHwinfo(string why)
    {
        if (!HwInfoProcess.OursRunning()) return;
        HwInfoProcess.StopOurs();
        HwInfoHealth.Reset();
        MarkHwinfoNotReady();
        _hwCache.Invalidate();
        Console.WriteLine("[hwinfo] 按需：已停掉自己的实例（" + why + "）");
    }

    /// <summary>
    /// 确保 HWiNFO 在跑。规则：
    ///   ① 共享内存**真能出数**（段在且解析得出来）→ 谁的实例都行，直接用，不碰它；
    ///   ② 有进程但没数据：**我们自己的**才自救（只杀自己的 PID），
    ///      别人开的那份绝不杀、不藏、也不并排再起一个（会抢同一个段名）；
    ///   ③ 没进程 → 起一个，然后等共享内存就绪（最多 45 秒）。
    ///
    /// ⚠️ 判据必须是"能解析出读数"，不能用"段打得开" —— 段会残留（见 SharedMemoryFrame）。
    /// </summary>
    private void EnsureHwinfo()
    {
        try
        {
            // 走帧缓存：能拿到（非 null）就说明段里有新鲜数据，不用再解析一遍。
            // 拿不到时 Get() 不会缓存 null，下一拍照样重新探 —— 判据不变，只是省了重复解析。
            if (_hwCache.Get() is not null)
            {
                MarkHwinfoReady();
                return;
            }

            if (HwInfoProcess.IsRunning())
            {
                // 自己的那只没数据 → 自救；别人的 → 不动（数据来自它的共享内存）
                if (HwInfoProcess.OursRunning())
                {
                    HwInfoProcess.RestartIfStalled(waitSec: 45);
                    // 重启后它又会弹窗口 + 挂托盘图标 ⇒ 再收一轮（只收我们自己的）
                    HwInfoUi.HideWatchdog();
                    _hwCache.Invalidate();   // 新实例的段，旧帧作废
                }
                return;
            }

            // ⚠️ 这一行是「UAC 到底是谁弹的」的判据：HWiNFO64 的清单是
            //    requireAdministrator，父进程**没提权**时拉它就会弹一发 UAC；
            //    已提权则直接起来（凌晨日志里 2.6 秒就绪就是这种情况）。
            var admin = Elevation.IsElevated();
            Console.WriteLine($"[hwinfo] 拉起 HWiNFO（本进程已提权={(admin ? "是" : "否 → 会弹一次 UAC")}）");

            var (ok, detail, _) = HwInfoProcess.Launch();
            if (!ok)
            {
                Console.Error.WriteLine("[hwinfo] 拉起失败：" + detail);
                return;
            }
            _hwCache.Invalidate();   // 拉起前可能缓存过"段不存在"的 null

            // 它一启动就有窗口和托盘图标。归属规则：只收我们拉起的这只，
            // 用户自己开的那份从头到尾都不碰（HwInfoUi.Hide 里再兜一层）。
            HwInfoUi.HideWatchdog();

            var until = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < until)
            {
                if (SharedMemoryFrame.Read(requireFresh: true) is not null)
                {
                    MarkHwinfoReady();
                    return;
                }
                Thread.Sleep(1000);
            }
            Console.Error.WriteLine("[hwinfo] 45 秒内没等到共享内存（HWiNFO 可能停在开始界面）");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[hwinfo] ensure 异常：" + e.Message);
        }
    }

    private void Loop()
    {
        while (_running)
        {
            var started = Environment.TickCount64;
            var full = FullSnapshotDue();
            try
            {
                var snap = Status(full);      // 内部已加锁；托盘态走精简档，见 AppState.Status
                _frame = new SnapshotFrame(snap, full);
                if (full) _fullTicks++; else _leanTicks++;
                AppendHistory(snap);

                // 硬件档案预热（2026-11-08）：首个快照出来后静默采一次，用户点开硬件页
                // 就是 0 毫秒。等快照就绪再采是硬要求 —— Collect 要读 status 里的
                // power_plan / gpu_info / perf，拿 null 采下来的档案不会自愈。
                // 幂等：已有结果或正在采都直接返回，每拍只是一个字段读。
                try { WarmupHardware(); } catch { /* 预热不许影响采样 */ }

                // 插拔电源自动切档（presets.perf.auto_office_on_battery）：
                // 挂在采样这一拍上 —— 它就是"每秒走一次的时钟"，且关着界面也照样走。
                try { WatchPowerSource(snap); } catch { /* 自动切档不许影响采样 */ }

                // 键盘背光自动熄灭（presets.light）：闲置 / 锁屏判定挂在这一拍，
                // 真正的 EC 写入丢后台线程（三区背光要 1.4 秒，不能占采样）。
                try { TryAutoOffBacklight(snap); } catch { /* 自动熄灯不许影响采样 */ }
            }
            catch (Exception e)
            {
                // 出错时的语义（整帧换成一条 error）：
                // 出错的这一帧**替换** cache 而不是保留旧值，否则界面会拿一帧多年前的数字
                // 装作还在更新。界面看到 error 会显示断线。
                _frame = new SnapshotFrame(new JsonObject { ["error"] = e.Message }, full);
            }

            // 曲线自愈：每 30 拍（≈30 秒）试一次，和采样同频，不另起线程
            try { HealFanCurve(); } catch { /* 自愈本身不许影响采样 */ }

            // 风扇卡死自救（未开手动转速时）：判据见 Core/FanCurve/FanStuckGuard。
            // 排在自愈之后 —— 自救自己会 Stop/Start，先把"曲线根本没跑"那种交给自愈。
            try { TryFanStuckRescue(_cache); } catch { /* 自救不许影响采样 */ }

            // 区间记录心跳：**只在"正在记录"的那几分钟里**每 2 拍写一行紧凑状态。
            // 为什么需要：平时后端只记"事件"，一段没问题的时间窗口里日志是一片空白，
            // 导出的文件会只有头尾两行 —— 那对"截取问题区间"毫无意义。
            // 有了心跳，这段日志就是一条能对时间轴的温度/转速曲线，问题时刻前后都有数。
            if (_tick % 2 == 0)
            {
                try { CaptureBeat(_cache); } catch { /* 心跳不许影响采样 */ }
                try { CaptureAutoCheck(_cache); } catch { /* 自动记录判定也不许影响采样 */ }
            }

            // 空闲归还工作集：每分钟问一次「界面关着 + 距上次满 15 分钟」。
            // 只托盘常驻时每秒采样还在分配对象，工作集会慢慢爬回来，隔一段收一次
            // 才能长期停在低位（关窗那一次由 UiHost 自己触发，这里管的是长期漂移）。
            // ⚠️ 启动前 3 分钟不动手：那会儿正在预热（硬件档案 / EC / HWiNFO），
            //    换出去马上又被拉回来，白付缺页。
            // ⚠️ TrimAsync 内部丢线程池，这一拍不会被 GC 卡住。
            if (_tick % 60 == 0 && (DateTime.Now - _startedAt).TotalMinutes >= 3)
            {
                try { if (MemTrim.IdleDue) MemTrim.TrimAsync("空闲"); } catch { /* 不许影响采样 */ }
            }

            // 供电形态决定下一拍睡多久（插电 1 秒 / 电池 2 秒，见 SampleIntervalAcMs）。
            // `_onBattery` 由 BuildSnapshot 从 `battery.plugged` 刷新，这里只读。
            // 只在翻转的那一拍打一行 —— 拔插电源是用户能感知的事件，日志里该有痕迹。
            if (_onBattery != _onBatteryLogged)
            {
                _onBatteryLogged = _onBattery;
                Console.WriteLine(_onBattery
                    ? $"[sample] 电池供电：采样间隔 {SampleIntervalBattMs} ms（风扇曲线仍每秒一轮）"
                    : $"[sample] 已接电源：采样间隔 {SampleIntervalAcMs} ms");
            }

            var interval = _onBattery ? SampleIntervalBattMs : SampleIntervalAcMs;
            var spent = Environment.TickCount64 - started;
            var wait = interval - (int)spent;
            if (wait > 0)
            {
                // ⚠️ 用 WaitOne 而不是 Sleep：`DemandFullSnapshot()` 会在"缓存还是精简档"
                //    时 Set 一下，把"等下一个整拍"变成"立刻出这一帧"（见 _wake）。
                //    Stop() 走 Interrupt()，所以这层 catch 必须留着。
                try { _wake.WaitOne(wait); }
                catch (ThreadInterruptedException) { break; }
            }
        }
    }

    /// <summary>
    /// 区间记录心跳：正在记录时每 2 拍写一行紧凑状态（`[记录] …`）。
    ///
    /// 平时后端**只记事件**，一段平安无事的窗口里日志是空白 —— 那样"截取问题区间"
    /// 只会得到头尾两行标记，等于没用。加上心跳后，导出的那段就是一条能对时间轴的
    /// 温度 / 占用 / 转速 / duty 曲线，问题发生前后都有数可看。
    /// ⚠️ 只在 `LogCapture.Active` 期间写：不进日常日志（否则 8MB 上限会被心跳自己顶爆），
    ///    这也是"全量日志照旧、区间只是多切一份"的边界。
    /// </summary>
    private void CaptureBeat(JsonObject? snap)
    {
        if (snap is null || !Logging.LogCapture.Active) return;

        var cpu = snap["cpu"] as JsonObject;
        var gpu = snap["gpu"] as JsonObject;
        var ec = snap["ec"] as JsonObject;
        var perf = snap["perf"] as JsonObject;        // ⚠️ CPU 温度不在 status.cpu 里（那边只有占用/频率/功率），实测温度在 `ec.cpu_temp`
        //    （EC 侧）—— 没有 EC 的机型就干脆不显示，别拿 -- 占位。
        // ⚠️ 曲线 duty 走 `Fan.State()`：它不在 /api/status 里（是另一个端点），
        //    每 2 拍取一次只在记录期间发生，开销可以忽略。
        var fan = Fan.State() as JsonObject;
        var duty = Num(fan?["duty"]) ?? Num(fan?["stuck_duty"]);

        var parts = new List<string>();
        parts.Add($"CPU {(ec is not null && Num(ec["cpu_temp"]) is { } ct ? I0(ct) + "°C" : "?°C")}"
                  + $"/{I0(cpu?["usage"]) ?? 0}% {F1(cpu?["power"]) ?? 0}W");
        if (gpu is not null && Truthy(gpu["available"]) != false)
            parts.Add($"GPU {I0(gpu["temp"]) ?? 0}°C/{I0(gpu["usage"]) ?? 0}%");
        if (ec is not null)
            // 三风扇机型（layout 认到 fan3_rpm）就把第三路也写进这一行；没有就还是两路。
            parts.Add(I0(ec["fan3_rpm"]) is { } f3
                ? $"风扇 {I0(ec["fan1_rpm"]) ?? 0}/{I0(ec["fan2_rpm"]) ?? 0}/{f3} RPM"
                : $"风扇 {I0(ec["fan1_rpm"]) ?? 0}/{I0(ec["fan2_rpm"]) ?? 0} RPM");
        if (duty is not null) parts.Add($"duty {I0(duty)}%");
        if (perf is not null) parts.Add($"模式 {Str(perf["mode"]) ?? "-"}");
        parts.Add("EC写:" + (Truthy(snap["ec_writes"]) ? "开" : "关"));

        Console.WriteLine("[记录] " + string.Join(" | ", parts));
    }

    /// <summary>
    /// 自动记录判定：与心跳同一个节拍（每 2 拍）调一次，温度从同一帧快照里取。
    /// ⚠️ 放在心跳旁边是刻意的：两件事都用同一帧数据、同一个节拍，
    ///    分开放会多一次锁竞争，也容易两边节拍不一致（开录的那一拍没有心跳行）。
    /// </summary>
    private void CaptureAutoCheck(JsonObject? snap)
    {
        if (snap is null || !Logging.LogCapture.AutoOn) return;
        var ec = snap["ec"] as JsonObject;
        var gpu = snap["gpu"] as JsonObject;
        Logging.LogCapture.AutoCheck(Num(ec?["cpu_temp"]), Num(gpu?["temp"]));
    }
}
