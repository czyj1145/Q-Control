namespace QControl.Core.FanCurve;

/// <summary>
/// 一次判定的结果。<see cref="RescueNow"/> = 现在就该去做自救（这一次已经记账，
/// 调用方照做即可，不用再关心冷却）。
///
/// <para>
/// <see cref="Risk"/> / <see cref="NeedSeconds"/> / <see cref="RisePerMin"/> 是"为什么现在动手"
/// 的依据，进日志和 <c>/api/fancurve</c> 的 <c>guard</c> 段 —— 用户复盘时要看的是
/// 「当时多少度、温度在不在爬、按这次的风险需要等多久」，而不是一个光秃秃的秒数。
/// </para>
/// </summary>
public readonly record struct FanGuardVerdict(
    bool Stuck,
    string? Reason,
    int? Fan,
    int? Rpm,
    double? Temp,
    double FrozenSeconds,
    bool RescueNow,
    bool Recovered,
    string? Risk = null,
    double NeedSeconds = 0,
    double? RisePerMin = null)
{
    public static readonly FanGuardVerdict None = new(false, null, null, null, null, 0, false, false);
}

/// <summary>
/// **风扇卡死的自救判定**（纯逻辑，不碰硬件、不碰配置 —— App 层负责执行）。
///
/// <para>
/// 为什么需要它：EC 拿到 UFA 之后就不接管，一旦"写成功但转速不跟"
/// （90 °C 以上被 EC 抢走 / 固件把目标值粘住），风扇会**一直停在那个转速上**，
/// 只有睡眠唤醒或重启才放开。用户看到的形态就是「风扇卡在 1500 转，温度都 85 度了」。
/// 本类负责把这种状态**认出来**，再由 App 层发交回命令（`0x0206=0` + 三路 UFA=`0xFF`）
/// 让风扇重新听固件 —— 这就是"自救"，不用让用户重启。
/// </para>
///
/// <para>
/// 三条判据（任意一条成立就算卡住，都要求**持续**成立，避免一拍噪声就动手）：
/// <list type="number">
/// <item><b>高温不动</b>（<see cref="ReasonHot"/>）：温度已到 <see cref="HotTemp"/> 以上、
///   转速却低于 <see cref="HotRpm"/>，并且**按当前温度算出来的**等待时间已到
///   （见 <see cref="NeedSeconds"/>）。这是「84 °C 下 1500 RPM 锁死」那个事故的形态。</item>
/// <item><b>温度变了转速不跟</b>（<see cref="ReasonSwing"/>）：<see cref="FrozenSeconds"/> 内
///   温度摆了 <see cref="TempSwing"/> 度以上，转速一点点都没变。正常固件曲线是阶梯的，
///   温度跨 10 °C 不可能一级都不跳。</item>
/// <item><b>写了不该写</b>（<see cref="ReasonDuty"/>）：我们（或曲线）下发了与上一次相差
///   ≥ <see cref="DutyStep"/> 的目标，<see cref="ResponseSeconds"/> 后转速没动
///   ≥ <see cref="ResponseTolerance"/>。这是"命令被接受但没生效"的硬证据。</item>
/// </list>
/// 第二条**不认转速 0**（0 = 风停 / 没有转速传感器，写 UFA 也救不回来），
/// 第三条只在转速已经在转、目标没饱和时判（90→100 这种本来就看不出来）。
/// </para>
///
/// <para>
/// <b>不只看"卡了多久"，还看"现在有多危险"</b>（2026-11-19 用户要求）：等待时间不是一个常数，
/// 而是按**温度**和**升温速率**算出来的（<see cref="NeedSeconds"/>）——
/// <list type="bullet">
/// <item>温度在 <see cref="HotTemp"/> 时等于 <see cref="HotFrozenSeconds"/>（默认 78 °C → 90 秒）；</item>
/// <item>越接近 <see cref="DangerTemp"/> 越急，到危险温度只需 <see cref="DangerFrozenSeconds"/>
///   （默认 92 °C → 20 秒）—— 那时多等一分钟就是白烤一分钟；</item>
/// <item>温度正在快速往上爬（≥ <see cref="RiseRateWarn"/> °C/min）再打五折：
///   卡住的本质是"热量进得来、出不去"，升温速率就是风险的斜率；</item>
/// <item>温度低于 <see cref="RiskTemp"/> 时**根本不判**（除了硬证据"写了不跟"）——
///   58 °C 转速不动没有任何风险，去写 EC 只会让风扇当着用户的面抖一下。</item>
/// </list>
/// 下限是 <see cref="MinFrozenSeconds"/>：再急也要先确认"一直没动"，不能被一拍读数点着。
/// </para>
///
/// <para>
/// 自救不是无限的：每次卡住最多 <see cref="MaxAttempts"/> 次，间隔 1、2、3 倍
/// <see cref="CooldownSeconds"/> 递增；转速恢复 <see cref="RecoverSeconds"/> 后计数归零
/// （<see cref="FanGuardVerdict.Recovered"/> 会亮一次，界面据此报"自救生效"）。
/// 反复卡死说明是真故障（风扇坏 / EC 固件死），再写也没用 —— 宁可停下来报，不要每 3 分钟
/// 折腾一次风扇。
/// </para>
/// </summary>
public sealed class FanStuckGuard
{
    // ================================================================ 判据名（进日志 / 接口）

    public const string ReasonHot = "hot";        // 高温 + 转速不动
    public const string ReasonSwing = "swing";    // 温度变了转速不跟
    public const string ReasonDuty = "duty";      // 下发了新目标、转速不跟

    // ================================================================ 阈值

    /// <summary>"高温"门限（°C）。低于它只按 <see cref="TempSwing"/> 判 —— 60 °C 静音不动是正常的。</summary>
    public double HotTemp = 78;

    /// <summary>高温下"转速太低"门限（RPM）。80 °C 还低于这个值就是被钉住了。</summary>
    public int HotRpm = 2400;

    /// <summary>
    /// 刚好在 <see cref="HotTemp"/> 上要等多久（秒）。**这只是基准**：
    /// 实际等待时间按当前温度和升温速率折算，见 <see cref="NeedSeconds"/>。
    /// </summary>
    public double HotFrozenSeconds = 90;

    /// <summary>危险温度（°C）：到这儿风扇还不转，属于"别再等了"。</summary>
    public double DangerTemp = 92;

    /// <summary>危险温度上只需要等多久（秒）。</summary>
    public double DangerFrozenSeconds = 20;

    /// <summary>无论多急，至少要确认"一直没动"这么久（秒）。防止被一拍读数点着。</summary>
    public double MinFrozenSeconds = 15;

    /// <summary>升温速率警告线（°C/min）。超过它风险按比例上调（等待时间最多打五折）。</summary>
    public double RiseRateWarn = 2.0;

    /// <summary>
    /// 算升温速率用的时间窗（秒）。
    ///
    /// <para>
    /// ⚠️ 不能拿相邻两拍算（2026-11-19 实测）：EC 温度是**整数**，5 秒采样下
    /// 一个 ±1 °C 的量化跳变就是 ±12 °C/min，现场看到速率在 -23 … +6 之间乱跳，
    /// `need_s` 跟着在 90 / 45 之间蹦 —— 风险模型被单拍噪声牵着走。
    /// 用 60 秒的首尾差，量化噪声压到 ~1 °C/min，和"卡死要等几十秒"这个尺度也匹配。
    /// 窗口内样本不足 <see cref="RiseMinSpanSeconds"/> 时不给结论（null）。
    /// </para>
    /// </summary>
    public double RiseWindowSeconds = 60;

    /// <summary>算升温速率至少要有多长的跨度（秒）。</summary>
    public double RiseMinSpanSeconds = 20;

    /// <summary>
    /// **低于这个温度就不算风险**（°C）：温度摆动、写了不跟这两条判据都不触发。
    /// 58 °C 转速不动没有任何危险，去写 EC 只是让风扇当着用户的面抖一下。
    /// 硬证据（写了不跟）也要过这道门 —— 温度起来之前不需要救。
    /// </summary>
    public double RiskTemp = 65;

    /// <summary>通用"纹丝不动"窗口（秒），配合 <see cref="TempSwing"/> 用。</summary>
    public double FrozenSeconds = 150;

    /// <summary>转速抖动容差（RPM）。EC 的转速读数本身会 ±10 上下跳。</summary>
    public int RpmTolerance = 25;

    /// <summary>同一窗口里温度要变多少度才算"转速该跟了"。</summary>
    public double TempSwing = 10;

    /// <summary>下发新目标后等多久看响应（秒）。</summary>
    public double ResponseSeconds = 20;

    /// <summary>目标变化多少才算"要求变速"（百分点）。</summary>
    public int DutyStep = 10;

    /// <summary>响应判定容差（RPM）。</summary>
    public int ResponseTolerance = 60;

    /// <summary>"下发不跟"命中后，转速仍然不动时把这段卡死保持多久（秒）。</summary>
    public double DutyHoldSeconds = 120;

    /// <summary>目标到这个值以上就不做响应判定：90→100 的转速差本来就可能是 0。</summary>
    public int DutySaturated = 92;

    /// <summary>两次自救之间的基础间隔（秒），第 n 次 × n。</summary>
    public double CooldownSeconds = 180;

    /// <summary>一次卡住最多自救几次。</summary>
    public int MaxAttempts = 3;

    /// <summary>多久没再判到卡住就算恢复（秒），计数归零。</summary>
    public double RecoverSeconds = 30;

    /// <summary>
    /// 交回固件之后**仍然卡住**时，隔多久就允许下一级自救（秒）。
    ///
    /// <para>
    /// 交回命令本该在几秒内让转速重新跟随固件（2026-11-18 实测 8 秒内 1497→3802 RPM）。
    /// 所以"交回后 30 秒还是一动不动"就是**固件也管不了**的硬证据 ——
    /// 这时不该按常规冷却再干等 180 秒，而要立刻升级成"控制中心软件接管"。
    /// </para>
    /// </summary>
    public double EscalateDelaySeconds = 30;

    /// <summary>
    /// 两拍之间允许的最大间隔（秒）。超过就当作"观测断了"（睡眠唤醒 / EC 读停摆 /
    /// 采样线程被卡住），把冻结窗口清掉重新起算。
    ///
    /// <para>
    /// 为什么必须有：判据全靠"这段时间里转速一次都没变"，而窗口起点是**墙钟**。
    /// 睡眠一夜醒来、或者 EC 连续读失败两分钟之后恢复，`now - 起点` 会一下子变成
    /// 几万秒 —— 明明一帧都没观测过，却会被判成"卡了好几小时"，抬手就把风扇交回一次。
    /// </para>
    /// </summary>
    public double MaxGapSeconds = 10;

    // ================================================================ 状态

    /// <summary>每一路风扇的"冻结窗口"起点。null = 这一路没有有效读数。</summary>
    private sealed class Frozen
    {
        public double T;
        public int Rpm;
        public double TempLo;
        public double TempHi;
    }

    private readonly Frozen?[] _frozen = new Frozen?[3];

    /// <summary>"下发新目标 → 看转速跟没跟"的探针。</summary>
    private (double T, int Rpm, int Duty)? _probe;

    /// <summary>上一拍看到的目标 —— 只有它变了 ≥ <see cref="DutyStep"/> 才起探针。</summary>
    private int? _lastDuty;

    /// <summary>
    /// 「下发不跟」命中之后的**保持期**：判据本身只在一拍上成立（探针用完就拆），
    /// 但转速仍然没动 ⇒ 这是一段持续的卡死，不能下一拍就报"好了"。
    /// 转速一动或保持期结束就清掉。
    /// </summary>
    private (double T, int Rpm)? _dutyHold;

    private int _attempts;
    private double _nextAt;
    private double _lastStuckAt = double.NegativeInfinity;
    private double _lastUpdate;
    private bool _wasStuck;

    // ---- 风险模型用的温度趋势 ----
    /// <summary>最近 <see cref="RiseWindowSeconds"/> 内的温度样本（首尾差就是升温速率）。</summary>
    private readonly List<(double T, double Temp)> _temps = [];
    private double? _rise;

    // ================================================================ 只读状态（诊断用）

    public int Attempts => _attempts;
    public bool Stuck => _wasStuck;
    public double LastStuckAt => _lastStuckAt;
    /// <summary>当前升温速率（°C/min，指数平滑）；样本不够时 null。</summary>
    public double? RisePerMin => _rise;
    /// <summary>距离下次允许自救还有多久（秒），0 = 现在就可以。</summary>
    public double RetryIn(double now) => Math.Max(0, _nextAt - now);

    /// <summary>
    /// **交回固件没效果**时把下一次的冷却缩短到 <see cref="EscalateDelaySeconds"/>。
    ///
    /// <para>
    /// 由调用方在"交回命令已经发出去、于是不再该等常规冷却"的那一刻调（见 AppState.FanGuard
    /// 的升级路径）。只提前、不推后：`min` 保证它不会把已经到点的时机推迟。
    /// </para>
    /// </summary>
    public void Accelerate(double now) => _nextAt = Math.Min(_nextAt, now + EscalateDelaySeconds);

    /// <summary>换模式 / 用户改了开关时清空，别把上一段的计数带过去。</summary>
    public void Reset()
    {
        ResetWindows();
        _attempts = 0;
        _nextAt = 0;
        _lastStuckAt = double.NegativeInfinity;
        _wasStuck = false;
        _lastUpdate = 0;
        _temps.Clear();
        _rise = null;
    }

    /// <summary>只清"观测窗口"（冻结窗口 / 探针 / 保持期），配额保持不变。</summary>
    private void ResetWindows()
    {
        for (var i = 0; i < _frozen.Length; i++) _frozen[i] = null;
        _probe = null;
        _lastDuty = null;
        _dutyHold = null;
    }

    /// <summary>
    /// 喂一拍读数，返回这一拍的判定。
    /// </summary>
    /// <param name="now">同一条时钟（Unix 秒）。</param>
    /// <param name="rpms">各路转速（CPU / GPU / SYS），没有的传 null。</param>
    /// <param name="temp">**最能代表当前热负担**的温度（CPU / GPU 取最高；已过合理性校验）。</param>
    /// <param name="duty">我们当前下发的目标（%）；没在控制就传 null。</param>
    /// <param name="canAct">
    /// 现在允不允许真的写 EC（管理员 / 写入开关 / 机型许可 / 没交给原厂 / 不是用户手动钉转速）。
    /// false 时照样判卡（界面能显示），但**不给** <see cref="FanGuardVerdict.RescueNow"/>。
    /// </param>
    public FanGuardVerdict Update(double now, IReadOnlyList<int?> rpms,
                                  double? temp, int? duty, bool canAct)
    {
        // 观测断了（睡眠唤醒 / EC 读停摆 / 时钟回拨）⇒ 窗口全部重新起算。
        // 配额（attempts）不动：这还是同一段卡死，别因为睡了一觉又给三次机会。
        var gap = _lastUpdate > 0 ? now - _lastUpdate : 0;
        if (_lastUpdate > 0 && (gap > MaxGapSeconds || now < _lastUpdate))
        {
            ResetWindows();
            _temps.Clear();               // 中间断了一段时间，速率得重新起算
            _rise = null;
        }
        else if (temp is { } cur)
        {
            TrackTemp(now, cur);
        }
        _lastUpdate = now;

        var (stuck, reason, fan, rpmAt, frozenSec) = Detect(now, rpms, temp, duty);

        // ⚠️ 没有转速读数 ≠ 0 转：`Detect` 没判到卡住时 `rpmAt` 是 null，以前用 `?? 0` 兜底，
        //    于是"未卡住 + ≥78℃"的每一拍都报 high —— 风扇明明在 4000 转，界面上却常驻
        //    "风险高 / 需持续 85 秒"（2026-11-23 审查）。null 一路传下去，由 RiskOf 单独处理。
        var risk = RiskOf(temp, rpmAt);
        var need = NeedSeconds(temp, rpmAt ?? 0);

        if (!stuck)
        {
            var recovered = _wasStuck && now - _lastStuckAt >= RecoverSeconds;
            if (recovered)
            {
                _attempts = 0;
                _nextAt = 0;
                _wasStuck = false;
            }
            return FanGuardVerdict.None with
            {
                Recovered = recovered,
                Risk = risk,
                NeedSeconds = need,
                RisePerMin = _rise,
            };
        }

        _lastStuckAt = now;
        _wasStuck = true;

        // 自救配额：一次卡住最多 MaxAttempts 次，间隔 180 / 360 / 540 秒递增。
        var rescue = canAct && _attempts < MaxAttempts && now >= _nextAt;
        if (rescue)
        {
            _attempts++;
            _nextAt = now + CooldownSeconds * _attempts;
        }

        return new FanGuardVerdict(true, reason, fan, rpmAt, temp, frozenSec, rescue, false,
                                   risk, need, _rise);
    }

    /// <summary>
    /// 记一个温度样本并重算升温速率：**窗口首尾差**，不是相邻两拍差。
    /// 理由见 <see cref="RiseWindowSeconds"/> 的注释（EC 温度是整数，相邻差全是量化噪声）。
    /// </summary>
    private void TrackTemp(double now, double temp)
    {
        _temps.Add((now, temp));
        // 只留窗口内的样本；至少留两个，否则窗口里永远只有一个点
        while (_temps.Count > 2 && now - _temps[0].T > RiseWindowSeconds) _temps.RemoveAt(0);

        var span = now - _temps[0].T;
        _rise = span >= RiseMinSpanSeconds ? (temp - _temps[0].Temp) / span * 60.0 : null;
    }

    /// <summary>
    /// 风险等级：<c>high</c> / <c>medium</c> / <c>low</c> / <c>none</c>（温度未知时 <c>unknown</c>）。
    ///
    /// <para>
    /// 只看温度和转速，不看时间 —— 时间决定"等多久"（<see cref="NeedSeconds"/>），
    /// 温度决定"这事有多要紧"。两者合起来才是"要不要现在动手"。
    /// </para>
    /// </summary>
    public string RiskOf(double? temp, int? rpm)
    {
        if (temp is not { } t) return "unknown";
        if (t >= DangerTemp) return "high";
        // `rpm` 为 null = 这一轮没有转速读数（没判到卡住），不是"0 转"：
        // 那就不敢说 high，按温度给 medium；有读数时才按"低于 HotRpm"判 high。
        if (t >= HotTemp) return rpm is { } r ? (r <= HotRpm ? "high" : "medium") : "medium";
        if (t >= RiskTemp) return "low";
        return "none";
    }

    /// <summary>
    /// **按当前温度和升温速率算出来的等待时间**（秒）—— 判据不再用固定秒数。
    ///
    /// <para>
    /// 78 °C → <see cref="HotFrozenSeconds"/>（90 s）；越接近 <see cref="DangerTemp"/>
    /// 越短，到 92 °C 只要 <see cref="DangerFrozenSeconds"/>（20 s）；
    /// 升温 ≥ <see cref="RiseRateWarn"/> °C/min 再乘 0.5~1.0 的折扣；
    /// 下限 <see cref="MinFrozenSeconds"/>。
    /// </para>
    /// </summary>
    public double NeedSeconds(double? temp, int rpm)
    {
        if (temp is not { } t) return HotFrozenSeconds;

        var span = Math.Max(1.0, DangerTemp - HotTemp);
        var f = Math.Clamp((t - HotTemp) / span, 0, 1);
        var sec = HotFrozenSeconds + (DangerFrozenSeconds - HotFrozenSeconds) * f;

        // 升温越快越等不起：2 °C/min 不动，4 °C/min 及以上打五折。
        if (_rise is { } rise && rise > RiseRateWarn)
        {
            var k = Math.Clamp((rise - RiseRateWarn) / Math.Max(0.1, RiseRateWarn), 0, 1);
            sec *= 1.0 - 0.5 * k;
        }

        return Math.Max(MinFrozenSeconds, sec);
    }

    /// <summary>
    /// 判卡。返回 (是否卡住, 判据, 哪一路, 转速, 冻结了多少秒)。
    ///
    /// 顺序上先判"冻结窗口"（对固件调速的机器也成立），再判 duty 探针
    /// （只有我们在控制时才有意义）。
    /// </summary>
    private (bool Stuck, string? Reason, int? Fan, int? Rpm, double FrozenSec)
        Detect(double now, IReadOnlyList<int?> rpms, double? temp, int? duty)
    {
        for (var i = 0; i < _frozen.Length; i++)
        {
            var rpm = i < rpms.Count ? rpms[i] : null;

            // 没有读数 / 读数为 0（风停或没传感器）：不参与判定。
            // ⚠️ 0 不能当"卡住"：写 UFA 也转不起来的是坏风扇，自救只会白折腾。
            if (rpm is not { } v || v <= 0)
            {
                _frozen[i] = null;
                continue;
            }

            var f = _frozen[i];
            if (f is null)
            {
                _frozen[i] = new Frozen { T = now, Rpm = v, TempLo = temp ?? 0, TempHi = temp ?? 0 };
                continue;
            }

            // 转速动了 ⇒ 这一段"冻结"结束，从现在重新起算。
            if (Math.Abs(v - f.Rpm) > RpmTolerance)
            {
                _frozen[i] = new Frozen { T = now, Rpm = v, TempLo = temp ?? 0, TempHi = temp ?? 0 };
                continue;
            }

            if (temp is { } t)
            {
                if (f.TempLo == 0 && f.TempHi == 0) { f.TempLo = t; f.TempHi = t; }
                else
                {
                    f.TempLo = Math.Min(f.TempLo, t);
                    f.TempHi = Math.Max(f.TempHi, t);
                }
            }

            var sec = now - f.T;

            // ① 高温不动 —— 等待时间按**当前温度和升温速率**折算（见 NeedSeconds），
            //    不是固定的 90 秒：78 °C 等 90 秒，92 °C 只等 20 秒，升温快再打折。
            if (temp is { } t1 && t1 >= HotTemp && v <= HotRpm && sec >= NeedSeconds(t1, v))
                return (true, ReasonHot, i + 1, v, sec);

            // ② 温度摆了这么多度，转速一级都没跳。
            //    温度没风险（低于 RiskTemp）时不算 —— 58 °C 不动是正常的，别去打扰风扇。
            if (f.TempHi >= RiskTemp && f.TempHi - f.TempLo >= TempSwing && sec >= FrozenSeconds)
                return (true, ReasonSwing, i + 1, v, sec);
        }

        // ③ 下发新目标后转速不跟（只在我们真有目标时判，且温度得有风险才动手）
        var curRpm = FirstRpm(rpms);
        var risky = temp is { } tr && tr >= RiskTemp;

        // ③-a 保持期：上一次"下发不跟"命中后，只要转速还没动，就还处于同一段卡死。
        if (_dutyHold is { } h && risky)
        {
            if (curRpm is { } hc && Math.Abs(hc - h.Rpm) <= RpmTolerance
                && now - h.T <= DutyHoldSeconds)
                return (true, ReasonDuty, 1, hc, now - h.T);
            _dutyHold = null;
        }

        if (duty is { } d && curRpm is { } cr)
        {
            if (_probe is { } p)
            {
                if (Math.Abs(d - p.Duty) >= DutyStep)
                {
                    // 又换了目标：以现在的转速为新基线重新等
                    _probe = (now, cr, d);
                }
                else if (now - p.T >= ResponseSeconds)
                {
                    var moved = Math.Abs(cr - p.Rpm);
                    var saturated = p.Duty >= DutySaturated || d >= DutySaturated || p.Rpm >= 4500 || cr >= 4500;
                    _probe = null;
                    // `risky`：温度没风险时，"写了不跟"也不值得动风扇（低温下限钳位是正常行为）
                    if (!saturated && moved < ResponseTolerance && risky)
                    {
                        _dutyHold = (now, cr);
                        return (true, ReasonDuty, 1, cr, now - p.T);
                    }
                }
            }
            else if (_lastDuty is { } prev && Math.Abs(d - prev) >= DutyStep)
            {
                // ⚠️ **只有目标真的变了 ≥ DutyStep 才起探针**（2026-11-18 收尾时修）：
                //    以前"没有探针"就无条件起一个，于是待机这种「目标恒定 + 转速恒定」
                //    的正常稳态，20 秒后必然落到 `moved < 容差` 上 —— 一次误判就是
                //    平白交回一次风扇（转速会当着用户的面抖一下）。稳态不是卡住。
                _probe = (now, cr, d);
            }

            _lastDuty = d;
        }
        else
        {
            _probe = null;
            _lastDuty = null;
        }

        return (false, null, null, null, 0);
    }

    private static int? FirstRpm(IReadOnlyList<int?> rpms)
    {
        foreach (var r in rpms)
            if (r is { } v && v > 0) return v;
        return null;
    }
}
