using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using QControl.App.Http.Contract;
using QControl.App.Ui.Views;
using QControl.Core.Window;

namespace QControl.App.Ui;

/// <summary>
/// 主窗口：侧栏导航 + 顶栏（标题/状态胶囊/窗口按钮）+ 页面宿主。
///
/// 页面按需 new、按需缓存：只有**打开过**的页才存在，关窗口整个 Window 就被扔掉
/// （UiHost 负责收线程），所以 WPF 那部分内存跟着窗口走 —— 关掉即释放。
///
/// ⚠️ 关闭按钮 = <see cref="Window.Close"/>，**不是** Hide：后端进程要继续跑（托盘还在），
///    但 WPF 这块要真正还回去。别改成 Hide —— 那样窗口对象和数据绑定全都常驻。
/// ⚠️ 每秒 tick 里只读 LocalApi 的**缓存快照**（CachedStatus），绝不在 UI 线程现算 ——
///    现算会读 EC/PDH，非管理员还会抛，表现是界面一秒一卡。
/// </summary>
public partial class MainWindow : Window
{
    private sealed record NavSpec(string Key, string Label, Geometry Icon);

    private sealed record PageMeta(string Title, string Desc);

    // 业务页（中间那组，等分剩余高度）；顺序 = 侧栏顺序
    private static readonly NavSpec[] MainNav =
    [
        new("dash", "总览", Icons.Dash),
        new("perf", "性能", Icons.Perf),
        new("cool", "散热", Icons.Fan),
        new("light", "背光", Icons.Sun),
        new("sys", "系统", Icons.Sys),
        new("hw", "硬件", Icons.Hardware),
    ];

    // 底部固定区（顺序是用户定的：关于在设置上面）
    private static readonly NavSpec[] FootNav =
    [
        new("about", "关于", Icons.User),
        new("set", "设置", Icons.Gear),
    ];

    // 页面标题 + 副标题
    private static readonly Dictionary<string, PageMeta> Meta = new()
    {
        ["dash"] = new("总览", "处理器 / 显卡 / 温度趋势"),
        ["perf"] = new("性能", "性能模式 · 显卡模式 · 电源计划 · 超频"),
        ["cool"] = new("散热", "风扇 · 自动风扇曲线"),
        ["light"] = new("背光", "键盘背光 · 分区配色 · 灯效 · 亮度"),
        ["sys"] = new("系统", "存储 · 电池 · 运行状态 · 机型配置"),
        ["hw"] = new("硬件", "处理器 · 显卡 · 主板 · 内存 · 存储 · 屏幕 · 网络 · EC 实测"),
        ["about"] = new("关于", "作者主页 · 应用信息 · 日志"),
        ["set"] = new("设置", "启动与窗口 · 界面与提示 · 原厂软件"),
    };

    private readonly LocalApi _api;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, ToggleButton> _nav = new();

    /// <summary>
    /// 「引导适配」的置顶小窗。生命周期挂在**主窗口**而不是关于页：采集那 1 分钟里
    /// 用户可能翻去别的页看温度，页面卸载会停掉自己的轮询 —— 那时弹窗就不能浮出来了。
    /// </summary>
    private GuideWindow? _guideWindow;

    /// <summary>
    /// 稳定态刷新间隔（插电 0.5 秒一拍、电池 2 秒一拍，见 `SampleIntervalAcMs`）。
    ///
    /// 2026-11-15 从 1 秒提到 500 ms：后端采样本身已经是半秒一档，界面还按整秒刷的话，
    /// 新帧最多要等满一秒才上屏 —— 那半秒是纯延迟，而这一层每拍只是取一次缓存引用
    /// （`LocalApi.Status` 命中缓存时按引用判等，见它的注释），成本可以忽略。
    /// </summary>
    private static readonly TimeSpan IdleTick = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// **首帧**用的快节奏（见 <see cref="MarkFirstData"/>）。
    ///
    /// 为什么需要：开窗那一瞬间缓存里还是托盘态的精简帧，真正能画的全量帧要等采样
    /// 线程下一拍（一拍 ≈ 0.9 秒，全花在 EC 往返的人为节流上）。按 1 秒一刷的话，
    /// 就算数据 0.3 秒就到了，界面也要干等到下一个整秒 —— 那就是用户看到的"打开界面
    /// 半天不出数"。首帧前用 150 ms 探，数据一到手立刻切回 1 秒。
    /// 成本可忽略：<see cref="LocalApi.Status"/> 命中缓存时只是按引用判等（见它的注释），
    /// 真正反序列化只在新帧落地的那一下发生。
    /// </summary>
    private static readonly TimeSpan WarmupTick = TimeSpan.FromMilliseconds(150);

    /// <summary>从窗口对象开始构造起的计时（开窗耗时日志用）。</summary>
    private readonly System.Diagnostics.Stopwatch _openClock = System.Diagnostics.Stopwatch.StartNew();
    private long _ctorMs;
    private long _loadedMs;
    private bool _firstData;

    /// <summary>
    /// 首帧到手：切回 1 秒节奏，并打一行开窗耗时。
    ///
    /// 这一行是"打开界面多久出数"的**唯一**判据（构造 / Show / 首帧三段分开记），
    /// 以后有人问"能不能再快点"，先看这三个数再动手。
    /// </summary>
    private void MarkFirstData(bool hasData = true)
    {
        if (_firstData) return;
        _firstData = true;
        _timer.Interval = IdleTick;
        Console.WriteLine(hasData
            ? $"[ui] 开窗耗时：构造 {_ctorMs} ms / 首帧起画 {_loadedMs} ms / 出数 {_openClock.ElapsedMilliseconds} ms"
            : $"[ui] 开窗耗时：构造 {_ctorMs} ms / 首帧起画 {_loadedMs} ms（没有数据源，按读不到渲染）");
    }

    /// <summary>
    /// 当前**正在显示**的页。刷新只认这一个 —— 见 <see cref="Push"/> 的注释。
    /// </summary>
    private string? _current;

    /// <summary>当前显示的页键。UiHost 用它判断"启动页是不是已经落好了"，避免重复 ShowPage。</summary>
    internal string? CurrentPageKey => _current;

    /// <summary>导航图标单独留一份引用：选中态要给它挂发光（.nav-i.on svg 的 drop-shadow 等价物）。</summary>
    private readonly Dictionary<string, ShapePath> _navIcons = new();

    /// <summary>
    /// 选中图标的淡光（CSS: filter: drop-shadow(0 0 6px rgba(77,214,255,.55))）。
    /// 只在 1 颗图标上，成本可以忽略；换成 Effect 批量上就会掉帧。
    /// ⚠️ 颜色要跟着主题重建：浅色主题的 Acc 是深蓝 #0f86cc，套暗色那支荧光青会发脏。
    /// </summary>
    private System.Windows.Media.Effects.DropShadowEffect? _navGlow;

    private void RebuildNavGlow()
    {
        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = UiTheme.Palette.Acc,
            BlurRadius = 10,
            ShadowDepth = 0,
            Opacity = 0.55,
        };
        glow.Freeze();
        _navGlow = glow;
    }
    private readonly Dictionary<string, FrameworkElement> _pages = new();

    private DashView? _dash;
    private PerfView? _perf;
    private CoolView? _cool;
    private LightView? _light;
    private SysView? _sys;
    private HardwareView? _hw;
    private AboutView? _about;
    private SettingsView? _set;
    private TextBlock? _chipCpu;
    private TextBlock? _chipGpu;
    private TextBlock? _chipFan;
    private TextBlock? _chipEc;
    /// <summary>胶囊外框 —— 没数据时整颗收起来（不留「风扇 --」这种空壳）。</summary>
    private Border? _chipGpuHost;
    private Border? _chipFanHost;
    private Border? _warnAdmin;
    private Border? _warnDevice;
    private Button? _themeButton;

    // 提示条（toast）：整窗只有一个，新的覆盖旧的
    private Border? _toast;
    private TextBlock? _toastText;
    private DispatcherTimer? _toastTimer;

    /// <param name="themeOverride">只给 <c>--ui-shot</c> 用：截图要固定暗/亮，不能受用户偏好影响。</param>
    public MainWindow(AppState? state, string? startPage = null, string? themeOverride = null)
        : this(new LocalApi(state), startPage, themeOverride)
    {
    }

    internal MainWindow(LocalApi api, string? startPage = null, string? themeOverride = null)
    {
        _api = api;

        // 主题必须在 InitializeComponent 之前定下来：XAML 里的 DynamicResource 会在加载时
        // 先解析一遍，字典里还没有键就会一路空着（不报错，但首帧是白的）。
        UiTheme.Load(themeOverride ?? api.SavedTheme);

        InitializeComponent();
        // ⚠️ 调色板不是"灌进窗口"就完事：页面（SysView / HardwareView …）里 cs 现建的
        //    控件在挂进窗口逻辑树之前就会 FindResource 画笔，只灌窗口会查不到（抛
        //    ResourceReferenceKeyNotFoundException + NamedObject→Brush 强转失败）。
        //    所以挂的是 UiTheme 那份**共享字典**（Resolve 里已经灌好），窗口和每个
        //    页面都合并它 —— 谁查都查得到，换主题也只要重灌那一份。
        UiTheme.AttachPalette(this);

        FitToScreen();
        RebuildNavGlow();
        BuildNav();
        BuildFooter();
        BuildChips();
        // 启动落点：--page（含 --ui-shot 的指定页）> presets.ui.start_page > 总览。
        // ⚠️ 只在这里读，不要挪到 Program 的 OpenUi：那条路同时负责"叫醒已有窗口"，
        //    把默认页传下去会让用户每次点托盘都被拽回启动页。
        ShowPage(startPage is not null && Meta.ContainsKey(startPage)
            ? startPage
            : _api.SavedStartPage is { } saved && Meta.ContainsKey(saved) ? saved : "dash");

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = WarmupTick };
        _timer.Tick += OnTick;

        UiTheme.Changed += OnThemeChanged;
        // 主窗口关掉时把引导小窗一并收掉（不设 Owner，最小化 Q-Control 时它要继续浮着）
        Closed += (_, _) => CloseGuideWindow();
        _ctorMs = _openClock.ElapsedMilliseconds;
        Loaded += (_, _) =>
        {
            _loadedMs = _openClock.ElapsedMilliseconds;
            _timer.Start();
            OnTick(null, EventArgs.Empty);          // 立刻出一帧，别让用户先看一秒"读取中…"
            PrewarmBacklight();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            DetachTheme();
        };
        StateChanged += (_, _) => SyncMaximizeGlyph();
    }

    // ---------------------------------------------------------------- 窗口外观

    /// <summary>
    /// 空闲预热背光页的柔光遮罩。
    ///
    /// 为什么：本程序是**软件渲染**（见 <see cref="UiHost"/>），背光页键盘图的三区柔光
    /// 要靠一次高斯模糊（6 张遮罩，实测约 50ms）—— 那是一次性成本，但付在"点击侧栏
    /// 背光"的那一帧上就是肉眼可见的卡。挪到窗口显示后 1.2 秒（此时用户正在看总览页的
    /// 数字，不会注意），点进去就只剩构建页面本身。
    /// ⚠️ 只在**还没建过背光页**时预热：建过了说明遮罩早缓存好了，再算一遍纯浪费。
    /// </summary>
    private void PrewarmBacklight()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_pages.ContainsKey("light")) return;
            try { Controls.KeyboardView.Prewarm(); }
            catch { /* 预热失败不影响功能：真进页面时会再算一次 */ }
        };
        timer.Start();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyRoundedCorners();
        // 最大化守卫要在窗口句柄就绪后装：否则最大化会连任务栏一起盖住，
        // 侧栏最底那颗「设置」被任务栏吃掉（见 MainWindow.Maximize.cs）。
        InstallMaximizeGuard();
        // 补回 WS_CAPTION：没有它 DWM 不播收栏动画（见 MainWindow.Caption.cs）。
        // 必须在安装最大化守卫之后 —— 那个钩子只关心 WM_GETMINMAXINFO，互不干扰。
        InstallCaptionGuard();

    }

    /// <summary>
    /// Win11 的窗口圆角（14px）。
    ///
    /// WindowStyle=None 的窗口默认是**直角**，不处理一眼就能看出别扭；
    /// DWM 的 DWMWA_WINDOW_CORNER_PREFERENCE(=33) 设成 ROUND(2) 就圆回来了，
    /// 系统阴影也会跟着回来。Win10 上没有这个属性，调用返回失败 —— 忽略，退回直角。
    ///
    /// ⚠️ 只能这样圆：`AllowsTransparency=true` + WPF 自己裁圆角会关掉硬件合成，
    ///    得不偿失（见 WindowChrome 那段注释）。
    /// </summary>
    private void ApplyRoundedCorners()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var round = 2;                       // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        }
        catch
        {
            // 圆角是锦上添花，拿不到就算了（Win10 / 被系统策略关掉）
        }
    }

    // ---------------------------------------------------------------- 构造

    /// <summary>
    /// 摆位：按**工作区**（扣掉任务栏）算，高度封顶 880，
    /// 窗口再高也只是多留白，不该把卡片拉成长条。
    /// </summary>
    private void FitToScreen()
    {
        // 窗口尺寸口径：宽的 62%、高的 84%（物理像素算完再按
        // 缩放折回逻辑单位；这里直接用 WorkArea 的逻辑尺寸，比例等价，且天然跟着 DPI 走）。
        // ⚠️ 别按"能放多大放多大"来：窗口一宽，卡片就被摊平、留白变多，
        //    观感反而不如那套比例紧凑。
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Round(area.Width * 0.62));
        Height = Math.Max(MinHeight, Math.Round(area.Height * 0.84));
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void BuildNav()
    {
        for (var i = 0; i < MainNav.Length; i++)
        {
            // 每项占一个等高行 ⇒ 与 CSS 的 justify-content:space-evenly 同一个观感
            NavHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var button = MakeNavButton(MainNav[i]);
            Grid.SetRow(button, i);
            NavHost.Children.Add(button);
        }
    }

    private ToggleButton MakeNavButton(NavSpec spec)
    {
        var icon = UiKit.Icon(spec.Icon, 21);
        _navIcons[spec.Key] = icon;
        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock
        {
            Text = spec.Label,
            FontSize = 12,
            // 7 而不是 5：选中时图标挂着 BlurRadius=10 的发光（见 RebuildNavGlow），
            // 只留 5 的话光晕会压到字上，看着像"字被图标盖住"。7 给光晕留出余量。
            Margin = new Thickness(0, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var button = new ToggleButton
        {
            Content = content,
            Style = (Style)FindResource("NavItem"),
            Focusable = false,
            Tag = spec.Key,
            // 高度**不在这里写**：由 NavItem 里的 MinHeight 定下限（62 = 21 图标 + 7 间距 +
            // 16 文字 + 上下 9 内边距），内容说了算。写死 Height 会比内容还矮，
            // 标签就贴着胶囊下沿。
            // 为什么还要 VerticalAlignment=Center：行是等高的（space-evenly 的等价物），
            // 不居中的话按钮被拉满整行，选中态的淡蓝底就变成一条通高色块。
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => ShowPage(spec.Key);
        _nav[spec.Key] = button;
        return button;
    }

    private void BuildFooter()
    {
        // 底部区 = 分割线 → 月亮 → 关于 → 设置。上 8 / 条目间 7。
        // （border-top 之后 padding-top:10，条目之间 gap:7）。
        // ⚠️ 别用**零间距**：那样底下三颗紧贴着叠在一起，和上面 6 个业务入口的
        //    舒展节奏对不上，看着像"挤在角落"。
        const double footGap = 7;

        // 分割线用 DynamicResource：侧栏是**常驻**的，抓 PaletteBrush 实例会在切主题后
        // 留下一根深色线（浅色下尤其突兀）。
        // 上 8：分割线跟上面最后一个入口之间要留气口，否则它像被"挤"上去贴着「硬件」；
        //       下 10 = 底部区的上沿内边距。
        var footRule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 10) };
        footRule.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        FootHost.Children.Add(footRule);

        _themeButton = new Button
        {
            Style = (Style)FindResource("RailButton"),
            ToolTip = "切换白天 / 黑夜模式",
            Margin = new Thickness(0, 0, 0, footGap),
        };
        _themeButton.Click += (_, _) => ToggleTheme();
        FootHost.Children.Add(_themeButton);
        UpdateThemeIcon();

        for (var i = 0; i < FootNav.Length; i++)
        {
            var button = MakeNavButton(FootNav[i]);
            // 最后一颗不留底边距：多出来的那段会变成侧栏底部的空档
            button.Margin = new Thickness(0, 0, 0, i == FootNav.Length - 1 ? 0 : footGap);
            FootHost.Children.Add(button);
        }
    }

    private void BuildChips()
    {
        // 顺序：CPU / GPU / 风扇 / EC，警告类跟在后面（默认隐藏）
        ChipHost.Children.Add(UiKit.Chip(this, "CPU", out _chipCpu));
        _chipGpuHost = UiKit.Chip(this, "GPU", out _chipGpu);
        ChipHost.Children.Add(_chipGpuHost);
        _chipFanHost = UiKit.Chip(this, "风扇", out _chipFan);
        ChipHost.Children.Add(_chipFanHost);
        ChipHost.Children.Add(UiKit.Chip(this, "EC", out _chipEc));

        _warnAdmin = WarnChip("⚠ 需管理员",
            "非管理员：EC 写入会被接受却不生效，所以写操作一律被拒。\n"
            + "点这里可以以管理员身份重启（会弹一次 UAC，同意后窗口自动重开）。");
        // 徽章本身就是入口：只读实例上唯一该给的动作就是"换成管理员再来一遍"。
        // 实现在 MainWindow.Elevate.cs（partial），这里只接线。
        _warnAdmin.Cursor = Cursors.Hand;
        _warnAdmin.MouseLeftButtonUp += (_, _) => AskElevate();
        _warnDevice = WarnChip("⚠ 写入未验证 · 只读",
            "这台机型的写入没有经过作者复核，或「EC 控制」被关掉：写操作一律 403。"
            + "\n文案与提示按 status.device 现算（见 UpdateChips），这里只是初始值。");
    }

    private Border WarnChip(string text, string tip)
    {
        var chip = new Border
        {
            Style = (Style)FindResource("ChipWarn"),
            Child = new TextBlock { Text = text, Style = (Style)FindResource("ChipWarnText") },
            ToolTip = tip,
            Visibility = Visibility.Collapsed,
        };
        ChipHost.Children.Add(chip);
        return chip;
    }

    // ---------------------------------------------------------------- 翻页

    /// <summary>切页。internal 是因为 UiHost 要在"窗口已经开着"时把指定页顶上来。</summary>
    internal void ShowPage(string key)
    {
        if (!Meta.TryGetValue(key, out var meta)) return;

        _lastPage = key;          // 提权重启时用，见 MainWindow.Elevate.cs
        // 重复点当前这一项（侧栏已选中）不算翻页：刷新照做，但**不要再播一次转场** ——
        // 那看起来像"闪了一下"，而不是"切过去了"。
        var changed = _current != key;
        _current = key;

        foreach (var (name, button) in _nav)
        {
            var on = name == key;
            button.IsChecked = on;
            // 选中项图标发光：CSS 那边是 filter: drop-shadow(0 0 6px rgba(77,214,255,.55))
            if (_navIcons.TryGetValue(name, out var icon))
                icon.Effect = on ? _navGlow : null;
        }

        PageTitle.Text = meta.Title;
        PageDesc.Text = meta.Desc;

        if (!_pages.TryGetValue(key, out var view))
        {
            // 首次进入某页要现建整棵可视树（性能页/散热页最重）。超过 60ms 才记一行：
            // 正常情况这条日志是安静的，只有**真的慢**才会冒出来 —— 以后有人说
            // "翻页卡一下"，先看日志里有没有这行，再决定要不要动那一页的构造。
            var started = Environment.TickCount64;
            view = CreatePage(key);
            _pages[key] = view;
            var cost = Environment.TickCount64 - started;
            if (cost >= 60) Console.WriteLine($"[ui] 首次构建 {key} 页 {cost} ms");
        }
        else if (changed)
        {
            // 再次进入「切页时拉一次」的页 ⇒ 刷新（因为这些内容在变：
            // 日志尾巴在长、后台采集可能已经跑完、区间记录状态可能变了）
            // ⚠️ 只有 about / hw 是"切页拉一次"的页；其它页每秒都在吃快照，
            //    切页时再拉一遍纯属重复请求（电源计划列表、原厂进程要查 COM）。
            if (key == "about") _about?.Refresh();
            else if (key == "hw") _hw?.Refresh();
        }

        // ⚠️ 必须在 Refresh 之前把共享调色板挂到页面上：页面这时的控件还没进窗口
        //    逻辑树，FindResource("TxtBrush"/"DimBrush") 一路查到空会连抛两条异常。
        //    挂上去之后页面自己的字典里就有画笔，与树无关。
        UiTheme.AttachPalette(view);

        PageHost.Content = view;
        // 切进来立刻刷一帧，不等下一次 tick（否则有一秒空白/旧值）
        RefreshCurrent();
        // 翻页**没有转场**（2026-10-28 用户要求删掉全部装饰动效）：点了就到位。
        // 这里以前有一条只动顶栏的淡入+位移，取舍反复过（整页淡入太贵 → 只动顶栏 →
        // 嫌不明显 → 全删），结论是不要。别再加回来。
        // 重复点当前这一项 = 让这一页重新加载一次（用户拿它当 F5：插了 U 盘、
        // 装完驱动、在系统里改过电源计划之后，不用切到别的页再切回来）。
        // 快照上面已经刷过一帧，这里补的是各页自己的"一次性数据"。
        if (!changed)
        {
            RefreshPage(key);
            // 刷新得让人**看得见**：这一页上的数字本来就一秒一变，光重拉数据等于什么都没发生。
            // 没有动画可播了，所以提示条是唯一反馈 —— 别删。
            Toast(meta.Title + " · 已刷新", ok: true, ms: 1500);
        }
    }

    /// <summary>
    /// 重新加载某一页自己的"一次性数据"（机型档案 / 原厂进程 / 电源计划列表 / 曲线）。
    /// 快照（每秒那一份 `/api/status`）**不在这里刷** —— 由 <see cref="RefreshCurrent"/> 负责。
    ///
    /// 只有「重复点击侧栏当前项」这一条路径会走到这里。切页进入时的加载时机不同
    /// （各页在 Loaded 里做，about / hw 由 <see cref="ShowPage"/> 的 changed 分支触发），
    /// 别把两处合并 —— 那些页的首次加载本来就早于这里。
    /// 总览页不在列：它没有一次性数据，快照与历史图每秒都在重铺。
    /// </summary>
    private void RefreshPage(string key)
    {
        switch (key)
        {
            case "perf": _perf?.Refresh(); break;
            case "cool": _cool?.Refresh(); break;
            case "light": _light?.Refresh(); break;
            case "sys": _sys?.Refresh(); break;
            case "hw": _hw?.Refresh(); break;
            case "about": _about?.Refresh(); break;
            case "set": _set?.Refresh(); break;
            default: break;      // dash：RefreshCurrent 已经是它的刷新
        }
    }

    private FrameworkElement CreatePage(string key)
    {
        if (key == "dash")
        {
            _dash = new DashView(_api);
            return _dash;
        }
        if (key == "perf")
        {
            _perf = new PerfView(_api);
            return _perf;
        }
        if (key == "cool")
        {
            _cool = new CoolView(_api);
            return _cool;
        }
        if (key == "light")
        {
            _light = new LightView(_api);
            return _light;
        }
        if (key == "sys")
        {
            _sys = new SysView(_api);
            return _sys;
        }
        if (key == "hw")
        {
            _hw = new HardwareView(_api);
            return _hw;
        }
        if (key == "about")
        {
            _about = new AboutView(_api);
            return _about;
        }
        if (key == "set")
        {
            _set = new SettingsView(_api);
            return _set;
        }
        // Meta 里有的键上面全覆盖了；走到这里只可能是调用方传了野键
        throw new ArgumentOutOfRangeException(nameof(key), key, "MainWindow.Meta 里没有这一页");
    }

    // ---------------------------------------------------------------- 每秒刷新

    private void OnTick(object? sender, EventArgs e)
    {
        // 窗口开着还在看 = 那几项 HWiNFO 独占的读数还要用（按需模式的心跳）
        _api.KeepHwinfoAlive();

        var status = _api.HasState ? _api.Status() : null;
        UpdateChips(status);
        Push(status);

        // 拿到第一帧真数据 ⇒ 回到 1 秒一拍（之前是 150ms 的"等首帧"节奏，见 WarmUp）
        // ⚠️ 没有数据源（截图 / 自检 / 初始化失败）时也要收住节奏，否则它一直 150ms 空转。
        if (status is not null) MarkFirstData();
        else if (!_api.HasState) MarkFirstData(hasData: false);

        // auto 模式下才真的读注册表；其它模式这行什么都不做
        UiTheme.PollSystem();
        SyncGuideWindow();
    }

    /// <summary>
    /// 引导弹窗的开关：**轮到用户操作时**才浮出来（观察真开始），观察一结束收掉。
    /// 采集前那 30 多秒（读机型 / 采样 / 读显卡）不弹 —— 那时用户没东西可点，
    /// 一个置顶空窗只会压在原厂窗口上面；那段进度由关于页的五段进度条交代。
    /// 不设 Owner（最小化主窗口时小窗要留在原厂窗口上面），所以关窗时显式收一次。
    /// </summary>
    private void SyncGuideWindow()
    {
        var g = _api.GuideView;
        var active = g?["active"] is JsonValue av && av.TryGetValue<bool>(out var a) && a;
        var observing = g?["phase"] is JsonValue pv && pv.TryGetValue<string>(out var phase)
                        && phase == "observe";
        if (!active || !observing)
        {
            CloseGuideWindow();
            return;
        }
        if (_guideWindow is not null) return;

        try
        {
            _guideWindow = new GuideWindow(_api);
            _guideWindow.Show();     // ShowActivated=False：不抢原厂窗口的焦点
        }
        catch (Exception ex)
        {
            _guideWindow = null;
            Console.Error.WriteLine($"[WARN] 引导弹窗没打开：{ex}");
        }
    }

    private void CloseGuideWindow()
    {
        var w = _guideWindow;
        _guideWindow = null;
        if (w is null) return;
        try { w.Close(); } catch { /* 会话结束时可能已经跟着 Dispatcher 收掉了 */ }
    }

    /// <summary>刚切进来的页立刻刷一帧（各页自己决定要不要这份快照）。</summary>
    private void RefreshCurrent() => Push(_api.HasState ? _api.Status() : null);

    /// <summary>
    /// 把这一秒的快照交给**当前页**，其它页一律不碰。
    ///
    /// 为什么这条很重要：早先这里是"所有已构造的页挨个 Update"，而 Update 不是纯赋值 ——
    /// 性能页会顺手起后台任务查 NVML（每 5 拍一次）、设置页会枚举进程查原厂软件、
    /// 散热页会重算曲线画布。于是**翻过的页越多，当前页越卡**：点满 6 页之后，每秒
    /// tick 里要做 6 份完整的渲染工作，而屏幕上只显示其中一份。
    /// 界面只显示一页，那就只刷一页；切回来时 <see cref="RefreshCurrent"/> 会给它
    /// 补一帧新的，显示效果完全一样。
    ///
    /// ⚠️ 「系统」「硬件」「关于」三页不在 switch 里：它们不吃每秒快照
    ///    （硬件/关于是切进来时自己拉一次的，见 <see cref="SysView"/> 那两页的 Refresh）。
    /// </summary>
    private void Push(StatusDto? status)
    {
        switch (_current)
        {
            case "dash": _dash?.Update(status, _api.HasState ? _api.History() : null); break;
            case "perf": _perf?.Update(status); break;
            case "cool": _cool?.Update(status); break;
            case "light": _light?.Update(status); break;
            case "sys": _sys?.Update(status); break;
            case "set": _set?.Update(status); break;
            default: break;      // hw / about / 还没翻页
        }
    }

    /// <summary>顶栏胶囊与警告。</summary>
    private void UpdateChips(StatusDto? status)
    {
        if (_chipCpu is null || _chipGpu is null || _chipFan is null || _chipEc is null) return;

        if (status is null)
        {
            // 还没出第一帧/快照读不到：整排胶囊先摆出来但写「--」（这是"读取中"，
            // 不是"这台机器没有"—— 有数据和没数据必须能分清，见下面的收起逻辑）
            _chipCpu.Text = "--";
            _chipGpu.Text = "--";
            _chipFan.Text = "--";
            _chipEc.Text = "不可用";
            ShowChip(_chipGpuHost, true);
            ShowChip(_chipFanHost, true);
            if (_warnAdmin is not null) _warnAdmin.Visibility = Visibility.Collapsed;
            if (_warnDevice is not null) _warnDevice.Visibility = Visibility.Collapsed;
            return;
        }

        var ec = status.Ec;
        var cpu = status.Cpu;
        var gpu = status.Gpu;

        _chipCpu.Text = ec?.CpuTemp is long temp
            ? temp.ToString(CultureInfo.InvariantCulture) + "°C · " + Fmt(cpu?.Usage) + "%"
            : Fmt(cpu?.Usage) + "%";

        // ⚠️ 没数就**整颗收起来**，不留「GPU --」「风扇 --」（用户定的规矩：
        //    数据不可用整行隐藏）。这两颗最容易被未适配机型 / 集显直连触发；
        //    2026-11-02 用 `--device quanta-gmwmi-generic` 预览时第一次看见空壳。
        // ⚠️ 2026-11-07：核显回退只给占用率、没有温度 ⇒ 不能再用「--°C · 13%」，
        //    有哪项写哪项，两项都没有才收起（见 GpuChipText）。
        var gpuChip = gpu?.Available == true ? GpuChipText(gpu) : "";
        _chipGpu.Text = gpuChip;
        ShowChip(_chipGpuHost, gpuChip.Length > 0);

        var fanOk = ec?.Fan1Rpm is not null || ec?.Fan2Rpm is not null;
        _chipFan.Text = fanOk ? FanText(ec) : "";
        ShowChip(_chipFanHost, fanOk);

        _chipEc.Text = status.EcAvailable == true ? "已连接" : "不可用";

        if (_warnAdmin is not null)
            _warnAdmin.Visibility = status.Env?.Admin == false ? Visibility.Visible : Visibility.Collapsed;
        if (_warnDevice is not null)
        {
            var dev = status.Device;
            var show = dev?.WritesAllowed == false;
            if (show)
            {
                // ⚠️ 三种"写不了"必须分开说（2026-11-12 修）：以前这颗胶囊的文案是写死的
                //    「机型未适配 · 只读」，而它的显示条件只是 `writes_allowed == false` ——
                //    用户在设置页把「EC 控制」关掉一次，一台**已适配**的机器就会被挂上
                //    "机型未适配"，看着像识别失败了。
                var (text, tip) = dev!.EcCapable != true
                    ? ("⚠ 无 EC 命令表 · 只读",
                       "本机识别到的机型没有 EC 命令表，控制中心不会向 EC 写入任何东西。")
                    : dev.Verified == true
                        ? ("⚠ EC 控制已关闭 · 只读",
                           "设置页「EC 控制」这颗开关被关掉了：风扇 / 背光 / 性能档都不会下发。\n"
                           + "重新打开后会立刻恢复接管，不必重启程序。")
                        // ⚠️ 不能写「机型未适配」（2026-11-13 改）：做完「关于 › 机型适配」的
                        //    引导适配之后，本机档案是真的适配过了，只是没有作者的 verified 标定。
                        //    写「未适配」会让用户以为采集白做了 —— 他要做的只是去那一页点「启用本机」。
                        : ("⚠ 写入未验证 · 只读",
                           "这台机器的写入还没有经过作者复核（verified=false）：写操作默认关闭。\n"
                           + "刚做完「关于 › 机型适配」的引导适配的话，去那一页点「启用本机」即可全功能；\n"
                           + "也可以随时在设置页「EC 控制」里手动打开（有风险确认）。");

                if (_warnDevice.Child is TextBlock tb) tb.Text = text;
                _warnDevice.ToolTip = tip;
            }
            _warnDevice.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static void ShowChip(FrameworkElement? chip, bool show)
    {
        if (chip is not null) chip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 顶栏 GPU 胶囊的文本：`温度 · 占用`，**缺哪项就少哪项**（两项都没有 = 空串，调用方收起）。
    /// 核显回退只有占用率、没有温度，写成「--°C · 13%」就是假占位。
    /// </summary>
    private static string GpuChipText(StatusGpuDto gpu)
    {
        var temp = gpu.Temp is double t ? Fmt(t) + "°C" : "";
        var usage = gpu.Usage is double u ? Fmt(u) + "%" : "";
        if (temp.Length == 0) return usage;
        return usage.Length == 0 ? temp : temp + " · " + usage;
    }

    private static string FanText(StatusEcDto? ec)
    {
        if (ec is null) return "--";
        var first = ec.Fan1Rpm;
        var second = ec.Fan2Rpm;
        if (first is null && second is null) return "--";

        // 三风扇机型（layout 认到 fan3_rpm）才有第三个数；两风扇机型保持原样，
        // 不要写成「3054 / 2103 / --」——那看着像有个风扇读不到。
        // 契约里 fan3_rpm 采样时是 null ⇒ 生成器给的是 JsonElement?，这里按数字解
        var third = ec.Fan3Rpm is { } je
                    && je.ValueKind == System.Text.Json.JsonValueKind.Number
                    && je.TryGetInt64(out var n) ? n : (long?)null;
        return Dump(first) + " / " + Dump(second)
               + (third is null ? "" : " / " + Dump(third)) + " RPM";

        static string Dump(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "--";
    }

    private static string Fmt(double? value)
        => value is double v ? v.ToString("F0", CultureInfo.InvariantCulture) : "--";

    // ---------------------------------------------------------------- 主题

    private void ToggleTheme()
    {
        var next = UiTheme.ManualOpposite();
        if (!UiTheme.SetMode(next)) return;
        _api.SaveTheme(UiTheme.Token(next));      // 配置键：presets.ui.theme
    }

    /// <summary>
    /// 退订主题事件。<see cref="UiTheme.Changed"/> 是**进程级静态事件**，而窗口是
    /// "开一个、关一个、再开一个"的：漏退一次订阅，下一个窗口切主题（或 auto
    /// 跟系统）时就会调到这个已死窗口的控件上 —— 跨线程 InvalidOperationException
    /// 把整条 UI 线程连坐，窗口凭空消失（用户眼里的"闪退"）。
    /// 所以除了 Closed，UiHost 起窗口失败/线程提前结束的路径也必须调它（幂等）。
    /// </summary>
    internal void DetachTheme() => UiTheme.Changed -= OnThemeChanged;

    private void OnThemeChanged()
    {
        // 共享调色板已在 UiTheme.Resolve 里重灌（比 Changed 早一步），这里只是
        // 兜底确认窗口合并了它 —— 幂等，重复调用不会叠字典。
        UiTheme.AttachPalette(this);
        RebuildNavGlow();
        foreach (var (name, icon) in _navIcons)
            icon.Effect = _nav[name].IsChecked == true ? _navGlow : null;
        UpdateThemeIcon();
        // 各页自己那部分要跟着主题重画的，由窗口统一转发。
        // ⚠️ 页面**不许**自己订 UiTheme.Changed：WPF 关窗时 content 的 Unloaded
        //    不触发，页面在构造里订阅、只在 Unloaded 里退订 = 必漏
        //    （第二个窗口一切主题就调到第一个窗口死线程上的 CurveEditor.InvalidateVisual，
        //    当场闪退）。
        //    窗口这条订阅跟着 Closed / DetachTheme 走，生命周期与窗口一致。
        _dash?.ApplyTheme();
        _cool?.ApplyTheme();
        _light?.ApplyTheme();
        // ⚠️ 硬件页的分组徽标是**构造时按当时的调色板算死的**（浅色要淡底深图标、
        //    暗色才用实心色块压白图标，见 HardwareView.MakeBadge）—— DynamicResource
        //    救不了它，漏了这一行就是"切到白天模式，徽标还是夜里那套饱和色"。
        _hw?.ApplyTheme();
        // 性能页的电源计划行（「当前」标签 / 亮度 / CPU 区间那几串小字）同理：
        // 它们抓的是 PaletteBrush 的**实例**，换主题不重建就一直是暗色那支中灰。
        _perf?.ApplyTheme();
        // 设置页那颗「浅色 / 深色」标签和「跟随系统外观」开关的当前态。
        _set?.ApplyTheme();
    }

    private void UpdateThemeIcon()
    {
        if (_themeButton is null) return;
        _themeButton.Content = UiKit.Icon(
            UiTheme.Palette == UiPalette.Light ? Icons.Sun : Icons.Moon, 20);
    }

    // ---------------------------------------------------------------- 提示条 / 叠层

    /// <summary>叠层容器（提示条、模态框）。UiDialog 从这里挂界面。</summary>
    internal Grid Overlay => OverlayHost;

    /// <summary>
    /// 弹一条提示：默认 2.6 秒后自己淡出，不打断操作。
    /// ⚠️ 用**淡出**而不是直接消失：写操作的结果常常出现在用户视线之外，
    ///    突然消失会让人怀疑"刚才那句是什么"。
    /// </summary>
    /// <param name="ms">停留时长。反馈类短提示（如"已刷新"）传更小的值，别让一条语气词占着屏幕。</param>
    internal void Toast(string text, bool ok = true, int ms = 2600)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_toast is null)
        {
            _toastText = new TextBlock
            {
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _toast = new Border
            {
                Style = (Style)FindResource("ToastBox"),
                Child = _toastText,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 26),
                IsHitTestVisible = false,
                Opacity = 0,
            };
            OverlayHost.Children.Add(_toast);

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                _toast!.Opacity = 0;      // 瞬时消失（提示条不再淡出，见 Toast 注释）
            };
        }

        _toastText!.Text = text;
        _toastText.Foreground = UiTheme.PaletteBrush(ok ? "TxtBrush" : "HotBrush");
        _toast.BorderBrush = UiTheme.PaletteBrush(ok ? "OkBrush" : "HotBrush");
        // 瞬时出现：2026-10-28 删掉全部装饰动效，提示条不再淡入淡出。
        // ⚠️ 别改成先设 0 再等一拍 —— 那是动画的老写法，没有动画就只剩"闪一下空白"。
        _toast.Opacity = 1;
        _toastTimer!.Stop();
        // 每次按调用方的时长重定：默认 2.6 s，反馈类短提示 1.5 s 左右
        _toastTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- 窗口按钮

    private void Topbar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // 最大化态拖动顶栏 = 系统标题栏的手势：先还原到光标下，再跟手走。
        // 不接这个手势的话，最大化窗口除了双击/按钮就没有别的退出方式，
        // 而"拖一下标题栏把它拉下来"是用户最熟的那个（详见 BeginRestoreDrag）。
        if (WindowState == WindowState.Maximized)
        {
            BeginRestoreDrag(e);
            return;
        }
        DragMove();
    }

    /// <summary>
    /// 最大化态下顶栏被按住：**先不还原**。系统标题栏的规矩是"按住不动 = 没事发生，
    /// 拖过系统拖动阈值才还原并跟手" —— 照抄它。否则用户最大化后随手点一下顶栏
    /// （最常见的是点标题栏让窗口获得焦点）窗口就恢复了，纯惊吓。
    /// 阈值内移动：什么都不做；越过阈值：还原到光标下并接上 DragMove。
    /// </summary>
    private void BeginRestoreDrag(MouseButtonEventArgs e)
    {
        var origin = e.GetPosition(this);
        var threshold = SystemParameters.MinimumHorizontalDragDistance;

        void OnMove(object s, MouseEventArgs ev)
        {
            var now = ev.GetPosition(this);
            if (Math.Abs(now.X - origin.X) < threshold && Math.Abs(now.Y - origin.Y) < threshold)
                return;
            Detach();
            RestoreUnderCursorAndDrag(ev);
        }

        void OnUp(object s, MouseButtonEventArgs ev)
        {
            if (ev.LeftButton == MouseButtonState.Released) Detach();
        }

        // 捕获被外部抢走（Alt+Tab 等）也要收场，否则两个处理器一直挂在窗口上
        void OnLost(object s, MouseEventArgs ev) => Detach();

        void Detach()
        {
            MouseMove -= OnMove;
            MouseUp -= OnUp;
            LostMouseCapture -= OnLost;
            ReleaseMouseCapture();
        }

        MouseMove += OnMove;
        MouseUp += OnUp;
        LostMouseCapture += OnLost;
        CaptureMouse();
    }

    /// <summary>
    /// 把最大化的窗口还原到光标下，并让当次拖动继续生效（不续 DragMove 的话
    /// 手势会"断"——窗口还原了，但松开前它不跟手，用户以为拖拽失败）。
    ///
    /// 三个必须这样写的点：
    ///  ① <see cref="Window.ActualWidth"/> 与 <see cref="Window.RestoreBounds"/>
    ///     都要在 WindowState 改回 Normal **之前**读：前者还是最大化宽（比例的分母），
    ///     后者按文档只在最大化/最小化态有意义。
    ///  ② 光标坐标走 PointToScreen 拿到的是**设备像素**，喂给 Win32（MonitorFromPoint）
    ///     正好，但 Left/Top 与 RestoreDrag 要的是 DIP —— 用 CompositionTarget 换算。
    ///     这台机器 150% 缩放，不换算窗口会偏 1.5 倍。
    ///  ③ 还原的 Win32 重排是异步的：同步设一次 Left/Top，再在 Loaded 优先级补一次，
    ///     防止首帧被系统还原矩形盖掉（两设同值，幂等）。
    /// </summary>
    private void RestoreUnderCursorAndDrag(MouseEventArgs e)
    {
        var grabDip = e.GetPosition(this);                 // 抓取点相对窗口（DIP）
        var cursorDev = PointToScreen(grabDip);            // 设备像素
        var maxWidth = ActualWidth;                        // ① 还原前的最大化宽
        var restore = RestoreBounds;                       // ① 还原后的矩形

        WindowState = WindowState.Normal;

        var source = PresentationSource.FromVisual(this);
        // TransformFromDevice 是 Matrix?（可视源没就绪时是 null）：退回单位矩阵 =
        // 假设 96DPI、换算恒等。比"不换算"安全 —— 真出那种场景也只是位置不缩，不会偏 1.5 倍。
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursorDip = toDip.Transform(cursorDev);

        // 工作区取光标所在那一块显示器（设备像素问 Win32，再换算成 DIP 供夹取）
        var work = SystemParameters.WorkArea;             // 兜底：主显示器工作区（DIP）
        if (WorkAreaUnder(cursorDev, out var workDev))
        {
            // Matrix 没有 Transform(Rect)：两个角点各换一次，再由两点框出矩形
            var tl = toDip.Transform(workDev.TopLeft);
            var br = toDip.Transform(workDev.BottomRight);
            work = new Rect(tl, br);
        }

        var (left, top) = RestoreDrag.RestoreTopLeft(
            grabDip.X, maxWidth, cursorDip.X, cursorDip.Y,
            restore.Width, restore.Height,
            work.Left, work.Top, work.Right, work.Bottom);

        Left = left;
        Top = top;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => { Left = left; Top = top; }));

        DragMove();
    }

    /// <summary>
    /// 最小化交给系统。
    ///
    /// 2026-11-06 定论：自绘那一套（逐帧 SetWindowPos + 截图缩放 + 分层窗口渐变）在这台
    /// **软件渲染**的 WPF 上做不到流畅 —— 截图一次性就要 26ms、每帧还要重排重画，
    /// 观感是"点了顿一下、动起来一顿一顿、最后突然变透明"。试了七轮都只能到这。
    /// 系统自带的收栏动画是 DWM 用 GPU 合成的，不占 CPU、不会割裂 ——
    /// Steam / Edge 看到的也是它。别再自绘了。
    /// </summary>
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>最大化后同一颗按钮变成"还原"（E923），与系统标题栏行为一致。</summary>
    private void SyncMaximizeGlyph()
    {
        if (MaxButton is null) return;
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }
}
