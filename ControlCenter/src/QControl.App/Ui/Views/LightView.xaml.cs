using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using QControl.App.Http.Contract;
using QControl.App.Ui.Controls;
using QControl.Core.Devices;
using ShapePath = System.Windows.Shapes.Path;

namespace QControl.App.Ui.Views;

/// <summary>
/// 背光页：分区 tab + 键盘示意图 / 灯光开关 / 灯效 / 色盘 / 亮度。
///
/// **分区数不是固定的三区**：以 `caps.kb_zones`（来自机型档案）+ `presets.backlight.zones`
/// 为准，1/2/3/4… 都要能画。三区是本机 NL5 的标定结果，别的数量按色彩数均分示意。
///
/// 数据来源分两半，别混：
///   · **颜色与灯效** EC 读不回来（写进去就没人记得），所以真源是 `presets.backlight`
///     —— 首次进页从 <see cref="LocalApi.SavedBacklight"/> 取一次，之后以界面内存为准；
///   · **亮度档位**是真状态，走每秒快照的 `ec.backlight_level`。
///
/// 别"简化"的几条：
///   · 档位下发有 <see cref="SettleMs"/> 的待确认窗口：快照有 1.5~2.8s 采样延迟，
///     点完「50%」立刻点颜色，请求里的 level 会被旧快照回写 —— 背光被以 100% 重新点亮；
///   · **三区必须一起下发**（后端拿 zones 重建 presets，只发一区会把另两区丢掉）；
///   · 色盘拖动中只改本地预览，**松手才写 EC**（一次下发是 3 条命令，靠 EC 层的写批
///     连发压到 ~140ms，见 `EcHal.SetBacklightZones`；拖一格写一次会把这个预算吃光）；
///   · **分区联动**（原厂"神光同步"）是状态不是按钮：开着时调任一区全体一起变，
///     真源 `presets.backlight.sync`，开关在 tab 行右侧（见 <see cref="RenderSync"/>）；
///   · **按模式**：办公/游戏/狂暴加原厂第四套「自定义」各有档案（`perf.modes[档].light`），
///     切系统模式由后端重放该档配色 —— 常驻行为，不再有"按模式记忆"开关（它与「自定义」作用冲突）。
///     那四颗 chip 是"编辑哪一档"，**点前三颗 chip 不写 EC**，真调色才写；
///     没接管时 chip 会自动跟着系统档走（用户手动点别的档只是临时查看，下次切档自动回到当前档）；
///     点第四颗「自定义」= 接管，重放的是**自定义档案本身**。原厂点 Custom 就是加载 Custom 存档
///     （出厂默认白/常亮/50%），绝不拿当前灯色覆盖它 —— 别再把当前配色播种进 custom（见 <see cref="ModeChip_Click"/>）；
///   · 写失败一律回到真值（开关拨回去、档位重读），绝不停在用户点出来的样子。
/// </summary>
public partial class LightView : UserControl
{
    /// <summary>灯效码 → 名字。码来自原厂抓包（DT3D 高半字节），不要列 0~15。</summary>
    private static readonly (long Code, string Name, Geometry Icon)[] Effects =
    [
        (1, "常亮", Icons.EffSteady),
        (3, "呼吸", Icons.EffBreath),
        (6, "多彩循环", Icons.EffCycle),
        (7, "流光溢彩", Icons.EffWave),
    ];

    /// <summary>亮度档位 → 卡头/提示文案（与后端 server.BRI_SUBTITLE 一致）。</summary>
    private static readonly string[] LevelNames = ["关闭", "低亮", "高亮"];

    /// <summary>
    /// 自动熄灭的特殊码：只锁屏/睡眠熄，闲置不熄（<see cref="AutoOffChoices"/> 用一个下拉表达，
    /// 因为「闲置 X 分钟」与「锁屏就熄」对用户是**互斥的选择**而不是叠加）。
    /// </summary>
    private const int LockCode = -1;

    private static readonly (int Code, string Label)[] AutoOffChoices =
    [
        (0, "不自动熄灭"),
        (1, "闲置 1 分钟"),
        (3, "闲置 3 分钟"),
        (5, "闲置 5 分钟"),
        (10, "闲置 10 分钟"),
        (LockCode, "锁屏 / 睡眠时"),
    ];

    /// <summary>下发的档位在这段时间内不被旧快照覆盖（5000ms）。</summary>
    private const int SettleMs = 5000;

    private readonly LocalApi _api;

    /// <summary>最近一份快照 —— 刷新时用它重推档位，不必等下一次 tick。</summary>
    private StatusDto? _status;

    private readonly List<CardButton> _tabs = [];
    private readonly Dictionary<long, CardButton> _effButtons = [];
    private readonly List<(CardButton Button, int[] Rgb)> _pures = [];

    private int[][] _zones = [];
    private int _active;
    private long _effect = 1;
    private int _level = 2;
    private int _lastLevel = 2;          // 关灯前记住的档位，重新打开时恢复
    private bool _suppress;              // 渲染回写控件时不要触发写入
    private bool _busy;
    private bool _autoOffSyncing;        // 下拉回写时不要触发写入（与 _suppress 分开：它只管 EC 那一组）
    private bool _autoOffBusy;

    /// <summary>
    /// 分区联动（原厂的「神光同步」）：开启后调任意一个区的颜色，其余分区跟着变。
    /// 真源是 `presets.backlight.sync`，缺省 false（= 各区独立，老行为）。
    /// </summary>
    private bool _sync;
    private bool _syncBusy;

    /// <summary>
    /// 键盘之外的独立灯珠颜色（Logo / 尾灯）。**只有机型档案声明了才有条目**；
    /// 和键盘分区表完全分开，避免再次出现"探针把 7/8 当分区、页面冒出五个 tab"。
    /// </summary>
    private readonly Dictionary<string, int[]> _extraColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _extraButtons = new(StringComparer.Ordinal);
    private string? _activeExtra;   // null = 色盘正在改键盘分区；否则是 Logo / 尾灯
    /// <summary>上一次摆出来的灯珠集合指纹（见 <see cref="RefreshExtraLights"/>）。</summary>
    private string _extraSig = "";

    /// <summary>原厂第四套的「自定义接管」（`presets.backlight.custom_override`）。</summary>
    private bool _customOverride;
    private bool _customBusy;

    /// <summary>正在编辑哪一档的灯效（null = 首帧还没选，按当前系统档读；可以是第四套 `custom`）。</summary>
    private string? _editMode;

    /// <summary>上一拍的系统模式（用来识别"切档了"，以及编辑目标要不要跟着漂）。</summary>
    private string? _lastCurMode;

    /// <summary>
    /// 键盘上此刻正在显示的是哪一套（与后端 `AppState.LiveBacklightKey` 同口径：
    /// 自定义接管开着时就是 `custom`）。**亮度条只有在这一档才跟着 EC 走** ——
    /// 编辑别的档时，EC 里的亮度是"现在这档的"，拿它显示会骗人。
    /// </summary>
    private string? LiveKey => _customOverride ? "custom" : BacklightModes.Key(_lastCurMode);

    /// <summary>正在编辑的这一档是不是键盘上正在显示的那一套（null = 还不知道，按当前档算）。</summary>
    private bool EditingLive => _editMode is null || _editMode == LiveKey;

    private readonly Dictionary<string, CardButton> _modeChips = [];

    private DispatcherTimer? _brightTimer;
    private long? _levelWant;
    private long _levelWantAt;

    internal LightView(LocalApi api)
    {
        _api = api;
        InitializeComponent();

        BuildTabs();
        BuildEffects();
        BuildPures();
        BuildAutoOff();
        BuildSync();
        BuildModes();
        BuildExtraLights();

        Wheel.Picked += OnWheelPicked;
        BrightSlider.ValueChanged += Bright_ValueChanged;
        BrightSlider.IsMoveToPointEnabled = true;   // 点轨道直接跳

        // 点键盘示意图上的分区 = 点上方那颗同名 tab（同一件事，见 SelectZone）
        Keyboard.ZoneClicked += (_, index) => SelectZone(index);

        UiKit.FillViewport(Scroll, Root);
        UiKit.EnableSpaceEvenly(Ctl, minGap: 10, maxGap: 40);

        // 主题变化由 MainWindow 统一转发（ApplyTheme）—— 页面自己不订全局事件，
        // 原因见 DashView.ApplyTheme 的注释（关窗 Unloaded 不触发 ⇒ 订阅泄漏 ⇒
        // 下一个窗口切主题时调到死线程上的控件 ⇒ 闪退）。

        // 颜色/灯效是**配置**（presets.backlight，纯内存读，不碰 EC），构造时就能取；
        // 档位是真状态，得等快照 —— 那个在 Update 里。先读配置也是为了别让首帧
        // 显示白色的假数据（用户在这 1 秒内点颜色会拿白色覆盖自己的配置）。
        LoadFromPresets();

        // 构造函数可能跑在 Dispatcher 消息循环起来之前（--page light 截图路径），
        // 首帧快照放 Loaded 之后再取。
        // ⚠️ 只在**还没吃到过任何快照**时补这一帧：页面每次被切回可见（PageHost.Content
        //    换回来）都会再触发一次 Loaded，无条件 Update(null) 会把档位打回「读取中…」
        //    再等下一拍才恢复 —— 和散热页风扇圈那个问题是同一处（见 CoolView 构造里的注释）。
        Loaded += (_, _) =>
        {
            if (_status is null) Update(null);
        };
    }

    // ================================================================ 构建

    private void BuildTabs()
    {
        var addrs = _api.KbZoneAddrs;
        var names = ZoneNames(addrs.Length);
        for (var i = 0; i < addrs.Length; i++)
        {
            var tab = new CardButton
            {
                Content = names[i],
                Style = (Style)FindResource("KbTab"),
                // EC 地址写进提示：排障时一眼能对上抓包。
                // 非三区时再补一句"位置是示意"—— 原厂只给了地址，没给物理位置，
                // 图上按色彩数均分画，别让人以为那是标定过的分区边界。
                ToolTip = "EC 地址 " + addrs[i]
                          + (addrs.Length != 3 ? "（示意图按色彩数均分，不是实测分区位置）" : ""),
                Tag = i,
            };
            tab.Click += Tab_Click;
            _tabs.Add(tab);
            TabsHost.Children.Add(tab);
        }
    }

    /// <summary>tab 名字。三区沿用 A/B/C（本机标定），其它数量按字母顺延。</summary>
    private static string[] ZoneNames(int count)
        => count <= 1
            ? ["键盘"]
            : Enumerable.Range(0, count).Select(i => "键盘" + (char)('A' + i) + "区").ToArray();

    private void BuildEffects()
    {
        foreach (var (code, name, icon) in Effects)
        {
            var stack = new StackPanel { Orientation = Orientation.Vertical };
            stack.Children.Add(new ShapePath
            {
                Data = icon,
                Style = (Style)FindResource("KbEffIcon"),
            });
            stack.Children.Add(new TextBlock
            {
                Text = name,
                Style = (Style)FindResource("KbEffText"),
            });

            var button = new CardButton
            {
                Content = stack,
                Style = (Style)FindResource("KbEff"),
                ToolTip = $"{name}（EC {code}）",
                Tag = code,
            };
            button.Click += Effect_Click;
            _effButtons[code] = button;
            EffHost.Children.Add(button);
        }

        // UniformGrid 里最后一颗不要再留右边距，否则整排看起来右缩了 8px
        if (EffHost.Children.Count > 0)
            ((FrameworkElement)EffHost.Children[^1]).Margin = new Thickness(0);
    }

    /// <summary>
    /// 预设纯色板：HSV 每 30° 抽一个纯色（s=v=1）再加白 —— 在色盘上描点很难正好落到"纯色"上，
    /// 这排就是给"我就要纯红 / 纯绿"用的。色值与色盘共用同一套 hsv2rgb，
    /// 所以点完色盘上的指示环会精确落在盘上对应位置。
    /// </summary>
    private void BuildPures()
    {
        (string Name, int[] Rgb)[] list =
        [
            ("红", FromHue(0)), ("橙", FromHue(30)), ("黄", FromHue(60)), ("绿", FromHue(120)),
            ("青", FromHue(180)), ("蓝", FromHue(240)), ("紫", FromHue(270)), ("品红", FromHue(300)),
            ("白", [255, 255, 255]),
        ];

        foreach (var (name, rgb) in list)
        {
            var button = new CardButton
            {
                Style = (Style)FindResource("KbPure"),
                Background = Frozen(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2])),
                ToolTip = name,
            };
            button.Click += Pure_Click;
            _pures.Add((button, rgb));
            PuresHost.Children.Add(button);
        }
    }

    private static int[] FromHue(double hue)
    {
        var (r, g, b) = HsvToRgb(hue, 1, 1);
        return [r, g, b];
    }

    /// <summary>
    /// 自动熄灭下拉（真源 `presets.light`）。选项见 <see cref="AutoOffChoices"/> ——
    /// 「闲置 X 分钟」与「锁屏就熄」互斥，所以是一个下拉而不是两颗控件。
    /// </summary>
    private void BuildAutoOff()
    {
        foreach (var (code, label) in AutoOffChoices)
            AutoOffCombo.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        AutoOffCombo.SelectionChanged += AutoOff_Changed;
    }

    /// <summary>
    /// 分区联动开关（原厂叫「神光同步」，跟「灯光」并排放在右栏）。
    ///
    /// 为什么不是一颗「全部同色」按钮：那是**一次性动作**，原厂那个是**持续状态** ——
    /// 用户开一次，之后调哪个区都是全体一起变，不用每换一次颜色再点一次。
    /// 所以它做成开关 + 落盘（重启后还在），「全部同色」按钮已经撤掉（联动取代了它）。
    /// 单分区机型没有"联动"这回事（自己跟自己联动），整行收掉。
    /// </summary>
    private void BuildSync()
    {
        if (_api.KbZoneAddrs.Length <= 1) SecSync.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Logo / 尾灯的颜色块：只有**这次判定启用**的才显示。
    ///
    /// <para>
    /// 判据不只看"档案里写了地址"：`ec.kb_extra_lights` 写在机型档案里，而档案可能是
    /// 按三风扇 SKU 升级过来的 —— 同模具的三风扇机大多数没有 Logo 灯。所以真源是
    /// `Device.ExtraLightsByProfile`（见 ExtraLightsGateTests）：**只有名字命中档案**才为真。
    /// 没有用户开关（2026-11-18 撤掉），识别不到就一个控件都不摆。
    /// </para>
    /// </summary>
    private void BuildExtraLights()
    {
        var addrs = _api.ExtraLightAddrs;
        _extraSig = SigOfExtraLights(addrs);
        _extraButtons.Clear();

        LogoRow.Visibility = addrs.ContainsKey("logo") ? Visibility.Visible : Visibility.Collapsed;
        TailRow.Visibility = addrs.ContainsKey("tail") ? Visibility.Visible : Visibility.Collapsed;
        if (addrs.ContainsKey("logo")) _extraButtons["logo"] = LogoSwatch;
        if (addrs.ContainsKey("tail")) _extraButtons["tail"] = TailSwatch;

        // 色盘原来指着的那一路可能没了（换机 / 识别变了）—— 别让 `_activeExtra` 指向不存在的色块
        if (_activeExtra is not null && !addrs.ContainsKey(_activeExtra))
            _activeExtra = addrs.ContainsKey("logo") ? "logo" : addrs.ContainsKey("tail") ? "tail" : null;

        SecExtraLights.Visibility = addrs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>灯珠集合的指纹：只用来判断"要不要重建"，顺序无关。</summary>
    private static string SigOfExtraLights(IReadOnlyDictionary<string, int> addrs)
        => string.Join("|", addrs.Keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>
    /// 灯珠集合只在换机 / 识别结果变化时才会变（没有用户开关）。
    /// 每次刷新比一次指纹，变了才重建 —— 无条件重建会丢当前选中的那一路。
    /// </summary>
    private void RefreshExtraLights()
    {
        if (SigOfExtraLights(_api.ExtraLightAddrs) == _extraSig) return;
        BuildExtraLights();
    }

    /// <summary>
    /// 键盘上方那四颗档案按钮（办公 / 游戏 / 狂暴 / 自定义）：
    /// 前三颗点谁编辑谁；第四颗是接管开关（三档同一套）。
    ///
    /// 为什么不写成"切模式时自动把本页切过去"：那样用户没法给"现在没用到的档"调色
    /// （比如想在插电时先配好办公档的配色）。chip 把"编辑目标"和"系统当前档"分开了，
    /// 前者是用户的选择，后者用 ● 标出来，两个都不会骗人。
    /// </summary>
    private void BuildModes()
    {
        foreach (var key in BacklightModes.Keys)
        {
            var chip = new CardButton
            {
                Content = BacklightModes.Label(key),
                // 用温度来源那种小胶囊，不用 KbTab 的宽 tab：UniformGrid 拉满时又扁又长。
                Style = (Style)FindResource("MiniCard"),
                Width = 88,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = key,
            };
            chip.Click += ModeChip_Click;
            _modeChips[key] = chip;
            ModeHost.Children.Add(chip);
        }

        if (ModeHost.Children.Count > 0)
            ((FrameworkElement)ModeHost.Children[^1]).Margin = new Thickness(0);
    }

    // ================================================================ 每秒刷新

    /// <summary>
    /// 重新加载（重复点击侧栏「背光」时由 MainWindow 调）：颜色 / 灯效是**配置**，
    /// 可能被另一处改过 —— 光靠每秒快照是刷不回来的（EC 读不回颜色）。
    /// 档位来自快照，用最近一份重推；还没有快照时 <see cref="Update"/> 会显示"读取中…"。
    /// </summary>
    public void Refresh()
    {
        // 灯珠集合只随「识别结果 / 换机」变（没有用户开关），
        // 指纹变了就重建控件，再把颜色补上 —— 顺序不能反。
        RefreshExtraLights();
        LoadFromPresets();
        Update(_status);
    }

    /// <summary>主窗口每秒推一份快照进来。只取「亮度档位」这一项（颜色/灯效 EC 读不回来）。</summary>
    public void Update(StatusDto? status)
    {
        _status = status;

        // ---- 系统模式：可能是我们在性能页切的，也可能是 Fn 键 / 原厂软件改的 ----
        // 只要它变了，**非自定义接管状态下**就把档案选中态自动带回当前系统档
        // （2026-11-18 用户要求：没有自定义接管时，"当前编辑哪一档"就该等于系统在哪一档；
        //  用户之前手动点过别的档也不钉住 —— 那是临时查看，不是要跟系统脱钩）。
        // 只有自定义接管开着时保持 custom：那是显式接管，切系统档不该把它顶掉。
        if (status?.Perf?.Mode is { } cur && BacklightModes.Key(cur) is { } key && key != _lastCurMode)
        {
            _lastCurMode = key;
            if (!_customOverride && key != _editMode)
            {
                _editMode = key;
                LoadFromPresets();          // 载入新档的配色（后端重放的是同一份，页面不再等快照）
                RenderZones();
            }
            RenderModes();
        }

        if (status is null)
        {
            LevelTag.Text = "读取中…";
            return;
        }

        // 编辑的不是键盘上这一套 ⇒ 亮度条摆**这一档档案里记的值**（LoadFromPresets 已设好），
        // 不拿 EC 覆盖：EC 反映的是键盘现在那一档，照抄过去会让四个档永远看着同一个亮度，
        // 用户也就永远看不到"这一档记的是 50%"（2026-11-19 报的"亮度还是统一的"）。
        if (!EditingLive)
        {
            RenderLevel(_level);
            LevelTag.Text = _level switch
            {
                0 => "这一档：关闭 · 切过去才生效",
                1 => "这一档：50% · 切过去才生效",
                _ => "这一档：100% · 切过去才生效",
            };
            return;
        }

        var level = status.Ec?.BacklightLevel is { } v ? (int)v : (int?)null;
        if (level is { } now)
        {
            // 待确认窗口：刚下发的档位在快照追上之前不采纳（见类注释）
            if (_levelWant is { } want && now != want
                && Environment.TickCount64 - _levelWantAt < SettleMs)
            {
                level = (int)want;
            }
            else
            {
                _levelWant = null;
                _level = now;
                if (now > 0) _lastLevel = now;     // 关灯后要恢复回来的那个档位
            }
        }

        RenderLevel(level);
        LevelTag.Text = level is { } lv ? "档位 " + lv : "档位读不到";
    }

    /// <summary>首帧：颜色 / 灯效 / 分区 / 联动开关从 presets 恢复一次（之后不覆盖用户正在调的值）。</summary>
    /// <remarks>
    /// 真源是**某一档的档案**（`perf.modes[档].light`，共四套含 custom）：
    ///   · 那一档有档案 ⇒ 读它；
    ///   · 还没有（只调过别的档）⇒ 退回当前生效的那套，不自己编一套。
    /// </remarks>
    private void LoadFromPresets()
    {
        _customOverride = _api.BacklightCustomOverride;
        _lastCurMode = _api.PerfModeKey ?? _lastCurMode;

        // 编辑目标必须落在四套档案之一；没选过就跟着系统走（接管开着则先给你看自定义）
        if (BacklightModes.Key(_editMode) is null)
            _editMode = _customOverride
                ? "custom"
                : (BacklightModes.Key(_lastCurMode) ?? BacklightModes.Keys[0]);

        var saved = _api.SavedModeProfile(_editMode) ?? _api.SavedBacklight();

        _zones = saved.Zones.Select(x => x.ToArray()).ToArray();
        if (saved.Effect is { } eff && Effects.Any(e => e.Code == eff)) _effect = eff;
        _sync = saved.Sync;

        // 亮度：**这一档档案里记住的那一档**。
        //   · 编辑的就是键盘上这一套 ⇒ 亮度该跟 EC 走（真状态），交给 Update；
        //   · 编辑的是别的档 ⇒ EC 里的值属于"现在这档"，拿它显示会让四个档看着一模一样
        //     （用户 2026-11-19 报"亮度还是统一的"，根因就在这）。这里直接摆该档记忆值。
        if (!EditingLive && saved.Level is { } savedLevel)
        {
            _level = Math.Clamp((int)savedLevel, 0, 2);
            _levelWant = null;
            RenderLevel(_level);
        }

        // 独立灯珠：档案里没记过就先用当前键盘色做预览（不写 EC）；用户一改色就落盘。
        var fallback = _zones.Length > 0 ? _zones[0].ToArray() : new[] { 255, 255, 255 };
        foreach (var name in _api.ExtraLightAddrs.Keys)
        {
            var rgb = name == "logo" ? saved.Logo : saved.Tail;
            _extraColors[name] = rgb?.ToArray() ?? fallback.ToArray();
        }

        _suppress = true;
        foreach (var (code, button) in _effButtons) button.IsOn = code == _effect;
        _suppress = false;

        RenderEffectTag();
        RenderZones();
        RenderSync();
        RenderExtras();
        RenderModes();
        SyncAutoOff();
    }

    // ================================================================ 渲染

    private void RenderZones()
    {
        if (_zones.Length == 0) return;
        _active = Math.Clamp(_active, 0, _zones.Length - 1);

        Keyboard.SetZones(_zones, _active);
        for (var i = 0; i < _tabs.Count; i++)
            _tabs[i].IsOn = _activeExtra is null && i == _active;

        // 色盘/色值跟着"当前在编辑谁"走：键盘分区，或者独立灯珠（Logo / 尾灯）
        var rgb = _activeExtra is not null && _extraColors.TryGetValue(_activeExtra, out var extra)
            ? extra
            : _zones[_active];
        HexText.Text = "#" + rgb[0].ToString("X2") + rgb[1].ToString("X2") + rgb[2].ToString("X2");
        Wheel.SetColor(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));

        // 预设纯色：与当前区颜色相同的那颗标出来
        foreach (var (button, pures) in _pures)
            button.IsOn = pures[0] == rgb[0] && pures[1] == rgb[1] && pures[2] == rgb[2];
    }

    private void RenderLevel(int? level)
    {
        _suppress = true;
        try
        {
            if (level is { } lv)
            {
                BrightValue.Text = lv switch { 0 => "关闭", 1 => "50%", 2 => "100%", _ => lv + "/2" };
                // 手指还按在滑块上时别回写（否则拖到一半被拽回去）
                if (!BrightSlider.IsMouseCaptureWithin) BrightSlider.Value = Math.Clamp(lv, 0, 2);
                LightSwitch.IsOn = lv != 0;
            }
            else
            {
                BrightValue.Text = "读取中…";
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void RenderEffectTag()
    {
        var name = Effects.FirstOrDefault(e => e.Code == _effect).Name ?? _effect.ToString();
        EffectTag.Text = "灯效 " + name;
    }

    /// <summary>联动开关的显示。真值只来自 `presets.backlight.sync`（写失败会拨回去）。</summary>
    private void RenderSync() => SyncSwitch.IsOn = _sync;

    /// <summary>独立灯珠色块：颜色是真值，边框表示色盘当前在改哪一路。</summary>
    private void RenderExtras()
    {
        foreach (var (name, button) in _extraButtons)
        {
            if (!_extraColors.TryGetValue(name, out var rgb) || rgb.Length < 3) continue;
            button.Background = Frozen(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
            button.BorderBrush = (Brush)FindResource(name == _activeExtra ? "AccBrush" : "LineBrush");
            button.ToolTip = $"{(name == "logo" ? "Logo 灯" : "尾灯")} #{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}"
                             + " · 点这里再用下方色盘改色";
        }
    }

    /// <summary>
    /// 左侧键盘上方那四颗档案按钮的显示（常驻，没有开关）：
    /// <b>●</b> 只标系统现在所在的三档之一；<b>◆ 自定义</b> 表示"三个系统档都用这一套"。
    /// 选中态标出正在编辑的档案 —— 和系统当前档可以不同（给没在用的那档调色就是这种场景）。
    /// </summary>
    private void RenderModes()
    {
        foreach (var (key, chip) in _modeChips)
        {
            var isCurrent = key != "custom" && key == _lastCurMode;
            var marker = key == "custom" ? (_customOverride ? "◆ " : "") : (isCurrent ? "● " : "");
            chip.Content = marker + BacklightModes.Label(key);
            chip.IsOn = key == _editMode;
            chip.ToolTip = key == "custom"
                ? (_customOverride
                    ? "正在接管：办公 / 游戏 / 狂暴三个系统档都用这一套（点任一系统档退出）"
                    : "点它 = 三个系统档都用这一套灯效（等同原厂 CustomOverride）")
                : isCurrent
                    ? $"系统现在就在「{BacklightModes.Label(key)}」档 —— 点它编辑这一档的灯效"
                    : $"编辑「{BacklightModes.Label(key)}」档的灯效（切到该档时才会用上）";
        }
    }

    /// <summary>
    /// 把**当前编辑这一档**的自动熄灭策略画到下拉上（`perf.modes[档].light`）。
    /// 这一档还没单独设过时显示「不自动熄灭」—— 默认就不该自己灭灯
    /// （2026-11-19 用户定），也不拿任何别的档 / 全局配置顶替。
    /// 配置里出现下拉没有的组合（手改 model.json，比如"闲置 7 分钟"）时**不硬塞一个最接近的档**：
    /// 清空选中并给 tooltip 让用户自己重选 —— 显示成"闲置 5 分钟"而实际跑 7 分钟，
    /// 是这一页最不该有的假信息。
    /// </summary>
    private void SyncAutoOff()
    {
        if (!_api.HasState) return;

        var (own, minutes, onLock) = _api.ModeAutoOff(_editMode);
        var want = onLock ? LockCode : minutes;
        var scope = own
            ? $"「{BacklightModes.Label(_editMode)}」档自己的设置"
            : $"「{BacklightModes.Label(_editMode)}」档还没设过：默认不自动熄灭";

        _autoOffSyncing = true;
        try
        {
            ComboBoxItem? match = null;
            foreach (var item in AutoOffCombo.Items.OfType<ComboBoxItem>())
                if (item.Tag is int code && code == want) { match = item; break; }

            if (match is not null)
            {
                AutoOffCombo.SelectedItem = match;
                AutoOffCombo.ToolTip = "不动键鼠就关掉背光，一有输入立刻恢复（不改亮度档位与配色）\n" + scope;
            }
            else
            {
                AutoOffCombo.SelectedIndex = -1;
                AutoOffCombo.ToolTip =
                    $"配置里是「闲置 {minutes} 分钟」+ 锁屏{(onLock ? "熄" : "不熄")}（不在选项里），从上面选一个即可覆盖\n" + scope;
            }
        }
        finally
        {
            _autoOffSyncing = false;
        }
    }

    /// <summary>
    /// 主题变了（由 <see cref="MainWindow"/> 统一转发，页面自己不订 <see cref="UiTheme.Changed"/>，
    /// 原因见 <see cref="DashView.ApplyTheme"/> 的注释）。
    /// </summary>
    public void ApplyTheme()
    {
        Keyboard.RefreshTheme();
        RenderZones();
    }

    // ================================================================ 交互

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CardButton { Tag: int index }) return;
        SelectZone(index);
    }

    /// <summary>
    /// 切"色盘接下来改哪一区"。**只改界面状态，不写 EC** —— 真正调色才写（见 <see cref="SetZoneColor"/>）。
    /// 两个入口共用：上方分区 tab、键盘示意图上点击分区（<see cref="KeyboardView.ZoneClicked"/>）。
    /// 顺带把色盘从独立灯珠（Logo / 尾灯）切回键盘分区 —— 用户点了键盘，意图已经很明显了。
    /// </summary>
    private void SelectZone(int index)
    {
        if (_zones.Length == 0) return;
        index = Math.Clamp(index, 0, _zones.Length - 1);
        if (_activeExtra is null && index == _active) return;

        _active = index;
        _activeExtra = null;
        RenderZones();
        RenderExtras();
    }

    /// <summary>点 Logo / 尾灯色块：色盘接下来改这一路，不写 EC（真正调色才写）。</summary>
    private void Extra_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || !_extraColors.ContainsKey(name)) return;
        _activeExtra = name;
        RenderZones();
        RenderExtras();
    }

    private void Effect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not CardButton { Tag: long code }) return;
        _effect = code;
        foreach (var (key, button) in _effButtons) button.IsOn = key == _effect;
        RenderEffectTag();
        _ = ApplyAsync($"灯效「{Effects.FirstOrDefault(x => x.Code == code).Name}」已下发", force: true);
    }

    private void Pure_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var match = _pures.FirstOrDefault(p => ReferenceEquals(p.Button, sender));
        if (match.Rgb is null) return;
        SetZoneColor(match.Rgb, commit: true);
    }

    private void OnWheelPicked(int r, int g, int b, bool commit)
        => SetZoneColor([r, g, b], commit);

    /// <summary>改「当前区」的颜色。commit=false 只改本地预览（拖动中），true 才写 EC。</summary>
    private void SetZoneColor(int[] rgb, bool commit)
    {
        if (_zones.Length == 0) return;

        // 色盘正在改独立灯珠：只动那一路，键盘三区表一个字节都不碰
        if (_activeExtra is { } extra)
        {
            _extraColors[extra] = [rgb[0], rgb[1], rgb[2]];
            RenderZones();
            RenderExtras();
            if (!commit || _busy) return;
            _ = ApplyAsync($"{(extra == "logo" ? "Logo 灯" : "尾灯")}颜色 {HexText.Text}");
            return;
        }

        if (_sync)
        {
            // 联动：拖色盘时**所有分区一起预览**（松手才写 EC），松手后一次三区连发。
            // 预览不同步的话，用户会以为只有当前区在变，直到松手才"跳"成全色。
            for (var i = 0; i < _zones.Length; i++) _zones[i] = [rgb[0], rgb[1], rgb[2]];
        }
        else
        {
            _zones[_active] = [rgb[0], rgb[1], rgb[2]];
        }

        RenderZones();
        if (!commit || _busy) return;
        // 联动关着时这是"只改一个区"—— EC 层会按颜色 diff 只发那一条，原厂就是这样直接变；
        // 联动开着时三个区都变了，一次三区连发。
        _ = ApplyAsync(_sync
            ? $"{_zones.Length} 个分区联动为 {HexText.Text}"
            : $"背光颜色 {HexText.Text}");
    }

    /// <summary>
    /// 切"编辑哪一套"：前三颗系统档**只载入配色，不写 EC**；
    /// 第四颗「自定义」就是接管开关 —— 点它 = 三个系统档都用这一套（原厂 CustomOverride）。
    /// ⚠️ 接管时重放的是 **custom 档案本身**，不拿当前灯色覆盖它（原厂点 Custom 的语义，见 SetCustomAsync）。
    /// 反过来点任一系统档 = 退出接管，后端立刻把当前系统档自己的灯效写回来。
    ///
    /// 与 CCLite 的差别：它点哪个档案按钮就当场写键盘；我们给前三档留了"只换编辑目标"，
    /// 没人要求的写一条都不发。
    /// </summary>
    private void ModeChip_Click(object sender, RoutedEventArgs e)
    {
        if (_customBusy || _busy) return;
        if (sender is not CardButton { Tag: string key }) return;

        if (key == "custom")
        {
            if (_customOverride && _editMode == "custom") return;
            _ = SetCustomAsync(true);
            return;
        }

        if (_customOverride)
        {
            // 退出接管是**业务动作**（要重放当前系统档），不能只改本地编辑目标
            _ = SetCustomAsync(false, editAfter: key);
            return;
        }

        if (key == _editMode) return;

        _editMode = key;
        LoadFromPresets();
        RenderZones();
        RenderModes();

        Toast(key == _lastCurMode
                ? $"正在编辑「{BacklightModes.Label(key)}」档（当前档）：改动会直接下发到键盘"
                : $"正在编辑「{BacklightModes.Label(key)}」档：改动会记进这一档，切到该档时才用上", true);
    }

    /// <summary>
    /// 接管开关的后端动作（由「自定义」按钮 / 点回系统档触发；写路径与按模式重放同源）。
    /// ⚠️ 原厂行为：点 Custom = 加载 Custom 自己的档案（出厂默认白/常亮/50%），
    /// **不是**把当前键盘灯色存进 Custom。后端只在 Custom 档案从来没建过时补一份原厂默认值，
    /// 绝不读当前配色播种。关闭时立刻回到当前系统档。
    /// </summary>
    /// <param name="want">true=接管（三档同一套）/ false=退出接管。</param>
    /// <param name="editAfter">退出接管后要把编辑目标放在哪一档；null=跟随当前系统档。</param>
    private async Task SetCustomAsync(bool want, string? editAfter = null)
    {
        _customBusy = true;
        _customOverride = want;              // 先按用户意图显示，失败再拨回去
        RenderModes();
        try
        {
            var result = await Task.Run(() => _api.Post("/api/backlight/custom-override",
                new JsonObject { ["on"] = want }));
            // 200 + ok:false 也是失败（EC 写不动 / 没读到系统档），别只认 HTTP 状态
            var innerOk = result.Raw?["ok"] is not JsonValue ov || !ov.TryGetValue<bool>(out var b) || b;
            if (!result.Ok || !innerOk)
            {
                var note = result.Raw?["note"] is JsonValue nv && nv.TryGetValue<string>(out var s) ? s : null;
                _customOverride = !want;
                RenderModes();
                Toast(!result.Ok ? result.Why("自定义接管没保存") : note ?? "「自定义」这一档没能切换", false);
                return;
            }

            _customOverride = want;
            _editMode = want
                ? "custom"
                : (editAfter ?? BacklightModes.Key(_api.PerfModeKey) ?? _editMode);
            LoadFromPresets();
            RenderZones();
            RenderModes();

            Toast(want
                ? "已接管：办公 / 游戏 / 狂暴三个系统档都用「自定义」档案这一套"
                : editAfter is not null
                    ? $"已退出接管：三档恢复各自灯效，正在编辑「{BacklightModes.Label(editAfter)}」档案"
                    : "已退出接管：切系统档恢复各档自己的灯效", true);
        }
        finally
        {
            _customBusy = false;
        }
    }

    private void Light_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _suppress) return;
        var want = !LightSwitch.IsOn;

        if (want)
        {
            // 非当前档时 _level 是这一档的记忆值，亮着就直接用它；关着才退回"关灯前那档"
            _level = _level > 0 ? _level : (_lastLevel > 0 ? _lastLevel : 2);
        }
        else
        {
            // 「关灯前记住的档位」只属于键盘上这一套：给别的档设开关不该污染它
            if (EditingLive) _lastLevel = _level > 0 ? _level : 2;
            _level = 0;
        }

        // 先按用户意图点亮/熄掉，写失败再由 RenderLevel(真值) 拨回来
        _suppress = true;
        LightSwitch.IsOn = want;
        _suppress = false;
        RenderLevel(_level);

        _ = ApplyAsync(want ? $"背光已打开（{LevelNames[_level]}）" : "背光已关闭", force: true);
    }

    private void Bright_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress) return;
        _level = (int)Math.Round(BrightSlider.Value);
        BrightValue.Text = _level switch { 0 => "关闭", 1 => "50%", _ => "100%" };
        LightSwitch.IsOn = _level != 0;
        if (EditingLive && _level > 0) _lastLevel = _level;

        // ⚠️ **立刻**占住"待确认"窗口，不能等 ApplyAsync（它 180ms 后才跑）。
        //    这 180ms 里只要来一拍快照（500ms 一拍），Update() 就会把 _level 改回 EC 里的旧档位，
        //    滑块自己跳回 100%，随后那次写又用被改过的 _level 发出去 —— 实测就是
        //    「拨到 50% 会回弹成 100%」，而且还是**有时候**（看快照是否正好落在窗口里）。
        //    占住之后 Update() 会拿 _levelWant 覆盖显示，且**不会**改 _level。
        _levelWant = _level;
        _levelWantAt = Environment.TickCount64;

        ScheduleBrightWrite();
    }

    /// <summary>
    /// 拖滑块时不要每一格都下发（一条背光命令 ~46ms 且 0.2s 节流），
    /// 停手 180ms 再写一次。用 DispatcherTimer 而不是 Task.Delay：它在 UI 线程上跑，
    /// 页面关掉后自然不再触发。
    /// </summary>
    private void ScheduleBrightWrite()
    {
        _brightTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _brightTimer.Stop();
        _brightTimer.Tick -= BrightTimer_Tick;
        _brightTimer.Tick += BrightTimer_Tick;
        _brightTimer.Start();
    }

    private void BrightTimer_Tick(object? sender, EventArgs e)
    {
        _brightTimer?.Stop();
        if (_busy)
        {
            // 上一次写还没回来：别把这一格的拖动丢掉，忙完再提交一次
            //（与关于页触发温度滑块的 debounce 同一个处理）。
            _brightTimer?.Start();
            return;
        }
        _ = ApplyAsync($"背光档位 {_level}", force: true);
    }

    /// <summary>
    /// 分区联动开关（原厂「神光同步」）。
    ///
    /// 打开 = 立刻把当前区的颜色铺到所有分区并下发一次，然后才把 `sync` 落盘 ——
    /// 顺序不能反：先落盘的话，EC 写失败会留下"配置说联动、灯还是各是各的"，
    /// 下次进页面开关是亮的、灯却不同色。写失败就拨回去并说明。
    /// 关闭 = 只落盘（灯保持现状，各区从这里开始各自独立）。
    /// </summary>
    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_syncBusy || _busy) return;

        var want = !_sync;
        _sync = want;
        RenderSync();

        if (want && _zones.Length > 0)
        {
            var rgb = _zones[_active];
            for (var i = 0; i < _zones.Length; i++) _zones[i] = [rgb[0], rgb[1], rgb[2]];
            RenderZones();
            _ = EnableSyncAsync();
            return;
        }

        _ = SaveSyncAsync(false, "已关闭分区联动：各分区颜色独立");
    }

    private async Task EnableSyncAsync()
    {
        _syncBusy = true;
        try
        {
            // 三区连发在 EC 层（SetBacklightZones 走写批），这里只是一次请求
            if (!await ApplyAsync($"{_zones.Length} 个分区已联动 {HexText.Text}", force: true))
            {
                _sync = false;
                RenderSync();
                return;
            }
            await SaveSyncAsync(true, $"分区联动已开启：以后调任一区，{_zones.Length} 个分区一起变");
        }
        finally
        {
            _syncBusy = false;
        }
    }

    /// <summary>
    /// 只把 `presets.backlight.sync` 落盘（按节合并写，不碰 zones / effect）。
    /// 失败一律把开关拨回真值 —— 停在用户点出来的样子等于告诉他"设置成功了"。
    /// </summary>
    private async Task SaveSyncAsync(bool on, string okMessage)
    {
        var body = new JsonObject
        {
            ["path"] = new JsonArray("backlight"),
            ["value"] = new JsonObject { ["sync"] = on },
        };

        var result = await Task.Run(() => _api.Post("/api/model/preset", body));
        if (result.Ok)
        {
            Toast(okMessage, true);
            return;
        }

        _sync = !on;
        RenderSync();
        Toast(result.Why("分区联动没保存"), false);
    }

    /// <summary>
    /// 自动熄灭下拉改了：写进**当前编辑这一档**的灯效档案（`perf.modes[档].light` 的两个键，
    /// 一次落盘）。四个 chip 各有自己的一份策略 —— 切到哪一档就按哪一档的来，
    /// 与配色档案完全同一套口径（2026-11-19 用户要求）。
    /// </summary>
    private void AutoOff_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_autoOffSyncing || _autoOffBusy) return;
        if (AutoOffCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int code) return;

        var onLock = code == LockCode;
        var minutes = code > 0 ? code : 0;

        var body = new JsonObject
        {
            ["mode"] = _editMode,
            ["minutes"] = minutes,
            ["on_lock"] = onLock,
        };

        var label = BacklightModes.Label(_editMode);
        var message = code switch
        {
            0 => $"已关闭「{label}」档的背光自动熄灭",
            LockCode => $"「{label}」档：锁屏 / 睡眠时熄背光，解锁后恢复",
            _ => $"「{label}」档：键鼠闲置 {minutes} 分钟后熄背光，一动就恢复",
        };
        _ = SaveAutoOffAsync(body, message);
    }

    /// <summary>
    /// 写某一档的自动熄灭策略（`/api/backlight/auto-off`）。
    /// 失败必须把下拉拨回真值 —— 停在用户点出来的样子等于告诉他"设置成功了"。
    /// </summary>
    private async Task SaveAutoOffAsync(JsonObject body, string okMessage)
    {
        _autoOffBusy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/backlight/auto-off", body));
            if (!result.Ok)
            {
                Toast(result.Why("自动熄灭没保存"), false);
                return;
            }

            // 改的是别的档 ⇒ 说明这次只记进那一档，现在的行为不变（免得用户以为立刻就生效了）
            var live = result.Raw?["live"] is JsonValue lv && lv.TryGetValue<bool>(out var b) && b;
            Toast(live ? okMessage : okMessage + "（切到该档才生效）", true);
        }
        finally
        {
            _autoOffBusy = false;
            SyncAutoOff();
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _zones = Enumerable.Repeat(new[] { 255, 255, 255 }, Math.Max(1, _zones.Length)).ToArray();
        foreach (var name in _extraColors.Keys.ToList()) _extraColors[name] = [255, 255, 255];
        _effect = 1;
        _level = 2;
        _lastLevel = 2;
        foreach (var (code, button) in _effButtons) button.IsOn = code == _effect;
        RenderZones();
        RenderExtras();
        RenderLevel(_level);
        RenderEffectTag();
        _ = ApplyAsync($"已恢复默认（{_zones.Length} 个分区白色"
                       + (_extraColors.Count > 0 ? " + 独立灯珠白" : "") + " · 常亮 · 100%）", force: true);
    }

    // ================================================================ 写入

    /// <summary>
    /// 下发一次背光。**所有分区必须一起发**（后端据此重建 `presets.backlight.zones`）。
    /// 走 <see cref="LocalApi.Post"/> → `UiActions` → `PostHandlers`：守卫、副作用（落 presets）、
    /// 403 与 200+ok:false 的语义全都只有一份。
    ///
    /// <paramref name="force"/> = 让 EC 层**全写**（不按颜色 diff 跳过）。用在档位/灯效变化、
    /// 恢复默认、全部同色、开启联动这些"要绝对对齐"的动作上；日常调色保持 false，
    /// 那时只发真正变了的区 —— 单区调色就一条命令，灯直接变，不会闪。
    ///
    /// 返回是否真的写成功 —— 「开启分区联动」要等它回来才落 sync（见 <see cref="EnableSyncAsync"/>）。
    /// </summary>
    private async Task<bool> ApplyAsync(string okMessage, bool force = false)
    {
        _busy = true;
        ResetButton.IsEnabled = false;
        try
        {
            // 记下这次下发的档位：快照在 SettleMs 内不拿旧值盖它。
            // 只有编辑当前档才真写 EC；别的档走 deferred，根本没有"待确认"可言。
            if (EditingLive)
            {
                _levelWant = _level;
                _levelWantAt = Environment.TickCount64;
            }

            // 有几个分区就发几个：后端按这份 zones 重建 presets，少发一个就会把那个区的
            // 颜色丢掉（以前固定发三个，1/2 区机型的地址会越界）。
            var addrs = _api.KbZoneAddrs;
            var zones = new JsonArray();
            for (var i = 0; i < _zones.Length; i++)
            {
                var rgb = _zones[i];
                zones.Add(new JsonObject
                {
                    ["addr"] = i < addrs.Length ? addrs[i] : addrs[^1],
                    ["rgb"] = new JsonArray(rgb[0], rgb[1], rgb[2]),
                });
            }

            var body = new JsonObject
            {
                ["level"] = _level,
                ["effect"] = _effect,
                ["zones"] = zones,
            };
            // 独立灯珠：改它自己时必带；灯效/亮度/恢复默认这类 force 操作也要带上，
            // 否则键盘换灯效了、独立灯珠还停在旧灯效上。
            if (_extraColors.Count > 0 && (force || _activeExtra is not null))
                foreach (var (name, rgb) in _extraColors)
                    body[name] = new JsonArray(rgb[0], rgb[1], rgb[2]);
            // 告诉后端这一笔算**哪一档**的改动：
            // 用户在办公档页面里调色、而系统正跑游戏档时，这笔要记进办公档的档案。
            if (_editMode is not null) body["mode"] = _editMode;
            // 非当前档：滑块摆的是这一档档案里记的亮度，用户拖它就是对这一档的明确设定。
            // 后端 deferred 路径默认忽略 level（防"用当前档亮度覆盖别的档"），这里必须显式声明。
            if (!EditingLive) body["level_explicit"] = true;
            if (force) body["force"] = true;

            var result = await Task.Run(() => _api.Post("/api/ec/backlight", body));

            if (result.Ok)
            {
                // 编辑的不是「键盘上现在这一档」⇒ 后端只落了档案、没写 EC（见 PostHandlers）。
                // 文案必须说清楚，否则用户会以为"点了没反应"（2026-11-19 报的正是"应该不变"）。
                if (result.Raw?["deferred"] is JsonValue dfv && dfv.TryGetValue<bool>(out var deferred) && deferred)
                {
                    _levelWant = null;               // 没写 EC，别再护着这个档位
                    var stored = result.Raw?["stored"] is JsonValue sv && sv.TryGetValue<string>(out var sk)
                        ? sk : _editMode;
                    Toast($"{okMessage} · 已记进「{BacklightModes.Label(stored)}」档"
                          + "（切到该档才生效，键盘现在不变）", true);
                    RenderEffectTag();
                    return true;
                }

                var dt3d = result.Raw?["dt3d"] is JsonValue dv && dv.TryGetValue<long>(out var d) ? d : (long?)null;
                Toast(dt3d is null ? okMessage : $"{okMessage}（DT3D={dt3d}）", true);
                RenderEffectTag();
                return true;
            }

            _levelWant = null;                       // 没写成功就别再护着这个值
            Toast(result.Why("背光写入失败"), false);
            return false;
        }
        finally
        {
            _busy = false;
            ResetButton.IsEnabled = true;
        }
    }

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);

    // ================================================================ 颜色换算

    private static (int R, int G, int B) HsvToRgb(double hue, double s, double v)
    {
        hue = (hue % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = v - c;
        var (r1, g1, b1) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (ToByte(r1 + m), ToByte(g1 + m), ToByte(b1 + m));
    }

    private static int ToByte(double v) => (int)Math.Clamp(Math.Round(v * 255), 0, 255);

    private static SolidColorBrush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
