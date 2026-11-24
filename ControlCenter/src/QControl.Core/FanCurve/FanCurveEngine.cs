using System.Text.Json.Nodes;
using QControl.Core.Model;

namespace QControl.Core.FanCurve;

/// <summary>
/// 自动风扇曲线引擎：按曲线算占空比、带迟滞与最小写入间隔。
///
/// 这一段是**整台机器的散热安全阀**，所以迁移原则是"逐行照搬，不重构"：
/// 迟滞、最小写入间隔、温度合理性校验、温度墙、连续失败自停，每一条都对应
/// 一次真实事故（见各方法注释里的日期）。
///
/// 与早先实现的唯一结构性差异：硬件访问走 <see cref="IFanHardware"/>，
/// 时间走 <see cref="TimeProvider"/> 风格的注入函数 —— 于是整个循环可以用
/// 假时钟 + 假硬件在毫秒内跑完 150 轮，不需要真机。
///
/// ⚠️ **2026-10-23 起曲线按原厂拆成 CPU / GPU 两条**（反编译
/// `ControlCenterDaemon.exe::MonitorAndControlFan` 实证，见 docs/PROTOCOL.md §7.3）：
///   · CPU 风扇（UFA1）听 CPU 曲线@CPU 温度，GPU 风扇（UFA2）听 GPU 曲线@GPU 温度；
///   · 老配置里的单条 `curve` 迁移时**同时**填进 cpu/gpu 两路，行为与早先实现一致；
///   · `source`（cpu/gpu/max）字段从此**只读不参与控制** —— 原厂没有这个概念，
///     保留它只会让用户以为还能"只听一边"。
/// </summary>
public sealed class FanCurveEngine : IDisposable
{
    /// <summary>曲线可绑定的系统模式键（与 `presets.perf.modes` 一致）。</summary>
    public static readonly string[] ModeKeys = ["office", "game", "beast"];

    /// <summary>连续异常这么多轮就自停（每轮约 1 s ⇒ 半分钟）。</summary>
    public const int FailHaltRounds = 30;

    /// <summary>
    /// 温度读数合理区间，**开区间**：之外一律当无效。
    ///
    /// EC 回全 0 buffer 时 `cpu_temp = 0` 是"合法解析值"但只可能是坏数据 ——
    /// 拿它插值会得到低于曲线首点的目标（= min_duty 30%），把 EC 钉死在低转速上，
    /// 正是 2026-10-13「84°C 下 1500 RPM 锁死」的事故形态。
    /// </summary>
    public const double TempValidLo = 1.0;
    public const double TempValidHi = 115.0;

    /// <summary>
    /// 温度快照的保鲜期（秒）。曲线读 EC 用 <c>Temps(10s)</c>；交回转速时也按同一上限判，
    /// 超过就当作"温度未知"，一个字节都不写（见 <see cref="ReleaseDuty"/>）。
    /// </summary>
    public const double TempFreshSeconds = 10.0;

    private readonly IFanHardware _hw;
    private readonly ModelStore _model;
    private readonly Func<double> _now;          // Unix 秒
    private readonly Action<double>? _sleep;     // 测试可注入"不真睡"

    private readonly object _lifecycleLock = new();

    public FanCurveEngine(IFanHardware hw, ModelStore model,
                          Func<double>? now = null, Action<double>? sleep = null)
    {
        _hw = hw;
        _model = model;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
        _sleep = sleep;
        _lastWrite = 0.0;
    }

    // ================================================================ 状态

    public volatile bool Running;
    public bool Protecting { get; private set; }
    public double? ProtectTemp { get; private set; }
    /// <summary>
    /// FN+1 飓风接管中。**循环看到它就把目标钉到 <see cref="MaxDuty"/>**（见 Step）。
    /// 外部只该通过 <see cref="ApplyHwMax"/> 改它 —— 那里还会立刻写一次 EC，
    /// 否则"曲线没在跑"时按 FN+1 什么都不会发生。
    /// </summary>
    public bool YieldHw { get; set; }

    /// <summary>进入飓风之前我们自己写过的转速（退出飓风时恢复用；曲线在跑时不用它）。</summary>
    private int? _dutyBeforeMax;

    public int? LastDuty { get; private set; }
    public int? LastDutyGpu { get; private set; }
    /// <summary>
    /// 第三路（SYS / UFA3）最近一次下发值。null = 这一路没在由我们控制
    /// （两风扇机型，或者配置里没有 `curve_sys`）。
    /// </summary>
    public int? LastDutySys { get; private set; }
    /// <summary>CPU 那一路最近一次用上的温度（兼容字段 = 这个）。</summary>
    public double? LastTemp { get; private set; }
    public double? LastTempCpu { get; private set; }
    public double? LastTempGpu { get; private set; }
    /// <summary>第三路（SYS）最近一次用上的温度。</summary>
    public double? LastTempSys { get; private set; }
    public string? LastError { get; private set; }

    private double _lastWrite;
    /// <summary>
    /// 上一次**读到有效温度**的时刻（与 <see cref="_now"/> 同一时钟）。
    /// <para>
    /// 为什么需要它：`LastTempCpu/Gpu` 只在 <see cref="Step"/> 里被写，**从不清空**。
    /// EC 读停摆、或者曲线早就停了，它们还挂着几分钟前的值；`Stop(release:true)` 拿它
    /// 算"交回转速"，就等于按一个不存在的温度写 EC —— 而 EC 拿到 UFA 后不会自己接管，
    /// 风扇会一直停在那个人为值上（2026-11-12 审查发现）。
    /// </para>
    /// </summary>
    private double _lastTempAt;
    private int _failStreak;
    private int _gen;
    private volatile bool _stop;
    private Thread? _thread;

    /// <summary>
    /// 交回（<see cref="Stop"/>）与曲线写 EC 的互斥闸。
    ///
    /// `Join(1500)` 超时**不代表**旧线程已经停手：它可能正卡在 `_hw.SetFans` 里
    /// （GmWmi 严格串行 + 节流，一次往返可达秒级，还要跟采样线程抢锁）。没有这把闸，
    /// Stop 的 `ReleaseToEc()` 会先落地，随后那条**已在排队**的 SetFans 又把 UFA 写回去 ⇒
    /// 界面报「已交回固件 / released:true」，实际风扇仍被钉在曲线值上（要重启才恢复）。
    /// 加闸后顺序固定：要么写先完成、要么交回先完成，不会再反过来盖掉交回。
    /// </summary>
    private readonly object _hwGate = new();
    private string? _perfKey;                 // 当前 EC 性能模式键
    private JsonObject? _switch;              // 待播报的「切模式换曲线」事件

    // ================================================================ 配置（每次现读）

    /// <summary>
    /// `presets.fan`。**每次访问都重新取** —— 记事本改完 model.json 即生效。
    /// <para>
    /// 取的时候顺手做**双曲线迁移**：老配置只有单条 `curve`，第一次访问时
    /// 复制进 `curve_cpu`/`curve_gpu`（档位档案里的那份在 `perf.modes[档].fan`）并落盘。
    /// 放在这里而不是启动时，是因为单测/`--ui-shot` 之类的路径会直接
    /// <see cref="Step"/> 而不走 <see cref="Start"/>，那些路径同样要能读到拆分后的配置。
    /// </para>
    /// </summary>
    private JsonObject Cfg
    {
        get
        {
            var fan = _model.Presets["fan"] as JsonObject;
            if (fan is null)
            {
                fan = new JsonObject();
                lock (_model.SyncRoot) _model.Presets["fan"] = fan;
            }
            if (fan["curve_cpu"] is null || fan["curve_gpu"] is null) MigrateSplitCurves(fan);
            return fan;
        }
    }

    /// <summary>
    /// 单曲线 → CPU/GPU 双曲线的一次性迁移（幂等）。
    /// 老 `curve` 同时喂两路：旧语义就是"两个风扇同一个目标"，迁移后不能变。
    ///
    /// ⚠️ **`presets.default.json` 里绝不能出现 `curve_cpu` / `curve_gpu`**：
    ///    `ModelStore` 启动时会把出厂默认深合并进内存配置，老用户的 model.json 只有
    ///    `curve` —— 一旦默认里带了拆分键，合并结果就是"用户的 curve + 默认的
    ///    curve_cpu/gpu"，而本引擎优先读拆分键 ⇒ **用户调好的曲线被默认曲线顶掉**。
    ///    所以拆分只能靠这里按需补，不能进出厂默认（2026-10-23 踩过，一改就 10 条测试红）。
    /// </summary>
    private void MigrateSplitCurves(JsonObject fan)
    {
        lock (_model.SyncRoot)
        {
            if (fan["curve_cpu"] is not null && fan["curve_gpu"] is not null) return;

            // ⚠️ 只有**真的改动了**才落盘。以前是无条件 `_model.Save()`，
            //    而 `Cfg` 在 Step / State 里被访问十几次（每秒数次）——
            //    配置里连 `curve` 都没有时（恢复出厂后被手改删掉），等于每秒
            //    整棵树序列化 + fsync + Move 好几次，还要跟采样线程抢 model 锁。
            var dirty = false;
            var one = fan["curve"]?.DeepClone();
            if (fan["curve_cpu"] is null && one is not null) { fan["curve_cpu"] = one.DeepClone(); dirty = true; }
            if (fan["curve_gpu"] is null && one is not null) { fan["curve_gpu"] = one.DeepClone(); dirty = true; }

            // `mode_curves*` 的搬移不在这里做了（2026-11-17）：档位档案统一由
            // `ModeBundle.Migrate` 在 ModelStore.Load 里搬到 `perf.modes[档].fan`。
            // 两处都搬会出现"运行期又把 fan.mode_curves_cpu 建回来"的半新半旧状态。
            if (dirty) _model.Save();
        }
    }

    public int MinDuty => Bounds.Clamp("fan_duty", Num(Cfg, "min_duty", 30));

    public int MaxDuty => Math.Max(Bounds.Clamp("fan_duty", Num(Cfg, "max_duty", 100)), MinDuty);

    // ⚠️ 这两个值同样要过边界表（`limits.json` 里有 `hysteresis` 0.5~10 / `min_interval` 2~60）。
    //    以前是裸读配置：手改 model.json 写 `hysteresis: 20` ⇒ 首轮之后再不改速、温度墙
    //    也永远退不出来；写 `min_interval: 0` ⇒ 每轮都刷一次 EC，和采样线程抢队列。
    //    用 ClampDouble 而不是 Clamp：迟滞 0.5 °C、间隔 2.5 s 这类小数值不能被抹成整数。
    public double Hysteresis => Bounds.ClampDouble("hysteresis", Num(Cfg, "hysteresis", 2.0));
    public double MinInterval => Bounds.ClampDouble("min_interval", Num(Cfg, "min_interval", 6.0));

    /// <summary>温度保护墙，热读，夹在 `LIMITS.temp_wall`（60~100）内。</summary>
    public int Wall => Bounds.Clamp("temp_wall", Num(Cfg, "temp_wall", 95));

    public string Source => Str(Cfg, "source") ?? "cpu";

    /// <summary>
    /// `presets.fan.mode`：`auto` = 软件曲线在管，`manual` = 用户钉死转速。
    ///
    /// 开机接管与自愈都要读它：**manual 是用户的明确选择，不能抢**
    /// （抢了就是「用户钉了 60%，重启后自己变成曲线」）。
    /// </summary>
    public string FanMode => Str(Cfg, "mode") ?? "auto";

    /// <summary>
    /// `presets.fan.curve_enabled`：**软件曲线总开关**。默认 `false` ——
    /// 2026-11-18 用户定：默认让风扇听 EC 固件（开机一个字节都不写 UFA），
    /// 软件曲线属于"自定义"功能，要用户自己打开。
    ///
    /// <para>
    /// 为什么"开机不写"是最干净的固件控制：写 0x0205 的 0 是停转、非零就照那个值一直转
    /// （见 <c>EcHal.SetFans</c>），一旦写过就必须显式交回（`0x0206=0` + 三路 UFA=`0xFF`，
    /// 见 <c>EcHal.ReleaseToEc</c>）。所以三种状态这么表达 ——
    ///   · 关（本次进程没写过 UFA）：固件自己按温度调速；
    ///   · 开：曲线接管，按 CPU/GPU 各自曲线写 UFA；
    ///   · 运行中关掉：**写完交回命令就真回到固件**（2026-11-18 实测 8 秒内 1497→3802 RPM）。
    /// </para>
    /// <para>
    /// 只有交回**失败**（写入被拒 / 固件不认 `0x0206`）时，EC 才继续钉着最后一次写入的转速，
    /// 那时才需要睡眠唤醒或重启复位。界面与回包按这两种情况分开说（见 <see cref="SetEnabled"/>）。
    /// </para>
    /// <para>
    /// ⚠️ 老配置里的 `enabled` **不再被读**：那个键历史上恒为 true（"曲线不能关"），
    /// 用它当开关会让所有老用户升级后自动开着曲线，与"默认固件控制"相反。
    /// 它现在只作镜像字段（写配置时跟 <c>curve_enabled</c> 同步），给旧脚本看。
    /// </para>
    /// </summary>
    public bool CurveEnabled => Truthy(Cfg["curve_enabled"]);

    /// <summary>当前**正在生效**的档位：跟随 = EC 模式；固定 = 锁定的那档。</summary>
    public string? ActiveMode
    {
        get
        {
            if (Truthy(Cfg["bind_mode"])) return _perfKey;
            var fm = Str(Cfg, "fixed_mode");
            return fm is not null && ModeKeys.Contains(fm) ? fm : null;
        }
    }

    /// <summary>全局生效曲线（= CPU 那一路，兼容旧字段名 `curve`）。</summary>
    public List<CurvePoint> Curve => ParseCurve(Cfg["curve_cpu"] ?? Cfg["curve"]);

    /// <summary>CPU 风扇曲线（UFA1）。</summary>
    public List<CurvePoint> CurveCpu => ParseCurve(Cfg["curve_cpu"] ?? Cfg["curve"]);

    /// <summary>GPU 风扇曲线（UFA2）。</summary>
    public List<CurvePoint> CurveGpu => ParseCurve(Cfg["curve_gpu"] ?? Cfg["curve"]);

    /// <summary>
    /// 第三路（SYS / UFA3）曲线。**空 = 这一路不由曲线控制** ——
    /// 此时 <c>EcHal.SetFans</c> 会让 SYS 跟 `max(CPU, GPU)`（三风扇机型的既有行为），
    /// 两风扇机型则直接写 255（= 忽略）。
    ///
    /// ⚠️ 故意**不**退回 `curve_cpu`：SYS 是机箱/供电那一路，跟着 CPU 曲线跑等于
    ///    悄悄给它一条从没人看过的曲线。要控制就拿一条自己的（界面里选「SYS 风扇」）。
    /// </summary>
    public List<CurvePoint> CurveSys => ParseCurve(Cfg["curve_sys"]);

    public Dictionary<string, List<CurvePoint>> ModeCurves() => ModeCurvesOf(ModeBundle.FanCpu);

    public Dictionary<string, List<CurvePoint>> ModeCurvesCpu() => ModeCurvesOf(ModeBundle.FanCpu);

    public Dictionary<string, List<CurvePoint>> ModeCurvesGpu() => ModeCurvesOf(ModeBundle.FanGpu);

    public Dictionary<string, List<CurvePoint>> ModeCurvesSys() => ModeCurvesOf(ModeBundle.FanSys);

    /// <summary>
    /// 从**档案**读每档的某一路曲线（`presets.perf.modes[档].fan.cpu/gpu/sys`）。
    ///
    /// 2026-11-17 归位：以前散在 `fan.mode_curves_cpu` / `mode_curves_gpu`，和灯效的
    /// `backlight.mode_profiles` 各存一处。老配置由 `ModeBundle.Migrate` 在载入时搬过来，
    /// 所以这里**只认新位置** —— 双读会让"到底哪份是真的"永远说不清。
    /// </summary>
    private Dictionary<string, List<CurvePoint>> ModeCurvesOf(string slot)
    {
        var outDict = new Dictionary<string, List<CurvePoint>>(StringComparer.Ordinal);
        foreach (var k in ModeKeys)
            if (ModeBundle.FanCurve(_model.Presets, k, slot) is JsonArray { Count: > 0 } arr)
                outDict[k] = ParseCurve(arr);
        return outDict;
    }

    // ================================================================ 插值

    /// <summary>线性插值求目标转速（含配置上下限钳制）。</summary>
    public int Interp(IReadOnlyList<CurvePoint>? curve, double temp)
        => CurveInterpolator.Interp(curve, temp, MinDuty, MaxDuty);

    // ================================================================ 模式跟着走

    /// <summary>
    /// 采样循环每秒喂一次 EC 里的当前性能模式。
    ///
    /// 为什么必须由后端观测而不是只做界面按钮：性能模式可以被**外部**改动
    /// （原厂控制中心、Fn 快捷键直接写 EC 的 APMD），只有每秒读真值，
    /// 「曲线跟着模式走」才能对那些改动也生效。
    /// </summary>
    public void ObservePerfMode(string? key)
    {
        if (key is null || !ModeKeys.Contains(key)) return;

        if (_perfKey is null)
        {
            // 首次只记录、不换曲线：启动瞬间用户可能刚在界面上改过曲线，
            // 直接拿档位曲线盖回去会把那次编辑吞掉。
            // 但绑定本来就开着时首次要对齐，否则"开了绑定、重启后还停在别的模式曲线上"。
            _perfKey = key;
            if (Truthy(Cfg["bind_mode"])) ApplyModeCurve(key, announce: false);
            return;
        }
        if (key == _perfKey) return;
        _perfKey = key;
        if (!Truthy(Cfg["bind_mode"])) return;
        ApplyModeCurve(key, announce: true);
    }

    /// <summary>
    /// 把某档位的**一对**曲线（cpu + gpu）切进全局，并**清掉写入节流**让下一轮立刻生效。
    ///
    /// 不清 `_lastWrite` 的话，切模式后新曲线要等满 min_interval（默认 6 秒）才写 ——
    /// 用户看到的是"切了没反应"，又是一次"功能坏了"的误判。
    /// </summary>
    private bool ApplyModeCurve(string key, bool announce)
    {
        var mcCpu = ModeCurvesCpu();
        var mcGpu = ModeCurvesGpu();
        var mcSys = ModeCurvesSys();
        var hasCpu = mcCpu.TryGetValue(key, out var cCpu) && cCpu.Count > 0;
        var hasGpu = mcGpu.TryGetValue(key, out var cGpu) && cGpu.Count > 0;
        // SYS 是第三路：**没配就切回"不控制"**（把 curve_sys 清空），
        // 让 EcHal 走"跟最热/255"的兜底 —— 不能因为切了个模式就悄悄给 SYS 一条曲线。
        var hasSys = mcSys.TryGetValue(key, out var cSys) && cSys.Count > 0;
        if (!hasCpu && !hasGpu) return false;

        lock (_model.SyncRoot)
        {
            if (_model.Presets["fan"] is not JsonObject fan) return false;
            if (hasCpu) fan["curve_cpu"] = CurveToJson(cCpu!);
            if (hasGpu) fan["curve_gpu"] = CurveToJson(cGpu!);
            if (hasSys) fan["curve_sys"] = CurveToJson(cSys!);
            else fan.Remove("curve_sys");
            // 兼容镜像：旧配置文件与外部脚本仍读 `curve`（= CPU 那一路）
            fan["curve"] = (fan["curve_cpu"]?.DeepClone());
            _lastWrite = 0.0;
            if (announce)
                _switch = new JsonObject { ["to"] = key, ["duty"] = null, ["at"] = _now() };
            _model.Save();
        }
        return true;
    }

    /// <summary>配置被外部重置（恢复出厂）后，把全局曲线重新对齐当前模式。</summary>
    public bool Realign()
    {
        if (!Truthy(Cfg["bind_mode"])) return false;
        if (_perfKey is null || !ModeKeys.Contains(_perfKey)) return false;
        return ApplyModeCurve(_perfKey, announce: false);
    }

    /// <summary>
    /// 「读原厂曲线 / 恢复三档默认」之后用：**跟随和固定两种模式都**把当前生效档的
    /// 档案曲线同步进全局生效曲线并清节流，让下一轮（≤1 秒）就用上。
    ///
    /// 为什么不能只用 <see cref="Realign"/>：它只认跟随模式（`bind_mode=true`），
    /// 固定模式下直接 return false —— 用户固定着一条曲线时导入原厂档案，界面/EC
    /// 会停在旧曲线上，直到切一次模式才更新，看着就像"导入没生效"。
    /// </summary>
    public bool RealignActive()
    {
        var key = ActiveMode ?? _perfKey;
        return key is not null && ModeKeys.Contains(key) && ApplyModeCurve(key, announce: false);
    }

    // ================================================================ 改配置

    /// <summary>
    /// `POST /api/fancurve/config` —— 曲线 / 温度墙 / 绑定开关的唯一写入口。
    ///
    /// **先校验全部入参，任何一项不合法都整体拒绝**（不能写一半就落盘）。
    ///
    /// 三处顺序要求（照搬早先实现，都是踩过的）：
    ///   ① `bind_mode` 必须**先于** curve 处理 —— 开绑定时会用"当前全局曲线"补齐缺失档位，
    ///      顺序反了的话同一次请求里刚传上来的 curve 会被当成兜底抄进所有档位；
    ///   ② `fixed_mode` 必须**先于** curve —— 下面"全局 curve 要不要跟着写"依赖
    ///      `ActiveMode`，而它正是读 fixed_mode 的，顺序反了会拿旧档判断；
    ///   ③ 只切固定档、不带曲线时（界面点「用这条」），要把那一档的曲线抄进全局
    ///      并**清掉写入节流**，让风扇下一轮就用上。
    ///
    /// `apply_now`（2026-10-29 新增，界面「松手那次保存」专用）：带了曲线正文时
    ///   一并清掉写入节流 ⇒ 下一轮（≤1 秒）就用上新曲线，不必等满 `min_interval`（默认 6 秒）。
    ///   **拖动过程中的每 500 ms 自动保存不许带它** —— 那会让风扇跟着手指逐帧跳转速，
    ///   还跟采样抢 EC 队列；界面只在 `!Editor.IsDragging` 时带（见 CoolView.FlushAsync）。
    ///
    /// ⚠️ 2026-10-23 双曲线：`curve_cpu`/`curve_gpu` 分别写两路；旧字段 `curve`
    ///    （旧配置 / 旧脚本还在发）**同时写两路** —— 保持"一条曲线管两个风扇"的旧语义，
    ///    不让升级悄悄改变行为。`source` 收下但**不再落盘、不再参与控制**
    ///    （原厂没有"只听一边"这个概念，留着只会误导）。
    /// </summary>
    public JsonObject Update(
        string? source = null,
        IReadOnlyList<CurvePoint>? curve = null,
        IReadOnlyList<CurvePoint>? curveCpu = null,
        IReadOnlyList<CurvePoint>? curveGpu = null,
        IReadOnlyList<CurvePoint>? curveSys = null,
        bool? bindMode = null,
        double? tempWall = null,
        IReadOnlyDictionary<string, IReadOnlyList<CurvePoint>>? modeCurves = null,
        IReadOnlyDictionary<string, IReadOnlyList<CurvePoint>>? modeCurvesCpu = null,
        IReadOnlyDictionary<string, IReadOnlyList<CurvePoint>>? modeCurvesGpu = null,
        IReadOnlyDictionary<string, IReadOnlyList<CurvePoint>>? modeCurvesSys = null,
        string? mode = null,
        string? fixedMode = null,
        bool applyNow = false)
    {
        // ---------------- 先校验，任何一项不合法都整体拒绝 ----------------
        List<CurvePoint>? pts = null;
        if (curve is { Count: > 0 })
        {
            try { pts = CurveInterpolator.Normalize(curve); }
            catch (ArgumentException e) { return Fail("曲线格式错误：" + e.Message); }
        }

        List<CurvePoint>? ptsCpu = null;
        if (curveCpu is { Count: > 0 })
        {
            try { ptsCpu = CurveInterpolator.Normalize(curveCpu); }
            catch (ArgumentException e) { return Fail("CPU 曲线格式错误：" + e.Message); }
        }

        List<CurvePoint>? ptsGpu = null;
        if (curveGpu is { Count: > 0 })
        {
            try { ptsGpu = CurveInterpolator.Normalize(curveGpu); }
            catch (ArgumentException e) { return Fail("GPU 曲线格式错误：" + e.Message); }
        }

        // 第三路（SYS）：空数组是**有语义**的 —— 「这一路交给 EcHal 的兜底规则」，
        // 也就是删掉 curve_sys（三风扇改跟 max(cpu,gpu)、两风扇写 255）。
        List<CurvePoint>? ptsSys = null;
        var clearSys = curveSys is { Count: 0 };
        if (curveSys is { Count: > 0 })
        {
            try { ptsSys = CurveInterpolator.Normalize(curveSys); }
            catch (ArgumentException e) { return Fail("SYS 曲线格式错误：" + e.Message); }
        }

        if (fixedMode is not null && !ModeKeys.Contains(fixedMode))
            return Fail("未知模式：" + fixedMode);

        int? wall = null;
        if (tempWall is not null)
        {
            if (double.IsNaN(tempWall.Value) || double.IsInfinity(tempWall.Value))
                return Fail("温度保护墙数值非法");
            wall = Bounds.Clamp("temp_wall", tempWall.Value);
        }

        Dictionary<string, List<CurvePoint>>? mcs = null;
        if (modeCurves is not null)
        {
            mcs = new Dictionary<string, List<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in modeCurves)
            {
                if (!ModeKeys.Contains(k)) continue;         // 未知档位直接忽略（早先实现同语义）
                try { mcs[k] = CurveInterpolator.Normalize(v); }
                catch (ArgumentException e) { return Fail($"{k} 曲线格式错误：{e.Message}"); }
            }
        }

        Dictionary<string, List<CurvePoint>>? mcsCpu = null;
        if (modeCurvesCpu is not null)
        {
            mcsCpu = new Dictionary<string, List<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in modeCurvesCpu)
            {
                if (!ModeKeys.Contains(k)) continue;
                try { mcsCpu[k] = CurveInterpolator.Normalize(v); }
                catch (ArgumentException e) { return Fail($"{k} CPU 曲线格式错误：" + e.Message); }
            }
        }

        Dictionary<string, List<CurvePoint>>? mcsGpu = null;
        if (modeCurvesGpu is not null)
        {
            mcsGpu = new Dictionary<string, List<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in modeCurvesGpu)
            {
                if (!ModeKeys.Contains(k)) continue;
                try { mcsGpu[k] = CurveInterpolator.Normalize(v); }
                catch (ArgumentException e) { return Fail($"{k} GPU 曲线格式错误：" + e.Message); }
            }
        }

        Dictionary<string, List<CurvePoint>>? mcsSys = null;
        if (modeCurvesSys is not null)
        {
            mcsSys = new Dictionary<string, List<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in modeCurvesSys)
            {
                if (!ModeKeys.Contains(k)) continue;
                try { mcsSys[k] = CurveInterpolator.Normalize(v); }
                catch (ArgumentException e) { return Fail($"{k} SYS 曲线格式错误：" + e.Message); }
            }
        }

        // ---------------- 校验通过，统一落盘 ----------------
        lock (_model.SyncRoot)
        {
            if (_model.Presets["fan"] is not JsonObject fan)
                _model.Presets["fan"] = fan = new JsonObject();

            // `enabled` 只是 `curve_enabled` 的兼容镜像（见 CurveEnabled 的注释）：
            // 老配置里残留的 True 曾经被当成"曲线在跑"，现在只有 curve_enabled 说了算。
            fan["enabled"] = CurveEnabled;

            // ① 绑定开关先于曲线
            if (bindMode is { } bm)
            {
                var r = SetBind(bm);
                if (r["ok"]?.GetValue<bool>() != true) return r;
            }

            // ② 固定档先于曲线
            if (fixedMode is not null) fan["fixed_mode"] = fixedMode;

            var active = ActiveMode;

            // ---- 曲线正文：优先新双路字段，退回旧单字段（旧字段同时写两路）----
            var curveTouched = ptsCpu is not null || ptsGpu is not null || pts is not null
                               || ptsSys is not null || clearSys;
            if (ptsCpu is not null || ptsGpu is not null)
            {
                if (mode is not null && ModeKeys.Contains(mode))
                {
                    if (ptsCpu is not null) ModeBundle.SetFanCurve(_model.Presets, mode, ModeBundle.FanCpu, CurveToJson(ptsCpu));
                    if (ptsGpu is not null) ModeBundle.SetFanCurve(_model.Presets, mode, ModeBundle.FanGpu, CurveToJson(ptsGpu));
                    // 只发了一路时，另一路用当前全局值补，避免留下"半个档位"
                    if (ptsCpu is not null && ptsGpu is null)
                        ModeBundle.SetFanCurve(_model.Presets, mode, ModeBundle.FanGpu, CurveToJson(CurveGpu));
                    if (ptsGpu is not null && ptsCpu is null)
                        ModeBundle.SetFanCurve(_model.Presets, mode, ModeBundle.FanCpu, CurveToJson(CurveCpu));
                    // 全局 curve 只在"编辑的就是当前生效那一档"时跟着写，
                    // 否则会静默改掉正在跑的那条（编辑一档、改掉另一档）。
                    if (mode == active)
                    {
                        if (ptsCpu is not null) fan["curve_cpu"] = CurveToJson(ptsCpu);
                        if (ptsGpu is not null) fan["curve_gpu"] = CurveToJson(ptsGpu);
                        MirrorLegacy(fan);
                    }
                }
                else
                {
                    // 没指认档位 = 直接改全局（恢复出厂后的手工编辑）
                    if (ptsCpu is not null) fan["curve_cpu"] = CurveToJson(ptsCpu);
                    if (ptsGpu is not null) fan["curve_gpu"] = CurveToJson(ptsGpu);
                    MirrorLegacy(fan);
                }
            }
            else if (pts is not null)
            {
                // 旧字段：一条曲线同时管两个风扇（升级前后行为一致）
                if (mode is not null && ModeKeys.Contains(mode))
                {
                    ModeBundle.SetFanCurve(_model.Presets, mode, ModeBundle.FanCpu, CurveToJson(pts));
                    ModeBundle.SetFanCurve(_model.Presets, mode, ModeBundle.FanGpu, CurveToJson(pts));
                    if (mode == active)
                    {
                        fan["curve_cpu"] = CurveToJson(pts);
                        fan["curve_gpu"] = CurveToJson(pts);
                        MirrorLegacy(fan);
                    }
                }
                else
                {
                    fan["curve_cpu"] = CurveToJson(pts);
                    fan["curve_gpu"] = CurveToJson(pts);
                    MirrorLegacy(fan);
                }
            }
            else if (fixedMode is not null && !Truthy(fan["bind_mode"]))
            {
                // ③ 只切了固定目标、没带曲线 → 立刻把全局 curve 换成那一档的
                var cCpu = ModeBundle.FanCurve(_model.Presets, fixedMode, ModeBundle.FanCpu) as JsonArray;
                var cGpu = ModeBundle.FanCurve(_model.Presets, fixedMode, ModeBundle.FanGpu) as JsonArray;
                var tCpu = cCpu is null ? [] : ParseCurve(cCpu);
                var tGpu = cGpu is null ? [] : ParseCurve(cGpu);
                if (tCpu.Count > 0 || tGpu.Count > 0)
                {
                    if (tCpu.Count > 0) fan["curve_cpu"] = CurveToJson(CurveInterpolator.Normalize(tCpu));
                    if (tGpu.Count > 0) fan["curve_gpu"] = CurveToJson(CurveInterpolator.Normalize(tGpu));
                    MirrorLegacy(fan);
                    _lastWrite = 0.0;      // 解除节流：下一轮（≤1 秒）就用上
                }

                // 第三路跟着档位走：这一档有 SYS 曲线就换上，没有就**回到不控制**
                // （不能留着上一档的 SYS 曲线继续跑 —— 那等于切了模式却没换曲线）。
                var cSys = ModeBundle.FanCurve(_model.Presets, fixedMode, ModeBundle.FanSys) as JsonArray;
                var tSys = cSys is null ? [] : ParseCurve(cSys);
                if (tSys.Count > 0) fan["curve_sys"] = CurveToJson(CurveInterpolator.Normalize(tSys));
                else fan.Remove("curve_sys");
            }

            // ---- 第三路（SYS）：**独立处理**，不参与上面"只发一路就抄另一路"的互补逻辑 ----
            // 因为它默认是"不存在"的：没配 = 交给 EcHal 的兜底（跟最热 / 255）。
            // 空数组有语义 = 删掉这一路（见 Update 参数注释）。
            if (ptsSys is not null || clearSys)
            {
                var json = ptsSys is null ? null : CurveToJson(ptsSys);
                if (mode is not null && ModeKeys.Contains(mode))
                {
                    ModeBundle.SetFanCurve(_model.Presets, mode, ModeBundle.FanSys, json);
                    if (mode == active)
                    {
                        if (json is null) fan.Remove("curve_sys");
                        else fan["curve_sys"] = json.DeepClone();
                    }
                }
                else
                {
                    if (json is null) fan.Remove("curve_sys");
                    else fan["curve_sys"] = json.DeepClone();
                }
            }

            // ④ 松手那次保存（apply_now）：曲线刚落地就打断写入节流，下一轮（≤1 秒）就用上。
            //    只认"真写了曲线正文"的情形 —— 单纯改温度墙 / 绑定模式不该顺带提前写一次 EC。
            if (applyNow && curveTouched) _lastWrite = 0.0;

            if (mcs is not null || mcsCpu is not null || mcsGpu is not null || mcsSys is not null)
            {
                // 旧字段 `mode_curves` 一条管两路（升级前后行为一致）——**不管 SYS**：
                // 那个语义是"两个风扇同一条"，第三路不在其中。
                foreach (var (k, v) in mcs ?? new Dictionary<string, List<CurvePoint>>())
                {
                    ModeBundle.SetFanCurve(_model.Presets, k, ModeBundle.FanCpu, CurveToJson(v));
                    ModeBundle.SetFanCurve(_model.Presets, k, ModeBundle.FanGpu, CurveToJson(v));
                }
                foreach (var (k, v) in mcsCpu ?? new Dictionary<string, List<CurvePoint>>())
                    ModeBundle.SetFanCurve(_model.Presets, k, ModeBundle.FanCpu, CurveToJson(v));
                foreach (var (k, v) in mcsGpu ?? new Dictionary<string, List<CurvePoint>>())
                    ModeBundle.SetFanCurve(_model.Presets, k, ModeBundle.FanGpu, CurveToJson(v));
                foreach (var (k, v) in mcsSys ?? new Dictionary<string, List<CurvePoint>>())
                    ModeBundle.SetFanCurve(_model.Presets, k, ModeBundle.FanSys, CurveToJson(v));
                MirrorLegacy(fan);
            }

            if (wall is not null) fan["temp_wall"] = wall.Value;

            _model.Save();
        }

        return new JsonObject { ["ok"] = true, ["cfg"] = State() };
    }

    /// <summary>把拆分后的 CPU 曲线镜像回旧字段 `curve`（旧配置文件 / 旧测试仍读它）。</summary>
    private static void MirrorLegacy(JsonObject fan)
    {
        if (fan["curve_cpu"] is not null) fan["curve"] = fan["curve_cpu"]!.DeepClone();
    }

    /// <summary>
    /// 开/关「曲线绑定系统模式」。
    ///
    /// 开启时用当前全局曲线给缺失档位兜底 —— 绑定后每个模式都有得可换，
    /// 不会出现"切到某个模式却没曲线"的真空。
    ///
    /// ⚠️ 2026-10-13 修：**开绑定的瞬间就要把当前模式对应的档位曲线换上**。
    ///    旧实现只补齐缺失档位就返回，用户拨开开关后界面纹丝不动、转速也不变，
    ///    必须再手动切一次模式才"看见"绑定生效 —— 于是被当成开关坏了。
    /// </summary>
    public JsonObject SetBind(bool on)
    {
        lock (_model.SyncRoot)     // RLock 语义：Update 持锁调用本方法不会自锁
        {
            if (_model.Presets["fan"] is not JsonObject fan)
                _model.Presets["fan"] = fan = new JsonObject();

            if (on)
            {
                List<CurvePoint> curCpu;
                List<CurvePoint> curGpu;
                try { curCpu = CurveInterpolator.Normalize(CurveCpu); }
                catch (ArgumentException) { curCpu = []; }
                try { curGpu = CurveInterpolator.Normalize(CurveGpu); }
                catch (ArgumentException) { curGpu = []; }

                // 用当前全局曲线给**缺失**的档位兜底（已有的档位曲线一个字都不动）
                foreach (var k in ModeKeys)
                {
                    if (ModeBundle.FanCurve(_model.Presets, k, ModeBundle.FanCpu) is not JsonArray { Count: > 0 }
                        && curCpu.Count > 0)
                        ModeBundle.SetFanCurve(_model.Presets, k, ModeBundle.FanCpu, CurveToJson(curCpu));
                    if (ModeBundle.FanCurve(_model.Presets, k, ModeBundle.FanGpu) is not JsonArray { Count: > 0 }
                        && curGpu.Count > 0)
                        ModeBundle.SetFanCurve(_model.Presets, k, ModeBundle.FanGpu, CurveToJson(curGpu));
                }

                // 立刻对齐当前模式（_perfKey 未知时无从选，保留现有曲线等第一次 observe）
                var key = _perfKey;
                if (key is not null && ModeKeys.Contains(key))
                {
                    var tCpu = ParseCurve(ModeBundle.FanCurve(_model.Presets, key, ModeBundle.FanCpu));
                    var tGpu = ParseCurve(ModeBundle.FanCurve(_model.Presets, key, ModeBundle.FanGpu));
                    if (tCpu.Count > 0) fan["curve_cpu"] = CurveToJson(CurveInterpolator.Normalize(tCpu));
                    if (tGpu.Count > 0) fan["curve_gpu"] = CurveToJson(CurveInterpolator.Normalize(tGpu));
                    MirrorLegacy(fan);
                }
            }
            else
            {
                // 打开「固定曲线」→ 把固定档锁在**当前正在生效的那一档**。
                // ⚠️ 取值顺序有讲究：ActiveMode 在跟随模式下就是 _perfKey；但配置
                //    本来就是 bind_mode=False（老配置/测试夹具）时它返回 fixed_mode，
                //    所以再退一步用 _perfKey，最后才用旧的 fixed_mode。
                var cur = ActiveMode;
                if (cur is null || !ModeKeys.Contains(cur))
                    cur = _perfKey is not null && ModeKeys.Contains(_perfKey)
                        ? _perfKey
                        : Str(fan, "fixed_mode");

                if (cur is not null && ModeKeys.Contains(cur))
                    fan["fixed_mode"] = cur;
            }

            fan["bind_mode"] = on;
            _model.Save();

            return new JsonObject
            {
                ["ok"] = true,
                ["bind_mode"] = on,
                ["fixed_mode"] = Str(fan, "fixed_mode"),
            };
        }
    }

    /// <summary>
    /// 取某档位的**一对**曲线给界面编辑（三档列表点「编辑」）。
    /// **只读**：不碰全局 curve、不写 EC。
    /// </summary>
    public JsonObject EditCurve(string? mode)
    {
        if (mode is null || !ModeKeys.Contains(mode))
            return Fail("未知模式：" + mode);

        var cpu = ModeCurvesCpu();
        var gpu = ModeCurvesGpu();
        var sys = ModeCurvesSys();
        if (!cpu.TryGetValue(mode, out var cCpu) || cCpu.Count == 0)
            return Fail($"「{mode}」档还没有 CPU 曲线");

        return new JsonObject
        {
            ["ok"] = true,
            ["mode"] = mode,
            ["curve"] = CurveToJson(CurveInterpolator.Normalize(cCpu)),          // 兼容旧字段
            ["curve_cpu"] = CurveToJson(CurveInterpolator.Normalize(cCpu)),
            ["curve_gpu"] = gpu.TryGetValue(mode, out var cGpu) && cGpu.Count > 0
                ? CurveToJson(CurveInterpolator.Normalize(cGpu))
                : CurveToJson(CurveInterpolator.Normalize(cCpu)),
            // SYS：**空数组 = 这一档不控制第三路**，界面据此把 SYS 编辑器显示为"未启用"
            ["curve_sys"] = sys.TryGetValue(mode, out var cSys) && cSys.Count > 0
                ? CurveToJson(CurveInterpolator.Normalize(cSys))
                : new JsonArray(),
        };
    }

    /// <summary>界面播报完「切模式换了曲线」后调用。</summary>
    public JsonObject AckSwitch()
    {
        _switch = null;
        return new JsonObject { ["ok"] = true };
    }

    // ================================================================ 模式（manual ↔ auto）

    /// <summary>
    /// `auto` = 软件曲线接管；`manual` = 用户钉死转速（不是"关掉自动"，开机仍会被拉回 auto）。
    ///
    /// ⚠️ 为什么"自动"指软件曲线、而不是交回 EC（2026-10-13 实测，2026-11-18 补充）：
    ///   ① 写组里只有 0x0205 一个风扇命令，没有 auto/max 语义；
    ///   ② 0x0205 写 UFA=0 → 风扇**停转**，EC 不接管；
    ///   ③ EC 拿到非零 UFA 就照那个值一直转。
    ///   ⇒ 切"自动"= 让曲线接管；真要还给固件是**另一件事**：`SetEnabled(false)` /
    ///     `Stop(release:true)` 会发 `0x0206=0` + 三路 UFA=`0xFF`（见 `EcHal.ReleaseToEc`），
    ///     几秒内就回到固件自己的温度曲线，不再需要重启/睡眠唤醒。
    /// </summary>
    public JsonObject SetMode(string mode)
    {
        if (mode is not ("auto" or "manual"))
            return Fail("模式只能是 auto / manual");

        // 软件曲线关着的时候两种模式都不该进来（2026-11-18）：
        //   · manual = 手动钉转速，用户明确要求"必须开了软件曲线才能用手动风扇控制"；
        //   · auto   = 软件曲线接管，曲线压根不允许跑，回一句清楚的原因比"启动了又自停"好。
        if (!CurveEnabled)
            return new JsonObject
            {
                ["ok"] = false,
                ["need_curve"] = true,
                ["mode"] = "auto",
                ["error"] = "「自定义风扇」未开启：风扇现在由 EC 固件自动调速；"
                            + "要用自定义曲线或手动转速，请先在散热页卡头打开它",
            };

        if (mode == "auto")
        {
            if (!_hw.WritesEnabled)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["need_writes"] = true,
                    ["mode"] = "manual",
                    ["error"] = "EC 写入未开启，自动曲线接不了风扇（否则风扇会停在最后的手动值上）",
                };

            var r = Start();
            if (Truthy(r["ok"]))
            {
                lock (_model.SyncRoot)
                {
                    if (_model.Presets["fan"] is JsonObject fan) fan["mode"] = "auto";
                    _model.Save();
                }
                r["mode"] = "auto";
                r["note"] = "已切自动：由软件曲线按温度调速。要把风扇还给 EC 固件（不用软件曲线），"
                            + "关掉卡头的「自定义风扇」即可（会发交回命令，几秒内生效）";
            }
            return r;
        }

        var res = Stop(release: false);
        lock (_model.SyncRoot)
        {
            if (_model.Presets["fan"] is JsonObject fan2) fan2["mode"] = "manual";
            _model.Save();
        }
        res["mode"] = "manual";
        return res;
    }

    // ================================================================ 启动 / 停止

    /// <summary>
    /// 自定义风扇总开关（`POST /api/fancurve/enabled`，散热卡头上那颗「自定义风扇」）。
    ///
    /// **开**：先落盘 `curve_enabled=true` + `mode=auto`，再 Start。落盘在 Start 之前，
    ///   是因为自愈线程（每 30 拍）只认这个键 —— 反过来的话"没开成"会在 30 秒后
    ///   自己又跑起来，用户看到的是"我关掉的曲线自己开了"。
    /// **关**：先 `Stop(release:false)` 停写，再**真的交回固件**（`EcHal.ReleaseToEc`：
    ///   `0x0206=0` + 三路 UFA=`0xFF`）。落盘 false，并把 `mode` 归到 auto，
    ///   下次打开就是曲线模式，不会莫名其妙停在"手动"上（手动此时已不可用，见 SetMode）。
    ///
    /// <para>
    /// ⚠️ 这条是 2026-11-18 现场改的，两个认知都要更新：
    /// <list type="number">
    /// <item>旧结论「`0xFF` = 忽略并保持原值 ⇒ 只能重启/睡眠唤醒才回固件」**只对了一半**：
    ///   那是在"没清接管标志"的手动态下测的，那时 `0xFF` 其实是钳成 100%。
    ///   先写 `0x0206=0` 再写 `0xFF`，风扇 8 秒内就回到 EC 自己的温度曲线（实测 1497→3802 RPM）。</item>
    /// <item>只写 `0x0206=0`（不清 UFA）**没用**：风扇继续按钉住的 30% 转。</item>
    /// </list>
    /// 所以关掉开关 = 交回，不是"写最后一脚曲线值"（旧实现写按温度算的值，那是钉住）。
    /// 交回失败时如实报"已停写 · EC 保持 x%"，不假装回到了固件。
    /// </para>
    /// </summary>
    public JsonObject SetEnabled(bool on)
    {
        if (!on)
        {
            var stop = Stop(release: false);

            lock (_model.SyncRoot)
            {
                if (_model.Presets["fan"] is not JsonObject fan)
                    _model.Presets["fan"] = fan = new JsonObject();
                fan["curve_enabled"] = false;
                fan["enabled"] = false;
                if (Str(fan, "mode") == "manual") fan["mode"] = "auto";
                _model.Save();
            }

            // ② **真的交回固件**：`0x0206=0` 清接管标志 + 三路 UFA=0xFF 清目标转速
            //    （CCLite `FanService.Enable(false)` / FanControlPortable `RestoreAuto()`
            //    同款，2026-11-18 本机实证：做完这两步风扇 8 秒内从 1497 回到 3802 RPM，
            //    然后跟着温度自己走 —— 不再需要"重启或睡眠唤醒"）。
            //
            // ⚠️ 顺序不能反：Stop(release:false) 已经把曲线线程 Join 掉了，
            //    现在才写才不会被它补一脚 UFA。也**不能只写 0x0206=0** ——
            //    实测那样风扇继续按钉住的 30% 转；更不能只写 0xFF ——
            //    手动态下 0xFF 是 100%，风扇会直接满转。
            //
            bool? released = null;
            string? relErr = null;
            if (_hw.DutyApplied is not null)
            {
                try { _hw.ReleaseToEc(); released = true; }
                catch (FanWriteDeniedException e) { released = false; relErr = e.Message; }
                catch (Exception e) { released = false; relErr = e.Message; }
            }

            // 交回成功后 `DutyApplied` 会被清成 null（见 EcHal.ReleaseToEc），
            // 界面据此说"固件控制"；交回失败才说"已停写 · EC 保持 x%" —— 别假装成功。
            var keep = _hw.DutyApplied;

            return new JsonObject
            {
                ["ok"] = true,
                ["enabled"] = false,
                ["running"] = false,
                ["released"] = released,
                ["keep_duty"] = keep,
                ["note"] = released switch
                {
                    true => "已关闭：风扇已交回 EC 固件，按固件曲线自动调速",
                    false => "已关闭：本程序不再写风扇；但交回固件失败（"
                             + (relErr ?? "写入被拒") + "），EC 会保持它最后一次收到的 "
                             + (keep is null ? "转速" : keep.Value + "%")
                             + "，要完全回到固件曲线需重启或睡眠唤醒一次",
                    null when keep is null => "已关闭：风扇交给 EC 固件，本程序不再写风扇",
                    _ => "已关闭：本程序不再写风扇；但 EC 会保持它最后一次收到的 "
                         + keep!.Value + "%，要完全回到固件曲线需重启或睡眠唤醒一次",
                },
            };
        }

        lock (_model.SyncRoot)
        {
            if (_model.Presets["fan"] is not JsonObject fan)
                _model.Presets["fan"] = fan = new JsonObject();
            fan["curve_enabled"] = true;
            fan["enabled"] = true;
            fan["mode"] = "auto";
            _model.Save();
        }

        var r = Start();
        r["enabled"] = true;
        if (Truthy(r["ok"])) r["note"] = "已开启：由软件曲线按温度调速（CPU / GPU 各听各的曲线）";
        return r;
    }

    /// <summary>
    /// 接管风扇（起曲线线程）。
    /// </summary>
    /// <param name="force">
    /// **故障应急用的强制接管**：跳过「自定义风扇」总开关（<see cref="CurveEnabled"/>）那一道闸门。
    ///
    /// <para>
    /// 只在一种情况下传 true：**风扇卡死自救时把控制权交回 EC 固件、结果固件自己也管不了**
    /// （交回后转速依旧不动）。那一刻"要不要写 UFA"已经不是用户的偏好问题 ——
    /// 固件证明它不可靠，再由它调速就是把风扇留在卡死的转速上，所以由控制中心按曲线顶上。
    /// 这仍然**不是**"偷偷把曲线打开"：`curve_enabled` 一个字都不改，用户在散热页看到的
    /// 开关仍然是关的，自救标在 `/api/fancurve` 的 `guard.forced` 上；用户随手开/关一次
    /// 自定义风扇，就回到他自己选的那个状态（关 = `Stop(release:true)` 交回固件）。
    /// </para>
    /// <para>
    /// 其余闸门**一条都不放过**：EC 写入总开关、机型许可、管理员权限，
    /// 任何一条不满足照样拒绝 —— 那些是"写了也不会生效"，硬起线程只会变成一个假运行中。
    /// </para>
    /// </param>
    public JsonObject Start(bool force = false)
    {
        lock (_lifecycleLock)
        {
            if (Running) return new JsonObject { ["ok"] = true, ["running"] = true };

            // 总开关没开 ⇒ 拒绝。这是"默认交给固件"的唯一守门处：
            // 开机接管、自愈、设置页重开 EC 总闸、界面按钮全都走这里，
            // 一处拦住就等于所有路径都不会偷偷写 UFA。
            // `force` 只给"固件调速已证明失效"的卡死自救用，见参数说明。
            if (!CurveEnabled && !force)
                return Fail("「自定义风扇」未开启（风扇由 EC 固件自动调速）；"
                            + "要用自定义曲线请先在散热页卡头打开它");

            if (!_hw.WritesEnabled)
                return Fail("请先开启 EC 写入权限");

            // EC 控制开关关着（或未适配机型）⇒ 直接拒绝。
            // 不拦的话线程会先跑起来、第一轮写抛 WriteDenied 再自停 ——
            // 界面上一闪「运行中」再变红，用户以为是故障而不是"你自己关的"。
            if (!_hw.WritesAllowed)
                return Fail("EC 控制已关闭（设置页「EC 控制」开关），曲线无法接管风扇");

            // ⚠️ 非管理员时 WMI 写"被接受但静默不生效"：set_fans 不抛错、返回 ok。
            //    不在这里拦一道，曲线会永久假运行 —— 界面显示「运行中 / 目标 50%」，
            //    风扇实际听 EC 固件的（2026-10-14 修）。
            if (!_hw.AdminOk)
                return Fail("需要管理员权限：EC 写入不会生效，曲线无法接管风扇（"
                            + (_hw.AdminHint ?? "请用 run-as-admin.cmd 启动") + "）");

            // 一条曲线都没有 ⇒ 拒绝启动，说清原因。
            // 空曲线在 Interp 里的语义是"返回下限"，让它接管等于把风扇钉死在 min_duty
            // （2026-10-13 那次 84 ℃ / 1500 RPM 锁死），而界面上只会显示"运行中"。
            if (CurveCpu.Count == 0 && CurveGpu.Count == 0 && CurveSys.Count == 0)
                return Fail("风扇曲线是空的（一个温度点都没有）：不会接管风扇 —— "
                            + "请先在散热页加载默认曲线，或自己加几个点，再打开「自定义风扇」");

            lock (_model.SyncRoot)
            {
                // `enabled` 只是 `curve_enabled` 的镜像（见 CurveEnabled 注释）。
                // force=卡死自救接管时 curve_enabled 仍是 false，写死 true 会让旧脚本
                // 误判「用户已开软件曲线」，界面/诊断与真源对不上（2026-11-19 修）。
                if (_model.Presets["fan"] is JsonObject fan) fan["enabled"] = CurveEnabled;
                _model.Save();
            }

            Running = true;
            LastError = null;
            _failStreak = 0;
            _stop = false;

            // 重新接管 = 一个新的一轮，写入节流从零算。
            //
            // ⚠️ 不清 `_lastWrite` 的后果：`manual → auto`（以及自愈）是在**同一个实例**上
            //    重新 Start，上一次写 EC 的时间还留在里面 ⇒ 第一轮 Step 明明
            //    `LastDuty is null`（必须写），却被 `min_interval` 拦下，最多干等 6 秒。
            //    这期间 `Running` 已经是 true、界面显示「运行中」、`duty` 还是 null，
            //    用户看到的就是「切自动之后风扇半天不进曲线」（实测一次 3.65 s，
            //    等于「离上次写入还有多久」的余数，不是固定值，所以更像随机卡顿）。
            //    接管是用户明确要求的动作，和 `ApplyModeCurve()` / 切固定档一样
            //    应当立刻生效；节流是给**连续调速**用的，不该跨接管边界延续。
            _lastWrite = 0.0;

            var gen = ++_gen;

            _thread = new Thread(() => Loop(gen))
            {
                IsBackground = true,
                Name = "fancurve",
            };
            _thread.Start();
            return new JsonObject { ["ok"] = true, ["running"] = true };
        }
    }

    /// <summary>
    /// 交回时该写的转速 = 各路曲线在当前温度下的目标值。
    ///
    /// ⚠️ 这里**曾经写 min_duty（30%）**，是「关掉手动风扇就卡住」的直接元凶：
    ///    CPU 84~88°C 时写下限 30% → 1500 RPM 一动不动（EC 拿到 UFA 就完全不接管）。
    ///    所以要么写跟温度匹配的值，要么一个字节都不写（温度未知时）。
    /// </summary>
    private (int? Cpu, int? Gpu, int? Sys) ReleaseDuty()
    {
        // ⚠️ 温度必须是**新鲜的**（与 Step 里读 EC 快照用的是同一个 10 秒上限）。
        //    陈旧的 LastTemp 等于"按一个现在并不存在的温度写 EC"，而 EC 拿到 UFA
        //    就不接管了 ⇒ 风扇会一直被钉在那个错值上，直到重启。
        var age = _now() - _lastTempAt;
        if (_lastTempAt <= 0 || age < 0 || age > TempFreshSeconds) return (null, null, null);

        var c = LastTempCpu is { } tc ? Interp(CurveCpu, tc) : (int?)null;
        var g = LastTempGpu is { } tg ? Interp(CurveGpu, tg) : (int?)null;
        // SYS 只在"有曲线 + 有新鲜温度"时交回；没曲线就传 null，让 EcHal 走它自己的规则
        var s = LastTempSys is { } ts && CurveSys.Count > 0 ? Interp(CurveSys, ts) : (int?)null;
        return (c, g, s);
    }

    /// <summary>
    /// 停止曲线线程。`release=true` 时**把风扇交回 EC 固件**
    /// （`0x0206=0` + 三路 UFA=`0xFF`，见 <see cref="EcHal.ReleaseToEc"/> 的实测数据）。
    ///
    /// 返回的 `released` 是"真交回成功没有"：null = 本来就没写过 UFA、不需要交回，
    /// false = 交了但被拒（EC 仍钉在上一个值上，`stuck` 为 true）。
    /// </summary>
    public JsonObject Stop(bool release = true)
    {
        lock (_lifecycleLock)
        {
            Running = false;
            _stop = true;
            // 先换掉代际号：还卡在 Step 里的旧一轮下一处 StillOwner 立刻失败，
            // 不会在交回之后又写一次 UFA（见 _hwGate 注释）。
            _gen++;

            // ⚠️ 等旧一轮真的退出（2026-11-12 加）。`_stop` 只挡住"下一次 while 判断"，
            //    已经进了 Step、正卡在 `_hw.SetFans` 里的那一轮照样会把命令发出去
            //    （GmWmi 严格串行 + 0.45s 节流，一次往返可达秒级）。不等它就会出现
            //    "交回值已经写下去，旧一轮随后又按曲线写了一次"，最后谁赢取决于谁先拿到 WMI 锁。
            //    工作线程不碰 `_lifecycleLock`，所以在这里 Join 不会自锁。
            try { _thread?.Join(1500); } catch { /* 超时就算了，StillOwner 还会再挡一次 */ }

            Protecting = false;
            ProtectTemp = null;
            YieldHw = false;

            var hadManual = LastDuty is not null || LastDutyGpu is not null;
            bool? released = null;
            string? err = null;

            // **交回 = ReleaseToEc**（2026-11-18 改）：`0x0206=0` + 三路 UFA=`0xFF`。
            //
            // 旧实现在这里写"当前温度对应的曲线转速"，理由是"别把风扇钉在下限上"。
            // 但那一笔**同样是钉住**（EC 拿到 UFA 就不接管），退出程序后风扇就停在
            // 那个 % 上直到重启 —— 用户要的是"关掉程序就把风扇还给固件"。
            // 现在这笔写的是交回值，EC 自己按温度曲线跑，退出后行为才是对的。
            //
            // 只在"本次进程真的写过 UFA"（DutyApplied 非 null）时才交回：
            // 曲线从没开过 / 已经交回过的机器，一个字节都不该被我们碰。
            if (release && _hw.DutyApplied is not null)
            {
                try
                {
                    // 闸内交回：等那条可能还卡在 SetFans 里的旧写入彻底完成，
                    // 交回值永远是最后落地的那一笔。
                    lock (_hwGate)
                    {
                        if (_hw.DutyApplied is not null) _hw.ReleaseToEc();
                    }
                    released = true;
                }
                catch (Exception e)
                {
                    released = false;
                    err = e.Message.Length > 0 ? e.Message : "写入被拒";
                }
            }

            LastDuty = null;
            LastDutyGpu = null;
            LastDutySys = null;
            var stuck = hadManual && released is false;
            LastError = stuck ? $"交回固件失败（风扇停在原值）：{err}" : null;

            return new JsonObject
            {
                ["ok"] = true,
                ["running"] = false,
                ["released"] = released,
                ["stuck"] = stuck,
                ["last_error"] = LastError,
            };
        }
    }

    /// <summary>
    /// 自停（区别于手动 Stop）。两个刻意的取舍：
    ///   ① **不 release** —— 会走到这里的场景本来就写不进 EC，再写一次只是把同一个错吞掉；
    ///   ② **不落盘 enabled=false** —— 曲线没有"被关掉"这个状态，自停只由 `last_error` 表达。
    /// </summary>
    private void Halt(string reason)
    {
        Running = false;
        _stop = true;
        LastError = reason;
        LastDuty = null;
        LastDutyGpu = null;
        Protecting = false;
        ProtectTemp = null;
        YieldHw = false;
    }

    /// <summary>
    /// FN+1 切换飓风：**立刻**把转速打到 <see cref="MaxDuty"/>，退出时恢复。
    ///
    /// 为什么不是只翻 `YieldHw`：曲线可能没在跑（用户钉了手动 / 曲线因故障自停），
    /// 那时没有任何循环会去写 EC —— 只翻标志位等于"按键没反应"。
    ///
    /// 为什么不是"让固件自己接管"：2026-10-20 真机两次实测结果相反（详见 Step 里的注释），
    /// UFA 一旦写过就经常压过固件的 max。自己写 100% 实测与飓风峰值一致（5002 RPM），
    /// 行为确定。
    ///
    /// ⚠️ 幂等：连着抛多个事件（一次按键一个）也只会写一次。
    /// ⚠️ 退出时**只在曲线没跑**的情况下恢复旧转速；曲线在跑就交给它，
    ///    下一轮自然会按温度写回（否则会和曲线的下一次写打架）。
    /// ⚠️ 出 max 也必须写回一个非满速值 —— 否则 UFA 永久钉在 100。
    ///    上一版只写 100 不兜底恢复 ⇒「切回 auto 依然满速」。
    /// ⚠️ **on=null（采样线程的"未知"）= 不写 EC、也不翻 YieldHw**：
    ///    2026-11-18「max 升到一半就不升了」的根因 —— 采样线程每帧调
    ///    `ApplyHwMax(FanHwMax == true)`，事件回调设 true 与采样读到 true 之间
    ///    有毫秒级窗口，某一帧读到 false ⇒ YieldHw 被清 ⇒ Step 下一轮不再钉
    ///    max、按温度写回 59% ⇒ 转速掉到一半。null 一律当"没消息"。
    /// ⚠️ 状态没翻转（`YieldHw == on`）时幂等返回：采样线程每帧重放不重复写，
    ///    也不重复设标志。
    /// </summary>
    public void ApplyHwMax(bool? on)
    {
        if (on is null) return;                     // 镜像未知：不写 EC、不翻 YieldHw

        // 软件曲线关着、EC 里也没有我们写过的值 ⇒ 风扇本来就在纯固件手里，
        // 固件自己的飓风（FMAX）直接生效，我们插一手反而是"重新接管"，
        // 退出飓风时还会按曲线写一个转速把风扇钉住。
        // 只有在 EC 里还钉着我们的 UFA 时才必须替固件推上去/收回来 ——
        // FMAX 单独压不过被钉住的 UFA（2026-11-18 三次真机结论，见下面注释）。
        //
        // ⚠️ 「临时满转」（未开曲线时点了最大转速）也走这条 early return（2026-11-25 审查）：
        //    EC 里确实是 100，但它**不归引擎**。这里一旦往下写 `SetFans(MaxDuty)`，
        //    `EcHal.SetFans` 会把 `OemFanMax` 清掉 ⇒ 用户那颗按钮从「恢复自动」退回
        //    「最大转速」，而风扇还在 100%，等于把这个状态弄丢了，之后没有一键交回的入口。
        //    满转本来就是这个状态的效果，FN+1 进出都不需要动它。
        if (!CurveEnabled && (_hw.DutyApplied is null || _hw.OemFanMax))
        {
            YieldHw = on.Value;
            return;
        }

        if (YieldHw == on.Value) return;            // 状态没翻转：幂等

        var was = YieldHw;
        YieldHw = on.Value;

        try
        {
            if (on.Value)
            {
                // ⚠️ 恢复值必须**无条件**记一次。旧写法把这个赋值放在
                //    `if (LastDuty != MaxDuty || LastDutyGpu != MaxDuty)` 里面：进飓风时
                //    两路恰好已经是 MaxDuty（曲线本来就在 100%）⇒ 不进 if ⇒
                //    `_dutyBeforeMax` 仍是 null ⇒ 手动模式退出飓风走 "prev is null ⇒ return"，
                //    风扇真的被留在 100%（注释写的是"保持 EC 原值"，可原值正是满转）。
                _dutyBeforeMax = _hw.DutyApplied ?? LastDuty;
                if (LastDuty != MaxDuty || LastDutyGpu != MaxDuty)
                {
                    // 恢复值取**EC 里真正在转的那份**（DutyApplied），不是曲线自己的
                    // LastDuty：上一轮写失败时后者已被更新成没写进去的值，退出飓风
                    // 就会把风扇钉在一个从未生效过的占空比上，而且曲线没跑时再也
                    // 没人把它纠正回来（2026-11-08 审查发现）。
                    _hw.SetFans(MaxDuty, MaxDuty);
                    LastDuty = MaxDuty;
                    LastDutyGpu = MaxDuty;
                    _lastWrite = _now();
                }
            }
            else if (was && !Running)
            {
                // 退出飓风：EC 拿到 UFA 后不会自己接管，**必须写回一个非满速值**。
                // 优先用进 max 前记下的真实转速；它可能是 null（曲线没跑过、EC 里没有我们的值），
                // 这时按当前温度从曲线算一个兜底 —— 总比把风扇留在 100% 强。
                //
                // ⚠️ **手动模式下绝不能退到曲线值**（2026-11-18 用户报"手动风扇会乱跳"的
                //    成因之一）：用户钉的是 60%，FN+1 进出一次后按曲线写回 85%，
                //    手动就变味了；而且滑块还显示 60%（手动模式 DutyCpu 是 null，
                //    界面不会回填）⇒ 看着就是"手动数字自己跳"。
                //    手动模式只认 `_dutyBeforeMax`（进 max 前 EC 里那个值）；
                //    实在没有就不写 —— 保持 EC 原值，总比替用户改成曲线值强。
                var prev = FanMode == "manual"
                    ? _dutyBeforeMax
                    : (_dutyBeforeMax ?? CurveDutyAtCurrentTemp());
                if (prev is null) return;
                _hw.SetFans(prev, prev);
                LastDuty = prev;
                LastDutyGpu = prev;
                _lastWrite = _now();
                _dutyBeforeMax = null;
            }
        }
        catch
        {
            // 按键路径不许抛：写失败时界面靠 `yielded` + 转速就能看出没生效
        }
    }

    /// <summary>
    /// 退出飓风时的兜底转速：按 EC 当前温度从 CPU/GPU 曲线插值。
    /// 只在 `_dutyBeforeMax` 记不到时用（曲线没跑过、EC 里没写过值的现场）。
    /// 一路坏读数就退回另一路，两路都没有则退到下限 —— 绝不能返回 null 让调用方
    /// 什么都不写（那等于把风扇留在 100% 满速）。
    /// </summary>
    private int? CurveDutyAtCurrentTemp()
    {
        var (cpu, gpu) = _hw.Temps(TimeSpan.FromSeconds(TempFreshSeconds));
        var cpuCurve = CurveCpu;
        var gpuCurve = CurveGpu;
        var c = cpu is { } t && t > TempValidLo && t < TempValidHi && cpuCurve.Count > 0
            ? Interp(cpuCurve, t) : (int?)null;
        var g = gpu is { } t2 && t2 > TempValidLo && t2 < TempValidHi && gpuCurve.Count > 0
            ? Interp(gpuCurve, t2) : (int?)null;

        // 两条曲线都是空的（配置被改坏）⇒ 交回时也不写：这里写下的任何一个数字都会把
        // 风扇钉住，而没有曲线就说明我们本来就没有"该转多少"的依据。
        if (c is null && g is null && cpuCurve.Count == 0 && gpuCurve.Count == 0) return null;

        return c ?? g ?? MinDuty;
    }

    // ================================================================ 循环

    private void Sleep(double seconds)
    {
        if (_sleep is not null) _sleep(seconds);
        else Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// 这一轮还是"当前那一代的循环"吗。
    /// `gen &lt; 0`（单测直接调 <see cref="Step"/> 那种情况）= 不判代际；
    /// 真正的循环必须三件事同时成立：代际号没被换掉、`Running` 还是 true、没人在停。
    /// </summary>
    private bool StillOwner(int gen)
        => gen < 0 || (gen == _gen && Running && !_stop);

    private void Loop(int gen)
    {
        // gen = 启动时颁发的代际号。stop→start 时旧线程可能正睡在 sleep 里，
        // 醒来发现 _stop 已清、Running 已真 —— 靠代际号让它下一轮自检退出，
        // 否则两个循环会并发写 EC（2026-10-14 修）。
        //
        // fastRetry：接管初期的快速重试计数（见下）。上限保证"温度一直读不到"时
        // 不会 0.2 秒一轮永远空转。
        var fastRetry = 0;
        while (!_stop && Running && _gen == gen)
        {
            if (!Step(gen)) break;

            // 刚接管、一帧都还没写成：EC 温度快照多半还没被采样线程刷出来，
            // 这一轮是"什么都不做"的空转。按 1 秒一拍等下去，接管的手感就全花在
            // 等采样上了（实测开机时 Running=true 到首次写 EC 之间有 1.6 秒空窗，
            // 期间界面显示「运行中」而 duty 是空的）。这里改成 0.2 秒快跑几次，
            // 一旦写成过一帧就回到 1 秒的正常节奏。
            var firstFrame = LastDuty is null && LastDutyGpu is null;
            if (firstFrame && fastRetry < 10)
            {
                fastRetry++;
                Sleep(0.2);
            }
            else
            {
                if (!firstFrame) fastRetry = 0;
                Sleep(1.0);
            }
        }
    }

    /// <summary>
    /// 跑一轮。返回 false = 循环该退出了（已自停）。
    ///
    /// 从 <see cref="Loop"/> 里拆出来是为了**可测**：真机循环要跑几十分钟才能撞上
    /// "温度墙 + 迟滞 + 节流"的组合，这里注入假硬件 + 假时钟就能一秒跑完 150 轮。
    /// 这里只算一轮、不睡；节拍由调用方（后台线程）控制。
    /// </summary>
    internal bool Step(int gen = -1)
    {
        try
        {
            // ⚠️ 代际自检（2026-11-12 修）：`Stop()` 只在 `while` 条件上判代际，
            //    一个**已经进入 Step 的旧线程**会把这一轮完整跑完 —— 读温度 → 算占空比
            //    → SetFans → 把 LastDuty / Protecting 写回去。GmWmi 一次往返是秒级
            //    （严格串行 + 0.45s 节流），所以这个窗口不是微秒级：
            //    切手动之后旧一轮仍会写一次 EC、把 `LastDuty` 重新置成非 null，
            //    甚至把刚被 Stop 清掉的 `Protecting` 再立起来。
            if (!StillOwner(gen)) return false;

            _model.ReloadIfChanged();          // 热生效：记事本改完即生效

            // 写入开关被关掉 → 立刻自停，不等下一轮写 EC 才报错。
            // 两个理由：① 写入间隔最长 6 秒，那期间界面显示"运行中"是假显示；
            //           ② 只靠捕异常的话，某些实现下 set_fans 什么错都不抛。
            // ⚠️ 这条必须排在飓风分支**之前**：飓风现在也要写 EC（见下），
            //    开关关着时同样应该自停，而不是"让位中所以不写不算错"。
            if (!_hw.WritesEnabled)
            {
                Halt("EC 写入开关已关闭，0x0205（风扇）写入被拒，曲线已自动停止；重启控制中心即可恢复接管");
                return false;
            }

            // 固件飓风 max（FN+1）：**自己去写 max_duty**，不再"让位什么都不写"。
            //
            // ⚠️ 2026-10-20 真机推翻了「固件飓风优先于 UFA」那条老结论 —— 两次实测相反：
            //    · 22:32 按 FN+1（FMAX=1）不写 EC → 2600 爬到 5002，固件赢了；
            //    · 22:35 同样的按键与状态 → 一直停在 2600，固件**没**生效；
            //      此时手工写 70% → 3500、写 100% → 5002 ⇒ **UFA 赢**。
            //    也就是说"让位"等于把飓风悄悄关掉：用户按了键、灯亮了、风扇不动。
            //    ⇒ 不再赌固件行为，用我们自己的 100% 复现飓风（实测转速完全一致）。
            //
            // ⚠️ 用 `MaxDuty` 而不是硬编码 100：那是用户在配置里定的噪声上限
            //    （`max_duty`，温度保护墙也用同一个值）。想真·满转就把 max_duty 设 100。
            // ⚠️ 不套 `MinInterval`：按键要**立刻**有反应，而这里只在
            //    `LastDuty != MaxDuty` 时写一次，幂等，不会反复刷 EC。
            if (YieldHw)
            {
                if (LastDuty != MaxDuty || LastDutyGpu != MaxDuty)
                {
                    // 写之前再验一次身份：Stop / 重新 Start 可能就发生在这一轮的计算过程里
                    if (!StillOwner(gen)) return false;
                    _hw.SetFans(MaxDuty, MaxDuty);
                    LastDuty = MaxDuty;
                    LastDutyGpu = MaxDuty;
                    _lastWrite = _now();
                    LastError = null;
                    _failStreak = 0;
                }
                return true;
            }

            // ⚠️ 必须带 max_age：EC 读连续失败时快照不会被覆盖，无限陈旧的温度会让
            //    曲线一直按旧值输出（风扇看着在跑但跟不上）。超过 10 秒当作没有 → 本轮不写。
            var (cpu, gpu) = _hw.Temps(TimeSpan.FromSeconds(TempFreshSeconds));

            // ⚠️ 合理性校验（2026-10-14 修）：EC 回全 0 buffer 时 cpu_temp=0 是
            //    "合法解析值"但只可能是坏数据。旧代码只判 None，0 会一路插值到低于
            //    曲线首点 = min_duty(30%)，把 EC 钉死在低转速。
            //    坏读数不进 LastTemp，Stop 也就不会拿它交回。
            //
            // 2026-10-23 双曲线后**分路校验**：CPU 读数坏只影响 CPU 风扇
            //    （该路传 null = 保持 EC 当前值），GPU 照常调速。
            var cpuOk = cpu is { } c && c > TempValidLo && c < TempValidHi;
            var gpuOk = gpu is { } g && g > TempValidLo && g < TempValidHi;

            // 第三路（SYS）：温度来自另一条子命令（0x0207 a2），而**控制**要额外满足
            // "配置里有 curve_sys"。没配就是不控制 —— 此时 EcHal.On 三风扇机型让 SYS 跟
            // max(CPU,GPU)，两风扇机型直接 255（= 忽略），都是既有行为，一个字节都不变。
            var sys = _hw.SysTemp(TimeSpan.FromSeconds(TempFreshSeconds));
            var sysOk = sys is { } s && s > TempValidLo && s < TempValidHi;
            var sysCurve = CurveSys;
            var sysControlled = sysOk && sysCurve.Count > 0;

            if (!cpuOk && !gpuOk && !sysControlled) return true;

            LastTempCpu = cpuOk ? cpu : null;
            LastTempGpu = gpuOk ? gpu : null;
            // ⚠️ `LastTempSys` 的语义是"**被 SYS 曲线用上的**温度"，只在那一路真受控时才有值。
            //    `ReleaseDuty()` 靠它决定交回时写不写第三路；没有 curve_sys 就不该有值。
            //    界面要显示的"当前 SYS 温度"另走 `State()` 里的兜底读（不占这个字段）。
            LastTempSys = sysControlled ? sys : null;
            LastTemp = LastTempCpu;                 // 兼容字段 = CPU 那一路
            _lastTempAt = _now();                   // 交回时的"新鲜度"从这一刻算（见 ReleaseDuty）

            // ---- 温度保护：达到墙直接顶到 max_duty，回落到（墙-迟滞）以下才恢复曲线 ----
            // 释放侧也带迟滞（进入无迟滞、退出有迟滞的施密特思路），
            // 防止温度在墙边抖动时风扇 100%↔85% 反复横跳。
            // 保护看的是 CPU/GPU 的最高值（任一边摸到墙都得管），与走哪条曲线无关。
            // ⚠️ 该路读数坏就用 **null**（本轮不写这一路，保持 EC 当前值），
            //    不要退化成 `LastDuty ?? MinDuty`：首轮 LastDuty 还是 null 时那个兜底
            //    会把这一路直接钉在 min_duty(30%) 上 —— 正是上面注释里要防的事故形态
            //    （2026-10-31 修。只有一边读数坏、另一边正常时才会走到，原测试两条都坏，
            //     所以一直是绿的）。
            int? dutyCpu;
            int? dutyGpu;
            int? dutySys;
            // ⚠️ 保护用的温度也必须**先过合理性校验**（2026-11-12 修）：以前传的是原始
            //    cpu/gpu，一路读到 `0xFFFFFFFF` / 负值这类坏值时 ptemp 恒 ≥ 墙 ⇒
            //    两路永久顶到 max_duty、而且永远退不出来（释放条件要求 ptemp 落回墙以下），
            //    界面横幅还会显示"测得 4294967295 °C"。坏的那一路按"没有读数"处理。
            var ptemp = ProtectTempOf(cpuOk ? cpu : null, gpuOk ? gpu : null);
            var wall = Wall;
            if (ptemp is not null && (ptemp >= wall || (Protecting && ptemp >= wall - Hysteresis)))
            {
                dutyCpu = MaxDuty;
                dutyGpu = MaxDuty;
                // 保护是保险丝：SYS 只要在由曲线控制，也一起顶满（它也是散热的一部分）。
                dutySys = sysControlled ? MaxDuty : null;
                Protecting = true;
                ProtectTemp = ptemp;
            }
            else
            {
                // ⚠️ 曲线为空（`curve_cpu: []`）时**不能**交给 Interp：它给空曲线的语义是
                //    "返回下限"，接管后会把这一路死死钉在 min_duty=30%（约 1500 RPM），
                //    直到温度撞墙才顶到 100% —— 正是 2026-10-13 那次事故的形态。
                //    （空数组不会被 Cfg 的双曲线迁移补上：迁移只补 null，不补 `[]`。）
                //    SYS 那一路早在上面用 `sysCurve.Count > 0` 守住了，这里补齐 CPU/GPU。
                var cpuCurve = CurveCpu;
                var gpuCurve = CurveGpu;
                dutyCpu = cpuOk && cpuCurve.Count > 0 ? Interp(cpuCurve, cpu!.Value) : null;
                dutyGpu = gpuOk && gpuCurve.Count > 0 ? Interp(gpuCurve, gpu!.Value) : null;
                dutySys = sysControlled ? Interp(sysCurve, sys!.Value) : null;
                Protecting = false;
                ProtectTemp = null;
            }

            // 迟滞**分路**判：只有真正变了的那一路才推动写入；
            // 保护中是"跳变"不是"缓调"：差值小于迟滞也要立刻写。
            // duty 为 null = 这一路没有有效目标 ⇒ 永远不写。
            var needCpu = dutyCpu is { } dc
                          && (LastDuty is null
                              || Math.Abs(dc - LastDuty.Value) >= Hysteresis
                              || (Protecting && dc != LastDuty.Value));
            var needGpu = dutyGpu is { } dg
                          && (LastDutyGpu is null
                              || Math.Abs(dg - LastDutyGpu.Value) >= Hysteresis
                              || (Protecting && dg != LastDutyGpu.Value));
            var needSys = dutySys is { } ds
                          && (LastDutySys is null
                              || Math.Abs(ds - LastDutySys.Value) >= Hysteresis
                              || (Protecting && ds != LastDutySys.Value));
            // ⚠️ 时钟可能被往回调（NTP 校时 / 用户改时间 / 时区工具）：`_now() - _lastWrite`
            //    变成负数后，旧写法 `>= MinInterval` 会**在整个回拨区间里恒为 false** ——
            //    曲线显示"运行中"、duty 纹丝不动，连温度墙都进不去（它同样被这里门控）。
            //    负值一律当作"早该写了"。
            var sinceWrite = _now() - _lastWrite;
            var enoughTime = sinceWrite < 0 || sinceWrite >= MinInterval;

            // 温度保护是**跳变**，不参与缓调节流：进入保护那一拍就要拉满。
            // （旧实现保护也要等满 min_interval，最长 6 秒后才真升速 —— 而这 6 秒正是最热的时候。）
            if (Protecting && (needCpu || needGpu || needSys)) enoughTime = true;

            if ((needCpu || needGpu || needSys) && enoughTime)
            {
                // 闸内再确认代际：检查与写入必须原子，否则 Stop 可能在两者之间插进来
                // 完成交回，然后这笔写入把 UFA 又盖回去（见 _hwGate 注释）。
                lock (_hwGate)
                {
                    // 同上：真正落 EC 之前再确认自己仍是当前那一代
                    if (!StillOwner(gen)) return false;

                    // 没变的那路传 null ⇒ EcHal 保持 EC 里的当前值，不会把另一路刷下来。
                    // sys 传 null 时 EcHal 自有规则：三风扇跟 max(cpu,gpu)、两风扇写 255。
                    _hw.SetFans(needCpu ? dutyCpu : null, needGpu ? dutyGpu : null,
                                needSys ? dutySys : null);
                }
                if (needCpu) LastDuty = dutyCpu!.Value;
                if (needGpu) LastDutyGpu = dutyGpu!.Value;
                if (needSys) LastDutySys = dutySys!.Value;
                _lastWrite = _now();
                LastError = null;
                _failStreak = 0;

                // 补上待播报事件的实际转速：界面要告诉用户「切到狂暴后
                // 风扇目标从 50% 变成 75%」，光说换了曲线没有量。
                var sw = _switch;               // 先取本地引用：ack 可能刚把它置 null
                if (sw is not null) sw["duty"] = LastDuty;
            }
            return true;
        }
        catch (FanWriteDeniedException e)
        {
            // 确定性失败（开关被关 / 权限没了），不是抖动：每一轮都会同样失败。
            Halt(e.Message.Length > 0 ? e.Message : "EC 写入被拒绝");
            return false;
        }
        catch (Exception e)
        {
            // 一次 EC 失败不该断掉整个循环，但**不能无限吞**：
            // 持续性失败会让 Running 恒 True、界面恒显"运行中"而一个字节都没写。
            _failStreak++;
            if (_failStreak >= FailHaltRounds)
            {
                Halt($"曲线循环连续 {_failStreak} 轮异常（约 {FailHaltRounds} 秒），已自动停止：{e.Message}");
                return false;
            }
            return true;
        }
    }

    // ================================================================ 温度选择

    /// <summary>
    /// 保护专用 = CPU/GPU 取最高，**与两路曲线刻意不同**：
    /// 曲线是控制策略（各听各的），保护是保险丝（任一边摸到墙都得管）。
    /// 只想防一边就把墙调高 —— 绝不会再出现"只听 CPU 所以 GPU 烧到 100 也不加速"。
    /// </summary>
    public static double? ProtectTempOf(double? cpu, double? gpu) => MaxOf(cpu, gpu);

    private static double? MaxOf(double? a, double? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return Math.Max(a.Value, b.Value);
    }

    // ================================================================ 快照

    /// <summary>
    /// `GET /api/fancurve` 的响应体。
    /// 旧字段（`curve` / `mode_curves` / `source` / `duty` / `temp`）保留作兼容镜像，
    /// 语义分别是「CPU 那一路」与「CPU 温度」；新增的 `*_cpu` / `*_gpu` 才是真源。
    /// </summary>
    public JsonObject State()
    {
        var cfg = Cfg;

        var mc = new JsonObject();
        foreach (var (k, v) in ModeCurvesCpu()) mc[k] = CurveToJson(v);
        var mcCpu = new JsonObject();
        foreach (var (k, v) in ModeCurvesCpu()) mcCpu[k] = CurveToJson(v);
        var mcGpu = new JsonObject();
        foreach (var (k, v) in ModeCurvesGpu()) mcGpu[k] = CurveToJson(v);
        var mcSys = new JsonObject();
        foreach (var (k, v) in ModeCurvesSys()) mcSys[k] = CurveToJson(v);

        return new JsonObject
        {
            ["running"] = Running,
            // 软件曲线总开关（= `presets.fan.curve_enabled`）。false = 风扇由 EC 固件调速，
            // 界面按它把手动控制与曲线编辑区置灰（2026-11-18 用户定：默认固件控制）。
            ["enabled"] = CurveEnabled,
            // true = 固件飓风接管中（FN+1），两路都钉在 max_duty
            ["yielded"] = YieldHw,
            // 调试开关：两风扇机型也能编辑 SYS 曲线（只保存，不下发 EC 的第三路）
            ["debug_sys_curve"] = Truthy(cfg["debug_sys_curve"]),
            ["mode"] = Str(cfg, "mode") ?? "auto",
            // 非 null = 曲线没在跑、但 EC 里还钉着我们写过的转速（EC 不会接管）。
            ["stuck_duty"] = Running ? null : _hw.DutyApplied,
            // 兼容字段：原厂没有"只听一边"，这个值从此不参与控制
            ["source"] = Source,
            ["curve"] = CurveToJson(CurveCpu),
            ["curve_cpu"] = CurveToJson(CurveCpu),
            ["curve_gpu"] = CurveToJson(CurveGpu),
            // 空数组 = 第三路不由曲线控制（三风扇机型改跟 max(cpu,gpu)，两风扇写 255）
            ["curve_sys"] = CurveToJson(CurveSys),
            ["duty"] = LastDuty,
            ["duty_cpu"] = LastDuty,
            ["duty_gpu"] = LastDutyGpu,
            ["duty_sys"] = LastDutySys,
            ["temp"] = LastTemp,
            ["temp_cpu"] = LastTempCpu,
            ["temp_gpu"] = LastTempGpu,
            // 曲线没在跑 / 没有 SYS 曲线时，也从 EC 快照里把当前 SYS 温度吐出去：
            // 界面的 SYS 曲线编辑器只有这一个温度来源（CPU/GPU 另有状态卡），
            // 否则用户还没建 SYS 曲线时只能看到 "--"。读的是 `CachedStatus`，不发 WMI。
            // ⚠️ 这不改变控制：`duty_sys` 仍由 `CurveSys` 是否为空决定（见 Step）。
            ["temp_sys"] = LastTempSys ?? _hw.SysTemp(TimeSpan.FromSeconds(TempFreshSeconds)),
            ["min_duty"] = MinDuty,
            ["max_duty"] = MaxDuty,
            ["hysteresis"] = Hysteresis,
            ["min_interval"] = MinInterval,
            ["last_error"] = LastError,
            ["temp_wall"] = (double)Wall,
            ["protecting"] = Protecting,
            ["protect_temp"] = ProtectTemp,
            ["bind_mode"] = Truthy(cfg["bind_mode"]),
            ["perf_mode"] = _perfKey,
            ["fixed_mode"] = Str(cfg, "fixed_mode"),
            ["active_mode"] = ActiveMode,
            ["mode_curves"] = mc,
            ["mode_curves_cpu"] = mcCpu,
            ["mode_curves_gpu"] = mcGpu,
            ["mode_curves_sys"] = mcSys,
            ["switch"] = _switch?.DeepClone(),
        };
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread?.Join(1500); } catch { /* 不阻塞退出 */ }
    }

    // ================================================================ 小工具

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };

    /// <summary>读数值节点。⚠️ 不能用 TryGetValue&lt;double&gt;（不跨 Int32/Double 转换）。</summary>
    private static double Num(JsonObject o, string key, double fallback)
    {
        if (o[key] is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            return fallback;
        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    private static string? Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Truthy(JsonNode? n) => Devices.Device.Truthy(n);

    /// <summary>JSON 数组 → 点列表。形状不对的点直接跳过（配置文件是用户可手改的）。</summary>
    public static List<CurvePoint> ParseCurve(JsonNode? node)
    {
        var list = new List<CurvePoint>();
        if (node is not JsonArray arr) return list;
        foreach (var it in arr)
        {
            if (it is not JsonObject o) continue;
            var t = Num(o, "t", double.NaN);
            var d = Num(o, "d", double.NaN);
            if (double.IsNaN(t) || double.IsNaN(d)) continue;
            list.Add(new CurvePoint(t, (int)Math.Round(d, MidpointRounding.ToEven)));
        }
        list.Sort((a, b) => a.T.CompareTo(b.T));
        return list;
    }

    private static JsonArray CurveToJson(IEnumerable<CurvePoint> pts)
    {
        var arr = new JsonArray();
        foreach (var p in pts) arr.Add(new JsonObject { ["t"] = p.T, ["d"] = p.D });
        return arr;
    }

    /// <summary>曲线点 → 配置用的 {t,d} 数组。给只读导入等外部纯转换用（不碰任何状态）。</summary>
    public static JsonArray PointsToJson(IEnumerable<CurvePoint> pts) => CurveToJson(pts);
}
