using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.App.Http;
using QControl.Core.Devices;
using QControl.Core.Ec;
using QControl.Core.FanCurve;
using QControl.Core.Model;

namespace QControl.App;

/// <summary>
/// 已实现的 `POST /api/*`。
///
/// 从 `Program` 里拆出来，因为写操作会越来越多，而 Program 只该负责"起服务、挂路由"。
/// `TryHandle` 返回 **null = 这条还没实现**，由 `Router` 落回 501 ——
/// 这样"哪些写操作已接真"在代码里是一张显式的表，漏写不会悄悄变成假成功。
///
/// 所有 body 解析都走 <see cref="TryBody"/>：坏 JSON 一律 400，
/// 绝不让"解析失败 = {}"变成按默认值执行硬件动作。
/// </summary>
internal static class PostHandlers
{
    public static ApiResponse? TryHandle(ApiRequest req, AppState st, Action requestQuit)
        => req.Path switch
        {
            // ---- 电源 / 风扇 / 显示 ----
            "/api/power/plan" => PowerPlan(req),
            "/api/fan/mode" => FanMode(req, st),
            "/api/display/brightness" => DisplayBrightness(req),

            // ---- EC 直写（守卫都在 EcHal 里：总闸 / 阀门 / arm / 边界钳制）----
            "/api/ec/arm" => EcArm(req, st),
            "/api/ec/fan" => EcFan(req, st),
            // 「最大转速」：自定义风扇开着 → 手动满转；关着 → 临时接管满转 / 再点交回
            "/api/ec/fan-max" => EcFanMax(st),
            "/api/ec/backlight" => EcBacklight(req, st),
            // 第四套「自定义」的接管开关（原厂 CustomOverride）：开=始终用 custom，关=回到系统档
            "/api/backlight/custom-override" => BacklightCustomOverride(req, st),
            // 某一档的自动熄灭策略（跟着四套灯效档案走，不写 EC）
            "/api/backlight/auto-off" => BacklightAutoOff(req, st),
            "/api/ec/perf" => EcPerf(req, st),
            "/api/ec/gfx" => EcGfx(req, st),
            "/api/ec/selftest" => Ok(st.Ec.SelfTestWrite(restore: true)),
            "/api/ec/raw" => EcRaw(req, st),

            // ---- 应用设置 ----
            "/api/app/settings" => SetAppSettings(req, st),
            // 键盘：锁定 Win 键（低级键盘钩子，进程内；控制中心一退出按键就恢复）
            "/api/keyboard/win-lock" => KeyboardWinLock(req, st),
            // 日志区间：开始 / 结束（结束时导出独立文件），状态在后端，刷新页面不丢
            "/api/app/log/capture/start" => Ok(st.StartLogCapture()),
            "/api/app/log/capture/stop" => Ok(st.StopLogCapture()),
            "/api/app/log/open" => AppLogOpen(st),
            "/api/app/log/clear" => LogClear(req, st),
            // 机型适配报告：只读采集，save 缺省 true（落盘）；save=false 只回全文给界面复制
            "/api/app/machine-report" => ExportMachineReport(req, st),
            // 历史采样：把 history\hist-*.csv 导出一份 CSV（只读采样文件 + 写一个文件）
            "/api/history/export" => HistoryExport(req),
            // 检查更新：只起后台任务，立刻返回 checking（界面轮询 GET /api/app/update）
            "/api/app/update/check" => Ok(st.StartUpdateCheck()),
            // 下载 / 取消 / 安装：同样只起任务或交出去，状态一律从 GET /api/app/update 的 download 段读
            "/api/app/update/download" => Ok(st.StartUpdateDownload()),
            "/api/app/update/cancel" => Ok(st.CancelUpdateDownload()),
            "/api/app/update/install" => Ok(st.InstallUpdate()),

            // ---- 配置读写 ----
            "/api/model/preset" => ModelPreset(req, st),
            "/api/model/reset" => ModelReset(req, st),

            // ---- 应用 / 曲线 ----
            "/api/fancurve/start" => Ok(st.Fan.Start()),
            // 软件曲线总开关（散热页那颗「软件曲线」）。false = 风扇交给 EC 固件调速
            "/api/fancurve/enabled" => FanEnabled(req, st),
            "/api/fancurve/config" => FanConfig(req, st),
            "/api/fancurve/mode-curve" => FanModeCurve(req, st),
            "/api/fancurve/ack-switch" => Ok(st.Fan.AckSwitch()),
            // 原厂三档曲线：只读预览 + 导入（不写注册表、不碰原厂 daemon）
            "/api/fancurve/oem-preview" => Ok(FanCurveModePresets.PreviewOem()),
            "/api/fancurve/import-oem" => Ok(FanCurveModePresets.ImportOem(st.Model, st.Fan)),
            // 把原厂三档曲线读回来（只读，不写注册表、不让原厂接管）
            "/api/hardware/refresh" => Ok(new JsonObject
            {
                ["ok"] = true,
                ["started"] = st.RefreshHardware(force: true),
            }),

            // ---- 进程类（都是"杀进程"，不可逆，只由用户点按钮触发）----
            // 三条 HWiNFO 的入口共用 `HwInfoProcess`：归属规则是「只碰我们自己拉起
            // 的那只」，只有 `restart` 例外 —— 它明确会杀掉全部实例（含用户手动开的）。
            "/api/vendor/stop" => Ok(st.StopVendor()),
            "/api/osd" => Osd(req, st),

            // ---- GPU 超频（NVML 时钟偏移；写需要管理员，没权限时如实报 4）----
            "/api/gpu/oc" => GpuOc(req, st),
            "/api/gpu/oc/reset" => Ok(st.ResetGpuOc()),
            "/api/hwinfo/restart" => Ok(st.RestartHwinfo()),
            "/api/hwinfo/foreign-sm" => Ok(st.EnableForeignSm()),
            "/api/debug/hwinfo/restart" => Ok(st.DebugHwinfoRestart()),
            // 诊断：当场 GC + 归还工作集（关窗 / 空闲会自动做，这条只用于实测与排障）
            "/api/debug/mem/trim" => Ok(st.TrimWorkingSetNow()),

            // ---- 重启计划（显卡模式写进 EC 后必须重启才生效）----
            "/api/system/reboot" => Reboot(req, st),
            "/api/system/reboot/cancel" => Ok(st.CancelReboot()),

            // ---- 退出 ----
            "/api/app/quit" => Quit(st, requestQuit),

            _ => null,
        };

    // ================================================================ 电源 / 风扇

    private static ApiResponse PowerPlan(ApiRequest req)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        var guid = Str(body["guid"]);
        return Ok(QControl.Hal.Sensors.PowerPlans.SetActive(guid));
    }

    /// <summary>
    /// 内建屏亮度（0~100）。走 WMI，**不走 EC、不需要管理员**（Windows 设置里的亮度滑块同一条路）。
    ///
    /// 为什么要这个口（2026-11-05）：切性能模式会切电源计划，而每条计划各自存着
    /// 「显示器亮度」索引（本机 平衡 75 / 办公 55 / 游戏 50 / 高性能 40）⇒ 一切档屏幕就跳。
    /// 这里的语义是「每档各自记住」：写亮度会同时更新**当前**那条计划的索引，
    /// 所以在这个档调到多少、切走再切回来还是多少。
    /// ⚠️ 回包里的 `level` 是**回读到的真实值**（驱动可能量化到最近档位），界面拿它渲染。
    /// </summary>
    private static ApiResponse DisplayBrightness(ApiRequest req)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        if (Num(body["level"]) is not { } level)
            return BadRequest("缺少 level（0~100）");

        return Ok(QControl.Hal.Sys.ScreenBrightness.Set((int)Math.Round(level)));
    }

    /// <summary>
    /// 锁定 Win 键。`body: {locked: true|false}`。
    ///
    /// 拦的是左右 Win 键的按下与抬起（低级键盘钩子，见 <see cref="Input.WinKeyLock"/>）：
    /// 按下被吃掉，Win+D / Win+E / Win+Tab 这些组合自然全都不会触发。
    /// 安全桌面（Ctrl+Alt+Del）拦不住 —— 那也不该拦。
    ///
    /// ⚠️ 这是**进程内**的功能：控制中心退出（含崩溃）后 Windows 立刻恢复原样 ——
    ///    正因为如此才不写注册表 Scancode Map，也就没有"卸了程序 Win 键还是坏的"这种坑。
    /// </summary>
    private static ApiResponse KeyboardWinLock(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        if (body["locked"] is not JsonValue lv || !lv.TryGetValue<bool>(out var locked))
            return BadRequest("缺少 locked（true / false）");

        return Ok(st.SetWinKeyLock(locked));
    }

    /// <summary>
    /// 软件曲线总开关。`body: {enabled: true|false}`。
    ///
    /// 这是"风扇由谁调"的唯一开关（2026-11-18 用户定）：
    ///   · false（出厂默认）= 风扇听 EC 固件，本程序**一个字节都不写** UFA；
    ///   · true = 软件曲线接管，按 CPU/GPU 曲线写转速，手动风扇控制也才可用。
    /// 语义与副作用（关掉会发交回命令：`0x0206=0` + 三路 UFA=`0xFF`，几秒内回到固件曲线；
    /// 交回失败时才停在原值）全在
    /// <see cref="QControl.Core.FanCurve.FanCurveEngine.SetEnabled"/> 里，这里只转参数。
    /// </summary>
    private static ApiResponse FanEnabled(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        if (body["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var on))
            return BadRequest("缺少 enabled（true / false）");

        var r = st.Fan.SetEnabled(on);
        // 「自定义风扇」关掉 = 用户明确把风扇交回固件：卡死自救那条"强制接管"
        // 也必须跟着收（它同样表现为 curve_enabled=false，只能靠这个标记区分，
        // 否则自愈会在 30 秒后把用户刚关掉的曲线又接回来）。
        if (on == false && r["ok"]?.GetValue<bool>() == true) st.ClearGuardForced();
        // 这颗开关是"谁在控风扇"的切换，必须留痕：日志里要能对上"几点几分交回/接管"，
        // 否则现场只剩一句"风扇自己变了"（用户排障就是拿时间戳对日志）。
        var ok = r["ok"]?.GetValue<bool>() == true;
        var tail = ok
            ? r["note"]?.GetValue<string>() ?? ""
            : "失败：" + (r["error"]?.GetValue<string>() ?? r.ToJsonString());
        Console.WriteLine($"[fancurve] 自定义风扇 → {(on ? "开" : "关")}：{tail}");
        return Ok(r);
    }

    /// <summary>
    /// manual ↔ auto。
    /// ⚠️ `auto` 不等于"交回 EC 自动"：EC 拿到非零 UFA 后不会接管，
    /// 所以"自动"永远由软件曲线顶上（见 FanCurveEngine.SetMode 的注释）。
    /// </summary>
    private static ApiResponse FanMode(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        var mode = Str(body["mode"]);
        if (mode is null) return BadRequest("缺少 mode（auto / manual）");
        return Ok(st.Fan.SetMode(mode));
    }

    // ================================================================ EC 直写

    /// <summary>高危命令的二次确认授权。`cmd` 接受 768 / "768" / "0x0300"。</summary>
    private static ApiResponse EcArm(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        if (Num(body["cmd"]) is not { } cmd || cmd is < 0 or > 0xFFFF)
            return BadRequest("非法 cmd（0~65535）");
        return Ok(st.Ec.Arm((ushort)cmd));
    }

    private static ApiResponse EcFan(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;

        // 手动风扇控制**必须先开软件曲线**（2026-11-18 用户定的规则）。
        // 曲线关着的时候 EC 里一个字节都没写过、固件正按自己的曲线调速，
        // 这里写一脚就是"偷偷接管"：转速被钉死在滑块值上，而且关掉程序也回不去。
        if (!st.Fan.CurveEnabled)
            return Ok(FailObj("手动风扇控制需要先开启「自定义风扇」（散热页卡头那颗开关）"));

        // 三个槽都可以单独给；缺省 = **只有我们记得上一笔写过它时**才沿用上一笔的值。
        // ⚠️ 一个都不给必须直接拒：`SetFans(null,null,null)` 会走 EcHal.Pick 的兜底
        //    （lo = 30%）⇒ 一个空 body 的请求就把控制权从固件抢走、还顺手把没点名的
        //    风扇钉到 30%。EC 拿到非零 UFA 就不再接管，这类"看起来成功"的写入最难排查。
        // ⚠️ 只给一路同理（2026-11-26 审查）：CPU / GPU 两路如果**从没写过**，
        //    `Pick` 同样给 min_duty —— "缺省不动那个槽"是错的，缺省是"钉住它"。
        //    所以这两路必须要么给出值、要么我们确实记着上一次的值，否则 400。
        var cpuDuty = OptInt(body["cpu"]);
        var gpuDuty = OptInt(body["gpu"]);
        var sysDuty = OptInt(body["sys"]);
        if (cpuDuty is null && gpuDuty is null && sysDuty is null)
            return BadRequest("至少要给一路占空比（cpu / gpu / sys）");

        if (cpuDuty is null && st.Ec.AppliedDutyAt(0) is null)
            return BadRequest("缺 cpu：这一路没有记录过目标值，写下去会把它钉在最低转速；"
                              + "请把 cpu / gpu 一起给（sys 可以单独给）");
        if (gpuDuty is null && st.Ec.AppliedDutyAt(1) is null)
            return BadRequest("缺 gpu：这一路没有记录过目标值，写下去会把它钉在最低转速；"
                              + "请把 cpu / gpu 一起给（sys 可以单独给）");

        return Guarded(() => st.Ec.SetFans(cpuDuty, gpuDuty, sysDuty), out _);
    }

    /// <summary>
    /// 「最大转速」——**一颗按钮，两种语义**（2026-11-25 用户定）：
    ///
    /// <list type="bullet">
    /// <item><b>自定义风扇开着</b>：跟她原来一样，切手动模式 + UFA=100。
    /// 之后由手动/曲线保持，界面滑块跳 100。</item>
    /// <item><b>自定义风扇关着</b>（风扇在固件手里）：临时接管拉满，**不改曲线开关、
    /// 不改 `presets.fan.mode`、不动滑块**；再点一次走 `ReleaseToEc`
    /// （`0x0206=0` + UFA=`0xFF`）交回固件。</item>
    /// </list>
    ///
    /// <para>
    /// 为什么"关着"这一半也非得接管：2026-11-25 本机实测，固件模式下
    /// 只写 `0x0205=0xFF` 转速完全不动、只写 `0x0205=100` 冲 8 秒就回落，
    /// 只有 `0x0206=1` + UFA 才持久满转（详见 <c>EcHal.SetOemFanMax</c> 的实测表）。
    /// 也就是说"不接管就拉不到满转"是个硬件事实，不是实现偷懒。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 曲线开着时这里**不再接受 body**（不需要 cpu/gpu 参数）：满转没有第二种取值，
    /// 收一个 80 反而会让人以为"最大转速可以调"。
    /// </para>
    /// </summary>
    private static ApiResponse EcFanMax(AppState st)
    {
        // **先认状态**：临时满转还在 ⇒ 这一下就是「恢复自动」，不管曲线开关后来变成了什么。
        // 为什么要排在曲线开关前面（2026-11-25 审查）：曲线"开着但没接上"（非管理员 / 写入被拒 /
        // 刚开启还没写第一笔）时，EC 里仍是这一半钉的 100，若先按曲线分支走，用户点
        // 「恢复自动」会被当成"手动满转"再写一次 100 —— 越点越满，永远回不去。
        if (st.Ec.OemFanMax)
        {
            return Guarded(() =>
            {
                var r = st.Ec.ReleaseToEc();
                r["mode"] = "oem-released";
                return r;
            }, out _);
        }

        if (st.Fan.CurveEnabled)
        {
            // 手动模式下写入，不会被曲线几秒内盖回去（与界面原来的 EnsureManualAsync 同序）
            if (st.Fan.FanMode != "manual")
            {
                var m = st.Fan.SetMode("manual");
                if (m["ok"]?.GetValue<bool>() != true) return Ok(m);
            }
            return Guarded(() =>
            {
                var r = st.Ec.SetFans(100, 100);
                r["mode"] = "manual-max";
                return r;
            }, out _);
        }

        return Guarded(() => st.Ec.SetOemFanMax(), out _);
    }

    /// <summary>
    /// 背光。两套形态：
    ///   · `zones: [{addr, rgb:[r,g,b]}]` —— 三分区染色（优先）
    ///   · `level` + `r/g/b` + 可选 `addr` / `effect` —— 整条灯带
    /// 写成功后把用户选的颜色/地址/灯效记进 `presets.backlight`，重启后仍生效。
    /// </summary>
    private static ApiResponse EcBacklight(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;

        // ⚠️ 缺 `level` **绝不能当成 0**：0 = 关灯，而且紧接着的 MirrorBacklightProfile
        //    会把这个 0 永久写进该档档案 ⇒ 以后每次切到这档都是黑的（只能手动再拖亮度）。
        //    文档里 `zones` 形态本来就不要求 level，脚本按文档发一次就踩。
        //    没给 ⇒ 沿用 EC 当前亮度；但当前是 0（灯正关着，可能只是自动熄灯）也不算，
        //    退到 2（高亮）—— 否则"改个颜色"会把关灯状态一起固化进档案。
        var level = Num(body["level"]) is { } lv
            ? (int)Math.Round(lv)
            : st.EcBacklightLevelNow() is { } now and > 0 ? now : 2;
        var effect = OptInt(body["effect"]);

        // ⚠️ 编辑的不是「键盘上现在这一档」⇒ **只记档案，EC 一个字节都不写**。
        //    2026-11-19 用户报的就是这个：在背光页选「游戏」chip 调色、系统还在「办公」，
        //    键盘当场变色了。切到那一档时由按模式重放负责下发（AppState.BacklightFollow）。
        if (BacklightModes.Key(Str(body["mode"])) is { } editMode && !st.IsLiveBacklightMode(editMode))
            return Ok(StoreBacklightForModeOnly(st, body, editMode, effect));

        if (body["zones"] is JsonArray zarr && zarr.Count > 0)
        {
            var zones = new List<(int Addr, int[] Rgb)>();
            foreach (var z in zarr)
            {
                if (z is not JsonObject zo) continue;
                if (Num(zo["addr"]) is not { } addr) continue;
                var rgb = (zo["rgb"] as JsonArray)?.Select(x => (int)(Num(x) ?? 0)).ToArray()
                          ?? [255, 255, 255];
                zones.Add(((int)addr, rgb));
            }
            if (zones.Count == 0) return BadRequest("zones 里没有合法的 {addr, rgb}");

            // 界面日常调色：**只写颜色真的变了的区**（一次单区调色 = 1 条命令 ≈ 46ms，
            // 直接变；写三条会让 EC 重刷三次 LED 引擎，肉眼看到"闪几下"）。
            // `force: true` = 全写：恢复默认 / 全部同色 / 采集探针这类"要绝对对齐"的动作，
            // 以及在别的进程动过灯之后手动刷一遍。
            var force = body["force"] is JsonValue fv && fv.TryGetValue<bool>(out var fb) && fb;

            // 独立灯珠（当前只有猎刃16 的 Logo；通用代码保留给将来真有尾灯的机型）：只有机型档案声明了才存在。
            // 显式带了颜色就写；force（灯效/亮度变化、恢复默认）则用已存的颜色一起重写，
            // 否则改了 effect/level 后只有键盘变、独立灯珠还停在旧灯效上。
            var extraBody = new JsonObject();
            foreach (var name in st.Current.ExtraLightAddrs.Keys)
            {
                if (body[name] is JsonArray given) extraBody[name] = given.DeepClone();
                else if (force && st.Model.Get("backlight", name) is JsonArray saved)
                    extraBody[name] = saved.DeepClone();
            }

            var res2 = Guarded(() =>
            {
                var z = st.Ec.SetBacklightZones(
                    level, zones, effect, skipUnchanged: !force);
                st.ApplyExtraLights(extraBody, level, effect);
                // 我们自己写了 EC ⇒ 接下来一两拍的亮度变化不认成 FN 键（见 BacklightLevelWatch）
                st.MarkBacklightEcWrite();
                return z;
            }, out var zout);
            if (OkOf(zout))
            {
                // ⚠️ 重建 zones 的规矩（两个坑都踩过）：
                //   ① **保留 name**（A/B/C）—— 旧实现每下发一次颜色就丢掉 name；
                //   ② **以旧表打底**，只改请求里点名的地址。以前是"拿请求的 zones 整表重建"，
                //      于是只发一个地址就把三区配置砍成一区 —— 2026-11-14 采集事故是这个症状，
                //      2026-11-15 我自己用探针发单区请求又踩了一次。
                //   合并规则在 BacklightZones.Apply（纯函数，有单测）。
                //   ③ **追加受机型声明的分区数约束**：表已经装满（`caps.kb_zones`）时，
                //      请求里没见过的地址一律忽略 —— 否则一次探针试色就能把三区表撑成五区。
                var old = st.Model.Get("backlight", "zones") as JsonArray;
                st.Model.Set(["backlight", "zones"],
                    BacklightZones.Apply(old, zones, BacklightZones.Sanitize(st.Current.KbZones)));
                if (effect is not null) st.Model.Set(["backlight", "effect"], JsonValue.Create(effect.Value));
                // 独立灯珠的颜色也落盘，才不会被分区表裁剪逻辑当成脏数据清掉
                foreach (var (name, node) in extraBody)
                    st.Model.Set(["backlight", name], node?.DeepClone());
                st.Model.Save();
                // 用户亲自写了一次背光 ⇒ 作废"自动熄灯"的状态与在飞的熄灯/恢复写入
                // （否则下一次有输入时会把用户刚点亮的灯按旧档位再写一遍）
                st.NotifyBacklightUserWrite();
                MirrorBacklightProfile(st, body, level);
                // 同一套颜色同步进原厂 LED 注册表：原厂 daemon 开机按那边的值重放灯效，
                // 不镜像的话下一次它启动就把刚调的颜色盖回注册表里的旧色（本机是纯红）。
                st.MirrorOemLedAll(st.LiveBacklightKey());
            }
            return res2;
        }

        var res = Guarded(() =>
        {
            var r = st.Ec.SetBacklight(
                level,
                (int)(Num(body["r"]) ?? 255),
                (int)(Num(body["g"]) ?? 255),
                (int)(Num(body["b"]) ?? 255),
                OptInt(body["addr"]) ?? OptInt(body["brightness"]),
                effect);
            st.MarkBacklightEcWrite();
            return r;
        }, out var bout);

        if (OkOf(bout))
        {
            // 用**返回**里的 rgb：那是钳制后的值，直接存配置才不会把越界值写进去
            if (bout!["rgb"] is JsonArray rgb && rgb.Count == 3)
                st.Model.Set(["backlight", "rgb"], rgb.DeepClone());
            if (effect is not null) st.Model.Set(["backlight", "effect"], JsonValue.Create(effect.Value));
            if (OptInt(body["addr"]) is { } addr) st.Model.Set(["backlight", "addr"], JsonValue.Create(addr));
            st.Model.Save();
            st.NotifyBacklightUserWrite();
            MirrorBacklightProfile(st, body, level);
            st.MirrorOemLedAll(st.LiveBacklightKey());
        }
        return res;
    }

    /// <summary>
    /// 只把这一笔改动记进「某一档」的档案 —— **不写 EC、不碰 `presets.backlight`**。
    ///
    /// 键盘上现在是别的档，当场下发就是"改了 A 档却动了当前这盏灯"；这一步只落盘，
    /// 等用户真的切到那一档，按模式重放会把这份档案写到 EC（见 `AppState.ScheduleBacklightApply`）。
    /// 以该档**原有档案打底**，只覆盖请求里点名的项：分区 / 灯效 / 亮度 / 独立灯珠 ——
    /// 否则"只改了个 Logo 颜色"会把这一档的键盘配色抹掉。
    /// </summary>
    private static JsonObject StoreBacklightForModeOnly(AppState st, JsonObject body, string mode,
                                                        int? effect)
    {
        var maxZones = BacklightZones.Sanitize(st.Current.KbZones);
        var existing = BacklightModes.ForMode(st.Model.Presets, mode, maxZones);

        var draft = existing is null ? new JsonObject() : BacklightModes.ToJson(existing);

        // ⚠️ **默认不吃请求里的 level**（2026-11-19）：界面亮度滑块的值来自 EC，也就是
        //    "键盘上现在这一档有多亮"，跟被编辑的那一档无关。以前照抄进来，在「办公」tab 上
        //    只调了个颜色就把办公档的亮度记忆改成了当前这档的亮度 —— 切回办公时亮度就"自己变"。
        //    那一档还没档案时，用它出生时的键盘亮度做初值（读不到就 100%）。
        //
        //    例外：界面在**非当前档**的 tab 上把滑块显示成"这一档档案里记的亮度"，用户拖它
        //    就是对这一档的明确设定 ⇒ 请求带 `level_explicit: true` 时才采信 body.level
        //    （不带的话仍是上面那条防串档规则）。
        var explicitLevel = body["level_explicit"] is JsonValue lev
                            && lev.TryGetValue<bool>(out var levb) && levb;
        if (BacklightModes.DraftLevel(
                explicitLevel,
                Num(body["level"]) is { } wantLevel ? (int)Math.Round(wantLevel) : null,
                existing is not null,
                st.EcBacklightLevelNow()) is { } draftLevel)
            draft["level"] = draftLevel;
        if (effect is { } eff) draft["effect"] = Math.Clamp(eff, 0, 15);

        if (body["zones"] is JsonArray zarr && zarr.Count > 0)
        {
            // ⚠️ 与 live 路径同一套规则：**以该档原有分区表打底，只覆盖请求点名的地址**。
            //    旧写法 `draft["zones"] = zarr.DeepClone()` 会整表替换 —— 在别的档只改一个区，
            //    这一档的三区就被砍成一区；切过去时其余区还留着上一档颜色（混色），
            //    而且以后每次切到这档都这样。
            var incoming = new List<(int Addr, int[] Rgb)>();
            foreach (var z in zarr)
            {
                if (z is not JsonObject zo || Num(zo["addr"]) is not { } za) continue;
                var rgb = (zo["rgb"] as JsonArray)?.Select(x => (int)(Num(x) ?? 255)).ToArray()
                          ?? [255, 255, 255];
                incoming.Add(((int)za, rgb));
            }
            if (incoming.Count > 0)
                draft["zones"] = BacklightZones.Apply(draft["zones"] as JsonArray, incoming, maxZones);
        }
        else if (Num(body["addr"]) is { } addr && Num(body["r"]) is { } r
                 && Num(body["g"]) is { } g && Num(body["b"]) is { } b)
            // 老形态（整条灯带单色）：一条分区也算档案
            draft["zones"] = new JsonArray(new JsonObject
            {
                ["addr"] = (int)addr,
                ["rgb"] = new JsonArray((int)r, (int)g, (int)b),
            });

        // 独立灯珠：带了就换，没带就保留这一档原来的（解析会沿用 draft 里的旧值）
        foreach (var name in st.Current.ExtraLightAddrs.Keys)
            if (body[name] is JsonArray given) draft[name] = given.DeepClone();

        var profile = BacklightModes.Parse(draft, maxZones);
        if (profile is null)
            return new JsonObject
            {
                ["ok"] = false,
                ["mode"] = mode,
                ["error"] = "这一档还没有分区信息，先在键盘上标一次色再存",
            };

        st.WriteProfileForMode(mode, profile);
        // 只记档案也要同步原厂那一档的键：否则 daemon 用旧值重放这一档时，
        // 用户在这个 tab 里设的颜色从来没出现在键盘上过。LEDModule 不动（现在显示的不是它）。
        st.MirrorOemLed(mode, setModule: false);

        Console.WriteLine($"[light] 背光页在「{BacklightModes.Label(mode)}」档调色（系统现在不在这一档）："
                          + "只记进这一档的档案，不下发键盘");

        return new JsonObject
        {
            ["ok"] = true,
            ["mode"] = mode,
            ["stored"] = mode,
            ["deferred"] = true,
            ["level"] = profile.Level,
            ["effect"] = profile.Effect,
        };
    }

    /// <summary>
    /// 把这次下发的背光**记进「某一档」的档案**（`presets.perf.modes[档].light`）。
    ///
    /// 按模式是常驻行为，每次有效下发都要记。目标档位的判定：
    ///   · 请求里带 `mode`（背光页切到某个档的 tab 在调色）⇒ 就记那一档；
    ///   · 没带（FN 热键 / 托盘 / 老接口）⇒ 记当前系统模式那一档（= 键盘上正在显示的这套）。
    ///
    /// 这样用户在「办公」档的页面里调色、而系统正在跑游戏档时，改的是**办公档的记忆**，
    /// 不会被下一次切档覆盖掉。读取用的是刚写进 `presets.backlight` 的整张分区表
    /// （不是请求里那几个地址）—— EC 里三个区现在就是这个样子。
    /// </summary>
    private static void MirrorBacklightProfile(AppState st, JsonObject body, int level)
    {
        // ⚠️ 没带 `mode` 时记的是**键盘上正在显示的那一套**（`LiveBacklightKey` = 过 ReplayKey），
        //    不是系统档：接管开着时系统档是 game、键盘上显示的是 custom，记进 game 会让
        //    "刚调的颜色"落进一个没在用的档案，而紧接着的 `MirrorOemLedAll` 又把 LEDModule
        //    指到 custom ⇒ daemon 下次重放的是 custom 的旧色，用户刚调的颜色白调。
        var mode = BacklightModes.Key(Str(body["mode"])) ?? st.LiveBacklightKey() ?? st.CurrentPerfKey();
        st.StoreProfileForMode(mode, level);
    }

    /// <summary>「自定义接管」开关（`body: {"on": true}`）。语义见 AppState.SetBacklightCustomOverride。</summary>
    private static ApiResponse BacklightCustomOverride(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        if (body["on"] is not JsonValue v || !v.TryGetValue<bool>(out var on))
            return BadRequest("缺少 on（true / false）");

        return Ok(st.SetBacklightCustomOverride(on));
    }

    /// <summary>
    /// 某一档的**自动熄灭策略**（`body: {"mode":"office","minutes":5,"on_lock":false}`）。
    ///
    /// 跟着四套灯效档案走：写进 `perf.modes[档].light` 的 `auto_off_min` / `auto_off_on_lock`，
    /// **不写 EC**（它只是策略，真正的熄灯由采样拍里的状态机做）。`mode` 缺省 = 当前这一档。
    /// 写别的档不影响现在的行为；写当前这一档才会立刻按新策略走（灯正黑着而改成"不熄"时当场恢复）。
    /// </summary>
    private static ApiResponse BacklightAutoOff(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        if (Num(body["minutes"]) is not { } raw) return BadRequest("缺少 minutes（0 = 不自动熄灭）");

        var onLock = body["on_lock"] is JsonNode n && Device.Truthy(n);
        return Ok(st.SetAutoOffForMode(Str(body["mode"]), (int)Math.Round(raw), onLock));
    }

    /// <summary>性能模式（高危，需先 arm；写成功记 `presets.perf.last_mode`）。</summary>
    private static ApiResponse EcPerf(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        // ⚠️ 缺 mode 曾经被当成 0，再由边界表夹到 1（办公）—— 一个畸形请求就真的切了档，
        //    还把 `last_mode` 记成"用户选了办公"。缺参数必须当场拒。
        if (Num(body["mode"]) is not { } want) return BadRequest("缺少 mode（1=办公 / 2=游戏 / 3=狂暴）");
        var mode = (int)Math.Round(want);

        var r = Guarded(() => st.Ec.SetPerfMode(mode), out var pout);

        // ⚠️ 落 `last_mode` 要用**返回里的 apmd**，不能拿 `mode` 去翻档位键：
        //    `mode` 是界面索引（1/2/3），而 APMD 的三档值是 0/1/2 —— 混用会整体错位一档
        //    （切「游戏」记成 office，切「狂暴」记成 game）。2026-10-31 实测抓到。
        // ⚠️ 取值走 Num()（按 JSON 数字解析），别用 `TryGetValue<uint>`：EcHal 塞进去的是
        //    **int**，JsonValue 的强类型读取不做有符号→无符号转换，会静默 false（同一天踩过）。
        if (OkOf(pout) && Num(pout?["apmd"]) is { } apmd
            && st.Ec.PerfModeKey((uint)apmd).Key is { } key)
        {
            // APMD 不跨重启保持 ⇒ last_mode 只由**用户动作**更新（这里就是）。
            // ⚠️ 它只作记录，启动时**不**按它回写 APMD（开机一个字节都不写）——
            //    见 HotkeyService.RememberPerfMode 的说明。
            st.Model.Set(["perf", "last_mode"], JsonValue.Create(key));
            st.Model.Save();
        }
        return r;
    }

    /// <summary>显卡模式（高危，需先 arm；写组 0xFB00，**重启后生效**）。</summary>
    private static ApiResponse EcGfx(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        // 同上：缺 mode 会被夹成 1（混合输出）并写进 EC —— 而那要重启才生效，
        // 用户只会觉得"我就发了个空请求，重启后显卡模式变了"。
        if (Num(body["mode"]) is not { } gfx) return BadRequest("缺少 mode（1=混合 / 2=独显直连 / 3=集显）");
        // 越界/小数同样拒绝：以前交给 Bounds.Clamp，0/9 会被改成 1/3 真写进 EC（重启后生效）。
        if (gfx != Math.Round(gfx) || gfx is < 1 or > 3)
            return BadRequest("mode 只接受整数 1（混合）/ 2（独显直连）/ 3（集显）");
        return Guarded(() => st.Ec.SetGfxMode((int)gfx), out _);
    }

    /// <summary>逆向调试：下发任意参数命令，看 EC 真实回包。</summary>
    private static ApiResponse EcRaw(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        // ⚠️ 必须卡范围：`(ushort)66053` 会静默回绕成 0x0205（风扇写）—— arm 并下发
        //    的就不是同一条命令，排障结论会完全错，调试口还能借此绕过 /api/ec/fan 的闸。
        if (Num(body["sub"]) is not { } sub || sub is < 0 or > 0xFFFF)
            return BadRequest("非法 sub（0~65535）");

        var args = new uint[8];
        if (body["args"] is JsonArray arr)
            for (var i = 0; i < 8 && i < arr.Count; i++)
                args[i] = (uint)(Num(arr[i]) ?? 0);

        var group = OptInt(body["group"]);
        var gap = Num(body["gap"]);

        // 方向：**显式 `read: true|false` 优先**（2026-11-22 加），否则按组号 / 档案只读表推。
        //
        // 为什么必须有这个开关：`0x0100`（键盘背光）**不在**档案 `read_only` 里 —— 它本来就是
        // 可写命令。于是 `{"sub":256}` 会被当成**写**，参数全 0 = 地址 0 / 全黑 / 亮度 0，
        // 一发就把键盘灯关掉（我 2026-11-22 就这么把自己的灯灭了，还以为是"读回全 0"）。
        // 光看回包看不出区别，因为写命令的回包本来就是"数据区不变"。
        var explicitRead = body["read"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) ? rb : (bool?)null;
        var overrideGroup = group is null ? (ushort?)null : (ushort)group.Value;
        var readGroup = st.Ec.Spec.ReadGroup;
        var isRead = explicitRead
                     ?? (overrideGroup is { } g
                         ? g == readGroup
                         : st.Ec.Spec.ReadOnly.Contains((ushort)sub));

        // 背光命令的"空参数写"一律拒绝：那是关灯，不是查询。
        if (!isRead && sub == 0x0100 && args.All(x => x == 0))
            return BadRequest(
                "0x0100 参数全 0 = 写入地址 0 / 全黑 / 亮度 0（= 直接关灯），拒绝下发。"
                + "调色用 /api/ec/backlight；真想读这一条请显式带 read:true（该命令读组行为未验证）。");

        // 槽位按名字填，字节槽越界会抛（旧实现是静默截断成 (byte)v）
        EcArgs parsed;
        try
        {
            parsed = EcArgs.FromSequence(args);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        var command = new EcCommand(
            (ushort)sub,
            isRead ? EcDirection.Read : EcDirection.Write,
            parsed,
            overrideGroup);

        return Guarded(() => st.Ec.Raw(command, gap), out _);
    }

    // ================================================================ 风扇曲线配置

    /// <summary>
    /// `body: {source, curve, bind_mode, temp_wall, mode_curves, mode, fixed_mode}`。
    ///
    /// ⚠️ 校验失败一律回 **200 + ok:false**（与早先实现一致），不是 400 ——
    ///    这是"业务拒绝"（曲线格式不对），不是"请求形状不对"。
    ///    界面据此弹"保存失败 + 原因"。
    /// </summary>
    private static ApiResponse FanConfig(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;

        if (!TryCurve(body["curve"], out var curve, out var cerr))
            return Ok(FailObj("曲线格式错误：" + cerr));

        // 2026-10-23 起按原厂拆成 CPU / GPU 两条曲线（见 FanCurveEngine 顶部注释）
        if (!TryCurve(body["curve_cpu"], out var curveCpu, out var cerrCpu))
            return Ok(FailObj("CPU 曲线格式错误：" + cerrCpu));
        if (!TryCurve(body["curve_gpu"], out var curveGpu, out var cerrGpu))
            return Ok(FailObj("GPU 曲线格式错误：" + cerrGpu));

        // 第三路（SYS）：空数组是**有语义**的 —— "这一路交回 EcHal 的兜底"，
        // 引擎据此删掉 curve_sys。所以必须分清「没带这个键」和「显式给了 []」：
        // TryCurve 对两者都返回空列表，直接传进去会让每次保存 CPU 曲线都顺手删掉 SYS 曲线。
        List<CurvePoint>? curveSys = null;
        if (body["curve_sys"] is not null)
        {
            if (!TryCurve(body["curve_sys"], out var parsedSys, out var cerrSys))
                return Ok(FailObj("SYS 曲线格式错误：" + cerrSys));
            curveSys = parsedSys;
        }

        Dictionary<string, IReadOnlyList<CurvePoint>>? mcs = null;
        if (body["mode_curves"] is { } mcn)
        {
            if (mcn is not JsonObject mo) return Ok(FailObj("mode_curves 必须是对象"));
            mcs = new Dictionary<string, IReadOnlyList<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in mo)
            {
                if (!TryCurve(v, out var cp, out var e2))
                    return Ok(FailObj($"{k} 曲线格式错误：{e2}"));
                mcs[k] = cp;
            }
        }

        Dictionary<string, IReadOnlyList<CurvePoint>>? mcsCpu = null;
        if (body["mode_curves_cpu"] is { } mcnCpu)
        {
            if (mcnCpu is not JsonObject moCpu) return Ok(FailObj("mode_curves_cpu 必须是对象"));
            mcsCpu = new Dictionary<string, IReadOnlyList<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in moCpu)
            {
                if (!TryCurve(v, out var cp, out var e2))
                    return Ok(FailObj($"{k} CPU 曲线格式错误：" + e2));
                mcsCpu[k] = cp;
            }
        }

        Dictionary<string, IReadOnlyList<CurvePoint>>? mcsGpu = null;
        if (body["mode_curves_gpu"] is { } mcnGpu)
        {
            if (mcnGpu is not JsonObject moGpu) return Ok(FailObj("mode_curves_gpu 必须是对象"));
            mcsGpu = new Dictionary<string, IReadOnlyList<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in moGpu)
            {
                if (!TryCurve(v, out var cp, out var e2))
                    return Ok(FailObj($"{k} GPU 曲线格式错误：" + e2));
                mcsGpu[k] = cp;
            }
        }

        Dictionary<string, IReadOnlyList<CurvePoint>>? mcsSys = null;
        if (body["mode_curves_sys"] is { } mcnSys)
        {
            if (mcnSys is not JsonObject moSys) return Ok(FailObj("mode_curves_sys 必须是对象"));
            mcsSys = new Dictionary<string, IReadOnlyList<CurvePoint>>(StringComparer.Ordinal);
            foreach (var (k, v) in moSys)
            {
                if (!TryCurve(v, out var cp, out var e2))
                    return Ok(FailObj($"{k} SYS 曲线格式错误：" + e2));
                mcsSys[k] = cp;
            }
        }

        var result = st.Fan.Update(
            source: Str(body["source"]),
            curve: curve,
            curveCpu: curveCpu,
            curveGpu: curveGpu,
            curveSys: curveSys,
            bindMode: body["bind_mode"] is JsonValue bv && bv.TryGetValue<bool>(out var bb) ? bb : null,
            tempWall: Num(body["temp_wall"]),
            modeCurves: mcs,
            modeCurvesCpu: mcsCpu,
            modeCurvesGpu: mcsGpu,
            modeCurvesSys: mcsSys,
            mode: Str(body["mode"]),
            fixedMode: Str(body["fixed_mode"]),
            // 界面松手那次保存带 true：曲线立刻生效，不等 6 秒节流（拖动途中不带）
            applyNow: body["apply_now"] is JsonValue av && av.TryGetValue<bool>(out var an) && an);

        return Ok(result);
    }

    /// <summary>取某档位曲线给界面编辑（只读：不碰全局 curve、不写 EC）。</summary>
    private static ApiResponse FanModeCurve(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        return Ok(st.Fan.EditCurve(Str(body["mode"])));
    }

    /// <summary>
    /// 解析 `[{t, d}, …]`。**严格**：任一点不是 {t,d} 数字就整体拒绝 ——
    /// 曲线形状错一个点，插值就会在那一档跳到错误的转速。
    /// </summary>
    private static bool TryCurve(JsonNode? n, out List<CurvePoint> pts, out string? error)
    {
        pts = [];
        error = null;
        if (n is null) return true;                 // 没给 = 不改这一项

        if (n is not JsonArray arr)
        {
            error = "曲线必须是 {t, d} 对象数组";
            return false;
        }

        foreach (var it in arr)
        {
            if (it is not JsonObject o || Num(o["t"]) is not { } t || Num(o["d"]) is not { } d)
            {
                error = "曲线点必须是 {t, d} 对象";
                return false;
            }
            pts.Add(new CurvePoint(t, (int)Math.Round(d, MidpointRounding.ToEven)));
        }
        return true;
    }

    private static JsonObject FailObj(string error) => new() { ["ok"] = false, ["error"] = error };

    /// <summary>`body: {autostart? start_mode? theme? osd? ec_enabled? vendor_*?}`（界面每次只发一个键）。</summary>
    private static ApiResponse SetAppSettings(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        return Ok(st.SetAppSettings(body));
    }

    /// <summary>在资源管理器里打开日志目录（报障时把文件拖出来发人）。只开目录，不删不改。</summary>
    private static ApiResponse AppLogOpen(AppState st)
    {
        try
        {
            var dir = st.LogDirectory;
            Directory.CreateDirectory(dir);
            // UseShellExecute：交给外壳按"目录"处理，别自己拼 explorer.exe 参数
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir)
            {
                UseShellExecute = true,
            });
            return Ok(new JsonObject { ["ok"] = true, ["dir"] = dir });
        }
        catch (Exception e)
        {
            return Ok(new JsonObject { ["ok"] = false, ["error"] = "打开日志目录失败：" + e.Message });
        }
    }

    /// <summary>
    /// `POST /api/app/log/clear`：清日志，**可选**顺带清采集包 / 机型档案。
    ///
    /// `body: {"packs": false, "profiles": false}` —— 缺省 / 非 true 都只清日志
    /// （默认绝不能动采集包与档案）。
    /// 两者都比日志"重"：采集包是要发给作者的交付物，档案删掉就没有本机采出来的
    /// 温度/转速偏移了（要重启才回落内置档案）。
    /// </summary>
    private static ApiResponse LogClear(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        var profiles = Flag(body, "profiles");
        var packs = Flag(body, "packs");
        return Ok(st.ClearLogs(profiles, packs));
    }

    /// <summary>body 里某个键是不是**明确的 true**（缺省 / 别的类型一律当 false）。</summary>
    private static bool Flag(JsonObject body, string key)
        => body[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// `POST /api/app/machine-report`：导出「机型适配报告」，或（`adopt:true`）做一次
    /// 「一键机型档案采集」—— 采一段时间的 EC 帧、自动标定偏移、**验证性能档/背光的写入**、
    /// 把档案写进本机用户档案目录。
    ///
    /// `body: {"save": true, "adopt": false, "probe": false, "seconds": 20, "observe": 0}`
    ///   · save=false   只把全文放进响应（界面复制到剪贴板用）；
    ///   · adopt=true   额外采样并把 `devices/&lt;id&gt;.json` 落到用户档案目录（默认 20 秒）；
    ///   · probe=true   额外做**写入验证**（性能档 APMD 与键盘背光，写后读回、写完恢复），
    ///                  默认 **false** —— 缺省值绝不能是"会往 EC 写"的那个（见下）；
    ///   · observe      观察原厂写命令的窗口（秒，0 = 不观察）：写验证之后暂停风扇曲线、
    ///                  高频只读抓原厂控制中心真实下发的 0xFB00 写命令。期间界面按"引导步骤"
    ///                  让用户在原厂里操作、每步点一次，程序按标记切窗把语义绑上并补 caps
    ///                  （见 `AdoptGuide`）；标记与逐段结论都会写进采集包的 observation 段；
    ///   · seconds      采样窗口（6~30 秒），只在 adopt 时有效。
    ///
    /// ⚠️ 2026-11-22 起**没有 `gfx` 字段了**：显卡模式程序验不了（切换要重启），原来靠
    ///    用户回答"原厂里有没有"来开 `caps.gfx_switch`，等于拿回答当证据 —— 现在按默认
    ///    能切处理（读数仍进包的 `gfx_probe` 段当佐证）。旧脚本多传一个 `gfx` 会被忽略。
    ///
    /// ⚠️ `probe` 缺省为 false 是刻意的：这条接口自称"只读采集"很久了，外部脚本/旧界面
    ///    都在用它。默认改成写会变成"我没点过任何东西，它自己动了硬件"。界面上的完整采集
    ///    会显式传 true（并且先弹一次确认）。
    /// ⚠️ 这是**慢**请求（报告 1~2 s + 采样 20 s + 写验证约 5 s），调用方必须放后台线程。
    ///    采到一半失败一律回 `ok:true` + 文本里的「采集说明」，让人仍然能提交拿到的那部分。
    /// </summary>
    private static ApiResponse ExportMachineReport(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        // 只有明确写 false 才不落盘：坏 JSON / 缺字段都按"要存文件"处理（用户点的是导出）
        var save = !(body["save"] is JsonValue sv
                     && sv.TryGetValue<bool>(out var b) && !b);

        var adopt = body["adopt"] is JsonValue av && av.TryGetValue<bool>(out var ab) && ab;
        if (!adopt) return Report(() => st.ExportMachineReport(save), save);

        var probe = body["probe"] is JsonValue pv && pv.TryGetValue<bool>(out var pb) && pb;
        var seconds = (int)Math.Round(Num(body["seconds"]) ?? MachineAdopt.DefaultSeconds);
        var observe = (int)Math.Round(Num(body["observe"]) ?? 0);

        // 同一时刻只允许一轮采集：整条链的状态（引导会话、写窗口、风扇曲线、负载线程）
        // 都是进程级单例，重叠会互相踩 —— 详见 AppState.Adopting 的注释。
        // 界面那层也有一个 busy 标志，但那只挡界面自己；脚本并发打进来要靠这里。
        if (!st.BeginAdopt())
            return Conflict("已经在采集了：等这一轮跑完（约 40 秒；带引导时更久）再来");
        try
        {
            return Report(() => st.ExportMachineProfile(seconds, probe, observe), save);
        }
        finally
        {
            st.EndAdopt();
        }
    }

    /// <summary>
    /// 采集/报告失败必须**变成一条能读的答复**，不能把连接裸关掉。
    ///
    /// 2026-11-08 真机踩到：某个异常在这里被 `MiniHttpServer` 的 `catch (IOException)`
    /// 当成"客户端断线"吞掉 ⇒ 外面看到的只是 `curl: (52) Empty reply from server`，
    /// 日志里一个字都没有，只能靠猜。报告是用户报障时唯一能寄出来的东西，
    /// 它失败的时刻恰恰是最需要看到原因的时刻。
    /// </summary>
    private static ApiResponse Report(Func<JsonObject> run, bool save)
    {
        try
        {
            return Ok(run());
        }
        catch (Exception e)
        {
            return ApiResponse.Json(
                new JsonObject
                {
                    ["ok"] = false,
                    ["save"] = save,
                    ["error"] = "采集失败：" + e.GetType().Name + "：" + e.Message,
                    // 栈只给最后三帧：够定位，又不至于把 500 响应当日志用
                    ["where"] = string.Join(" ← ", (e.StackTrace ?? "")
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim()).Take(3)),
                }, 500);
        }
    }

    /// <summary>
    /// `POST /api/history/export`：把落盘的历史采样导出成 CSV。
    ///
    /// `body: {"path"?: "...", "from"?: &lt;unix 秒&gt;, "to"?: &lt;unix 秒&gt;}`，整包可空。
    /// 不带 path ⇒ 落在历史目录下 `qcontrol-history-yyyyMMdd-HHmmss.csv`（脚本 / 命令行用）。
    /// 导出的是**文件**而不是响应体：保留期最长 7 天、十几万行，塞进 JSON 谁都不好处理。
    ///
    /// ⚠️ 一行都没有时回 `ok:false` 而不是"成功导出 0 行"：后者会在用户的文档目录里
    ///    留下一个只有表头的文件，第二天他会以为数据丢了。
    /// </summary>
    private static ApiResponse HistoryExport(ApiRequest req)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;

        var from = Num(body["from"]);
        var to = Num(body["to"]);
        if (from is { } lo && to is { } hi && lo > hi) return BadRequest("from 必须早于 to");

        var r = HistoryStore.Export(Str(body["path"]), from, to);

        return Ok(new JsonObject
        {
            ["ok"] = r.Ok,
            ["error"] = r.Ok ? null : r.Error,
            ["file"] = r.File,
            ["rows"] = r.Rows,
            ["bytes"] = r.Bytes,
            // from/to 只在有数据时才有意义；保留三位小数与 t 列同精度
            ["from"] = r.Ok ? Math.Round(r.From, 3) : null,
            ["to"] = r.Ok ? Math.Round(r.To, 3) : null,
        });
    }

    // ================================================================ 配置读写
    /// <summary>
    /// `body: {"path": ["fan","min_duty"], "value": 35}`。
    ///
    /// `path` 只有一级时 = **按节合并**（`{"path":["light"],"value":{…}}` 一次写多个键，
    /// 背光自动熄灭那条就是这么用的），`value` 必须是对象。
    ///
    /// ⚠️ 参数必须逐条校验。旧实现只要求"path 非空"，于是
    /// `POST {"path":["fan"],"value":1}` 会把 presets.fan **整节换成标量** ⇒
    /// 曲线 cfg 变成 int，`cfg.get()` 抛异常 ⇒ 曲线连续异常自停、风扇停在最后一个值，
    /// 只能"恢复出厂"才救得回来。
    /// </summary>
    private static ApiResponse ModelPreset(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;

        if (body["path"] is not JsonArray pathArr || pathArr.Count == 0
            || pathArr.Any(x => Str(x) is not { Length: > 0 }))
            return BadRequest("path 必须是非空的字符串数组");

        if (!body.ContainsKey("value")) return BadRequest("缺少 value");

        var path = pathArr.Select(x => Str(x)!).ToList();
        if (!PresetDefaults.HasSection(path[0]))
            return BadRequest("path 第一级必须是已知配置节：" +
                              string.Join(", ", PresetDefaults.Sections.OrderBy(x => x, StringComparer.Ordinal)));

        if (path.Count == 1 && body["value"] is not JsonObject)
            return BadRequest("按节写时 value 必须是对象");

        // ⚠️ 这几个键不是纯数据，改一下就要有人收尾（起停曲线线程、停/接风扇、
        //    清"强制接管"态）。从这里直写能造出界面表达不出来、也没人负责收尾的组合：
        //    比如 `fan.mode=manual` 而曲线还在跑（手动面板可用但风扇仍按曲线走），
        //    或者 `curve_enabled=true` 而引擎没启动（界面显示已开启、风扇还在固件手里）。
        //    所以一律拒，指到各自的专用端点（2026-11-26 审查）。
        if (SwitchPathError(path, body["value"]) is { } switchErr)
            return BadRequest(switchErr);

        var r = st.Model.Set(path, body["value"]);

        // ⚠️ 改了 `light` 节（背光自动熄灭策略）⇒ 状态机里"熄灯前记下的档位"作废。
        //    不重置的话：灯正黑着时用户改了策略，下一拍看到的还是 0 档位 ——
        //    既不会恢复（没状态了）也不会再熄，键盘就一直黑着。
        if (r.Ok && path[0] == "light") st.ResetAutoOffState();

        return Ok(r.ToJson());
    }

    /// <summary>
    /// `model/preset` 里那些**只能走专用端点**的键。它们不是纯数据：改一下就要有人收尾
    /// （起停曲线线程、停/接风扇、清"强制接管"态）。返回 null = 可以直写。
    /// </summary>
    private static string? SwitchPathError(IReadOnlyList<string> path, JsonNode? value)
    {
        static string? EndpointOf(string section, string key) => (section, key) switch
        {
            ("fan", "mode") => "POST /api/fan/mode",
            ("fan", "curve_enabled") or ("fan", "enabled") => "POST /api/fancurve/enabled",
            ("ec", "enabled") => "POST /api/app/settings {\"ec_enabled\": true|false}",
            _ => null,
        };

        if (path.Count == 2 && EndpointOf(path[0], path[1]) is { } ep)
            return $"{path[0]}.{path[1]} 不是纯数据：改它要起停风扇曲线 / 重接风扇，请用 {ep}";

        // 按节合并（`{"path":["fan"],"value":{…}}`）同样能夹带这些键
        if (path.Count == 1 && value is JsonObject patch)
        {
            foreach (var (key, _) in patch)
                if (EndpointOf(path[0], key) is { } ep2)
                    return $"{path[0]}.{key} 不是纯数据：改它要起停风扇曲线 / 重接风扇，请用 {ep2}";
        }
        return null;
    }

    /// <summary>恢复出厂（可按 section）。恢复后必须把曲线重新对齐当前系统模式。</summary>
    private static ApiResponse ModelReset(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        var section = Str(body["section"]);
        var resetsFan = string.IsNullOrEmpty(section)
                        || string.Equals(section, "fan", StringComparison.OrdinalIgnoreCase);

        var r = string.IsNullOrEmpty(section) ? st.Model.Reset() : st.Model.ResetSection(section);

        // ⚠️ 重置会把 presets.fan.curve 换成**全局默认曲线**，而绑定开着时
        //    全局 curve 本该等于「当前模式」那条 —— 不对齐的话用户看到的还是旧曲线，
        //    得再切一次模式才"恢复"。
        try { st.Fan.Realign(); } catch { /* 对齐失败不该让恢复出厂失败 */ }

        // ⚠️ 真正的"出厂"= 原厂 UserFan1/2/3 里的曲线（见 NL5 实证），不是 presets.default.json
        //    里写死的近似值。整棵 reset 或只重置 fan 节时，必须把原厂三档曲线读回来写进
        //    perf.modes 档案 —— 否则用户看到的"恢复出厂"还是那三条写死的形状，三档可能全一样。
        //    只读原厂注册表、不写、不让原厂接管；失败不阻断 reset（非 NL5 机型没有那棵键）。
        JsonObject? oemCurves = null;
        if (resetsFan)
        {
            try
            {
                oemCurves = FanCurveModePresets.ImportOem(st.Model, st.Fan);
                r["oem_curves"] = oemCurves.DeepClone();
            }
            catch (Exception e)
            {
                r["oem_curves"] = new JsonObject { ["ok"] = false, ["error"] = e.Message };
            }
        }

        // 出厂默认 = 软件曲线**关**（风扇听 EC 固件）。循环本身不复查这个键，
        // 所以不能只靠"下次启动被拦"：这里必须把正在跑的那一轮停掉，
        // 否则配置说关、风扇还在按曲线跑（界面显示「已关闭」而转速在变）。
        if (resetsFan && !st.Fan.CurveEnabled && st.Fan.Running)
        {
            try { st.Fan.Stop(release: true); } catch { /* 停不下来也不该让恢复出厂失败 */ }
        }

        // 配置树换了，但 OSD 归属 / 日志句柄 / 自动记录 / 自动熄灯状态机都不读配置树
        // （各自只有设置页那一个入口）⇒ 必须按新值重放一遍，否则"恢复了出厂"只是
        // 磁盘上恢复了，进程还按旧设置跑，直到重启（2026-11-26 审查）。
        st.ReapplyRuntimeSwitches();

        return Ok(r);
    }

    // ================================================================ GPU 超频

    /// <summary>
    /// `body: {"core": 有效MHz, "mem": 有效MHz}`（两个都可省，省了就不动那一项）。
    ///
    /// 结果里每项分开报 `requested / raw / applied / error` —— 核心成功、显存失败时
    /// `ok:false` 但明细都在，绝不"发了一半还报成功"。写入后一律读回校验，
    /// 读回不一致会明确写"驱动接受但未生效"（被功耗/温度策略钳制时就是这样）。
    /// </summary>
    private static ApiResponse GpuOc(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;
        return Ok(st.ApplyGpuOc(body));
    }

    // ================================================================ OSD

    /// <summary>
    /// `body: {"kind": "backlight", "title": "键盘背光", "subtitle": "低亮"}`。
    ///
    /// 界面上点背光/性能时弹同一条提示，让"按 FN"和"点界面"的反馈一致
    /// （FN 那条路径由 WMI 监听线程自己触发，不经过这里）。
    ///
    /// `ok:false` 是**正常返回**：OSD 关着、窗口起不来、或者 1.5 秒内同内容
    /// 已经弹过（按一次 FN 会抛好几个事件，去重是必须的）。
    /// </summary>
    private static ApiResponse Osd(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;

        // 截断与早先实现一致：kind 16 / title 32 / subtitle 32 字符。
        // steps / level 两个字段照收但不再绘制（2026-10-13 去掉了档位方块条）。
        var kind = Cut(Str(body["kind"]), 16);
        var title = Cut(Str(body["title"]), 32);
        var subtitle = Cut(Str(body["subtitle"]), 32);

        return Ok(new JsonObject { ["ok"] = st.ShowOsd(kind, title, subtitle) });
    }

    private static string Cut(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // 按字符（不是字节）截，中文不会被切半
        return s.Length <= max ? s : s[..max];
    }

    // ================================================================ 重启计划

    /// <summary>
    /// `body: {"seconds": 60, "reason": "切换显卡模式", "mode": 2}`。
    /// `seconds = 0` 表示立刻（系统给的最短倒计时），上限 24 小时。
    ///
    /// 返回的是**计划后**的状态（`planned:true` + `remain`），界面拿它直接画倒计时条。
    /// `shutdown` 非 0 一律如实失败 —— 特别是"权限不足"和"已有其它关机进程"，
    /// 报成功会变成"界面说到点了会重启、机器其实不动"。
    /// </summary>
    private static ApiResponse Reboot(ApiRequest req, AppState st)
    {
        if (!TryBody(req, out var body, out var bad)) return bad!;

        // 缺 seconds 时给 60 秒：和界面默认一致，且留出"看到弹窗还能取消"的余量。
        // ⚠️ 必须**先按 double 判范围、再转 int**（2026-11-23 审查发现）：
        //    `(int)1e10` 在 x64 上溢出成 int.MinValue，再被 Clamp(0, 86400) 变成 0
        //    ⇒ `shutdown /r /t 0` 当场重启。把毫秒/时间戳当秒传是最常见的错法。
        var raw = Num(body["seconds"]) ?? 60;
        if (double.IsNaN(raw) || raw < 0 || raw > 86400)
            return BadRequest("seconds 必须是 0~86400 的秒数（0 = 立刻重启；不要传毫秒或时间戳）");
        var seconds = (int)Math.Round(raw);
        var reason = Str(body["reason"]) ?? "";
        var mode = Str(body["mode"]) ?? (Num(body["mode"]) is { } m ? m.ToString(CultureInfo.InvariantCulture) : null);

        return Ok(st.PlanReboot(seconds, reason, mode));
    }

    // ================================================================ 退出

    /// <summary>
    /// 先把风扇交回 EC 固件再退（`Stop(release:true)` → `EcHal.ReleaseToEc`：
    /// `0x0206=0` 清接管标志 + 三路 UFA=`0xFF`）。直接杀进程会让 EC 一直按最后的 UFA 转。
    /// </summary>
    private static ApiResponse Quit(AppState st, Action requestQuit)
    {
        var released = st.Fan.Stop(release: true);
        requestQuit();
        return Ok(new JsonObject
        {
            ["ok"] = true,
            ["released"] = released["released"]?.DeepClone(),
            ["stuck"] = released["stuck"]?.DeepClone(),
        });
    }

    // ================================================================ 工具

    private static ApiResponse Ok(JsonNode node) => new(200, ApiResponse.JsonContentType,
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(node, ApiResponse.JsonOptions)));

    /// <summary>
    /// 跑一次 EC 写操作。守卫失败时 EcHal **抛异常**（WriteDenied），HTTP 层要转成 JSON：
    /// 权限/开关类 → 403（不是"参数错"，是"你没被允许"），其余 → 200 + ok:false（带原因）。
    /// `result` 把原始响应带出来，调用方要拿返回里的 rgb / zones 落配置。
    /// </summary>
    private static ApiResponse Guarded(Func<JsonObject> action, out JsonObject? result)
    {
        result = null;
        try
        {
            result = action();
            return Ok(result);
        }
        catch (QControl.Hal.Ec.WriteDeniedException e)
        {
            return Forbidden(e.Message);
        }
        catch (Exception e)
        {
            return Ok(new JsonObject { ["ok"] = false, ["error"] = e.Message });
        }
    }

    private static ApiResponse Forbidden(string error)
        => new(403, ApiResponse.JsonContentType,
               Encoding.UTF8.GetBytes(
                   JsonSerializer.Serialize(new { ok = false, error }, ApiResponse.JsonOptions)));

    private static bool OkOf(JsonObject? o) => o?["ok"]?.GetValue<bool>() == true;

    private static ApiResponse BadRequest(string error)
        => new(400, ApiResponse.JsonContentType,
               Encoding.UTF8.GetBytes(
                   JsonSerializer.Serialize(new { ok = false, error }, ApiResponse.JsonOptions)));

    /// <summary>资源正忙（同一时刻只允许一个的动作）。</summary>
    private static ApiResponse Conflict(string error)
        => new(409, ApiResponse.JsonContentType,
               Encoding.UTF8.GetBytes(
                   JsonSerializer.Serialize(new { ok = false, error }, ApiResponse.JsonOptions)));

    /// <summary>解析请求体。空体 = 空对象；坏 JSON = 400（绝不按默认值执行动作）。</summary>
    private static bool TryBody(ApiRequest req, out JsonObject body, out ApiResponse? error)
    {
        body = new JsonObject();
        error = null;
        if (string.IsNullOrWhiteSpace(req.Body)) return true;
        try
        {
            if (JsonNode.Parse(req.Body) is JsonObject o)
            {
                body = o;
                return true;
            }
            error = BadRequest("请求体不是 JSON 对象");
            return false;
        }
        catch (Exception e)
        {
            error = BadRequest($"请求体不是合法 JSON：{e.Message}");
            return false;
        }
    }

    /// <summary>数值节点：数字或字符串（含 "0x0300"）。取不到给 null。</summary>
    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        var kind = v.GetValueKind();
        if (kind == JsonValueKind.Number)
            return double.TryParse(v.ToJsonString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var d) ? d : null;
        if (kind != JsonValueKind.String) return null;

        var s = v.GetValue<string>().Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)
                ? h : null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) ? dd : null;
    }

    private static int? OptInt(JsonNode? n) => Num(n) is { } v ? (int)v : null;

    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
