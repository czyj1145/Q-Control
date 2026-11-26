using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using QControl.App.Http.Contract;
using QControl.App.Ui.Controls;
using QControl.Core.Text;

namespace QControl.App.Ui.Views;

/// <summary>
/// 系统页：存储 / 电池 / 运行状态 / 机型配置 四张卡。
///
/// 数据来源分两条，别混：
///   · 存储/电池/运行状态 = **每秒快照**（主窗口推 <see cref="Update"/>）；
///   · 机型配置四条滑块 = <see cref="LocalApi.FanState"/>（纯内存，可随取随用），
///     进页时拉一次、之后每 5 秒对齐一次。
///
/// 写口分两半（别"统一"）：
///   · 转速下限 / 迟滞 / 最小写入间隔 → <c>POST /api/model/preset</c>（presets.fan.*）；
///   · 温度保护墙 → <c>POST /api/fancurve/config</c>（与曲线画布上那条红竖带同一个值）。
///
/// 显示纪律：哪一段没数据就整段/整行不出现（<see cref="KvRow.Set(null)"/>），
/// 绝不摆 "--" 占位；「电量」是卡的主读数，恒显示。
/// </summary>
public partial class SysView : UserControl
{
    /// <summary>
    /// 四条滑块：key = 写入口的字段名；前三项走 model/preset，temp_wall 走 fancurve/config。
    /// <see cref="Hint"/> = 悬停说明，null = 不挂。
    /// </summary>
    private sealed record SliderSpec(
        string Key, string Label, string Unit, double Min, double Max, double Step, int Digits, bool ViaFanConfig,
        string? Hint = null);

    private static readonly SliderSpec[] Sliders =
    [
        new("min_duty", "转速下限", "%", 30, 60, 1, 0, false),
        new("hysteresis", "迟滞", "°C", 0.5, 8, 0.5, 1, false,
            "两个作用：① 曲线算出的转速与已写入的相差达到此值才真正写 EC，温度小幅抖动时不会让风扇反复微调；"
            + "② 摸到温度保护墙后，需回落到（墙 − 迟滞）以下才恢复曲线"),
        new("min_interval", "最小写入间隔", "s", 2, 20, 1, 0, false,
            "两次写入 EC 的最小间隔（秒）：转速变化再快，也最多每这么久才调一次，减少 EC 写入；"
            + "切性能档、重新接管、飓风等手动操作不受此限，立即生效"),
        new("temp_wall", "温度保护墙", "°C", 60, 100, 1, 0, true,
            "CPU / GPU 任一边达到即强制最高转速并告警，回落到（墙 − 迟滞）以下自动恢复曲线"),
    ];

    private readonly LocalApi _api;

    // 电池（电量恒显示；其余各项取不到整行隐藏）
    private readonly KvRow _batPct;
    private readonly KvRow _batRate;
    private readonly KvRow _batVolt;
    private readonly KvRow _batFull;
    private readonly KvRow _batDesign;
    private readonly KvRow _batHealth;
    private readonly KvRow _batRemain;

    // 运行状态
    private readonly KvRow _memPct;
    private readonly KvRow _vmem;
    private readonly KvRow _uptime;
    private readonly KvRow _netRate;
    private readonly KvRow _netRecv;
    private readonly KvRow _netSent;

    // 机型配置：每个滑块 = (盒子, 值文字, Slider, 规格)
    private readonly List<(Border Box, TextBlock Value, Slider Slider, SliderSpec Spec)> _cfgSliders = [];
    private readonly DispatcherTimer _cfgDebounce;

    /// <summary>
    /// 待落盘的滑块，按触发顺序去重。
    /// ⚠️ 必须是**队列**不是单槽：单槽时 300ms 内连着拖两条滑块，
    ///    后一条直接把前一条顶掉 ⇒ 只有最后一条真的写了。
    ///    同一条再动就先摘出来再排到队尾，值取写那一刻的 <c>Slider.Value</c>（= 最终态）。
    /// </summary>
    private readonly List<SliderSpec> _cfgPending = [];
    private bool _cfgBusy;

    private FanCurveStateDto? _fan;
    private int _tick;
    private bool _cfgLoaded;

    /// <summary>
    /// 真值回写滑块期间置 true。
    /// ⚠️ 少了这个闸门，程序给 Slider.Value 赋值同样会引发 ValueChanged，被当成"用户拖了滑块"
    ///    ⇒ 进页那一次（60→95 之类）会静默 POST 一遍默认值并弹「温度保护墙：95 °C」。
    ///    照 LightView._suppress。
    /// </summary>
    private bool _cfgSyncing;

    /// <summary>存储卡上一次渲染时的内容签名（变了才重建可视树，见 <see cref="RenderDrives"/>）。</summary>
    private string? _driveSig;

    internal SysView(LocalApi api)
    {
        _api = api;
        InitializeComponent();

        UiKit.FillViewport(Scroll, Root);
        UiKit.ApplyCardGap(CardGrid);

        // ---------------- 电池 ----------------
        _batPct = UiKit.MakeKv(BatKv, "电量");
        _batRate = UiKit.MakeKv(BatKv, "电池功率");
        _batVolt = UiKit.MakeKv(BatKv, "电压");
        _batFull = UiKit.MakeKv(BatKv, "满充容量");
        _batDesign = UiKit.MakeKv(BatKv, "设计容量");
        _batHealth = UiKit.MakeKv(BatKv, "健康度");
        _batRemain = UiKit.MakeKv(BatKv, "剩余容量");

        // ---------------- 运行状态 ----------------
        _memPct = UiKit.MakeKv(LiveKv, "内存");
        _vmem = UiKit.MakeKv(LiveKv, "虚拟内存");
        _uptime = UiKit.MakeKv(LiveKv, "开机时长");
        _netRate = UiKit.MakeKv(LiveKv, "网络速率");
        _netRecv = UiKit.MakeKv(LiveKv, "累计接收");
        _netSent = UiKit.MakeKv(LiveKv, "累计发送");

        // ⚠️ 不再用 EnableSpaceEvenly 把行铺开（系统页向总览页看齐）：
        //    运行状态卡 / 电池卡的行距改为和总览页处理器卡一致的紧凑固定 8px（KvRow 样式自带
        //    底部 Margin），整组在卡片内居中。半屏高的卡片会让数据组落在正中、上下留白变多，
        //    这是该选择的已知代价。
        BuildSliders();

        // 拖滑块时不要每一格都落盘（model.json 一次完整写盘），停手 300ms 再写
        _cfgDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _cfgDebounce.Tick += CfgDebounce_Tick;

        Loaded += (_, _) =>
        {
            LoadModelTag();
            LoadFanConfig();
        };
    }

    // ================================================================ 构建

    /// <summary>
    /// 四条滑块各成一个盒子：标签一行、滑块独占下一行铺满整宽。
    /// 2×2 排布（固定两列：半幅宽下 auto-fit 会排出难看的 3+1）。
    /// </summary>
    private void BuildSliders()
    {
        for (var i = 0; i < Sliders.Length; i++)
        {
            var spec = Sliders[i];
            // ⚠️ 调色板画笔（TxtBrush/DimBrush…）**不能**在构造函数里 FindResource：
            //     UserControl 是 new 出来再塞进窗口的，那一刻没有父级，查不到窗口 Resources
            //     里的运行时键（DETAILS 里那条老坑）。用 SetResourceReference 挂动态引用，
            //     等页面进树后再解析 —— Theme.xaml 里的样式反而可以直接 FindResource。
            var value = new TextBlock
            {
                Text = "读取中…",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TxtBrush");
            // 等宽数字：每秒对齐真值时数字宽度不跳，行才不会抖
            System.Windows.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);

            var box = new Border { Style = (Style)FindResource("CfgSliderBox") };
            var stack = new StackPanel();
            var head = new Grid();
            var headLabel = new TextBlock
            {
                Text = spec.Label,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            headLabel.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            head.Children.Add(headLabel);
            head.Children.Add(value);
            stack.Children.Add(head);

            var slider = new Slider
            {
                Style = (Style)FindResource("RangeSlider"),
                Minimum = spec.Min,
                Maximum = spec.Max,
                SmallChange = spec.Step,
                LargeChange = spec.Step,
                TickFrequency = spec.Step,
                IsSnapToTickEnabled = true,
                IsMoveToPointEnabled = true,      // 点轨道直接跳
                Margin = new Thickness(0, 7, 0, 0),
                Tag = spec,
            };
            slider.ValueChanged += CfgSlider_ValueChanged;
            stack.Children.Add(slider);
            box.Child = stack;

            Grid.SetColumn(box, i % 2);
            Grid.SetRow(box, i / 2);
            box.Margin = i % 2 == 0 ? new Thickness(0, 0, 6, 12) : new Thickness(6, 0, 0, 12);
            if (i >= 2) box.Margin = i % 2 == 0 ? new Thickness(0, 0, 6, 0) : new Thickness(6, 0, 0, 0);
            SliderGrid.Children.Add(box);
            _cfgSliders.Add((box, value, slider, spec));

            // 悬停说明：Hint 非空才挂。
            // 挂 box = 整块可悬停；head 再挂一份，标签行单独悬停也触发。
            if (spec.Hint is { } hint)
            {
                box.ToolTip = hint;
                head.ToolTip = hint;
            }
        }
    }

    // ================================================================ 每秒刷新

    /// <summary>
    /// 重新对齐这一页的"一次性数据"（重复点击侧栏「系统」时由 MainWindow 调）：
    /// 机型配置（卡头 tag + 四条滑块）改过 model.json 之后靠它回填，不用切页再切回来。
    /// 滑块回写有 <see cref="_cfgSyncing"/> 闸着，不会顺手写一遍盘。
    /// </summary>
    public void Refresh()
    {
        LoadModelTag();
        LoadFanConfig();
    }

    /// <summary>主窗口每秒推一份快照进来：存储 / 电池 / 运行状态三张卡。</summary>
    public void Update(StatusDto? status)
    {
        // 机型配置每 5 秒对齐一次
        if (++_tick % 5 == 0) LoadFanConfig();

        if (status is null)
        {
            DiskTag.Text = "读取中…";
            AcTag.Text = "读取中…";
            _batPct.SetAlways("读取中…");
            _batRate.Set(null);
            _batVolt.Set(null);
            _batFull.Set(null);
            _batRemain.Set(null);
            _memPct.SetAlways("读取中…");
            _vmem.Set(null);
            _uptime.Set(null);
            _netRate.SetAlways("读取中…");
            _netRecv.Set(null);
            _netSent.Set(null);
            DrivesHost.Children.Clear();
            return;
        }

        RenderDrives(status.DiskInfo ?? []);
        RenderBattery(status);
        RenderLive(status);
        RenderWritable(status);
    }

    /// <summary>
    /// 这台机器到底能不能写 EC —— 决定「可写/只读」徽章和四条滑块能不能点。
    ///
    /// 为什么需要（2026-11-02 用 `--device quanta-gmwmi-generic` 预览才看见）：
    /// 未适配机型上那四条滑块照旧能拖、徽章还写着「可写」，而写入口其实关着 ——
    /// 拖完什么都不会发生（曲线连温度都读不到）。这是最典型的"骗人的界面"。
    /// ⚠️ 只改可视状态，不改值：配置本身照旧由 model.json 决定。
    /// </summary>
    private void RenderWritable(StatusDto status)
    {
        var writable = status.Device?.WritesAllowed == true;
        if (RwTagText is not null)
        {
            RwTagText.Text = writable ? "可写" : "只读";
            RwTagHost.BorderBrush = UiTheme.PaletteBrush(writable ? "AccBrush" : "LineBrush")
                                    ?? RwTagHost.BorderBrush;
            RwTagHost.ToolTip = writable
                ? "当前机型已标定，改这几项会真的写进 EC / 配置"
                : "当前机型未标定 EC 写权限：这几项改了也不会下发（见 docs/DEVICES.md）";
        }

        if (SliderGrid is not null)
        {
            SliderGrid.IsEnabled = writable;
            // WPF 的 Grid 不会自己变灰（只有模板化的控件才会）⇒ 显式压一档透明度，
            // 让人一眼看出"这些不是能拖的东西"。不是装饰动效，是状态。
            SliderGrid.Opacity = writable ? 1.0 : 0.55;
            SliderGrid.ToolTip = writable
                ? null
                : "这台机器还没有标定 EC 写入：参数可以看，但不会生效";
        }
    }

    // ---------------------------------------------------------------- 存储

    /// <summary>
    /// 物理硬盘档案（快照的 <c>disk_info</c>）。一块盘最多五行：型号+容量 ／
    /// 介质·总线·健康·卷 ／ 温度·剩余寿命 ／ 累计读写 ／ 固件·序列号。
    /// 哪段没数据就哪段不出现；一块都拼不出来时整块隐藏（卡头 tag 报「未读到」）。
    /// </summary>
    private void RenderDrives(List<StatusDiskInfoItemDto>? drives)
    {
        var list = drives ?? [];

        // 卡头 tag：一块盘报容量、多块盘报「N 块 · 总容量」
        long total = 0;
        foreach (var d in list) total += d.SizeGb ?? 0;
        DiskTag.Text = list.Count == 0 ? "未读到"
            : list.Count == 1 ? $"{Fmt(list[0].SizeGb)} GB"
            : $"{list.Count} 块 · {Fmt(total)} GB";

        // ⚠️ 磁盘档案是**静态**的（插拔才会变），但这段挂在每秒 tick 上。
        //    以前每次都 Clear + 重建整块可视树 —— 系统页只要开着，每秒就要重排
        //    十几行文本，纯属白烧 CPU。签名没变就直接返回。
        var sig = string.Join("|", list.Select(d => string.Join("~",
            d.Label, d.Model, d.SizeGb, d.Bus, d.Media, d.Health, d.Temp,
            d.Life, d.WritesGb, d.ReadsGb, d.Firmware, d.Serial,
            d.Volumes is { Count: > 0 } v ? string.Join(" ", v) : "")));
        if (sig == _driveSig) return;
        _driveSig = sig;

        DrivesHost.Children.Clear();
        foreach (var d in list)
        {
            var box = new Border { Style = (Style)FindResource("DriveBox") };
            var stack = new StackPanel();

            // 头一行：型号（可截断） + 容量
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", new[] { d.Label, d.Model }.Where(x => !string.IsNullOrEmpty(x))),
                Style = (Style)FindResource("DriveHead"),
            });
            head.Children.Add(new TextBlock
            {
                Text = d.SizeGb is { } gb ? $"{Fmt(gb)} GB" : "",
                Style = (Style)FindResource("DriveSize"),
            });
            Grid.SetColumn((UIElement)head.Children[1], 1);
            stack.Children.Add(head);

            // ② 介质 · 总线 · 健康 · 卷（健康不是"健康"时整行标黄）
            var ident = string.Join(" · ", new[] { d.Bus, d.Media }.Where(x => !string.IsNullOrEmpty(x)));
            var segs = new List<(string, string)>();
            if (ident.Length > 0) segs.Add(("", ident));
            if (!string.IsNullOrEmpty(d.Health)) segs.Add(("", d.Health!));
            if (d.Volumes is { Count: > 0 }) segs.Add(("", string.Join(" ", d.Volumes)));
            if (segs.Count > 0)
                stack.Children.Add(MetaLine(segs, hot: d.Health is { } h && h != "健康"));

            // ③ 温度 / 剩余寿命
            var hot3 = new List<(string, string)>();
            if (d.Temp is { } t) hot3.Add(("温度", Fmt(t, 0) + " °C"));
            if (d.Life is { } l) hot3.Add(("剩余寿命", Fmt(l, 0) + "%"));
            if (hot3.Count > 0)
                stack.Children.Add(MetaLine(hot3));

            // ④ 累计读写
            var io = new List<(string, string)>();
            if (d.WritesGb is { } w) io.Add(("累计写入", Bytes(w)));
            if (d.ReadsGb is { } r) io.Add(("累计读取", Bytes(r)));
            if (io.Count > 0)
                stack.Children.Add(MetaLine(io));

            // ⑤ 固件 / 序列号（报障时用户要抄的正是这两个编号）
            var ids = new List<(string, string)>();
            if (!string.IsNullOrEmpty(d.Firmware)) ids.Add(("固件", d.Firmware!));
            if (!string.IsNullOrEmpty(d.Serial)) ids.Add(("序列号", d.Serial!));
            if (ids.Count > 0)
            {
                stack.Children.Add(MetaLine(ids));
                box.ToolTip = string.Join(" · ", ids.Select(x => x.Item1 + " " + x.Item2));
            }

            box.Child = stack;
            DrivesHost.Children.Add(box);
        }
    }

    /// <summary>
    /// 盘上的暗字行：标签暗字 + 值亮粗（<c>&lt;b&gt;</c>）交替。
    /// <paramref name="segments"/> 每项 = (标签, 值)；只有标签时值留空。
    /// 健康异常时整行转警戒色。
    /// </summary>
    private TextBlock MetaLine(IEnumerable<(string Label, string Value)> segments, bool hot = false)
    {
        var block = new TextBlock { Style = (Style)FindResource("DriveMeta") };
        var first = true;
        foreach (var (label, value) in segments)
        {
            if (!first)
            {
                var sep = new Run(" · ");
                sep.SetResourceReference(TextElement.ForegroundProperty, "LineBrush");
                block.Inlines.Add(sep);
            }
            first = false;
            block.Inlines.Add(new Run(label));
            if (value.Length > 0)
            {
                var val = new Run(" " + value) { FontWeight = FontWeights.SemiBold };
                // ⚠️ 必须是 SetResourceReference（= DynamicResource），不能用
                //    UiTheme.PaletteBrush("TxtBrush") 抓实例：调色板画笔是**每次换主题新冻一个**，
                //    抓下来的实例永远停在当时的主题上。而这片盘的行有内容签名去重
                //    （见 RenderDrives 的 _driveSig），切主题**不会**重建 ⇒ 暗色切白天后
                //    这一堆"值"还是近白的 #e6ebf5，压在 #eef2f9 的盘盒上 = 整行字凭空消失。
                val.SetResourceReference(TextElement.ForegroundProperty, "TxtBrush");
                block.Inlines.Add(val);
            }
        }
        if (hot) block.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        return block;
    }

    private static string Bytes(double gb)
        => gb >= 1024 ? $"{Fmt(gb / 1024, 1)} TB" : $"{Fmt(gb)} GB";

    // ---------------------------------------------------------------- 电池

    private void RenderBattery(StatusDto s)
    {
        var bat = s.Battery;
        var rate = s.Hwmon?.Battery?.RateW;

        // 卡头 tag = 充电状态：优先按充放电功率判，没有才按"接不接电源 + 电量"推。
        // ⚠️ 插着电但没充时必须说「接通电源」，不能硬说"充电中"（保养阈值限住时 rate≈0）。
        var ac = "--";
        if (bat?.Present == true)
        {
            if (rate is > 0.5) ac = "充电中";
            else if (rate is < -0.5) ac = "放电中";
            else if (bat.Plugged != true) ac = "放电中";
            else ac = (bat.Percent ?? 0) >= 99 ? "已充满" : "接通电源";
        }
        AcTag.Text = ac;

        _batPct.SetAlways(bat?.Present == true ? $"{Fmt(bat.Percent)}%" : "--");

        // 充放电功率：不充不报 0 ⇒ 只在充电/放电时才出现（标签也跟着变）
        if (rate is { } w && Math.Abs(w) >= 0.5)
            _batRate.SetAlways((w > 0 ? "充电功率 " : "放电功率 ") + $"{Fmt(Math.Abs(w), 1)} W");
        else _batRate.Set(null);

        _batVolt.Set(bat?.VoltageV is { } v ? $"{Fmt(v, 2)} V" : null);
        _batFull.Set(bat?.FullWh is { } f ? $"{Fmt(f, 1)} Wh" : null);
        _batDesign.Set(bat?.DesignWh is { } d ? $"{Fmt(d, 1)} Wh" : null);
        // 健康度 = 满充 ÷ 设计，**后端已封顶 100**（设计容量是厂商的保底标称值，
        // 新电池实际满充常略大 ⇒ 原始比值会到 101%，摆出来只会被当成算错）。
        // 两个值缺一个后端就不给这个键 ⇒ 整行隐藏。
        // 整数不补小数位：100% 而不是 100.0%（后者像"算出来差一点"）。
        _batHealth.Set(bat?.HealthPct is { } h
            ? Fmt(h, h == Math.Floor(h) ? 0 : 1) + "%"
            : null);
        _batRemain.Set(bat?.RemainWh is { } r ? $"{Fmt(r, 1)} Wh" : null);
    }

    // ---------------------------------------------------------------- 运行状态

    private void RenderLive(StatusDto s)
    {
        var mem = s.Memory;
        var net = s.Network;
        var vmem = s.Hwmon?.Vmem;

        _memPct.SetAlways(mem?.Percent is { } p && mem.Used is { } u && mem.Total is { } t
            ? $"{Fmt(p)}% ({Fmt(u / 1073741824.0, 1)}/{Fmt(t / 1073741824.0, 1)} GB)"
            : "--");

        _vmem.Set(vmem?.Percent is { } vp
            ? $"{Fmt(vp, 0)}%" + (vmem.UsedGb is { } ug ? $"（{Fmt(ug, 1)} GB）" : "")
            : null);

        _uptime.Set(Humanize.UptimeCompact(s.System?.UptimeS));

        var rate = net?.Rate ?? 0;
        // 整数显示（不出现小数点）：实时网速跳得勤，位数越少越好读；口径见 Humanize.RateInteger
        _netRate.SetAlways(Humanize.RateInteger(rate));
        _netRecv.Set(Humanize.TrafficMb(net?.RecvMb));
        _netSent.Set(Humanize.TrafficMb(net?.SentMb));
    }

    // ================================================================ 机型配置

    /// <summary>卡头 tag：解析失败必须明说（后端此时保住上一份配置且拒绝写盘）。</summary>
    private void LoadModelTag()
    {
        ModelTag.Text = _api.ModelLoadError is { } err
            ? "model.json 解析失败"
            : _api.ModelSource == "registry" ? "原厂值已探测" : "使用默认配置";
        if (_api.ModelLoadError is not null)
            ModelTag.ToolTip = _api.ModelLoadError;
    }

    private void LoadFanConfig()
    {
        if (_cfgBusy) return;

        // ⚠️ 队列里还有没落盘的滑块（松手后那 300ms 防抖窗口内）⇒ 这一拍**不回写**。
        //    2026-11-03 修：5 秒一次的定时对齐只要落在这个窗口里，就会把用户刚拖到的值改回
        //    后端的旧值；而下发时取的是 `Slider.Value`（见 CfgDebounce_Tick），
        //    于是「改回旧值 → 写旧值 → toast 说已保存」——用户的改动被悄悄吃掉，还不报错。
        //    队列一清（防抖那一批写完）就会在 finally 里重新对齐，所以这里跳过不会留下脏显示。
        if (_cfgPending.Count > 0) return;

        var fan = _api.FanState();
        if (fan is null) return;
        _fan = fan;
        if (!_cfgLoaded) _cfgLoaded = true;

        // ⚠️ 回写期间必须挂起 ValueChanged（见 _cfgSyncing 的说明）：
        //    这段赋值和用户拖滑块走的是同一个事件，不闸住就等于"进页即写一遍"。
        _cfgSyncing = true;
        try
        {
            foreach (var (_, value, slider, spec) in _cfgSliders)
            {
                var now = spec.Key switch
                {
                    "min_duty" => fan.MinDuty,
                    "hysteresis" => fan.Hysteresis,
                    "min_interval" => fan.MinInterval,
                    "temp_wall" => fan.TempWall,
                    _ => null,
                };
                if (now is not { } v) continue;

                // ⚠️ 数值必须带单位（% / °C / s）—— 值后面紧跟 " " + spec.Unit。
                //    漏拼 spec.Unit 会让"运行配置"四条滑块只剩裸数字、读不懂。
                value.Text = v.ToString(spec.Digits == 0 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + spec.Unit;
                // 手指还按在滑块上时别回写（否则拖到一半被拽回去）
                if (!slider.IsMouseCaptureWithin && Math.Abs(slider.Value - v) > 0.001)
                    slider.Value = spec.Key == "hysteresis" ? Math.Round(v * 2) / 2 : Math.Round(v);
            }
        }
        finally
        {
            _cfgSyncing = false;
        }
    }

    private void CfgSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_cfgSyncing) return;                       // 真值回写，不是用户操作
        if (sender is not Slider { Tag: SliderSpec spec } slider) return;
        if (_cfgSliders.Count == 0) return;
        var entry = _cfgSliders.First(x => ReferenceEquals(x.Slider, sender));

        // 拖动中只改数字（即时反馈），写由防抖触发
        entry.Value.Text = slider.Value.ToString(spec.Digits == 0 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + spec.Unit;
        _cfgPending.Remove(spec);      // 已在队里就挪到队尾 —— 值取写那一刻的 Slider.Value
        _cfgPending.Add(spec);
        _cfgDebounce.Stop();
        _cfgDebounce.Start();
    }

    private async void CfgDebounce_Tick(object? sender, EventArgs e)
    {
        _cfgDebounce.Stop();
        if (_cfgPending.Count == 0) return;
        // 上一批还在写：队列留着，过一会儿再来（直接 return 会把这批丢在队列里没人认领）
        if (_cfgBusy)
        {
            _cfgDebounce.Start();
            return;
        }

        var batch = _cfgPending.ToList();
        _cfgPending.Clear();

        _cfgBusy = true;
        try
        {
            var results = new List<(bool Ok, string Text, string Label)>();
            foreach (var spec in batch)
            {
                var entry = _cfgSliders.First(x => x.Spec.Key == spec.Key);
                var value = entry.Slider.Value;
                var shown = value.ToString(spec.Digits == 0 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + spec.Unit;

                JsonObject body;
                if (spec.ViaFanConfig)
                {
                    body = new JsonObject { ["temp_wall"] = value };
                }
                else
                {
                    body = new JsonObject
                    {
                        ["path"] = new JsonArray("fan", spec.Key),
                        ["value"] = value,
                    };
                }

                var result = await Task.Run(() => _api.Post(
                    spec.ViaFanConfig ? "/api/fancurve/config" : "/api/model/preset", body));
                results.Add((result.Ok,
                    result.Ok ? $"{spec.Label}：{shown}" : result.Why($"{spec.Label}保存失败"),
                    spec.Label));
            }

            // ⚠️ MainWindow 的 Toast 是**单条覆盖式**（同一个 Border，后一条直接盖掉前一条）：
            //    多条时逐条弹只有最后一条看得见，用户会以为前面那几条没存上 ⇒ 汇总成一条。
            //    单条保持原样，不动已经对的文案。
            if (results.Count == 1) Toast(results[0].Text, results[0].Ok);
            else if (results.All(r => r.Ok))
                Toast($"已保存 {results.Count} 项：" + string.Join("、", results.Select(r => r.Label)), true);
            else Toast(results.First(r => !r.Ok).Text, false);
        }
        finally
        {
            _cfgBusy = false;
            LoadFanConfig();      // 无论成败都回到真值（队列已清空，写期间新拖的会再排一次防抖）
        }
    }

    // ================================================================ 两个按钮

    /// <summary>恢复出厂：清空 presets 里改过的所有数值 + 重新探测基准。不可逆，先问。</summary>
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var go = await UiDialog.ConfirmAsync(this, "恢复出厂默认配置？",
            "会清空你在 presets 里改过的所有数值，并重新探测出厂基准。\n\n"
            + "风扇会恢复成真正的出厂状态：参数回默认，三档曲线从原厂 UserFan1/2/3 读回"
            + "（只读原厂配置，不让原厂接管）；EC 里已经写过的转速不受影响。",
            "恢复出厂", "取消", danger: true);
        if (!go) return;

        ResetButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/model/reset", new JsonObject()));
            if (!result.Ok)
            {
                Toast(result.Why("恢复失败"), result.Ok);
                return;
            }
            var oem = result.Raw?["oem_curves"]?.AsObject();
            var n = oem?["written"]?.AsArray().Count ?? 0;
            Toast(oem?["ok"]?.GetValue<bool>() == true
                ? $"已恢复出厂配置，风扇三档曲线按原厂导入 {n} 档"
                : "已恢复出厂配置（风扇三档曲线未能从原厂读取）", true);
        }
        finally
        {
            ResetButton.IsEnabled = true;
            LoadModelTag();
            LoadFanConfig();
        }
    }

    /// <summary>
    /// EC 写入自检：短暂改一次键盘背光档位再读回，判断写入到底生不生效。
    /// 说明放在点击时的确认框里（卡片内不留提示小字）。
    /// </summary>
    private async void SelfTest_Click(object sender, RoutedEventArgs e)
    {
        var go = await UiDialog.ConfirmAsync(this, "EC 写入自检",
            "EC 写入自检会短暂改变键盘背光档位（随后自动还原）。\n\n"
            + "用途：判断 EC 写入到底有没有生效。\n确认继续？",
            "开始自检", "取消");
        if (!go) return;

        SelfTestButton.IsEnabled = false;
        SelfTestButton.Content = "自检中…";
        try
        {
            var result = await Task.Run(() => _api.Post("/api/ec/selftest", new JsonObject()));
            var raw = result.Raw;
            if (result.Ok && raw is not null)
            {
                var effective = raw["effective"] is JsonValue ev && ev.TryGetValue(out bool eb) && eb;
                var before = raw["before"]?.ToString();
                var wrote = raw["wrote"]?.ToString();
                var readback = raw["readback"]?.ToString();
                if (effective)
                    Toast($"写入生效 ✓ 档位 {before}→{wrote}，读回 {readback}，已还原", true);
                else
                {
                    Toast($"写入未生效 ✗ 写到 {wrote}，读回仍是 {readback}", false);
                    if (raw["note"] is JsonValue nv && nv.TryGetValue(out string? note) && !string.IsNullOrEmpty(note))
                        _ = DelayedToast(note);
                }
            }
            else
            {
                Toast(result.Why("自检失败"), false);
            }
        }
        finally
        {
            SelfTestButton.IsEnabled = true;
            SelfTestButton.Content = "EC 写入自检";
        }
    }

    /// <summary>第二条提示隔 2800ms 再弹 —— 两条都要看得见。</summary>
    private async Task DelayedToast(string text)
    {
        await Task.Delay(2800);
        Toast(text, false);
    }

    // ================================================================ 小工具

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);

    /// <summary>数值格式：F0 = 四舍五入到整数，digits&gt;0 时给定位小数。</summary>
    private static string Fmt(double? v, int digits = 0)
        => v is double d ? d.ToString(digits == 0 ? "F0" : "F" + digits, CultureInfo.InvariantCulture) : "--";
}
