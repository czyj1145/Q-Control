using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.App.Http;
using QControl.App.Http.Contract;
using QControl.Core.Devices;

namespace QControl.App.Ui;

/// <summary>
/// 已保存的背光（`presets.backlight`）。<c>Zones</c> 恒为 3 项，下标 = 界面 A/B/C（EC 地址 5/4/3）。
/// <c>Effect</c> 是 EC 灯效码（常亮 1 / 呼吸 3 / 多彩循环 6 / 流光溢彩 7），null = 配置里没有。
/// <c>Sync</c> = 分区联动（原厂叫神光同步）：开启后调任一区，其余分区跟着变；配置里缺省 false。
/// </summary>
internal readonly record struct BacklightPreset(int[][] Zones, long? Effect, bool Sync,
                                               int[]? Logo = null, int[]? Tail = null,
                                               long? Level = null);

/// <summary>
/// WPF 界面的数据入口 —— **进程内直读**，不走 HTTP。
///
/// 为什么不让 WPF 去调 127.0.0.1:8765（那是最省事的做法）：
///   ① 界面和后端本来就在一个进程里，多绕一圈 loopback + JSON 只为"看起来解耦"，
///      每秒一次快照的代价虽小，但窗口打开瞬间首帧会被 socket 调度推迟；
///   ② 关闭窗口要能真正把 WPF 那部分内存还回去 —— 独立客户端进程反而是更重的方案。
/// 刻意**不碰** AppState 的业务方法（那些在 POST 里），这里只读快照 + 一个主题偏好写口：
/// 写操作将来一律给 WPF 开独立的窄接口，不在这里顺手加（避免绕过 PostHandlers 的守卫）。
///
/// ⚠️ 只读 <see cref="AppState.CachedStatus"/>，**绝不**在 UI 线程上调 Status() 现算 ——
///    那会当场读 EC/PDH（非管理员还会抛），界面就会一秒一卡。
/// ⚠️ DTO 是契约生成物（spike/_gen_dtos.py）。这里反序列化用的是同一份 DTO 和同一份
///    JsonOptions，所以界面拿到的字段与契约**逐字一致**。
/// </summary>
internal sealed class LocalApi
{
    private readonly AppState? _state;

    // 采样 1 秒一拍、界面也 1 秒一刷：两边错开的时候会拿到**同一份** JsonObject 实例。
    // 按引用判等直接复用上次的 DTO，省掉每秒一次的全量反序列化（100+ 字段）。
    private JsonObject? _lastStatusNode;
    private StatusDto? _lastStatusDto;

    // 历史曲线同理：版本号没动就用上次的 DTO（见 History() 的注释）
    private long _lastHistVersion = -1;
    private HistoryDto? _lastHistory;

    public LocalApi(AppState? state) => _state = state;

    /// <summary>
    /// 「界面还开着、还在看」的心跳 —— 按需模式下靠它决定 HWiNFO 要不要继续在跑；
    /// **同时**把全量快照的需求窗口续上（关窗后没人看，采样线程就走精简档）。
    ///
    /// 这不是业务写操作（不碰 EC、不改 presets），只是延长一个"需求窗口"，
    /// 所以放在这里而不是 PostHandlers；窗口关掉后心跳停，60 秒后进程被收掉。
    /// </summary>
    public void KeepHwinfoAlive()
    {
        _state?.RequestHwinfo();
        _state?.DemandFullSnapshot();
    }

    /// <summary>
    /// 「一键采集」进行到哪一步（`null` = 没在采）。界面在采集期间每 300 ms 读一次。
    ///
    /// 为什么不走 HTTP：采集是个 30 秒的 POST，同一条连接上没法同时收进度；
    /// 而界面和后端本来就在一个进程里（见类注释），读一个 volatile 字段就够了。
    /// **只报阶段，不报百分比** —— 那些数字编不出来。
    /// </summary>
    public string? AdoptStage => _state?.AdoptStage;

    /// <summary>采集进行到第几段（1..5；0 = 没在采）。界面画分段进度用。</summary>
    public int AdoptStep => _state?.AdoptStep ?? 0;

    /// <summary>「观察原厂」是否正在进行（界面据此显示「完成观察」按钮）。</summary>
    public bool Observing => _state?.Observing ?? false;

    /// <summary>
    /// 原厂此刻有没有**可点的界面**（采集门槛用，见 <see cref="VendorPrompt"/>）。
    ///
    /// 直接读进程与窗口，不走 HTTP 也不走 10 秒一份的缓存：用户点「一键采集」的那一刻
    /// 刚打开原厂，缓存里还是"没在跑"，那就会把他赶去一个已经开着的窗口前。
    /// 代价是枚举 5 个进程名 + 一次 `GetProcessesByName`（几毫秒），点一次按钮摊得起。
    /// </summary>
    public VendorUiState VendorUi => _state?.VendorUi() ?? VendorUiState.Unknown;

    /// <summary>
    /// 当前**实际生效**的 EC 写许可（已适配机型默认就是开）。
    /// 关于页用它决定要不要摆「启用本机」—— 写都开了还摆一颗"启用"，点下去只是白重启一次。
    /// </summary>
    public bool EcWritesAllowed => _state?.Current.WritesAllowed ?? false;

    /// <summary>
    /// 「引导适配」当前走到哪一步（步骤清单 + 每步抓到几条 + 有没有走完）。
    /// 与 <see cref="AdoptStage"/> 同源：本来就在一个进程里，直接读内存，不走 HTTP。
    /// 采集结束后返回的 <c>active</c> 是 false —— 界面据此关掉引导弹窗。
    /// </summary>
    public JsonObject? GuideView => _state?.Guide.Snapshot();

    /// <summary>
    /// 用户在引导面板上点「这一步做完了」/「这台没有这项」。
    /// 返回 true = 步骤走完（界面应当请求结束观察，不用等满 150 秒）。
    /// </summary>
    public bool GuideMark(bool skip) => _state?.Guide.Mark(skip) ?? false;

    /// <summary>退回上一步（点错了 / 原厂那一下没写 EC）。返回 false = 已经在第一步。</summary>
    public bool GuideBack() => _state?.Guide.Back() ?? false;

    /// <summary>请求提前结束当前观察窗口（「完成观察」按钮）。</summary>
    public void RequestObserveStop() => _state?.RequestObserveStop();

    /// <summary>
    /// 请求把观察窗口往后推（引导小窗上的「再等 60 秒」）。
    /// 到点就按"没做到"收尾，所以这颗按钮是用户唯一能挽回那一步的入口。
    /// </summary>
    public void RequestObserveExtend(int seconds) => _state?.RequestObserveExtend(seconds);

    /// <summary>没有真数据源（没找到 model.json / 初始化失败）时界面要显示"读不到"，别装作有。</summary>
    public bool HasState => _state is not null;

    /// <summary>
    /// 读当前一秒的快照。任何异常都返回 null —— 界面按"没有数据"处理，不抛进渲染循环。
    ///
    /// ⚠️ 2026-11-02 起采样线程在**没人看的时候**只拼精简档（见 `AppState.DemandFullSnapshot`）。
    ///    所以这里先续上需求窗口、再要求"缓存必须是全量帧"：宁可让界面显示一帧
    ///    "读取中…"（≤1 个采样拍），也不能把缺段的快照渲染出去（那会像字段丢了）。
    /// </summary>
    public StatusDto? Status()
    {
        if (_state is null) return null;
        _state.DemandFullSnapshot();
        // 采样线程还没出第一帧（或当前帧是精简档）⇒ 返回 null（界面显示"读取中…"）。
        // 绝不能在这里退化成 CachedStatus() 的"现算"分支：那会当场读 EC（0.45s 节流），
        // 表现就是"窗口刚打开时卡半秒"。
        if (!_state.HasFullStatus) return null;
        var node = _state.CachedStatus();
        if (ReferenceEquals(node, _lastStatusNode) && _lastStatusDto is not null) return _lastStatusDto;
        try
        {
            _lastStatusDto = JsonSerializer.Deserialize<StatusDto>(node, ApiResponse.JsonOptions);
            _lastStatusNode = node;
            return _lastStatusDto;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// `GET /api/history` 的进程内等价物。按 <see cref="AppState.HistoryVersion"/> 缓存：
    /// 缓冲没变就直接复用上次的 DTO，省掉"重拼 150 点 + 反序列化"这一整套。
    /// 历史一秒才涨一个点，而界面也是一秒刷一次 —— 不缓存等于每秒白做一份无用功。
    /// </summary>
    public HistoryDto? History()
    {
        if (_state is null) return null;

        var version = _state.HistoryVersion;
        if (version == _lastHistVersion && _lastHistory is not null) return _lastHistory;
        try
        {
            var dto = JsonSerializer.Deserialize<HistoryDto>(_state.History(), ApiResponse.JsonOptions);
            _lastHistory = dto;
            _lastHistVersion = version;
            return dto;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// `GET /api/fancurve` 的进程内等价物（<c>FanCurveEngine.State()</c>）。
    /// 与 <see cref="Status"/> 不同，这个**不是**每秒缓存快照 —— 它由曲线引擎当场拼一个
    /// JsonObject（不读 EC、不动硬件，纯内存），所以散热页每秒调一次也不会卡界面。
    /// </summary>
    public FanCurveStateDto? FanState()
    {
        if (_state is null) return null;
        try
        {
            return JsonSerializer.Deserialize<FanCurveStateDto>(_state.Fan.State(), ApiResponse.JsonOptions);
        }
        catch
        {
            return null;
        }
    }


    // ---------------------------------------------------------------- 低频读口
    // ⚠️ 下面两个**会真的去问系统**（powercfg 要起进程、NVML 要开库），
    //    调用方必须放在 Task.Run 里，别在 UI 线程上每秒调（那正是"界面一秒一卡"的成因）。

    /// <summary>`GET /api/power/plans`：电源计划列表 + 当前项。</summary>
    public PowerPlansDto? Plans() => Read<PowerPlansDto>(() => QControl.Hal.Sensors.PowerPlans.List());

    /// <summary>
    /// 内建屏亮度（0~100）—— **没有对应的 HTTP GET**，读只走这里；
    /// 写走 POST `/api/display/brightness`（<see cref="Post"/>）。
    /// ⚠️ 会真的问 WMI（20~50ms），调用方必须放 Task.Run —— 与 <see cref="Plans"/> 同一条规矩。
    /// </summary>
    public JsonObject? Brightness()
    {
        try
        {
            return QControl.Hal.Sys.ScreenBrightness.Read();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 每条计划的关键数值（处理器最小/最大状态、系统散热方式；交流与电池各一份）。
    ///
    /// 走 powrprof 的 `PowerRead?CValueIndex`（进程内、微秒级、返回**合并后的有效值**），
    /// 不是读注册表 —— 注册表里只有"自己覆盖过的"项，本机平衡/办公/游戏那三条一个值都没有。
    /// ⚠️ 是系统调用，但极快；仍然放在 <see cref="Plans"/> 同一个 Task.Run 里调，别放 UI 线程。
    /// </summary>
    public Dictionary<string, QControl.Hal.Sensors.PowerPlans.PlanValues> PlanValues(
        IEnumerable<string?>? guids)
    {
        var map = new Dictionary<string, QControl.Hal.Sensors.PowerPlans.PlanValues>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var g in guids ?? [])
        {
            if (string.IsNullOrEmpty(g)) continue;
            map[g] = QControl.Hal.Sensors.PowerPlans.Values(g);
        }
        return map;
    }

    /// <summary>`GET /api/gpu/oc`：超频能力（当前偏移 + 驱动允许范围）+ 实时状态 + 边界表。</summary>
    public GpuOcStateDto? GpuOc() => Read<GpuOcStateDto>(() => _state!.GpuOcState());

    /// <summary>
    /// `GET /api/app/settings`：设置页那几个开关的真源。
    /// ⚠️ 会去查计划任务（`Autostart.IsOn()`），属于慢读 —— 调用方放 Task.Run 里。
    /// </summary>
    public AppSettingsDto? Settings() => Read<AppSettingsDto>(() => _state!.AppSettings());

    /// <summary>`GET /api/vendor/state`：原厂进程 / 登录自启 / OSD 服务现状（要枚举进程，同样慢读）。</summary>
    public VendorStateDto? Vendor() => Read<VendorStateDto>(() => _state!.VendorState());

    /// <summary>
    /// `GET /api/hardware`：硬件档案（8 组静态信息，开机采一次缓存）。
    /// ⚠️ **第一次调用会真起后台采集**（八组 WMI 并行，实测 ~120 ms，期间回 busy:true）。
    ///    正常情况下后端在首个快照就绪后已经预采过（`AppState.WarmupHardware`），
    ///    这里的 busy 分支只是兜底；真拿到 busy 就隔 250 ms 再拉一次。
    /// </summary>
    public HardwareDto? Hardware() => Read<HardwareDto>(() => _state!.HardwareInfo());

    /// <summary>
    /// `GET /api/app/about`：「关于」页要的一次性信息 —— 版本 / 运行环境 / 机型 / 端口 /
    /// 启动时间 / 已运行秒数 + 日志现状与最后 24 行 + 区间记录现状。
    /// </summary>
    public AboutDto? About() => Read<AboutDto>(() => _state!.About());

    /// <summary>`GET /api/app/log/usage`：日志目录占用明细（清理按钮旁边那份）。</summary>
    public LogUsageDto? LogUsage() => Read<LogUsageDto>(() => _state!.LogUsage());

    /// <summary>
    /// 区间记录的现状（手动 / 自动那两格）—— 关于页在**可见时每 2 秒**对齐一次。
    ///
    /// 为什么需要：自动档什么时候开一段、什么时候收，都不是用户点的；页面只在切进来时
    /// 拉一次 `about`，不轮询的话两格会一直停在那一帧（自动段早就收了，界面还写着"记录中"）。
    /// ⚠️ 没有对应的 HTTP GET（见 `AppState.CaptureState`）：同进程读一个锁内快照，
    ///    几微秒、不碰文件、不碰 EC —— 别为了它去开一条路由。
    /// </summary>
    public AboutCaptureDto? Capture() => Read<AboutCaptureDto>(() => _state!.CaptureState());

    /// <summary>
    /// `GET /api/app/update`：检查更新的内存状态（当前版本 / 有没有新版 / 上次检查 / 失败原因）。
    ///
    /// ⚠️ 只读内存，**不发网络请求** —— 检查期间的轮询会每 500 ms 调它一次，
    ///    这条路必须是"拼一个 JsonObject"这么便宜。真正的查询在 POST 里跑。
    /// </summary>
    public UpdateDto? Update() => Read<UpdateDto>(() => _state!.UpdateState());

    private T? Read<T>(Func<JsonObject> source) where T : class
    {
        if (_state is null) return null;
        try
        {
            return source().Deserialize<T>(ApiResponse.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 配置读口
    // 只读（presets.*）；写配置走 UiActions → PostHandlers。

    /// <summary>某档预设的 APMD 码（确认框里要说"要下发哪个值"，所以必须从配置取，不能写死）。</summary>
    public long? PerfApmd(string modeKey)
        // ⚠️ 走 Bounds.TryToDouble，不用 TryGetValue&lt;long&gt;：后者**不跨数值类型**
        //    —— 一键适配把 apmd 以 int 写进内存配置后这里会一律回 null，
        //    确认框就永远显示「该档位值」而不是真实的 0x02（同 Bounds 那条注释）。
        => _state?.Model.Get("perf", "modes", modeKey, "apmd") is JsonNode n
           && QControl.Core.Model.Bounds.TryToDouble(n, out var apmd)
            ? (long)apmd : null;

    /// <summary>某档要同步切的 Windows 电源计划名（model.json 的 presets.perf.modes[档].plan）。</summary>
    public string? PerfPlan(string modeKey)
        => _state?.Model.Get("perf", "modes", modeKey, "plan") is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : null;

    /// <summary>
    /// 显卡模式的名字（model.json 的 presets.gfx.modes[码]）。
    /// 三档语义已实测标定，配置缺失时有兜底 —— 不会露出空字符串。
    /// </summary>
    public string GfxName(long code)
        => _state?.Model.Get("gfx", "modes", code.ToString(System.Globalization.CultureInfo.InvariantCulture), "name")
               is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : code switch { 1 => "混合输出", 2 => "独显直连", 3 => "集显输出", _ => "未知(" + code + ")" };

    /// <summary>显卡模式的说明（只进悬停提示）。</summary>
    public string? GfxDesc(long code)
        => _state?.Model.Get("gfx", "modes", code.ToString(System.Globalization.CultureInfo.InvariantCulture), "desc")
               is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s : null;

    /// <summary>
    /// 写操作的唯一入口（转发给 <see cref="UiActions"/>：同一份守卫、同一份副作用）。
    /// 页面里**不要**自己去拿 AppState 直调 EC —— 那样会绕过 arm / 总闸 / verified。
    /// </summary>
    public ActResult Post(string path, JsonObject? body = null) => UiActions.Post(_state, path, body);

    /// <summary>
    /// 启动默认页（`presets.ui.start_page`）。没写过就走默认页。
    /// </summary>
    public string? SavedStartPage
        => _state?.Model.Get("ui", "start_page") is JsonValue v
           && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : null;

    /// <summary>
    /// 拔掉适配器时自动切办公模式（`presets.perf.auto_office_on_battery`）。
    /// **缺省为开** —— 原厂控制中心就是这个行为，与 `presets.default.json` 里那份默认值一致
    /// （用户关掉后落盘 false，之后都以配置为准）。
    /// </summary>
    public bool AutoOfficeOnBattery
        => _state?.Model.Get("perf", "auto_office_on_battery") is JsonNode n
            ? QControl.Core.Devices.Device.Truthy(n)
            : true;

    /// <summary>
    /// 读一个数字节点。⚠️ 不能靠 `TryGetValue&lt;double&gt;`：配置里的值可能是
    /// 解析来的（JsonElement）也可能是我们 `JsonValue.Create(int)` 写回去的（强类型 int），
    /// 两者不互通 —— 统一走 ToJsonString 再解析（与 AppState.Num 同一套路）。
    /// </summary>
    private static double? NumOf(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            return null;
        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>
    /// 硬件页「进阶行」的展开态（`presets.ui.hw_adv_open`）。
    /// 没写过就是 false（= 默认折叠）。
    /// </summary>
    public bool HwAdvOpen
        => _state?.Model.Get("ui", "hw_adv_open") is JsonValue v
           && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// 已保存的背光（`presets.backlight`）：**按 EC 地址**取各分区颜色（顺序 = 配置里的 zones 顺序），
    /// 外加灯效码。读取顺序：
    ///   · 优先 `zones`（新结构），按 addr 找、不按下标（presets 里的顺序是历史遗留）；
    ///   · 没有 zones 才退回老的单色 `rgb`（所有分区同色）；
    ///   · 都读不到 ⇒ 白色（EC 上电默认也是白）。
    /// ⚠️ 只读配置、不读 EC：档位（level）走每秒快照的 `ec.backlight_level`，
    ///    颜色 EC 根本读不回来（只能靠 presets 记着），所以这两条来源不能混。
    /// ⚠️ 所有数字一律走 <see cref="NumOf"/>（**不许** `TryGetValue&lt;long/double&gt;`）：
    ///    `PostHandlers.EcBacklight` 落盘时是用 `JsonValue.Create(int)` 重建 zones 的，
    ///    所以本进程写过一次颜色以后，`presets.backlight` 里就是**强类型 int**，
    ///    而 `TryGetValue` 不跨数值类型转换 —— addr 读成 -1、rgb 读成 null，
    ///    表现是"改完颜色关窗再打开，背光页三区全白、灯效回默认"（2026-11-07 修）。
    ///    从磁盘读回来的那份是 JsonElement，转得动，所以只有**同一进程内**才复现。
    /// </summary>
    public BacklightPreset SavedBacklight()
    {
        var addrs = KbZoneAddrs;
        var zones = addrs.Select(_ => new[] { 255, 255, 255 }).ToArray();
        long? effect = null;
        var sync = false;

        var model = _state?.Model;
        if (model is null) return new BacklightPreset(zones, null, false);
        try
        {
            // 分区联动：缺省 false（配置里还没这个键就是老行为 —— 各区独立）
            sync = SyncOn(model);

            if (model.Get("backlight", "zones") is JsonArray arr && arr.Count > 0)
            {
                foreach (var zone in arr)
                {
                    if (zone is not JsonObject zo) continue;
                    // ⚠️ 必须转成 int 再查表：`Array.IndexOf(int[], long)` 会落到
                    //    `IndexOf(Array, object)` 那个重载上，装箱后 int 5 ≠ long 5 ⇒ 永远找不到，
                    //    于是全部分区都被当成"读不到"退回白色（第一版就是这个症状）。
                    // ⚠️ 取值走 NumOf：本进程写回的 addr 是强类型 int，`TryGetValue<long>` 读不出来
                    //    （会静默 -1 ⇒ 每个分区都跳过）。
                    var addr = NumOf(zo["addr"]) is { } a ? (int)Math.Round(a) : -1;
                    var slot = Array.IndexOf(addrs, addr);
                    if (slot < 0) continue;
                    if (Rgb3(zo["rgb"]) is { } rgb) zones[slot] = rgb;
                }
            }
            else if (Rgb3(model.Get("backlight", "rgb")) is { } single)
            {
                // 老结构（单色）⇒ 所有分区同色
                for (var i = 0; i < zones.Length; i++) zones[i] = single;
            }

            // effect 同理：`JsonValue.Create(int)` 写回去的是强类型 int，`TryGetValue<long>` 读不出
            if (NumOf(model.Get("backlight", "effect")) is { } eff) effect = (long)Math.Round(eff);
        }
        catch
        {
            // 配置读坏了不该让整页白屏：退回白色 + 默认灯效
        }

        var logo = Rgb3(model.Get("backlight", "logo"));
        var tail = Rgb3(model.Get("backlight", "tail"));
        return new BacklightPreset(zones, effect, sync, logo, tail);
    }

    /// <summary>
    /// 当前系统模式键（`office` / `game` / `beast`，读不到为 null）。
    /// 背光页据此标出"现在系统在哪一档"，与 <see cref="SavedModeProfile"/> 配合。
    /// </summary>
    public string? PerfModeKey => _state?.CurrentPerfKey();

    /// <summary>第四套灯效的「自定义接管」总闸（`presets.backlight.custom_override`）。</summary>
    public bool BacklightCustomOverride
        => _state?.Model.Get("backlight", "custom_override") is JsonNode o
           && QControl.Core.Devices.Device.Truthy(o);

    /// <summary>键盘之外的独立灯珠（name → EC 地址）；空表 = 这台机器没有，界面不摆控件。</summary>
    public IReadOnlyDictionary<string, int> ExtraLightAddrs
        => _state?.Current.ExtraLightAddrs ?? new Dictionary<string, int>();

    /// <summary>
    /// 某一档的灯效档案（`presets.perf.modes[档].light`，含第四套 custom）。**没有就返回 null** ——
    /// 调用方要退回"当前生效的那套"，而不是自己编一套（见 BacklightModes 的规矩②）。
    /// </summary>
    public BacklightPreset? SavedModeProfile(string? mode)
    {
        var model = _state?.Model;
        if (model is null || _state is null) return null;

        var profile = QControl.Core.Devices.BacklightModes.ForMode(
            model.Presets, mode,
            QControl.Core.Devices.BacklightZones.Sanitize(_state.Current.KbZones));
        if (profile is null) return null;

        var addrs = KbZoneAddrs;
        var zones = addrs.Select(_ => new[] { 255, 255, 255 }).ToArray();
        foreach (var (addr, rgb) in profile.Zones)
        {
            var slot = Array.IndexOf(addrs, addr);
            if (slot >= 0) zones[slot] = rgb;
        }
        return new BacklightPreset(zones, profile.Effect, SyncOn(model), profile.Logo, profile.Tail,
                                   profile.Level);
    }

    /// <summary>
    /// 某一档的自动熄灭策略（`perf.modes[档].light` 的 `auto_off_min` / `auto_off_on_lock`）。
    ///
    /// `Own = false` ⇒ 这一档**还没单独设过**，策略是**不自动熄灭**（0 / false）。
    /// 默认不熄是硬规矩：这是"会替你动硬件"的功能，没设过就不许自己灭
    /// （2026-11-19 用户定）—— 也不拿全局 `presets.light` 顶。
    /// </summary>
    public (bool Own, int Minutes, bool OnLock) ModeAutoOff(string? mode)
    {
        var model = _state?.Model;
        if (model is not null
            && QControl.Core.Devices.BacklightModes.AutoOffOf(model.Presets, mode) is { } own)
            return (true, own.Minutes, own.OnLock);

        return (false, 0, false);
    }

    /// <summary>分区联动（`presets.backlight.sync`）：缺省 false = 各区独立。</summary>
    private static bool SyncOn(QControl.Core.Model.ModelStore model)
        => model.Get("backlight", "sync") is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// 界面 tab 顺序对应的 EC 地址，**数量 = 配置里的分区数**（1~N，不再固定三区）。
    ///
    /// 以前是写死的 `[5,4,3]`（本机 NL5），后来按配置读但要正好 3 条 —— 于是单区 / 双区 /
    /// 四区键盘的机型永远拿不到自己的地址，背光页也被 `caps.kb_zones` 关着。
    /// 现在按 `presets.backlight.zones` 的实际条目顺序给（引导适配按地址降序写进来的），
    /// 没有可用配置时才回落到 NL5 的 5/4/3。
    /// </summary>
    public int[] KbZoneAddrs
    {
        get
        {
            int[] fallback = [5, 4, 3];
            try
            {
                if (_state?.Model.Get("backlight", "zones") is JsonArray arr && arr.Count > 0)
                {
                    // ⚠️ 必须先按机型声明的分区数裁一刀（见 BacklightZones.Limit）：
                    //    配置里的表可能被探针试色撑大过（本机 5/4/3 曾涨成 5 条，页面上就是 A~E 五个 tab、
                    //    键盘按五等分画）。`caps.kb_zones` 说几个分区，界面上就只摆几个。
                    //    Sanitize 再兜一层：五分区键盘世上不存在，声明超过 4 一律按三区处理。
                    var (kept, _) = BacklightZones.Limit(arr, BacklightZones.Sanitize(_state.Current.KbZones));
                    var addrs = new List<int>(kept.Count);
                    foreach (var z in kept)
                    {
                        if (z is not JsonObject zo) continue;
                        if (NumOf(zo["addr"]) is { } a) addrs.Add((int)Math.Round(a));
                    }
                    if (addrs.Count > 0) return addrs.ToArray();
                }
            }
            catch
            {
                // 配置被手改坏：按默认三地址走，不让灯效页整页打不开
            }
            return fallback;
        }
    }

    /// <summary>
    /// 取 `[r, g, b]` 三项（0~255）。⚠️ 数值走 <see cref="NumOf"/>，别用 `TryGetValue&lt;double&gt;`：
    /// 本进程写回的 rgb 是强类型 int，读不出来就会让全部分区一起退回白色。
    /// </summary>
    private static int[]? Rgb3(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count != 3) return null;
        var rgb = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (NumOf(arr[i]) is not { } d) return null;
            rgb[i] = Math.Clamp((int)Math.Round(d), 0, 255);
        }
        return rgb;
    }

    /// <summary>
    /// HWiNFO 数据源模式：`always` / `ondemand` / `off`。
    /// 直接问后端（后端那侧才是唯一判定口，非法值/缺失都按 `ondemand` 兜底），
    /// 界面这边不自己解释 model.json。
    /// </summary>
    public string HwinfoMode => _state?.HwinfoMode() ?? "ondemand";

    /// <summary>按需模式的空闲超时秒数（设置页的说明文案要用真实值）。</summary>
    public int HwinfoIdleSec => _state?.HwinfoIdleSec() ?? 60;

    /// <summary>
    /// 现在是否**真的**在拦 Win 键（钩子装着，不是"用户点过开"）。
    ///
    /// 为什么不自己记一个布尔：钩子可能安装失败（被安全软件挡住等），那时用户点过、
    /// 按钮亮了，实际一个键都没拦住 —— 开关必须画成假的那一边，否则用户按了 Win 键
    /// 弹出开始菜单，界面却说"已锁定"。另外这个状态**不跨重启**（开机一律是关的）。
    /// 没有对应的 HTTP GET（同 `CaptureState` / `SavedStartPage`）：同进程读一个布尔值。
    /// </summary>
    public bool WinKeyLocked => _state?.WinKeyLocked ?? false;

    // ---------------------------------------------------------------- HWiNFO 是否装好

    /// <summary>
    /// 有没有找到 HWiNFO 目录（含 `HWiNFO64.exe`）。
    ///
    /// 给设置页那条「HWiNFO 未找到」引导用：分发包**不带**它（第三方软件，EULA 禁止 bundling），
    /// 所以安装版用户大概率是"没有"，而那六项数据会整行隐藏 —— 界面上必须能回答"为什么少了几行"。
    ///
    /// ⚠️ 结果是**缓存**的：探测要从 exe 目录往上翻 10 层、每层试两个路径，界面每秒刷一次
    ///    不能每次都扫盘。用户在界面上点「重新检测」走 <see cref="RefreshHwinfoProbe"/>。
    /// </summary>
    public bool HwinfoFound
    {
        get { ProbeHwinfo(); return _hwinfoFound == true; }
    }

    /// <summary>HWiNFO 所在目录；找不到为 null。</summary>
    public string? HwinfoDir
    {
        get { ProbeHwinfo(); return _hwinfoDir; }
    }

    /// <summary>强制重扫一遍（用户刚把 HWiNFO 放好、不想重启控制中心）。</summary>
    public void RefreshHwinfoProbe()
    {
        ProbeHwinfo(force: true);
        // 后端那份目录缓存（5 秒）也一起作废，否则界面这一行会晚几秒才翻篇
        _state?.InvalidateHwinfoDirCache();
    }

    private bool? _hwinfoFound;
    private string? _hwinfoDir;

    private void ProbeHwinfo(bool force = false)
    {
        if (!force && _hwinfoFound.HasValue) return;
        try
        {
            _hwinfoDir = QControl.Hal.Sys.HwInfoProcess.Dir();
        }
        catch
        {
            _hwinfoDir = null;   // 探测抛异常就当成"没有"：少一个外部程序不该把设置页带崩
        }
        _hwinfoFound = _hwinfoDir is not null;
    }

    /// <summary>
    /// 机型配置来源（`factory_specs.source`）：`registry` = 从原厂注册表探到的基准值，
    /// 其它（含读不到）= 用内置默认配置。系统页「机型配置」卡头那枚 tag 用它。
    /// </summary>
    public string? ModelSource
        => _state?.Model.Get("factory_specs", "source") is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : null;

    /// <summary>
    /// model.json 解析失败的原因（null = 正常）。失败时后端保住上一份配置、拒绝写盘，
    /// 界面必须**明说**（卡头写「model.json 解析失败」），不能让用户以为保存没生效。
    /// </summary>
    public string? ModelLoadError => _state?.Model.LoadError;

    /// <summary>已保存的主题模式（dark / light / auto）。读不到按 dark。</summary>
    public string SavedTheme
    {
        get
        {
            try
            {
                return _state?.Model.Get("ui", "theme") is JsonValue v && v.TryGetValue<string>(out var s)
                    ? s
                    : "dark";
            }
            catch
            {
                return "dark";
            }
        }
    }

    /// <summary>主题写回 presets.ui.theme（唯一入口，别处别直接写这个键）。</summary>
    public void SaveTheme(string token)
    {
        try
        {
            _state?.SetAppSettings(new JsonObject { ["theme"] = token });
        }
        catch
        {
            // 存偏好失败不影响本次显示（下次启动回到旧模式而已），不打扰用户
        }
    }
}
