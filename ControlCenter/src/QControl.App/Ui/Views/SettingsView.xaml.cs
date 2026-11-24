using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using QControl.App.Http.Contract;
using QControl.App.Ui.Controls;

namespace QControl.App.Ui.Views;

/// <summary>
/// 设置页：EC 与数据源 / 启动与窗口 / 界面与按键 / 原厂软件。
///
/// **四张卡**：EC 与数据源（EC 写入 + 传感器数据源）/ 启动与窗口 / 界面与按键 / 原厂软件。
/// 类似的合成一张，别拆成五张单行卡；**中间两列必须行数相同**（各三行），
/// 否则底边差一整行 —— 见 XAML 顶部 2026-11-21 那段。
///
/// **这一页一次 EC 都不写** —— 全是配置项（写 `presets`）与界面偏好；
/// 唯一会碰硬件的是「EC 控制」那颗总闸，而它也只是改 `presets.ec.enabled`，
/// 真正的写入许可由 `Device.writes_allowed` 统一判定（见 DETAILS.md 的「EC 控制总闸」）。
/// 唯一碰系统输入的是「锁定 Win 键」（进程内的键盘钩子，不碰 EC、不要管理员）。
///
/// 别"简化"的几条：
///   · **EC 总闸的默认态不在这里决定**：后端按机型档案下发（已验证=开 / 未适配=关），
///     界面只负责画。两者必须一致，否则会出现"界面开着、后端还在拦"；
///   · 未适配机型要**打开**时先过一道风险确认（关掉不问 —— 关掉永远是更安全的方向）；
///   · 开关写失败必须**回到真值**：每次写完都重新读一遍 `/api/app/settings`，
///     绝不停在用户点出来的样子（所有设置都走这个套路）；
///   · 原厂软件的两颗各管一件事：「启动时自动退出」只写配置、下次启动生效；
///     「立即退出」只管此刻。别合并；
///   · 「锁定 Win 键」的真值**不在配置里**，在钩子身上（`AppState.WinKeyLocked`）：
///     钩子装失败时配置可能写着 true —— 开关只认实际拦截状态，见 <see cref="SyncWinKey"/>。
///
/// ⚠️ 两个慢读（`/api/app/settings` 要查计划任务，`/api/vendor/state` 要枚举进程）都走后台，
///    回 UI 只经 Dispatcher；关窗后的回包直接丢弃。
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>启动默认页的可选项 —— 六项（不带硬件/关于）。</summary>
    private static readonly (string Key, string Label)[] StartPages =
    [
        ("dash", "总览"),
        ("perf", "性能"),
        ("cool", "散热"),
        ("light", "背光"),
        ("sys", "系统"),
        ("set", "设置"),
    ];

    // ⚠️ 「背光自动熄灭」那一行 **2026-11-01 搬到背光页**（LightView）：
    //    它改的是键盘背光本身，跟"这一页要开什么界面、弹不弹提示"不是一类东西；
    //    放在背光页「亮度」正下方，用户找熄灯逻辑时不用先在设置里翻。
    //    配置键没动（仍是 `presets.light.auto_off_min` / `auto_off_on_lock`），
    //    老 model.json 不用迁移；搬走顺带把这一行的高度还给了「界面与提示」那张卡。

    private readonly LocalApi _api;

    private readonly SwitchButton _ecSwitch;
    private readonly TextBlock _ecDesc;
    private readonly SwitchButton _autoStartSwitch;

    private readonly Dictionary<string, CardButton> _startModeButtons = new(StringComparer.Ordinal);
    private readonly ComboBox _startPageCombo;
    private readonly Dictionary<string, CardButton> _hwModeButtons = new(StringComparer.Ordinal);
    private readonly TextBlock _hwDesc;
    private readonly Border _hwFindRow;
    private readonly TextBlock _hwFindTitle;
    private readonly TextBlock _hwFindDesc;
    private readonly Button _hwDownload;
    private readonly Button _hwRecheck;
    private readonly Button _hwTakeover;
    private HwRowState _hwRow = HwRowState.Unknown;
    private bool _hwBlocked;
    private int _hwBlockTicks;
    /// <summary>快照说的病是哪种：`nodata`（在跑但没数）/ `notrunning`（没起来）；null = 正常。</summary>
    private string? _hwRowKind;
    /// <summary>拉起被退避挡住的原因与剩余秒数（只有 `notrunning` 那态有）。</summary>
    private string? _hwBlockWhy;
    private double _hwBlockLeft;
    /// <summary>后端说 HWiNFO 进程此刻在不在跑（`notfound` 那态要补一句"它其实在跑"）。</summary>
    private bool _hwRowRunning;

    /// <summary>
    /// 这一拍快照里**真有 HWiNFO 的读数**（PCH / 显存结温 / 电池功率 / 虚拟内存，都只有它给）。
    ///
    /// 为什么要它（2026-11-22 用户：「总是有 HWiNFO 没读数的那一行」）：`_api.HwinfoFound`
    /// 是"扫盘找到 exe 没有"，扫不到（目录被移走、探测抛异常、以别的身份运行）时那种
    /// 判断会一直说「未找到」—— 可数据明明在流。**读数本身就是它存在的证据**，
    /// 有读数就不许再贴"未找到 / 没有数据"那两行。
    /// </summary>
    private bool _hwDataFlowing;
    private readonly SwitchButton _themeAutoSwitch;
    private readonly ComboBox _osdModeCombo;

    /// <summary>
    /// 提示条归属四选一（2026-11-25 用户定）。原厂 OSD 是单体进程，**按不了单个键**，
    /// 所以只能在这四个粒度上选；文案要让人一眼看出"另一边的代价"。
    /// </summary>
    private static readonly (string Key, string Label)[] OsdModes =
    [
        ("vendor_backlight", "原厂 + 补背光"),
        ("vendor", "只用原厂"),
        ("ours", "只用我们"),
        ("both", "两边都弹"),
    ];

    private readonly SwitchButton _winLockSwitch;
    private readonly SwitchButton _vendorKillSwitch;
    private readonly SwitchButton _vendorBlockSwitch;
    private string _vendorSavedStart = "auto";
    private readonly TextBlock _vendorNow;
    private readonly Button _vendorStop;

    private AppSettingsDto? _settings;
    private bool _busy;
    /// <summary>Vendor 回读的请求序号：只认最后一次，防止旧帧晚到把开关拨回旧值。</summary>
    private int _vendorSeq;
    /// <summary>最近一次 RenderVendor 看到的原厂进程数（按钮可用性重算用，不必再查 COM）。</summary>
    private int _vendorRunCount;
    private bool _syncing;
    private int _tick;

    internal SettingsView(LocalApi api)
    {
        _api = api;
        InitializeComponent();
        UiKit.ApplyCardGap(CardGrid);

        // ---------------- EC 与数据源（整行，三行） ----------------
        // 两行都往 EcRows 里加（EC 写入 / 传感器数据源），说明见上面那段注释。
        _ecSwitch = new SwitchButton { Style = (Style)FindResource("SwitchButton") };
        _ecSwitch.Click += Ec_Click;
        var ecRow = UiKit.MakeSetRow(this, Icons.Ec, "允许向 EC 写入",
            "风扇、性能模式、键盘背光、显卡模式都靠 EC 命令下发；关掉后只读不写", _ecSwitch);
        _ecDesc = ecRow.Desc;
        EcRows.Children.Add(ecRow.Root);

        // 传感器数据源 + HWiNFO 提示两行**也归这张卡**（2026-11-21）：
        // 它们在下面「启动与窗口」段里创建，但那只是赋值顺序 —— StackPanel 的显示顺序
        // 只由 `EcRows.Children.Add` 的先后决定，两行都往 EcRows 里加。

        // ---------------- 独立灯珠（Logo / 尾灯）**没有设置项** ----------------
        // 2026-11-18 用户要求撤掉，"自动 / 强制开 / 关"三态已删除：
        // 有灯且识别到了就该生效，识别不到就不该出现 —— 不该让用户来纠正软件的判断。
        // 判定只看 Device.ExtraLightsByProfile（名字命中档案才给），背光页自己摆控件。

        // ---------------- 启动与窗口（三行） ----------------
        var segWindow = UiKit.SegButton(this, "显示窗口");
        var segTray = UiKit.SegButton(this, "静默进托盘");
        _startModeButtons["window"] = segWindow;
        _startModeButtons["tray"] = segTray;
        segWindow.Click += (_, _) => SaveAsync(new JsonObject { ["start_mode"] = "window" },
            "已设置：启动时直接开窗口（下次生效）");
        segTray.Click += (_, _) => SaveAsync(new JsonObject { ["start_mode"] = "tray" },
            "已设置：静默进托盘（下次生效）");
        StartRows.Children.Add(UiKit.MakeSetRow(this, Icons.Tray, "启动方式",
            "双击程序启动时：直接开窗口或静默进托盘",
            UiKit.SegBox(this, segWindow, segTray)).Root);

        _autoStartSwitch = new SwitchButton { Style = (Style)FindResource("SwitchButton") };
        _autoStartSwitch.Click += AutoStart_Click;
        StartRows.Children.Add(UiKit.MakeSetRow(this, Icons.Power, "随 Windows 自动启动",
            "登录后静默进托盘（不弹窗），托盘左键打开控制中心", _autoStartSwitch).Root);

        // 启动默认页**回到这张卡**（2026-11-21）：它管的是"启动后落在哪一页"，与上面两行同一类。
        // （2026-10-28 曾搬到「界面与按键」补行；现在那张卡有「锁定 Win 键」撑起来了，
        //   留着它那卡就四行、这卡三行 —— 两列底边差一整行，正是用户说的"不整齐"。）
        _startPageCombo = new ComboBox { Style = (Style)FindResource("Select") };
        foreach (var (key, label) in StartPages)
            _startPageCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        _startPageCombo.SelectionChanged += StartPage_Changed;
        StartRows.Children.Add(UiKit.MakeSetRow(this, Icons.Home, "启动默认页",
            "打开界面时先落在哪一页（下次生效）", _startPageCombo).Root);

        // ---------------- 传感器数据源（HWiNFO）→ 顶上那张「EC 与数据源」卡 ----------------
        // 这六项只有 HWiNFO 能给：PCH 温度 / CPU 电压 / PL1·PL2 / 显存结温 / 显卡电压 / 硬盘 SMART。
        // 三档各管一件事：常驻（一直在跑）、按需（有人看才起）、关闭（永不拉起，六项整行隐藏）。
        //
        // ⚠️ 2026-11-21 从「启动与窗口」挪到 EcRows：用户要"卡片整齐"，而那卡本来四行、
        //    右边三行 —— 两列底边差一整行。它与 EC 写入是同一件事的两头：
        //    一个决定"能不能下发"，一个决定"读得到什么"。
        var segAlways = UiKit.SegButton(this, "常驻");
        var segDemand = UiKit.SegButton(this, "按需");
        var segOff = UiKit.SegButton(this, "关闭");
        _hwModeButtons["always"] = segAlways;
        _hwModeButtons["ondemand"] = segDemand;
        _hwModeButtons["off"] = segOff;
        segAlways.Click += (_, _) => SaveHwMode("always");
        segDemand.Click += (_, _) => SaveHwMode("ondemand");
        segOff.Click += (_, _) => SaveHwMode("off");
        var hwRow = UiKit.MakeSetRow(this, Icons.Board, "传感器数据源", "读取中…",
            UiKit.SegBox(this, segAlways, segDemand, segOff));
        _hwDesc = hwRow.Desc;
        EcRows.Children.Add(hwRow.Root);

        // ---------------- HWiNFO 的病（引导） ----------------
        // 这一行同时管两种病，因为它俩的后果是同一个「传感器全是空的」：
        //   ① **没找到 HWiNFO64.exe**（分发包不带它 —— 第三方软件，官方 EULA 写明
        //      "Embedding or bundling HWiNFO in 3rd party software is allowed only with
        //      explicit approval of the Licensor."），所以安装版用户大概率压根没有；
        //   ② **在跑，但没把读数导出到共享内存**（2026-11-08 台式机报障查出来的）：
        //      HWiNFO 7 起「Shared Memory Support」**默认是关的**，免费版连续跑满 12 小时
        //      还会自动关掉。此时段名打不开，程序一个读数都拿不到 —— 而且它不报错。
        //      更要命的是会卡在"它自己在跑"这一步：程序永远不去改**用户自己那份**的 INI
        //      （归属铁律：不杀、不藏、不改别人的实例），于是永远等不到数据。
        //
        // ⚠️ 为什么合成一行而不是加两行：这两种病会同时出现（用户把 HWiNFO 装在
        //    Downloads，程序找不到 exe，而他手里那份又没开共享内存）。摆两行的话
        //    设置页要多 60px，一屏就顶破了。标题/说明/按钮都跟着状态换。
        // ⚠️ **不镜像、不转发安装包**，只开浏览器 —— 这是唯一合规的做法。
        // ⚠️ 一切正常就整行收起：用户定的规矩是"没问题的东西不占版面"。
        _hwDownload = new Button
        {
            Content = "下载 HWiNFO",
            Style = (Style)FindResource("MiniButton"),
            ToolTip = "用浏览器打开官网下载页（我们不分发它的安装包）",
        };
        _hwDownload.Click += HwinfoDownload_Click;

        _hwRecheck = new Button
        {
            Content = "重新检测",
            Style = (Style)FindResource("MiniButton"),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "重新扫一遍 HWiNFO64.exe 所在目录（放好了不用重启控制中心）",
        };
        _hwRecheck.Click += HwinfoRecheck_Click;

        // 「接管并重启」是这种病的唯一根治手段：杀掉所有实例 → 用程序目录那份
        // （INI 已写死 SensorsSM=1）重新拉起。它一定会打断用户手里的 HWiNFO，
        // 所以必须走确认框，且**只在找得到 exe 时才出现**（找不到就杀完再拉不起来，
        // 等于把人家的 HWiNFO 关掉还没换来数据）。
        _hwTakeover = new Button
        {
            Content = "接管并重启",
            Style = (Style)FindResource("MiniButton"),
            ToolTip = "先关掉现在所有 HWiNFO 实例，再用本程序目录里那份（已配好共享内存导出）"
                    + "重新拉起 —— 它的窗口和托盘图标都会被收掉，只在后台出数",
        };
        _hwTakeover.Click += HwinfoTakeover_Click;

        var hwButtons = new StackPanel { Orientation = Orientation.Horizontal };
        hwButtons.Children.Add(_hwDownload);
        hwButtons.Children.Add(_hwRecheck);
        hwButtons.Children.Add(_hwTakeover);

        var hwFind = UiKit.MakeSetRowFull(this, Icons.Refresh, "HWiNFO 未找到", "读取中…", hwButtons);
        _hwFindTitle = hwFind.Title;
        _hwFindDesc = hwFind.Desc;
        _hwFindRow = hwFind.Root;
        // 先收起：真实状态由 Loaded → SyncHwMode → SyncHwinfoRow 画。
        // 不先收起的话，构造到 Loaded 之间会闪一行"读取中…"。
        _hwFindRow.Visibility = Visibility.Collapsed;
        EcRows.Children.Add(hwFind.Root);

        // ---------------- 界面与提示（三行；密度那一行见 XAML 顶部说明，不做） ----------------
        _themeAutoSwitch = new SwitchButton { Style = (Style)FindResource("SwitchButton") };
        _themeAutoSwitch.Click += ThemeAuto_Click;
        UiRows.Children.Add(UiKit.MakeSetRow(this, Icons.Auto, "跟随系统外观",
            "开着时跟随 Windows 的浅色/深色设置（马上生效）",
            _themeAutoSwitch).Root);

        // FN 提示条改成四选一（2026-11-25）：原厂控制中心也会弹它自己那套，
        // 用户要求能选"谁显示"。默认「原厂为主，只补背光」—— 原厂没有背光那张图。
        _osdModeCombo = new ComboBox { Style = (Style)FindResource("Select") };
        foreach (var (key, label) in OsdModes)
            _osdModeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        _osdModeCombo.SelectionChanged += OsdMode_Changed;
        UiRows.Children.Add(UiKit.MakeSetRow(this, Icons.Bell, "FN 提示条",
            "FN 键的提示归谁画", _osdModeCombo).Root);

        // 锁定 Win 键（2026-11-21）：打游戏防误触。钩子在**本进程内**，控制中心一退出
        // 按键立刻恢复 —— 所以说明里必须写清楚，别让人以为这是改系统设置。
        // ⚠️ 状态**不跨重启**（用户定：开机一律关，每次手动开），所以文案用"只对本次运行有效"。
        // 状态读 `AppState.WinKeyLocked`（钩子实际装着没有），不读配置。
        _winLockSwitch = new SwitchButton { Style = (Style)FindResource("SwitchButton") };
        _winLockSwitch.Click += WinKeyLock_Click;
        UiRows.Children.Add(UiKit.MakeSetRow(this, Icons.Keyboard, "锁定 Win 键",
            "只对本次运行有效：Win 键不再弹开始菜单，退出控制中心即恢复",
            _winLockSwitch).Root);

        // 「启动默认页」已搬回「启动与窗口」卡（2026-11-21）：它管的是"启动后落在哪一页"，
        // 本就是那一类；而且搬走后两列各三行、底边齐。
        // （2026-10-28 曾把它搬到这张卡，是为了给右卡补一行 —— 现在右卡有「锁定 Win 键」了。）

        // 「背光自动熄灭」那一行 2026-11-01 搬到背光页「亮度」正下方（见类头注释）。

        // 「界面动效」整行已删（2026-10-28 用户要求删掉全部装饰动效）：
        // 界面上已经没有过渡可关，留一个怎么拨都一样的开关是噪音。
        // `presets.ui.motion` 这个配置项也一并下架（老 model.json 里的残留字段会被忽略）。

        // ---------------- 原厂软件（三行） ----------------
        //
        // 顺序就是处置力度：先"让它以后别再自己起来"（动系统状态、可逆），
        // 再"每次启动顺手收掉"（动进程，配置级），最后"现在这一下"（一次性）。
        _vendorBlockSwitch = new SwitchButton { Style = (Style)FindResource("SwitchButton") };
        _vendorBlockSwitch.Click += VendorBlock_Click;
        VendorRows.Children.Add(UiKit.MakeSetRow(this, Icons.Auto, "抑制原厂开机自启",
            "禁用它的登录计划任务与 Hotkey OSD 服务（不删除、不卸载，关掉这颗开关就恢复）",
            _vendorBlockSwitch).Root);

        _vendorKillSwitch = new SwitchButton { Style = (Style)FindResource("SwitchButton") };
        _vendorKillSwitch.Click += VendorKill_Click;
        VendorRows.Children.Add(UiKit.MakeSetRow(this, Icons.Power, "启动时自动退出原厂软件",
            "启动后停掉原厂的控制中心与守护进程（提示条靠原厂时，OSD 服务单独保留）",
            _vendorKillSwitch).Root);

        // 「立即退出」只在真有进程时才可用 —— 没进程可退时按下去只会弹"它本来就没在跑"，
        // 那种按钮等于骗人（用户定的规矩：不可用的东西不摆出来）。
        _vendorStop = new Button
        {
            Content = "立即退出",
            Style = (Style)FindResource("MiniButton"),
            ToolTip = "停服务 + 结束进程（只影响现在，下次开机会回来）",
        };
        _vendorStop.Click += VendorStop_Click;
        var vendorRow = UiKit.MakeSetRow(this, null, "当前状态", "读取中…", _vendorStop);
        _vendorNow = vendorRow.Desc;
        VendorRows.Children.Add(vendorRow.Root);

        TrimRowTails();

        Loaded += (_, _) =>
        {
            LoadSettings();
            LoadVendor();
            SyncThemeViews();
            SyncHwMode();
        };
    }

    /// <summary>
    /// 四组行的行距**按可见行**收尾：可见行之间 6px、最后一个可见行 0。
    /// 原因：SetRow 自带的 6px 下边距是行间距，最后一行下面是卡片 padding —— 留着就
    /// 变成"卡底比卡头厚 6px"，还白占 4×6px 高度；设置页本来就是一屏快到顶的页。
    /// ⚠️ 必须在**所有行都加完之后**调（构造末尾）。
    /// ⚠️ 用 NormalizeRowGaps 而不是 TrimLastRowGap：EC 卡最后一行（HWiNFO 提示）平时是收起的，
    ///    按"最后一个子元素"归零会把 6px 留到传感器行身上 —— 卡底比卡头厚 6px。
    ///    显隐一变就重算一次（见 <see cref="SyncHwinfoRow"/>）。
    /// </summary>
    private void TrimRowTails()
    {
        UiKit.NormalizeRowGaps(EcRows);
        UiKit.NormalizeRowGaps(StartRows);
        UiKit.NormalizeRowGaps(UiRows);
        UiKit.NormalizeRowGaps(VendorRows);
    }

    /// <summary>
    /// 主窗口每秒推一份快照进来。这一页只吃一个字段：`hwinfo_block`
    /// （= 六项 HWiNFO 专属读数这一拍一项都没有），见 <see cref="NoteHwinfoBlock"/>；
    /// 其余每 5 秒刷新一次原厂进程现状。
    /// </summary>
    public void Update(StatusDto? status)
    {
        if (status is not null)
        {
            NoteHwinfoBlock(status);
            // 读数在流 ⇒ 这一行必须收起（判据见 _hwDataFlowing 的说明）
            // ⚠️ 不算 `Pch`：那一项后端是**沿用上次值**的缓存，HWiNFO 停了它也还在，
            //    拿它当"有读数"会让这一行永远不出现（2026-11-22 踩到）。
            var flowing = status.Hwmon?.Gpu is not null
                          || status.Hwmon?.Battery is not null
                          || status.Hwmon?.Vmem is not null;
            if (flowing != _hwDataFlowing)
            {
                _hwDataFlowing = flowing;
                SyncHwinfoRow();
            }
        }
        // 「锁定 Win 键」的真值是进程内的钩子状态，托盘菜单也能切 ⇒ 每拍对一次，
        // 否则设置页开着时用托盘切换，界面一直显示旧状态（只读一个布尔，不碰 EC）。
        if (!_busy) SyncWinKey();
        if (++_tick % 5 == 0)
        {
            LoadVendor();
            // 「没在跑 / 没数据」那行会写"约 N 分钟后才会自己再试"，是**会走字**的文案 ⇒ 重画一次。
            if (_hwRow is HwRowState.NoRun or HwRowState.NoData) SyncHwinfoRow(force: true);
        }
    }

    /// <summary>
    /// 记一拍「快照说 HWiNFO 没给数」，**连续 2 拍才认**。
    ///
    /// ⚠️ 主要的防误报在后端：它要求「连续 12 秒一项读数都没有」才报（足够盖住一次拉起
    ///    3.6~7.4 秒），而且**重试不会重置那段计时**。这里只是再挡一帧抖动，
    ///    所以别把它当成"等 15 秒"的主要来源 —— 2026-11-22 22:45 那版就是这里数到第 2 拍
    ///    被后端的 25 秒静默清零，永远攒不满 3 拍。
    /// ⚠️ 两种病共用这一行，但文案不同（`kind`，见 HwinfoBlock）：进程在跑读不到
    ///    ⇒「在运行但没有数据」；进程压根没起来 ⇒「没有在运行」（带上"多久后会再试"）。
    /// </summary>
    private void NoteHwinfoBlock(StatusDto status)
    {
        string? kind = null;
        string? why = null;
        double left = 0;

        if (status.HwinfoBlock is { } el && el.ValueKind == JsonValueKind.Object)
        {
            // 老契约没有 kind：那种只会是"在运行但没数据"，按它兜底
            kind = el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() : "nodata";
            if (el.TryGetProperty("block_reason", out var w) && w.ValueKind == JsonValueKind.String)
                why = w.GetString();
            if (el.TryGetProperty("blocked_for", out var l) && l.ValueKind == JsonValueKind.Number)
                left = l.GetDouble();
            _hwRowRunning = el.TryGetProperty("running", out var r)
                            && r.ValueKind == JsonValueKind.True;
        }

        var before = (_hwBlocked, _hwRowKind);
        if (kind is null)
        {
            _hwBlockTicks = 0;
            _hwBlocked = false;
        }
        else
        {
            _hwBlockTicks = Math.Min(_hwBlockTicks + 1, 99);
            _hwBlocked = _hwBlockTicks >= 2;
        }
        _hwRowKind = kind;
        _hwBlockWhy = why;
        _hwBlockLeft = left;

        if (before != (_hwBlocked, _hwRowKind)) SyncHwinfoRow();
    }

    /// <summary>
    /// 重新加载这一页（重复点击侧栏「设置」时由 MainWindow 调）：配置项 + 原厂软件现状
    /// + 数据源档位。原厂进程 / 计划任务 / 服务启动类型都是**外部可改**的，
    /// 那边装完 / 卸完不用切页再切回来。
    /// 回写期间（<see cref="_busy"/>）不拦 —— 读回来的就是真值，正好把开关拨回去。
    /// </summary>
    public void Refresh()
    {
        // ⚠️ 主题这一组是**纯本地状态**（<see cref="UiTheme.Mode"/>，侧栏那颗按钮就能改），
        //    必须同步刷完：底下三项都是后台读（`Offload`），等它们回来要几百毫秒，
        //    期间用户看到的就是"点了刷新但开关没动"（2026-10-24 用户报的正是这个）。
        SyncThemeViews();
        LoadSettings();
        LoadVendor();
        SyncHwMode();
    }

    // ================================================================ 读取

    /// <summary>
    /// 「退出原厂软件」按钮的可用性只由「有没有进程 + 现在忙不忙」决定。
    /// 抽出来是为了在 `_busy` 变化的那一拍立刻重算：RenderVendor 用的是**渲染那一刻**
    /// 的 `_busy`，POST 期间的 5 秒轮询会把按钮灰掉，写完不重算就要等下一拍。
    /// </summary>
    private void SyncVendorStop() => _vendorStop.IsEnabled = _vendorRunCount > 0 && !_busy;

    private void LoadSettings() => Offload(() => _api.Settings(), RenderSettings);

    private void LoadVendor()
    {
        // 每 5 秒的轮询与「写完之后立刻读」可能同时在飞；谁先回谁渲染会让
        // POST 之前抓到的旧帧盖掉刚保存成功的开关。只认最后一次请求的结果。
        var seq = Interlocked.Increment(ref _vendorSeq);
        Offload(() => _api.Vendor(), v =>
        {
            if (seq == Volatile.Read(ref _vendorSeq)) RenderVendor(v);
        });
    }

    /// <summary>
    /// 后台读一次、回 UI 线程渲染。
    /// ⚠️ 三种情况都要吞掉：窗口已关（Dispatcher 停了 ⇒ BeginInvoke 抛）、后台抛异常、
    ///    页面已卸载。否则每次关窗都会在日志里留一条"看起来像 bug"的异常。
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

    private void RenderSettings(AppSettingsDto? s)
    {
        // ⚠️ 主题那一组**先刷**，而且不受下面那个 null 分支影响：它的真源是进程内的
        //    UiTheme.Mode（侧栏那颗按钮 / 系统跟随都能改它），不是后端。
        //    早先这行在方法末尾，读不到 /api/app/settings 时直接 return ⇒
        //    「跟随系统外观」这颗开关停在旧状态，只有切页（Loaded 重新触发）才归位。
        SyncThemeViews();
        // 同理：Win 锁的真源是进程内的钩子状态（`/api/app/settings` 里根本没有它），
        // 读不到设置也必须刷 —— 否则钩子被安全软件摘掉后开关还停在"开"。
        SyncWinKey();

        if (s is null)
        {
            EcTag.Text = "EC 读不到";
            _ecDesc.Text = "读不到设置（后端没初始化，或 /api/app/settings 失败）";
            _ecSwitch.IsEnabled = false;
            return;
        }

        _settings = s;

        // EC 控制 -------------------------------------------------------
        var capable = s.EcCapable == true;
        var on = s.EcWrites == true;
        var verified = s.DeviceVerified == true;
        var who = string.IsNullOrWhiteSpace(s.DeviceName) ? "未知机型" : s.DeviceName!;

        _syncing = true;
        _ecSwitch.IsOn = on;
        _ecSwitch.IsEnabled = capable;
        _autoStartSwitch.IsOn = s.Autostart == true;
        SyncOsdModeCombo(s.OsdMode);
        _vendorKillSwitch.IsOn = s.VendorKillOnStart == true;
        foreach (var (key, button) in _startModeButtons)
            button.IsOn = key == (s.StartMode ?? "window");
        SyncStartPageCombo();
        _syncing = false;

        // 卡名叫「EC 与数据源」了，标签单写"已开启"会指代不清 —— 带上 EC。
        EcTag.Text = !capable ? "EC 不可用" : on ? "EC 已开启" : "EC 已关闭";
        _ecDesc.Text = !capable
            ? $"{who} · 本机没有可用的 EC 命令表（没连上广达 RW_GMWMI 接口），控制中心不碰 EC"
            : verified
                ? on
                    ? $"{who} · 已验证机型。风扇/性能模式/键盘背光/显卡模式都靠 EC 命令下发"
                    : $"{who} · 你已关闭 EC 写入：只显示状态，不再下发任何命令（读数照常）"
                : on
                    ? $"{who} · 未适配机型：已打开，按 NL5 语义读写（不对请反馈）"
                    : $"{who} · 未适配机型，默认关闭；打开后按 NL5 语义下发";
    }

    private void RenderVendor(VendorStateDto? s)
    {
        if (s is null)
        {
            _vendorNow.Text = "读取失败：后端可能还没就绪";
            VendorTag.Text = "未知";
            _vendorStop.IsEnabled = false;
            return;
        }

        var run = s.Running ?? [];
        // 状态串用中文短名而不是进程名（ControlCenterDaemon.exe 那一长串在半幅宽里会折三行，
        // 而且用户要的是"谁在跑"，不是 exe 文件名）：按来源归成两类
        var kinds = new List<string>();
        foreach (var name in run)
        {
            var kind = name?.ToLowerInvariant().StartsWith("controlcenter") == true
                       || string.Equals(name, "CdsOSrv.exe", StringComparison.OrdinalIgnoreCase)
                ? "控制中心"
                : "Hotkey OSD";
            if (!kinds.Contains(kind)) kinds.Add(kind);
        }

        var bits = new List<string> { kinds.Count > 0 ? "运行中：" + string.Join(" + ", kinds) : "未运行" };
        if (s.Task?.Exists == true) bits.Add("登录自启：" + (s.Task.Enabled == true ? "开" : "已禁用"));
        if (s.Service?.Exists == true) bits.Add("OSD 服务：" + ServiceLabel(s.Service.StartLabel));
        if (s.Task?.Exists != true && s.Service?.Exists != true)
            bits.Add("没找到原厂的自启动入口（可能没安装）");

        _vendorNow.Text = string.Join(" · ", bits);
        VendorTag.Text = run.Count > 0 ? "运行中" : s.BlockAutostart == true ? "已抑制" : "未运行";
        _vendorRunCount = run.Count;
        SyncVendorStop();

        // 开关以**后端配置**为准（block 那个的后端值只在实际生效后才写 True）
        // 恢复时要说"会恢复成什么"，那个值是我们第一次抑制时记下的原值，不是猜的。
        _vendorSavedStart = s.SavedServiceStart ?? "auto";
        _syncing = true;
        _vendorKillSwitch.IsOn = s.KillOnStart == true;
        _vendorBlockSwitch.IsOn = s.BlockAutostart == true;
        _syncing = false;
    }

    private static string ServiceLabel(string? label) => label switch
    {
        "auto" => "自动",
        "demand" => "手动",
        "disabled" => "已禁用",
        "boot" => "引导",
        "system" => "系统",
        null => "--",
        _ => label,
    };

    private void SyncStartPageCombo()
    {
        var saved = _api.SavedStartPage ?? "dash";
        foreach (var item in _startPageCombo.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag != saved) continue;
            if (!ReferenceEquals(_startPageCombo.SelectedItem, item)) _startPageCombo.SelectedItem = item;
            return;
        }
    }

    /// <summary>
    /// 主题变了（由 <see cref="MainWindow"/> 统一转发，页面自己不订 <see cref="UiTheme.Changed"/>）。
    ///
    /// 这一页有两处显示直接说"现在是哪一档"：卡片头上的标签（浅色 / 深色 / 跟随系统）
    /// 和「跟随系统外观」那颗开关。它们都是 code-behind 写的文字与 IsOn，不走
    /// DynamicResource —— 侧栏那颗白天/黑夜按钮一按，标签就会停在旧的那一档
    /// （白天模式里写着「深色」，是这轮报上来的那个"夜里剩下的东西"）。
    /// </summary>
    public void ApplyTheme() => SyncThemeViews();

    /// <summary>主题相关的两处显示：标签说"跟随系统"还是当前色，开关跟着 <see cref="UiTheme"/>。</summary>
    private void SyncThemeViews()    {
        _syncing = true;
        _themeAutoSwitch.IsOn = UiTheme.Mode == ThemeMode.Auto;
        _syncing = false;
        ThemeTag.Text = UiTheme.Mode == ThemeMode.Auto
            ? "跟随系统"
            : UiTheme.Palette == UiPalette.Light ? "浅色" : "深色";
    }

    /// <summary>
    /// 传感器数据源三档。真值只认后端（`LocalApi.HwinfoMode` 直接问 AppState），
    /// 写完也重画一遍 —— 绝不停在用户点出来的样子。
    /// </summary>
    private void SyncHwMode()
    {
        var mode = _api.HwinfoMode;
        _syncing = true;
        foreach (var (key, button) in _hwModeButtons) button.IsOn = key == mode;
        _syncing = false;

        // 可见文案**必须压成一行**：这一行的右侧是三个分段按钮（比别行的开关宽），
        // 留给说明的位置只有 ~200px。写两行的话这行比同卡其它行高 17px，
        // 左右两卡内部就参差了（2026-10-28 用户报「左右内部的高度不同」）。
        // 完整解释放 ToolTip —— 与关于页「版本块」同一套做法：版面优先，细节悬停看。
        _hwDesc.Text = mode switch
        {
            "always" => "常驻：一直在跑，多占约 82 MB",
            "off" => "关闭：六项数据整行隐藏",
            _ => "按需：开窗才拉起，关窗后收掉",
        };
        _hwDesc.ToolTip = mode switch
        {
            "always" => "常驻：后台一直跑着，随时有数 —— 代价是它单独占约 82 MB 内存",
            "off" => "关闭：从不拉起。PCH 温度、CPU 电压、PL1·PL2、显存结温、"
                     + "显卡电压、硬盘 SMART 这六项整行隐藏",
            _ => $"按需（默认）：打开界面时才拉起，约 3 秒后出数；"
                 + $"关掉界面 {_api.HwinfoIdleSec} 秒后收掉，平时省约 82 MB",
        };

        SyncHwinfoRow(force: true);
    }

    /// <summary>HWiNFO 那一行的几种状态。`Unknown` 只表示"还没画过"，用来强制走一遍。</summary>
    private enum HwRowState { Unknown, Hidden, NotFound, NoData, NoRun }

    /// <summary>
    /// 画「HWiNFO 的病」那一行 —— 一切正常、或者用户已经把数据源关了，就整行收起。
    ///
    /// **唯一判据是后端那句 `hwinfo_block`**（见 HwinfoBlock）：
    ///   · `_hwDataFlowing` —— 快照里真有它给的读数 ⇒ 整行收起（最高否决：有数据就不许贴告示）；
    ///   · `_hwBlocked` + `_hwRowKind` —— 后端说"这一拍六项一项都没读到"，三种：
    ///     `notfound` = 连 exe 都没有，`notrunning` = exe 在、进程没起来，`nodata` = 在跑但读不到。
    /// ⚠️ **不再自己问 `_api.HwinfoFound`**（2026-11-22 19:38）：那是界面侧的缓存探测，
    ///    只在"重新检测"时刷新 —— 拿它当门会出现"exe 明明放好/拿走了，这行却还是老样子"，
    ///    而且"连 exe 都没有"时后端本来就沉默，两个都靠不住时那行就永远不出现。
    ///    现在后端每次快照都照实报 kind（带 5 秒目录缓存），界面只负责画。
    /// ⚠️ **数据源=关闭时整行收起**（2026-11-08 用户定）：那是"我不要这个进程"的意思，
    ///    再摆一行「未找到 / 没数据」外加一个接管按钮，等于在人家关掉的东西上贴告示。
    /// </summary>
    private void SyncHwinfoRow(bool force = false)
    {
        if (force) _hwRow = HwRowState.Unknown;

        var off = _api.HwinfoMode == "off";
        // 有读数 = 它确实在出数（见 _hwDataFlowing）：此时"未找到 / 没数据"一定是判断错了，
        // 不能拿它盖住真相 —— 用户看到"缺了 HWiNFO"而数据明明在，只会一头雾水。
        var state = off || _hwDataFlowing ? HwRowState.Hidden
                  : !_hwBlocked ? HwRowState.Hidden
                  : _hwRowKind == "notfound" ? HwRowState.NotFound
                  : _hwRowKind == "notrunning" ? HwRowState.NoRun
                  : HwRowState.NoData;
        if (state == _hwRow) return;
        _hwRow = state;

        _hwFindRow.Visibility = state == HwRowState.Hidden ? Visibility.Collapsed : Visibility.Visible;
        _hwDownload.Visibility = state == HwRowState.NotFound ? Visibility.Visible : Visibility.Collapsed;
        _hwRecheck.Visibility = state == HwRowState.NotFound ? Visibility.Visible : Visibility.Collapsed;
        _hwTakeover.Visibility = state is HwRowState.NoData or HwRowState.NoRun
            ? Visibility.Visible : Visibility.Collapsed;
        // 同一个按钮两种病→两个词：没起来时谈"接管并重启"是句废话（没有实例可接）
        _hwTakeover.Content = state == HwRowState.NoRun ? "立即拉起" : "接管并重启";
        // 悬停解释只在「没数据 / 没起来」那两态有；别的态不清掉，会把上一态的说明留在新文案上
        _hwFindDesc.ToolTip = null;

        switch (state)
        {
            case HwRowState.Hidden:
                break;

            case HwRowState.NotFound:
                _hwFindTitle.Text = "HWiNFO 未找到";
                _hwFindDesc.Text =
                    "PCH 温度、CPU/GPU 电压、显存结温、PL1·PL2、硬盘 SMART —— 以及没有 EC 的机器上的"
                    + "温度与转速，都由它提供；官方不允许随包分发，所以没带。下载便携版后，把 "
                    + "HWiNFO64.exe 放进本程序目录下的 HWiNFO 文件夹（安装时已经建好，里面有一张说明），"
                    + "再点「重新检测」。也可以用环境变量 CC_HWI_DIR 指向别处。"
                    // 同时另一种病：exe 不在，但系统里有一份在跑、只是没开共享内存导出
                    + (_hwRowRunning
                        ? " 另外：现在有一份 HWiNFO 在跑，但它没把读数导出到共享内存，程序读不到。"
                        : "");
                break;

            case HwRowState.NoRun:
                // 拉起被退避挡住时，光说"没在跑"没用 —— 什么时候会自己再试要写出来，
                // 否则用户只会看到六行一直空着（2026-11-22 报的正是这个：exe 已放好、
                // 程序也认了，但上一轮失败的退避还有 8 分钟，界面上什么提示都没有）。
                // ⚠️ 这一行只有一行高度（设置页一屏零余量）：原因留在悬停里，正文压到一行。
                _hwFindTitle.Text = "HWiNFO 没有在运行";
                _hwFindDesc.Text = _hwBlockLeft > 0
                    ? "它没起来，这六项都是空的。"
                      + (_hwBlockLeft < 60 ? $"{_hwBlockLeft:0} 秒" : $"{_hwBlockLeft / 60:0} 分钟")
                      + "后才会自动再试，点「立即拉起」不用等。"
                    : "它没在跑，这六项都是空的。点「立即拉起」现在起一份。";
                _hwFindDesc.ToolTip =
                    "PCH 温度、CPU/GPU 电压、PL1·PL2、显存结温、硬盘 SMART 这六项都靠 HWiNFO 给。"
                    + "它是单独的程序，控制中心只负责拉起它、读它的共享内存 —— "
                    + "这次它没起来（或者被关掉了）。"
                    + (_hwBlockLeft > 0 ? $"\n\n上一次没起来的原因：{_hwBlockWhy}" : "");
                break;

            case HwRowState.NoData:
                // 一句话说完：为什么没数 + 接管会做什么。细节留给确认框与悬停
                // （用户定：这一行不是文档，别把 12 小时规则、INI 归属都铺在界面上）。
                _hwFindTitle.Text = "HWiNFO 在运行，但没有数据";
                _hwFindDesc.Text = "它没导出读数（这一项默认关闭）。接管后由本程序目录那份重新拉起。";
                _hwFindDesc.ToolTip =
                    "读数只有 HWiNFO 把传感器导出到共享内存才读得到，而这一项从 7.0 起默认关闭，"
                    + "免费版连续跑满 12 小时还会自动关。你自己打开的那份我们不会去改它的配置，"
                    + "所以只能由控制中心接管：先关掉所有实例，再用本程序目录里那份（已配好导出）重新拉起 ——"
                    + "它的窗口和托盘图标都会被收掉。";
                break;
        }

        // 显隐一变就重算行距：这一行收起时，那 6px 必须还给上面那行（传感器数据源），
        // 否则卡底比卡头厚 6px —— 四张卡各自的卡底就一高一低。
        UiKit.NormalizeRowGaps(EcRows);
    }

    private void HwinfoDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 只开浏览器：**不下载、不镜像、不转发安装包** —— 官方 EULA 禁止再分发。
            Process.Start(new ProcessStartInfo("https://www.hwinfo.com/download/") { UseShellExecute = true });
            Toast("已打开官网下载页：解压后把 HWiNFO64.exe 放进程序目录下的 HWiNFO 文件夹，再点「重新检测」", true);
        }
        catch (Exception ex)
        {
            Toast("打不开浏览器：" + ex.Message, false);
        }
    }

    private void HwinfoRecheck_Click(object sender, RoutedEventArgs e)
    {
        _api.RefreshHwinfoProbe();
        SyncHwinfoRow(force: true);
        Toast(_api.HwinfoFound
                ? "已找到 HWiNFO：" + _api.HwinfoDir
                : "还是没找到。确认 HWiNFO64.exe 在程序目录下的 HWiNFO 文件夹里"
                  + "（安装时已建好），或已设 CC_HWI_DIR",
              _api.HwinfoFound);
    }

    /// <summary>
    /// 「接管并重启」：`POST /api/hwinfo/restart` = 杀全部实例 → 用程序目录那份重新拉起。
    ///
    /// ⚠️ 一定要先确认：它会关掉**用户自己开的那份** HWiNFO。全仓只有这一处和诊断口会
    ///    杀掉别人的实例，且必须由用户亲手点（归属铁律见 `HwInfoProcess`）。
    /// ⚠️ 后端这条要等 60 秒（启动 + 等共享内存），必须丢后台，否则界面僵住。
    /// </summary>
    private async void HwinfoTakeover_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // 「立即拉起」不需要确认：它没有实例可接、也不会碰到用户手动开的那份
        // （那一态等于"一个都没在跑"，见 SyncHwinfoRow 的 NoRun）。
        if (_hwRow != HwRowState.NoRun)
        {
            var go = await UiDialog.ConfirmAsync(this, "让控制中心接管 HWiNFO",
                "会先关掉现在所有 HWiNFO 实例（包括你自己开的那份），再用本程序目录里的那份重新拉起：\n\n"
                + "· 它的窗口和托盘图标都会被收掉，只在后台出数；\n"
                + "· 你正在用 HWiNFO 看的其它数据会中断；\n"
                + "· 免费版连续跑满 12 小时会自己关掉共享内存，到时这一行还会再出现一次。\n\n"
                + "继续吗？", "接管", "取消");
            if (!go) return;
        }

        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/hwinfo/restart", new JsonObject()));
            Toast(result.Ok ? "已接管：约 3 秒后出数" : result.Why("接管失败"), result.Ok);
        }
        finally
        {
            _busy = false;
            SyncHwinfoRow(force: true);
        }
    }

    // ================================================================ 写入

    /// <summary>
    /// 写一个设置键。**无论成败都重新读一遍**：失败时界面必须回到真值，
    /// 不能停在用户点出来的样子。
    /// </summary>
    private async void SaveAsync(JsonObject body, string okMessage, bool reloadVendor = false)
    {
        _busy = true;
        SyncVendorStop();
        try
        {
            var result = await Task.Run(() => _api.Post("/api/app/settings", body));
            var note = result.Raw?["note"] is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : null;
            Toast(result.Ok ? (note ?? okMessage) : result.Why("保存失败"), result.Ok);
        }
        finally
        {
            _busy = false;
            LoadSettings();
            if (reloadVendor) LoadVendor();
            else SyncVendorStop();     // 不额外查 COM，也要把按钮按写后的 _busy 拨回来
        }
    }

    private void Ec_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _syncing) return;

        var want = !_ecSwitch.IsOn;
        // 未适配机型：打开前必须问一句。关掉不问（关掉永远是更安全的方向）。
        if (want && _settings?.DeviceVerified != true) ConfirmUnverifiedEc(want);
        else SaveEc(want);
    }

    private async void ConfirmUnverifiedEc(bool want)
    {
        var name = _settings?.DeviceName ?? "这台机器";
        var go = await UiDialog.ConfirmAsync(this, "确认打开 EC 写入",
            $"{name} 没有在本机标定过：命令表、温度/转速偏移与背光分区都按 NL5 逆向出来的表预置（同一广达固件家族）。\n\n"
            + "打开后风扇、性能模式、键盘背光、显卡模式都按这套语义写进 EC —— "
            + "写错可能导致风扇停转、亮错分区、屏幕参数异常。读数越界（≤0 或 >115 ℃、>8000 rpm）时会自动退回 HWiNFO。\n\n"
            + "建议逐项试一遍；不对就关掉开关，并在「关于 › 机型适配」交一份采集包。\n\n"
            + "确认要打开 EC 写入吗？（随时可以在这一页关掉）",
            "打开写入", "取消", danger: true);
        if (go) SaveEc(want);
        else RenderSettings(_settings);      // 取消：把开关拨回真值（它本来就没被点过，这里只是稳一下）
    }

    private void SaveEc(bool want) => SaveAsync(new JsonObject { ["ec_enabled"] = want },
        want ? "已开启 EC 写入" : "已关闭 EC 写入：之后不再下发任何命令（风扇会停在最后写入的转速上）");

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _syncing) return;
        var want = !_autoStartSwitch.IsOn;
        SaveAsync(new JsonObject { ["autostart"] = want },
            want ? "已开启：登录后静默进托盘（下次生效）" : "已关闭：登录后不再自动启动（下次生效）");
    }

    private void StartPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _busy) return;
        if (_startPageCombo.SelectedItem is not ComboBoxItem item) return;
        var key = (string?)item.Tag;
        if (key is null) return;

        // 走 /api/model/preset（单个配置项）：WPF 侧的启动页只由本程序读，
        // 不必为此给 /api/app/settings 加一个新键（那会动一次契约）。
        var body = new JsonObject
        {
            ["path"] = new JsonArray("ui", "start_page"),
            ["value"] = key,
        };
        _ = SaveStartPageAsync(body, (string)item.Content);
    }

    private async Task SaveStartPageAsync(JsonObject body, string label)
    {
        var result = await Task.Run(() => _api.Post("/api/model/preset", body));
        Toast(result.Ok ? $"启动默认页：{label}（下次生效）" : result.Why("保存失败"), result.Ok);
    }

    /// <summary>传感器数据源切换：写 `presets.hwinfo.mode`（走通用配置项接口，不动契约）。</summary>
    private void SaveHwMode(string mode)
    {
        if (_busy || _syncing) return;
        _ = SaveHwModeAsync(new JsonObject
        {
            ["path"] = new JsonArray("hwinfo", "mode"),
            ["value"] = mode,
        }, mode);
    }

    private async Task SaveHwModeAsync(JsonObject body, string mode)
    {
        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/model/preset", body));
            var note = mode switch
            {
                "always" => "已设为常驻：HWiNFO 一直在后台跑",
                "off" => "已关闭：不再拉起 HWiNFO（那六项会整行隐藏）",
                _ => "已设为按需：打开界面时才拉起，看完自动收掉",
            };
            Toast(result.Ok ? note : result.Why("保存失败"), result.Ok);
        }
        finally
        {
            _busy = false;
            SyncHwMode();      // 无论成败都回到真值
        }
    }

    private void ThemeAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var want = !_themeAutoSwitch.IsOn;

        // 打开 = 跟随系统；关掉 = 钉在当前生效的那一档
        var next = want ? ThemeMode.Auto : UiTheme.ManualOpposite();
        if (UiTheme.SetMode(next)) _api.SaveTheme(UiTheme.Token(next));
        SyncThemeViews();
        Toast(want ? "已跟随系统外观：Windows 切换深浅色时界面马上跟着变" : "已固定为当前外观", true);
    }

    private void OsdMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || _syncing) return;
        if (_osdModeCombo.SelectedItem is not ComboBoxItem item) return;
        if ((string?)item.Tag is not { Length: > 0 } key) return;

        var tip = key switch
        {
            "vendor" => "已切换：全用原厂（背光无提示）",
            "vendor_backlight" => "已切换：原厂 + 补背光",
            "ours" => "已切换：只用我们（原厂 OSD 已停）",
            _ => "已切换：两边都弹",
        };
        SaveAsync(new JsonObject { ["osd_mode"] = key }, tip);
    }

    /// <summary>把下拉框贴到配置里的模式上（值没变就不动，避免每拍刷新触发一次 SelectionChanged）。</summary>
    private void SyncOsdModeCombo(string? saved)
    {
        var want = string.IsNullOrEmpty(saved) ? "vendor_backlight" : saved;
        foreach (var item in _osdModeCombo.Items.OfType<ComboBoxItem>())
        {
            if ((string?)item.Tag != want) continue;
            if (!ReferenceEquals(_osdModeCombo.SelectedItem, item)) _osdModeCombo.SelectedItem = item;
            return;
        }
    }

    /// <summary>
    /// 「锁定 Win 键」按**实际拦截状态**画（<see cref="LocalApi.WinKeyLocked"/>），不按配置画：
    /// 钩子装失败时配置里可能写着 true，而一个键都没拦住 —— 那种界面就是在撒谎。
    ///
    /// 每拍都会调（见 <see cref="Update"/>）：这个开关在**托盘菜单**里也能切，
    /// 设置页开着的时候从托盘点一下，界面必须跟着变，否则它显示的就是旧状态。
    /// 值没变就一个字节都不写（不白动 DP，也不白动 `_syncing` 那道门）。
    /// </summary>
    private void SyncWinKey()
    {
        var on = _api.WinKeyLocked;
        if (_winLockSwitch.IsOn == on) return;

        _syncing = true;
        _winLockSwitch.IsOn = on;
        _syncing = false;
    }

    /// <summary>
    /// 锁定 / 解锁 Win 键。走 `/api/keyboard/win-lock`（不走 `/api/app/settings`：
    /// 这个开关的真值在钩子身上，不在配置里，写失败必须把开关拨回去）。
    ///
    /// ⚠️ 别在这里自己拨 `IsOn`：成功与否以钩子实际状态为准，`SyncWinKey` 统一回读。
    /// </summary>
    private async void WinKeyLock_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _syncing) return;
        var want = !_winLockSwitch.IsOn;

        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/keyboard/win-lock",
                new JsonObject { ["locked"] = want }));
            Toast(result.Ok
                    ? want
                        ? "已锁定 Win 键：按 Win 不再弹开始菜单（关掉控制中心会自动恢复）"
                        : "已解锁 Win 键"
                    : result.Why("切换失败"),
                result.Ok);
        }
        finally
        {
            _busy = false;
            SyncWinKey();      // 无论成败都回到真值
        }
    }

    /// <summary>
    /// 抑制 / 恢复原厂开机自启 —— 原厂卡里**唯一会真动系统状态**的开关
    /// （计划任务 Enabled + 服务启动类型，两处都可逆）。
    ///
    /// ⚠️ 不要在这里自己改 `IsOn`：成败以后端实际结果为准（后端写成功才落盘
    ///    `block_autostart`），`reloadVendor` 会读回真值把开关拨正。自己先拨过去，
    ///    失败时界面就在撒谎（用户定的规矩：绝不显示错的状态）。
    /// </summary>
    private void VendorBlock_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _syncing) return;
        var want = !_vendorBlockSwitch.IsOn;

        SaveAsync(new JsonObject { ["vendor_block_autostart"] = want },
            want
                ? "已抑制：登录自启与 OSD 服务自启已关闭（当前进程不受影响，下次生效）"
                : _vendorSavedStart == "disabled"
                    ? "已恢复：它本来就是禁用的，没有可恢复的自启（开关已按实际状态复位）"
                    : "已恢复：原厂自启回到「" + ServiceLabel(_vendorSavedStart) + "」，"
                      + "OSD 服务已拉起（原厂控制中心本身下次登录时回来）",
            reloadVendor: true);
    }

    private void VendorKill_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _syncing) return;
        var want = !_vendorKillSwitch.IsOn;
        // 只写配置、此刻不动任何进程 —— 开关名说的是"启动时"。要立刻退请点右边那颗。
        SaveAsync(new JsonObject { ["vendor_kill_on_start"] = want },
            want ? "已开启：启动控制中心时自动退出原厂软件（下次生效）"
                 : "已关闭：启动时不再动原厂软件",
            reloadVendor: true);
    }

    private async void VendorStop_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _vendorStop.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/vendor/stop", new JsonObject()));

            var parts = new List<string>();
            if (result.Raw?["services"] is JsonArray services && services.Count > 0)
                parts.Add("停服务 " + string.Join("、", services.Select(x => x?.ToString())));
            if (result.Raw?["killed"] is JsonArray killed && killed.Count > 0)
                parts.Add("结束进程 " + string.Join("、", killed.Select(x => x?.ToString())));

            var errors = result.Raw?["errors"] is JsonArray errs && errs.Count > 0
                ? string.Join("；", errs.Select(x => x?.ToString()))
                : null;

            // 后端会把 `hkosdservice` 按当前提示条模式重新拉起（见 AppState.StopVendor）：
            // 于是"停服务"那一项马上又被恢复，文案必须如实说，否则用户以为被停掉了。
            var kept = result.Raw?["osd_service_kept"]?.GetValue<bool>() == true;

            Toast(result.Ok
                    ? "已退出原厂软件" + (parts.Count > 0 ? "（" + string.Join(" · ", parts) + "）" : "")
                      + (kept ? "；提示条用的 OSD 服务已按设置恢复" : "")
                    : errors ?? result.Why("退出失败"),
                result.Ok);
        }
        finally
        {
            _busy = false;
            LoadVendor();
        }
    }

    // ================================================================ 小工具

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);
}
