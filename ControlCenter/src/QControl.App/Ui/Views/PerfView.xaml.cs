using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using QControl.App.Http.Contract;
using QControl.App.Ui.Controls;
using QControl.Core.Power;
using QControl.Hal.Sensors;

// ⚠️ 别 `using QControl.Hal.Sys;`：那里的 TaskScheduler 会跟 System.Threading.Tasks.TaskScheduler
//    撞名（Offload 里用的正是后者）。只取需要的类型。
using LidSwitch = QControl.Hal.Sys.LidSwitch;

namespace QControl.App.Ui.Views;

/// <summary>
/// 性能页：系统模式 / 显卡模式 / 电源计划 / 显卡超频。
///
/// 看起来"多余"的分支都是必要的，别因为更简洁删掉：
///   · 切模式的顺序：**点按钮即 arm → 写 EC → 最后切电源计划**。
///     2026-11-18 按用户要求去掉了确认弹窗；电源计划仍必须等 EC 写成功后再切，
///     免得出现「计划变了、EC 还是旧档」的两状态不一致；
///   · **写成功才点亮按钮**。点亮靠 <see cref="CardButton.IsOn"/>（点击本身不改它），
///     所以"失败时界面先亮一下"这种事在结构上就不可能发生；
///   · 写完有 <see cref="PerfSettleMs"/> 的待确认窗口：/api/status 是采样快照，
///     实测落后写入 1.5~6 秒，期间拿真值渲染会让按钮弹回去（就是"乱跳"）；
///   · 显卡模式写完必须问要不要重启（值只是存进 EC，重启才生效）；
///   · 超频那几行的数据源**刻意不统一**：核心频率/温度/功耗走 /api/status 快照
///     （跟顶栏、总览页同源，同屏才不会出现"顶栏 56°C、卡里 57°C"），
///     显存频率/电源状态走 NVML（别处不显示），显卡电压只有 HWiNFO 给。
///
/// ⚠️ 写操作一律走 <see cref="LocalApi.Post"/> → PostHandlers（守卫/副作用只有一份），
///    这里的代码**不直调 EC**。
/// ⚠️ 慢读（powercfg / NVML）都在后台线程，UI 线程上只碰 DTO 与控件。
/// </summary>
public partial class PerfView : UserControl
{
    private sealed record ModeSpec(string Key, int Number, string Name, string Desc, Geometry Icon);

    /// <summary>顺序 = 界面列序（办公 / 游戏 / 狂暴）；data-mode 1/2/3 与 EC 的 APMD 档位对应。</summary>
    private static readonly ModeSpec[] Modes =
    [
        new("office", 1, "办公模式", "安静省电 · 日常办公", Icons.ModeOffice),
        new("game", 2, "游戏模式", "性能与噪音均衡", Icons.ModeGame),
        new("beast", 3, "狂暴模式", "最高性能 · 风扇最激进", Icons.ModeBeast),
    ];

    private static readonly (long Code, string Name, string Desc, Geometry Icon)[] GfxModes =
    [
        (1, "混合输出", "核显负责输出、独显按需参与渲染（日常省电）", Icons.GfxHybrid),
        (2, "独显直连", "显示输出直连独显，核显被固件隐藏（游戏最高帧）", Icons.GfxDgpu),
        (3, "集显输出", "只用核显出图，独显不参与（待机最省电）", Icons.GfxIgpu),
    ];

    /// <summary>写完 /api/status 多久之内以本地期望值为准（6000ms）。</summary>
    private const int PerfSettleMs = 6000;

    private readonly LocalApi _api;

    private readonly Dictionary<string, CardButton> _modeButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<long, CardButton> _gfxButtons = new();

    private readonly Dictionary<string, KvRow> _ocRows = new(StringComparer.Ordinal);

    /// <summary>刚写成功的档位 + 那一刻的 TickCount64（见 <see cref="PerfSettleMs"/>）。</summary>
    private string? _perfWant;
    private long _perfWantAt;

    private StatusDto? _status;
    private GpuOcStateDto? _oc;
    private GpuOcStateStatusDto? _ocNvml;
    private bool _ocDraggingGuard;
    private bool _modeBusy;
    private bool _ocBusy;

    /// <summary>取消重启正在发请求（防连点）。</summary>
    private bool _rebootBusy;
    private string _capsSig = "";
    private int _tick;

    // ---- 拔电自动切办公模式（presets.perf.auto_office_on_battery）----
    /// <summary>那一行的开关。点击只发请求，<see cref="SwitchButton.IsOn"/> 由真值渲染。</summary>
    private SwitchButton? _autoSwitch;
    private bool _autoBusy;

    /// <summary>本机有没有电池（null = 还没有快照）：没有就不摆这一行。</summary>
    private bool? _battPresent;

    /// <summary>这台机器有没有性能模式能力（caps.perf_modes）。</summary>
    private bool _hasPerf = true;

    // ---- 屏幕亮度（内建屏，WMI）----
    /// <summary>最后一次读到的真值（null = 读不到 ⇒ 整块隐藏）。</summary>
    private int? _brightLevel;

    /// <summary>回写真值时的闸门：别让 ValueChanged 把"渲染"当成"用户操作"再写一次。</summary>
    private bool _brightSyncing;

    private bool _brightBusy;

    /// <summary>正在拖拽：拖动中只更新数字，松手写一次（WMI 一次 20~50ms）。</summary>
    private bool _brightDragging;

    private DispatcherTimer? _brightTimer;

    /// <summary>亮度的外读节拍（每 10 秒一次）：FN 键 / Windows 设置也会改亮度，得跟上。</summary>
    private int _brightTick;

    /// <summary>
    /// 上次看到的盖子状态（null = 这台机器不报这个事件 ⇒ 永远不置灰）。
    /// 每秒在 <see cref="Update"/> 里比对一次就够：盖子一合，内建屏本来就已经黑了。
    /// </summary>
    private bool? _lidClosed;

    /// <summary>只有 MainWindow 造这一页（它才有 LocalApi）；页面不自己去摸 AppState。</summary>
    internal PerfView(LocalApi api)
    {
        _api = api;
        InitializeComponent();

        BuildModes();
        BuildAutoRow();
        BuildGfx();
        BuildOcLive();

        RebootCancel.Click += (_, _) => _ = OnRebootCancel();

        // 盖子状态：合盖时内建屏断电，WMI 却照样"写成功" —— 界面要能说清这件事
        LidSwitch.EnsureSubscribed();

        OcCoreSlider.ValueChanged += (_, _) => SyncOcLabels();
        OcMemSlider.ValueChanged += (_, _) => SyncOcLabels();
        OcCoreSlider.IsMoveToPointEnabled = true;    // 点轨道直接跳
        OcMemSlider.IsMoveToPointEnabled = true;

        // 屏幕亮度：拖动中只改数字，松手提交；键盘 / 点轨道走 180ms 防抖（见 Bright_ValueChanged）
        BrightSlider.ValueChanged += Bright_ValueChanged;
        BrightSlider.IsMoveToPointEnabled = true;
        BrightSlider.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _brightDragging = true));
        BrightSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                _brightDragging = false;
                _brightTimer?.Stop();
                _ = OnBrightCommit();
            }));

        // ⚠️ Root 要撑满视口，卡片组才有"上下居中"可言 ——
        //    FillViewport **只钉 Root 的 MinHeight**，卡片高度仍由内容决定
        //    （别让卡片跟着 Root 拉伸：拉到 708 高时卡内全是空档，居中≠拉伸）。
        //    内容超过一屏时 Root 被顶到内容高，Center 自动退化成顶对齐，照常滚动。
        UiKit.FillViewport(Scroll, Root);
        UiKit.ApplyCardGap(CardGrid);

        // ⚠️ 慢读放在 Loaded 之后：构造函数可能在 Dispatcher 消息循环起来之前就跑
        //    （--page perf 的截图路径），那时没有同步上下文，await 会回到线程池线程上碰 UI。
        Loaded += (_, _) =>
        {
            RefreshPlans();
            RefreshOc();
            SyncAutoRow();
            RefreshBrightness();
        };
    }

    // ================================================================ 每秒刷新

    /// <summary>
    /// 重新拉一遍这一页的"一次性数据"（重复点击侧栏「性能」时由 MainWindow 调）：
    /// 电源计划列表在系统设置里随时会被改，NVML 那两项超频读数也只在进页 / 定时才拉。
    /// 快照本身不在这里刷 —— 每秒那一拍已经在推了。
    /// </summary>
    public void Refresh()
    {
        RefreshPlans();
        RefreshOc();
        SyncAutoRow();
        RefreshBrightness();
    }

    /// <summary>主窗口每秒推一份快照进来（null = 这一拍没数据）。</summary>
    public void Update(StatusDto? status)
    {
        _status = status;

        // 超频卡里那两项只有 NVML 给，5 秒刷一次就够
        if (++_tick % 5 == 0) RefreshOc();

        // 亮度：FN 键 / Windows 设置都会改它，10 秒对一次表就够（WMI 一次 20~50ms，别每拍读）
        if (++_brightTick % 10 == 0) RefreshBrightness();

        // 合盖 / 开盖：只比一个缓存的 int（电源事件在系统线程更新它，不读硬件）。
        // 1 秒的延迟无所谓 —— 盖子合上时内建屏本来就是黑的。
        var lid = LidSwitch.IsClosed;
        if (lid != _lidClosed)
        {
            _lidClosed = lid;
            // 只在真变化时打一行（一天也就几次），排障时能看出"合盖判定到底有没有生效"
            if (lid is { } closedLid)
                Console.WriteLine(closedLid
                    ? "[lid] 已合盖：笔记本屏幕亮度不可调"
                    : "[lid] 已开盖：恢复笔记本屏幕亮度控制");
            SyncBrightEnabled();
            if (lid == false) RefreshBrightness();   // 开盖：重新读一次真值再显示
        }

        UpdateModes(status);
        UpdateGfx(status);
        UpdateOcLive();
        // 超频卡的可点状态跟着快照走：`gfx_mode` 一变（切到集显输出），标签上的原因
        // 也要跟着换 —— 光靠 5 秒一次 RefreshOc 会让"核显模式"这句迟一拍才出现
        SyncOcEnabled();
        UpdateReboot(status?.Reboot);
        ApplyCaps(status);

        // 有没有电池只在**变化时**动一次（这段每秒都跑，每次改 Visibility 会连带重排）
        if (status?.Battery?.Present is { } present && present != _battPresent)
        {
            _battPresent = present;
            SyncAutoRow();
        }
    }

    /// <summary>三档按钮 + 右上角当前档位。</summary>
    private void UpdateModes(StatusDto? status)
    {
        var perf = status?.Perf;
        var mode = perf?.Mode;

        // 待确认窗口：刚写完、快照还没跟上时按本地期望值渲染（快照追上 / 超时即交回真值）
        if (_perfWant is not null)
        {
            if (mode == _perfWant) _perfWant = null;
            else if (Environment.TickCount64 - _perfWantAt < PerfSettleMs) mode = _perfWant;
            else _perfWant = null;
        }

        if (mode is not null)
        {
            PerfState.Text = ModeLabel(mode);
            foreach (var (key, button) in _modeButtons) button.IsOn = key == mode;
        }
        else if (perf?.Apmd is not null && _perfWant is null)
        {
            // EC 读到了一个不认识的 APMD 码：如实说"未知档位"，别假装在某个模式上
            PerfState.Text = "未知档位";
            foreach (var button in _modeButtons.Values) button.IsOn = false;
        }
    }

    private void UpdateGfx(StatusDto? status)
    {
        var code = status?.GfxMode;
        GfxNow.Text = code is { } c ? "当前：" + _api.GfxName(c) : "当前：--";
        foreach (var (key, button) in _gfxButtons) button.IsOn = code is { } v && v == key;
    }

    // ================================================================ 能力表（caps）

    /// <summary>
    /// 按机型能力表做减法：这台机器没有的能力**整块拿掉**，
    /// 不留灰按钮、不留"点了没反应"的控件。
    /// ⚠️ 只在能力表**变化**时才动控件 —— 这段每秒都跑，每次都改会连带触发整页重排。
    /// </summary>
    private void ApplyCaps(StatusDto? status)
    {
        var caps = status?.Device?.Caps;
        if (caps is null) return;

        var hasPerf = caps.PerfModes is { Count: > 0 };
        var hasGfx = caps.GfxSwitch == true;
        var sig = hasPerf + "|" + hasGfx;
        if (sig == _capsSig) return;
        _capsSig = sig;

        _hasPerf = hasPerf;
        ModesHost.Visibility = hasPerf ? Visibility.Visible : Visibility.Collapsed;
        GfxBlock.Visibility = hasGfx ? Visibility.Visible : Visibility.Collapsed;
        SyncAutoRow();
    }

    // ================================================================ 系统模式（写）

    private async void OnModeClick(string key, int number)
    {
        if (_modeBusy) return;

        // 2026-11-18：用户要求切系统模式不再弹确认框 —— 点卡片直接写。
        // 高危写仍走 /api/ec/arm + PostHandlers/EcHal 的守卫；电源计划只在 EC 写成功后才切。
        var want = _api.PerfPlan(key) ?? "";

        _modeBusy = true;
        foreach (var button in _modeButtons.Values) button.IsEnabled = false;
        try
        {
            // 三个请求串成一条（arm → 写 EC → 切计划），中间夹着 EC 通道 0.45s 节流，
            // 所以整段丢后台，UI 上只留"按钮禁用"这一个反馈。
            var (ecHit, error, planHit) = await Task.Run(() => SwitchMode(number, want));

            if (!ecHit)
            {
                Toast(error ?? "写入失败", false);
                return;
            }

            // 写成功才点亮（并告诉 Update 别拿还没更新的快照盖回来）
            _perfWant = key;
            _perfWantAt = Environment.TickCount64;
            PerfState.Text = ModeLabel(key);
            foreach (var (name, button) in _modeButtons) button.IsOn = name == key;

            Toast($"{ModeLabel(key)}：EC 已下发 · 电源计划{(planHit ? "已切换" : "未找到同名")}", true);

            // 与按 FN 键同一条反馈（OSD 由后端弹，失败也不影响切换结果）
            await Task.Run(() => _api.Post("/api/osd", new JsonObject
            {
                ["kind"] = "perf",
                ["title"] = "性能模式",
                ["subtitle"] = ModeLabel(key),
            }));

            RefreshPlans();       // 当前计划可能变了
            RefreshBrightness();  // 计划一变，屏幕亮度就被重放成那条计划的值（每档各自记住）
        }
        finally
        {
            _modeBusy = false;
            foreach (var button in _modeButtons.Values) button.IsEnabled = true;
        }
    }

    /// <summary>
    /// 后台线程上跑完整条写链。返回值 = (EC 是否写成功, 失败原因, 电源计划是否切到位)。
    /// ⚠️ 电源计划只在 **EC 确实切成功** 后才跟：让两边保持在同一个状态上，
    ///    而不是"计划是新档、EC 还是旧档"。
    /// </summary>
    private (bool EcHit, string? Error, bool PlanHit) SwitchMode(int number, string want)
    {
        // 0x0300 属高危命令（无条件写 APMD）：先 arm 授权，再下发。
        // 「已经在位的档位再点一次 = 强制重写」是刻意保留的语义（固件把功耗墙调走后
        // 用户靠再点一次压回来），别优化成幂等。
        var armed = _api.Post("/api/ec/arm", new JsonObject { ["cmd"] = 0x0300 });
        if (!armed.Ok) return (false, armed.Why("授权失败"), false);

        var written = _api.Post("/api/ec/perf", new JsonObject { ["mode"] = number });
        if (!written.Ok) return (false, written.Why("写入失败"), false);

        var planHit = false;
        try
        {
            var plans = _api.Plans();
            // 同名多条（本机「高性能」有两条）由 PickByName 定：正在用的 > 系统自带 > 第一条。
            // 后端 SyncPlanFor 用的是同一个函数 —— 手动点按钮和自动跟档必须挑同一条，
            // 否则同一台机器上「点了按钮」和「切了档位」会落到两个不同计划上。
            var guid = plans is null
                ? null
                : QControl.Hal.Sensors.PowerPlans.PickByName(
                    (plans.Plans ?? []).Select(x => (x.Guid, x.Name)), want, plans.Active, out _);
            if (guid is not null)
            {
                // 已经是这个计划就别再 setactive：语义上是空操作，但 Windows 会整套重放
                planHit = guid == plans!.Active
                          || _api.Post("/api/power/plan", new JsonObject { ["guid"] = guid }).Ok;
            }
        }
        catch
        {
            // 计划那边失败不该把"模式已切好"这件事报成失败
        }

        return (true, null, planHit);
    }

    // ================================================================ 显卡模式（写）

    private async void OnGfxClick(long code)
    {
        var name = _api.GfxName(code);
        var now = _status?.GfxMode is { } cur ? "当前：" + _api.GfxName(cur) : "当前值未知";

        // 排版口径（2026-11-07）：卡片正文只有 ~400px 宽（DialogCard 440 − padding 20×2），
        // 一行放得下 ~30 个汉字。所以**一行说一件事**：写入 / 生效 / 当下三段固定顺序，
        // 警告单独一段，句子超长就自己折。别再把几条塞成一段 —— 会糊成一坨灰字。
        var warn = code switch
        {
            2 => "⚠ 独显直连：核显会被固件隐藏，续航变短（游戏帧数最高）。",
            3 => "⚠ 集显输出：独显不参与，游戏 / 渲染性能大幅下降，\n"
                 + "   本界面之后也读不到 NVIDIA 数据。",
            _ => "",
        };

        var ask = $"即将把显卡模式切换到「{name}」\n{now}\n\n"
                  + "· 写入：向 EC 的 0xFB00（GFXT）写组写入该模式\n"
                  + "· 生效：需重启电脑，由固件下次开机时应用（与原厂一致）\n"
                  + "· 当下：显示输出暂不变化，属正常现象";
        if (warn.Length > 0) ask += "\n\n" + warn;

        var go = await UiDialog.ConfirmAsync(this, "切换显卡模式", ask, "写入 EC", "取消");
        if (!go) return;

        var result = await Task.Run(() =>
        {
            // 0x0203 属高危写命令：先 arm 授权 → 再下发（授权一次性）
            var armed = _api.Post("/api/ec/arm", new JsonObject { ["cmd"] = 0x0203 });
            if (!armed.Ok) return (Ok: false, Error: armed.Why("授权失败"), Mode: code);
            var written = _api.Post("/api/ec/gfx", new JsonObject { ["mode"] = code });
            if (!written.Ok) return (Ok: false, Error: written.Why("写入失败"), Mode: code);

            // 用**回包里的 mode**（后端按实际写入的档位回）：这才是"写进去的是什么"
            var applied = written.Raw?["mode"] is JsonValue v && v.TryGetValue<long>(out var m) ? m : code;
            return (Ok: true, Error: (string?)null, Mode: applied);
        });

        if (!result.Ok)
        {
            Toast(result.Error ?? "写入失败", false);
            return;
        }

        var appliedName = _api.GfxName(result.Mode);

        // 写进 EC 只是"存了个值"，真正生效要靠重启 —— 所以写完直接问一句，别让人猜下一步。
        // 只有两条路：
        //   · 立即重启 = `/t 0`，系统立刻重启，不弹任何对话框（有未保存文档时系统会拦下）；
        //   · 稍后重启 = 什么都不做，值留在 EC 里，等用户自己重启。
        // ⚠️ 不要用 `shutdown /r /t N`（N>0）：那会让系统自己弹一个倒计时框，不归我们管。
        // 排版同 <see cref="OnGfxClick"/> 的确认框：一条一行，选项用「· 名称：说明」。
        var pick = await UiDialog.ChooseAsync(this, "显卡模式已写入 EC",
            $"已写入 EC：模式 {result.Mode} · {appliedName}\n"
            + "显示输出将在下次开机时由固件切换（与原厂一致），\n"
            + "重启前不会发生变化。\n\n"
            + "· 立即重启：立即重启电脑\n"
            + "· 稍后重启：本次不执行任何操作，稍后自行重启\n\n"
            + "已写入的值不会丢失，重启后生效。",
            [
                ("now", "立即重启", true, false),
                ("later", "稍后重启", false, false),
            ]);

        if (pick == "now")
        {
            // seconds = 0 ⇒ `shutdown /r /t 0`：立刻重启，且**不弹**系统的倒计时对话框
            var planned = await Task.Run(() => _api.Post("/api/system/reboot", new JsonObject
            {
                ["seconds"] = 0,
                ["mode"] = result.Mode,
                ["reason"] = $"Q-Control 控制中心：显卡模式已切到「{result.Mode}」 {appliedName}，重启后生效",
            }));
            Toast(planned.Ok
                ? "已发起重启，重启后新的显卡模式生效"
                : planned.Why("重启失败"), planned.Ok);
        }
        else
        {
            // 包括 Esc / 点遮罩：当成"稍后重启"，绝不在用户没确认时动系统电源
            Toast($"已写入 EC：模式 {result.Mode} · {appliedName}，请稍后自行重启，重启后生效", true);
        }
    }

    // ================================================================ 已计划的重启

    /// <summary>
    /// 快照里**还有**计划中的重启时，露一行残余秒数 + 取消。
    ///
    /// 本界面自己只发 `/t 0`（见 <see cref="OnGfxClick"/>），所以这一行正常不会出现；
    /// 留着是因为 `/api/system/reboot` 仍接受延迟秒数（外部客户端排的），而"能取消"这句话
    /// 必须有地方兑现 —— 否则界面没有任何取消入口。
    ///
    /// 每秒跟着快照刷新；`planned=false`（到点 / 已取消 / 从没计划过）就收起来。
    /// </summary>
    private void UpdateReboot(StatusRebootDto? reboot)
    {
        var remain = reboot?.Remain ?? 0;
        var planned = reboot?.Planned == true && remain > 0;

        if (planned)
        {
            // ⚠️ 内容没变就别动控件：这一拍每秒都跑，每次赋 Text 都会连带重排这一行
            var text = $"已计划重启：{remain} 秒后";
            if (RebootText.Text != text) RebootText.Text = text;
        }

        var want = planned ? Visibility.Visible : Visibility.Collapsed;
        if (RebootRow.Visibility != want) RebootRow.Visibility = want;
    }

    /// <summary>
    /// 取消计划重启 → 后端 `shutdown /a`。
    /// ⚠️ 后端把 1116（本来就没有关机操作）也算成功，但**其它非 0 表示系统可能还在倒计时**：
    ///    那时必须留住这一行、如实报错，绝不能弹一句「已取消」就把条收掉（2026-10-17 的教训）。
    /// </summary>
    private async Task OnRebootCancel()
    {
        if (_rebootBusy) return;
        _rebootBusy = true;
        RebootCancel.IsEnabled = false;
        try
        {
            var r = await Task.Run(() => _api.Post("/api/system/reboot/cancel"));
            if (r.Ok)
            {
                // 快照有一帧延迟，先自己收起来；真要没停住，下一拍后端会把它顶回来
                RebootRow.Visibility = Visibility.Collapsed;
                Toast("已取消重启 —— 显卡模式的值还在 EC 里，等下次重启才生效", true);
            }
            else
            {
                Toast(r.Why("取消重启失败"), false);
            }
        }
        finally
        {
            _rebootBusy = false;
            RebootCancel.IsEnabled = true;
        }
    }

    // ================================================================ 电源计划

    // 一次后台读：计划清单 + 每条的具体数值（同一个 Task.Run 里做完，界面线程不碰系统调用）
    private void RefreshPlans() => Offload(ReadPlans, RenderPlans);

    /// <summary>上一次渲染电源计划用的数据（null = 还没读到）。换主题时拿它原地重画。</summary>
    private (PowerPlansDto? Plans, Dictionary<string, PowerPlans.PlanValues> Values)? _plansData;

    /// <summary>
    /// 主题变了（由 <see cref="MainWindow"/> 统一转发，页面自己不订 <see cref="UiTheme.Changed"/>，
    /// 原因见 <see cref="DashView.ApplyTheme"/> 的注释）。
    ///
    /// 为什么必须重画：电源计划那几行里的「当前 / 亮度 / CPU 区间」是 code-behind 现建的
    /// TextBlock，颜色抓的是 <see cref="UiTheme.PaletteBrush"/> 返回的**画笔实例**
    /// —— 那个实例是冻结的、不会跟着调色板字典变。不重画的话，切到白天模式后
    /// 这几串字还压在暗色那支中灰上（浅底上糊成一片），而且行不会自己刷新，
    /// 会一直错到下次切档位为止。
    /// </summary>
    public void ApplyTheme()
    {
        if (_plansData is { } data) RenderPlans(data);   // 有缓存就原地重画，别再读一次系统
        else RefreshPlans();
    }

    private (PowerPlansDto? Plans, Dictionary<string, PowerPlans.PlanValues> Values) ReadPlans()
    {
        var plans = _api.Plans();
        return (plans, _api.PlanValues(plans?.Plans?.Select(p => p.Guid)));
    }

    private void RenderPlans((PowerPlansDto? Plans, Dictionary<string, PowerPlans.PlanValues> Values) data)
    {
        var plans = data.Plans;
        var values = data.Values;

        // 留一份给换主题用（见 ApplyTheme）：这几行的小字抓的是 PaletteBrush 的**实例**，
        // 换完主题不重画就一直是上一套色号。
        _plansData = data;

        PlansHost.Children.Clear();
        if (plans?.Plans is not { Count: > 0 } list)
        {
            // 读不到就直说，别留一个空盒子（"没数据"和"加载失败"在界面上是同一件事）
            PlansHost.Children.Add(new TextBlock
            {
                Text = "读不到电源计划",
                Style = (Style)FindResource("HintText"),
            });
            return;
        }

        // 同名多条时靠 tooltip 里的完整 GUID 区分（2026-11-03）。
        // 行内**不再**补 GUID 前 8 位：来源标签（系统 / 厂商）已经能分开那两条同名的了，
        // 再挂一串十六进制只是噪音（用户 13:02 要求删掉）。
        var dupNames = list
            .Where(p => (p.Name ?? "").Trim().Length > 0)
            .GroupBy(p => (p.Name ?? "").Trim(), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var plan in list)
        {
            var active = plan.Guid == plans.Active;
            var row = new DockPanel();
            if (active)
            {
                var tag = new TextBlock
                {
                    Text = "当前",
                    FontSize = 11,
                    Foreground = UiTheme.PaletteBrush("AccBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0),
                };
                DockPanel.SetDock(tag, Dock.Right);
                row.Children.Add(tag);
            }

            // 来源标签（2026-11-03 用户要求"标注清楚哪些是系统的、哪些是原厂的"）。
            // 判据在 Hal：注册表 FriendlyName 是 `@…powrprof.dll,-15,Balanced` 这种
            // **资源引用** ⇒ Windows 自带（名字跟随系统语言）；是"办公""游戏"这种
            // **写死的文本** ⇒ 由厂商软件或别的程序创建。本机这三条的描述分别是
            // 「办公模式 / 游戏模式 / 狂暴模式」，就是原厂 ControlCenter 的三档。
            // ⚠️ 拿不到来源（source 缺席）就**不标**，别把"读不到"画成"厂商的"。
            if (SourceLabel(plan.Source) is { } src)
            {
                var pill = new Border
                {
                    Style = (Style)FindResource("CardTag"),
                    Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = src.Tip,
                    Child = new TextBlock
                    {
                        Text = src.Text,
                        Style = (Style)FindResource("CardTagText"),
                    },
                };
                DockPanel.SetDock(pill, Dock.Right);
                row.Children.Add(pill);
            }

            // 这条计划的几项数值（查一次表，下面亮度 / CPU 两处共用）
            var v = plan.Guid is not null && values.TryGetValue(plan.Guid, out var found)
                ? found
                : default;

            // 屏幕亮度（2026-11-05）：亮度是电源计划里的一项，切档会把它重放出来
            // （本机 平衡 75 / 办公 55 / 游戏 50 / 高性能 40）—— 摆在行内，一眼能看出
            // "切过去会变多亮"，也能解释上面那颗滑块为什么跟着档位走。
            // 只显示交流侧（与 CPU 同口径），电池值在 tooltip 里。
            if (v.BrightAc is { } bright)
            {
                var b = new TextBlock
                {
                    Text = $"亮度 {bright}%",
                    FontSize = 11,
                    Foreground = UiTheme.PaletteBrush("DimBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Add(b);
            }

            // 具体数值：处理器交流侧的最小–最大（差异最直观的那一项）。
            // 只显示交流：笔记本插电才是常态，电池侧塞进来这行就挤了 —— 完整几项在 tooltip 里。
            if (v.MinAc is { } lo && v.MaxAc is { } hi)
            {
                var num = new TextBlock
                {
                    Text = $"CPU {lo}–{hi}%",
                    FontSize = 11,
                    Foreground = UiTheme.PaletteBrush("DimBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };
                DockPanel.SetDock(num, Dock.Right);
                row.Children.Add(num);
            }

            var name = plan.Name ?? plan.Guid ?? "--";

            row.Children.Add(new TextBlock
            {
                Text = name,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = new Button
            {
                Content = row,
                Style = (Style)FindResource(active ? "PlanRowOn" : "PlanRow"),
                Tag = plan.Guid,
                // 悬停给完整数值与（同名时）完整 GUID：这两样都是列表里放不下的
                ToolTip = PlanTip(plan, v, dupNames.Contains((plan.Name ?? "").Trim())),
            };
            button.Click += (_, _) => OnPlanClick(plan.Guid);
            PlansHost.Children.Add(button);
        }
    }

    /// <summary>一行的悬停明细：完整 GUID（仅同名时）+ 六项数值。</summary>
    /// <summary>悬停提示的拼法在 Core（<see cref="PlanTooltip"/>）：名称 / 重名补 GUID / 处理器 / 散热 / 亮度。</summary>
    private static string? PlanTip(PowerPlansPlansItemDto plan, PowerPlans.PlanValues v, bool dupName)
        => PlanTooltip.Build(new PlanTipInput(
            plan.Name, plan.Guid, dupName,
            v.MinAc, v.MaxAc, v.MinDc, v.MaxDc, v.CoolAc, v.CoolDc, v.BrightAc, v.BrightDc));

    /// <summary>来源 → 标签文字与解释；不认识或缺失就返回 null（不标）。</summary>
    private static (string Text, string Tip)? SourceLabel(string? source) => source switch
    {
        "system" => ("系统", "Windows 自带的计划（名字跟随系统语言）。"),
        "vendor" => ("厂商", "不是 Windows 自带的计划 —— 名字由创建它的程序写死，"
                             + "本机这几条就是原厂控制中心的办公 / 游戏 / 狂暴三档。"),
        _ => null,
    };

    private async void OnPlanClick(string? guid)
    {
        if (guid is null) return;
        var result = await Task.Run(() => _api.Post("/api/power/plan", new JsonObject { ["guid"] = guid }));
        Toast(result.Ok ? "已切换电源计划" : result.Why("切换失败"), result.Ok);
        RefreshPlans();
        RefreshBrightness();      // 计划自带「显示器亮度」索引 ⇒ 切完亮度会跟着变
    }

    // ================================================================ 屏幕亮度

    /// <summary>
    /// 读一次真实亮度（WMI，20~50ms ⇒ 后台线程 + Dispatcher 回填）。
    /// 拖动 / 写入期间跳过：那会儿滑块下面已经有用户的意图，被真值盖回去就成了"跟人抢手柄"。
    /// </summary>
    private void RefreshBrightness()
    {
        if (_brightDragging || _brightBusy) return;
        if (BrightSlider.IsMouseCaptureWithin) return;
        Offload(() => _api.Brightness(), RenderBrightness);
    }

    private void RenderBrightness(JsonObject? node)
    {
        var ok = node?["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var level = ok && node?["level"] is JsonValue lv && lv.TryGetValue<int>(out var n) ? n : (int?)null;

        // 读不到内建屏（外接显示器 / 台式机）：整块不摆 —— 不留一个拖了没反应的滑块。
        if (level is null)
        {
            _brightLevel = null;
            BrightBlock.Visibility = Visibility.Collapsed;
            return;
        }

        _brightLevel = level;
        BrightBlock.Visibility = Visibility.Visible;
        SyncBrightEnabled();     // 合盖态可能在这期间变过（窗口关着时合上的盖）

        // 用户正握着滑块（拖动中 / 待提交）⇒ 不回写，等他撒手
        if (_brightDragging || _brightBusy || BrightSlider.IsMouseCaptureWithin) return;
        RenderBrightValue(level.Value);
    }

    /// <summary>
    /// 按真值回填滑块。⚠️ 必须闸住 <see cref="Bright_ValueChanged"/>：
    /// 否则每次回填都会被当成用户操作，接着写一次 WMI（进页即写 + 弹 toast）。
    /// </summary>
    private void RenderBrightValue(int level)
    {
        _brightSyncing = true;
        BrightSlider.Value = level;
        _brightSyncing = false;

        // 合盖时数值位置不摆一个"看起来生效"的百分比 —— 那块屏根本没亮
        BrightValue.Text = LidSwitch.IsClosed == true ? "已合盖" : level + "%";
    }

    /// <summary>
    /// 合盖时置灰并说清原因：内建屏断电，`WmiSetBrightness` 照样返回成功、回读也是旧值，
    /// 界面若还显示"已调到 60%"就是在说一件用户看不见的事。
    /// 盖子状态未知（null = 台式机 / 这台机器不报这个事件）⇒ 保持可用，不猜。
    /// </summary>
    private void SyncBrightEnabled()
    {
        var closed = LidSwitch.IsClosed == true;
        BrightSlider.IsEnabled = !closed;

        if (closed)
            BrightValue.Text = "已合盖";
        else if (_brightLevel is { } level)
            BrightValue.Text = level + "%";
    }

    private void Bright_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var level = (int)Math.Round(e.NewValue);
        BrightValue.Text = level + "%";

        if (_brightSyncing) return;     // 回填，不是用户操作
        if (_brightDragging) return;    // 拖动中只更数字，松手由 DragCompleted 提交

        // 键盘方向键 / 点轨道没有 Drag 事件，用 180ms 防抖兜住（与背光页同一个节奏）
        _brightTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _brightTimer.Tick -= Bright_TimerTick;
        _brightTimer.Tick += Bright_TimerTick;
        _brightTimer.Stop();
        _brightTimer.Start();
    }

    private void Bright_TimerTick(object? sender, EventArgs e)
    {
        _brightTimer?.Stop();
        _ = OnBrightCommit();
    }

    /// <summary>
    /// 把滑块上的值写进显示器。
    /// ⚠️ 成败都以**回读值**渲染：驱动会把 43 量化到 40，界面显示 40 才是真话；
    ///    写失败则退回上一次真值 —— 绝不停在用户拖出来却没生效的位置。
    /// </summary>
    private async Task OnBrightCommit()
    {
        if (_brightBusy)
        {
            // 上一次 POST 还没回来：停在这里会把用户最后拖到的那一格永久丢掉，
            // 180ms 后再试一次（LightView / AboutView 的 debounce 都这么做）。
            _brightTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _brightTimer.Stop();
            _brightTimer.Tick -= Bright_TimerTick;
            _brightTimer.Tick += Bright_TimerTick;
            _brightTimer.Start();
            return;
        }

        var want = (int)Math.Round(BrightSlider.Value);
        if (want == _brightLevel) return;      // 没变就不写（回填 / 写后重复触发都会走到这里）

        _brightBusy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/display/brightness",
                new JsonObject { ["level"] = want }));

            if (result.Raw?["level"] is JsonValue v && v.TryGetValue<int>(out var applied))
            {
                _brightLevel = applied;
                RenderBrightValue(applied);
            }
            else if (!result.Ok && _brightLevel is { } keep)
            {
                RenderBrightValue(keep);       // 连回读值都没有：退回上次真值
            }

            if (!result.Ok) Toast(result.Why("调亮度失败"), false);
        }
        finally
        {
            _brightBusy = false;
        }
    }

    // ================================================================ 显卡超频

    private void RefreshOc() => Offload(() => _api.GpuOc(), RenderOc);

    /// <summary>
    /// 后台读一次、回 UI 线程渲染。
    /// ⚠️ 三种情况都要吞掉：窗口已经关掉（Dispatcher 停了 ⇒ BeginInvoke 抛）、
    ///    后台抛异常（Task 置为 Faulted）、页面已卸载。否则每次关窗都会在日志里
    ///    留一条"看起来像 bug"的异常。
    /// </summary>
    private void Offload<T>(Func<T> work, Action<T> render)
    {
        Task.Run(work).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            try { Dispatcher.BeginInvoke(new Action(() => render(t.Result))); }
            catch { /* 窗口已关，结果没地方放 */ }
        }, TaskScheduler.Default);
    }

    private void RenderOc(GpuOcStateDto? dto)
    {
        _oc = dto;

        if (dto is null)
        {
            // 连 DTO 都没拿到（这一拍读取失败 / 后端线路断了）：卡片留着，但一个能点的都没有
            OcNameBox.Visibility = Visibility.Collapsed;
            OcNow.Text = "--";
            SyncOcEnabled();
            return;
        }

        var cap = dto.Cap;
        var limits = dto.Limits;

        // 型号（nvmlDeviceGetName）
        var name = cap?.Name ?? "";
        OcName.Text = name;
        OcNameBox.Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ApplySliderRange(OcCoreSlider, limits?.GpuOcCore?.Min, limits?.GpuOcCore?.Max,
            cap?.Core?.EffMin, cap?.Core?.EffMax, -1000, 1000);
        ApplySliderRange(OcMemSlider, limits?.GpuOcMem?.Min, limits?.GpuOcMem?.Max,
            cap?.Mem?.EffMin, cap?.Mem?.EffMax, -1000, 3000);

        // 滑块与按钮的可用性**只由 SyncOcEnabled 判定**（别在这里再设一遍 IsEnabled，
        // 否则"能不能写"会有两个真源，按钮漏判就是 2026-11-07 那个 bug 的形状）。
        // 只显示驱动里当前真正生效的值。⚠️ 正拖着滑块时别盖回去（会跟用户抢手柄）
        if (!OcCoreSlider.IsMouseCaptureWithin)
            OcCoreSlider.Value = cap?.Core?.Eff ?? 0;
        if (!OcMemSlider.IsMouseCaptureWithin)
            OcMemSlider.Value = cap?.Mem?.Eff ?? 0;

        _ocDraggingGuard = true;
        SyncOcLabels();
        _ocDraggingGuard = false;

        OcNow.Text = cap?.Ok == true
            ? $"核心 {Signed(cap.Core?.Eff)} / 显存 {Signed(cap.Mem?.Eff)} MHz"
            : "--";

        _ocNvml = dto.Status;
        UpdateOcLive();
        SyncOcEnabled();
    }

    /// <summary>
    /// 「超频 / 复位能不能点」的**唯一判定处**（2026-11-07 补）：NVML 读不到显卡时
    /// （核显模式下独显被固件整个隐藏，`nvmlDeviceGetHandleByIndex` 直接失败），
    /// 两个滑块和两个按钮必须**一起**置灰。
    ///
    /// 改之前这里只给滑块设了 IsEnabled，两颗按钮全生命周期只在请求进行中临时禁用 ——
    /// 于是核显模式下按钮亮着、点了**必然失败**：写路径在 <see cref="QControl.Hal.Nvml.NvmlOc"/>
    /// 的 `Open()` 就抛了，用户拿到的是一句 NVML 的英文异常串。
    ///
    /// ⚠️ 判据只认 **NVML 自己的回读**（cap.ok / available），**不拿 `gfx_mode` 当开关**：
    ///    EC 里那个值只是"存着、下次开机才生效"，切完没重启时它已经变了、显卡却还在正常工作
    ///    （反过来也一样）。`gfx_mode` 只用来**解释原因**（见 <see cref="OcOffReason"/>）。
    /// ⚠️ 也**不按 admin_hint 禁用**：本机实测权限判定会偶发假值（见 AppState.GpuOcState 注释），
    ///    宁可让用户点一下拿到驱动给的真实报错。
    /// </summary>
    private void SyncOcEnabled()
    {
        var dto = _oc;
        var live = dto is { Ok: true, Cap.Ok: true } && dto.Available != false;

        OcCoreSlider.IsEnabled = live && dto?.Cap?.Core?.Raw is not null;
        OcMemSlider.IsEnabled = live && dto?.Cap?.Mem?.Raw is not null;
        OcApplyButton.IsEnabled = live && !_ocBusy;
        OcResetButton.IsEnabled = live && !_ocBusy;

        string text;
        string? tip;
        if (live)
        {
            text = dto!.AdminHint == false ? "需管理员权限" : "就绪";
            tip = null;
        }
        else
        {
            (text, tip) = OcOffReason(dto);
        }

        // 每秒都会走一趟：内容没变就别赋（赋 Text 会连带重排这一行）
        if (OcState.Text != text) OcState.Text = text;
        // ⚠️ 字符串要比**值**：OcOffReason 每次都新建一个内容相同的串，
        //    用 ReferenceEquals 永远判不等 ⇒ 每秒白赋一次（这段注释原本就是这个意思）
        if ((string?)OcStateBox.ToolTip != tip) OcStateBox.ToolTip = tip;
    }

    /// <summary>超频不可用时，右上角那个标签写什么、为什么（点不了的东西必须能问出原因）。</summary>
    private (string Text, string Tip) OcOffReason(GpuOcStateDto? dto)
    {
        // 集显输出：固件把独显整个隐藏 —— 这是"核显模式"下超频用不了的直接原因
        if (_status?.GfxMode is 3)
            return ("核显模式",
                "现在是集显输出模式：独显不参与，NVIDIA 驱动不暴露设备，超频与复位都无处下发。\n"
                + "在「显卡模式」里切回混合输出或独显直连，重启后这里会自动恢复可用。");

        if (dto is null)
            return ("读不到 NVML",
                "读不到 NVIDIA 的超频接口（nvml.dll）：驱动没装 / 没加载，或者独显被固件隐藏了。");

        var why = dto.Cap?.Error is { ValueKind: JsonValueKind.String } err ? err.GetString() : null;
        return ("不可用",
            "NVML 读不到显卡，超频与复位都无处下发。"
            + (string.IsNullOrWhiteSpace(why) ? "" : "\n" + why));
    }


    /// <summary>
    /// 可拖范围 = LIMITS（项目边界）∩ 驱动允许（capability 的 eff_min/eff_max）。
    /// ⚠️ LIMITS 的键是 model.json 里的名字（gpu_oc_core / gpu_oc_mem），不是 core/mem。
    /// </summary>
    private static void ApplySliderRange(Slider slider, long? limitMin, long? limitMax,
        long? driverMin, long? driverMax, double fallbackMin, double fallbackMax)
    {
        var lo = limitMin ?? (long)fallbackMin;
        var hi = limitMax ?? (long)fallbackMax;
        slider.Minimum = Math.Max(lo, driverMin ?? lo);
        slider.Maximum = Math.Min(hi, driverMax ?? hi);
    }

    private void SyncOcLabels()
    {
        if (_ocDraggingGuard) return;      // 别在"按快照回填"的过程中再触发一轮
        OcCoreValue.Text = Signed((long)OcCoreSlider.Value);
        OcMemValue.Text = Signed((long)OcMemSlider.Value);
    }

    private void BuildOcLive()
    {
        // 行序：核心频率 → 显存频率 → 电源状态 → 温度 → 功耗 → 显卡电压
        // （末位对齐总览页那两张卡的"…→电压"）
        _ocRows["clock"] = UiKit.MakeKv(OcLive, "核心频率");
        _ocRows["mem"] = UiKit.MakeKv(OcLive, "显存频率");
        _ocRows["ps"] = UiKit.MakeKv(OcLive, "电源状态");
        _ocRows["temp"] = UiKit.MakeKv(OcLive, "温度");
        _ocRows["power"] = UiKit.MakeKv(OcLive, "功耗");
        _ocRows["volt"] = UiKit.MakeKv(OcLive, "显卡电压");

        _ocRows["ps"].Root.ToolTip = "超频偏移只在 P0（最高性能档）生效；数字越大越省电";
        _ocRows["volt"].Root.ToolTip = "GPU 核心供电电压，来自 HWiNFO（NVML 无此接口）";

        // 六行在卡里均匀铺开（要 align-content:space-evenly 的效果；
        // StackPanel 只会挤在一起，卡片一高就差得很明显）
        UiKit.EnableSpaceEvenly(OcLive);
    }

    private void UpdateOcLive()
    {
        var gpu = _status?.Gpu;
        var volt = _status?.Hwmon?.Gpu?.Voltage;
        var nv = _ocNvml;

        _ocRows["clock"].Set(gpu?.Clock is { } clock ? Fmt(clock) + " MHz" : null);
        _ocRows["mem"].Set(nv?.MemClock is { } mem ? Fmt(mem) + " MHz" : null);
        // ⚠️ pstate 判 `is not null` 而不是真值：P0 就是 0，写 `!= 0` 会把
        //    "正处在最高性能档"这条最该看到的信息吞掉。
        _ocRows["ps"].Set(nv?.Pstate is { } ps ? "P" + ps.ToString(CultureInfo.InvariantCulture) : null);
        _ocRows["temp"].Set(gpu?.Temp is { } temp ? Fmt(temp) + " °C" : null);
        _ocRows["power"].Set(gpu?.Power is { } power ? Fmt(power, 1) + " W" : null);
        // 后端已用 0.2 < v < 2.0 挡过 0 与 19V 输入轨，这里判 null 就够
        _ocRows["volt"].Set(volt is { } v ? Fmt(v, 2) + " V" : null);

        // 全缺就整块收起（项目规矩：不留「--」、不补 0、不拿旧值凑数）
        OcLive.Visibility = _ocRows.Values.Any(r => r.Root.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OcApply_Click(object sender, RoutedEventArgs e)
    {
        if (_ocBusy) return;
        _ocBusy = true;
        SyncOcEnabled();          // 两颗按钮一起禁用（恢复见 finally）
        try
        {
            var body = new JsonObject
            {
                ["core"] = (long)OcCoreSlider.Value,
                ["mem"] = (long)OcMemSlider.Value,
            };
            var result = await Task.Run(() => _api.Post("/api/gpu/oc", body));

            // 后端逐项读回校验，失败原因直接透出来（不做"发出去就算成功"）
            var detail = new List<string>();
            foreach (var key in new[] { "core", "mem" })
                if (result.Raw?[key]?["error"] is JsonValue err && err.TryGetValue<string>(out var s) && s.Length > 0)
                    detail.Add(s);

            Toast(result.Ok ? "超频已生效"
                    : detail.Count > 0 ? string.Join("；", detail) : result.Why("失败"),
                result.Ok);
        }
        finally
        {
            _ocBusy = false;
            // ⚠️ 别写死成 IsEnabled = true：显卡读不到时这两颗按钮本来就该是灰的
            SyncOcEnabled();
            RefreshOc();
        }
    }

    private async void OcReset_Click(object sender, RoutedEventArgs e)
    {
        if (_ocBusy) return;
        _ocBusy = true;
        SyncOcEnabled();
        try
        {
            var result = await Task.Run(() => _api.Post("/api/gpu/oc/reset", new JsonObject()));
            Toast(result.Ok ? "已复位出厂频率" : result.Why("复位失败"), result.Ok);
        }
        finally
        {
            _ocBusy = false;
            // ⚠️ 别写死成 IsEnabled = true：显卡读不到时这两颗按钮本来就该是灰的
            SyncOcEnabled();
            RefreshOc();
        }
    }

    // ================================================================ 造控件

    private void BuildModes()
    {
        for (var i = 0; i < Modes.Length; i++)
        {
            var spec = Modes[i];

            var content = new StackPanel { Orientation = Orientation.Vertical };
            content.Children.Add(UiKit.Icon(spec.Icon, 22, 1.6));
            content.Children.Add(new TextBlock
            {
                Text = spec.Name,
                Style = (Style)FindResource("CardButtonName"),
            });
            content.Children.Add(new TextBlock
            {
                Text = spec.Desc,
                Style = (Style)FindResource("CardButtonSub"),
            });

            var button = new CardButton
            {
                Content = content,
                Style = (Style)FindResource("ModeCard"),
                Margin = new Thickness(0),
                ToolTip = "往 EC 的 APMD 下发该档位并同步切换 Windows 电源计划",
            };
            var number = spec.Number;
            button.Click += (_, _) => OnModeClick(spec.Key, number);
            _modeButtons[spec.Key] = button;

            // 每档下面原来还会挂一行「CPU 35 W」的功耗墙，2026-11-12 整块删了
            // （用户："CPU 的功耗墙也删了"）—— 它靠"进档后采一次"学出来，而固件那头
            // 的墙会随温度和过渡期漂，永远有一部分时间对不上，不如不显示。
            var box = new StackPanel { Margin = new Thickness(0, 0, i == Modes.Length - 1 ? 0 : 8, 0) };
            box.Children.Add(button);

            Grid.SetColumn(box, i);
            ModesHost.Children.Add(box);
        }
    }

    /// <summary>
    /// 「拔掉电源时自动切办公模式」那一行：左边一句说明、右边一颗开关。
    ///
    /// 这是原厂控制中心的行为（拔电切办公、插回电恢复原档），2026-10-31 用户要求对齐，
    /// 真源是 `presets.perf.auto_office_on_battery`（默认开），执行在**后端采样线程**上
    /// （见 AppState.PowerMode.cs）—— 所以关掉界面它照样生效，这里只负责开合与显示。
    ///
    /// ⚠️ 说明只留一行、细节进 tooltip：三档模式卡下面这块是"一屏装得下"页面的余量，
    ///    多一行小字就会把电源计划那块挤下去。
    /// ⚠️ 开关**点击不改 IsOn**（项目铁律），写完统一走 <see cref="SyncAutoRow"/> 读回真值。
    /// </summary>
    private void BuildAutoRow()
    {
        _autoSwitch = new SwitchButton
        {
            Style = (Style)FindResource("SwitchButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _autoSwitch.Click += OnAutoOfficeClick;

        var label = new TextBlock
        {
            Text = "拔掉电源时自动切办公模式",
            Style = (Style)FindResource("RowLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "原厂控制中心就是这样：用电池时自动切到办公模式（省电、更安静），"
                      + "插回适配器再恢复成拔电前那一档。\n"
                      + "在电池上自己换了档，就不再做恢复 —— 以你的选择为准。\n"
                      + "需要管理员权限且「EC 控制」开着，否则什么都不会发生（只记一行日志）。",
        };

        // 一行：标签占满余量、开关钉在右（与设置页那些行同一个观感）
        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_autoSwitch, Dock.Right);
        line.Children.Add(_autoSwitch);
        line.Children.Add(label);

        AutoHost.Children.Add(line);
    }

    /// <summary>
    /// 按真值渲染那一行：开关状态来自配置，显示与否看「有电池 + 有性能模式」。
    /// 两件都成立才摆出来 —— 台式机没有"拔掉适配器"这回事，未适配机型也没有档位可切。
    /// </summary>
    private void SyncAutoRow()
    {
        if (_autoSwitch is null) return;
        _autoSwitch.IsOn = _api.AutoOfficeOnBattery;
        AutoHost.Visibility = _hasPerf && _battPresent == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAutoOfficeClick(object sender, RoutedEventArgs e)
    {
        if (_autoSwitch is null || _autoBusy) return;
        var want = !_autoSwitch.IsOn;         // 点击本身不改 IsOn，这里以当前真值取反

        _autoBusy = true;
        _autoSwitch.IsEnabled = false;
        try
        {
            // 走通用配置项接口（与「启动默认页」「传感器数据源」同一条路），不动 HTTP 契约
            var result = await Task.Run(() => _api.Post("/api/model/preset", new JsonObject
            {
                ["path"] = new JsonArray("perf", "auto_office_on_battery"),
                ["value"] = want,
            }));

            Toast(result.Ok
                    ? want
                        ? "已开启：拔掉适配器自动切办公模式，插回电恢复原来那一档"
                        : "已关闭：插拔电源不再改动性能模式"
                    : result.Why("保存失败"),
                result.Ok);
        }
        finally
        {
            _autoBusy = false;
            _autoSwitch.IsEnabled = true;
            SyncAutoRow();      // 成败都回到真值
        }
    }

    private void BuildGfx()
    {
        for (var i = 0; i < GfxModes.Length; i++)
        {
            var (code, name, desc, icon) = GfxModes[i];

            var content = new StackPanel { Orientation = Orientation.Vertical };
            content.Children.Add(UiKit.Icon(icon, 28, 1.6));
            content.Children.Add(new TextBlock
            {
                Text = name,
                Style = (Style)FindResource("CardButtonName"),
                FontSize = 12.5,
            });

            var button = new CardButton
            {
                Content = content,
                Style = (Style)FindResource("GfxCard"),
                Margin = new Thickness(0, 0, i == GfxModes.Length - 1 ? 0 : 8, 0),
                // 说明只进 tooltip（卡上不放那行小字）
                ToolTip = _api.GfxDesc(code) ?? desc,
            };
            var value = code;
            button.Click += (_, _) => OnGfxClick(value);
            _gfxButtons[code] = button;

            Grid.SetColumn(button, i);
            GfxHost.Children.Add(button);
        }
    }

    // ================================================================ 小工具

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);

    private static string ModeLabel(string key) => key switch
    {
        "office" => "办公模式",
        "game" => "游戏模式",
        "beast" => "狂暴模式",
        _ => key,
    };

    private static string Fmt(double value, int digits = 0)
        => value.ToString(digits == 0 ? "F0" : "F" + digits.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture);

    private static string Fmt(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>带符号的 MHz（+120 / -40 / 0）。</summary>
    private static string Signed(long? value)
        => value is not { } v ? "--" : (v > 0 ? "+" : "") + v.ToString(CultureInfo.InvariantCulture);
}
