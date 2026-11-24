using System.Text.Json.Nodes;
using QControl.App.Osd;
using QControl.Core.FanCurve;
using QControl.Core.Model;
using QControl.Hal.Ec;
using QControl.Hal.Sensors;

namespace QControl.App.HotKey;

/// <summary>
/// FN 快捷键 → OSD 提示的整条链路：WMI 事件 → 帧差分 → 提示条。
///
/// 两类事件、两条路：
///   · `GMC_WMIEvent`：FN 背光 / FN 性能模式等。事件**不带可识别信息**
///     （EventDetail 里那几个字节映射关系不明），只能"按完之后读一次 EC，
///     和上一帧比，谁变了就是谁" —— 也就是阶梯式差分。
///   · `WMI2Event`（CBWMI 设备）：FN+1 飓风模式。事件**自带** FMAX 状态
///     （EVID=0x30，BUFF[0x1E] 交替），一次按键一个事件，不需要差分。
///
/// ⚠️ 阶梯式差分**一律现场读 EC**，绝不用 `/api/status` 的缓存帧：
///    采样周期 1~2.8 秒，事件到达时手上那帧常常是**变化前**的；拿旧帧比基线
///    会把"回到过去"当成新变化 —— 用户报的「从低亮切高亮，却显示低亮」就是它。
/// ⚠️ 每级读到变化就停：EC 命令之间有 0.45s 节流，三级全读一遍要 2 秒多。
/// ⚠️ 两级都落空时**不是没救**：EC 有时慢一步（FN+3 的 APMD 约 0.7s 后才变），
///    所以还要在 +0.6s / +1.2s 现场补读两眼。
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    // ---- 事件类与字段偏移（2026-10-14 真机实证）----
    private const string GmcClass = "GMC_WMIEvent";
    private const string CbwmiClass = "WMI2Event";
    private const byte FanEvid = 0x30;      // BUFF[0] = EVID
    private const int FanFmaxOffset = 0x1E; // BUFF[0x1E] = FMAX（1 = 飓风，0 = 自动）

    /// <summary>
    /// `GMC_WMIEvent.EventDetail[1]` = 哪个 `_QBx` 触发的。
    /// 2026-10-19 真机对照：FN+2（背光）→ 0x00，FN+3（性能模式）→ 0x07，
    /// 与 DSDT 的 `_QB7`（`PERM+1` + `GWMI.PEMR=1`）对得上。
    /// ⚠️ 之前一直以为 EventDetail 不带可识别信息（PROTOCOL 10.3）—— 那是只看了
    ///    全零样本的结论，靠逐事件记录才发现这一位是有值的。
    /// </summary>
    private const byte DetailPerfKey = 0x07;

    // ---- 补读节奏（与早先实现的 OSD_RETRY_* 一致）----
    private static readonly TimeSpan RetryTail = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan RetryStep = TimeSpan.FromSeconds(0.6);
    private const int RetryLockWaitMs = 3000;   // 阶梯本身 2s+，等太短补读会被饿死

    /// <summary>
    /// FN+3 的档位循环顺序 = **界面上从低到强的排列**（办公 → 游戏 → 狂暴）。
    ///
    /// ⚠️ **绝不能**按 APMD 数值递增取下一档：APMD 的数值方向恰好是反的
    ///    （beast=0 · game=1 · office=2），`(apmd+1)%3` 算出来的正是「倒着切」——
    ///    2026-10-23 用户报的就是它。循环方向只认这张表，不认数值。
    /// </summary>
    private static readonly string[] PerfOrder = ["office", "game", "beast"];

    /// <summary>OSD 文案表（与界面 MODES_LABEL 一致）。</summary>
    private static readonly Dictionary<string, string> ModeLabel = new()
    {
        ["office"] = "办公模式",
        ["game"] = "游戏模式",
        ["beast"] = "狂暴模式",
    };

    private static readonly Dictionary<int, string> BriSubtitle = new()
    {
        [0] = "关闭",
        [1] = "低亮",
        [2] = "高亮",
    };

    private readonly EcHal _ec;
    private readonly FanCurveEngine _fan;
    private readonly ModelStore _model;
    private readonly object _gate = new();
    private readonly object _recentLock = new();
    private readonly List<JsonObject> _recent = new();
    private readonly object _dedupeLock = new();

    private WmiEventWatcher? _gmc;
    private WmiEventWatcher? _cbwmi;

    // 差分基线：键 = backlight / perf / fan1 / fan2
    private readonly Dictionary<string, object?> _prev = new();
    private byte[] _prevRaw = [];          // 0x0200 整段原始缓冲区（找"未映射字段"的变化）
    private string? _gmcLastDetail;        // 最近一条 GMC 事件的 EventDetail（hex）
    /// <summary>
    /// "这一轮阶梯跑完后还要再来一轮"。用 int + Interlocked 而不是 bool：
    /// 置位在**抢不到 _gate** 的那条线程上、清零+判读在持锁线程上，两边不在同一个原子操作里，
    /// 单纯 bool 会出现"持锁线程刚读到 false、另一条线程正好写 true"⇒ 那次按键的变化没人消费，
    /// 要等下一次按 FN 才被差分出来（表现：偶尔按了没提示条）。
    /// </summary>
    private int _again;

    private volatile bool _perfPending;     // 本事件是 FN+3，还没处理
    private DateTime _lastPerfCycle = DateTime.MinValue;

    private string? _lastKey;
    private DateTime _lastAt = DateTime.MinValue;

    private long _retryUntilTicks;
    private int _retryBusy;

    private volatile bool _fanHwMax;
    private volatile bool _fanHwMaxKnown;
    private string? _hotkeyError;
    private string? _cbwmiError;

    public HotkeyService(EcHal ec, FanCurveEngine fan, ModelStore model, OsdWindow osd,
                         Func<bool> vendorUiRunning)
    {
        _ec = ec;
        _fan = fan;
        _model = model;
        Osd = osd;
        _vendorUiRunning = vendorUiRunning;
    }

    /// <summary>原厂界面进程（`ControlCenter.exe`）在不在跑 —— 它在的时候 FN+3 由它同步，我们不能抢。</summary>
    private readonly Func<bool> _vendorUiRunning;

    public OsdWindow Osd { get; }

    /// <summary>FN 事件总数（GMC）。`/api/osd.events` 用的就是它。</summary>
    public long Events => _gmc?.Count ?? 0;

    /// <summary>FN+1 飓风镜像：null = 还没收到过事件（与早先实现同语义）。</summary>
    public bool? FanHwMax => _fanHwMaxKnown ? _fanHwMax : null;

    // ---------------------------------------------------------------- 生命周期

    public void Start()
    {
        _gmc = new WmiEventWatcher(GmcClass);
        if (!_gmc.Start(OnGmcEvent))
            _hotkeyError = _gmc.LastError;

        _cbwmi = new WmiEventWatcher(CbwmiClass);
        if (!_cbwmi.Start(OnCbwmiEvent))
            _cbwmiError = _cbwmi.LastError;

        // 基线在后台建：两次 EC 读约 0.9s，不能挡在启动路径上
        Task.Run(CaptureBaseline);
    }

    private void CaptureBaseline()
    {
        try
        {
            // ⚠️ 与事件路径共用一把锁：建基线时不能有差分在改 _prev
            lock (_gate)
            {
                var st = _ec.ReadStatus();
                if (ToInt(st["backlight_level"]) is { } bl) _prev["backlight"] = bl;
                if (ToInt(st["fan1_rpm"]) is { } f1) _prev["fan1"] = f1;
                if (ToInt(st["fan2_rpm"]) is { } f2) _prev["fan2"] = f2;

                var (key, _) = _ec.PerfModeKey(_ec.ReadPerfApmd());
                if (!string.IsNullOrEmpty(key)) _prev["perf"] = key;
            }
        }
        catch (Exception e)
        {
            _hotkeyError = "基线建立失败：" + e.Message;
        }
    }

    public void Dispose()
    {
        try { _gmc?.Dispose(); } catch { }
        try { _cbwmi?.Dispose(); } catch { }
        try { Osd.Dispose(); } catch { }
    }

    // ---------------------------------------------------------------- GMC：差分

    private void OnGmcEvent(byte[] detail)
    {
        _gmcLastDetail = string.Join(" ", detail.Take(32).Select(b => b.ToString("X2")));
        // FN+3：一次按键会连抛好几个事件，去重交给 TryCyclePerfMode
        if (detail.Length > 1 && detail[1] == DetailPerfKey) _perfPending = true;

        if (!Monitor.TryEnter(_gate))
        {
            // 上一轮阶梯还在跑（一次按键会抛好几个事件）：不排队、不阻塞，
            // 只记一笔"还要再来一轮"，让当前阶梯跑完再补。
            Volatile.Write(ref _again, 1);
            return;
        }

        var found = false;
        try
        {
            while (true)
            {
                if (_perfPending)
                {
                    _perfPending = false;
                    TryCyclePerfMode();      // 写完 APMD 之后，LadderOnce 就能读到新档位
                }
                found = LadderOnce();
                // 交换成 0 再判：读到 0 = 这一轮期间没人要求重跑 ⇒ 收工
                if (Interlocked.Exchange(ref _again, 0) == 0) break;
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }

        if (!found) ScheduleRetry();
    }

    /// <summary>
    /// FN+3（性能模式）→ **我们自己切档**。
    ///
    /// 为什么必须自己写：`_QB7` 只把 EC RAM 里的 `PERM` 加一，把 `PERM` 同步成
    /// `APMD`（唯一可读的那个）的是原厂 `ControlCenter.exe`。原厂不跑时，键按下去
    /// 没有任何可读变化 —— 这就是用户报的「FN+3 用不了」。
    ///
    /// 所以我们做原厂做的那一步：读当前 APMD → 翻成档位键 → 按**界面顺序**
    /// （office→game→beast→office，见 <see cref="PerfOrder"/>）算出下一档 → 走
    /// **和界面按钮完全相同**的写路径（arm 0x0300 → SetPerfMode）。写完 LadderOnce
    /// 立刻能读到新 APMD，OSD 与 `last_mode` 落盘都由既有的差分/回调完成，不另开一条路。
    ///
    /// ⚠️ 原厂界面在跑时**必须让位**（它自己会把 PERM 同步成 APMD），否则一次按键
    ///    两个人都写 = 连跳两档。
    /// ⚠️ EC 写入总闸关着 / 非管理员 → 如实弹一条失败提示，绝不假装切了。
    /// </summary>
    private void TryCyclePerfMode()
    {
        // 同一次按键会抛 3~4 个事件（DSDT 在轮询里 Notify），只认第一个。
        // ⚠️ 这里**只判不写**：时间戳要在整轮结束（finally）才刷新。写在判断处的话，
        //    这一轮的后续事件只要晚到 800 ms 以上就会再过一次门 —— 而一轮里
        //    arm + SetPerfMode(0.45 s 节流×2) + FollowPowerPlan(powercfg 子进程) 轻松过 1 秒，
        //    于是同一次按键翻两次档：「办公 → 狂暴」连跳两档（2026-11-19 审查发现）。
        lock (_dedupeLock)
        {
            if ((DateTime.UtcNow - _lastPerfCycle).TotalMilliseconds < 800) return;
        }

        try { CyclePerfModeCore(); }
        finally { lock (_dedupeLock) _lastPerfCycle = DateTime.UtcNow; }
    }

    private void CyclePerfModeCore()
    {
        if (_vendorUiRunning())
        {
            // 原厂界面负责同步，我们只等差分读到结果（读不到就是它也没在同步）
            return;
        }

        if (!_ec.WritesAllowed)
        {
            ShowOsd("perf", "性能模式", "EC 写入已关闭");
            return;
        }
        if (!_ec.IsAdmin())
        {
            ShowOsd("perf", "性能模式", "需要管理员权限");
            return;
        }

        try
        {
            var apmd = _ec.ReadPerfApmd();            // 0=beast 1=game 2=office
            // 先翻成键，再按键的顺序走 —— 直接用 APMD 数值递增会倒着切（见 PerfOrder）
            var curKey = _ec.PerfModeKey(apmd).Key;
            var nextKey = NextPerfKey(curKey);
            // SetPerfMode 吃的是界面索引 1/2/3（1=office 2=game 3=beast），不是 APMD
            var index = nextKey switch { "office" => 1, "game" => 2, _ => 3 };

            _ec.Arm((ushort)_ec.Spec.Sub("perf"));
            var r = _ec.SetPerfMode(index);
            if (r["ok"]?.GetValue<bool>() != true)
            {
                ShowOsd("perf", "性能模式", "切换失败：" + (r["error"]?.GetValue<string>() ?? "未知原因"));
                return;
            }

            // 与界面按钮同口径：EC 切成功之后才跟电源计划（失败就不动系统计划，
            // 免得出现"计划是新档、EC 还是旧档"）。已是该计划则不发 —— 每次
            // setactive 都会让 Windows 整套重放一遍方案。
            FollowPowerPlan(nextKey);
        }
        catch (WriteDeniedException e)
        {
            ShowOsd("perf", "性能模式", "切换被拒绝：" + e.Message);
        }
        catch (Exception e)
        {
            ShowOsd("perf", "性能模式", "切换失败：" + e.Message);
        }
    }

    /// <summary>
    /// 下一档（环形）。认不出当前档位（APMD 不是有效值 / model.json 缺 perf.modes）
    /// 时从最弱的办公开始，不按数值瞎跳。
    /// </summary>
    private static string NextPerfKey(string? current)
    {
        var i = current is null ? PerfOrder.Length - 1 : Array.IndexOf(PerfOrder, current);
        if (i < 0) i = PerfOrder.Length - 1;      // 未知键 = 当作"在最后一档"，下一档从头发起
        return PerfOrder[(i + 1) % PerfOrder.Length];
    }

    /// <summary>
    /// 阶梯式差分：0 级读 0x0200（背光 / 转速）→ 1 级读 0x0300（APMD）。
    /// 返回 true = 本轮已经弹过提示（不用再补读）。
    /// </summary>
    private bool LadderOnce()
    {
        if (_prev.Count == 0) return false;      // 基线还没建好

        // ---- 0 级：0x0200 ----
        var rawDiff = new JsonObject();
        try
        {
            var st = _ec.ReadStatus();
            rawDiff = DiffRaw(st);
            var cur = new Dictionary<string, object?>();
            if (ToInt(st["backlight_level"]) is { } bl) cur["backlight"] = bl;
            if (ToInt(st["fan1_rpm"]) is { } f1) cur["fan1"] = f1;
            if (ToInt(st["fan2_rpm"]) is { } f2) cur["fan2"] = f2;
            if (cur.Count > 0 && Fire(cur, "event", rawDiff)) return true;
        }
        catch (Exception e)
        {
            _hotkeyError = "0x0200 补读失败：" + e.Message;
        }

        // ---- 1 级：0x0300 APMD ----
        try
        {
            var (key, _) = _ec.PerfModeKey(_ec.ReadPerfApmd());
            if (!string.IsNullOrEmpty(key))
            {
                var cur = new Dictionary<string, object?> { ["perf"] = key };
                if (Fire(cur, "event", rawDiff)) return true;
            }
        }
        catch (Exception e)
        {
            _hotkeyError = "APMD 补读失败：" + e.Message;
        }

        // 落空也要把 raw 差分记下来：FN+3 这类"改了 EC 但没改我们能读的那几个字段"
        // 的按键，唯一线索就在这里（PERM 不在 0x0300 读组里）。
        AddRecent("event", null, 0,
                  rawDiff.Count > 0
                      ? "两级全落空，但 0x0200 原始缓冲区有变化（见 raw）"
                      : "快路径两级全落空（0x0200/0x0300 都没变）→ 等现场补读",
                  rawDiff);
        return false;
    }

    /// <summary>
    /// 拿 cur 和基线比：合并基线（有变化没变化都要合并）→ 有变化就弹**第一条**。
    ///
    /// ⚠️ 无变化也必须合并基线：风扇转速一直在 ±几十转地飘，不吸收的话会慢慢累积，
    ///    某次按 FN 时突然越过阈值，平白弹一个「风扇」出来。
    /// ⚠️ 一次事件只弹一条（背光优先于性能模式）：逐条弹会连闪两次。
    /// </summary>
    private bool Fire(Dictionary<string, object?> cur, string source, JsonObject? rawDiff = null)
    {
        var changed = new Dictionary<string, (object? Old, object? New)>();
        var noise = 0;
        foreach (var (key, value) in cur)
        {
            if (value is null) continue;
            // 只有"真的变了"才算；风扇/温度这类天然在动的字段只记 noise 计数，
            // 不进 changed（进 recent 会把真正有用的记录挤掉）
            if (!_prev.TryGetValue(key, out var old) || Equals(old, value)) continue;
            if (key is "fan1" or "fan2" or "cpu_t") { noise++; continue; }
            changed[key] = (old, value);
        }

        // 合并基线（先合并再弹：弹窗失败也不该让基线停在旧值）
        foreach (var (key, value) in cur)
            if (value is not null) _prev[key] = value;

        if (changed.Count > 0 || (rawDiff?.Count ?? 0) > 0)
            AddRecent(source, changed, noise, null, rawDiff);

        if (changed.ContainsKey("backlight"))
        {
            var lv = changed["backlight"].New is int i ? i : 0;
            var sub = BriSubtitle.TryGetValue(lv, out var s) ? s : $"档位 {lv}";
            ShowOsd("backlight", "键盘背光", sub);
            return true;
        }

        if (changed.ContainsKey("perf"))
        {
            var key = changed["perf"].New as string ?? "";
            var label = ModeLabel.TryGetValue(key, out var s) ? s : key;
            ShowOsd("perf", "性能模式", label);
            RememberPerfMode(key);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 用户在原厂/固件侧按 FN 换了档位，也是一次"用户选择"：
    /// 记进 `presets.perf.last_mode`，重启后才恢复到他最后按到的那个档位。
    /// （与 `/api/ec/perf` 写侧的语义一致：**只有用户动作**才更新这个键。）
    /// </summary>
    private void RememberPerfMode(string key)
    {
        try
        {
            _fan.ObservePerfMode(key);
            _model.Set(["perf", "last_mode"], JsonValue.Create(key));
        }
        catch (Exception e)
        {
            _hotkeyError = "记录性能模式失败：" + e.Message;
        }
    }

    // ---------------------------------------------------------------- 补读

    /// <summary>
    /// 阶梯落空后隔一会儿现场再看两眼（+0.6s / +1.2s）。
    ///
    /// 单飞 + 续期：新事件只把截止时间往后推，线程只有一个；新事件不该因为
    /// "上一轮补读还没退"而丢掉自己的机会。
    /// </summary>
    private void ScheduleRetry()
    {
        var until = DateTime.UtcNow.Add(RetryTail).Ticks;
        while (true)
        {
            var current = Interlocked.Read(ref _retryUntilTicks);
            if (until <= current) break;
            if (Interlocked.CompareExchange(ref _retryUntilTicks, until, current) == current) break;
        }

        if (Interlocked.CompareExchange(ref _retryBusy, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(RetryStep).ConfigureAwait(false);
                    if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref _retryUntilTicks)) break;
                    if (!Osd.Enabled) return;      // OSD 关着就不必补读了

                    if (!Monitor.TryEnter(_gate, RetryLockWaitMs)) continue;
                    bool found;
                    try { found = LadderOnce(); }
                    finally { Monitor.Exit(_gate); }
                    if (found) return;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _retryBusy, 0);
            }
        });
    }

    // ---------------------------------------------------------------- CBWMI：FN+1

    /// <summary>
    /// FN+1 飓风模式。固件自己已经在切风扇了（飓风优先级高于 UFA 钉值），
    /// 所以这里**不写 EC**，只做四件事：镜像 FMAX、曲线让位、弹 OSD、记诊断。
    ///
    /// ⚠️ 不走 1.5s 去重：那是为「按一次 FN 抛好几个 GMC 事件」准备的。
    ///    CBWMI 一次按键一个事件（FMAX 严格交替），去重反而会吞掉快速连按的中间态。
    /// </summary>
    private void OnCbwmiEvent(byte[] detail)
    {
        // 诊断：每个 CBWMI 事件都留前 32 字节（含无线/触控板那些我们不认识的）
        _lastDetail = string.Join(" ", detail.Take(32).Select(b => b.ToString("X2")));
        if (detail.Length <= FanFmaxOffset || detail[0] != FanEvid) return;
        _fanEvents++;

        var max = detail[FanFmaxOffset] != 0;
        var prev = FanHwMax;
        _fanHwMax = max;
        _fanHwMaxKnown = true;

        // 立刻执行飓风：把转速钉到满转（退出时恢复）。幂等，见 FanCurveEngine.ApplyHwMax。
        // ⚠️ 不再只翻 `YieldHw` 标志位 —— 曲线没在跑（手动 / 已自停）时那样等于没反应，
        //    而且 2026-10-20 实测固件 max 经常被 UFA 压掉，不能赌它。
        try { _fan.ApplyHwMax(max); } catch { }

        AddRecent("cbwmi", new Dictionary<string, (object?, object?)>
        {
            ["fan_hw_max"] = (prev, max),
        }, 0, "FN+1 → " + (max ? "最大（飓风）" : "自动"));

        if (prev != max)
            Osd.Show("fan", "风扇", max ? "最大（飓风）" : "自动");
    }

    // ---------------------------------------------------------------- OSD

    /// <summary>
    /// 弹一条 OSD。同一内容 1.5 秒内不重复（按一次 FN 会抛好几个 GMC 事件）。
    /// 返回 false = 没弹（osd 关着 / 窗口起不来 / 被去重）。
    /// </summary>
    public bool ShowOsd(string kind, string title, string subtitle)
    {
        if (!Osd.Enabled) return false;

        var key = $"{title}\u0001{subtitle}";
        lock (_dedupeLock)
        {
            var now = DateTime.UtcNow;
            if (key == _lastKey && (now - _lastAt).TotalSeconds < 1.5) return false;
            _lastKey = key;
            _lastAt = now;
        }
        return Osd.Show(kind, title, subtitle);
    }

    // ---------------------------------------------------------------- 诊断

    private void AddRecent(string source, Dictionary<string, (object? Old, object? New)>? changed,
                           int noise, string? note, JsonObject? raw = null)
    {
        var obj = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["src"] = source,
        };
        if (changed is { Count: > 0 })
        {
            var c = new JsonObject();
            foreach (var (key, (oldV, newV)) in changed)
                c[key] = new JsonArray(ToNode(oldV), ToNode(newV));
            obj["changed"] = c;
        }
        if (noise > 0) obj["noise"] = noise;
        // 事件自带的那几个字节：FN+2 和 FN+3 如果不同，就能直接认键
        if (source == "event" && _gmcLastDetail is not null) obj["detail"] = _gmcLastDetail;
        if (raw is { Count: > 0 }) obj["raw"] = raw.DeepClone();
        if (note is not null) obj["note"] = note;

        lock (_recentLock)
        {
            _recent.Add(obj);
            if (_recent.Count > 30) _recent.RemoveRange(0, _recent.Count - 30);
        }
    }

    /// <summary>最近几次 FN 事件的明细（只留最近 14 条，更早的直接丢）。</summary>
    public JsonArray Recent(int take = 14)
    {
        var arr = new JsonArray();
        lock (_recentLock)
        {
            foreach (var item in _recent.Skip(Math.Max(0, _recent.Count - take)))
                arr.Add(item.DeepClone());
        }
        return arr;
    }

    // ---------------------------------------------------------------- 状态

    /// <summary>`/api/osd.hotkey`：GMC 监听器状态。</summary>
    public JsonObject HotkeyState() => new()
    {
        ["available"] = _gmc?.Available ?? false,
        ["running"] = _gmc?.Running ?? false,
        ["count"] = _gmc?.Count ?? 0,
        ["last"] = _gmc?.LastAt,
        ["last_detail"] = _gmcLastDetail,
        ["error"] = _gmc?.LastError ?? _hotkeyError,
    };

    /// <summary>`/api/osd.cbwmi`：WMI2（FN+1）监听器状态。</summary>
    public JsonObject CbwmiState() => new()
    {
        ["available"] = _cbwmi?.Available ?? false,
        ["running"] = _cbwmi?.Running ?? false,
        ["count"] = _cbwmi?.Count ?? 0,
        ["fan_events"] = _fanEvents,
        ["last"] = _cbwmi?.LastAt,
        ["last_detail"] = _lastDetail,
        ["error"] = _cbwmi?.LastError ?? _cbwmiError,
    };

    private long _fanEvents;
    private string? _lastDetail;

    // ---------------------------------------------------------------- 原始缓冲区差分

    /// <summary>
    /// 把 0x0200 的**整段原始缓冲区**和上一次比，列出变化的偏移。
    ///
    /// 为什么需要这个：`ReadStatus()` 只映射了 cpu_temp / gpu_temp / fan1 / fan2 /
    /// backlight 五个字段，**其余字节没人看**。FN+3 改的 PERM 就藏在 EC RAM 里，
    /// 0x0300 读组只暴露 APMD —— 所以"两级全落空"不等于"什么都没发生"。
    /// 先把变化摆出来，才能判断有没有可用的按键识别信号。
    ///
    /// 首次调用只建立基线（返回空）；只记前 12 个偏移，避免连按刷屏。
    /// </summary>
    private JsonObject DiffRaw(JsonNode? status)
    {
        var diff = new JsonObject();
        if (status?["raw"] is not JsonValue v || !v.TryGetValue<string>(out var hex))
            return diff;

        var now = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => Convert.ToByte(x, 16)).ToArray();
        if (_prevRaw.Length == now.Length && now.Length > 0)
        {
            for (var i = 0; i < now.Length && diff.Count < 12; i++)
            {
                if (_prevRaw[i] == now[i]) continue;
                // 已映射的字段（温度/转速/背光）每秒都在小幅度变，排掉才不会把
                // 真正要找的"未映射字节"淹掉
                if (IsMappedOffset(i)) continue;
                diff[$"0x{i:X2}"] = new JsonArray($"0x{_prevRaw[i]:X2}", $"0x{now[i]:X2}");
            }
        }
        _prevRaw = now;
        return diff;
    }

    /// <summary>切档后把 Windows 电源计划切到该档位配置的那一个（找不到/已是它则什么都不做）。</summary>
    private void FollowPowerPlan(string key)
    {
        try
        {
            var want = _model.Get("perf", "modes", key, "plan")?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(want)) return;

            var pd = PowerPlans.List();
            var active = pd["active"]?.GetValue<string>();
            if (pd["plans"] is not JsonArray plans) return;

            foreach (var p in plans)
            {
                var name = p?["name"]?.GetValue<string>()?.Trim();
                if (!string.Equals(name, want, StringComparison.OrdinalIgnoreCase)) continue;
                var guid = p?["guid"]?.GetValue<string>();
                if (string.Equals(guid, active, StringComparison.OrdinalIgnoreCase)) return;
                if (!string.IsNullOrEmpty(guid)) PowerPlans.SetActive(guid);
                return;
            }
        }
        catch
        {
            // 电源计划跟不动不影响 EC 已切过去的档位，静默即可
        }
    }

    /// <summary>这个偏移是不是已被 layout 映射（那些字段本来就在差分里管）。</summary>
    private bool IsMappedOffset(int offset)
    {
        foreach (var (_, (start, length)) in _ec.Spec.Layout)
            if (offset >= start && offset < start + Math.Max(1, length)) return true;
        return false;
    }

    // ---------------------------------------------------------------- 小工具

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        bool b => JsonValue.Create(b),
        string s => JsonValue.Create(s),
        uint u => JsonValue.Create(u),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>
    /// EC 读数是 `uint`，也可能是 `int/double/string`（不同来源）。逐个类型试，
    /// **不要**只试 double —— `JsonValue.TryGetValue<double>()` 不跨数值类型转换。
    /// </summary>
    private static int? ToInt(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<uint>(out var u)) return (int)u;
        if (v.TryGetValue<long>(out var l)) return (int)l;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }
}
