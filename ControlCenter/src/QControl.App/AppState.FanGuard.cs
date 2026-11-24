using System.Text.Json.Nodes;
using QControl.Core.FanCurve;

namespace QControl.App;

/// <summary>
/// AppState 分片：**风扇卡死自救**（判据见 <see cref="FanStuckGuard"/>）。
///
/// <para>
/// 场景：用户**没有**开「手动风扇转速」（绝大多数人 —— 默认就是让 EC 固件调速，
/// 或者开着软件曲线跑 auto）。这时候风扇如果卡在一个转速不动 —— 最典型的是
/// 「84 °C 还 1500 RPM」「写 30/90/100 回显都对、转速一点不动」—— 只能靠睡眠唤醒
/// 或重启才恢复，用户根本没别的办法。
/// </para>
///
/// <para>
/// <b>两级自救</b>（2026-11-18 用户定："交给 EC 时如果还是无效，就交给控制中心，
/// 软件控制风扇"）：
/// <list type="number">
/// <item><b>交回固件</b>：<c>0x0206=0</c> + 三路 UFA=<c>0xFF</c>
///   （见 <c>EcHal.ReleaseToEc</c>；实测 8 秒内 1497→3802 RPM 回到固件自己的曲线）。
///   原本曲线在跑的话紧接着重新接管（第一次写 UFA 前 <c>EnsureFanControl</c> 会重新置
///   <c>0x0206=1</c>）。</item>
/// <item><b>软件接管</b>：交回后 30 秒转速还是一动不动 ⇒ 固件也管不了，这一轮直接
///   `Start(force:true)` 由控制中心按曲线写转速（**不改**用户的 `curve_enabled`，
///   只在 `/api/fancurve` 的 `guard.forced` 上标出来）。</item>
/// </list>
/// 全程**不写"猜的转速"** —— 写一个值只会把风扇重新钉住，那正是事故形态本身。
/// </para>
///
/// <para>
/// 五道闸门，缺一不可（宁可不动手，也不替用户乱写）：
/// <list type="number">
/// <item>用户开着**手动风扇**（`mode=manual`）⇒ 一律不动 —— 那是他钉的转速，
///   "卡住"是他的选择，不是故障；</item>
/// <item>正在「观察原厂」/ 一键采集 ⇒ 不动，那段时间的 EC 写会污染采集包；</item>
/// <item>EC 写入总闸关 / 机型不允许写 / 非管理员 ⇒ 写了也不生效，只报不自救；</item>
/// <item>转速读数为 0 ⇒ 不判（风停或没有转速传感器，写 UFA 也救不回来）；</item>
/// <item>同一段卡死最多 3 次、间隔递增（180 / 360 / 540 秒；交回固件后如果仍卡，
///   第二次提前到 30 秒）⇒ 反复无效说明是真故障，停下来报，不要每 3 分钟折腾一次风扇。</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ **不弹 OSD**（2026-11-18 用户定）：自救是后台动作，用户没在操作时突然亮一块屏幕
/// 反而像出事。痕迹只留 `[fanguard]` 日志 + `/api/fancurve` 的 `guard` 段，
/// 复盘时按时间戳对得上。
/// </para>
/// </summary>
public sealed partial class AppState
{
    private readonly FanStuckGuard _fanGuard = new();
    private FanGuardVerdict _fanGuardVerdict = FanGuardVerdict.None;
    private double _guardLastRescueAt;
    private bool _guardLastRescueOk;
    private string? _guardLastRescueKind;     // "release" = 交回固件 / "software" = 软件接管
    private string? _guardLastError;
    private bool _guardStuckLogged;           // 这一段卡死只在判定那一刻打一行，别每秒刷屏

    /// <summary>下一次自救直接走"软件接管"（上一次交回固件已经证明无效）。</summary>
    private bool _guardEscalateNext;

    /// <summary>现在处于"固件失效 ⇒ 控制中心强制接管"的状态（用户的 curve_enabled 没改）。</summary>
    private bool _guardForced;

    private static double NowSec => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private JsonObject? GuardCfg => (_model.Presets["fan"] as JsonObject)?["guard"] as JsonObject;

    /// <summary>
    /// 自救总开关（`presets.fan.guard.enabled`，**默认开** —— 它就是给"用户什么都没配"
    /// 的默认态兜底的；关掉等于放任风扇卡死到重启）。
    /// </summary>
    private bool GuardEnabled => GuardCfg?["enabled"] is { } n ? Truthy(n) : true;

    /// <summary>把用户写在配置里的阈值套进判定器（每拍现读，记事本改完即生效）。</summary>
    private void ApplyGuardConfig()
    {
        var g = GuardCfg;
        if (g is null) return;
        if (Num(g["hot_temp"]) is { } ht) _fanGuard.HotTemp = ht;
        if (Num(g["hot_rpm"]) is { } hr) _fanGuard.HotRpm = (int)Math.Round(hr);
        if (Num(g["hot_frozen_s"]) is { } hf) _fanGuard.HotFrozenSeconds = hf;
        if (Num(g["frozen_s"]) is { } fz) _fanGuard.FrozenSeconds = fz;
        if (Num(g["temp_swing"]) is { } ts) _fanGuard.TempSwing = ts;
        if (Num(g["cooldown_s"]) is { } cd) _fanGuard.CooldownSeconds = cd;
        if (Num(g["max_attempts"]) is { } ma) _fanGuard.MaxAttempts = Math.Clamp((int)Math.Round(ma), 0, 5);
        // 转速抖动容差：不同模具的转速读数跳动幅度差很多（有的 ±10，有的 ±80），
        // 太紧会让"卡住"永远攒不满窗口，太松会把正常调速当成卡住。按机型调这一个键即可。
        if (Num(g["rpm_tol"]) is { } rt) _fanGuard.RpmTolerance = Math.Clamp((int)Math.Round(rt), 5, 600);
        if (Num(g["response_s"]) is { } rs) _fanGuard.ResponseSeconds = rs;

        // ---- 风险模型（2026-11-19）：判据不只看"卡了多久"，还看"现在多危险" ----
        // 温度越低越不急着动手（<risk_temp 干脆不判），温度越高、升温越快越早介入。
        if (Num(g["risk_temp"]) is { } rk) _fanGuard.RiskTemp = rk;
        if (Num(g["danger_temp"]) is { } dg) _fanGuard.DangerTemp = dg;
        if (Num(g["danger_frozen_s"]) is { } df) _fanGuard.DangerFrozenSeconds = df;
        if (Num(g["min_frozen_s"]) is { } mf) _fanGuard.MinFrozenSeconds = mf;
        if (Num(g["rise_warn"]) is { } rw) _fanGuard.RiseRateWarn = rw;
    }

    /// <summary>
    /// 采样线程每拍调一次（在 <see cref="HealFanCurve"/> 之后 —— 自救自己会重新 Start，
    /// 先让自愈把"曲线根本没跑"那种情况处理掉，再来处理"跑着但转速不跟"）。
    /// </summary>
    private void TryFanStuckRescue(JsonObject? snap)
    {
        // ① 总开关
        if (!GuardEnabled)
        {
            if (_fanGuard.Attempts > 0 || _fanGuard.Stuck) _fanGuard.Reset();
            _fanGuardVerdict = FanGuardVerdict.None;
            _guardEscalateNext = false;
            return;
        }

        // ② 正在「观察原厂」/ 一键采集 ⇒ 连判定都不做：那段时间风扇本来就是
        //    被我们故意停写的，温度摆动 10 度是采集动作造成的，判出来全是噪声。
        //
        //    ⚠️ 采集**整轮**都要挡（2026-11-23 审查发现的漏）：第 4 段「加 CPU 负载」会
        //    自己 `Stop(release:false)` 把转速钉住、满载 30 秒，正好命中「高温不动」；
        //    以前只判 `Observing`（观察原厂那一段），于是自救会在采集途中写 0x0206/0xFF，
        //    把 `load_samples` 的转速序列打断 —— 风扇归属的因果证据当场作废。
        if (Observing || Adopting)
        {
            if (_fanGuard.Attempts > 0 || _fanGuard.Stuck) _fanGuard.Reset();
            _fanGuardVerdict = FanGuardVerdict.None;
            _guardEscalateNext = false;
            return;
        }

        // ③ 用户手动钉了转速 ⇒ 完全不管（连"判定"都不做，避免界面报一个他不想听的状态）
        if (_fan.CurveEnabled && _fan.FanMode == "manual")
        {
            if (_fanGuard.Attempts > 0 || _fanGuard.Stuck) _fanGuard.Reset();
            _fanGuardVerdict = FanGuardVerdict.None;
            _guardEscalateNext = false;
            _guardForced = false;
            return;
        }

        // ④ "软件接管"只在曲线真的在跑时才算数：用户关掉自定义风扇（Stop(release:true)）
        //    或引擎自停之后这个状态就结束了 —— 下一段卡死重新从"交回固件"开始试。
        if (_guardForced && !_fan.Running) _guardForced = false;

        var ec = snap?["ec"] as JsonObject;
        if (ec is null) return;

        var r1 = I0(ec["fan1_rpm"]);
        var r2 = I0(ec["fan2_rpm"]);
        var r3 = I0(ec["fan3_rpm"]);
        if (r1 is null && r2 is null && r3 is null) return;   // 这台机器没有转速读数，无从判断

        ApplyGuardConfig();

        // 温度取 CPU / GPU 的最高值：两边任一边真热，风扇都该转。
        // ⚠️ 必须过合理性校验 —— EC 抖出来的 0 / 4294967295 会把"高温"判据直接点着。
        var temp = MaxValid(I0(ec["cpu_temp"]) is { } ct ? (double)ct : null,
                            I0(ec["gpu_temp"]) is { } gt ? (double)gt : null,
                            Num(snap?["gpu"]?["temp"]));

        // duty 只在我们真控制风扇时才有意义（曲线没跑就没有"下发了什么"）
        var duty = _fan.Running ? _fan.LastDuty : null;

        var v = _fanGuard.Update(NowSec, [r1, r2, r3], temp, duty, canAct: CanRescueNow());
        _fanGuardVerdict = v;

        if (!v.Stuck)
        {
            if (v.Recovered)
            {
                // 恢复了 ⇒ "交回无效"这条结论作废：下一次卡死重新从交回固件开始试。
                // `_guardForced` 不清：那代表"现在由我们在控"，与"下次怎么救"是两件事。
                _guardEscalateNext = false;

                if (_guardStuckLogged)
                {
                    _guardStuckLogged = false;
                    var how = !_guardLastRescueOk ? ""
                        : _guardLastRescueKind == "software" ? "（已由控制中心接管调速）"
                        : "（交回固件后由固件调速）";
                    Console.WriteLine("[fanguard] 风扇恢复：转速已重新跟随温度" + how);
                }
            }
            return;
        }

        if (!_guardStuckLogged)
        {
            _guardStuckLogged = true;
            // 日志要能回答"为什么是现在动手"：温度多少、温度在不在爬、按这次的风险要求等多久。
            // 用户复盘时拿这几项对时间线，比一个光秃秃的秒数有用得多。
            Console.WriteLine($"[fanguard] 判定风扇卡住：{FanName(v.Fan)} {v.Rpm ?? 0} RPM / "
                              + $"{(v.Temp is { } t ? t.ToString("0.0") : "?")} °C"
                              + $"（风险 {RiskName(v.Risk)}"
                              + (v.RisePerMin is { } rise && Math.Abs(rise) >= 0.3
                                  ? $"，{(rise >= 0 ? "升温" : "降温")} {Math.Abs(rise):0.0} °C/min"
                                  : "")
                              + $"，需持续 {v.NeedSeconds:0} 秒）"
                              + $"，实际已持续 {v.FrozenSeconds:0} 秒（{ReasonText(v.Reason)}）");
        }

        if (!v.RescueNow) return;

        // 交回固件已经试过且没效果 ⇒ 这一轮直接交给控制中心。
        if (_guardForced || _guardEscalateNext) SoftwareTakeover(v);
        else ReleaseToFirmware(v);
    }

    /// <summary>
    /// 现在允许写 EC 吗。false 时照样判定/报状态，但**不发自救命令** ——
    /// 写不进去的路径硬写只会留下一串失败日志。
    /// </summary>
    private bool CanRescueNow()
    {
        if (Observing) return false;                 // 采集期间不碰 EC
        if (!_ec.WritesEnabled) return false;
        if (!Current.WritesAllowed) return false;
        return _ec.IsAdmin();
    }

    // ================================================================ 第一级：交回固件

    /// <summary>
    /// 停写 → 交回固件（`0x0206=0` + 三路 UFA=`0xFF`）→ 曲线原来在跑就重新接管。
    ///
    /// ⚠️ 顺序不能反也不能省：不先 `Stop(release:false)`，曲线线程可能在交回之后又补一脚
    ///    UFA，把刚清掉的目标值重新钉回去（`Stop` 内部会 Join）。
    /// ⚠️ 交回失败**绝不**再补写任何值：那一刻写 UFA 等于把风扇钉在我们猜的转速上。
    /// ⚠️ 交回成功后把下一次自救提前到 30 秒后（<see cref="FanStuckGuard.Accelerate"/>）：
    ///    固件接管本该几秒内见效，30 秒还没动静就是"固件也不行"，该走软件接管了。
    /// </summary>
    private void ReleaseToFirmware(FanGuardVerdict v)
    {
        var wasRunning = _fan.Running;

        if (wasRunning) _fan.Stop(release: false);

        var ok = false;
        string? err = null;
        try
        {
            _ec.ReleaseToEc();
            ok = true;
        }
        catch (Exception e)
        {
            err = string.IsNullOrWhiteSpace(e.Message) ? "交回命令被拒" : e.Message;
        }

        if (ok && wasRunning)
        {
            try
            {
                var r = _fan.Start();
                if (r["ok"]?.GetValue<bool>() != true)
                    err = r["error"]?.GetValue<string>() ?? "重新接管失败";
            }
            catch (Exception e)
            {
                err = e.Message;
            }
        }

        RecordRescue(ok, "release", err);

        var what = $"{FanName(v.Fan)} {v.Rpm ?? 0} RPM（{ReasonText(v.Reason)}，{v.FrozenSeconds:0} 秒）";
        var attempt = $"{_fanGuard.Attempts}/{_fanGuard.MaxAttempts}";

        if (ok)
        {
            // 交回成功 ⇒ 30 秒后若仍卡，下一步直接软件接管（不用再等 180 秒）
            _guardEscalateNext = true;
            _fanGuard.Accelerate(NowSec);

            Console.WriteLine($"[fanguard] 自救（第 {attempt} 次·交回固件）：{what} → 已交回 EC 固件"
                              + (wasRunning ? "（0x0206=0 + UFA=0xFF），曲线已重新接管" : "（0x0206=0 + UFA=0xFF）")
                              + "；30 秒内转速还不跟就改由控制中心接管");
        }
        else
        {
            Console.Error.WriteLine($"[fanguard] 自救失败（第 {attempt} 次·交回固件）：{what} → 交回被拒：{err}");
        }
    }

    // ================================================================ 第二级：软件接管

    /// <summary>
    /// 交回固件没有效果 ⇒ **由控制中心按曲线接管风扇**。
    ///
    /// <para>
    /// 调 `Start(force:true)` 跳过「自定义风扇」总开关：固件刚被证明调速失效，
    /// 再让它调就是把风扇留在卡死的转速上。**不改** `curve_enabled`（界面上的开关仍是用户的
    /// 那个值），自救状态标在 `/api/fancurve` 的 `guard.forced`；用户随手开/关一次
    /// 自定义风扇就回到他自己选的状态。
    /// </para>
    /// <para>
    /// ⚠️ `force` 只放过"总开关"这一道；EC 写入总开关、机型许可、管理员权限
    ///    仍然一条都不少（见 <c>FanCurveEngine.Start</c>）。
    /// </para>
    /// </summary>
    private void SoftwareTakeover(FanGuardVerdict v)
    {
        if (_fan.Running) _fan.Stop(release: false);   // 清掉上一轮的写入节流与 duty

        var ok = false;
        string? err = null;
        try
        {
            var r = _fan.Start(force: true);
            ok = r["ok"]?.GetValue<bool>() == true;
            if (!ok) err = r["error"]?.GetValue<string>() ?? "启动曲线被拒";
        }
        catch (Exception e)
        {
            err = e.Message;
        }

        _guardForced = ok;
        if (ok) _guardEscalateNext = false;   // 已经在软件接管态，后续都走这条

        RecordRescue(ok, "software", err);

        var what = $"{FanName(v.Fan)} {v.Rpm ?? 0} RPM（{ReasonText(v.Reason)}，{v.FrozenSeconds:0} 秒）";
        var attempt = $"{_fanGuard.Attempts}/{_fanGuard.MaxAttempts}";

        if (ok)
            Console.WriteLine($"[fanguard] 自救（第 {attempt} 次·软件接管）：{what} → 交回固件无效，"
                              + "改由控制中心按曲线调速（0x0206=1 + UFA，「自定义风扇」开关保持用户设置）");
        else
            Console.Error.WriteLine($"[fanguard] 自救失败（第 {attempt} 次·软件接管）：{what} → 接管被拒：{err}");
    }

    private void RecordRescue(bool ok, string kind, string? err)
    {
        _guardLastRescueAt = NowSec;
        _guardLastRescueOk = ok;
        _guardLastRescueKind = kind;
        _guardLastError = err;
    }

    /// <summary>`GET /api/fancurve` 的响应：曲线状态 + 自救诊断段（`guard`）。</summary>
    public JsonObject FanState()
    {
        var st = _fan.State();
        st["guard"] = FanGuardState();
        return st;
    }

    /// <summary>
    /// 自救诊断段。字段名给用户对时间线用（他会拿日志和状态对照），所以都是直白的键：
    /// <c>stuck / reason / fan / rpm / frozen_s / attempts / forced / last_rescue_*</c>。
    /// </summary>
    public JsonObject FanGuardState()
    {
        var v = _fanGuardVerdict;
        return new JsonObject
        {
            ["enabled"] = GuardEnabled,
            ["stuck"] = v.Stuck,
            ["reason"] = v.Reason,
            ["fan"] = v.Fan,
            ["rpm"] = v.Rpm,
            ["temp"] = v.Temp,
            ["frozen_s"] = Math.Round(v.FrozenSeconds, 1),
            // 这一拍的风险评估：温度 + 升温速率 → 风险等级，以及"按这个风险需要等多久"。
            // `stuck` 看的是"动没动"，`risk` 看的是"要不要紧"，两个一起看才说明白为什么动手。
            ["risk"] = v.Risk,
            ["need_s"] = Math.Round(v.NeedSeconds, 0),
            ["rise_per_min"] = v.RisePerMin is { } rp ? Math.Round(rp, 1) : null,
            ["attempts"] = _fanGuard.Attempts,
            ["max_attempts"] = _fanGuard.MaxAttempts,
            ["retry_in_s"] = Math.Round(_fanGuard.RetryIn(NowSec), 0),
            // true = 固件调速失效，现在由控制中心强制接管（用户的 curve_enabled 没被改）
            ["forced"] = _guardForced,
            ["last_rescue_at"] = _guardLastRescueAt > 0 ? Math.Round(_guardLastRescueAt, 1) : null,
            ["last_rescue_ok"] = _guardLastRescueAt > 0 ? _guardLastRescueOk : null,
            ["last_rescue"] = _guardLastRescueKind,
            ["last_error"] = _guardLastError,
        };
    }

    // ================================================================ 小工具

    private static double? MaxValid(params double?[] temps)
    {
        double? best = null;
        foreach (var t in temps)
        {
            if (t is not { } x) continue;
            if (x <= FanCurveEngine.TempValidLo || x >= FanCurveEngine.TempValidHi) continue;
            if (best is null || x > best) best = x;
        }
        return best;
    }

    private static string FanName(int? fan) => fan switch
    {
        1 => "CPU 风扇",
        2 => "GPU 风扇",
        3 => "SYS 风扇",
        _ => "风扇",
    };

    private static string ReasonText(string? reason) => reason switch
    {
        FanStuckGuard.ReasonHot => "高温不转",
        FanStuckGuard.ReasonSwing => "温度变了转速不跟",
        FanStuckGuard.ReasonDuty => "下发目标后转速不跟",
        _ => "转速不动",
    };

    private static string RiskName(string? risk) => risk switch
    {
        "high" => "高",
        "medium" => "中",
        "low" => "低",
        "none" => "无",
        _ => "未知",
    };
}
