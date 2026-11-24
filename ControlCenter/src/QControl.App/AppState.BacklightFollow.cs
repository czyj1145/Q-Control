using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Model;

namespace QControl.App;

/// <summary>
/// AppState 分片：**键盘背光跟着系统模式走**（常驻行为，没有总闸）。
///
/// 为什么要有这个功能：EC 的 `0x0100` 只吃"当前这一帧"的灯效数据，它自己不会按性能档换色
/// —— 原厂控制中心也没做（它的灯效只在进灯光页 / 点灯效模块按钮时才下发），
/// 于是"切到游戏档键盘还是办公那个色"是原厂的老毛病。做法和对手（CCLite）一致：
/// 每个档位记一份档案，切档时把那一份**重放**一次。原厂有四套：
/// 办公 / 游戏 / 狂暴，外加 `custom`（自定义）—— 打开「自定义接管」后不管系统在哪一档，
/// 重放的都是第四套（照搬 CCLite 的 `CustomOverride` 语义）。
///
/// ⚠️ 这里**没有** `follow_mode` 开关：它和「自定义接管」作用冲突（关掉开关第四套就不存在了），
/// 2026-11-18 已从界面与配置里撤掉，按模式重放是常驻的。
/// ⚠️ 切到 Custom 时**绝不拿当前灯色覆盖 Custom 档案**。原厂 `LEDModuleControl()` 的做法是
/// 读`新选中`模块自己的存档（`GMRegisty.GetRegistryStr(get_led_policy())`）写进 EC；
/// 出厂 `LED_Custom = "1100FFFF"` = 常亮 / 50% / 白。只有从未建过 Custom 档案时才补这份原厂默认值。
///
/// 三条不能省的规矩：
///   ① **首帧补一次档位档案**：进程启动后的第一拍，档位没变过，但 EC 里挂的是**上电默认灯色**
///      （广达模具开机是红色），不是任何一档的档案 —— 2026-11-21 用户报"一开机键盘是红的，
///      不是我狂暴档的青色"，根因就是这条：不补，键盘就一直红着直到手动切一次档。
///      ⚠️ 首拍读到 EC 亮度**不可信**（可能是一帧全零），所以首拍只挂起、下一拍拿可信读数再补；
///      真关着灯（连续几拍都是 0）就不点亮。
///   ② **缺档不凑**：这一档没档案就什么都不写、只记一行日志。绝不拿别的档顶上 ——
///      否则"我只调过办公档"会变成"三个档都变成办公配色"。
///   ③ **不占采样线程**：三区重放实测 ~1.4 秒，采样线程每 500ms 一拍，同步写会把
///      采样和风扇曲线一起卡住。判断在采样拍里做，写入丢后台单飞（同自动熄灯的做法）。
///
/// 与自动熄灯的配合：重放成功后调 <see cref="NotifyBacklightUserWrite"/> 作废旧状态机，
/// 否则"熄灯期间切了个模式"会让它过一会儿拿旧档位把灯又写回去。
/// </summary>
public sealed partial class AppState
{
    /// <summary>档位基线 / 重放状态的锁（采样线程与 HTTP 线程都会碰）。</summary>
    private readonly object _blFollowLock = new();

    /// <summary>最近一次观测到的系统模式（null = 还没建基线）。</summary>
    private string? _blModeKey;

    /// <summary>最近一次**成功**重放的档位（界面据此知道"现在键盘上是哪一档的配色"）。</summary>
    private string? _blAppliedKey;

    /// <summary>最近一次重放失败/跳过的原因（界面显示一行，别静默）。</summary>
    private string? _blApplyNote;

    /// <summary>重放单飞：同一时刻只有一个后台写入。</summary>
    private int _blApplyBusy;

    /// <summary>"这一档没有档案"只记一次日志（否则每拍刷一行）。</summary>
    private string? _blMissingLogged;

    /// <summary>
    /// 挂起中的「启动恢复」目标档（受 <see cref="_blFollowLock"/> 保护；null = 没有挂起的）。
    ///
    /// 为什么要挂起：进程启动第一拍读到的 EC 亮度**不可信** —— EC 刚上电时 `0x0100` 可能回
    /// 一帧全零（2026-11-19 就踩过"首帧亮度是 0 把办公档写成 level 0"）。用那一拍的 0 判
    /// 「用户关着灯」会把开机恢复直接挡掉（2026-11-21 重启后仍红着，就是这条），
    /// 所以首帧读到 0 / 读不到时先记下来，等下一拍拿到可信读数再决定。
    /// </summary>
    private string? _blBootPending;

    /// <summary>启动恢复已经等了几拍（见 <see cref="_blBootPending"/>）。</summary>
    private int _blBootWaits;

    /// <summary>启动恢复最多等几拍再下结论（插电 500ms/拍 ≈ 2s；电池 2s/拍 ≈ 8s）。</summary>
    private const int BootWaitTicks = 4;

    /// <summary>
    /// 「刚开机」的判定窗口（自系统启动以来的毫秒数）。窗口内的启动恢复**一定写**，
    /// 即使 EC 亮度读到 0：EC 上电时 `0x0100` 的颜色是上电默认值（广达模具是红的），
    /// 亮度也不是用户关灯留下的，而是 EC 自己回的一帧零 —— 2026-11-21 重启后仍红着就是这个。
    /// 窗口外（进程重启 / 托盘重启，EC 没断过电）读到 0 才是用户真的关着灯。
    /// </summary>
    private const long FreshBootMs = 180_000;

    /// <summary>
    /// 「墙钟 − **醒着的时间**」的基线。睡眠期间醒着的时间不累加、墙钟照走，
    /// 所以醒来第一拍这个差值会跳变 —— 用它判睡眠唤醒，不用挂 SystemEvents 的 STA 线程。
    ///
    /// ⚠️ 这里**不能**用 <see cref="Environment.TickCount64"/>（2026-11-21 审查发现）：
    ///    .NET 10 的 TickCount64 就是 Win32 `GetTickCount64`，**含**睡眠 / 休眠时间
    ///    —— MSDN 原话是"要算上睡眠的时间请用 GetTickCount/GetTickCount64"，
    ///    而"只算醒着的时间"的是 `QueryUnbiasedInterruptTime`（.NET 11 才把
    ///    TickCount64 换成后者的口径）。拿含睡眠的钟算差值，睡多久差值都不变 ⇒
    ///    跳变永远等不到 ⇒ 整条「唤醒后把灯补回去」的路径是死的，
    ///    用户看到的还是"无操作久了键盘变红"。见 <see cref="AwakeMs"/>。
    /// </summary>
    private long? _blClockSkew;

    /// <summary>
    /// 唤醒后「这档配色还没真正补进 EC」的待办（1 = 待办）。
    ///
    /// 为什么要有：唤醒补写只有 3 秒 / 10 秒两次，而**两次都可能被「自动熄灭」挡掉** ——
    /// 睡眠前是锁屏态（合盖睡眠就是这样），醒来还锁着，两次全跳过。用户要是超过 10 秒才解锁，
    /// 之后没有任何路径补色，键盘停在 EC 上电默认色（广达模具是红的）直到下次切档。
    /// 2026-11-21 实测就是这个细节：那次 4 秒内解锁才刚好赶上第二次。
    ///
    /// 规矩：**只有真正写完 EC 的那次重放**才清它（见 ApplyBacklightForMode 的成功分支）；
    /// 解锁恢复背光时若还挂着，顺手重放一次本档档案
    /// （见 AppState.BacklightAutoOff.cs 的 RestoreBacklightAsync）。
    ///
    /// ⚠️ **有寿命**（2026-11-21 补）：待办不是"永久欠一笔"。灯本来就关着就睡的用户
    /// （或醒来一直锁屏没解锁），唤醒那两次必然被挡、待办也永远清不掉 —— 之后任何一次
    /// 自动熄灯→恢复都会莫名其妙多写一次 EC（1.4 秒）并打一行"唤醒后解锁"，日志骗人。
    /// 所以超过 <see cref="ResumePendingLifeMs"/> 就作废，见 <see cref="ResumeReplayPending"/>。
    /// </summary>
    private int _blResumeReplayPending;

    /// <summary>挂上待办那一刻（TickCount64）。</summary>
    private long _blResumeReplayAtMs;

    /// <summary>待办最多活 10 分钟：再往后的"解锁"已经跟那次睡眠无关了。</summary>
    private const long ResumePendingLifeMs = 10 * 60_000;

    /// <summary>`QueryUnbiasedInterruptTime` 不可用时只抱怨一次（不让每拍刷一行）。</summary>
    private static int _awakeClockWarned;

    /// <summary>差值跳多少毫秒算一次睡眠唤醒（普通校时是毫秒级，不会误判）。</summary>
    private const long ResumeJumpMs = 5_000;

    /// <summary>「启动恢复」这个由头（日志 + 开机核对判定共用一处，别再各写一份字面量）。</summary>
    private const string BootReplayWhy = "启动恢复";

    /// <summary>
    /// 开机后多久再核一次灯效。原厂 <c>ControlCenterDaemon</c> 启动比我们晚，
    /// 它 <c>SetCurrentLED()</c> 会按注册表把我们刚写的颜色盖掉；这一拍等它写完再补一次。
    /// </summary>
    private const int BootVerifyMs = 25_000;

    /// <summary>
    /// 开机核对被"已经有一次重放在飞"挡掉时，隔多久再试（见 <see cref="ScheduleBootLedVerify"/>）。
    /// </summary>
    private const int BootVerifyRetryMs = 5_000;

    /// <summary>
    /// 上一拍读到的 EC 亮度（受 <see cref="_blFollowLock"/> 保护）。**每拍都更新**，
    /// 不管这一拍采不采信 —— 判定必须基于"相对上一拍变没变"，不能基于"和档案等不等"。
    /// </summary>
    private int? _prevEcLevel;

    /// <summary>
    /// 最近一次**我们自己**写 EC 背光的时刻（TickCount64，null = 从没写过）。
    /// 写完后的 <see cref="BacklightLevelWatch.QuietMs"/> 毫秒内，EC 上报的亮度变化不认，
    /// 那可能是切档/接管留下的旧档值，不是用户按了 FN 亮度键。
    /// </summary>
    private long? _blLastEcWriteAt;

    /// <summary>标记"这一刻我们自己写了 EC 背光"（切档重放、背光页下发、自动熄灯恢复都算）。</summary>
    internal void MarkBacklightEcWrite()
    {
        lock (_blFollowLock) _blLastEcWriteAt = Environment.TickCount64;
    }

    /// <summary>
    /// `presets.backlight.custom_override`：原厂那套「自定义接管」—— 打开后不管系统在哪一档，
    /// 切档重放的一律是第四套 `custom`。**缺省 false**，绝不悄悄接管用户的键盘灯。
    /// </summary>
    public bool BacklightCustomOverride
        => Truthy(_model.Get(JsonValue.Create(false), "backlight", "custom_override"));

    /// <summary>
    /// 当前系统模式键（优先用快照里的 APMD，没读到就退回基线）。
    /// 供 HTTP 侧"这次写入算哪一档的改动"用。
    /// </summary>
    internal string? CurrentPerfKey()
    {
        // 这里是**系统性能档**，不是灯效档案键：`custom` 不能被当成当前档
        var raw = Str((_cache?["perf"] as JsonObject)?["mode"]);
        var live = ModeBundle.IsKey(raw) ? raw : null;
        if (live is not null) return live;
        lock (_blFollowLock) return _blModeKey;
    }

    /// <summary>EC 里的键盘亮度档位（读最近一拍快照；读不到返回 null，绝不猜）。</summary>
    internal int? EcBacklightLevelNow() => CurrentBacklightLevel(_cache);

    /// <summary>
    /// 采样线程 / 热键线程每拍喂一次 EC 里的档位（与 `FanCurveEngine.ObservePerfMode` 同源）。
    ///
    /// **必须由后端观测而不是只在界面按钮里做**：模式可以被外部改（原厂软件、Fn 键直写 APMD），
    /// 只有每秒读真值，联动才能对那些改动也生效。
    /// </summary>
    /// <param name="ecLevel">
    /// 同一拍读到的 EC 键盘亮度（`ec.backlight_level`），用于"每档各记一份亮度"。
    /// 只在**档位没变**的拍子里采信：切档那一拍 EC 里还是旧档的亮度，拿它记新档就会串档。
    /// </param>
    internal void ObserveBacklightMode(string? perfKey, int? ecLevel = null)
    {
        // 睡眠唤醒检测与档位无关，每拍都要跑（它可能触发一次补写）
        DetectResume();

        // 观测的是**系统性能档**（只有三档），custom 只能由 ReplayKey 产生
        if (!ModeBundle.IsKey(perfKey)) return;
        var key = perfKey!;

        bool changed;
        bool first;
        lock (_blFollowLock)
        {
            changed = _blModeKey != key;
            first = _blModeKey is null;
            _blModeKey = key;
        }

        if (first)
        {
            // ① 启动后第一拍：EC 里是上电默认灯色（广达开机红），不是任何一档的档案 ⇒ 要补一次。
            var bootTarget = BacklightModes.ReplayKey(key, BacklightCustomOverride);
            if (bootTarget is null) return;

            if (Environment.TickCount64 >= FreshBootMs)
            {
                // 不是刚开机（托盘重启 / 覆盖安装，EC 没断过电）：首拍亮度可能还是旧的，
                // 先挂起一拍，等可信读数判断用户是不是自己关着灯（见 TryBootApply）。
                lock (_blFollowLock) { _blBootPending = bootTarget; _blBootWaits = 0; }
                Console.WriteLine($"[light] 启动恢复：首拍 EC 亮度 = "
                                  + $"{(ecLevel?.ToString() ?? "读不到")}，下一拍确认后补"
                                  + $"「{BacklightModes.Label(bootTarget)}」的档案");
                return;
            }

            // 刚开机：EC 上电后首拍亮度不可信（可能是一帧 0），但灯一定是亮着上电默认色，
            // 所以**立刻写**，不让用户多看一眼红色。
            ScheduleBacklightApply(bootTarget, BootReplayWhy);
            return;
        }

        if (!changed)
        {
            // 挂起的启动恢复先处理（它可能会写 EC，写完进静默期，这一拍的亮度就不该再采信）
            TryBootApply(ecLevel);
            // 档位没变 ⇒ 这一拍的 EC 亮度可以放心当成"这一档现在的亮度"去记。
            ObserveBacklightLevel(key, ecLevel);
            return;
        }

        // 档位变了 ⇒ 正常切档路径接管，挂起的启动恢复作废（不然会多写一次旧档）
        lock (_blFollowLock) { _blBootPending = null; _blBootWaits = 0; }

        // 原厂的「自定义接管」优先级最高：系统档变了，但重放的还是第四套 custom
        var target = BacklightModes.ReplayKey(key, BacklightCustomOverride);
        if (target is not null) ScheduleBacklightApply(target);
    }

    /// <summary>
    /// 处理挂起的「启动恢复」：等 EC 亮度给出**可信**读数再决定点不点亮。
    /// <list type="bullet">
    /// <item>读到 &gt;0：灯亮着，EC 里却是上电默认色 ⇒ 重放目标档档案。</item>
    /// <item>连续 <see cref="BootWaitTicks"/> 拍都读到 0 且**不是刚开机**：关机前用户用 FN 关着灯
    ///   ⇒ 不点亮，放弃。</item>
    /// <item>刚开机（<see cref="FreshBootMs"/> 窗口内）：不管读到什么都补一次 —— 那一帧 0 是
    ///   EC 上电还没就绪回的，不是用户关灯。</item>
    /// <item>一直读不到（null）且等够拍数：EC 没就绪 / 不可用，还是试一次 ——
    ///   否则键盘只能一直亮着上电默认的红。写不成的真实原因由 ApplyBacklightForMode 自己报。</item>
    /// </list>
    /// </summary>
    private void TryBootApply(int? ecLevel)
    {
        string? target = null;
        var giveUp = false;

        lock (_blFollowLock)
        {
            if (_blBootPending is null) return;
            _blBootWaits++;

            // 开机早期：EC 的亮度读数不可用（0 / 读不到都是它还没就绪），别拿它当"关着灯"
            var freshBoot = Environment.TickCount64 < FreshBootMs;
            var ready = freshBoot
                        || ecLevel is > 0
                        || (ecLevel is null && _blBootWaits >= BootWaitTicks);
            if (ready)
            {
                target = _blBootPending;
                _blBootPending = null;
            }
            else if (_blBootWaits >= BootWaitTicks)
            {
                _blBootPending = null;
                giveUp = true;
            }
            else
            {
                return;     // 再等一拍（首拍读数可能是 EC 还没就绪的 0）
            }
        }

        if (giveUp)
        {
            Console.WriteLine($"[light] 启动恢复：EC 亮度连续 {BootWaitTicks} 拍都是 0"
                              + "（关机前灯是关着的）⇒ 不点亮，保持关灯");
            return;
        }

        if (target is not null) ScheduleBacklightApply(target, BootReplayWhy);
    }

    /// <summary>
    /// 睡眠唤醒检测 + 唤醒后补写灯效（2026-11-21 用户报「无操作久了键盘变红」的直接补丁）。
    ///
    /// 为什么需要：S3 睡眠唤醒时 EC 会被重新初始化，`0x0100` 的灯效回到**上电默认色**
    /// （广达模具是红的）。而我们的按档重放只在「档位变化」时触发 —— 档位没变，就没人把
    /// 青色写回去，键盘一直红着，直到下次切档或重启程序。用户看到的就是"有时候红有时候青"。
    ///
    /// 判定用「墙钟 − 醒着的时间」的跳变（见 <see cref="AwakeMs"/>）：睡眠期间那个钟不累加、
    /// 墙钟照走，醒来第一拍差值就跳了 <see cref="ResumeJumpMs"/> 以上。
    /// 比挂 SystemEvents 便宜，也不占 STA 线程。
    ///
    /// 为什么补两次（3 秒 + 10 秒）：EC 唤醒后的初始化可能比我们晚，第一次写进去有可能被它
    /// 后面的初始化顶掉。两次都走单飞，同档不会重复写（第二次若已一致也不闪）。
    ///
    /// ⚠️ 两次都可能**没写进去**（锁屏自动熄灭会把它们挡掉）。所以检测到唤醒时先挂一个待办，
    ///    只有真写完 EC 才清 —— 解锁晚了还能在解锁那一刻补上，见 <see cref="_blResumeReplayPending"/>。
    ///
    /// ⚠️ 唤醒补写**故意不看 EC 亮度**（2026-11-22 用户定，别当成 bug 重开）：
    ///    开机恢复那条路会"读到 0 就不点亮"，这里不照抄 —— S3 醒来 EC 一般已经把灯恢复成
    ///    上电默认色（连亮度一起，用户报的"变红"就是亮着的），此时再去判 0 只会让
    ///    "EC 还没就绪回了帧 0" 变成"红着不补"。优先级是**别红着**，不是别被点亮。
    ///    真关灯睡觉的情况由 EC 自己负责（它本来就点了），我们不额外背这一锅。
    /// </summary>
    private void DetectResume()
    {
        var skew = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - AwakeMs();
        long? prev;
        lock (_blFollowLock)
        {
            prev = _blClockSkew;
            _blClockSkew = skew;
        }
        if (prev is not { } p || Math.Abs(skew - p) < ResumeJumpMs) return;

        Console.WriteLine("[light] 检测到睡眠唤醒：EC 会把灯效重置成上电默认色，稍后补回当前档的档案");
        // 先记时刻再置位：读的一方看到 1 时，时刻一定已经写好了
        Interlocked.Exchange(ref _blResumeReplayAtMs, Environment.TickCount64);
        Interlocked.Exchange(ref _blResumeReplayPending, 1);

        _ = Task.Run(async () =>
        {
            foreach (var delay in new[] { 3_000, 7_000 })
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (BacklightModes.ReplayKey(CurrentPerfKey(), BacklightCustomOverride) is { } t)
                        ScheduleBacklightApply(t, "唤醒恢复");
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("[light] 唤醒恢复异常：" + e.Message);
                }
            }
        });
    }

    /// <summary>
    /// 唤醒补写的待办还挂着吗（且没过期）。
    ///
    /// 过期就地作废：一次睡眠的补色只跟"刚醒来的那十几分钟"有关，过了这个窗口再补，
    /// 只是白写一次 EC 加一行误导人的日志。见 <see cref="_blResumeReplayPending"/>。
    /// </summary>
    private bool ResumeReplayPending()
    {
        if (Volatile.Read(ref _blResumeReplayPending) == 0) return false;

        var at = Interlocked.Read(ref _blResumeReplayAtMs);
        if (Environment.TickCount64 - at <= ResumePendingLifeMs) return true;

        if (Interlocked.Exchange(ref _blResumeReplayPending, 0) == 1)
            Console.WriteLine("[light] 唤醒补写的待办已过期（超过 10 分钟），作废：解锁时不再补这一次");
        return false;
    }

    /// <summary>
    /// 「系统醒着多久了」的毫秒数 —— **不含**睡眠 / 休眠。
    ///
    /// 用它而不是 <see cref="Environment.TickCount64"/>：后者在 .NET 10 上就是
    /// `GetTickCount64`，**含**睡眠时间（`QueryUnbiasedInterruptTime` 的文档写得很明白：
    /// "要算上睡眠/休眠的时间请用 GetTickCount/GetTickCount64"），拿它做唤醒检测永远
    /// 等不到跳变。.NET 11 才把 TickCount64 换成不带偏的口径，这个工程还停在 net10。
    ///
    /// 失败时退回 TickCount64 并抱怨一次：检测会退化（睡眠前后差值不变），但其它逻辑
    /// 不依赖它，不至于把灯写坏。
    /// </summary>
    private static long AwakeMs()
    {
        if (QueryUnbiasedInterruptTime(out var ticks))
            return (long)(ticks / 10_000UL);       // 100ns → ms

        if (Interlocked.Exchange(ref _awakeClockWarned, 1) == 0)
            Console.Error.WriteLine("[light] QueryUnbiasedInterruptTime 不可用："
                                    + "睡眠唤醒检测这次运行内失效（其它背光逻辑不受影响）");
        return Environment.TickCount64;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    /// <summary>
    /// 把 **EC 里的键盘亮度**记进「键盘上正在显示的那一档」的档案（每档各记一份亮度）。
    ///
    /// 为什么需要（2026-11-19 用户报"FN+3 切档后亮度自己变"的直接补丁）：
    /// FN+F1/F2 调键盘亮度是 EC 固件自己处理的，**不经过我们任何 HTTP 路径** ⇒ 档案里的
    /// `level` 永远停在旧值。用户在游戏档用 FN 调到 50%、切到办公再切回游戏，亮度又被档案里
    /// 的旧值顶回去 —— 看起来就是"亮度自己变"。既然亮度是每档各记一份，那"这一档最后是
    /// 多亮"就必须以 EC 真值为准，而不能只认我们自己的写入。
    ///
    /// 三条不做的事：
    ///   ① **不写 EC**（写完再写回去就是自己跟自己打架）；
    ///   ② **不改颜色/灯效**（只动 `level` 一个键）；
    ///   ③ 自动熄灯期间不记 —— 那时候的 0 是"临时关灯"，不是这一档的偏好。
    /// </summary>
    private void ObserveBacklightLevel(string perfKey, int? level)
    {
        if (level is not { } lv || lv is < 0 or > 2) return;

        // ① **先更新这一拍的观测基线，再判"是不是外部改的"**（2026-11-19 13:41 事故）。
        //    老实现拿「这一档档案的 level ≠ EC level」就写盘：开自定义接管时档案是 50%，
        //    EC 里还挂着刚才狂暴档的 100%，采样拍就把 100% 当成用户意图写进自定义档案，
        //    于是"切自定义 ↔ 狂暴"亮度来回跳，而且一旦写坏就一直坏下去。
        //    真值来自 FN 亮度键（EC 固件直接改），它一定表现为**相对上一拍跳变**；
        //    切档后的残留值是"一直没变"的，不该记。
        int? prev;
        long? sinceWrite;
        lock (_blFollowLock)
        {
            prev = _prevEcLevel;
            _prevEcLevel = lv;
            sinceWrite = _blLastEcWriteAt is { } at ? Environment.TickCount64 - at : null;
        }
        if (!BacklightLevelWatch.IsExternalChange(prev, lv, sinceWrite)) return;

        // **0 永远不记**：关灯（FN 灯键 / 自动熄灯 / 手工关）是临时动作，不是"这一档的亮度偏好"。
        // 记进去的后果就是切到这一档键盘直接黑着 —— 2026-11-19 实测踩到：首帧 EC 亮度恰好是 0，
        // 把「办公」档写成了 level 0。档位记忆只在"真的亮着"的时候才成立（同理界面的 _lastLevel）。
        if (lv == 0) return;

        var key = BacklightModes.ReplayKey(perfKey, BacklightCustomOverride);
        if (key is null) return;

        // 自动熄灯正把灯关着 / 正等着恢复：那一拍的 0 不采信
        if (_autoOffBy != 0) return;

        // 重放还在飞：这几拍 EC 里的亮度可能是"旧档的"或者"我们刚写的"，都不算用户意图
        if (Volatile.Read(ref _blApplyBusy) == 1) return;

        // 只有当键盘上显示的确实是这一档的配色时，亮度才有归属
        string? applied;
        lock (_blFollowLock) applied = _blAppliedKey;
        if (applied != key) return;

        var maxZones = BacklightZones.Sanitize(Current.KbZones);
        // ② 缺档不凑：这一档没有配色档案就不建"只有亮度的块"，否则切过去会点亮一盏没人要的灯
        if (BacklightModes.ForMode(_model.Presets, key, maxZones) is not { } profile) return;
        if (profile.Level == lv) return;

        lock (_model.SyncRoot)
        {
            if (ModeBundle.EnsureLightBlock(_model.Presets, key) is not { } light) return;
            light["level"] = lv;
            _model.Save();
        }

        Console.WriteLine($"[light] 键盘亮度被外部改成 {lv}（FN 键 / 原厂软件）："
                          + $"已记进「{BacklightModes.Label(key)}」档，切走再切回来就是这个亮度");
    }

    /// <summary>
    /// 后台重放（单飞）。期间又换档的话，跑完这一轮接着处理最新那一档 ——
    /// 用户连按两下 Fn 时不能只认第一次。
    /// </summary>
    /// <param name="why">
    /// 这次重放的由头（日志用）：切档是「切到」，进程启动补第一次是「启动恢复」。
    /// 追帧（用户连按两下 Fn）一律按「切到」记。
    /// </param>
    private void ScheduleBacklightApply(string key, string why = "切到")
    {
        if (Interlocked.Exchange(ref _blApplyBusy, 1) == 1) return;

        _ = Task.Run(() =>
        {
            string? applied = null;
            try
            {
                var target = key;
                var reason = why;
                while (true)
                {
                    ApplyBacklightForMode(target, why: reason);
                    applied = target;

                    string? next;
                    lock (_blFollowLock) next = _blModeKey;
                    // ⚠️ 追帧目标必须过 ReplayKey：接管开着时系统档是 game、该写的却是 custom。
                    //    直接拿 next 会让这一轮刚写完 custom 又马上用 game 覆盖掉（实测踩过）。
                    var want = BacklightModes.ReplayKey(next, BacklightCustomOverride);
                    if (want is null || want == target) break;
                    target = want;
                    reason = "切到";
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[light] 按模式重放灯效异常：" + e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _blApplyBusy, 0);

                // ⚠️ 收尾再对一次档位（2026-11-19）：上面 break 之后、清 busy 之前如果又换了档，
                //    那一次 ScheduleBacklightApply 会看到 busy=1 直接返回 ⇒ **没人重放**，
                //    键盘就停在上一档的配色上，直到下一次手动切档。
                //    这里补一次；apply 自己幂等，同档不会重复写。
                string? want;
                lock (_blFollowLock) want = BacklightModes.ReplayKey(_blModeKey, BacklightCustomOverride);
                // ⚠️ `applied is null` = 第一次重放就抛了（EC 写被拒等）。这时**不许**再排一次，
                //    否则会变成"失败 → 重排 → 又失败"的忙循环，每秒堆一个 Task 把日志刷爆。
                if (applied is not null && want is not null && want != applied) ScheduleBacklightApply(want);
            }
        });
    }

    /// <summary>
    /// 写键盘之外的独立灯珠（当前档案只有 Logo；`tail` 保留给将来真有尾灯的机型）。**只有机型档案声明了才写**，
    /// 输入是 `{ "logo":[r,g,b], "tail":[r,g,b] }` 里实际出现的键；没出现的键一个字节都不发。
    /// 槽位来自 `ec.kb_extra_lights`，绝不把 7/8 当键盘分区猜。
    /// </summary>
    internal void ApplyExtraLights(JsonObject? body, int level, int? effect)
    {
        var addrs = Current.ExtraLightAddrs;
        if (addrs.Count == 0 || body is null) return;

        foreach (var (name, addr) in addrs)
        {
            if (RgbOf(body[name]) is not { } rgb) continue;
            _ec.SetBacklight(level, rgb[0], rgb[1], rgb[2], addr, effect);
        }
    }

    /// <summary>从 `[r,g,b]` 读一个钳制后的颜色；手改成 300 / 字符串都不许原样下发。</summary>
    private static int[]? RgbOf(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count < 3) return null;
        var rgb = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!Bounds.TryToDouble(arr[i], out var v)) return null;
            rgb[i] = Bounds.Clamp("backlight_rgb", (int)Math.Round(v));
        }
        return rgb;
    }

    /// <summary>
    /// 把某档的灯效档案重放到 EC（同步执行，写一次就返回）。
    ///
    /// 返回值给 HTTP / 自检用：`{ok, mode, applied, level, effect, zones, note}`。
    /// `ok:false` 一定带 `note`（哪种原因写不了），绝不做"假成功"。
    /// </summary>
    internal JsonObject ApplyBacklightForMode(string? mode, string why)
    {
        if (BacklightModes.Key(mode) is not { } key)
            return Note(false, mode, "不认识的档位：" + (mode ?? "null"));

        // 门：机型能不能写 → 进程总闸 → 这台机器有没有键盘灯
        // ⚠️ 三道门被挡时也要留一行日志（2026-11-21）：它们的 Note 只进状态接口，
        //    用户在日志里查不到"为什么开机没补色"就只能靠猜。
        var dev = Current;
        if (!dev.WritesAllowed)
        {
            Console.WriteLine($"[light] {why}「{BacklightModes.Label(key)}」失败：EC 写入未开启（{dev.DenyHint()}）");
            return Note(false, key, "EC 写入未开启（" + dev.DenyHint() + "）");
        }
        if (!_ec.WritesEnabled)
        {
            Console.WriteLine($"[light] {why}「{BacklightModes.Label(key)}」失败：进程内部 EC 总闸是关的");
            return Note(false, key, "进程内部 EC 总闸是关的");
        }

        // 分区数按机型声明裁（0 = 没声明 ⇒ 不裁，与 BacklightZones 的口径一致）
        var backlight = _model.Presets["backlight"] as JsonObject;
        var profile = BacklightModes.ForMode(_model.Presets, key, BacklightZones.Sanitize(dev.KbZones));
        // 我们这边缺档时，先问原厂注册表："原厂存哪我们就存哪"（换装置/重装后不用重新调色）
        profile ??= SeedFromOem(key);
        if (profile is null)
        {
            // ② 缺档不凑：保持现状，日志只记一次。
            // ⚠️ 用 `why` 而不是写死"切到"（2026-11-21）：启动恢复 / 唤醒恢复也会走到这里，
            //    日志里全写成"切到"会让人以为用户自己按了 Fn 切档，时间线上对不上。
            if (_blMissingLogged != key)
            {
                _blMissingLogged = key;
                Console.WriteLine($"[light] {why}「{BacklightModes.Label(key)}」：这一档还没有灯效档案，"
                                  + "键盘保持现状（在背光页调一次色就会记成这一档的）");
            }
            return Note(false, key, "这一档还没有灯效档案");
        }
        _blMissingLogged = null;

        // 自动熄灯正把灯关着（锁屏 / 闲置中）⇒ **不点亮**。这条与自动熄灯那边
        // 「绝不凭空点亮一盏关着的灯」是同一条规矩：拔电自动切档是**没人看着**的时候发生的，
        // 那时候把键盘点亮是最不该干的事。用户一动键鼠，自动熄灯会自己恢复。
        if (_autoOffBy != 0)
        {
            Console.WriteLine($"[light] {why}「{BacklightModes.Label(key)}」跳过："
                              + "键盘背光正被「自动熄灭」关着，这次不点亮");
            return Note(false, key, "键盘背光正被「自动熄灭」关着，这次不点亮");
        }

        try
        {
            _ec.SetBacklightZones(profile.Level, profile.Zones, profile.Effect);
            // 写完 EC 就进静默期：接下来一两拍里读到的亮度变化是我们自己写的回响，
            // 不是用户按了 FN 亮度键（切自定义 ↔ 狂暴的跳动就是从这来的）。
            MarkBacklightEcWrite();

            // 独立灯珠（当前只有 Logo）是档案里的另一路，不混进键盘分区表；有才写（没声明机型的 EC 一个字节都不发）
            var extras = new JsonObject();
            if (profile.Logo is { } logo) extras["logo"] = new JsonArray(logo[0], logo[1], logo[2]);
            if (profile.Tail is { } tail) extras["tail"] = new JsonArray(tail[0], tail[1], tail[2]);
            ApplyExtraLights(extras, profile.Level, profile.Effect);

            // 落地"当前生效"：`presets.backlight` 的 effect/zones 语义就是"EC 里现在是这个"
            var wrote = 0;
            if (backlight is not null)
            {
                lock (_model.SyncRoot)
                {
                    wrote = BacklightModes.WriteSection(backlight, profile);
                    _model.Save();
                }
            }

            // 与自动熄灯的状态机交接：这次是我们写的，旧档位记录作废
            NotifyBacklightUserWrite();

            // 这次是真写进 EC 了 ⇒ 唤醒待办可以销账（解锁恢复那边不必再补一次）
            Interlocked.Exchange(ref _blResumeReplayPending, 0);

            lock (_blFollowLock)
            {
                _blAppliedKey = key;
                _blApplyNote = null;
            }

            Console.WriteLine($"[light] {why}「{BacklightModes.Label(key)}」：已重放该档灯效"
                              + $"（灯效 {profile.Effect} / 档位 {profile.Level} / {wrote} 个分区）");

            // 把同一套配色写进原厂 LED 注册表（原厂存哪我们就存哪）：daemon 会在我们之后启动、
            // 按那边的 `LEDModule` 重放一次（本机 = PERF = 纯红），不镜像就永远"蓝一下又变红"。
            MirrorOemLedAll(key);

            // 刚开机：daemon 可能比我们晚十秒才写，这一笔会被它盖掉 ⇒ 25 秒后再核一次
            if (why == BootReplayWhy) ScheduleBootLedVerify(key);

            return new JsonObject
            {
                ["ok"] = true,
                ["mode"] = key,
                ["applied"] = key,
                ["level"] = profile.Level,
                ["effect"] = profile.Effect,
                ["zones"] = wrote,
            };
        }
        catch (Exception e)
        {
            lock (_blFollowLock) _blApplyNote = e.Message;
            Console.Error.WriteLine($"[light] 切到「{BacklightModes.Label(key)}」档，重放灯效失败：{e.Message}");
            return Note(false, key, e.Message);
        }
    }

    /// <summary>
    /// 把某档配色同步进原厂 ControlCenter 的 LED 注册表（见 <see cref="OemLedRegistry"/>）。
    ///
    /// 原厂 daemon 的开机灯效是从那边读的：本机 <c>LEDModule=1</c>(PERF) →
    /// <c>HighPerformance=12ff0000</c> = 常亮/高亮/**纯红**。它启动比我们晚几秒，
    /// 于是"我们写青 → 它写红"，用户看到的就是"写进去又被覆盖"。
    /// 每次我们真写进 EC 之后顺手把这档的键改掉，它什么时候重放都是用户的颜色。
    ///
    /// 缺档不写（没有档案就没有配色可镜像）；写失败只记日志，不影响本次下发的成败。
    /// </summary>
    /// <param name="setModule">只有键盘上正在显示的那一档才动 <c>LEDModule</c>（否则等于对原厂谎报当前档）。</param>
    internal void MirrorOemLed(string? modeKey, bool setModule)
    {
        if (BacklightModes.Key(modeKey) is not { } key) return;

        // 取档案要持 SyncRoot：调用方可能来自 HTTP 线程，后台重放线程同时在改同一棵
        // `perf.modes`。注册表写入放在锁外（慢、且与配置无关）。
        BacklightProfile? profile;
        lock (_model.SyncRoot)
            profile = BacklightModes.ForMode(_model.Presets, key, BacklightZones.Sanitize(Current.KbZones));
        if (profile is null) return;

        OemLedRegistry.Mirror(key, profile, setModule);
    }

    /// <summary>
    /// 四套档案**全写一遍**，再把原厂 <c>LEDModule</c> 指向键盘上正在显示的那一档。
    ///
    /// 为什么要全写、不是只写当前档：原厂 daemon 是"按 `LEDModule` 选一份键重放"，而
    /// `LEDModule` 可能被原厂界面改过（用户点了原厂的游戏/办公档）。只写当前档的话，
    /// 它一启动就按另一个键重放我们**从没写过**的旧值 —— 又变回红。
    /// 四套都对齐，它指向哪一档都是用户的颜色。
    /// </summary>
    internal void MirrorOemLedAll(string? liveKey)
    {
        foreach (var k in BacklightModes.Keys) MirrorOemLed(k, setModule: false);
        OemLedRegistry.SetModule(BacklightModes.Key(liveKey));
    }

    /// <summary>
    /// 这一档在我们这儿**还没有档案**时，试从原厂注册表读回来播种（"原厂存哪我们就存哪"）。
    ///
    /// 地址表只认已有的事实：`presets.backlight.zones`（EC 里那一套 A/B/C）优先，
    /// 没有就用机型声明的分区数推不出来 ⇒ 直接放弃（不猜地址，写错会亮错分区）。
    /// 返回 null = 原厂那边也没有可用的值，调用方继续走"缺档不凑"。
    /// </summary>
    private BacklightProfile? SeedFromOem(string key)
    {
        var maxZones = BacklightZones.Sanitize(Current.KbZones);
        var extras = Current.ExtraLightAddrs;          // 独立灯珠（Logo / 尾灯）不是键盘分区

        // ① 地址表只认现有事实（`presets.backlight.zones`，含老结构的 addr＋rgb）：原厂值里只有颜色、没有地址。
        //    走 Snapshot 拿现表 = 免费获得去重 / 去掉 addr<1 / 按机型分区数收口。
        //    ⚠️ 再排掉独立灯珠地址：这张表被探针撑成 `5/4/3/8/7` 时，降序后"键盘 1"会变成 8（Logo），
        //       主色就写到 Logo 上了。
        // 读配置要持 SyncRoot（HTTP 线程也可能同时在改这棵树）
        BacklightProfile? current;
        lock (_model.SyncRoot)
            current = BacklightModes.Snapshot(_model.Presets["backlight"] as JsonObject, level: 2, maxZones);
        if (current is null) return null;

        var table = new List<(int Addr, int[] Rgb)>();
        var seen = new HashSet<int>();
        foreach (var z in current.Zones.OrderByDescending(z => z.Addr))   // addr 降序 = 键盘 1..3
        {
            if (extras.Values.Contains(z.Addr) || !seen.Add(z.Addr)) continue;
            table.Add(z);
        }
        if (table.Count == 0) return null;

        var seeded = OemLedRegistry.Read(key, table.ConvertAll(t => t.Addr), maxZones);
        if (seeded is null) return null;

        // 原厂 brightness=0 / led_mode=OFF 存的不是"这一档的配色"，是"关灯"。
        // 播进来这一档以后每次切过去都是黑的（EC 的 level 0 = 关闭），宁可继续"缺档不凑"。
        if (seeded.Level == 0 || seeded.Effect == 0)
        {
            Console.WriteLine($"[light/oem] 「{BacklightModes.Label(key)}」档原厂那边存的是关灯"
                              + $"（灯效 {seeded.Effect} / 档位 {seeded.Level}），不拿它播种");
            return null;
        }

        // ② 分区数必须与现有表**一样多**：Parse 会跳过原厂里全 0 的段，少一条就会让随后的
        //    `WriteSection` 把 `presets.backlight.zones` 整表砍小（背光页 tab 变少，2026-11-14 那类事故）。
        //    原厂没给色的地址沿用现表颜色。
        var byAddr = new Dictionary<int, int[]>();
        foreach (var z in seeded.Zones) byAddr[z.Addr] = z.Rgb;

        var zones = new (int Addr, int[] Rgb)[table.Count];
        for (var i = 0; i < table.Count; i++)
            zones[i] = (table[i].Addr, byAddr.TryGetValue(table[i].Addr, out var rgb) ? rgb : table[i].Rgb);

        var profile = new BacklightProfile(seeded.Level, seeded.Effect, zones);

        Console.WriteLine($"[light/oem] 「{BacklightModes.Label(key)}」档我们这边没有档案，"
                          + $"用原厂注册表里的配色补上（灯效 {profile.Effect} / 档位 {profile.Level} / "
                          + $"{profile.Zones.Length} 个分区）");
        WriteProfileForMode(key, profile);
        return profile;
    }

    /// <summary>
    /// 开机后 25 秒再核一次本档灯效。
    ///
    /// 为什么需要：注册表镜像解掉的是"daemon 读到的旧值"，但它可能已经读过一次旧值并写完
    /// （它比我们晚启动、却可能比这次核对早）；这一拍无条件再写一次，谁先谁后都收敛到用户的颜色。
    /// 期间用户换了档就作废（不对已经切走的档补色）。
    /// </summary>
    private void ScheduleBootLedVerify(string key)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BootVerifyMs).ConfigureAwait(false);

                // 三次机会：正常一次就够。被"已经有一次重放在飞"的单飞闸门挡掉时错开再试 ——
                // 直接调 ScheduleBacklightApply 会被它第一行的 busy 判定无声吞掉，这一拍就永远没了
                // （daemon 盖回旧色也没人补）。
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0) await Task.Delay(BootVerifyRetryMs).ConfigureAwait(false);

                    string? want;
                    lock (_blFollowLock) want = BacklightModes.ReplayKey(_blModeKey, BacklightCustomOverride);
                    if (want != key) return;                     // 用户换了档：不对已经切走的档补色

                    // 灯是关着的（开机 25 秒内按了 FN 灯键 / 自动熄灭正在跑）⇒ 不点亮，
                    // 与 TryBootApply「绝不凭空点亮一盏关着的灯」同一条规矩。
                    if (EcBacklightLevelNow() is 0)
                    {
                        Console.WriteLine("[light] 开机核对：键盘背光是关着的，不点亮");
                        return;
                    }

                    if (Volatile.Read(ref _blApplyBusy) == 1) continue;   // 占用：等一下再试

                    ScheduleBacklightApply(key, "开机核对");
                    return;
                }

                Console.WriteLine("[light] 开机核对：三分钟内一直有别处的重放在跑，这次跳过（下次切档会自然对齐）");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[light] 开机核对异常：" + e.Message);
            }
        });
    }

    /// <summary>
    /// 开关「自定义接管」（`POST /api/backlight/custom-override`）—— 抄原厂的 `CustomOverride`：
    /// 打开后不管系统在哪一档，重放的都是第四套 `custom`；关掉时立刻回到当前系统档那份。
    ///
    /// ⚠️ **打开时绝不读当前灯色去播种 custom**（2026-11-18 改）。原厂 `LEDModuleControl()` 点 Custom
    /// 读的是 Custom 自己的存档（`GMRegisty.GetRegistryStr(get_led_policy())`）再写 EC，
    /// 从不把当前档的配色拷进 Custom。只有 Custom 档案**从来没建过**时才补原厂出厂默认
    /// `LED_Custom = "1100FFFF"` = 常亮 / 50% / 全白（见 <see cref="EnsureOemCustomProfile"/>）。
    /// </summary>
    internal JsonObject SetBacklightCustomOverride(bool on)
    {
        string? seeded = on ? EnsureOemCustomProfile() : null;

        var r = _model.Set(["backlight"], new JsonObject { ["custom_override"] = on });
        if (!r.Ok) return new JsonObject { ["ok"] = false, ["error"] = r.Error };

        var target = on ? "custom" : CurrentPerfKey();
        var res = target is not null
            ? ApplyBacklightForMode(target, on ? "自定义接管" : "关闭自定义接管")
            : Note(false, null, "读不到当前系统档，关掉接管后没法选该回哪一套");

        res["override"] = on;
        if (seeded is not null) res["seeded"] = seeded;

        Console.WriteLine(on
            ? "[light] 已开启「自定义接管」：不管系统档怎么切都用「自定义」档案这一套"
              + (seeded is null ? "" : "（原来没有 Custom 档案，已补原厂默认白/常亮/50%）")
            : "[light] 已关闭「自定义接管」：回到按系统档重放");

        return res;
    }

    /// <summary>
    /// Custom 档案从未建过时补原厂出厂默认：常亮(1) / 50%(1) / 全白 —— 反编译常量
    /// `LED_Custom = "1100FFFF;1100FFFF;1100FFFF;1100FFFF;1100FFFF"`。
    /// 有档案就一个字节都不动；连分区地址都读不到就不凭空编（保持"缺档不凑"）。
    /// </summary>
    private string? EnsureOemCustomProfile()
    {
        var maxZones = BacklightZones.Sanitize(Current.KbZones);
        if (BacklightModes.ForMode(_model.Presets, "custom", maxZones) is not null) return null;

        var backlight = _model.Presets["backlight"] as JsonObject;
        var addrs = Current.ExtraLightAddrs;
        var oem = BacklightModes.OemCustomDefault(backlight, maxZones,
            hasLogo: addrs.ContainsKey("logo"), hasTail: addrs.ContainsKey("tail"));
        return oem is null ? null : WriteProfileForMode("custom", oem);
    }

    /// <summary>
    /// 把**当前生效的**配色存成某档的档案（常驻按模式功能，只落盘不写 EC）。
    /// 返回落盘的档位键；没有可存的东西（读不到分区）时返回 null。
    /// </summary>
    internal string? StoreProfileForMode(string? mode, int? level)
    {
        if (BacklightModes.Key(mode) is not { } key) return null;

        var backlight = _model.Presets["backlight"] as JsonObject;
        var snapshot = BacklightModes.Snapshot(backlight, level,
            BacklightZones.Sanitize(Current.KbZones));
        return snapshot is null ? null : WriteProfileForMode(key, snapshot);
    }

    /// <summary>
    /// 在背光页调的是**哪一套**：返回键盘上此刻正在显示的那一套（自定义接管开着时是 `custom`），
    /// 读不到系统档时返回 null。
    /// </summary>
    internal string? LiveBacklightKey()
        => BacklightModes.ReplayKey(CurrentPerfKey(), BacklightCustomOverride);

    /// <summary>
    /// 这笔背光改动要不要**当场写 EC**。
    ///
    /// 2026-11-19 用户报的 bug：在背光页选「游戏」chip 调色（系统此刻在「办公」），键盘当场就变了。
    /// 正确语义是"改动只记进游戏档的档案，切到游戏才用上" —— 判定与切档重放共用
    /// <see cref="LiveBacklightKey"/>，别再在别处各写一份比较。
    /// </summary>
    internal bool IsLiveBacklightMode(string? editMode)
        => BacklightModes.AppliesLive(BacklightModes.Key(editMode), LiveBacklightKey());

    /// <summary>灯效块里属于「配色档案」的键（`BacklightModes.ToJson` 会产出的那几个）。</summary>
    private static readonly string[] ProfileKeys = ["effect", "level", "zones", "logo", "tail"];

    /// <summary>把一份档案整块写进 `perf.modes[档].light`，与风扇曲线同一个家；custom 不会创建风扇档。</summary>
    /// <remarks>
    /// ⚠️ 只清**配色档案自己的键**（effect/level/zones/logo/tail），别整块清空：
    /// 同一个 `light` 块里还住着这一档的自动熄灯策略（`auto_off_min` / `auto_off_on_lock`），
    /// 整块重建会在用户调个颜色的时候把那个策略一起抹掉。
    /// </remarks>
    internal string? WriteProfileForMode(string key, BacklightProfile profile)
    {
        lock (_model.SyncRoot)
        {
            if (ModeBundle.EnsureLightBlock(_model.Presets, key) is not { } light) return null;

            var fresh = BacklightModes.ToJson(profile);
            // 只清配色档案自己那几个键，其余（这一档的自动熄灯策略）原样留着
            foreach (var name in ProfileKeys)
                if (light.ContainsKey(name)) light.Remove(name);
            foreach (var (name, value) in fresh.ToList()) light[name] = value?.DeepClone();

            _model.Save();
        }
        return key;
    }

    /// <summary>
    /// 按模式灯效的现状（给 `GET /api/status` 的 `bl_follow` 段）。`on` 恒为 true —— 开关已撤掉，
    /// 留给老消费者看；`applied` = 键盘上现在实际是哪一档的配色（重放成功后才会变）。
    /// </summary>
    internal JsonObject BacklightFollowState()
    {
        lock (_blFollowLock)
        {
            var o = new JsonObject
            {
                ["on"] = true,
                ["override"] = BacklightCustomOverride,
                ["mode"] = _blModeKey,
                ["applied"] = _blAppliedKey,
                ["busy"] = Volatile.Read(ref _blApplyBusy) == 1,
            };
            if (_blApplyNote is { } note) o["note"] = note;
            return o;
        }
    }

    private JsonObject Note(bool ok, string? mode, string note)
    {
        lock (_blFollowLock) _blApplyNote = note;
        return new JsonObject { ["ok"] = ok, ["mode"] = mode, ["note"] = note };
    }
}
