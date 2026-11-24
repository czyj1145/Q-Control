using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.FanCurve;
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
/// AppState 分片：**用户设置与周边硬件**。
/// `GET/POST /api/app/settings`、检查更新、区间日志、OSD、显卡超频、原厂自启抑制。
/// </summary>
public sealed partial class AppState
{
    /// <summary>
    /// `GET /api/app/settings`：设置页要的全部开关现状。
    ///
    /// 三个 EC 值（`ec_capable` / `ec_default` / `ec_writes`）**必须一起给**，
    /// 界面才能把开关画对 —— 让它自己推默认值，就会在"未适配机型"上画出一个
    /// 看起来能开、实际被硬拦的开关（10-18 踩过）。
    /// </summary>
    public JsonObject AppSettings()
    {
        bool? auto = null;
        string? err = null;
        try
        {
            auto = Autostart.IsOn();
        }
        catch (Exception e)
        {
            err = "读取开机自启失败：" + e.Message;
        }

        var dev = Current;
        return new JsonObject
        {
            ["ok"] = err is null,
            ["error"] = err,
            ["autostart"] = auto,
            ["start_mode"] = Str(_model.Get("ui", "start_mode")) ?? "window",
            ["theme"] = Str(_model.Get("ui", "theme")) ?? "dark",
            ["ec_capable"] = dev.EcCapable,
            ["ec_default"] = dev.EcDefault,
            ["ec_writes"] = dev.WritesAllowed,
            ["ec_user"] = dev.EcOverride,
            ["device_verified"] = dev.Verified,
            ["device_name"] = dev.Name,
            // FN 快捷键提示条（OSD）的唯一总闸；默认开
            ["osd"] = Truthy(_model.Get(JsonValue.Create(true), "ui", "osd")),
            ["vendor_kill_on_start"] = Truthy(_model.Get(JsonValue.Create(false), "vendor", "kill_on_start")),
            ["vendor_block_autostart"] = Truthy(_model.Get(JsonValue.Create(false), "vendor", "block_autostart")),
            // 日志：自动记录开关 + 触发温度（2026-10-21；真源在 presets.log）
            ["log_auto"] = Logging.LogCapture.AutoOn,
            ["log_auto_temp"] = Logging.LogCapture.AutoTemp,
            ["log_dir"] = Logging.FileLog.DirectoryPath,
        };
    }

    // ================================================================ 锁定 Win 键

    /// <summary>
    /// EC 侧的锁状态（`0x0200` 写 a6=1）。EC **只管内建键盘**：外接 USB/蓝牙键盘
    /// 根本不经过 EC，所以这一半必须配 <see cref="_winLock"/> 的全局键盘钩子一起用。
    /// </summary>
    private volatile bool _winEcLocked;

    /// <summary>
    /// 现在**真的**在拦 Win 键吗 —— 两条腿任一在拦就算（不是"用户上次点了开"）。
    /// 设置页每拍对一次（托盘也能切这个开关），所以只读字段、不做任何 IO。
    /// </summary>
    public bool WinKeyLocked => _winEcLocked || _winLock.Locked;

    /// <summary>当前生效的通道：`ec+hook` / `ec` / `hook` / `none` —— 诊断与界面提示用。</summary>
    public string WinKeyBackendName => (_winEcLocked, _winLock.Locked) switch
    {
        (true, true) => "ec+hook",
        (true, false) => "ec",
        (false, true) => "hook",
        _ => "none",
    };

    /// <summary>
    /// `POST /api/keyboard/win-lock`：锁定 / 解锁 Win 键。`body: {locked: true|false}`。
    ///
    /// <para>
    /// **两套一起上，各管一半**（2026-11-22 实测后的结论）：
    /// <list type="bullet">
    /// <item><b>EC</b>（<c>0x0200</c> 写 a6=1，原厂 <c>SetWinKey</c> 同一套编码）——
    ///   跑在固件/EC 层，**只管笔记本内建键盘**。好处是不依赖我们进程活着、锁着时
    ///   连钩子都不用装；坏处是外接 USB / 蓝牙键盘完全不经过 EC，一点都拦不到
    ///   （用户实测："只锁了笔记本键盘"）。</item>
    /// <item><b>全局键盘钩子</b>（<see cref="Input.WinKeyLock"/>）—— <c>WH_KEYBOARD_LL</c>
    ///   在系统输入层吞键，**所有键盘都拦**，包括外接的。代价是只在本进程运行时有效，
    ///   且会让反作弊软件起疑。</item>
    /// </list>
    /// 所以锁定 = EC 写 1 + 装钩子；解锁 = EC 写 0 + 摘钩子。任一条失败不影响另一条生效，
    /// 回包里 <c>backend</c> 如实说明这次到底哪条腿站住了。
    /// </para>
    ///
    /// <para>
    /// ⚠️ EC 这条**没有读回通道**（读侧 a6 是亮度档），状态记在原厂注册表 <c>WinKey</c>，
    /// 重启后靠它重放（见 <see cref="RestoreWinKeyLock"/>）。
    /// ⚠️ 退出时会主动解锁（EC 写 0 + 摘钩子）：这是会吃掉键盘按键的功能，
    /// 关掉控制中心还留一个没人知道的死 Win 键，比它带来的便利糟得多。
    /// </para>
    /// </summary>
    public JsonObject SetWinKeyLock(bool on)
    {
        // ① EC：内建键盘（固件层，跨进程有效）
        var ecOk = false;
        string? ecError = null;
        try
        {
            _ec.SetWinKeyLock(on);
            _winEcLocked = on;
            ecOk = true;
        }
        catch (Exception e)
        {
            ecError = $"{e.GetType().Name}: {e.Message}";
            Console.WriteLine($"[winkey] EC 写失败（{ecError}）—— 内建键盘这一半没锁上");
        }

        // ② 钩子：外接键盘（系统输入层，进程内）
        var hookOk = _winLock.Set(on);

        if (!ecOk && !hookOk)
        {
            Console.WriteLine($"[winkey] 锁定 Win 键失败：EC 与钩子都没成功（{_winLock.Error}）");
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = _winLock.Error ?? ecError ?? "EC 与键盘钩子都没成功",
                ["locked"] = WinKeyLocked,
                ["backend"] = WinKeyBackendName,
            };
        }

        Console.WriteLine($"[winkey] Win 键 → {(on ? "锁定" : "解锁")}"
            + $"（EC {(ecOk ? "ok" : "失败")} / 钩子 {(hookOk ? "ok" : "失败")}"
            + $"，吞掉 {_winLock.Blocked} 次）");
        return new JsonObject
        {
            ["ok"] = true,
            ["locked"] = WinKeyLocked,
            ["ec"] = ecOk,
            ["hook"] = hookOk,
            ["ec_error"] = ecError,
            ["backend"] = WinKeyBackendName,
            ["blocked"] = _winLock.Blocked,
            ["seen"] = _winLock.Seen,
            ["thread_alive"] = _winLock.ThreadAlive,
        };
    }

    /// <summary>
    /// 启动时把上次的 Win 键锁重放一遍（`Start()` 里调）。
    ///
    /// <para>
    /// 为什么需要：EC 的锁**不随程序退出消失**。程序崩了、或热重启而 EC 状态还在时，
    /// 界面会显示"未锁"而内建键盘其实还锁着 —— 那是撒谎。真值记在原厂注册表
    /// <c>WinKey</c>（原厂自己也是这么记的），这里读出来重放：EC 写 1 + 装钩子。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 读不到（没装原厂 / 非 NL5）就**什么都不做**，绝不主动去锁 —— 默认永远是不锁。
    /// </para>
    /// </summary>
    private void RestoreWinKeyLock()
    {
        bool? stored;
        try
        {
            stored = EcHal.StoredWinKeyLock;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[winkey] 读注册表失败，跳过 Win 键恢复：{e.Message}");
            return;
        }
        if (stored is not true) return;

        var ecOk = false;
        try
        {
            _ec.SetWinKeyLock(locked: true);
            _winEcLocked = true;
            ecOk = true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[winkey] 恢复 EC 锁失败（{e.GetType().Name}: {e.Message}）");
        }

        var hookOk = _winLock.Set(true);          // 外接键盘那一半
        Console.WriteLine($"[winkey] 按注册表恢复：内建键盘 EC {(ecOk ? "已锁" : "未锁")}"
                          + $" / 外接键盘钩子 {(hookOk ? "已锁" : "未锁")}");
    }

    /// <summary>
    /// `POST /api/app/settings`：设置页那几个开关。
    ///
    /// 界面**每次只发自己那一个键**，所以这里按 key 逐个处理、逐个回结果 ——
    /// 一个开关失败不该把别的开关也判成失败（旧实现是共用一个 ok）。
    ///
    /// `autostart` 已接真（托盘做完之后）：走计划任务，非管理员**如实失败**。
    /// `vendor_block_autostart` 还不能做，如实拒绝并说明原因。
    /// </summary>
    public JsonObject SetAppSettings(JsonObject body)
    {
        var outObj = new JsonObject { ["ok"] = true };

        if (body["autostart"] is JsonValue av && av.TryGetValue<bool>(out var autoOn))
        {
            // 早先实现同口径：这里只开合自启，不碰「启动方式」（那是另一个键）。
            // 自启一律拉起 `--tray`（静默进托盘），与"默认最小化到托盘"一致。
            var (ok, detail) = Autostart.Set(autoOn, Autostart.TrayCommand(Port));
            if (ok)
            {
                outObj["autostart"] = autoOn;
                // 记下用户的明确意图：关过就不再被「默认开启」重新打开
                _model.Set(["ui", "autostart_off"], JsonValue.Create(!autoOn));
                outObj["note"] = detail;
            }
            else
            {
                // ⚠️ 失败时不写 autostart_off：那是"用户的选择"，写入失败不等于选择
                outObj["ok"] = false;
                outObj["error"] = detail;
                outObj["autostart"] = Autostart.IsOn();   // 回读实际状态给界面对齐
            }
        }

        if (body["start_mode"] is JsonValue sv && sv.TryGetValue<string>(out var rawMode))
        {
            // 非法值一律按 window（与早先实现同语义：只有 tray / window 两个合法值）
            var m = rawMode is "tray" or "window" ? rawMode : "window";
            _model.Set(["ui", "start_mode"], JsonValue.Create(m));
            outObj["start_mode"] = m;
        }

        if (body["theme"] is JsonValue tv && tv.TryGetValue<string>(out var rawTheme))
        {
            // ⚠️ auto 必须**原样存下来**（而不是解析成当前生效色）—— 否则下次启动
            //    就变成"写死的一档"，用户选的"跟随系统"丢了。解析成生效色是界面的事。
            var t = rawTheme is "dark" or "light" or "auto" ? rawTheme : "dark";
            _model.Set(["ui", "theme"], JsonValue.Create(t));
            outObj["theme"] = t;
        }

        if (body["osd"] is JsonValue ov && ov.TryGetValue<bool>(out var osdOn))
        {
            // 立刻生效，不用等重启（与早先实现同语义：关掉后 FN 事件照样做差分，
            // 只是不弹窗；差分本身还要负责把 FN 改的档位落盘）
            _osd.Enabled = osdOn;
            if (osdOn)
                _hotkey.ShowOsd("", "提示条已开启", "按 FN 键时会显示在屏幕左上角");
            _model.Set(["ui", "osd"], JsonValue.Create(osdOn));
            outObj["osd"] = osdOn;
        }

        // ---- 独立灯珠（Logo / 尾灯）**没有设置项** ----
        // 2026-11-18 用户要求撤掉"自动 / 强制开 / 关"三态：有灯且识别到了就该生效，
        // 识别不到就不该出现，不该让用户来纠正软件的判断。判定只看 Device.ExtraLightsByProfile。
        // 旧 model.json 里残留的 `lights.extra` 已经没人读，留着也无害（不要在这里写迁移）。

        if (body["ec_enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var ecOn))
        {
            var dev = Current;
            if (!dev.EcCapable)
            {
                outObj["ok"] = false;
                outObj["error"] = $"本机没有可用的 EC 命令表（识别为「{dev.Name}」），开关打开也写不出去";
            }
            else
            {
                // 关总闸前先把曲线收掉：留它跑只会在下一轮 WriteDenied 自停，
                // 日志里一串"被拒绝"，用户还以为是故障。
                // release=False —— 此刻也写不进去了，保持当前转速最安全。
                if (!ecOn && Fan.Running) Fan.Stop(release: false);

                // 和档案默认值一致时存回 **null**（= 跟随档案），不落一个"恰好等于默认"的
                // 明确选择：否则换到另一台机器上，这个值会盖掉那台机器该有的默认态。
                _model.Set(["ec", "enabled"],
                    ecOn == dev.EcDefault ? null : JsonValue.Create(ecOn));

                // 回读**实际生效**的值（model.Set 之后 Current 会重新解析）
                var after = Current;
                outObj["ec_enabled"] = after.WritesAllowed;
                outObj["ec_default"] = after.EcDefault;

                // 关掉再打开总闸之后，把曲线接回来（2026-11-12 修）。
                // 关的时候上面那行 `Fan.Stop(release:false)` 会把 `LastError` 清成 null，
                // 而自愈 `HealFanCurve()` 只在 `LastError` 非 null 时重试 ⇒ 总闸重新打开后
                // 没有**任何**路径会把曲线接回来：散热页停在"启动中…"、风扇定在最后写入的转速，
                // 只能重启程序才恢复。用户刚明确把 EC 写权限打开，这里直接接管。
                // 只在 auto 模式下接 —— manual 是用户自己钉的转速，抢了就是"手动自己变回曲线"。
                // 曲线总开关关着时也不接：那是"默认交给固件"，接回来就成了关不掉的功能。
                if (ecOn && !Fan.Running && Fan.FanMode == "auto" && Fan.CurveEnabled)
                {
                    try
                    {
                        var resumed = Fan.Start();
                        if (resumed["ok"]?.GetValue<bool>() == true)
                            outObj["fan_resumed"] = true;
                        else
                            outObj["fan_resume_error"] = resumed["error"]?.GetValue<string>();
                    }
                    catch (Exception e)
                    {
                        outObj["fan_resume_error"] = e.Message;
                    }
                }

                if (!ecOn)
                    outObj["note"] = "EC 写入已关闭：风扇会停在最后写入的转速上"
                                     + "（EC 不会自己接管），重启或睡眠唤醒才复位";
            }
        }

        // ---- 日志总开关（presets.log.enabled；2026-10-29 用户要求"关了就不记"）----
        // 与下面两个键一样：界面每次只发自己那一个，逐键处理、逐键回结果。
        // 关 = 立刻停写（句柄关掉、Console 还原），历史文件一个不删；
        // 开 = 立刻接着写，不用重启。**只有真开成了才落盘**，否则开关会与事实不符。
        if (body["log_enabled"] is JsonValue lev && lev.TryGetValue<bool>(out var logOn))
        {
            if (logOn && Logging.FileLog.Healthy)
            {
                outObj["log_enabled"] = true;      // 已经在写，什么都不用做
            }
            else if (!logOn && !Logging.FileLog.Enabled && !Logging.FileLog.Active)
            {
                outObj["log_enabled"] = false;     // 本来就是关的
            }
            else if (logOn)
            {
                if (Logging.FileLog.SetEnabled(true, out var why))
                {
                    _model.Set(["log", "enabled"], JsonValue.Create(true));
                    outObj["log_enabled"] = true;
                    outObj["note"] = "已开始写日志：" + (Logging.FileLog.CurrentPath ?? "");
                }
                else
                {
                    outObj["ok"] = false;
                    outObj["error"] = "日志没能打开：" + why;
                    outObj["log_enabled"] = false;
                }
            }
            else
            {
                // 正在截区间：先把这一段收尾导出，再停写 —— 否则用户按了关，
                // 手头那段记录会一声不响地变成一份截不出来的半截。
                if (Logging.LogCapture.Active) Logging.LogCapture.Stop(out _, out _, out _);

                Logging.FileLog.SetEnabled(false, out _);
                _model.Set(["log", "enabled"], JsonValue.Create(false));
                outObj["log_enabled"] = false;
                outObj["note"] = "已停止写日志（历史文件保留，随时可以再打开）";
            }
        }

        // ---- 日志：自动记录开关 + 触发温度（presets.log；真源在 Core 的默认配置里）----
        // ⚠️ 两个键都可以单独发（界面每次只发自己那一个），所以逐键处理、逐键回结果。
        if (body["log_auto"] is JsonValue lav && lav.TryGetValue<bool>(out var logAuto))
        {
            var temp = Logging.LogCapture.AutoTemp;
            Logging.LogCapture.SetAuto(logAuto, temp);
            _model.Set(["log", "auto"], JsonValue.Create(logAuto));
            outObj["log_auto"] = logAuto;
            // "出现**新的**错误"是准话：同一条错误反复报（典型是 HWiNFO 没装，重试一次报一次）
            // 只录第一段，60 分钟内不再重复开段 —— 见 LogCapture.ErrRepeatHold。
            outObj["note"] = logAuto
                ? $"已开启自动记录：CPU/GPU 达到 {temp}°C，或出现新的错误时自动开始，回落后自动导出"
                : "已关闭自动记录（手动「开始记录」照常可用）";
        }

        if (body["log_auto_temp"] is JsonValue tv2 && Num(tv2) is { } rawTemp)
        {
            // 钳在 [60,110]：低于 60 会把日常负载全判成"过热"，高于 110 永远不触发
            var t = (int)Math.Clamp(Math.Round(rawTemp), 60, 110);
            Logging.LogCapture.SetAuto(Logging.LogCapture.AutoOn, t);
            _model.Set(["log", "auto_temp"], JsonValue.Create(t));
            outObj["log_auto_temp"] = t;
        }

        if (body["vendor_kill_on_start"] is JsonValue kv && kv.TryGetValue<bool>(out var kill))
        {
            // 只写配置，**此刻不动任何进程** —— 开关名说的是"启动时"。
            // 当前这一刻要退请点卡片里的「立即退出」（一个控件只表达一件事）。
            _model.Set(["vendor", "kill_on_start"], JsonValue.Create(kill));
            outObj["vendor_kill_on_start"] = kill;
        }

        if (body["vendor_block_autostart"] is JsonValue bv && bv.TryGetValue<bool>(out var block))
        {
            var r = SetVendorBlock(block);
            foreach (var (k, v) in r)
                outObj[k] = v?.DeepClone();

            if (r["ok"]?.GetValue<bool>() != true)
            {
                outObj["ok"] = false;
                outObj["error"] = string.Join("；", r["errors"] is JsonArray ee
                    ? ee.Select(e => e?.ToString() ?? "")
                    : ["抑制原厂自启失败"]);
            }
        }

        return outObj;
    }

    /// <summary>
    /// `GET /api/app/about`：「关于」页要的一次性信息 —— 版本 / 运行环境 / 机型 /
    /// 端口 / 启动时间 / 配置文件路径，外加**日志现状与最后 8 行**。
    ///
    /// 为什么全揉一个接口：这一页只在切过去时拉一次，拆成三个 GET 只是多三次
    /// 往返；而「日志尾巴」正好能让人当场看见"刚才那句写进去了没有"。
    /// ⚠️ 里面的 `model_path` / `log.dir` 是**本机绝对路径**，只给本机界面看。
    /// </summary>
    public JsonObject About()
    {
        var dev = Current;
        var ver = AppVersion;

        // 多给几行：尾巴框高度跟着窗口长（见 .log-tail 的 flex），
        // 大屏上能多看到 12 行左右，小窗口只露 6 行 —— 都由 CSS 决定，接口只管给够。
        var (tail, bytes) = Logging.FileLog.Tail(24);

        return new JsonObject
        {
            ["ok"] = true,
            ["version"] = ver,
            ["runtime"] = $".NET {Environment.Version}",
            ["pid"] = Environment.ProcessId,
            ["port"] = Port,
            ["started"] = _startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            ["uptime_s"] = (int)Math.Max(0, (Environment.TickCount64 - _startedTick) / 1000),
            ["device"] = dev.Name,
            ["device_verified"] = dev.Verified,
            ["ec_writes"] = dev.WritesAllowed,
            ["model_path"] = _model.Path,
            ["log"] = new JsonObject
            {
                ["active"] = Logging.FileLog.Active,
                ["healthy"] = Logging.FileLog.Healthy,
                // 总开关（presets.log.enabled 的生效态）。`active` 说的是"句柄挂没挂上"，
                // `enabled` 说的是"用户要不要写" —— 界面上的开关读这个，别拿 active 猜。
                ["enabled"] = Logging.FileLog.Enabled,
                // 没在写的原因（在写时为 null）：总开关关着 / --no-log / 目录不可写
                ["off_reason"] = Logging.FileLog.OffReason,
                ["dir"] = Logging.FileLog.DirectoryPath,
                ["path"] = Logging.FileLog.CurrentPath,
                ["file"] = Logging.FileLog.CurrentPath is { } p ? Path.GetFileName(p) : null,
                ["bytes"] = bytes,
                ["tail"] = new JsonArray(tail.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
            },
            // 区间记录现状（记录中是后端状态，刷新/关页面都还在）
            ["capture"] = CaptureJson(Logging.LogCapture.Snapshot()),
        };
    }

    // ================================================================ 检查更新

    /// <summary>
    /// `GET /api/app/update`：检查更新的**内存状态**（当前版本 / 有没有新版 / 上次检查 / 失败原因）。
    ///
    /// ⚠️ 这里一次网络请求都不发：界面在检查期间会每 500 ms 拉一次，任何"顺手查一下"
    ///    都会变成对着更新源打连发。真正的查询在 <see cref="StartUpdateCheck"/> 起的后台任务里。
    /// </summary>
    public JsonObject UpdateState() => Updates.UpdateChecker.Instance.Snapshot(AppVersion);

    /// <summary>
    /// `POST /api/app/update/check`：起一次后台检查并**立刻返回**。
    ///
    /// 为什么不同步等结果：更新源不通时最长要等 8 秒超时 ×2 个源，让 HTTP 请求陪着等
    /// 会把界面卡成假死。客户端拿到 <c>state=checking</c> 后自己轮询
    /// <see cref="UpdateState"/>，出结果再渲染。
    /// </summary>
    public JsonObject StartUpdateCheck()
    {
        Updates.UpdateChecker.Instance.CheckInBackground();
        return new JsonObject { ["ok"] = true, ["state"] = "checking" };
    }

    /// <summary>
    /// `POST /api/app/update/download`：把最新发布的安装包**下到本机**（后台），立刻返回快照。
    ///
    /// 与检查一样不在 HTTP 里等：一个 2 MB 的包在慢网里也要几分钟，界面靠轮询
    /// <see cref="UpdateState"/> 里的 `download` 段画进度。真正干活的是 <see cref="Updates.UpdateDownload"/>。
    /// </summary>
    public JsonObject StartUpdateDownload()
        => Updates.UpdateDownload.Instance.Start(Updates.UpdateChecker.Instance.Latest);

    /// <summary>`POST /api/app/update/cancel`：停掉下载并删掉半截文件。</summary>
    public JsonObject CancelUpdateDownload() => Updates.UpdateDownload.Instance.Cancel();

    /// <summary>
    /// `POST /api/app/update/install`：把已校验的安装包交给安装器（提权 + 向导）。
    ///
    /// ⚠️ 不在这一层退出进程：安装器写文件前会先 POST /api/app/quit 请我们退，
    ///    用户若在向导里点取消，控制中心必须还活着。
    /// </summary>
    public JsonObject InstallUpdate() => Updates.UpdateDownload.Instance.Install();

    /// <summary>
    /// 开窗时调一次：距上次检查超过 24 小时才真去查（节流在 UpdateChecker 里）。
    ///
    /// ⚠️ 只在**人真的打开界面**时调用（见 UiHost.Run）。`--ui-shot` / 自检路径
    ///    直接 new MainWindow，不经过这里 —— 否则截图会因为网络抖动而变慢、
    ///    甚至截到"正在查询"那一态。
    /// </summary>
    public void MaybeCheckUpdate() => Updates.UpdateChecker.Instance.AutoCheck();

    /// <summary>
    /// `POST /api/app/log/capture/start`：开始一段**区间记录**。
    ///
    /// 全量日志照旧一直在写；这里只是在当天日志里打一个开始标记并记下字节偏移，
    /// 结束时把这段单独导出成 `logs/capture-*.log`。用户要看的是"出问题那几分钟"，
    /// 不是整天那份（单日可到 8 MB）。
    /// </summary>
    public JsonObject StartLogCapture()
    {
        var ok = Logging.LogCapture.Start(out var snap, out var error);
        var outObj = new JsonObject
        {
            ["ok"] = ok,
            ["capture"] = CaptureJson(snap),
        };
        if (!ok) outObj["error"] = error;
        return outObj;
    }

    /// <summary>
    /// `POST /api/app/log/capture/stop`：结束当前区间 → 导出成一个独立文件。
    ///
    /// 失败也要把 `capture` 带上（界面靠它回到"未在记录"），绝不把界面卡在"记录中"。
    /// </summary>
    public JsonObject StopLogCapture()
    {
        var ok = Logging.LogCapture.Stop(out var snap, out var error, out var export);

        var outObj = new JsonObject
        {
            ["ok"] = ok,
            ["capture"] = CaptureJson(snap),
        };
        if (!ok) outObj["error"] = error;
        if (export is { } e)
        {
            outObj["export"] = new JsonObject
            {
                ["file"] = e.File,
                ["path"] = e.Path,
                ["lines"] = e.Lines,
                ["bytes"] = e.Bytes,
                ["from"] = e.From.ToString("yyyy-MM-dd HH:mm:ss"),
                ["to"] = e.To.ToString("yyyy-MM-dd HH:mm:ss"),
                ["seconds"] = e.Seconds,
            };
        }
        return outObj;
    }

    /// <summary>
    /// 区间记录的现状 —— 关于页那两格（手动 / 自动）每 2 秒对齐一次用它。
    ///
    /// 形状与 `GET /api/app/about` 里那份**完全一样**（同一个 <see cref="CaptureJson"/>），
    /// 所以界面只需要一套渲染代码。**故意不开 HTTP 路由**：界面与后端同进程，
    /// 读一个锁内快照就够（见 <c>LocalApi.Capture</c>），为它加一条 GET 只是多一份契约要维护。
    /// </summary>
    public JsonObject CaptureState() => CaptureJson(Logging.LogCapture.Snapshot());

    /// <summary>区间记录的现状（GET /api/app/about 与两个 POST 共用一份形状）。</summary>
    private static JsonObject CaptureJson(Logging.CaptureSnapshot s)
    {
        var outObj = new JsonObject
        {
            ["active"] = s.Active,
            ["auto_running"] = s.AutoRunning,     // 这段是自动开的（界面标注"自动"）
            ["auto"] = s.Auto,                    // 自动记录开关本身（设置页那颗）
            ["auto_temp"] = s.AutoTemp,
            // 这一段是**为什么**自动开的（"CPU 92°C ≥ 90°C" / "出现错误"）。手动段恒 null。
            // 只写"温度≥90°C 或出现错误"那种含混说法，用户会以为阈值算错了（2026-11-08 报障）。
            ["auto_reason"] = s.AutoRunning ? s.Reason : null,
            ["seq"] = s.Seq,
            ["since"] = s.Active ? s.Since.ToString("yyyy-MM-dd HH:mm:ss") : null,
            ["seconds"] = s.Active ? (int)Math.Max(0, (DateTime.Now - s.Since).TotalSeconds) : 0,
        };
        if (s.LastAt is { } at)
        {
            outObj["last"] = new JsonObject
            {
                ["file"] = s.LastFile,
                ["path"] = s.LastPath,
                ["lines"] = s.LastLines,
                ["bytes"] = s.LastBytes,
                ["at"] = at.ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }
        return outObj;
    }

    /// <summary>日志目录（「打开日志目录」用；文件还没建出来也给得出）。</summary>
    public string LogDirectory => Logging.FileLog.DirectoryPath;

    /// <summary>
    /// `GET /api/osd`：OSD / 热键监听的现状。
    ///
    /// `recent` 是最近几次 FN 事件的 EC 变化明细 —— 用户报"按了没反应"时，
    /// 这是唯一能对齐现场的证据（是没收到事件、还是收到了但差分没看出变化）。
    /// </summary>
    public JsonObject OsdState()
    {
        var vendor = _vendor ?? VendorRunning();
        return new JsonObject
        {
            ["ok"] = true,
            ["enabled"] = _osd.Enabled,
            ["available"] = _osd.Available,
            ["error"] = _osd.LastError,
            ["events"] = _hotkey.Events,
            ["hotkey"] = _hotkey.HotkeyState(),
            ["cbwmi"] = _hotkey.CbwmiState(),
            ["fan_hw_max"] = _hotkey.FanHwMax,
            ["vendor"] = vendor?.DeepClone(),
            ["recent"] = _hotkey.Recent(),
        };
    }

    /// <summary>`POST /api/osd`：界面上操作背光/性能时也弹同一条提示（与 FN 路径一致）。</summary>
    public bool ShowOsd(string kind, string title, string subtitle)
        => _hotkey.ShowOsd(kind, title, subtitle);

    // ================================================================ GPU 超频（NVML）

    /// <summary>
    /// `GET /api/gpu/oc`：能力（范围 + 当前偏移）、实时状态、边界表。
    ///
    /// `admin_hint` **只作提示、不用来拦写入** —— 本机实测 `IsUserAnAdmin` 会偶发假值
    /// （同一解释器两次调用结果不同）。真正的裁决交给 NVML 的返回码（4 = 没权限）。
    /// </summary>
    public JsonObject GpuOcState()
    {
        var limits = new JsonObject();
        foreach (var (key, lim) in Bounds.All)
        {
            if (!key.StartsWith("gpu_oc_", StringComparison.Ordinal)) continue;
            limits[key] = new JsonObject
            {
                ["min"] = lim.Lo,
                ["max"] = lim.Hi,
                ["desc"] = lim.Desc,
            };
        }

        var cap = Nvml.Capability();
        var available = cap["ok"] is JsonValue okv && okv.TryGetValue<bool>(out var ok) && ok;

        return new JsonObject
        {
            ["ok"] = true,
            ["available"] = available,
            ["admin_hint"] = _ec.IsAdmin(),
            ["cap"] = cap,
            ["status"] = Nvml.Status(),
            ["limits"] = limits,
        };
    }

    /// <summary>
    /// `POST /api/gpu/oc`：`{core?: 有效MHz, mem?: 有效MHz}`。
    ///
    /// ⚠️ 入参是**有效 MHz**，先过安全边界（`gpu_oc_core` / `gpu_oc_mem`），
    ///    再换算成驱动原值（显存 ×2）。
    /// ⚠️ **不落 model.json**：驱动每次重启都会把 offset 归零，存了反而会让界面显示一个
    ///    "其实没生效"的值 —— 滑块只如实反映驱动里的当前值。
    /// </summary>
    public JsonObject ApplyGpuOc(JsonObject body)
    {
        var core = Num(body["core"]);
        var mem = Num(body["mem"]);
        if (core is null && mem is null)
            return new JsonObject { ["ok"] = false, ["error"] = "没有给出要写入的偏移值" };

        int? coreRaw = null, memRaw = null;
        try
        {
            if (core is not null)
                coreRaw = NvmlOc.EffToRaw("core", Bounds.Clamp("gpu_oc_core", core.Value));
            if (mem is not null)
                memRaw = NvmlOc.EffToRaw("mem", Bounds.Clamp("gpu_oc_mem", mem.Value));
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = "偏移值非法：" + e.Message };
        }

        return Nvml.Apply(coreRaw, memRaw);
    }

    /// <summary>`POST /api/gpu/oc/reset`：两个时钟域归零。</summary>
    public JsonObject ResetGpuOc() => Nvml.Reset();

    /// <summary>
    /// `GET /api/vendor/state`：原厂软件现状（进程 + 两个自启动入口 + 两个开关）。
    /// **只读** —— 只查进程/注册表/计划任务，不改任何东西。
    /// </summary>
    public JsonObject VendorState()
    {
        var procs = _vendor ??= VendorRunning();
        var task = VendorStartup.TaskState();
        // 服务的宿主进程就是 HkeyOSDSvis.exe（见 VendorProcs 的说明），
        // 直接看它在不在，比再起一次 `sc query` 便宜也更准。
        var svcRunning = procs.Any(p => p?.GetValue<string>() is { } s
                                        && s.Equals("HkeyOSDSvis.exe", StringComparison.OrdinalIgnoreCase));
        var svc = VendorStartup.ServiceState(svcRunning);

        // "已抑制" = 任一入口处于禁用态（两个入口任一在跑都能弹原厂 OSD）
        var blocked =
            (task["exists"]?.GetValue<bool>() == true && task["enabled"]?.GetValue<bool>() == false)
            || (svc["exists"]?.GetValue<bool>() == true && Str(svc["start_label"]) == "disabled");

        return new JsonObject
        {
            ["task"] = task,
            ["service"] = svc,
            ["blocked"] = blocked,
            // 恢复时会用到的原值（抑制时记下来的），让界面能写"会恢复成 auto"
            ["saved_service_start"] = Str(_model.Get("vendor", "service_start_type")) ?? "auto",
            ["ok"] = true,
            ["running"] = procs.DeepClone(),
            ["kill_on_start"] = Truthy(_model.Get(JsonValue.Create(false), "vendor", "kill_on_start")),
            ["block_autostart"] = Truthy(_model.Get(JsonValue.Create(false), "vendor", "block_autostart")),
        };
    }

    /// <summary>
    /// 抑制 / 恢复原厂的**开机自启**（`vendor_block_autostart` 这个键的唯一实现）。
    ///
    /// 与「退出原厂软件」的分工（一个控件只表达一件事）：
    ///   · **这个** = 改自启入口（任务 Enabled + 服务启动类型），**不动当前进程**；
    ///   · `POST /api/vendor/stop` = 现在就把进程收掉，**不动自启**。
    /// 所以抑制之后原厂可能还在跑 —— 那是预期，重点是"下次开机不回来"。
    ///
    /// 三个必须守住的点（全部来自早先实现 `vendor_block_autostart`，一条都不能省）：
    ///   ① 原值只在**这次真的从非禁用改成禁用**时记录 —— 否则第二次抑制会把 `disabled`
    ///      存成"原值"，恢复时就恢复不回去了；
    ///   ② 恢复时服务没在跑就顺手拉起来 —— 只改标志位的话用户得重启才看得见原厂回来；
    ///   ③ `block_autostart` 只在**整体成功**时落盘 —— 一处失败还写 True，下次启动就会
    ///      去"复核"一个根本没生效的状态，而且界面会一直显示"已抑制"。
    /// </summary>
    public JsonObject SetVendorBlock(bool on)
    {
        var log = new JsonArray();
        var errors = new JsonArray();

        // ---------------- ① 计划任务 `\ControlCenter\ControlCenter` ----------------
        var task = VendorStartup.TaskState();
        if (task["exists"]?.GetValue<bool>() == true)
        {
            var (ok, detail) = VendorStartup.SetTaskEnabled(!on);
            (ok ? log : errors).Add(JsonValue.Create(detail));
        }
        else
        {
            // 没装原厂（或用户自己删了任务）不是错误 —— 目标状态本来就成立
            log.Add(JsonValue.Create("没有原厂计划任务（没装 / 已被删除），跳过"));
        }

        // ---------------- ② 服务 hkosdservice ----------------
        var svc = VendorStartup.ServiceState(running: false);
        if (svc["exists"]?.GetValue<bool>() == true)
        {
            if (on)
            {
                // ⚠️ 只在"从非禁用变禁用"时记原值（见 ①）
                var label = Str(svc["start_label"]) ?? "";
                if (label.Length > 0 && label != "disabled")
                    _model.Set(["vendor", "service_start_type"], JsonValue.Create(label));

                var (ok, detail) = VendorStartup.SetServiceStart("disabled");
                (ok ? log : errors).Add(JsonValue.Create(detail));
            }
            else
            {
                var want = Str(_model.Get("vendor", "service_start_type")) ?? "auto";
                if (want is not ("auto" or "demand" or "system" or "boot")) want = "auto";

                var (ok, detail) = VendorStartup.SetServiceStart(want);
                (ok ? log : errors).Add(JsonValue.Create(detail));

                if (ok)
                {
                    // 服务宿主就是 HkeyOSDSvis.exe（见 VendorControl 的进程清单），
                    // 看它在不在比再起一次 `sc query` 便宜也更准。
                    var svcRunning = VendorRunning().Any(p => string.Equals(
                        p?.GetValue<string>(), "HkeyOSDSvis.exe", StringComparison.OrdinalIgnoreCase));

                    if (!svcRunning)
                    {
                        var (sok, sdetail) = VendorStartup.StartService();
                        (sok ? log : errors).Add(JsonValue.Create(sdetail));
                    }
                }
            }
        }
        else
        {
            log.Add(JsonValue.Create("没有 Hotkey OSD 服务，跳过"));
        }

        var okAll = errors.Count == 0;

        // 权限问题只在最后补一句人话：两处都失败时原始错误里各带一串异常号，读不懂
        if (!okAll)
        {
            var joined = string.Join(" ", errors.Select(e => e?.ToString() ?? ""));
            if (joined.Contains("拒绝", StringComparison.Ordinal)
                || joined.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || joined.Contains("0x80070005", StringComparison.OrdinalIgnoreCase))
                errors.Add(JsonValue.Create("需要管理员权限：请用 run-as-admin.cmd 启动，"
                                            + "或点顶栏「⚠ 需管理员」徽章换代"));
        }

        // ⚠️ 落盘的开关状态以**实际结果**为准（见 ③）
        _model.Set(["vendor", "block_autostart"], JsonValue.Create(on && okAll));
        _vendor = null;      // 进程/服务现状可能变了：让下一拍重扫，别缓存 10 秒

        return new JsonObject
        {
            ["ok"] = okAll,
            ["block_autostart"] = on && okAll,
            ["saved_service_start"] = Str(_model.Get("vendor", "service_start_type")) ?? "auto",
            ["log"] = log,
            ["errors"] = errors,
        };
    }

    /// <summary>取字符串节点（不抛）。⚠️ TryGetValue&lt;string&gt; 只对字符串节点成立，正合适。</summary>
    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ================================================================ 进程类写操作
    //
    // 这一组都是「杀进程」—— 不可逆、只由用户动作触发。唯一的自动路径是
    // 「启动时自动退出原厂软件」那颗开关（<see cref="StopVendorOnStart"/>），
    // 那也是用户自己打开的；HWiNFO 的自救走 HwInfoHealth。所以这里的入口只有 POST。

    /// <summary>
    /// `POST /api/vendor/stop`：先停服务再收进程（顺序见 <see cref="VendorControl"/>）。
    ///
    /// 成功后立刻让 `_vendor` 缓存失效：否则快照要等最多 10 轮（10 秒）才不再报
    /// "原厂还在跑"，界面刚隐藏的警告会被下一帧又顶回来。
    /// </summary>
    public JsonObject StopVendor()
    {
        var r = VendorControl.Stop();
        _vendor = null;
        return r;
    }
}
