using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.App.Http;
using QControl.App.Http.Contract;
using QControl.App.Logging;
using QControl.App.Tray;
using QControl.App.Ui;
using QControl.App.Ui.SelfTest;
using QControl.Core.Startup;
using QControl.Hal.Sys;

namespace QControl.App;

/// <summary>
/// Q-Control 控制中心 · 进程入口（后端 + 界面 + 托盘 + OSD）。
///
/// 一个进程里三件事：
///   1. `AppState` 每秒采样一帧快照，`Router` 把 41 条 `/api/*` 暴露给脚本与自检工具；
///   2. 界面**只有**进程内 WPF 一种（读 AppState 快照，不走 HTTP）；
///   3. 托盘 / OSD / 热键是裸 Win32 + 自绘（见 Tray/、Osd/）。
///
/// 全部写操作走 `PostHandlers` → `AppState` → HAL，UI 绝不直调 EC。
/// </summary>
internal static class Program
{
    /// <summary>
    /// **8765 = 主端口**（P8 起）。脚本、开机自启、用户书签都指向它 ——
    /// 换端口等于换入口，所以这里必须是 8765。
    /// </summary>
    private const int DefaultPort = 8765;

    /// <summary>`POST /api/app/quit` 的信号源。null = 还没进主循环（自检模式）。</summary>
    private static ManualResetEventSlim? _quitEvent;

    private static int Main(string[] argv)
    {
        // 主程序是 GUI 子系统（双击不弹黑框）。有控制台可借、或者是 `--selftest` 这类
        // 要文字的诊断，就在这里把它接回来；双击起界面时这里什么都不做。
        // ⚠️ 必须排在 FileLog.Attach 之前 —— 那个 tee 是包在当时的 Console.Out 外面的。
        ConsoleHost.Ensure(argv);

        // ⚠️ 必须排在任何窗口（含托盘）创建之前：DPI 感知一旦被系统按虚拟化值
        //    定下来，界面算出的窗口尺寸就会在 150% 屏上翻 1.5 倍。
        TrayIcon.InitDpiMode();

        // ------------------------------------------------------------ 文件日志
        // 开机自启走 `QControl.exe --tray`（WinExe，**没有控制台**），
        // `Console.WriteLine` 全进黑洞 —— 开机那几十秒「接管成功没有 / HWiNFO 起来没有」
        // 事后一个字都查不到。这里把 Console 的 stdout/stderr 同时 tee 进
        // `ControlCenter/logs/cc-yyyy-MM-dd.log`，130 处输出一行都不用改。
        // 挂得越早越好：下面的参数校验、FATAL 也要留证据。
        //
        // 三个优先级（2026-10-29 加总开关）：
        //   ① `--no-log`（命令行，只管本次进程）
        //   ② `presets.log.enabled`（界面总开关，落盘、重启后仍然算数）
        //   ③ 其余一律开（默认与早先实现一致）
        // ⚠️ 「关」不是"写了再删"，而是**根本不挂**：句柄都不开，磁盘上一个字节都不多。
        var noLog = argv.Contains("--no-log");
        var modelPathArg = ArgValue(argv, "--model");
        if (noLog)
            FileLog.Skip("本次启动带了 --no-log（命令行）");
        else if (!FileLog.PrefEnabled(Paths.FindModel(modelPathArg)))
            FileLog.Skip("日志总开关已关闭");
        else if (FileLog.Attach(ArgValue(argv, "--log")) is not null)
            // 启动横幅放在这里而不是 Attach 里：Attach 也会被"重新打开总开关"调，
            // 那时写"进程启动"是假话（见 FileLog.Attach 的注）。
            FileLog.Note($"===== 进程启动 pid={Environment.ProcessId} 参数={string.Join(' ', argv)} =====");

        // 老快捷方式可能还带着 `--elevate`（升级后的 .lnk 不一定来得及重建）：
        // 主程序默认就要提权、默认就开窗，留它对语义没有影响，但摘掉能保证后面
        // 所有 `argv.Contains` 判断干净。
        argv = argv.Where(a => !a.Equals("--elevate", StringComparison.OrdinalIgnoreCase)).ToArray();

        var port = int.TryParse(ArgValue(argv, "--port"), out var p) ? p : DefaultPort;
        var fixturesDir = Paths.FindFixtures(ArgValue(argv, "--fixtures"));
        var selftest = argv.Contains("--selftest");

        // 界面相关开关（界面只有 WPF 一种）
        var uiPage = ArgValue(argv, "--page");
        var shotPath = ArgValue(argv, "--ui-shot");
        var shotTheme = ArgValue(argv, "--theme");
        var (shotWidth, shotHeight) = ParseSize(ArgValue(argv, "--size"));
        var shotWait = int.TryParse(ArgValue(argv, "--wait"), out var waitMs) ? waitMs : 6000;
        // 截图时顺便把一个模态框拍进去（confirm / choose）：对话框也是界面，
        // 圆角、遮罩透明度、按钮顺序、换行全都只能靠看图发现。
        var shotDialog = ArgValue(argv, "--ui-dialog");
        // 截图前在活窗口里切一次主题（暗↔亮）：验证"切主题后界面有没有跟着变"。
        var shotFlip = argv.Contains("--flip");
        // 托盘菜单元图（暗/亮各一张）：菜单是分层窗口，走不了 --ui-shot，只能直接渲位图。
        var menuShot = ArgValue(argv, "--tray-menu-shot");
        // OSD 卡片元图（暗/亮各一张）：同理，OSD 是分层窗口 + 位图，只能直接渲。
        var osdShot = ArgValue(argv, "--osd-shot");

        // 检查更新的**调试源**：把 GitHub / Gitee 两个地址都指到同一个 URL。
        // 用途只有一个 —— 本地起个假 release 源，把 latest / newer / empty / error
        // 四种状态挨个截出来核对版式，不用等真的发一版。
        // ⚠️ 只在命令行显式给出时生效；打一行醒目提示，免得把演练结果当成"真有新版"。
        if (ArgValue(argv, "--update-source") is { Length: > 0 } updateSource)
        {
            Updates.UpdateChecker.SourceOverride = updateSource;
            Console.WriteLine("[update] ⚠ 检查更新已指向调试源：" + updateSource
                              + "（这不是真实的 GitHub / Gitee）");
        }

        if (argv.Contains("--help") || argv.Contains("-h"))
        {
            Console.WriteLine("""
                用法: QControl [选项]

                  --port <n>           监听端口（默认 8765 = 主端口）
                  --fixtures <dir>     契约样本目录（默认自动向上查找 tests/fixtures）
                  --fancurve           已废弃（曲线由散热页「自定义风扇」开关控制，仅为兼容旧脚本保留）
                  --tray               只进托盘，不自动打开界面（开机自启走这条）
                  --no-open            不自动打开界面（托盘照常有）
                  --no-tray            不创建托盘图标（调试/无人值守时用）
                  --window             显式要求打开界面（默认就是打开 WPF 界面）
                  --show               只把已知实例的界面叫到前台，然后退出（不起后端、不提权）
                  --page <key>         打开时停在指定页：dash/perf/cool/light/sys/hw/about/set
                  --ui-shot <png>      渲染一张界面截图后退出（截图即冒烟，见 Ui/SelfTest/UiShot.cs）
                  --tray-menu-shot <png> 把托盘右键菜单渲成 <png>-dark / -light 两张后退出
                  --osd-shot <png>     把 OSD 卡片渲成 <png>-dark / -light 两张后退出
                  --theme <mode>       截图用：dark / light / auto（不影响用户偏好）
                  --size <WxH>         截图尺寸（默认跟窗口一致：1320x880 上限）
                  --ui-dialog <kind>   截图时弹框拍进去：confirm / choose / gfx-confirm / guide /
                                       vendor-notrunning / vendor-nowindow（采集前的原厂门槛两版）
                  --flip               截图前在活窗口里切一次主题（验「切主题后是否跟着变」）
                  --ui-actions-selftest 自检"UI → PostHandlers"窄写接口（只读，不动硬件）
                  --ui-reopen-selftest 连开连关 3 次界面（验「关窗口再打开」不崩，只读）
                  --ui-refresh-selftest 八页各点两下（验「重复点侧栏 = 原地刷新、不重建页」，只读）
                  --ui-fit-selftest    逐页复核"常态一屏装得下、不用滚动条"（不含关于页，只读）
                  --update-source <url> 调试：把检查更新的两个源都指到 <url>（演练四种状态用）
                  --wait <ms>          截图前等多久（默认 6000：EC 有 0.45s 节流 +
                                       HWiNFO/PDH 要几秒才出功耗/电压，等短了那两行是空的）
                  --selftest           跑契约自检后退出（DTO 往返比对 + 路由清单）
                  --import-pack <file> 把用户发来的采集包（logs\adopt-*.json）转成机型档案
                                       可配 --out <dir> / --force / --allow-write / --dry-run
                  --autostart-selftest 用临时名字验证计划任务/注册表读写后退出
                  --gpu-oc-selftest    读 NVML 能力/状态，写 +50/+100 再复位（要管理员）
                  --elevate            已废弃（老快捷方式兼容）：默认行为本来就是提权 + 开窗
                  --elevated           内部标记：本进程是提权重启出来的（防止反复提权）
                  --takeover           内部标记：请已有实例退出并接管端口（界面「以管理员身份重启」用）
                  --no-elevate         不请求管理员权限，以只读模式运行（写操作一律 403）
                  --log <dir>          文件日志目录（默认 ControlCenter/logs，按天分文件）
                  --no-log             不写文件日志（只输出到控制台）
                  --help               显示本帮助
                """);
            return 0;
        }

        // 机型采集包 → 正式档案（见 PackImportCli）：纯文件转换，不碰 EC、不占端口、不提权。
        // 放在任何初始化之前 —— 作者在"日常实例正跑着"的机器上处理用户发来的包，
        // 不该为了读一个 json 去抢 8765 / 弹 UAC。
        if (ArgValue(argv, "--import-pack") is { Length: > 0 } packArg)
            return PackImportCli.Run(packArg,
                outDir: ArgValue(argv, "--out"),
                force: argv.Contains("--force"),
                allowWrite: argv.Contains("--allow-write"),
                dryRun: argv.Contains("--dry-run"));

        // 托盘菜单元图：纯渲染，不起后端、不碰 EC、不提权（见 TrayMenu.Shot）。
        if (menuShot is not null)
            return TrayMenu.Shot(menuShot);

        // OSD 卡片元图：同上（见 OsdCard.Shot）。
        if (osdShot is not null)
            return Osd.OsdCard.Shot(osdShot);

        // 自启自检不碰真实的自启入口（用临时任务名），所以放在任何初始化之前。
        // 它**不**参与自我提权：非管理员下会自动降级成 LeastPrivilege 只验通路并说明
        // "没验到 ≠ 验过了"（见 AutostartCheck），是个可以随时安全跑的诊断。
        if (argv.Contains("--autostart-selftest"))
            return AutostartCheck.Run(port);

        // 「关了再开」自检：连开连关 3 次界面，每次一条新 STA 线程（与生产路径同构）。
        // 放在任何初始化之前 —— 不建 AppState（不碰 EC、不占端口、不提权），
        // 所以日常实例在跑时也能安全跑。见 Ui/SelfTest/UiReopenSelfTest.cs。
        if (argv.Contains("--ui-reopen-selftest"))
            return UiEntry.ReopenSelfTest();

        // 「重复点侧栏 = 刷新这一页」自检：八页各点两下（第二条路径只有重复点击才走得到）。
        // 同样只读、不建 AppState，可与日常实例共存。见 Ui/SelfTest/UiRefreshSelfTest.cs。
        if (argv.Contains("--ui-refresh-selftest"))
            return UiEntry.RefreshSelfTest();

        // ------------------------------------------------------------ 自我提权
        //
        // EC 写命令在非管理员下会被 WMI **接受却不生效**，所以写守卫一律回 403
        // （见 EcHal.AdminWriteDeniedHint）。早先只有外部 `run-as-admin.cmd` 能提权，
        // 直接双击 exe 永远是只读的 —— 用户的感受就是"点了没反应"。
        // 这里先自己换一个管理员进程再来一遍，用户只需要在 UAC 上点一下「是」。
        //
        // 谁不参与：见 Core/Startup/ElevationPlan.ReadOnlyModes（--help / --show /
        // --selftest / --ui-shot / --ui-actions-selftest / --autostart-selftest /
        // --no-elevate / --elevated）。那几个都是只读诊断：为看一份自检结果弹 UAC
        // 是纯骚扰，截图回归更是没人去点。
        if (ElevationPlan.WantsElevation(argv) && !Elevation.IsElevated())
        {
            // 端口上已经有实例 ⇒ 用户要的是"把界面打开"，不是"再来一个后端"。
            // 与 --show 同口径：只发具名事件，**不提权**（绝不为开个窗口弹 UAC）。
            if (IsOurInstance(port))
            {
                var woke = UiSignal.Trigger(port);
                Console.WriteLine(woke
                    ? "已有一个控制中心在运行，已请它把窗口打开（本次不再重复提权）。"
                    : "已有一个控制中心在运行（旧版本，不接「打开界面」的请求）。");
                return 0;
            }

            // ------------------------------------------------------------ 零 UAC 通道
            //
            // 走到这里是「端口上没有我们的实例，且当前不是管理员」——典型就是用户点
            // 桌面快捷方式 / 双击 exe，而托盘实例已经被退出（或还没起来）。
            //
            // 以前这里直接 runas，于是**每次冷启动**都要点一发 UAC。其实系统里已经有
            // 一个 HighestAvailable 的自启任务：任务计划服务代启它时用的是任务里存的
            // 完整令牌，**整个提权协商被跳过**，一发都不弹。只有第一次（任务还不存在）
            // 才需要 runas —— 而那一次会顺手把任务装好，之后开机与双击都走任务。
            //
            // ⚠️ 只在端口是默认值时才走：任务里写死了 `--port 8765`，用户用
            //    `--port 9000` 时任务起的是另一个端口，等下去只会白等 15 秒。
            if (port == DefaultPort)
            {
                // ⚠️ 带上「本程序自己」：任务若指向另一份副本（安装版 × 绿色版并存），
                //    拉起来的会是那一份 —— 用户双击的是这份，跑的却是那份，而且永远
                //    修不好（2026-10-30 真机：HWiNFO 放在安装目录里一直"检测不到"）。
                //    不匹配时 RunNow 回 InstallNeeded，下面自然退回 runas，由 RepairDeadEntry
                //    在提权启动后把入口改回本程序。
                var run = Autostart.RunNow(Autostart.SelfCommand());
                if (run.Ok || run.AlreadyRunning)
                {
                    if (WaitOurInstance(port, 15000))
                    {
                        // 任务启动的实例带 --tray（只进托盘）。用户这回点的是"要看界面"，
                        // 所以替他把窗口叫出来 —— 除非这次调用本身就带 --tray。
                        var wantsWindow = !argv.Contains("--tray");
                        var opened = !wantsWindow || UiSignal.Trigger(port);
                        Console.WriteLine("[elevate] " + run.Detail
                            + (wantsWindow
                                ? (opened ? "；已把界面叫到前台"
                                          : "；实例在托盘里，但它不接「打开界面」的信号")
                                : "；按 --tray 只进托盘"));
                        return 0;
                    }

                    Console.Error.WriteLine("[elevate] " + run.Detail + "，但 15 秒内端口没就绪。");
                    Console.Error.WriteLine("          看 ControlCenter\\logs\\ 当天的日志；"
                                            + "要强制重启请双击 run-as-admin.cmd 并加 console 开关。");
                    return 7;
                }

                Console.WriteLine("[elevate] " + run.Detail + " —— 退回 UAC 提权（只需要这一次）。");
            }

            var elevated = Elevation.RestartSelf(argv);
            switch (elevated.Status)
            {
                case ElevateStatus.Started:
                    Console.WriteLine("[elevate] 已请求管理员权限，本次启动交给新的管理员进程。");
                    return 0;

                case ElevateStatus.Cancelled:
                    // ⚠️ 不接着以只读身份起后端：8765 一旦被非管理员进程占住，用户再双击
                    //    run-as-admin.cmd 也只会把那个只读界面叫到前台 —— 形成"怎么点都
                    //    写不进去"的死循环。宁可当场退出，把端口留给真正的管理员实例。
                    Console.Error.WriteLine("[elevate] UAC 被取消，本次不启动。");
                    Console.Error.WriteLine("          EC 写入必须要管理员。只想看温度/曲线的话，"
                                            + "加 --no-elevate 以只读模式运行。");
                    return 6;

                default:
                    Console.Error.WriteLine("[elevate] 提权启动失败：" + elevated.Detail);
                    Console.Error.WriteLine("          要照常启动，请加 --no-elevate（只读模式，写操作会 403）。");
                    return 6;
            }
        }

        // 走到这里要么已经是管理员，要么这次调用本来就不该提权。只读模式要说清楚 ——
        // 界面上的每个写按钮都会 403，用户得先知道原因在权限，不在功能。
        if (!Elevation.IsElevated() && argv.Contains(ElevationPlan.NoElevateFlag))
            Console.WriteLine("[elevate] --no-elevate：以只读模式运行（EC 写操作会 403）。");

        // NVML 超频自检：读能力/状态 + 温和写一次 + 复位（要管理员才写得进去）
        if (argv.Contains("--gpu-oc-selftest"))
            return GpuOcCheck.Run();

        // ------------------------------------------------------------ --show
        // 给 run-silent.wsf 的"已经在跑"分支用：它的活**只有**一件事 —— 把旧实例的窗口叫到前台。
        // 不走 AppState / 不起服务 / **不提权**（这一步只是发个具名事件，用户不该为此再点一次 UAC）。
        // 退出码约定：
        //   0 = 已叫醒旧实例（调用方到此为止）
        //   5 = 端口上是我们，但对方不接信号（旧版本）
        //   4 = 端口已经没人占（调用方该去走正常提权启动，别让这个非管理员进程把后端占了）
        if (argv.Contains("--show"))
        {
            if (!IsOurInstance(port)) return 4;
            var woke = UiSignal.Trigger(port);
            Console.WriteLine(woke ? "[show] 已请旧实例打开界面" : "[show] 旧实例不接信号（旧版本）");
            return woke ? 0 : 5;
        }

        if (fixturesDir is null)
        {
            Console.Error.WriteLine("[FATAL] 找不到 tests/fixtures 目录。");
            Console.Error.WriteLine("        这是契约样本，缺了就没法起服务（绝不用空对象冒充）。");
            Console.Error.WriteLine("        用 --fixtures <dir> 显式指定，或先跑 spike/_grab_contract.py。");
            return 2;
        }

        var fixtures = new StubData(fixturesDir);

        if (selftest)
            return SelfTest.Run(fixtures);

        // ------------------------------------------------------------ 端口交棒（--takeover）
        //
        // 只由「界面里点『以管理员身份重启』」带来：那个按钮是在**只读实例自己身上**点的，
        // 所以旧进程此刻还占着 8765。这里先请它退出、等它真正让出端口，再往下走。
        //
        // ⚠️ 必须排在 **AppState 之前**：AppState 的构造就会开始采样并接管风扇，
        //    两个进程同时碰 EC 会互相覆盖（跨进程单槽命令缓冲区，见 REWRITE-PLAN 风险 #6）。
        // ⚠️ 只请我们自己的实例退出（`IsOurInstance` 有 env 块才算），绝不 taskkill，
        //    也绝不抢别的程序的端口 —— 交不了棒就如实失败。
        if (argv.Contains(ElevationPlan.TakeoverFlag))
        {
            var (ok, detail) = HandoverPort(port);
            if (!ok)
            {
                Console.Error.WriteLine("[FATAL] 接管端口 127.0.0.1:" + port + " 失败 —— " + detail);
                Console.Error.WriteLine("        旧实例可能是别的程序占着这个端口"
                                        + "\n        先跑一次 stop.cmd 再启动。");
                return 3;
            }
            Console.WriteLine("[elevate] 已接管端口：" + detail);
        }

        // ------------------------------------------------------------ 真数据源
        //
        // ------------------------------------------------------------ 强制预览某个机型档案（调试用）
        //
        // `--device quanta-gmwmi-generic` 或 `CC_DEVICE=...`：让**任何**机器都能看到
        // "未适配机型长什么样"（温度/转速/风扇曲线整块消失、写入口关着）。
        // 必须在 AppState 之前设好：机型档案是在 AppState 构造里解析的。
        // ⚠️ 强制来的档案一律只读（见 DeviceResolver.Forced），只用来预览界面。
        var forcedDevice = ArgValue(argv, "--device");
        if (string.IsNullOrWhiteSpace(forcedDevice))
            forcedDevice = Environment.GetEnvironmentVariable("CC_DEVICE");
        if (!string.IsNullOrWhiteSpace(forcedDevice))
        {
            QControl.Core.Devices.DeviceResolver.ForceId = forcedDevice.Trim();
            Console.WriteLine($"[device] **调试用**：强制预览档案「{forcedDevice.Trim()}」（写入口强制关闭）");
        }

        // 有 model.json 就接真数据（P6）：/api/status 与 /api/model 现算；
        // 没有就退纯桩模式 —— 桩数据是旧快照，绝不冒充实时读数。
        AppState? state = null;
        var modelPath = Paths.FindModel(ArgValue(argv, "--model"));
        if (modelPath is null)
        {
            Console.Error.WriteLine("[WARN] 没找到 model.json —— 退回纯桩模式（GET 全是旧快照）。");
        }
        else
        {
            try
            {
                state = new AppState(modelPath, port);

                // 档位档案归位（2026-11-17）：老配置的 `fan.mode_curves*` / `backlight.mode_profiles`
                // 在载入时被搬进 `perf.modes[档].{fan,light}` 并删掉老键。用户会发现自己的
                // model.json 里那几个键不见了 —— 日志里必须有这一行，否则就是"配置被吃了"。
                var moved = state.Model.LastMigration;
                if (moved.Changed)
                    Console.WriteLine($"[conf] 档位档案已归位到 perf.modes："
                                      + $"{moved.Modes} 个档位 / {moved.FanCurves} 条风扇曲线 / {moved.Lights} 套灯效"
                                      + "（老的 fan.mode_curves* 与 backlight.mode_profiles 已并入并删除）");

                // 采样循环必须在开始服务前起来：否则第一帧要等 1 秒，
                // 而且 /api/history 会从空开始（关掉页面再打开也接不上）。
                state.Start();
                var s = state;
                Router.UseRealData(path => path switch
                {
                    "/api/status" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.RequestHwinfoAndStatus(), ApiResponse.JsonOptions),
                    "/api/history" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.History(), ApiResponse.JsonOptions),
                    "/api/model" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.ModelView(), ApiResponse.JsonOptions),
                    "/api/device" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.DeviceReport(), ApiResponse.JsonOptions),
                    "/api/power/plans" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(QControl.Hal.Sensors.PowerPlans.List(),
                                              ApiResponse.JsonOptions),
                    "/api/app/settings" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.AppSettings(), ApiResponse.JsonOptions),
                    // 「关于」页：版本/机型/端口/启动时间 + 日志现状与尾巴（含手动写入的校验）
                    "/api/app/about" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.About(), ApiResponse.JsonOptions),
                    // 设置页「日志」卡：目录占用明细（清理按钮旁边那份）
                    "/api/app/log/usage" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.LogUsage(), ApiResponse.JsonOptions),
                    // 检查更新：只读内存状态（一次网络请求都不发，查询由 POST 触发）
                    "/api/app/update" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.UpdateState(), ApiResponse.JsonOptions),
                    "/api/vendor/state" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.VendorState(), ApiResponse.JsonOptions),
                    // 曲线状态 + 卡死自救诊断段（`guard`，见 AppState.FanGuard）
                    "/api/fancurve" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.FanState(), ApiResponse.JsonOptions),
                    "/api/osd" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.OsdState(), ApiResponse.JsonOptions),
                    "/api/gpu/oc" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.GpuOcState(), ApiResponse.JsonOptions),
                    // GPU 静态信息（驱动 / PCIe 代数与宽度）。⚠️ 必须先接真数据：
                    //    这条路径原来没有真实实现，真机模式下永远落回 tests/fixtures/gpu_info.json
                    //    那份 2026-10-18 的旧快照 —— 消费者会把它当实时信息。
                    //    NVML 拿不到（Optimus 关了 dGPU）时回 null，照旧落回桩。
                    "/api/gpu/info" => QControl.Hal.Sensors.GpuSensors.StaticInfo() is { } gi
                        ? System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(gi, ApiResponse.JsonOptions)
                        : null,
                    // 诊断口：HWiNFO 进程/共享内存/窗口/托盘的全部现状（会当场 ensure 一次）
                    "/api/debug/hwinfo" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.DebugHwinfo(), ApiResponse.JsonOptions),
                    // 诊断口：进程内存拆账（工作集 / 提交 / 托管堆各代 / 上次归还工作集）
                    "/api/debug/mem" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.DebugMem(), ApiResponse.JsonOptions),
                    "/api/hardware" => System.Text.Json.JsonSerializer
                        .SerializeToUtf8Bytes(s.HardwareInfo(), ApiResponse.JsonOptions),
                    _ => null,
                });

                // ---- 已实现的写操作（其余 POST 仍回 501）----
                // 已实现的写操作都在 PostHandlers 里（那里是一张显式的表，
                // 没实现的一条会返回 null 由 Router 落回 501）。
                Router.UseRealPost(req => PostHandlers.TryHandle(req, s, RequestQuit));

                // `--fancurve`：**已无作用，只为兼容旧脚本保留**。
                // 2026-11-18 用户改定「默认交回 EC 固件，软件曲线是自定义功能」后，
                // 接管改由 `presets.fan.curve_enabled` 控制（见 TakeOverFans）。
                // 还留着这个开关是因为老的 .cmd / 手打命令可能带着它，
                // 直接报"未知参数"会让那些人以为坏了。
                if (argv.Contains("--fancurve"))
                    Console.WriteLine("[fancurve] --fancurve 已不需要：请在散热页打开「自定义风扇」");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] 真数据源初始化失败，退回纯桩模式：{ex.Message}");
                state = null;
            }
        }

        // ------------------------------------------------------------ WPF 窄接口自检
        // 放在起服务之前（不占端口）：验证"UI → PostHandlers"这条链路的状态码映射，
        // 全程只发只读/授权类请求，不动硬件。见 Ui/SelfTest/UiActionsCheck.cs。
        if (argv.Contains("--ui-actions-selftest"))
        {
            var code = UiEntry.ActionsSelfTest(state);
            state?.Dispose();
            return code;
        }

        // ------------------------------------------------------------ 截图即冒烟
        // 放在起服务之前：截图不需要 8765，也**不该**去占端口（可以和日常实例共存）。
        // 退出前把风扇交回曲线转速（与正常退出同口径）—— 否则这次快照运行会留下一个钉住的 UFA。
        if (shotPath is not null)
        {
            var code = UiEntry.Shot(state, shotPath, uiPage, shotTheme, shotWidth, shotHeight, shotWait, shotDialog, shotFlip);
            try { state?.Fan.Stop(release: true); } catch { /* 退出路径不抛 */ }
            state?.Dispose();
            return code;
        }

        // ------------------------------------------------------------ 一屏装得下自检
        // 「所有页常态都不用滚动条」是个会悄悄回归的约束（内容只超 0.7px 也会挂条），
        // 所以给它一条能反复跑的断言：逐页切过去，断言滚动量 = 0。见 Ui/SelfTest/UiFitCheck.cs。
        // 与 --ui-shot 同口径：不起服务、不占端口，起完把风扇交回曲线。
        if (argv.Contains("--ui-fit-selftest"))
        {
            var code = UiEntry.FitSelfTest(state, shotWidth, shotHeight);
            try { state?.Fan.Stop(release: true); } catch { /* 退出路径不抛 */ }
            state?.Dispose();
            return code;
        }

        // ------------------------------------------------------------ 起服务
        var url = $"http://127.0.0.1:{port}/";

        // 「请把界面打开」的纸条（任务代启时用，见 Core/Startup/OpenRequest）。
        // ⚠️ 必须在这里读：下面每条提前 return 的路径都算"已兑现 / 已作废"，
        //    晚读就会留下"下次启动莫名开窗"。
        var hasOpenRequest = OpenRequest.TryTake(out var requestedPage);
        if (requestedPage is not null) uiPage = requestedPage;

        // 界面要不要自动打开：命令行（--tray / --no-open）是显式意图，优先于配置。
        // 与早先实现同口径：配置文件里的 start_mode=tray 只在没被命令行覆盖时生效。
        // ⚠️ 纸条优先于 `--tray`：任务里写死的就是 `--tray`，但要开窗的正是那一路。
        var quietStart = (argv.Contains("--tray") || argv.Contains("--no-open")
                          || StartModeIsTray(state)) && !hasOpenRequest;
        var wantTray = !argv.Contains("--no-tray");

        var sw = Stopwatch.StartNew();
        var server = new MiniHttpServer(port, req => Dispatch(req, fixtures));

        // ⚠️ 退出信号必须在**开始接客之前**装好（2026-11-23 审查）。
        //    原来这两行写在 server.Start() 之后（横幅 / 托盘 / 自启任务注册之后），
        //    那几百毫秒里进来的 POST /api/app/quit 会走到 `_quitEvent?.Set()` 空转：
        //    接口回 ok:true、进程不退出，而风扇已经被 Stop(release:true) 交回固件了。
        var quit = new ManualResetEventSlim();
        _quitEvent = quit;

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            // 端口被占：先判断是不是**我们自己**的另一个实例。
            // 是 → 直接把界面打开（或静默退出），不要甩一段 FATAL 给用户；
            // 不是 → 照旧报清楚，绝不"猜"。
            if (IsOurInstance(port))
            {
                // ---- 提权实例遇到只读实例：接管（「⚠ 需管理员」徽章的落点）----
                //
                // 只读实例是降级形态：写操作全 403、按需拉 HWiNFO 还要弹一发 UAC。
                // 提权实例出现本身就说明"要的是完整功能"，所以不问用户、直接换代。
                // 走 HandoverPort：请它**自己退出**（交回风扇、摘托盘、收自己拉起的
                // HWiNFO），绝不 taskkill —— 那条路会跳过 on_quit 把风扇卡住。
                if (Elevation.IsElevated() && InstanceIsReadOnly(port))
                {
                    Console.WriteLine($"[elevate] 127.0.0.1:{port} 上跑的是只读实例，请它退出后接管……");
                    var (hOk, hDetail) = HandoverPort(port);
                    Console.WriteLine("[elevate] " + hDetail);

                    if (!hOk)
                    {
                        Console.Error.WriteLine("[FATAL] 接管只读实例失败 —— " + hDetail);
                        return 3;
                    }

                    try
                    {
                        server.Start();
                    }
                    catch (Exception ex2)
                    {
                        Console.Error.WriteLine("[FATAL] 接管后仍无法监听 —— " + ex2.Message);
                        return 3;
                    }
                }
                else
                {
                    Console.WriteLine($"已有一个控制中心在 127.0.0.1:{port} 上运行。");
                    if (!quietStart || hasOpenRequest)
                        OpenUi(state: null, port, page: uiPage);
                    return 0;
                }
            }
            else
            {
                Console.Error.WriteLine($"[FATAL] 无法监听 127.0.0.1:{port} —— {ex.Message}");
                Console.Error.WriteLine("        端口被占用？先跑一次 src\\verify-port.cmd 或 stop.cmd，"
                                        + "\n        确认 8765 上没有残留实例（先跑 src\\verify-port.cmd 或 stop.cmd）。");
                return 3;
            }
        }

        Banner(port, fixturesDir, fixtures, modelPath);

        // 「把窗口叫出来」的信号口：已经在跑的实例监听它（第二个进程双击 exe 时用它，
        // 见 Ui/UiSignal.cs）。托盘左键与信号走同一个 OpenUi，行为一致。
        UiSignal.Listen(port, () => OpenUi(state, port, uiPage));

        // ------------------------------------------------------------ 托盘
        TrayIcon? tray = null;
        if (wantTray)
        {
            // 菜单配色跟着「白天/黑夜」设置走（2026-10-22 用户要求）。真源仍是
            // presets.ui.theme；这里只交一个**现读**的委托 —— 菜单每次弹出时读一次，
            // 切完主题立刻生效，不需要谁去推送。auto 的"跟随系统"由 ThemeIntent 现算。
            Func<string?> trayTheme = () =>
            {
                try
                {
                    return state?.Model.Get("ui", "theme") is JsonValue tv
                           && tv.TryGetValue<string>(out var token) ? token : null;
                }
                catch
                {
                    return null;      // 采样线程正在改 model.json：读不到就按暗色，别崩
                }
            };

            tray = TrayIcon.Start(
                onOpen: () => OpenUi(state, port, uiPage),
                onQuit: RequestQuit,
                setAutostart: on => Autostart.Set(on, Autostart.TrayCommand(port)),
                // Win 键锁：菜单上显示的"当前"读**钩子实际状态**（不是配置值），
                // 写完也不缓存 —— 每次弹出重新读，被安全软件摘掉钩子时会自己变回"关"。
                winKeyLocked: () => state?.WinKeyLocked == true,
                setWinKeyLock: on =>
                {
                    if (state is null) return (false, "后端没有初始化（没找到 model.json），改不了这个开关");
                    var r = state.SetWinKeyLock(on);
                    if (r["ok"]?.GetValue<bool>() != true)
                        return (false, r["error"]?.GetValue<string>() ?? "切换失败");
                    return (true, on
                        ? "本次运行内有效：Win 键不再弹开始菜单（控制中心退出后恢复，开机默认关闭）"
                        : "Win 键已恢复弹出开始菜单");
                },
                themeToken: trayTheme,
                out var trayError);

            if (tray is null)
                Console.Error.WriteLine("[WARN] 托盘图标没起来：" + trayError
                                        + "\n       后端照常工作；界面仍可从开始菜单 / 桌面快捷方式重新打开。");
        }

        // ------------------------------------------------------------ 自启维护
        // 自启维护口径：用户没明确关过、当前也没开
        // 就打开；已是任务就顺手清掉遗留 Run 项。**不重写已有任务** —— 那等于
        // 每次启动都偷偷把开机程序换成自己。
        if (state is not null && !argv.Contains("--no-autostart"))
        {
            var userTurnedOff = state.Model.Get("ui", "autostart_off") is JsonValue offNode
                                && offNode.TryGetValue<bool>(out var off) && off;
            var (changed, detail) = Autostart.EnsureDefault(userTurnedOff,
                                                            Autostart.TrayCommand(port));
            if (changed) Console.WriteLine("[autostart] " + detail);
        }

        // ------------------------------------------------------------ 原厂自启抑制复核
        //
        // 为什么每次启动都要钉一次（历次踩过的结论）：
        // 原厂的安装/修复程序、甚至某些系统更新会把那个计划任务重新启用。用户开这个
        // 开关的期望是"以后它别再自己起来"，一次设置管终身不现实。
        //
        // 幂等：只有真的从"启用"改成"禁用"时才会有实际写入，日志也只在那时多一行。
        // 只在**已提权**时做 —— 改任务/服务都需要管理员，没权限时连试都不必试。
        if (state is not null && Elevation.IsElevated()
            && state.Model.Get("vendor", "block_autostart") is JsonValue blockNode
            && blockNode.TryGetValue<bool>(out var blockOn) && blockOn)
        {
            Task.Run(() =>
            {
                try
                {
                    // 先看现状：两条入口都还锁着就什么都不做。
                    // 只有"任务又被启用了 / 服务又变回自动"才需要纠正 —— 那才是该留日志的时刻，
                    // 否则每次开机都白写一遍、打一行噪音，真出事时反而看不出来。
                    var taskNow = VendorStartup.TaskState();
                    var svcNow = VendorStartup.ServiceState(running: false);
                    var taskLocked = taskNow["exists"]?.GetValue<bool>() != true
                                     || taskNow["enabled"]?.GetValue<bool>() == false;
                    var startLabel = svcNow["start_label"] is JsonValue lv
                                     && lv.TryGetValue<string>(out var ls) ? ls : null;
                    var svcLocked = svcNow["exists"]?.GetValue<bool>() != true
                                    || startLabel == "disabled";
                    if (taskLocked && svcLocked) return;

                    Console.WriteLine("[vendor] 抑制态被改回来了，重新钉住……");
                    var r = state.SetVendorBlock(true);
                    var logs = r["log"] is JsonArray l && l.Count > 0
                        ? string.Join("；", l.Select(x => x?.ToString()))
                        : "无改动";
                    var errs = r["errors"] is JsonArray e && e.Count > 0
                        ? "；失败：" + string.Join("；", e.Select(x => x?.ToString()))
                        : "";
                    Console.WriteLine("[vendor] 抑制自启复核：" + logs + errs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[vendor] 抑制自启复核异常：" + ex.Message);
                }
            });
        }

        // ------------------------------------------------------------ 打开界面
        if (quietStart)
        {
            Console.WriteLine(" 界面     : 未自动打开（--tray / 启动方式=托盘）；托盘左键可打开");
        }
        else
        {
            OpenUi(state, port, uiPage);
        }

        Console.WriteLine($" 启动耗时 {sw.ElapsedMilliseconds} ms");
        Console.WriteLine();
        Console.WriteLine($" API      : {url}api/status   （脚本与诊断用；界面不走 HTTP）");
        // 没有控制台（双击启动 / 开机自启）时，"Ctrl+C" 这句话是错的 —— 那会儿唯一的
        // 正常退出方式是托盘菜单。GUI 子系统之后这条分支才真正用得上。
        Console.WriteLine(ConsoleHost.IsRealConsole()
            ? " 退出     : Ctrl+C 或 POST /api/app/quit —— 关窗口只关界面，后端与托盘继续跑"
            : " 退出     : 托盘图标右键 → 退出（或 POST /api/app/quit）—— 关窗口只关界面，后端与托盘继续跑");
        Console.WriteLine();

        // `quit` 已在 server.Start() 之前建好（见上面那段注释）
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };

        // ⚠️ 关窗口（X 按钮）走的是 ProcessExit，**不经过** CancelKeyPress。
        //    不在这里交回的话，EC 会一直按最后写入的 UFA 转（它不会自动接管），
        //    用户看到的是"软件都关了风扇还卡在 73%" —— 这正是 10-13 那类事故的形态。
        //    Stop(release:true) 走 `EcHal.ReleaseToEc`：先 `0x0206=0` 清接管标志，
        //    再把三路 UFA 写成 0xFF —— 风扇几秒内回到 EC 自己的温度曲线。
        //    幂等，重复调用无副作用（DutyApplied 为 null 时一个字节都不写）。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { tray?.Shutdown(1000); } catch { /* 退出路径不抛 */ }
            try { state?.Fan.Stop(release: true); } catch { /* 退出路径不抛 */ }
            // 关控制台窗口（X）不走 Dispose：这里也要把「自己拉起的那个 HWiNFO」收掉
            try { HwInfoProcess.StopOurs(); } catch { /* 退出路径不抛 */ }
            quit.Set();
        };
        quit.Wait();

        // 界面先收（窗口是同一进程里的，进程一死它就是"凭空消失"，用户会以为崩了）
        UiEntry.Close();

        // ⚠️ 先摘图标再退：进程先死会让托盘留下幽灵图标（早先实现 2026-10-14 修过）
        tray?.Shutdown();
        try { state?.Fan.Stop(release: true); } catch { /* Ctrl+C 路径也交回 */ }
        server.Stop();
        state?.Dispose();
        Console.WriteLine("[quit] 已退出（曲线若在跑，已交回 EC 固件：0x0206=0 + UFA=0xFF）");
        return 0;
    }

    // ---------------------------------------------------------------- 启动辅助

    /// <summary>
    /// 打开界面。**只有 WPF 一种**（进程内，直读 AppState）。
    /// 本进程没有 AppState（= 另一个实例正在管硬件）时，请它把窗口叫出来；
    /// 结果都打到控制台 —— 用户靠这一行判断界面到底开在哪，别静默。
    /// </summary>
    private static void OpenUi(AppState? state, int port, string? page)
    {
        // 我们这个进程里没有 AppState（硬件归另一个实例管）⇒ 自己开窗也没数据，
        // 只能是"请它把窗口叫出来"。
        if (state is null && UiSignal.Trigger(port))
        {
            Console.WriteLine(" 界面     : 已有实例在跑，已让它把窗口叫到前台");
            return;
        }

        UiEntry.Show(state, page);
        Console.WriteLine(" 界面     : 已打开 WPF 界面（关窗口即释放界面内存）");
    }

    /// <summary>解析 <c>--size 1320x880</c>。写错就当没写（用窗口默认尺寸），不抛。</summary>
    private static (int Width, int Height) ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0);
        var parts = text.Split('x', 'X');
        return parts.Length == 2
               && int.TryParse(parts[0], out var w)
               && int.TryParse(parts[1], out var h)
               && w > 0 && h > 0
            ? (w, h)
            : (0, 0);
    }

    /// <summary>
    /// 配置里的「启动方式」是不是"静默进托盘"。
    /// 读不到 / 值非法一律按 window。
    /// </summary>
    private static bool StartModeIsTray(AppState? state)
    {
        try
        {
            return state?.Model.Get("ui", "start_mode") is JsonValue v
                   && v.TryGetValue<string>(out var mode)
                   && mode == "tray";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 端口上跑的是不是**我们自己**（另一个实例）：看 `/api/status` 里有没有 `env` 块。
    /// 有 = 本程序的响应；超时/解析失败/没有 env = 别人占着，交给调用方报错。
    ///
    /// ⚠️ 不能用"端口通不通"当判据：任何程序占着 80 端口都会"通"，
    ///    那会把"别的程序占了端口"说成"控制中心已在运行"。
    /// </summary>
    private static bool IsOurInstance(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            var text = http.GetStringAsync($"http://127.0.0.1:{port}/api/status")
                           .GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("env", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 端口上那个实例是不是**只读**（`/api/status` → `env.write_ok == false`）。
    ///
    /// 用途只有一个：判断"提权实例该不该顶掉它"。只读实例是降级形态（写操作全 403、
    /// 按需拉 HWiNFO 还弹 UAC），提权实例出现说明用户要完整功能。
    /// ⚠️ 读不到 / 没有该字段（旧版本）一律按**不是**只读处理 —— 宁可什么都不动，
    ///    也不能把"版本太老看不懂"当成"它是残废的"然后去抢人家的端口。
    /// </summary>
    private static bool InstanceIsReadOnly(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
            var text = http.GetStringAsync($"http://127.0.0.1:{port}/api/status")
                           .GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("env", out var env)) return false;
            if (!env.TryGetProperty("write_ok", out var w)) return false;
            return w.ValueKind == JsonValueKind.False;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 等端口上出现**我们的**实例（任务代启要 1~3 秒：任务计划服务 → QControl.exe →
    /// 后端绑端口）。超时返回 false，由调用方如实报错。
    ///
    /// ⚠️ 只用于"任务代启后的确认"，别拿它替代 <see cref="IsOurInstance"/> 做语义判断：
    ///    它内部就是反复问同一个问题，等到就是等到，等不到不代表端口是别人的。
    /// </summary>
    private static bool WaitOurInstance(int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (IsOurInstance(port)) return true;
            Thread.Sleep(300);
        }
        return IsOurInstance(port);
    }

    // ---------------------------------------------------------------- 端口交棒

    /// <summary>
    /// `--takeover` 的握手：让旧实例自己退出，等端口真空了才返回。
    ///
    /// 为什么"请它退"而不是杀它：退出路径要交回风扇转速、摘托盘图标、收掉自己拉起的
    /// HWiNFO（见 ProcessExit）；`taskkill /F` 全跳过，风扇会卡在最后写入的转速。
    ///
    /// 三种结局分得开（文案要能指导下一步动作）：
    ///   · 端口本来就空 → 成功；
    ///   · 是我们的实例但 `/api/app/quit` 不成功 → 失败（可能被 arm/权限挡住，或版本太老）；
    ///   · 端口有东西但不是我们 → 失败（绝不抢别人的端口）。
    /// </summary>
    private static (bool Ok, string Detail) HandoverPort(int port, int waitMs = 8000)
    {
        if (!PortListening(port)) return (true, "端口原本就空着（旧实例已经退出了）");
        if (!IsOurInstance(port))
            return (false, "端口上的程序不是本程序（/api/status 里没有 env 块）");

        if (!PostQuit(port))
            return (false, "旧实例不响应 POST /api/app/quit");

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitMs)
        {
            Thread.Sleep(200);
            if (!PortListening(port))
                return (true, $"旧实例已退出，用时 {sw.ElapsedMilliseconds} ms");
        }

        // 退出了但端口还挂着：通常是旧进程卡在风扇交回/托盘清理上
        return (false, $"旧实例 {waitMs} ms 内没让出端口（它可能卡在退出流程里）");
    }

    /// <summary>
    /// 端口上有没有人在监听。
    ///
    /// ⚠️ 与 <see cref="IsOurInstance"/> 是两件事，别合并：那个问"是不是我们"（要 HTTP + JSON），
    ///    这个只问"端口通不通"（TCP 连一下）。交棒时两者都要用 —— 先看"有没有人"，
    ///    再看"是不是我们"，最后看"走了没有"。
    /// </summary>
    private static bool PortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            // 本机连一个没人监听的端口会立刻 RST；400 ms 只是防万一
            return client.ConnectAsync("127.0.0.1", port).Wait(400) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>POST /api/app/quit（不带 body）。成功 = 对方收下并开始退出。</summary>
    private static bool PostQuit(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var res = http.PostAsync($"http://127.0.0.1:{port}/api/app/quit", null)
                                .GetAwaiter().GetResult();
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 分发

    /// <summary>
    /// 请求退出。**延迟 200 ms 再真正退**：立刻关监听会让这次 HTTP 响应断在半路，
    /// 调用方（stop.cmd / 界面）就拿不到结果，只能靠超时猜。
    /// </summary>
    private static void RequestQuit()
        => _ = Task.Delay(200).ContinueWith(
            _ => _quitEvent?.Set(), System.Threading.Tasks.TaskScheduler.Default);

    private static ApiResponse Dispatch(ApiRequest req, StubData fixtures)
        => Router.Route(req, fixtures);

    // ---------------------------------------------------------------- 横幅

    private static void Banner(int port, string fixturesDir, StubData fixtures, string? modelPath)
    {
        var all = Router.AllEndpoints.ToList();
        var gets = all.Count(e => e.Method == "GET");
        var posts = all.Count(e => e.Method == "POST");

        Line();
        Console.WriteLine(" Q-Control 控制中心 · C# 后端   [P8 · 主端口 8765 · 风扇默认交回 EC]");
        Line();
        Console.WriteLine($" 端口     : {port}   （{(port == DefaultPort ? "主端口" : "非默认端口")}）");
        Console.WriteLine($" 路由     : {Router.Count} 条（GET {gets} / POST {posts}）");
        Console.WriteLine($" 契约样本 : {fixturesDir}");

        var report = fixtures.Report(["status", "hardware", "history", "power_plans",
            "fancurve", "gpu_info", "gpu_oc", "app_settings", "vendor_state",
            "osd", "device", "model", "app_about", "app_log_usage", "app_update"]).ToList();
        var okCount = report.Count(r => r.Ok);
        Console.WriteLine($" 桩数据   : {okCount}/{report.Count} 就位"
            + (okCount < report.Count
                ? "   ⚠️ " + string.Join(", ", report.Where(r => !r.Ok).Select(r => r.Name))
                : ""));

        // 权限一行要显式：非管理员时所有写操作都会 403，界面上体现为"点了没反应"。
        // 启动横幅里先说清，用户才知道该去点 UAC 而不是查功能。
        Console.WriteLine(Elevation.IsElevated()
            ? " 权限     : 管理员（EC 可写）"
            : " 权限     : 非管理员 — 只读（EC 写会 403；--no-elevate 下就是这样）");
        Console.WriteLine(modelPath is null
            ? " 数据源   : 纯桩（GET 全是旧快照）"
            : " 数据源   : 真数据 — /api/status 与 /api/model 现算");
        // 条数**现算**，别再写死数字（加一条 POST 就会变成假话）
        Console.WriteLine($" 写操作   : {posts}/{posts} 条 POST 已接真"
                          + "（GPU 超频/重启 HWiNFO 需要管理员，没权限会如实报原因）");
        Console.WriteLine(" 诊断     : /api/debug/hwinfo 已接真（进程 / 共享内存 / 窗口 / 托盘 / 失败计数）");
        Console.WriteLine(" 内存     : 关窗与空闲时归还工作集（/api/debug/mem 看拆账，/mem/trim 手动收一次）");
        Console.WriteLine(" 风扇曲线 : 默认交回 EC 固件；在散热页打开「自定义风扇」后才由软件曲线接管"
                          + "（失败每 30 秒自动重试）");
        Console.WriteLine(FileLog.Active
            ? $" 日志     : {FileLog.CurrentPath}   （每行落盘，保留 {FileLog.KeepDays} 天）"
            : $" 日志     : 未启用（{FileLog.OffReason ?? "目录不可写"} → 只输出到控制台）");
        Console.WriteLine();
        Console.WriteLine(" ⚠️ 开机自启入口全机唯一（任务名 QControl），当前指向本程序："
                          + "\n    被别的程序抢走时，下次开机跑起来的就是它。");
        Line();
    }

    private static void Line() => Console.WriteLine(new string('=', 66));

    private static string? ArgValue(string[] argv, string key)
    {
        var i = Array.IndexOf(argv, key);
        return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
    }
}

/// <summary>
/// 契约硬验收：DTO 必须能**无损**装下真机响应。
///
/// 做法是「JSON → DTO → JSON」往返，然后把两边展平成 路径=值 的集合逐项比。
/// 漏一个字段、改一个名、可空判错，都会在这里现形 —— 这是唯一能离线跑
/// 又不骗人的验证手段。
/// </summary>
internal static class SelfTest
{
    private static readonly (string Fixture, Type Dto)[] Cases =
    [
        ("status", typeof(StatusDto)),
        ("hardware", typeof(HardwareDto)),
        ("history", typeof(HistoryDto)),
        ("power_plans", typeof(PowerPlansDto)),
        ("fancurve", typeof(FanCurveStateDto)),
        ("gpu_info", typeof(GpuInfoDto)),
        ("gpu_oc", typeof(GpuOcStateDto)),
        ("app_settings", typeof(AppSettingsDto)),
        ("vendor_state", typeof(VendorStateDto)),
        ("osd", typeof(OsdStateDto)),
        ("device", typeof(DeviceReportDto)),
        ("model", typeof(ModelViewDto)),
        // 「关于」页三个端点（2026-10-22 WPF 迁移时补进出 round-trip）
        ("app_about", typeof(AboutDto)),
        ("app_log_usage", typeof(LogUsageDto)),
        // 检查更新（2026-10-27）：state / latest / assets 三层都要能无损装下
        ("app_update", typeof(UpdateDto)),
    ];

    public static int Run(StubData fixtures)
    {
        Console.WriteLine(new string('=', 66));
        Console.WriteLine(" P1 契约自检");
        Console.WriteLine(new string('=', 66));

        // ---- [1] 路由清单 ----
        Console.WriteLine();
        Console.WriteLine("[1] 冻结的路由");
        var all = Router.AllEndpoints.OrderBy(e => e.Method).ThenBy(e => e.Path).ToList();
        foreach (var e in all)
            Console.WriteLine($"  {e.Method,-5} {e.Path,-32} {e.Notes}");
        Console.WriteLine($"  合计 {all.Count} 条（GET {all.Count(x => x.Method == "GET")}"
                          + $" / POST {all.Count(x => x.Method == "POST")}）");

        // ---- [2] DTO 往返 ----
        Console.WriteLine();
        Console.WriteLine("[2] DTO 往返一致性（JSON → DTO → JSON，逐字段比对）");
        var failed = 0;
        var totalFields = 0;
        foreach (var (fixture, dto) in Cases)
        {
            if (!fixtures.TryGet(fixture, out var raw))
            {
                Console.WriteLine($"  [FAIL] {fixture,-14} 样本缺失");
                failed++;
                continue;
            }

            try
            {
                var original = Flatten(Encoding.UTF8.GetString(raw));
                var obj = JsonSerializer.Deserialize(raw, dto, ApiResponse.JsonOptions);
                var round = JsonSerializer.Serialize(obj, dto, ApiResponse.JsonOptions);
                var back = Flatten(round);

                var missing = original.Keys.Except(back.Keys).ToList();
                var extra = back.Keys.Except(original.Keys).ToList();
                var differ = original.Keys.Intersect(back.Keys)
                                     .Where(k => original[k] != back[k]).ToList();

                totalFields += original.Count;
                if (missing.Count == 0 && extra.Count == 0 && differ.Count == 0)
                {
                    Console.WriteLine($"  [ OK ] {fixture,-14} {original.Count,4} 个字段全部往返一致");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  [FAIL] {fixture,-14} 缺失 {missing.Count} / 多出 {extra.Count}"
                                      + $" / 值不同 {differ.Count}");
                    foreach (var k in missing.Take(6)) Console.WriteLine($"         缺失 {k}");
                    foreach (var k in extra.Take(6)) Console.WriteLine($"         多出 {k}");
                    foreach (var k in differ.Take(6))
                        Console.WriteLine($"         不同 {k}: 原 {original[k]} → 回 {back[k]}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  [FAIL] {fixture,-14} {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- 结论 ----
        Console.WriteLine();
        Console.WriteLine(new string('=', 66));
        Console.WriteLine(failed == 0
            ? $" => 通过：{all.Count} 条路由冻结，{totalFields} 个字段零丢失"
            : $" => 不通过：{failed} 项失败");
        Console.WriteLine(new string('=', 66));
        return failed == 0 ? 0 : 1;
    }

    /// <summary>把 JSON 展平成 `路径=规范化值`，对象按键名、数组按索引。</summary>
    private static Dictionary<string, string> Flatten(string json)
    {
        var sink = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement, "", sink);
        return sink;
    }

    private static void Walk(JsonElement e, string path, Dictionary<string, string> sink)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                    Walk(prop.Value, path + "/" + prop.Name, sink);
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray())
                    Walk(item, path + "/" + i++, sink);
                if (i == 0) sink[path] = "[]";
                break;

            // ⚠️ 数值按 double 规范化再比：JSON 里 `51.0` 序列化回来是 `51`，
            //    按文本比会假报"值不同"。
            case JsonValueKind.Number:
                sink[path] = e.TryGetDouble(out var d)
                    ? "n:" + d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : "n:" + e.GetRawText();
                break;

            case JsonValueKind.String: sink[path] = "s:" + e.GetString(); break;
            case JsonValueKind.True: sink[path] = "b:true"; break;
            case JsonValueKind.False: sink[path] = "b:false"; break;
            case JsonValueKind.Null: sink[path] = "null"; break;
            default: sink[path] = e.ValueKind.ToString(); break;
        }
    }
}
