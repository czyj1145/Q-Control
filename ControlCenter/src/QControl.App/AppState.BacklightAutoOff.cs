using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.App;

/// <summary>
/// AppState 分片：**键盘背光自动熄灭**。
///
/// 两个独立行为，各一个配置键（一个控件只表达一件事）：
///   · `auto_off_min` —— 闲置 N 分钟（键鼠无输入）熄灯。0 = 不做；
///   · `auto_off_on_lock` —— 锁屏时立刻熄灯（锁屏图片一出现就算锁）。
///
/// **跟着四套灯效档案走**（2026-11-19 用户要求）：这两个键存在 `perf.modes[档].light` 里，
/// 办公 / 游戏 / 狂暴 / 自定义各一份，切到哪一档就用哪一档的策略；
/// **某档没设过 = 不自动熄灭**（默认就不该自己动硬件），见 <see cref="EffectiveAutoOff"/>。
///
/// 恢复条件只有一条：**解锁了 且 最近 2 秒内有输入**。这条同时覆盖了
/// 「睡眠唤醒」—— GetLastInputInfo 的 tick 在休眠期间不累加，醒来闲置值仍然很大，
/// 所以唤醒后不会自己亮起来，直到用户真的动了键鼠（那时说明有人在用）。
///
/// ⚠️ 三条不能省的规矩：
///   ① **熄灯前必须看 EC 真值**（快照里的 `ec.backlight_level`），不能按配置猜 ——
///      用户按 FN+2 把灯关掉时我们什么都不用做，凭配置猜就会"熄"一个已经黑着的灯，
///      然后再"恢复"成亮的（凭空点亮）。
///   ② **恢复前必须再看一眼真值**：熄灯期间用户按 FN+2 / 点界面把灯打开了 ⇒
///      现在的档位不是 0，那就不许拿旧的 saved_level 覆盖（那是用户的动作）。
///   ③ 写 EC 慢（一条 ~46ms × 分区数 + 节流），**不占采样线程**：判断在采样拍里做，
///      实际写入丢独立线程，且失败后 30 秒冷却 —— 否则每次输入都重试一遍，
///      EC 命令队列会把采样和曲线一起拖住。
/// </summary>
public sealed partial class AppState
{
    /// <summary>0 = 没熄；1 = 闲置熄灯；2 = 锁屏熄灯。</summary>
    private int _autoOffBy;

    /// <summary>熄灯前记下的档位（1/2）。-1 = 没有记录。</summary>
    private int _autoOffSavedLevel = -1;

    /// <summary>用户手动写背光的代数。后台熄灯线程开工前比对，变了就放弃 —— 不抢用户的动作。</summary>
    private int _autoOffUserToken;

    /// <summary>写入失败后的冷却（TickCount64 毫秒）。</summary>
    private long _autoOffCooldownUntil;

    /// <summary>后台写入单飞。</summary>
    private int _autoOffBusy;

    /// <summary>"不允许写 EC"这个原因只记一次日志。</summary>
    private bool _autoOffDeniedLogged;

    private const long AutoOffCooldownMs = 30_000;

    /// <summary>「最近有输入」的判据（毫秒）。比这个更久不算人在用。</summary>
    private const long AutoOffIdleResetMs = 2_000;

    /// <summary>
    /// 采样循环里每拍调一次（`Loop`）。只做判断 + 起后台写入，本身很快。
    /// </summary>
    private void TryAutoOffBacklight(JsonObject? snap)
    {
        var (minutes, onLock) = EffectiveAutoOff();

        // 功能被关掉：如果灯正黑着，先恢复（用户关开关不等于喜欢黑着键盘）
        if (minutes <= 0 && !onLock)
        {
            if (_autoOffBy != 0) _ = RestoreBacklightAsync("功能已关闭");
            return;
        }

        var locked = SessionLocked();
        var idle = IdleMilliseconds();

        if (_autoOffBy != 0)
        {
            // 恢复：解锁 + 有人在用（见类注释的 ②）
            if (!locked && idle < AutoOffIdleResetMs)
                _ = RestoreBacklightAsync("已恢复");
            return;
        }

        if (Environment.TickCount64 < _autoOffCooldownUntil) return;

        if (locked && onLock)
        {
            _ = TurnOffBacklightAsync(2, snap);
            return;
        }

        if (minutes > 0 && idle >= minutes * 60_000L)
            _ = TurnOffBacklightAsync(1, snap);
    }

    /// <summary>
    /// **键盘上此刻显示的那一档**的自动熄灯策略：
    ///   · 这一档自己有（`perf.modes[档].light` 里的 `auto_off_min` / `auto_off_on_lock`）⇒ 用它；
    ///   · 没设过 ⇒ **不自动熄灭**（0 / false）。
    ///
    /// 没设过**不拿全局 `presets.light` 顶**（2026-11-19 用户定「默认改为不自动熄灭」）：
    /// 默认是一盏灯就不该自己灭 —— "会替你动硬件"的功能，默认必须关。
    /// 全局那两个键只留给老配置读、不再参与判定（界面上也没有编辑器了）。
    ///
    /// 判定用 <see cref="LiveBacklightKey"/> —— 与"这一档是不是当前档"、切档重放共用同一份口径
    /// （自定义接管开着时当前那一套是 `custom`）。读不到系统档时同样按"不熄"处理。
    /// </summary>
    private (int Minutes, bool OnLock) EffectiveAutoOff()
    {
        if (LiveBacklightKey() is not { } key) return (0, false);
        if (BacklightModes.AutoOffOf(_model.Presets, key) is not { } own) return (0, false);

        return (own.Minutes, own.OnLock);
    }

    /// <summary>
    /// 写某一档的自动熄灯策略（`POST /api/backlight/auto-off`）。
    /// `mode` 为 null ⇒ 写当前这一档（自定义接管开着时就是 `custom`）。
    ///
    /// 只在写的是**当前这一档**时才作废旧状态机：改了别的档不影响现在的行为。
    /// 当前这一档从"熄灯"改成"不熄"而灯正黑着 ⇒ 立刻恢复（`ResetAutoOffState` 的语义）。
    /// </summary>
    internal JsonObject SetAutoOffForMode(string? mode, int minutes, bool onLock)
    {
        var key = BacklightModes.Key(mode) ?? LiveBacklightKey();
        if (key is null)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "读不到当前系统档，没法决定这份自动熄灭存进哪一档",
            };

        var value = new BacklightModes.AutoOff(minutes, onLock);
        bool written;
        lock (_model.SyncRoot)
        {
            written = BacklightModes.SetAutoOff(_model.Presets, key, value);
            if (written) _model.Save();
        }
        if (!written)
            return new JsonObject { ["ok"] = false, ["mode"] = key, ["error"] = "这一档写不进去（档位键不认识）" };

        var live = IsLiveBacklightMode(key);
        if (live) ResetAutoOffState();

        Console.WriteLine($"[light] 自动熄灭：把「{BacklightModes.Label(key)}」档设为 "
                          + (minutes <= 0 && !onLock
                              ? "不自动熄灭"
                              : (onLock ? "锁屏 / 睡眠时熄" : $"闲置 {minutes} 分钟熄"))
                          + (live ? "（当前档，立即生效）" : "（切到该档才生效）"));

        return new JsonObject
        {
            ["ok"] = true,
            ["mode"] = key,
            ["minutes"] = Math.Clamp(minutes, 0, 60),
            ["on_lock"] = onLock,
            ["live"] = live,
        };
    }

    /// <summary>
    /// 熄灯。`_autoOffBy` 立刻置位（乐观），失败在后台线程里回滚。
    /// 注意：置位前先确认 EC 真值是亮的 —— 黑着就别记状态，否则会"恢复"出一盏被用户关掉的灯。
    /// </summary>
    private Task TurnOffBacklightAsync(int kind, JsonObject? snap)
    {
        if (!Current.WritesAllowed)
        {
            // 只记一次：用户看到"设了没反应"时，日志里得有原因（每秒一条会把日志顶爆）
            if (!_autoOffDeniedLogged)
            {
                _autoOffDeniedLogged = true;
                Console.Error.WriteLine("[light] 自动熄灯没生效：现在不允许写 EC（" + Current.DenyHint() + "）");
            }
            return Task.CompletedTask;
        }

        var level = CurrentBacklightLevel(snap);
        if (level is null or 0) return Task.CompletedTask;

        _autoOffSavedLevel = level.Value;
        _autoOffBy = kind;

        var token = Volatile.Read(ref _autoOffUserToken);
        return RunAutoOffWrite(async () =>
        {
            var error = await Task.Run(() => WriteBacklightLevel(0)).ConfigureAwait(false);

            if (Volatile.Read(ref _autoOffUserToken) != token)
            {
                // 用户在写入期间动了背光 —— 但 **0 已经落地了**（这条 Task.Run 已完成），
                // 用户的颜色被这一笔盖掉。旧写法只清记账、什么都不补 ⇒ 键盘黑着且
                // 没有任何路径会再点亮它（用户看到"调色后灯自己灭了，还不回来"）。
                // 这里按刚记下的亮度补回来；颜色走的是 presets 里用户刚更新的那份。
                if (error is null) WriteBacklightLevel(_autoOffSavedLevel > 0 ? _autoOffSavedLevel : 2);
                _autoOffBy = 0;
                _autoOffSavedLevel = -1;
                return;
            }

            if (error is not null)
            {
                _autoOffBy = 0;
                _autoOffSavedLevel = -1;
                _autoOffCooldownUntil = Environment.TickCount64 + AutoOffCooldownMs;
                Console.Error.WriteLine("[light] 自动熄灯失败：" + error);
                return;
            }

            Console.WriteLine(kind == 2
                ? "[light] 锁屏：键盘背光已熄灭"
                : "[light] 闲置：键盘背光已熄灭");
        });
    }

    /// <summary>恢复。真正开跑之后，任何结局都把状态清掉 —— 反复重试只会把 EC 命令队列占满。</summary>
    private Task RestoreBacklightAsync(string why)
    {
        var saved = _autoOffSavedLevel > 0 ? _autoOffSavedLevel : 2;
        var token = Volatile.Read(ref _autoOffUserToken);

        // 写不了 EC（总闸 / 未提权）：留着状态也没用，清掉免得每拍重试
        if (!Current.WritesAllowed)
        {
            _autoOffBy = 0;
            _autoOffSavedLevel = -1;
            return Task.CompletedTask;
        }

        // ⚠️ 上一次自动写入还在飞（熄灯要逐区写，三区机型实测 ~1.4 秒）⇒ **状态先留着，下一拍再来**。
        //    2026-11-03 修：以前在起后台任务**之前**就把 _autoOffBy / _autoOffSavedLevel 清了，
        //    于是这次恢复一旦被单飞挡掉（RunAutoOffWrite 直接 return），熄灯那次写仍会落地，
        //    而恢复再也没有人安排 —— 键盘一直黑着，直到用户自己按 FN+2 / 点界面把灯打开。
        //    留着状态的代价只是"多试几拍"，写不了时由上面的分支收尾。
        if (Volatile.Read(ref _autoOffBusy) == 1) return Task.CompletedTask;

        return RunAutoOffWrite(async () =>
        {
            // 真正开跑才清状态：清早了就再也没人重试
            _autoOffBy = 0;
            _autoOffSavedLevel = -1;

            try
            {
                // 熄灯期间灯被别的路径点亮了（FN+2 / 界面）⇒ 不覆盖用户的动作
                var now = CurrentBacklightLevel(_cache);
                if (now is null or > 0) return;

                if (Volatile.Read(ref _autoOffUserToken) != token) return;

                var error = await Task.Run(() => WriteBacklightLevel(saved)).ConfigureAwait(false);
                if (error is not null)
                {
                    _autoOffCooldownUntil = Environment.TickCount64 + AutoOffCooldownMs;
                    Console.Error.WriteLine("[light] 自动熄灯后恢复失败：" + error);
                    return;
                }

                Console.WriteLine($"[light] {why}：键盘背光已回到档位 {saved}");

                // ⚠️ 唤醒后解锁（2026-11-21）：上面只写回了**亮度**，颜色仍停在 EC 上电默认色 ——
                //    因为唤醒补写的 3s / 10s 两次都被「锁屏自动熄灭」挡掉了（合盖睡眠醒来就是锁屏态）。
                //    解锁这一刻按档重放一次，颜色和档位一起回来。
                //    ⚠️ 重放写的是**档案里的** level（不是刚恢复的 `saved`）：两者正常是同一个值
                //    （FN 改亮度会被 ObserveBacklightLevel 记进档案），只有在"外部改过但没记上"
                //    的边缘情况才会差一档 —— 那种情况以档案为准是可接受的。
                //    待办**不在这里清**：写完 EC 由 ApplyBacklightForMode 销账，这次要是没写成
                //    （EC 写入被关、后面又被熄灯挡掉），下次解锁还能再试（直到待办满 10 分钟过期）。
                if (ResumeReplayPending()
                    && BacklightModes.ReplayKey(CurrentPerfKey(), BacklightCustomOverride) is { } target)
                {
                    Console.WriteLine("[light] 唤醒后解锁：补一次本档灯效（唤醒那两次被锁屏熄灯挡掉了）");
                    ScheduleBacklightApply(target, "唤醒后解锁");
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[light] 恢复背光异常：" + e.Message);
            }
        });
    }

    /// <summary>后台单飞执行（同一时刻只有一个自动熄灯/恢复动作）。</summary>
    private Task RunAutoOffWrite(Func<Task> work)
    {
        if (Interlocked.Exchange(ref _autoOffBusy, 1) == 1) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception e) { Console.Error.WriteLine("[light] 自动背光异常：" + e.Message); }
            finally { Interlocked.Exchange(ref _autoOffBusy, 0); }
        });
    }

    /// <summary>
    /// 用户（界面 / FN 路径）动过背光：作废自动熄灯的状态与在飞的写入。
    /// 必须在**每一次成功的背光下发**之后调用，否则状态机会拿旧状态覆盖用户的选择。
    /// </summary>
    internal void NotifyBacklightUserWrite()
    {
        Interlocked.Increment(ref _autoOffUserToken);
        _autoOffBy = 0;
        _autoOffSavedLevel = -1;
    }

    /// <summary>
    /// 配置里的自动熄灯策略变了（设置页改键）：旧的"熄灯前档位"作废。
    /// 如果灯正黑着，先把它恢复 —— 只是清状态的话，下一拍会看到一个已经为 0 的档位，
    /// 既不会再熄（本来就是黑的）也不会再恢复（没有状态），键盘就一直黑着。
    /// </summary>
    internal void ResetAutoOffState()
    {
        if (_autoOffBy != 0)
        {
            // 恢复任务自己会清状态；这里**不要** token++，否则会把这次恢复自己作废
            _ = RestoreBacklightAsync("配置已变");
            return;
        }
        _autoOffSavedLevel = -1;
    }

    /// <summary>EC 里的当前档位（读快照；读不到返回 null，绝不猜）。</summary>
    private static int? CurrentBacklightLevel(JsonObject? snap)
    {
        if (snap?["ec"] is not JsonObject ec || ec["error"] is not null) return null;
        return Num(ec["backlight_level"]) is { } v ? (int)v : null;
    }

    /// <summary>
    /// 按 `presets.backlight` 里的分区颜色 / 灯效，把指定档位写到 EC。
    /// 返回 null = 成功；否则是一句给用户看的原因。
    ///
    /// 全部分区同色也要走 <c>SetBacklightZones</c>：EC 侧颜色是按地址存的，
    /// 只发整条（addr=6）会把 B/C 两区留在上一次的颜色。
    /// </summary>
    private string? WriteBacklightLevel(int level)
    {
        var effect = (int)(Num(_model.Get(JsonValue.Create(1), "backlight", "effect")) ?? 1);

        var zones = new List<(int Addr, int[] Rgb)>();
        if (_model.Get("backlight", "zones") is JsonArray arr && arr.Count > 0)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject zo) continue;
                if (Num(zo["addr"]) is not { } addr) continue;
                if (zo["rgb"] is not JsonArray rgb || rgb.Count != 3) continue;
                zones.Add(((int)addr, [
                    Math.Clamp((int)(Num(rgb[0]) ?? 255), 0, 255),
                    Math.Clamp((int)(Num(rgb[1]) ?? 255), 0, 255),
                    Math.Clamp((int)(Num(rgb[2]) ?? 255), 0, 255),
                ]));
            }
        }

        try
        {
            if (zones.Count > 0) _ec.SetBacklightZones(level, zones, effect);
            else _ec.SetBacklight(level, effect: effect);
            // 恢复亮灯也是我们自己写的 EC ⇒ 下一拍读到的亮度变化不认成 FN 键
            MarkBacklightEcWrite();
            return null;
        }
        catch (Exception e)
        {
            return "下发背光失败：" + e.Message;
        }
    }

    // ================================================================ 平台探测

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    private const int WtsCurrentSession = -1;   // WTS_CURRENT_SESSION
    private const int WtsSessionInfoEx = 25;    // WTSSessionInfoEx

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass,
        out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    /// <summary>
    /// 距最后一次键鼠输入过了多久（毫秒）。
    /// 用 32 位 tick 做无符号减法：GetLastInputInfo 只给 32 位，跨 49.7 天回绕也成立。
    /// 取不到就返回 0（= 当作"刚有输入"，最保守：不会误熄灯）。
    /// </summary>
    private static long IdleMilliseconds()
    {
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.Time);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 当前是否处于锁屏。**两个判据取或**，任一说是锁屏就算锁：
    ///
    /// ① `WTSQuerySessionInformation(WTSSessionInfoEx)` 的 `SessionFlags` —— 主判据。
    ///    Windows 10/11 的锁屏是两段：先是**锁屏图片**（时间/日期/壁纸），这段仍在用户自己的
    ///    普通桌面上；按任意键才切到 Winlogon 安全桌面、出现 PIN 输入框。②那种"打不开输入桌面"
    ///    的判据只能认出**第二段**，于是熄灯要等到用户敲键盘唤起 PIN —— 2026-11-01 实测：
    ///    锁屏图片出现 3.5 秒后才认出锁屏（详见 DETAILS）。`SessionFlags` 在第一段就置 LOCKED。
    /// ② <see cref="InputDesktopLocked"/> —— 兜底。WTS 查不到（极老系统 / API 失败）时用它。
    ///
    /// ⚠️ 两个判据都只往"锁了"的方向合并，不互相否决：漏判的代价是灯该灭没灭（用户看得见），
    /// 误判的代价是干活时突然黑灯 —— 所以查到 LOCKED 就立刻认，不必等两边一致。
    /// </summary>
    private static bool SessionLocked()
    {
        if (SessionFlagsLocked() == true) return true;
        return InputDesktopLocked();
    }

    /// <summary>
    /// WTS 会话标志（`WTS_SESSIONSTATE_LOCK = 0` / `UNLOCK = 1`）。null = 查不到，交给兜底判据。
    ///
    /// 结构 `WTSINFOEX`（Level 1）在 64 位下的实际布局 —— 用探针逐字节 dump 过：
    /// ```
    /// +0  DWORD                  Level            (= 1)
    /// +4  -(4 字节填充：union 里有 LARGE_INTEGER，按 8 字节对齐)
    /// +8  ULONG                  SessionId
    /// +12 WTS_CONNECTSTATE_CLASS SessionState
    /// +16 LONG                   SessionFlags
    /// +20 WCHAR[]                WinStationName   ("Console")
    /// ```
    /// ⚠️ 别按"Level 后面紧跟 SessionId"去读偏移 4 —— 那样读到的是填充 0，SessionState 读成
    /// SessionFlags，永远得到"未锁屏"（第一版探针就是这么被骗的）。
    /// </summary>
    private static bool? SessionFlagsLocked()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, WtsCurrentSession, WtsSessionInfoEx,
                    out buffer, out var bytes)) return null;
            if (buffer == IntPtr.Zero || bytes < 20) return null;
            if (Marshal.ReadInt32(buffer, 0) != 1) return null;      // 只认 Level 1 的布局

            var flags = Marshal.ReadInt32(buffer, 16);
            return flags == 0;                                        // 0 = WTS_SESSIONSTATE_LOCK
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// 兜底判据：能不能打开输入桌面。锁屏切到 Winlogon 桌面后，普通（管理员）进程打开会失败。
    /// **打开失败一律按"没锁屏"处理** —— 宁可少熄一次灯，也不能在用户干活时突然黑灯。
    /// </summary>
    private static bool InputDesktopLocked()
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_READOBJECTS */);
            if (handle != IntPtr.Zero) return false;

            // ERROR_ACCESS_DENIED = 被 Winlogon 桌面挡在外面 ⇒ 真的锁屏了
            var err = Marshal.GetLastWin32Error();
            return err == 5;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseDesktop(handle);
        }
    }
}
