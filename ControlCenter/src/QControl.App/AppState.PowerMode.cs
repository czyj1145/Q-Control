using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Model;
using QControl.Core.Power;
using QControl.Hal.Sensors;

namespace QControl.App;

/// <summary>
/// AppState 分片：**插拔电源时自动切性能模式**（原厂控制中心的同款行为，2026-10-31 用户要求）。
///
/// 行为（真源 = `presets.perf.auto_office_on_battery`，默认开）：
///   · 拔掉适配器 → 切「办公模式」（省电、更安静）；
///   · 插回适配器 → 恢复拔电前那一档（我们替他记着，只记在内存里）；
///   · 电池上用户自己换了档 → 那次"记住"作废，插回电不再覆盖他的选择。
///
/// 四条不能省的边界：
///   ⓪ **翻转要连续保持 2.5 秒才算数**：本机的 ACLineStatus 会偶发报 0 一秒多又自己回来，
///      单拍取信就会"没断电也切办公"（见 <see cref="PowerFlipConfirmMs"/>）。
///   ① **首帧只建基线**：开机时本来就插着（或没插着）不是一次"刚拔掉"。
///      否则每次启动都可能在电池上先写一次 EC —— 那是没人要求的写。
///   ② **照走四道门**：机型/总闸（`WritesAllowed`）→ 管理员（写守卫里判）→ arm → 写，
///      一个都不跳。arm 的人工确认由"用户自己打开这颗开关"提前给了，ttl 只放 10 秒。
///   ③ **写后读回 APMD 才算成功**：WMI 在通道故障/非管理员时会"接受但不生效"，
///      `SetPerfMode` 只回一个 ok —— 光看它就会变成假成功（本项目最忌讳那种）。
///
/// ⚠️ 这条路径**不改 `presets.perf.last_mode`**：那个键的语义是"用户最后选的那一档"，
///    自动切档不是用户的选择（与 PostHandlers 的同一条规矩一致）。
/// ⚠️ 走的是后台采样线程（<see cref="Loop"/> 每拍末尾调一次），所以**关着界面也生效** ——
///    原厂那颗守护进程也是这么干的。
/// </summary>
public sealed partial class AppState
{
    /// <summary>切性能模式的高危命令（0x0300）。自动写也要先 arm，走同一条守卫。</summary>
    private const ushort PerfCmd = 0x0300;

    /// <summary>两次自动切换之间至少隔 5 秒：扩展坞 / 松动的插头会在几秒内反复通断。</summary>
    private const int AutoSwitchMinGapMs = 5000;

    /// <summary>
    /// 电源状态翻转的**确认时长**（2026-11-17 加）：新读数必须连续保持这么久才算一次真的插拔。
    ///
    /// 为什么必须有这道门：本机（Quanta NL5）的 `GetSystemPowerStatus` 会偶发把
    /// `ACLineStatus` 报成 0（电池），一秒多之后又自己回 1 —— 实测四次：
    ///   11-13 12:48:27.099 → 12:48:28.019（0.92 s）
    ///   11-15 22:54:20.571 → 22:54:22.256（1.69 s）
    ///   11-17 13:14:58.567 → 13:14:59.969（1.40 s）
    ///   11-17 21:25:48.784 → 21:25:50.500（1.72 s）
    /// 每次都被当成"拔了适配器"，写一次 EC 切办公档、再把采样间隔拉到 2 秒，
    /// 一两秒后又写回去 —— 用户看到的就是"没断电，自己切办公模式了"。
    /// 用户真正拔插一次不会在 2 秒内插回去，所以 2.5 秒能把两者干净分开。
    /// </summary>
    private const int PowerFlipConfirmMs = 2500;

    /// <summary>上一拍看到的"插着适配器"（null = 还没建基线）。**只放确认过的值**。</summary>
    private bool? _acPlugged;

    /// <summary>
    /// 当前已确认（或正在确认）的读数。与 <see cref="_acPlugged"/> 的区别：
    /// 这个值是"最近一次读数被确认成什么"，可能是刚确认、但切换还没办完（基线尚未提交）。
    /// null = 还没确认过任何值。
    /// </summary>
    private bool? _acConfirmed;

    /// <summary>正在观察的那个读数（观察期内的原始值）。</summary>
    private bool? _acSeen;

    /// <summary>当前这个待观察读数是从什么时候开始的（`Environment.TickCount64`）。</summary>
    private long _acFlippedAt;

    /// <summary>拔电前那一档（插回电时恢复用）。只在**我们真的切成功**之后才有值。</summary>
    private string? _autoPerfBefore;

    /// <summary>
    /// 上次自动切换的时刻（`Environment.TickCount64`），见 <see cref="AutoSwitchMinGapMs"/>。
    /// ⚠️ 初值取负数：默认 0 会让"开机后头 5 秒内拔电"被当成在防抖窗口里。
    /// </summary>
    private long _lastAutoSwitchAt = -AutoSwitchMinGapMs;

    /// <summary>防抖那一行日志是否已经打过（重试的这几拍不重复刷）。</summary>
    private bool _gapLogged;

    /// <summary>自动写档的结果：决定调用方要不要在下一拍重试（见 <see cref="ApplyAutoMode"/>）。</summary>
    private enum AutoWrite
    {
        /// <summary>写成了（或本来就不必写）。</summary>
        Done,

        /// <summary>瞬时原因没轮上（5 秒防抖窗口内）⇒ 下一拍再来，别把这次插拔丢掉。</summary>
        Retry,

        /// <summary>确定写不了（总闸 / 管理员 / capsule / EC 拒绝 / 异常）⇒ 这次插拔到此为止，不重试。</summary>
        GaveUp,
    }

    /// <summary>
    /// `presets.perf.auto_office_on_battery`：拔电自动切办公模式的总闸。
    /// **默认开** —— 原厂就是这个行为，用户要的也是对齐（想关随时在性能页关）。
    /// </summary>
    public bool AutoOfficeOnBattery
        => Truthy(_model.Get(JsonValue.Create(true), "perf", "auto_office_on_battery"));

    /// <summary>
    /// 这一拍的 `plugged` 读数能不能当真（= 已被确认过，或已经连续保持够了确认时长）。
    ///
    /// 只给"跟着供电形态走、但抖一下会难受"的地方用 —— 目前是采样间隔
    /// （<see cref="AppState.Status"/> 里的 `_onBattery`）。真去写 EC 的那条路
    /// 走的是 <see cref="WatchPowerSource"/> 自己的确认门，两处判据同源（同一个常量）。
    ///
    /// ⚠️ 采样线程里 `Status()` 先于 `WatchPowerSource()` 跑，所以这里看到的
    ///    `_acConfirmed` / `_acFlippedAt` 是**上一拍**的结论 —— 时间戳单调，判据照样成立。
    /// </summary>
    private bool PowerReadingSettled(bool plugged)
        => _acConfirmed == plugged
           || (_acSeen == plugged && Environment.TickCount64 - _acFlippedAt >= PowerFlipConfirmMs);

    /// <summary>
    /// 每拍采样之后看一眼电源状态（<see cref="Loop"/> 里调）。
    /// 只在**状态翻转**那一拍动手，平时只做一次布尔比较。
    /// </summary>
    private void WatchPowerSource(JsonObject snap)
    {
        // 台式机 / 读不到电池：没有"插拔"这回事
        if (snap["battery"] is not JsonObject batt || !Truthy(batt["present"])) return;
        if (batt["plugged"] is not JsonValue pv) return;
        var plugged = Truthy(pv);

        // EC 这一帧没读到档位（认不出的 APMD 也是 null）⇒ 不知道现在是什么档，别乱切
        var mode = Str((snap["perf"] as JsonObject)?["mode"]);

        // ---- ⓪ 先过确认门：单拍读数不算插拔（见 PowerFlipConfirmMs）----
        var now = Environment.TickCount64;
        if (_acSeen != plugged)
        {
            // 上一拍还在观察、而"已确认的值"正好就是现在这个 ⇒ 观察被打断了，是一次抖动
            if (_acSeen is not null && _acConfirmed == plugged)
                Console.WriteLine($"[perf] 电源读数只闪了 {now - _acFlippedAt} ms 就恢复了，已忽略：不切档");

            _acSeen = plugged;      // 新读数：重新计时
            _acFlippedAt = now;
            if (_acConfirmed != plugged) return;   // 与已确认值不同 ⇒ 进入观察
        }
        if (!PowerModePolicy.FlipConfirmed(_acConfirmed, plugged, now, _acFlippedAt, PowerFlipConfirmMs))
            return;                                                     // 还没看清，再等几拍
        if (_acConfirmed != plugged) _acConfirmed = plugged;            // 连续保持够了 ⇒ 认账

        if (_acPlugged is not { } prev)
        {
            _acPlugged = plugged;       // ① 首帧只建基线
            return;
        }

        if (prev == plugged)
        {
            // 电池上用户自己换了档 ⇒ 之前记的"插回电恢复"作废（别拿旧档覆盖他的选择）
            if (!plugged && PowerModePolicy.UserTookOver(mode, _autoPerfBefore))
            {
                Console.WriteLine($"[perf] 电池上已手动切到 {mode}：插回适配器时不再恢复旧档");
                _autoPerfBefore = null;
            }
            return;
        }

        // ---- 到这里 = 逮到了一次真实的翻转 ----
        // ⚠️ `_acPlugged` **必须"办完这次再提交"**。旧写法在判断之前就写死了：
        //    只要这一拍恰好读不到档位（EC 通道偶发失败 ⇒ status.perf 整段为 null），
        //    或者被 5 秒防抖挡一下，这次插拔就被永久吃掉了 —— 之后 `prev == plugged`
        //    恒成立，再也不会处理。用户看到的是"偶尔拔了电没切办公"，还复现不出来。
        if (plugged)
        {
            // 与拔电分支同一条纪律：这一拍没读到档位（EC 通道偶发失败 ⇒ status.perf
            // 整段为 null）就下一拍再看。否则 TargetOnAc 回 null 会走下面的
            // `_acPlugged = true`，把这次插电**永久吃掉** —— 用户手动切过的档位
            // 再也不会被恢复，而 _autoPerfBefore 还一直挂着。
            if (mode is null) return;

            var target = PowerModePolicy.TargetOnAc(_autoPerfBefore, mode);
            if (target is null)
            {
                _acPlugged = true;              // 没记过原档 / 本来就在原档 ⇒ 这次插电不用动
                return;
            }

            var from = _autoPerfBefore;
            if (ApplyAutoMode(target, from, toBattery: false) == AutoWrite.Retry) return;
            _autoPerfBefore = null;             // 一次性：成败都不再等下一次插拔
            _acPlugged = true;
            return;
        }

        var to = PowerModePolicy.TargetOnBattery(mode);
        if (to is null)
        {
            if (mode is null) return;           // 这一拍没读到档位：下一拍再看，别吃掉这次拔电
            _acPlugged = false;                 // 本来就在办公档 ⇒ 不需要写
            return;
        }

        switch (ApplyAutoMode(to, mode, toBattery: true))
        {
            case AutoWrite.Done:
                _autoPerfBefore = mode;         // 只有真写成才记（失败不记，免得插回电时误恢复）
                _acPlugged = false;
                return;

            case AutoWrite.Retry:
                return;                         // 不提交，下一拍重试

            default:                            // GaveUp：这次插拔认了，别每秒重试刷日志
                _acPlugged = false;
                return;
        }
    }

    /// <summary>
    /// 真去写 EC（+ 顺带切电源计划）。返回值告诉调用方"这次到底算不算处理完了"，
    /// 据此决定要不要记住旧档、以及要不要在下一拍重试。
    /// </summary>
    private AutoWrite ApplyAutoMode(string target, string? from, bool toBattery)
    {
        if (!AutoOfficeOnBattery) return AutoWrite.GaveUp;   // 开关关着 = 这次插拔不管

        var label = ModeLabelOf(target);
        var why = toBattery ? "已拔掉适配器" : "已接回适配器";

        // 门挡着就**说清是哪一道**（只在真的发生插拔时打一行，不是每秒刷）：
        // 否则用户打开开关拔了电，什么都看不到，只能猜"是不是没生效"。
        // 唯一的例外是防抖：那是"等一拍还会再来"的瞬时原因，日志只打头一次。
        if (Environment.TickCount64 - _lastAutoSwitchAt < AutoSwitchMinGapMs)
        {
            if (!_gapLogged)
            {
                _gapLogged = true;
                Console.WriteLine($"[perf] {why}，本来要切到「{label}」，先等一拍："
                                  + "距上次自动切换不到 5 秒（插头在反复通断？）");
            }
            return AutoWrite.Retry;
        }
        _gapLogged = false;

        var dev = Current;
        if (!dev.WritesAllowed) return Skipped("EC 写入未开启（机型未适配，或「EC 控制」被关掉）");
        if (!_ec.WritesEnabled) return Skipped("进程内部 EC 总闸是关的");
        if (!Truthy(dev.Caps()["perf_modes"])) return Skipped("本机型没有性能模式可切（caps.perf_modes 为空）");
        if (ModeNumber(target) is not { } number) return Skipped($"不认识的目标档位「{target}」");

        try
        {
            // ⚠️ arm 不是"多余的一步"：0x0300 是高危命令，写守卫见到没授权就拒绝。
            //    用户打开这颗开关 = 提前给了这次授权；ttl 只放 10 秒，过期不续。
            _ec.Arm(PerfCmd, ttl: 10);

            // ⚠️ mirrorOem:false —— 自动切档**不写**原厂 ModeBeforeDC。
            //    那个键是原厂「插电恢复拔电前那一档」的基准，原厂自己在 DC 模式下
            //    切办公也刻意不写它（PowerLineStatusCheck 的 Offline 分支）。
            //    我们拔电若写成 office(2)，插电时原厂会按它恢复成办公，和这里的
            //    `_autoPerfBefore` 恢复逻辑对着干。用户主动切档的镜像由 SetPerfMode 默认值做。
            var written = _ec.SetPerfMode(number, mirrorOem: false);
            _lastAutoSwitchAt = Environment.TickCount64;   // 写过就计一次（防抖），成不成再说
            if (!Truthy(written["ok"]))
                return Fail("EC 拒绝写入：" + (Str(written["error"]) ?? "原因未知"));

            // ③ 读回校验：只认 EC 里的 APMD 真的变成了目标值
            var want = ApmdOf(target);
            var got = (int)_ec.ReadPerfApmd();
            if (want is { } w && got != w)
                return Fail($"写入后回读不一致（期望 APMD {w}，实际 {got}）");

            Console.WriteLine(toBattery
                ? $"[perf] {why}：{from ?? "?"} → {label}（APMD={got}）"
                : $"[perf] {why}：恢复成 {label}（APMD={got}）");

            SyncPlanFor(target);        // 与手动切档同一条规矩：EC 写成了才跟电源计划
            _hotkey.ShowOsd("perf", "性能模式", label);
            return AutoWrite.Done;
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }

        AutoWrite Fail(string reason)
        {
            Console.Error.WriteLine($"[perf] {why}，自动切到「{label}」失败：{reason}"
                                    + "（模式没变，风扇曲线仍是原来那一档）");
            return AutoWrite.GaveUp;
        }

        AutoWrite Skipped(string reason)
        {
            Console.WriteLine($"[perf] {why}，本来要切到「{label}」，跳过：{reason}");
            return AutoWrite.GaveUp;
        }
    }

    /// <summary>
    /// 跟着档位切 Windows 电源计划（`presets.perf.modes[档].plan`）。
    /// 失败只记日志：计划没切到位不该把"模式已切好"这件事报成失败。
    /// </summary>
    private void SyncPlanFor(string modeKey)
    {
        var want = Str(_model.Get("perf", "modes", modeKey, "plan"))?.Trim();
        if (string.IsNullOrEmpty(want)) return;

        try
        {
            var plans = PowerPlans.List();
            var active = Str(plans["active"]);

            // 同名多条时由 PickByName 给确定性答案（本机「高性能」就有两条，见那个函数的注释）。
            // 以前的写法是"遇到第一个同名就 break" ⇒ 切到哪条取决于 powercfg 的输出顺序。
            var candidates = plans["plans"] is JsonArray list
                ? list.OfType<JsonObject>().Select(po => (Str(po["guid"]), Str(po["name"]))).ToList()
                : [];

            var guid = PowerPlans.PickByName(candidates, want, active, out var matches);

            if (guid is null)
            {
                Console.Error.WriteLine($"[perf] 没找到名为「{want}」的电源计划，跳过切换");
                return;
            }
            if (matches > 1)
            {
                Console.WriteLine($"[perf] 有 {matches} 个同名「{want}」电源计划，用 {guid}"
                                  + $"（{(guid == active ? "正在用的那条" : PowerPlans.IsStock(guid) ? "系统自带的那条" : "列表里第一条")}）");
            }
            if (guid == active) return;        // 已经在用的计划不必再 setactive（会整套重放）

            var r = PowerPlans.SetActive(guid);
            if (!Truthy(r["ok"]))
                Console.Error.WriteLine($"[perf] 电源计划切到「{want}」失败："
                                        + (Str(r["error"]) ?? r.ToJsonString()));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[perf] 切电源计划异常：" + e.Message);
        }
    }

    /// <summary>目标档位在配置里的 APMD 值（读回校验用；没有配置就给 null = 不做数值比对）。</summary>
    private int? ApmdOf(string modeKey)
        => _model.Get("perf", "modes", modeKey, "apmd") is JsonNode n && Bounds.TryToDouble(n, out var v)
            ? (int)v
            : null;

    /// <summary>档位键 → 写入用的索引（1/2/3，与 `EcHal.SetPerfMode` 的入参一致）。</summary>
    private static int? ModeNumber(string key) => key switch
    {
        "office" => 1,
        "game" => 2,
        "beast" => 3,
        _ => null,
    };

    /// <summary>档位中文名（OSD 用）。⚠️ 与 PerfView / HotkeyService 那两处同义，改文案三处一起改。</summary>
    private static string ModeLabelOf(string key) => key switch
    {
        "office" => "办公模式",
        "game" => "游戏模式",
        "beast" => "狂暴模式",
        _ => key,
    };
}
