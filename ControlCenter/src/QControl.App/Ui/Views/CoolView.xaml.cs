using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QControl.App.Http;
using QControl.App.Http.Contract;
using QControl.App.Ui.Controls;
using QControl.Core.FanCurve;
using ShapePath = System.Windows.Shapes.Path;

namespace QControl.App.Ui.Views;

/// <summary>
/// 散热页：风扇图案 / 手动转速 / 自动风扇曲线（含三档并列、拖点编辑、
/// 温度保护横幅、温度来源、固定曲线、恢复出厂）。
///
/// 看起来"多余"的分支都是必要的，别因为更简洁删掉：
///   · **自动保存**（防抖 0.5 秒），不是"保存曲线"按钮：轮询会用服务器值覆盖本地，
///     改完不点保存时输入框显示新值、内存里还是旧值，再点保存写回去的还是旧曲线
///     —— 表现成「改了但没生效」的隐蔽假成功；
///   · **编辑锁**：跟随模式下点了别的档的「编辑」，该档成为编辑目标，轮询不许改它，
///     否则用户改的是 A 档、保存却写进 B 档槽（切一次模式就白改）；
///   · 解锁判据是「**系统模式自加锁以来变过**」（比 _editLock），不是「模式 == 锁的那档」
///     —— 后者条件恒成立，每次轮询都会立刻解锁；
///   · 本地正在编辑（脏 / 拖点中 / 锁着）时**绝不**用服务器值盖掉曲线：用户的心血
///     比"自动跟上"重要；
///   · 温度保护只播报**跳变**，不能每秒弹一次；
///   · 「切模式换了曲线」由后端记一条 switch 事件，播报完调 ack-switch 清掉，
///     否则每秒轮询都会重弹同一条。
///
/// ⚠️ 写操作一律走 <see cref="LocalApi.Post"/> → PostHandlers（守卫/副作用只有一份）。
/// ⚠️ 慢操作（曲线保存、模式切换）丢后台；UI 线程上只碰 DTO 与控件。
/// </summary>
public partial class CoolView : UserControl
{
    private sealed record ModeSpec(string Key, string Label);

    /// <summary>顺序 = 三档并列的行序（界面从上到下）。</summary>
    /// <summary>名字必须用全称（"办公模式"而不是"办公"）——
    /// 三档并列那几行、跟随徽标、toast 全都用这一份，短写就对不上了。</summary>
    private static readonly ModeSpec[] Modes =
    [
        new("office", "办公模式"),
        new("game", "游戏模式"),
        new("beast", "狂暴模式"),
    ];

    /// <summary>点太多图会糊，也给 EC 侧少点写入抖动（上限 12 点）。</summary>
    private const int MaxPoints = 12;

    /// <summary>自动保存防抖：连续拖动只写一次（500ms）。</summary>
    private const int SaveDebounceMs = 500;

    private readonly LocalApi _api;
    private readonly FanDial _fan1;
    private readonly FanDial _fan2;
    /// <summary>第三路（SYS / 系统风扇）：只有 caps.fan_count ≥ 3 的机型才存在。</summary>
    private readonly FanDial _fan3;
    private readonly Border _spacer3;
    /// <summary>这台机器有没有第三路（`caps.fan_count ≥ 3`）。决定风扇圈与滑块行显不显示。</summary>
    private bool _hasFan3;
    /// <summary>要不要显示 SYS 曲线按钮。三风扇机型恒真；两风扇机型只有显式打开调试开关才真。</summary>
    private bool _showSysCurve;
    private readonly Dictionary<string, CardButton> _srcButtons = new(StringComparer.Ordinal);
    private readonly List<(TextBox T, TextBox D)> _pointInputs = [];

    // ---------------- 本地编辑态（与后端快照分开存，见类注释里的"编辑锁"） ----------------
    private List<CurvePt>? _curve;          // 编辑器当前编辑的那一条（= _curveKind 那一路）
    private List<CurvePt>? _curveCpu;       // CPU 风扇曲线（UFA1，听 CPU 温度）
    private List<CurvePt>? _curveGpu;       // GPU 风扇曲线（UFA2，听 GPU 温度）
    private List<CurvePt>? _curveSys;       // 第三路 SYS 曲线（UFA3）；null = 这台机器没有/没启用
    private string _curveKind = "cpu";      // 编辑器在编辑哪一路：cpu / gpu / sys
    private string? _editMode;      // 编辑器里这条曲线属于哪一档
    private string? _editLock;      // 显式锁定去编辑的档（跟随模式下点了别的档）
    private string? _editLockAt;    // 加锁那一刻的系统模式
    private bool _dirty;
    private bool _saving;
    private int _ver;
    private DispatcherTimer? _saveTimer;
    private string _curveSig = "";
    private string _slotsSig = "";
    private string _capsSig = "";
    private Border? _saveBox;
    private TextBlock? _saveText;
    private string _saveKind = "";
    private bool _busy;
    /// <summary>手动转速滑块：用户拖过、还没写入。true 期间不许拿实际转速回填，否则一秒弹回。</summary>
    private bool _sliderDirty;
    /// <summary>程序自己在写 Slider.Value（回填），别把这次改动当成"用户拖了"。</summary>
    private bool _sliderSyncing;
    /// <summary>上次把滑块贴到实际转速时的模式；只在模式变化时再贴（防"数字自己乱跳"）。</summary>
    private string? _sliderMode;
    // ---------------- 服务端快照 ----------------
    private FanCurveStateDto? _fan;
    private StatusDto? _status;
    private bool _bindMode;
    private string? _fixedMode;
    private bool _protectWas;
    private string? _switchAcked;
    private string? _lastBindText;
    /// <summary>三档行的统一高度（首次布局后量一次）。旗标行比按钮行矮 1.3px，
    /// 选中一条曲线时那一行从按钮换成旗标 ⇒ 行变矮、下面画布跟着变高。</summary>
    private double? _rowH;

    /// <summary>只有 MainWindow 造这一页（它才有 LocalApi）；页面不自己去摸 AppState。</summary>
    internal CoolView(LocalApi api)
    {
        _api = api;
        InitializeComponent();

        _fan1 = new FanDial("CPU 风扇");
        _fan2 = new FanDial("GPU 风扇");
        // 第三路先建好但藏起来：两风扇机型上多一个空圆环就是假数据。
        // 显不显示由 RenderCaps 按 caps.fan_count 决定（见 _hasFan3）。
        _fan3 = new FanDial("SYS 风扇") { Visibility = Visibility.Collapsed };
        _spacer3 = new Border { Width = 15, Visibility = Visibility.Collapsed };
        FansHost.Children.Add(_fan1);
        FansHost.Children.Add(new Border { Width = 15 });
        FansHost.Children.Add(_fan2);
        FansHost.Children.Add(_spacer3);
        FansHost.Children.Add(_fan3);

        // Tag = 编辑目标（cpu / gpu / sys 三路曲线，按原厂语义各听各的温度）。
        // 原厂没有"只听一边 / 取最高"这种曲线来源 —— 别加回那个按钮。
        // SYS 只在三风扇机型（caps.fan_count ≥ 3）上显示，见 RenderCaps。
        foreach (var (key, button) in new (string, CardButton)[]
                 { ("cpu", SrcCpu), ("gpu", SrcGpu), ("sys", SrcSys) })
        {
            button.Tag = key;
            _srcButtons[key] = button;
        }

        Editor.Changed += OnCurveDragged;
        UiKit.FillViewport(Scroll, Root);

        // 行高基准要在「真的可见」之后才量得到：曲线关着时三档区是折叠的。
        // 打开「自定义风扇」当场补量一次，不然这一会话里行高都锁不住。
        SlotsHost.IsVisibleChanged += (_, _) =>
        {
            if (SlotsHost.IsVisible)
                Dispatcher.BeginInvoke(new Action(LearnRowHeight), DispatcherPriority.Loaded);
        };

        // ValueChanged 已经在 XAML 里绑了 FanSlider_Changed，这里不再重复订阅
        Fan1Slider.IsMoveToPointEnabled = true;   // 点轨道直接跳
        Fan2Slider.IsMoveToPointEnabled = true;

        // 主题变化由 MainWindow 统一转发（ApplyTheme）—— 页面自己不订全局事件，
        // 原因见 DashView.ApplyTheme 的注释（关窗 Unloaded 不触发 ⇒ 订阅泄漏 ⇒
        // 下一个窗口切主题时调到死线程上的控件 ⇒ 闪退）。

        // Loaded 之后才读：构造函数可能在 Dispatcher 消息循环起来之前就跑（--page cool 截图路径）
        // ⚠️ 只在**还没吃到过任何快照**时补这一帧：页面每次被切回可见（PageHost.Content 换回来）
        //    都会再触发一次 Loaded，无条件 Update(null) 会把读数清成 "--"、两个环当场归零
        //    再扫回真值 —— 表现就是「重进散热页，风扇圈重新渲染了一遍」，而总览页的 CPU/GPU
        //    环（没有 Loaded 钩子、只吃每秒快照）纹丝不动。这里必须和总览页同一条路径。
        Loaded += (_, _) =>
        {
            if (_status is null) Update(null);
        };
    }

    /// <summary>
    /// 主题变了（由 <see cref="MainWindow"/> 统一转发，页面自己不订 <see cref="UiTheme.Changed"/>，
    /// 原因见 <see cref="DashView.ApplyTheme"/> 的注释）。
    /// </summary>
    public void ApplyTheme()
    {
        Editor.RefreshTheme();
        RenderPoints(false);      // 点位框的字色/底色跟着主题走
        _slotsSig = "";           // 三档小折线的颜色也随主题，强制重建
        RenderSlots();
    }

    // ================================================================ 每秒刷新

    /// <summary>
    /// 重新对齐一次（重复点击侧栏「散热」时由 MainWindow 调）—— 走的就是每秒那一拍：
    /// 拉最新 fancurve 快照 + 重画。编辑中（脏 / 拖点中 / 有编辑锁）由 <see cref="Merge"/>
    /// 自己冻结，不会把正在拖的点弹回原位置。
    /// </summary>
    public void Refresh() => Update(_status);

    /// <summary>主窗口每秒推一份 /api/status 快照（null = 这一拍没数据）。</summary>
    public void Update(StatusDto? status)
    {
        _status = status;
        _fan = _api.FanState();

        if (_fan is not null) Merge();
        RenderFans(status);
        RenderTags();
        RenderCaps(status);
        RenderCurveEnabled();     // 总开关的视觉态：曲线编辑区 / 手动面板置灰
        RenderKindButtons();
        RenderCurve();
        RenderSwitchToast();
    }

    /// <summary>
    /// 把服务端快照并进本地编辑态（loadFc 的那套判定）。
    /// ⚠️ 顺序不能换：先定"编辑器属于哪一档"，再决定要不要用服务器曲线覆盖本地。
    /// </summary>
    private void Merge()
    {
        var s = _fan!;
        var bind = s.BindMode == true;

        if (_editLock is null)
        {
            if (!_dirty) _editMode = bind ? s.PerfMode : StrOf(s.FixedMode);
        }
        else if (s.PerfMode is { } perf && perf != _editLockAt && !_dirty)
        {
            // 系统模式自加锁以来真的变了（原厂控制中心 / FN 键切的）⇒ 锁失效。
            // ⚠️ 绝不能拿 `perf == _editLock` 当条件：锁的是"办公"而系统是"游戏"时
            //    这两者本来就不同，那样写每次轮询都会立刻解锁。
            _editLock = null;
            _editLockAt = null;
            _editMode = s.PerfMode;
        }

        _bindMode = bind;
        _fixedMode = StrOf(s.FixedMode);

        // 两路曲线都收下来（迁移前的老配置后端会补成同一条，行为与早先实现一致）
        // ⚠️ 编辑中（脏 / 拖点中 / 有编辑锁）不许换这两个列表：拖动改的是 _curve 这一份
        //    （编辑器 _points 与它同一对象），轮询每秒把它们换成新对象后，_curve 就和它们
        //    不再是同一份 —— 防抖保存发出去的会是轮询刚拉回的旧曲线，响应回填再把点弹回
        //    原位置（就是"还没拖完就自动保存并回弹"）。
        //    锁着时冻结还多一层正确性：槽位里装的是「锁定那一档」的一对曲线，
        //    不是服务器当前档的曲线，拿它当另一路的保存基线才不会写错槽。
        if (!_dirty && !Editor.IsDragging && _editLock is null)
        {
            var cpu = PointsOf(s.CurveCpu);
            if (cpu.Count > 0) _curveCpu = cpu;
            var gpu = PointsOf(s.CurveGpu);
            if (gpu.Count > 0) _curveGpu = gpu;
            // SYS 是可选的一路：后端给空数组表示"这一路不由曲线控制"，
            // 那就**清掉本地那份** —— 否则界面上会留着一条后端根本没在用的曲线。
            _curveSys = PointsOf(s.CurveSys) is { Count: > 0 } sys ? sys : null;
        }

        // 编辑中不许被服务器值盖掉：脏 / 拖点中 / 有编辑锁
        // （拖点中也得拦：防抖保存可能在拖到一半时完成，那一刻 _dirty 会翻回 false）
        if (_dirty || _editLock is not null || Editor.IsDragging) return;
        if (CurveOf(_curveKind) is { Count: > 0 } pts) _curve = pts;
    }

    private void RenderFans(StatusDto? status)
    {
        var ec = status?.Ec;
        _fan1.SetRpm(ec?.Fan1Rpm);
        _fan2.SetRpm(ec?.Fan2Rpm);
        _fan3.SetRpm(LongOf(ec?.Fan3Rpm));

        // PCH 只有 HWiNFO 给；取不到整行隐藏（项目铁律：不留占位横线、不补 0）
        if (status?.Pch is { } pch)
        {
            PchText.Text = Fmt(pch) + " °C";
            PchText.ToolTip = "PCH（南桥）温度 · 与 HWiNFO 显示的 PCH 一致（共享内存）";
            CoolKv.Visibility = Visibility.Visible;
        }
        else CoolKv.Visibility = Visibility.Collapsed;

        // ⚠️ 卡头不再显示 FN+1 的「飓风 Max / 固件自动」标签（2026-11-25 用户让删）：
        //    FMAX 镜像只说"FN+1 按过"，实测镜像=1 时转速仍可能只有 3252/3511，
        //    挂着"飓风"就是在说假话。满转与否直接看下面两个风扇图案的数字。
        //    `fan_hw_max` 本身还要留着 —— 曲线的 `ApplyHwMax` / OSD 都靠它。

        // 「手动 / 自动」开关：真值只看 fancurve 的 mode
        var manual = _fan?.Mode == "manual";
        // ⚠️ `_fan` 某拍取不到（引擎还没就绪）时**不要动开关**：之前无条件写
        //    `IsOn = _fan?.Mode == "manual"`，取不到那一拍就变成 false ⇒ 开关自己
        //    闪一下再弹回（用户看到的"手动开关乱跳"）。宁可这一拍不刷新。
        if (_fan is not null) ManualSwitch.IsOn = manual;
        // 滑块的可用性归 RenderCurveEnabled 管（2026-11-18：曲线没开时手动控制整体禁用）。
        // 别再在这里写 IsEnabled = true —— 那会把它每一拍又点亮回来。

        // 「最大转速」两副面孔（2026-11-25）：已经临时拉满 ⇒ 变成「恢复自动」。
        // ⚠️ 判据只看后端 `fan_oem_max`，不看本地点击记忆、也不看曲线开关：
        //    曲线"开着但没接上"（非管理员 / 刚开启还没写第一笔）时 EC 里仍是这一半钉的 100，
        //    按钮必须留着"恢复自动"才有一键交回的入口（后端同判据，见 EcFanMax）。
        var oemMax = status?.FanOemMax == true;
        FanMaxButton.Content = oemMax ? "恢复自动" : "最大转速";
        FanMaxButton.ToolTip = oemMax
            ? "交回 EC 固件（0x0206=0 + 三路 UFA=0xFF），风扇恢复按温度自动调速"
            : _fan?.Enabled != true
                ? "临时接管风扇并拉到满转（原厂最大）：不改自定义风扇开关、不改手动模式、不动滑块；"
                    + "再点一次这颗按钮交回固件"
                : "三路转速拉到 100%，并切到手动模式（滑块跟着跳到 100%）";

        // 没被用户改过就把滑块贴到当前实际转速：否则它永远停在下限 30%，
        // 而风扇其实在按曲线跑 60%+ —— 数字和转速对不上，看着就像"滑块没生效"。
        SyncSlidersToActual();

        // 卡头那句"风扇整体状态"
        // 判定顺序（三条优先级坑）在 Core，见 FanTagRule 的注释；这里只负责说话。
        var tagKind = FanTagRule.Decide(
            statusLoaded: status is not null,
            ecPresent: ec is not null,
            oemMax: oemMax,
            enabled: _fan?.Enabled == true,
            manual: manual,
            running: _fan?.Running == true,
            stuckDuty: _fan?.StuckDuty);

        if (tagKind == FanTagKind.Loading) FanModeTag.Text = "读取中…";
        else if (tagKind == FanTagKind.EcFailed) FanModeTag.Text = "EC 读取失败";
        else if (tagKind == FanTagKind.GuardForced)
        {
            // 开关关着、引擎在跑：风扇守护按曲线接管了（写 EC）。
            // 这里绝不能显示「固件控制」—— 界面说没在写、实际在写，是本项目最不能有的错。
            FanModeTag.Text = "守护接管";
            FanModeTag.ToolTip = "固件调速被判定失效（温度变了转速不跟），风扇守护已按曲线接管，"
                + "「自定义风扇」开关仍是关闭状态。"
                + "开一次或关一次「自定义风扇」即回到你自己的选择。";
        }
        // ⚠️ 「自定义风扇关着」排在「手动」之前：关掉时后端会把 mode 归到 auto，
        //    正常走不到这里，但它才是"风扇现在听谁"的**根本答案**，优先级最高。
        //
        //    关掉开关 = **停写 + 真交回固件**（2026-11-18 起，`Stop(release:false)` +
        //    `EcHal.ReleaseToEc`：先 `0x0206=0` 清接管标志，再把三路 UFA 写成 0xFF）。
        //    实测风扇 8 秒内从 1497 回到 3802 RPM，之后跟着温度自己走 —— 不再需要重启/睡眠唤醒。
        //    ⚠️ 只写 `0x0206=0` 没用（EC 继续按钉住的 30% 转）；只写 `0xFF` 更糟
        //    （手动态下 0xFF 是 100%，直接满转）。两条必须按这个顺序一起发。
        //    所以文案分两种，别混成一句：
        //      · EC 里没有我们写的值（交回成功，或本次进程从没写过）⇒ 真·固件控制；
        //      · 还有 ⇒ 交回**失败**了（被拒/固件不认），EC 仍钉在上一个值上。
        else if (tagKind == FanTagKind.OemMax)
        {
            // 临时满转：EC 里确实钉着 100（StuckDuty 也非 null），但那不是"交回失败"，
            // 必须排在下面的 stuck 分支前面，否则用户会看到「已停写 · EC 仍为 100%」。
            FanModeTag.Text = "最大转速（临时）";
            FanModeTag.ToolTip = "未开启自定义风扇，这颗按钮临时接管风扇并拉满；"
                + "再点一次「恢复自动」交回固件（0x0206=0 + 三路 UFA=0xFF），"
                + "或重启 / 睡眠唤醒后自动恢复";
        }
        else if (tagKind is FanTagKind.Firmware or FanTagKind.StoppedStillStuck)
        {
            var keep = _fan?.StuckDuty;
            FanModeTag.Text = keep is null
                ? "固件控制"
                : "已停写 · EC 仍为 " + keep.Value.ToString(CultureInfo.InvariantCulture) + "%";
            FanModeTag.ToolTip = keep is null
                ? "风扇由 EC 固件按温度自动调速（自定义风扇未开启，本程序不写风扇）。"
                    + "要用自定义曲线或手动转速：打开卡头的「自定义风扇」"
                : "已关闭自定义风扇，本程序不再写风扇；但这次没能交回固件，EC 里还留着我们"
                    + "上一次写的 " + keep.Value.ToString(CultureInfo.InvariantCulture) + "%。"
                    + "补偿办法：把「自定义风扇」打开再关掉（会重发一次交回命令），"
                    + "或者睡眠唤醒 / 重启一次。"
                    + "若之前把转速钉得很低、温度顶到过 90°C 以上，EC 可能已经自己接管了 —— "
                    + "实际以风扇读数为准";
        }
        // ⚠️ 「手动」必须排在 stuck 前面：手动模式曲线本来就不跑，后端必然给
        //    stuck_duty 非 null（= 我们写进 EC 的那个值），先判 stuck 就会把用户
        //    **自己选的**转速说成「卡在 60%」—— 看着像故障。
        //    "卡在"只用于「不是手动、但 EC 里还钉着旧值」这种异常。
        //    `Running != true` 是兜底：曲线万一还在跑，就该按「自动」说，不能说手动。
        else if (tagKind == FanTagKind.Manual)
        {
            FanModeTag.Text = "手动";
            FanModeTag.ToolTip = "转速按滑块写的值钉在 EC 里，不随温度变化；"
                + "关掉「手动风扇控制」即回到自动曲线。"
                + "要让风扇完全回到 EC 自己的温度曲线：关掉卡头的「自定义风扇」（几秒内生效）";
        }
        else
        {
            var stuck = _fan?.StuckDuty;
            if (tagKind == FanTagKind.AutoRunning)
            {
                FanModeTag.Text = "自动 · CPU " + Dump(LongOf(_fan?.DutyCpu))
                                + "% / GPU " + Dump(LongOf(_fan?.DutyGpu)) + "%";
                FanModeTag.ToolTip = "由软件曲线按温度调速：CPU 风扇听 CPU 曲线，GPU 风扇听 GPU 曲线"
                    + "（关掉「自定义风扇」即交回 EC 固件，几秒内生效）";
            }
            else if (tagKind == FanTagKind.Stuck && stuck is not null)
            {
                FanModeTag.Text = "卡在 " + stuck.Value.ToString(CultureInfo.InvariantCulture) + "%";
                FanModeTag.ToolTip = "EC 里还钉着我们写过的转速，它不会自己接管。"
                    + "要自动：把风扇切回「自动」（自定义风扇）；"
                    + "要 EC 原生自动：把「自定义风扇」打开再关掉（会重发交回命令："
                    + "清接管标志 0x0206 + 转速要求 0xFF），或睡眠唤醒 / 重启一次";
            }
            else
            {
                // 走到这里 = 不是手动、曲线没跑、EC 里也没留值
                FanModeTag.Text = "自动（未运行）";
                FanModeTag.ToolTip = "曲线没在运行，看曲线区的状态标签";
            }
        }
    }

    /// <summary>曲线自己的两个徽标 + 保存状态（renderFcState / renderFcBind）。</summary>
    private void RenderTags()
    {
        if (_fan is null) return;

        // ---- 曲线状态 ----
        string text, tone;
        string? tip;
        // 自救接管：开关关着、引擎却在跑（只有 Start(force:true) 会出现这种组合）。
        // 这一条必须排在「已关闭 · 固件调速」之前，否则曲线明明在写、界面却说固件在调。
        if (_fan.Enabled != true && _fan.Running == true)
        {
            text = "守护接管中 · CPU " + Dump(LongOf(_fan.DutyCpu)) + "% / GPU " + Dump(LongOf(_fan.DutyGpu)) + "%";
            tone = "warn";
            tip = "固件调速被判定失效，风扇守护已按曲线接管；你的「自定义风扇」开关仍是关闭状态。"
                + "开一次或关一次「自定义风扇」即回到你自己的选择。";
        }
        // 总开关关着（出厂默认）⇒ 曲线没有"在不在跑"这回事，一句话说清现状。
        // 这一条必须排最前：关着时 running=false、last_error 也多半是 null，
        // 走到下面的 else 会显示成「启动中…」，看着像卡在启动阶段。
        else if (_fan.Enabled != true)
        {
            text = "已关闭 · 固件调速";
            tone = "none";
            tip = "自定义风扇未开启，风扇由 EC 固件按温度自动调速。"
                + "打开卡头的「自定义风扇」后才会用这里的曲线接管（手动转速同样需要它）";
        }
        else if (_fan.Running == true)
        {
            text = _fan.Protecting == true
                ? "温度保护中 · CPU " + Dump(LongOf(_fan.DutyCpu)) + "% / GPU " + Dump(LongOf(_fan.DutyGpu)) + "%"
                : "运行中 · CPU " + Dump(LongOf(_fan.DutyCpu)) + "% / GPU " + Dump(LongOf(_fan.DutyGpu)) + "%";
            tone = _fan.Protecting == true ? "hot" : "none";
            tip = _fan.Protecting == true
                ? $"CPU/GPU 达到保护墙 {Fmt(_fan.TempWall ?? 0)}°C，两个风扇都已强制最高转速；降温后自动恢复曲线"
                : "CPU 风扇按 CPU 曲线调速，GPU 风扇按 GPU 曲线调速（与原厂控制中心一致）";
        }
        else if (StrOf(_fan.LastError) is { Length: > 0 } err)
        {
            // 曲线自停了（写入被拒）：说清"已自停"，别显示成"未启用"——
            // 那是用户根本没开过的状态，两者原因不同、该做的动作也不同
            text = "已自停";
            tone = "warn";
            tip = err;
        }
        else if (_fan.Mode == "manual")
        {
            text = "手动接管 · 曲线不跑";
            tone = "none";
            tip = "手动模式下转速由你写死，曲线不参与；关掉手动风扇控制即回到自动";
        }
        else
        {
            text = "启动中…";
            tone = "none";
            tip = "自动曲线正在启动";
        }
        CurveStateTag.Text = text;
        CurveStateTag.ToolTip = tip;
        SetTagTone(CurveStateBox, CurveStateTag, tone);

        // ---- 跟随 / 固定 ----
        string bindText;
        if (_bindMode)
        {
            bindText = "跟随：" + (LabelOf(_status?.Perf?.Mode) ?? "读取中");
            BindModeTag.ToolTip = _status?.Perf?.Mode is not null
                ? "切模式会自动换成对应曲线；编辑器载入的也是这条"
                : "EC 模式读取中…";
        }
        else
        {
            var fixedName = LabelOf(_fixedMode);
            bindText = "固定：" + (fixedName ?? "未指定");
            BindModeTag.ToolTip = fixedName is not null
                ? $"锁在「{fixedName}」这一档：一直用它的曲线，切系统模式也不换"
                : "还没指定固定在哪个模式（切一次系统模式后再打开固定即可）";
        }
        if (bindText != _lastBindText)
        {
            BindModeTag.Text = bindText;
            _lastBindText = bindText;
        }
        SetTagTone(BindModeBox, BindModeTag, "none");

        // 「固定曲线」开关 = bind_mode 取反（默认关 = 跟随）。
        // 它只在渲染里设，点击不改 —— 写失败时不会先亮一下再弹回（见 SwitchButton 注释）。
        FixSwitch.IsOn = !_bindMode;
        // 「应用到全部模式」只在跟随模式下有意义（固定曲线时三档不生效）。
        // ⚠️ 用 Hidden 而不是 Collapsed：Collapsed 会把这一行的高度从「按钮高度」变成
        //    「左边 CPU/GPU 按钮的高度」，开关固定曲线时下面整块会上下跳 2px。
        BindAllButton.Visibility = _bindMode ? Visibility.Visible : Visibility.Hidden;

        // ---- 温度保护横幅（只在触发时出现）+ 跳变幻报 ----
        var on = _fan.Protecting == true;
        AlarmBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            AlarmText.Text = "⚠ 温度保护触发：测得 " + (Dump(LongOf(_fan.ProtectTemp)) ?? "--")
                + " °C，风扇已强制 " + (_fan.MaxDuty?.ToString(CultureInfo.InvariantCulture) ?? "100")
                + "%，降温后自动恢复曲线";
        }
        if (on != _protectWas)
        {
            _protectWas = on;
            Toast(on ? "温度保护已触发：风扇强制最高转速" : "温度已回落，恢复曲线调速", !on);
        }
    }

    /// <summary>
    /// 软件曲线总开关的视觉态（2026-11-18 用户定：默认关 = 风扇听 EC 固件）。
    ///
    /// 关着时**禁用**而不是隐藏：曲线参数还在（用户之前画过的），只是现在不生效 ——
    /// 藏起来会让人以为配置丢了，还会让"恢复到全部模式"之类的按钮凭空消失。
    /// 但禁用必须配一句原因（<see cref="CurveOffNote"/>）：
    /// 只置灰不说原因，用户报障就是"曲线拖不动了"。
    /// （手动面板那侧的一句 2026-11-25 已被用户删掉，置灰即止。）
    /// </summary>
    private void RenderCurveEnabled()
    {
        var on = _fan?.Enabled == true;
        CurveSwitch.IsOn = on;

        CurveOffNote.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // 关着时「跟随/固定」徽标收起：那说的是"切模式换哪条曲线"，曲线没开时没意义，
        // 腾出的位置给 CurveOffNote（行内，不额外占高度）。
        BindModeBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        foreach (var el in new FrameworkElement[] { CurveToolsRow, SlotsHost, PointsHost, Editor })
        {
            el.IsEnabled = on;
            el.Opacity = on ? 1.0 : 0.45;
        }
        FixSwitch.IsEnabled = on;
        BindModeBox.Opacity = on ? 1.0 : 0.45;
        // 手动面板：曲线关着就不能动（2026-11-25 起不再出说明文字）
        var canManual = on;
        ManualSwitch.IsEnabled = canManual;
        Fan1Slider.IsEnabled = canManual;
        Fan2Slider.IsEnabled = canManual;
        Fan3Slider.IsEnabled = canManual;
        FanApplyButton.IsEnabled = canManual;
        // ⚠️ 「最大转速」**不跟曲线开关走**（2026-11-25 用户定）：曲线关着时它是另一半语义 ——
        //    临时接管拉满，再点一次 `0x0206=0 + UFA=0xFF` 交回固件。按钮文案在 RenderFans 里按
        //    `fan_oem_max` 切换（最大转速 ↔ 恢复自动），这里只管它能不能点。
        FanMaxButton.IsEnabled = true;
        // 只压暗滑块区，不压整块：整块 0.55 会让人以为"最大转速"也是灰的。
        FanSliders.Opacity = canManual ? 1.0 : 0.45;
        LockPanel.Opacity = 1.0;
    }

    /// <summary>按机型能力表做减法。只在能力表变化时才动控件。</summary>
    private void RenderCaps(StatusDto? status)
    {
        var caps = status?.Device?.Caps;
        if (caps is null) return;

        var manual = caps.FanManual == true;
        var curve = caps.FanCurve == true;
        // 「EC 偏移标定过」≠「有没有读数」：未标定机型现在有 HWiNFO 兜底（2026-11-02），
        // 转速可能照样有。两件事必须分开判，否则兜底值会被这条 caps 一起藏掉。
        var native = caps.EcRead == true;
        var fans = HasFanRpm(status);
        var temps = HasTemps(status);
        var fanCount = (int)(caps.FanCount ?? 0);
        var debugSys = _fan?.DebugSysCurve == true;
        var sig = manual + "|" + curve + "|" + native + "|" + fans + "|" + temps + "|" + fanCount
                  + "|" + debugSys;
        if (sig == _capsSig) return;
        _capsSig = sig;

        // 第三路：只认机型能力表（`caps.fan_count`）。它是从档案来的，不是猜的 ——
        // 没这个数就是两风扇，多摆一个 SYS 圈和滑块等于骗人。
        _hasFan3 = fanCount >= 3;
        _showSysCurve = _hasFan3 || debugSys;
        _fan3.Visibility = _hasFan3 ? Visibility.Visible : Visibility.Collapsed;
        _spacer3.Visibility = _hasFan3 ? Visibility.Visible : Visibility.Collapsed;
        Fan3Row.Visibility = _hasFan3 ? Visibility.Visible : Visibility.Collapsed;
        SrcSys.Visibility = _showSysCurve ? Visibility.Visible : Visibility.Collapsed;
        SrcSys.ToolTip = _hasFan3
            ? "SYS 风扇的曲线：按 SYS 温度调速（0x0207 a2，三风扇机型才有）"
            : "调试模式：本机没有第三路风扇，这条曲线只保存，不会下发到 EC";
        // 配置被改回两风扇（或换了机器）时，不许把编辑器留在一条用户再也看不到的曲线上
        if (!_showSysCurve && _curveKind == "sys") _curveKind = "cpu";

        LockPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        // 可用性（曲线总开关）由 RenderCurveEnabled 每拍统一管。
        CurveBlock.Visibility = curve ? Visibility.Visible : Visibility.Collapsed;
        // 这一块画的是**转速**（两个叶轮 + 一行 PCH）。转速没有就整块拿掉 ——
        // 摆两个「--」圆环等于放假数据（项目铁律），温度在总览页/曲线里照常显示。
        LeftBlock.Visibility = fans ? Visibility.Visible : Visibility.Collapsed;

        // 没有任何写操作 ⇒ 得有一句话说明"为什么这里啥也没有"
        var noWrites = !manual && !curve;
        CoolNote.Visibility = noWrites ? Visibility.Visible : Visibility.Collapsed;
        CoolNote.Text = native
            ? "EC 写入已在「设置 · EC 控制」里关闭：风扇曲线与手动控制暂时不显示，读数照常"
            : fans
                ? "本机 EC 偏移未标定：转速与温度由 HWiNFO 提供（不是 EC 原生读数）；"
                  + "「关于 · 机型适配」采集一次后自动改回原生"
                : temps
                    ? "本机 EC 偏移未标定，也没读到 HWiNFO 的风扇转速：转速与风扇控制暂时没有"
                      + "（温度仍在总览页与历史曲线里）"
                    : "本机 EC 偏移未标定，也没读到 HWiNFO：温度与转速暂时没有"
                      + "（装好 HWiNFO 或「一键采集」标定后就有了）";
    }

    /// <summary>两路转速有没有（来源可能是 EC，也可能是 HWiNFO 兜底）。</summary>
    private static bool HasFanRpm(StatusDto? status)
        => status?.Ec is { Fan1Rpm: not null } or { Fan2Rpm: not null };

    /// <summary>CPU / 显卡温度有没有（同上，来源不保证是 EC）。</summary>
    private static bool HasTemps(StatusDto? status)
        => status?.Ec is { CpuTemp: not null } or { GpuTemp: not null };

    private void RenderCurve()
    {
        UpdateEditor();
        RenderPoints(skipIfSame: true);
        RenderSlots();
    }

    /// <summary>
    /// 画布上那条「当前温度」竖线跟**正在编辑的那一路**走 ——
    /// CPU 曲线标 CPU 温度、GPU 曲线标 GPU 温度，与后端"各听各"的算法一致。
    /// 曲线没在跑时引擎的 temp 是 null，所以退回快照里的实时温度。
    /// </summary>
    private double? TempNow
    {
        get
        {
            var ec = _status?.Ec;
            var cpu = ec?.CpuTemp is { } c ? (double)c : (double?)null;
            var gpu = _status?.Gpu is { Available: true, Temp: { } gt } ? gt : (double?)null;
            return _curveKind switch
            {
                // SYS 没有"实时温度"兜底：后端不控制第三路时它压根不给这个值，
                // 与其拿 CPU 温度冒充，不如不画那条竖线（Editor 收到 null 就不画）。
                "sys" => _fan?.TempSys,
                "gpu" => gpu ?? cpu,
                _ => cpu ?? gpu,
            };
        }
    }

    private void UpdateEditor()
    {
        Editor.SetData(
            _curve ?? [],
            _curveKind == "sys" ? (_fan?.TempSys ?? TempNow) : (_curveKind == "gpu" ? (_fan?.TempGpu ?? TempNow) : (_fan?.TempCpu ?? TempNow)),
            DutyOf(_curveKind),
            _fan?.Running == true,
            Floor,
            _fan?.TempWall,
            _fan?.Protecting == true);
    }

    /// <summary>当前编辑的那一路的曲线列表。</summary>
    private List<CurvePt>? CurveOf(string kind) => kind switch
    {
        "gpu" => _curveGpu,
        "sys" => _curveSys,
        _ => _curveCpu,
    };

    /// <summary>当前编辑的那一路的后端 duty（null = 这一路没在控制）。</summary>
    private int? DutyOf(string kind) => kind switch
    {
        "gpu" => (int?)LongOf(_fan?.DutyGpu),
        // DutySys 的 DTO 类型是 long?（不是 JsonElement?），别套 LongOf
        "sys" => (int?)_fan?.DutySys,
        _ => (int?)LongOf(_fan?.DutyCpu),
    };

    /// <summary>某一档里某一路的曲线点（SYS 空 = 这一档不控制第三路）。</summary>
    private List<CurvePt> SlotsOf(string kind, string key) => kind switch
    {
        "gpu" => PointsOf(SlotPoints(_fan?.ModeCurvesGpu, key)),
        "sys" => PointsOf(SlotPoints(_fan?.ModeCurvesSys, key)),
        _ => PointsOf(SlotPoints(_fan?.ModeCurvesCpu, key)),
    };

    /// <summary>某一档某一路的签名（判断"要不要重画三档小折线"）。</summary>
    private string SlotSigOf(string kind, string key)
        => string.Join(",", SlotsOf(kind, key)
            .Select(p => p.T.ToString("F1", CultureInfo.InvariantCulture) + ":" + p.D));

    /// <summary>转速下限（曲线不会出现比它更低的点，画布上有一条虚线）。</summary>
    private int Floor => (int)(_fan?.MinDuty ?? 30);

    // ================================================================ 三档并列

    /// <summary>
    /// 三档曲线并列：每行一条迷你折线 + 三个关键温度的转速 + 动作。
    /// 这是"绑模式到底换了什么"唯一说得清的地方 —— 主画布一次只画一条，没对比就看不出换了没有，
    /// 于是好用的功能被当成坏的。
    /// </summary>
    private void RenderSlots()
    {
        if (_fan is null) return;

        // 三档小折线显示的是**正在编辑的那一路**（cpu / gpu / sys）—— 与主画布同一条曲线，
        // 否则"编辑 GPU 曲线、小图却画 CPU"会让人以为改错地方了。
        var sig = string.Join("|",
            _bindMode, _fixedMode, _status?.Perf?.Mode, _editLock, _editMode, _curveKind,
            SlotSigOf(_curveKind, "office"),
            SlotSigOf(_curveKind, "game"),
            SlotSigOf(_curveKind, "beast"));
        if (sig == _slotsSig) return;
        _slotsSig = sig;

        SlotsHost.Children.Clear();
        var following = _bindMode;
        var cur = _status?.Perf?.Mode;

        foreach (var spec in Modes)
        {
            var pts = SlotsOf(_curveKind, spec.Key);
            var isLocked = _editLock == spec.Key;
            var isCur = following && spec.Key == cur;
            var isFixedHere = !following && spec.Key == _fixedMode;
            var isEdit = isLocked || (_editLock is null && spec.Key == _editMode);
            var hot = isLocked || isEdit || isCur || isFixedHere;

            var row = new Border
            {
                Style = (Style)FindResource(isLocked ? "SlotRowEdit" : hot ? "SlotRowOn" : "SlotRow"),
            };
            // 行高锁定成同一份：旗标行天然比按钮行矮一点点，
            // 不锁的话选中曲线时那一行会缩短，整块内容上下跳。
            if (_rowH is { } rowH) row.MinHeight = rowH;

            var line = new DockPanel { LastChildFill = true };
            row.Child = line;

            line.Children.Add(new TextBlock
            {
                Text = spec.Label,
                Style = (Style)FindResource("SlotName"),
            });

            // 行尾动作（先 Dock 右，最后剩下的是"弹性宽度"那条小折线）。
            // ⚠️ 右槽宽度**写死**：按钮在「编辑 / 用这条 / 当前·可编辑 / 固定中」之间切换，
            //    自动宽度会让小折线和温度数字跟着左右平移 —— 开关固定曲线时整行都在抖。
            var act = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var actHost = new Grid { Width = 104 };
            actHost.Children.Add(act);
            DockPanel.SetDock(actHost, Dock.Right);

            if (isLocked)
            {
                // 只有旗标，不摆「跟随当前模式」按钮：退出锁定 = 点「当前」那一行
                // （行点击走 LoadModeCurve，点到当前档就自动解锁跟随）。
                act.Children.Add(Flag("编辑中", edit: true,
                    tip: "下方编辑器载入的就是这条曲线；保存只写回这一档。"
                         + "想回到跟随当前模式：点上面标着「当前」的那一行"));
            }
            else if (following)
            {
                // 跟随模式（固定曲线开关关）：按钮 = 「编辑」= 把这一档载入编辑器。
                // 当前档本来就在编辑器里，不摆按钮（点了也是同一条）。每行最多一个。
                if (isCur)
                {
                    // isEdit = 编辑器真的停在这一档。锁在别的档时旗标只说「当前」，
                    // 点这一行 = 解锁回到跟随（行点击走 LoadModeCurve）。
                    act.Children.Add(Flag(isEdit ? "当前 · 可编辑" : "当前", edit: false,
                        tip: isEdit
                            ? "下方编辑器现在就是这一条：直接拖点或改数字即可，保存写回本档。"
                              + "切系统模式编辑器会自动跟着换"
                            : "点这一行回到跟随当前模式（有未保存的修改会先确认）"));
                }
                else
                {
                    var edit = new Button
                    {
                        Content = "编辑",
                        Style = (Style)FindResource("MiniButton"),
                        ToolTip = $"载入「{spec.Label}」的曲线到下方编辑器（不影响正在跑的曲线，保存只写这一档）",
                    };
                    edit.Click += (_, _) => LoadModeCurve(spec.Key);
                    act.Children.Add(edit);
                }
            }
            else
            {
                // 固定模式（固定曲线开关开）：按钮 = 「用这条」= 把固定曲线换成这一档。
                // 已经固定的那一档就是编辑器目标，只有旗标、没有按钮。
                if (isFixedHere)
                    act.Children.Add(Flag("固定中", edit: false,
                        tip: "固定曲线就属于这一档，下方编辑器改的就是它"));
                else
                {
                    var pick = new Button
                    {
                        Content = "用这条",
                        Style = (Style)FindResource("MiniButton"),
                        ToolTip = $"把固定曲线换成「{spec.Label}」这一档，之后就能编辑它",
                    };
                    pick.Click += (_, _) => UseModeCurve(spec.Key);
                    act.Children.Add(pick);
                }
            }

            line.Children.Add(actHost);

            // 三个关键温度的实际转速：数字比曲线更好判断「安静 vs 激进」
            var nums = new TextBlock
            {
                Style = (Style)FindResource("SlotNums"),
                Inlines =
                {
                    new System.Windows.Documents.Run("60° "),
                    new System.Windows.Documents.Run(DutyText(pts, 60)),
                    new System.Windows.Documents.Run("   75° "),
                    new System.Windows.Documents.Run(DutyText(pts, 75)),
                    new System.Windows.Documents.Run("   90° "),
                    new System.Windows.Documents.Run(DutyText(pts, 90)),
                },
            };
            DockPanel.SetDock(nums, Dock.Right);
            line.Children.Add(nums);

            // 迷你折线：固定 100×30 的坐标系，外面套 Viewbox 拉到整行宽（等比缩放）。
            // ⚠️ 别直接给 Path 设 Stretch=Fill：那会把图形按**自身包围盒**归一，
            //    坐标轴就没了（60→38% 和 60→55% 会画成同一条线，正好毁掉对比的意义）。
            var spark = new ShapePath
            {
                Stretch = Stretch.None,
                Width = 100,
                Height = 30,
                Data = SparkGeometry(pts),
                StrokeThickness = 1.9,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            spark.SetResourceReference(ShapePath.StrokeProperty,
                isLocked ? "Acc2Brush" : hot ? "AccBrush" : "DimBrush");

            line.Children.Add(new Viewbox
            {
                Stretch = Stretch.Fill,
                Height = 26,
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = spark,
                ToolTip = pts.Count == 0 ? "这一档还没有曲线" : null,
            });

            // 点整行 = 载入这一档曲线（原来的「编辑」按钮；行尾只剩一个按钮）。
            // 已经在编辑的那一行不挂事件：无意义重载还会弹"丢弃未保存修改"。
            var canPick = following ? !isEdit : !isFixedHere;
            if (canPick)
            {
                var pickKey = spec.Key;
                row.Cursor = Cursors.Hand;
                row.ToolTip = following
                    ? $"点这一行：把「{spec.Label}」的曲线载入下方编辑器（不影响正在跑的曲线，保存只写这一档）"
                    : $"点这一行：把固定曲线换成「{spec.Label}」这一档";
                row.MouseLeftButtonUp += (_, e) => PickModeRow(pickKey, e);
            }

            SlotsHost.Children.Add(row);
        }

        // 首次布局后把「按钮行」的高度记下来，之后所有行（含旗标行）都按这个高度撑开。
        if (_rowH is null)
            Dispatcher.BeginInvoke(new Action(LearnRowHeight), DispatcherPriority.Loaded);
    }

    /// <summary>量一次三档行的统一高度（取最高的一行 = 带按钮那行）。</summary>
    private void LearnRowHeight()
    {
        if (_rowH is not null || !SlotsHost.IsVisible) return;

        var rows = SlotsHost.Children.OfType<Border>().ToList();
        if (rows.Count == 0) return;
        var h = rows.Max(b => b.ActualHeight);
        if (h < 20) return;      // 还没布局完，等下一拍

        _rowH = h;
        foreach (var b in rows) b.MinHeight = h;
    }

    /// <summary>
    /// 点三档行（不是行里的按钮）= 载入这一档曲线到编辑器。
    /// 跟随模式走 <see cref="LoadModeCurve"/>（点到当前档 = 解锁跟随，不建空锁）；
    /// 固定模式走 <see cref="UseModeCurve"/>（切换固定目标）。
    /// </summary>
    private void PickModeRow(string key, MouseButtonEventArgs e)
    {
        // 行尾按钮自己处理点击，别让冒泡再触发一次载入
        if (e.OriginalSource is DependencyObject src && InsideButton(src)) return;
        e.Handled = true;

        if (_bindMode) LoadModeCurve(key);
        else UseModeCurve(key);
    }

    /// <summary>点击源是不是落在某个按钮里（Run 等非 Visual 元素直接判否）。</summary>
    private static bool InsideButton(DependencyObject node)
    {
        for (var cur = node; cur is Visual || cur is System.Windows.Media.Media3D.Visual3D;)
        {
            if (cur is Button) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    private Border Flag(string text, bool edit, string tip)
    {
        var label = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource(edit ? "SlotFlagTextEdit" : "SlotFlagText"),
        };
        return new Border
        {
            Style = (Style)FindResource(edit ? "SlotFlagEdit" : "SlotFlag"),
            Child = label,
            ToolTip = tip,
        };
    }

    /// <summary>
    /// 迷你折线几何。纵轴按**全档位共同的 30~100** 归一 —— 别每行各按自己最大值缩，
    /// 否则 60→38% 和 60→55% 会画出两条一样的线，正好毁掉对比的意义。
    /// </summary>
    private static Geometry SparkGeometry(List<CurvePt> pts)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            if (pts.Count == 0)
            {
                ctx.BeginFigure(new Point(0, 28), false, false);
            }
            else
            {
                double X(double t) => (t - CurveEditor.TMin) / (CurveEditor.TMax - CurveEditor.TMin) * 100;
                double Y(double d) => 28 - (Math.Max(30, Math.Min(100, d)) - 30) / 70 * 26;
                ctx.BeginFigure(new Point(X(pts[0].T), Y(pts[0].D)), false, false);
                for (var i = 1; i < pts.Count; i++)
                    ctx.LineTo(new Point(X(pts[i].T), Y(pts[i].D)), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    // ================================================================ 点位编辑（自动保存）

    /// <summary>
    /// 重建点位输入框。
    /// <paramref name="skipIfSame"/>：曲线内容没变就只刷状态标签，不动控件 ——
    /// 否则每秒一次轮询会把用户正在改的数字和光标一起抹掉。
    /// </summary>
    private void RenderPoints(bool skipIfSame)
    {
        var sig = Sig(_curve);
        if (skipIfSame && sig == _curveSig && PointsHost.Children.Count > 0)
        {
            RenderSaveState();
            return;
        }
        _curveSig = sig;

        PointsHost.Children.Clear();
        _pointInputs.Clear();

        if (_curve is not null)
        {
            for (var i = 0; i < _curve.Count; i++)
            {
                var index = i;
                var pt = _curve[i];

                var t = new TextBox { Text = Fmt((int)pt.T), Style = (Style)FindResource("PointInput"),
                    ToolTip = "这个点的温度（40~100°C）" };
                var d = new TextBox { Text = pt.D.ToString(CultureInfo.InvariantCulture),
                    Style = (Style)FindResource("PointInput"), ToolTip = "这个点的转速百分比" };

                t.LostFocus += (_, _) => CommitPoint(index, isTemp: true, t.Text);
                d.LostFocus += (_, _) => CommitPoint(index, isTemp: false, d.Text);
                t.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitPoint(index, true, t.Text); };
                d.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitPoint(index, false, d.Text); };

                var del = new TextBlock
                {
                    Text = "×",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand,
                    ToolTip = "删除这个点",
                };
                del.SetResourceReference(TextBlock.ForegroundProperty, "HotBrush");
                del.MouseLeftButtonDown += (_, _) => DeletePoint(index);

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = "≥", Style = (Style)FindResource("PointLabel") });
                row.Children.Add(t);
                row.Children.Add(new TextBlock { Text = "°C →", Style = (Style)FindResource("PointLabel") });
                row.Children.Add(d);
                row.Children.Add(new TextBlock { Text = "%", Style = (Style)FindResource("PointLabel") });
                row.Children.Add(del);

                PointsHost.Children.Add(new Border
                {
                    Style = (Style)FindResource("PointBox"),
                    Child = row,
                });
                _pointInputs.Add((t, d));
            }
        }

        var add = new Button
        {
            Content = "+ 加点",
            Style = (Style)FindResource("MiniButton"),
            Margin = new Thickness(0, 0, 8, 6),
            ToolTip = "在温度跨度最大的区间中间插一个点",
        };
        add.Click += (_, _) => AddPoint();
        PointsHost.Children.Add(add);

        _saveText = new TextBlock { Style = (Style)FindResource("CardTagText"), Text = "已保存" };
        _saveBox = new Border
        {
            Style = (Style)FindResource("CardTag"),
            Child = _saveText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
        };
        PointsHost.Children.Add(_saveBox);
        _saveKind = "";

        RenderSaveState();
    }

    /// <summary>拖动时把数字框跟着改（否则画布在动、数字不动，看起来像两套状态）。</summary>
    private void SyncPointInputs()
    {
        if (_curve is null) return;
        for (var i = 0; i < Math.Min(_pointInputs.Count, _curve.Count); i++)
        {
            var (t, d) = _pointInputs[i];
            var want = Fmt((int)_curve[i].T);
            if (t.Text != want) t.Text = want;
            var wantD = _curve[i].D.ToString(CultureInfo.InvariantCulture);
            if (d.Text != wantD) d.Text = wantD;
        }
    }

    private void CommitPoint(int index, bool isTemp, string text)
    {
        if (_curve is null || index >= _curve.Count) return;
        var v = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (isTemp ? _curve[index].T : _curve[index].D);

        var pt = _curve[index];
        _curve[index] = isTemp
            ? new CurvePt(ClampT(v), pt.D)
            : new CurvePt(pt.T, ClampD(v, Floor));

        Normalize();
        // 改温度可能改变排序 ⇒ 整块重建（被钳制的值要如实显示出来）
        RenderPoints(false);
        UpdateEditor();
        Touch();
    }

    private void DeletePoint(int index)
    {
        if (_curve is null || index >= _curve.Count) return;
        if (_curve.Count <= 2)
        {
            Toast("至少要保留 2 个点", false);
            return;
        }
        _curve.RemoveAt(index);
        RenderPoints(false);
        UpdateEditor();
        Touch();
    }

    private void AddPoint()
    {
        if (_curve is null) _curve = [];

        if (_curve.Count >= MaxPoints)
        {
            Toast($"最多 {MaxPoints} 个点", false);
            return;
        }

        if (_curve.Count == 0)
        {
            _curve.Add(new CurvePt(60, ClampD(60, Floor)));
        }
        else
        {
            // 插在温度跨度最大的区间中间；转速取该处的插值 ——
            // 连点两次不会叠在一起，也不会把折线拽出一个新尖峰
            double at = 0;
            double gap = -1;
            var bounds = new List<double> { CurveEditor.TMin };
            bounds.AddRange(_curve.Select(p => p.T));
            bounds.Add(CurveEditor.TMax);
            for (var i = 1; i < bounds.Count; i++)
            {
                var span = bounds[i] - bounds[i - 1];
                if (span <= gap) continue;
                gap = span;
                at = Math.Round((bounds[i] + bounds[i - 1]) / 2);
            }
            _curve.Add(new CurvePt(ClampT(at), DutyAt(ClampT(at))));
        }

        Normalize();
        RenderPoints(false);
        UpdateEditor();
        Touch();
    }

    /// <summary>拖动（CurveEditor 的回调）：拖动中只防抖，松手才规范化 + 重建输入框。</summary>
    private void OnCurveDragged(bool final)
    {
        if (_curve is null) return;
        SyncPointInputs();
        UpdateEditor();
        if (!final)
        {
            Touch();
            return;
        }
        Normalize();
        RenderPoints(false);
        UpdateEditor();
        Touch();
    }

    private void Touch()
    {
        if (_curve is null) return;
        _ver++;                       // 保存途中又改了？靠它认出来
        _dirty = true;
        RenderSaveState();
        _saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTick;
        _saveTimer.Tick += OnSaveTick;
        _saveTimer.Start();
    }

    private void OnSaveTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        _ = FlushAsync();
    }

    /// <summary>
    /// 落盘。⚠️ 竞态：POST 在飞的时候用户又改了一次，
    /// 这条响应先回来 —— 若无条件"dirty=false + 用旧曲线覆盖"，第二次改动会被顶回原值，
    /// 而标签还写着"已保存"。这里比对版本号，不一致就只补非曲线字段并立刻再存一次。
    /// </summary>
    private async Task FlushAsync()
    {
        if (!_dirty || _curve is null || _saving) return;

        _saving = true;
        RenderSaveState();
        var myVer = _ver;
        // 两路一起发：后端按原厂语义分别写 UFA1/UFA2，只发一路会把另一路留空
        // ⚠️ 正在编辑的那一路必须发 _curve（编辑器拖动改的就是这一份列表）：_curveCpu /
        //    _curveGpu 随时可能被轮询或回填换成别的对象，直接拿它们当发送源会把旧曲线
        //    存回去，响应回填再把点弹回原位置。
        var cpuPts = _curveKind == "gpu" || _curveKind == "sys" ? _curveCpu : _curve;
        var gpuPts = _curveKind == "gpu" ? _curve : _curveGpu;
        var body = new JsonObject
        {
            ["curve_cpu"] = CurveJson(cpuPts ?? []),
            ["curve_gpu"] = CurveJson(gpuPts ?? []),
            // mode = 编辑器载入时的档位键：后端据此把曲线写回对应档位槽。
            // 不传就只写全局 curve，切一次模式这次编辑就没了。
            ["mode"] = _editMode,
        };
        // 第三路只在**正在编辑它**时才发 —— 空数组 = 明确清掉（交回"跟最热/255"的兜底）。
        // 编辑 CPU/GPU 时不发这个键，免得把用户没有在看的 SYS 曲线一起重写一遍。
        if (_curveKind == "sys") body["curve_sys"] = CurveJson(_curve ?? []);

        // 松手那次才要"立刻生效"：拖动途中每 500 ms 的自动保存不带它 —— 否则风扇会
        // 跟着手指逐帧跳转速、还跟采样抢 EC 队列。松手后这里 IsDragging 已为 false，
        // 后端清掉节流，下一轮（≤1 秒）就用上新曲线，不必等满 min_interval（默认 6 秒）。
        if (!Editor.IsDragging) body["apply_now"] = true;

        var result = await Task.Run(() => _api.Post("/api/fancurve/config", body));
        _saving = false;

        if (!result.Ok)
        {
            // 失败保留 _dirty：下一次改动会再试，标签一直显示"保存失败"
            RenderSaveState(result.Why("保存失败"));
            Toast(result.Why("曲线保存失败"), false);
            return;
        }

        if (_ver != myVer)
        {
            RenderSaveState("保存中…");
            _saveTimer?.Stop();
            _saveTimer?.Start();
            return;
        }

        _dirty = false;

        // 拖到一半才回来的响应：不回填、不重绘。回填会把编辑器手里的列表换成新对象，
        // 正在拖的点可能因服务端去重/排序换了下标而跳走；松手后那次保存再取真值回填。
        if (!Editor.IsDragging)
        {
            // 后端两种模式都写档位槽，所以只要指认了档位就取那一槽的**钳制后真值**回填
            if (result.Raw?["cfg"] is JsonObject cfg)
            {
                try
                {
                    var st = cfg.Deserialize<FanCurveStateDto>(Http.ApiResponse.JsonOptions);
                    if (st is not null)
                    {
                        _fan = st;
                        var locked = _editLock ?? _editMode;
                        var mineCpu = PointsOf(SlotPoints(st.ModeCurvesCpu, locked));
                        if (mineCpu.Count > 0) _curveCpu = mineCpu;
                        var mineGpu = PointsOf(SlotPoints(st.ModeCurvesGpu, locked));
                        if (mineGpu.Count > 0) _curveGpu = mineGpu;
                        // 正在编辑 SYS 而这一档没配 SYS 曲线时，CurveOf 返回 null ⇒ 编辑器空白，
                        // 这才是"这一路没启用"的真值；退回 CPU 曲线会让人以为 SYS 已经有曲线了。
                        _curve = CurveOf(_curveKind);
                        _bindMode = st.BindMode == true;
                        _fixedMode = StrOf(st.FixedMode);
                    }
                }
                catch
                {
                    // 回包解析失败不影响"已经存下去了"这件事，下一拍 Update 会把真值拉回来
                }
            }

            RenderPoints(false);
            RenderSlots();
            UpdateEditor();
        }
        RenderSaveState();
    }

    private void RenderSaveState(string? error = null)
    {
        if (_saveBox is null || _saveText is null) return;

        string text, kind;
        string? tip;
        if (error is not null)
        {
            text = "保存失败";
            kind = "hot";
            tip = error;
        }
        else if (_saving)
        {
            text = "保存中…";
            kind = "none";
            tip = null;
        }
        else if (_dirty)
        {
            text = "未保存";
            kind = "warn";
            tip = "改动会在半秒内自动写入 model.json";
        }
        else
        {
            text = "已保存";
            kind = "ok";
            tip = null;
        }

        _saveText.Text = text;
        _saveText.ToolTip = tip;
        if (kind != _saveKind)
        {
            _saveKind = kind;
            SetTagTone(_saveBox, _saveText, kind);
        }
    }

    // ================================================================ 曲线写操作

    /// <summary>把某一档的曲线载入编辑器。跟随模式要上锁，固定模式不用（目标由 fixed_mode 定）。</summary>
    private async void LoadModeCurve(string? mode, bool follow = false)
    {
        if (_fan is null) return;

        // 跟随模式下载入「当前档」没有意义：编辑器本来就跟着它，上锁之后
        // 再点回来还是同一条。统一按"解锁跟随"处理（界面上点「当前」那一行就走这里）。
        if (!follow && mode is not null && _fan.BindMode == true && mode == _fan.PerfMode)
            follow = true;

        var label = follow ? "当前系统模式" : (LabelOf(mode) ?? mode ?? "");
        if (_dirty && !await UiDialog.ConfirmAsync(this, "还有未保存的曲线修改",
                $"当前有未保存的曲线修改，载入「{label}」曲线会丢弃它们。继续？", "继续", "取消"))
            return;

        if (follow)
        {
            _editLock = null;
            _editLockAt = null;
            _dirty = false;
            _editMode = _fan.PerfMode;
            _curve = null;                       // 强制用服务器值重建
            _fan = _api.FanState();
            if (_fan is not null) Merge();
            RenderCurve();
            Toast("已跟随当前系统模式的曲线", true);
            return;
        }

        var result = await Task.Run(() => _api.Post("/api/fancurve/mode-curve",
            new JsonObject { ["mode"] = mode }));
        if (!result.Ok)
        {
            Toast(result.Why("载入失败"), false);
            return;
        }

        _dirty = false;
        var following = _fan.BindMode == true;
        if (following)
        {
            _editLock = mode;                    // 锁住：保存写回这一槽，轮询不许改
            _editLockAt = _fan.PerfMode;
        }
        else
        {
            _editLock = null;                    // 固定模式：目标由 fixed_mode 定
            _editLockAt = null;
        }
        _editMode = mode;

        // 后端回的是三路曲线；正在编辑哪一路就把它放进编辑器（SYS 空 = 这一档不控制）
        var ptsCpu = PointsOf(result.Raw?["curve_cpu"] as JsonArray);
        var ptsGpu = PointsOf(result.Raw?["curve_gpu"] as JsonArray);
        var ptsSys = PointsOf(result.Raw?["curve_sys"] as JsonArray);
        if (ptsCpu.Count > 0) _curveCpu = ptsCpu;
        if (ptsGpu.Count > 0) _curveGpu = ptsGpu;
        _curveSys = ptsSys.Count > 0 ? ptsSys : null;
        _curve = CurveOf(_curveKind);   // SYS 没配就是空白，不拿 CPU 曲线补位

        RenderPoints(false);
        RenderSlots();
        UpdateEditor();
        Toast(following
                ? $"已载入「{label}」曲线，修改后只影响该模式"
                : $"正在编辑固定曲线（属于「{label}」这一档）",
            true);
    }

    /// <summary>三档列表里点某一行的统一入口：跟随=载入编辑；固定=选它作为固定曲线再载入。</summary>
    private async void UseModeCurve(string key)
    {
        if (_fan is null) return;
        if (_fan.BindMode == true) { LoadModeCurve(key); return; }
        if (key == _fixedMode) { LoadModeCurve(key); return; }

        if (_dirty && !await UiDialog.ConfirmAsync(this, "还有未保存的曲线修改",
                "当前有未保存的曲线修改，切换固定曲线会丢弃它们。继续？", "继续", "取消"))
            return;

        var result = await Task.Run(() => _api.Post("/api/fancurve/config",
            new JsonObject { ["fixed_mode"] = key }));
        if (!result.Ok)
        {
            Toast(result.Why("切换失败"), false);
            return;
        }

        ResetLocal();
        _fan = _api.FanState();
        if (_fan is not null) Merge();
        RenderCurve();
        Toast($"已固定为「{LabelOf(key) ?? key}」的曲线，可直接编辑", true);
    }

    // ================================================================ 控件事件

    /// <summary>
    /// 软件曲线总开关（2026-11-18 用户要求）：
    /// 关 = 风扇交给 EC 固件（出厂默认），开 = 自定义曲线接管，手动风扇控制也才可用。
    ///
    /// ⚠️ 关掉会**真的交回固件**（2026-11-18 起）：后端写 `0x0206=0` + 三路 UFA=`0xFF`，
    ///    风扇几秒内回到 EC 自己的温度曲线。回包里的 note 两种口径都可能有
    ///    （已交回 / 交回失败仍在原值），这里**照后端的话说**，别自己编。
    /// </summary>
    private async void CurveSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _fan is null) return;
        var want = _fan.Enabled != true;      // 当前关 ⇒ 目标开

        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/fancurve/enabled",
                new JsonObject { ["enabled"] = want }));
            if (!result.Ok)
            {
                Toast(result.Why(want ? "开启自定义风扇失败" : "关闭自定义风扇失败"), false);
                return;
            }

            var note = result.Raw?["note"] is JsonValue nv && nv.TryGetValue<string>(out var ns)
                ? ns : null;
            Toast(note ?? (want ? "已开启自定义风扇" : "已关闭自定义风扇"), want);

            // 开关会改变"哪些控件可用"、卡头状态与曲线徽标 ⇒ 当场重刷，别等下一拍
            _sliderDirty = false;
            _fan = _api.FanState();
        }
        finally
        {
            _busy = false;
            Update(_status);        // 失败也要按真值把开关拨回去，别留假状态
        }
    }

    private async void Manual_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var want = _fan?.Mode != "manual";

        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/fan/mode",
                new JsonObject { ["mode"] = want ? "manual" : "auto" }));
            if (!result.Ok)
            {
                Toast(result.Why("切换失败"), false);
                return;
            }

            // auto 的回包里带一句 note（"曲线会在一两秒内接管"这类说明），照它说，别自己编
            var note = result.Raw?["note"] is JsonValue nv && nv.TryGetValue<string>(out var ns)
                ? ns : null;
            Toast(want ? "已切手动：转速由滑块固定，不再随温度变" : (note ?? "已切自动"), !want);

            _fan = _api.FanState();
            _sliderDirty = false;    // 切模式后回到"跟着实际转速走"
        }
        finally
        {
            _busy = false;
            Update(_status);        // 无论成败都按真值重刷（失败时开关要拨回去，别留假状态）
        }
    }

    private void SyncSliderLabels()
    {
        Fan1Value.Text = ((int)Fan1Slider.Value).ToString(CultureInfo.InvariantCulture);
        Fan2Value.Text = ((int)Fan2Slider.Value).ToString(CultureInfo.InvariantCulture);
        // Fan3 那两个控件只在三风扇机型上可见，但控件本身一直在（XAML 里 Collapsed）
        if (Fan3Value is not null)
            Fan3Value.Text = ((int)Fan3Slider.Value).ToString(CultureInfo.InvariantCulture);
    }

    private void FanSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 初始化阶段标签还没建好（XAML 解析顺序），空引用一秒钟都别炸
        if (Fan1Value is null || Fan2Value is null || Fan3Value is null) return;
        // 程序回填真值不算"用户改过"，否则回填完就永久锁死为脏、再也不跟实际转速走
        if (!_sliderSyncing) _sliderDirty = true;
        SyncSliderLabels();
    }

    /// <summary>
    /// 把滑块贴到 EC 当前实际转速。
    ///
    /// ⚠️ **只在模式变化时贴一次**（2026-11-18 用户报"手动风扇控制会乱跳"）。
    /// 以前是每拍都贴：自动模式下曲线每秒都在换目标（迟滞、切档、保护墙），
    /// 于是「手动风扇控制」里两个滑块数字每秒自己变 —— 用户看着就是"乱跳"，
    /// 而它其实只是显示曲线当前值。滑块是**输入**，当前值另有卡头徽标
    /// （「自动 · CPU x% / GPU y%」）和风扇图案上的 RPM 在显示。
    ///   贴一次的时机：进页面 / 用户在手动与自动之间切换 / 写完转速。
    /// 手动模式下**永不回填**：EC 里钉的是用户自己写的值，滑块已经是他写的那个；
    /// 回填只会把 `LastDuty`（可能被 FN+1 改过）抄回来，看着像"手动自己变"。
    /// </summary>
    private void SyncSlidersToActual()
    {
        if (_sliderDirty || Fan1Slider.IsMouseCaptureWithin || Fan2Slider.IsMouseCaptureWithin
            || Fan3Slider.IsMouseCaptureWithin) return;

        var mode = _fan?.Mode;
        if (mode is null || mode == "manual") return;   // 手动：滑块 = 用户输入，别动
        if (mode == _sliderMode) return;                // 同一模式：不重复回填（防乱跳）
        _sliderMode = mode;

        _sliderSyncing = true;
        try
        {
            var cpu = LongOf(_fan?.DutyCpu);
            var gpu = LongOf(_fan?.DutyGpu);
            if (cpu is { } c) Fan1Slider.Value = ClampToSlider(c);
            if (gpu is { } g) Fan2Slider.Value = ClampToSlider(g);

            // 第三路没有自己的曲线：自动模式下 HAL 让它跟随最高的那一路（见 EcHal.SetFans），
            // 所以这里也按同一个口径回填 —— 界面上的数就是 EC 里真正写的数。
            if (_hasFan3 && (cpu ?? gpu) is { } any)
                Fan3Slider.Value = ClampToSlider(Math.Max(cpu ?? any, gpu ?? any));
        }
        finally
        {
            _sliderSyncing = false;
        }
        SyncSliderLabels();
    }

    private double ClampToSlider(long value)
        => Math.Min(Fan1Slider.Maximum, Math.Max(Fan1Slider.Minimum, value));

    /// <summary>
    /// 写转速前先把风扇切到手动。自动模式下直接写 EC 会被曲线在几秒内覆盖，
    /// 用户看到的就是"拖了白拖"——所以这一步不让他自己去找那个开关。
    /// 返回 null = 已就绪/切换成功；非 null = 给用户看的原因。
    /// </summary>
    private async Task<string?> EnsureManualAsync()
    {
        // 手动风扇控制的前置条件（2026-11-18 用户定）：自定义风扇必须先开着。
        // 后端 /api/ec/fan 同样会拒，但在这里先拦一句，用户能马上知道该点哪里。
        if (_fan?.Enabled != true)
            return "手动风扇控制需要先开启「自定义风扇」（散热页卡头那颗开关）";

        if (_fan?.Mode == "manual") return null;

        var result = await Task.Run(() => _api.Post("/api/fan/mode",
            new JsonObject { ["mode"] = "manual" }));
        if (!result.Ok) return result.Why("切换手动模式失败");

        _fan = _api.FanState();      // 立刻刷新，别等下一拍 Update 才认
        return null;
    }

    private async void FanApply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // 钳到下限再发：发出去的就是界面上看到的（下限真源 = 滑块 min = 后端硬边界 30）
            var lo = (int)Fan1Slider.Minimum;
            var cpu = Math.Max(lo, Math.Min(100, (int)Fan1Slider.Value));
            var gpu = Math.Max(lo, Math.Min(100, (int)Fan2Slider.Value));
            var sys = _hasFan3 ? Math.Max(lo, Math.Min(100, (int)Fan3Slider.Value)) : (int?)null;

            // 自动模式下先切手动：不切就会被曲线几秒内盖回去
            var switched = _fan?.Mode != "manual";
            if (await EnsureManualAsync() is { } why)
            {
                Toast(why, false);
                return;
            }

            var fanBody = new JsonObject { ["cpu"] = cpu, ["gpu"] = gpu };
            if (sys is { } sv) fanBody["sys"] = sv;

            var result = await Task.Run(() => _api.Post("/api/ec/fan", fanBody));
            if (!result.Ok)
            {
                Toast(result.Why("写入失败"), false);
                return;
            }

            // 用后端回读值回填滑块（后端可能按 boundary / EC 实际值再钳一次）
            _sliderSyncing = true;
            try
            {
                if (LongOfE(result.Raw?["fan1"]) is { } f1) Fan1Slider.Value = ClampToSlider(f1);
                if (LongOfE(result.Raw?["fan2"]) is { } f2) Fan2Slider.Value = ClampToSlider(f2);
                if (_hasFan3 && LongOfE(result.Raw?["fan3"]) is { } f3) Fan3Slider.Value = ClampToSlider(f3);
            }
            finally
            {
                _sliderSyncing = false;
            }
            _sliderDirty = false;        // 已落地，恢复"跟着实际转速走"
            SyncSliderLabels();

            var written = "已写入 " + Dump(LongOfE(result.Raw?["fan1"])) + "/" + Dump(LongOfE(result.Raw?["fan2"]))
                          + (_hasFan3 ? "/" + Dump(LongOfE(result.Raw?["fan3"])) : "") + "%";
            Toast(switched ? "已切到手动并" + written : written, true);
            Update(_status);             // 卡头徽标/开关当场跟着翻，别等下一拍
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 「最大转速」——**一颗按钮两种语义**（2026-11-25 用户定）：
    ///   · 自定义风扇开着：切手动 + 三路 100%，滑块跟着跳（与原来的行为一致）；
    ///   · 自定义风扇关着：临时接管拉满，不改开关 / 模式 / 滑块；再点一次交回固件。
    ///
    /// 为什么曲线关着时也要接管：固件模式下只写 UFA 拉不到满转 ——
    /// `0xFF` 完全不生效、`100` 冲 8 秒就回落，只有 `0x0206=1` + UFA 才持久
    /// （2026-11-25 本机实测，详见 <c>EcHal.SetOemFanMax</c>）。
    /// </summary>
    private async void FanMax_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // 曲线开着：先切手动，否则写了也会被曲线几秒内盖回去
            if (_fan?.Enabled == true)
            {
                if (await EnsureManualAsync() is { } why)
                {
                    Toast(why, false);
                    return;
                }

                _sliderSyncing = true;
                try
                {
                    Fan1Slider.Value = 100;
                    Fan2Slider.Value = 100;
                    if (_hasFan3) Fan3Slider.Value = 100;
                }
                finally
                {
                    _sliderSyncing = false;
                }
                _sliderDirty = false;
                SyncSliderLabels();
            }

            var result = await Task.Run(() => _api.Post("/api/ec/fan-max", new JsonObject()));
            if (!result.Ok)
            {
                Toast(result.Why("失败"), false);
                return;
            }

            // 后端按「曲线开关 + 当前是否已临时满转」决定走哪条，界面只认它回来的 mode
            var mode = result.Raw?["mode"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
            switch (mode)
            {
                case "oem-max":
                    Toast("已临时拉满（原厂最大）；再点一次「恢复自动」交回固件", true);
                    break;
                case "oem-released":
                    Toast("已交回固件：风扇恢复按温度自动调速", true);
                    break;
                default:
                    Toast("已拉满 100%（手动）", true);
                    break;
            }
            // 卡头徽标 / 按钮文案（最大转速 ↔ 恢复自动）的真源是 `/api/status.fan_oem_max`，
            // 由主窗口每秒推送。**这里不要 `Update(_status)`**（2026-11-25 修）：
            // `_status` 是点击**之前**那一帧，拿它重画整页等于把刚变的状态按旧值盖回去
            // —— 卡头先闪回「固件控制」、按钮仍是「最大转速」，1 秒后才翻，比不刷新更糟。
            // 宁可晚一拍（≤1 s），也不显示错的那一拍。
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Fix_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _fan is null) return;
        // 开关语义：IsOn = !bind_mode（见 RenderTags）。点一下 ⇒ 目标 = 当前 bind_mode 取反。
        // ⚠️ 别写成 `fixedWasOn = _fan.BindMode != true` 再发 `!fixedWasOn`：
        //    两次取反又回到**当前值**，后端收到的配置与现状相同 ⇒ 按钮点了什么都不发生。
        var turningFixedOn = _fan.BindMode == true;   // 当前跟随 ⇒ 目标是固定

        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/fancurve/config",
                new JsonObject { ["bind_mode"] = !turningFixedOn }));
            if (!result.Ok)
            {
                Toast(result.Why("切换失败"), false);
                return;
            }

            // 两种方向后端都会动配置（开=锁定 fixed_mode；关=把 curve 对齐当前模式）
            // 且都会换掉编辑器该显示的那条曲线 ⇒ 清本地编辑态 + 重新拉一遍最稳
            ResetLocal();
            _fan = _api.FanState();
            if (_fan is not null) Merge();
            RenderTags();       // 开关/徽标当场跟着翻，不等下一拍 Update（否则看着像没生效）
            RenderCurve();

            var fm = StrOf(_fan?.FixedMode);
            Toast(turningFixedOn
                    ? $"已固定：锁在「{LabelOf(fm) ?? "当前档"}」，切模式不再换曲线"
                    : "已恢复跟随：切模式自动换成对应曲线",
                true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Kind_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not CardButton button) return;
        var kind = (string)button.Tag!;
        if (kind == _curveKind) return;

        if (_dirty && !await UiDialog.ConfirmAsync(this, "还有未保存的曲线修改",
                $"当前编辑的是「{KindLabel(_curveKind)}」曲线，且有未保存的修改。"
                + $"切换到「{KindLabel(kind)}」会丢弃它们。继续？", "继续", "取消"))
        {
            RenderKindButtons();      // 失败也要按真值把高亮拨回去
            return;
        }

        _curveKind = kind;
        _dirty = false;
        _ver++;                                  // 作废在飞的保存请求
        _curve = kind switch
        {
            "gpu" => _curveGpu,
            // SYS 没配过就是空曲线（空 = 这一路不由我们控制），**绝不能拿 CPU/GPU 的曲线冒充**，
            // 否则用户以为在改 SYS，保存时其实改的是刚从别处复制来的点。
            "sys" => _curveSys,
            _ => _curveCpu,
        };
        RenderKindButtons();
        RenderCurve();
        RenderSaveState();
    }

    private static string KindLabel(string kind) => kind switch
    {
        "gpu" => "GPU 风扇",
        "sys" => "SYS 风扇",
        _ => "CPU 风扇",
    };

    private void RenderKindButtons()
    {
        foreach (var (key, button) in _srcButtons) button.IsOn = key == _curveKind;
    }

    private async void BindAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var cur = _curve;
        if (cur is null || cur.Count == 0)
        {
            Toast("当前没有曲线可复制", false);
            return;
        }

        // 会把每一档（含正在编辑的那档）都覆盖掉 —— 有编辑锁时必须先说清楚
        if (_editLock is not null
            && !await UiDialog.ConfirmAsync(this, "覆盖全部三档",
                $"「{LabelOf(_editLock) ?? _editLock}」这一档也会被当前曲线覆盖，确定？",
                "覆盖", "取消", danger: true))
            return;

        _busy = true;
        try
        {
            // 两路一起覆盖：只发一路会让另一档的 GPU 曲线保持旧值（原厂是成对的两条）
            var arrCpu = CurveJson(_curveCpu ?? cur);
            var arrGpu = CurveJson(_curveGpu ?? cur);
            var body = new JsonObject
            {
                ["mode_curves_cpu"] = new JsonObject
                {
                    ["office"] = arrCpu.DeepClone(),
                    ["game"] = arrCpu.DeepClone(),
                    ["beast"] = arrCpu.DeepClone(),
                },
                ["mode_curves_gpu"] = new JsonObject
                {
                    ["office"] = arrGpu.DeepClone(),
                    ["game"] = arrGpu.DeepClone(),
                    ["beast"] = arrGpu.DeepClone(),
                },
            };
            var result = await Task.Run(() => _api.Post("/api/fancurve/config", body));
            Toast(result.Ok ? "已把当前曲线应用到全部三档" : result.Why("应用失败"), result.Ok);
            if (!result.Ok) return;

            // 三档被统一覆盖，编辑器里这条可能已经不是同一档了 —— 解锁回跟随
            _editLock = null;
            _editLockAt = null;
            _editMode = _fan?.PerfMode;
            _fan = _api.FanState();
            RenderCurve();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// 「恢复风扇出厂」：把整节 fan 参数恢复默认，**并且**把三档曲线改成原厂 UserFan1/2/3 里的那三条。
    ///
    /// 后端 `/api/model/reset` section=fan 已统一做了原厂曲线导入（见 PostHandlers.ModelReset），
    /// 所以这里只发一次请求 —— 前端再单独调 import-oem 会重复写盘、还可能把刚对齐的曲线盖掉。
    /// 早先这条只调 reset 时三档曲线纹丝不动（曲线在 perf.modes 不在 fan 节），
    /// 那才是「三档都一样」一直修不好的根因，现在由后端一次性解决。
    ///
    /// 先只读预览原厂真值给用户看（不静默做事），确认后才落盘。只读原厂注册表，
    /// 不写注册表、不让原厂 daemon 接管 —— 与已移除的「交给原厂」无关。
    /// </summary>
    private async void CurveReset_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // ① 只读预览：让用户看清原厂三档真值再决定，不静默覆盖
            var preview = await Task.Run(() => _api.Post("/api/fancurve/oem-preview", new JsonObject()));
            if (!preview.Ok)
            {
                Toast(preview.Why("读原厂曲线失败，未做任何改动"), false);
                return;
            }

            var modes = preview.Raw?["modes"]?.AsObject();
            var desc = new System.Text.StringBuilder();
            if (modes is not null)
            {
                foreach (var m in Modes)
                {
                    var one = modes[m.Key]?.AsObject();
                    if (one is null) continue;
                    var userfan = one["userfan"]?.GetValue<string>() ?? "";
                    desc.Append("· ").Append(m.Label).Append("：")
                        .Append(string.IsNullOrWhiteSpace(userfan) ? "没配过（跳过）" : userfan)
                        .Append('\n');
                }
            }

            if (!await UiDialog.ConfirmAsync(this, "恢复风扇出厂（按原厂三档曲线）",
                    "会把三档风扇曲线改成原厂 UserFan1/2/3 里的那三条（8 档温度 30…100°C）：\n"
                    + desc + "\n"
                    + "同时把温度保护墙、转速下限 / 迟滞 / 写入间隔、跟随 / 固定模式"
                    + "全部还原为出厂默认值；背光、显卡模式等其它设置不受影响。\n"
                    + "只读原厂曲线，不写原厂注册表、不让原厂接管风扇。确定继续？",
                    "恢复出厂", "取消", danger: true))
                return;

            // ② 重置整节 fan 参数 + 由后端读原厂三档曲线写进档案（一次请求搞定）
            var reset = await Task.Run(() => _api.Post("/api/model/reset",
                new JsonObject { ["section"] = "fan" }));
            if (!reset.Ok)
            {
                Toast(reset.Why("恢复失败"), false);
                return;
            }

            // 清掉本地编辑状态再重载：编辑锁 / 未保存标记会挡住服务器值
            ResetLocal();
            _fan = _api.FanState();
            if (_fan is not null) Merge();
            RenderCurve();

            var oem = reset.Raw?["oem_curves"]?.AsObject();
            var oemOk = oem?["ok"]?.GetValue<bool>() == true;
            var n = oem?["written"]?.AsArray().Count ?? 0;
            var skip = oem?["skipped"]?.AsArray().Count ?? 0;
            Toast(oemOk
                ? $"已恢复出厂：三档曲线按原厂导入 {n} 档" + (skip > 0 ? $"，跳过 {skip} 档（原厂没配）" : "")
                : "风扇参数已恢复出厂，但原厂曲线读取失败（这台机器可能没有原厂曲线配置）",
                oemOk);
        }
        finally
        {
            _busy = false;
        }
    }

    // ================================================================ 播报

    /// <summary>
    /// 「切模式换了曲线」的一次性播报：取走后调 ack-switch 清掉，
    /// 否则每秒轮询会把同一条提示反复弹出来。
    /// </summary>
    private async void RenderSwitchToast()
    {
        if (_fan?.Switch is not { } sw || sw.ValueKind != JsonValueKind.Object) return;

        var to = sw.TryGetProperty("to", out var toNode) && toNode.ValueKind == JsonValueKind.String
            ? toNode.GetString() : null;
        var at = sw.TryGetProperty("at", out var atNode) && atNode.ValueKind == JsonValueKind.Number
            ? atNode.GetDouble() : 0;
        var fingerprint = to + "@" + at.ToString("F1", CultureInfo.InvariantCulture);
        if (fingerprint == _switchAcked) return;
        _switchAcked = fingerprint;

        var duty = sw.TryGetProperty("duty", out var dutyNode) && dutyNode.ValueKind == JsonValueKind.Number
            ? (long?)dutyNode.GetInt64() : null;
        var name = LabelOf(to) ?? to;
        Toast(duty is not null
                ? $"已切到{name} · 风扇目标 {duty}%"
                : $"已切到{name} · 已换用该模式曲线",
            true);

        try { await Task.Run(() => _api.Post("/api/fancurve/ack-switch", new JsonObject())); }
        catch { /* 播报没清掉无所谓，指纹挡住重复即可 */ }
    }

    // ================================================================ 小工具

    /// <summary>
    /// 清空本地曲线编辑状态。
    /// ⚠️ 「恢复出厂 / 切绑定 / 载入别的档」都必须先调它 —— 编辑锁与未保存标记会让
    ///    Merge 保留本地曲线、拒绝服务器的新值（"点了没变化"就是这么来的）。
    /// </summary>
    private void ResetLocal()
    {
        _dirty = false;
        _editLock = null;
        _editLockAt = null;
        _editMode = null;
        _curve = null;
        _saving = false;
        _saveTimer?.Stop();
        _saveTimer = null;
        _curveSig = "";
        _slotsSig = "";
    }

    private void Normalize()
    {
        if (_curve is null) return;
        var pts = _curve
            .Select(p => new CurvePt(ClampT(p.T), ClampD(p.D, Floor)))
            .OrderBy(p => p.T)
            .ToList();

        var result = new List<CurvePt>();
        foreach (var p in pts)
        {
            if (result.Count > 0 && Math.Abs(result[^1].T - p.T) < 0.0001) result[^1] = p;
            else result.Add(p);
        }

        _curve.Clear();
        _curve.AddRange(result);
    }

    /// <summary>与后端 fancurve.interp 同一套线性插值（加点时用，新点才不会拽出尖峰）。</summary>
    private int DutyAt(double t)
    {
        if (_curve is null || _curve.Count == 0) return ClampD(60, Floor);
        var sorted = _curve.OrderBy(p => p.T).ToList();
        if (t <= sorted[0].T) return sorted[0].D;
        var last = sorted[^1];
        if (t >= last.T) return last.D;
        for (var i = 1; i < sorted.Count; i++)
        {
            var lo = sorted[i - 1];
            var hi = sorted[i];
            if (lo.T <= t && t <= hi.T)
            {
                var k = (t - lo.T) / Math.Max(1, hi.T - lo.T);
                return (int)Math.Round(lo.D + (hi.D - lo.D) * k);
            }
        }
        return last.D;
    }

    private static double ClampT(double v) => double.IsFinite(v)
        ? Math.Round(Math.Max(CurveEditor.TMin, Math.Min(CurveEditor.TMax, v)))
        : CurveEditor.TMin;

    private int ClampD(double v, int floor)
        => (int)Math.Round(Math.Max(floor, Math.Min(100, double.IsFinite(v) ? v : floor)));
}
