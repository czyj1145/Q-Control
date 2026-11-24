using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using QControl.App.Http.Contract;
using QControl.App.Ui.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace QControl.App.Ui.Views;

/// <summary>
/// 硬件页：一张卡的扁平规格表，8 组静态档案。
///
/// 数据是**开机采一次**的缓存（八组 WMI 并行 + 显卡走 NVML，实测 ~120ms）：
/// 后端在首个快照就绪后就会预采一次（`AppState.WarmupHardware`），所以正常进页是 **0 毫秒**；
/// 万一还没采完，界面显示「正在采集…」并每 250 毫秒自己再看一眼。
///
/// 别"简化"的几条：
///   · 显示顺序走 <see cref="HwOrder"/>（不是后端采集顺序）；没写的分组排最后、保持原顺序；
///   · 「系统」组有 5 行被**提到顶部横幅**（一条信息只出现一次），其中「系统内部版本」
///     在横幅里改名「版本号」且不随进阶折叠；
///   · 主型号默认取第一条普通行，内存组指定取「内存条 1」（第一条是总容量，读起来不明所以）；
///   · 整组只剩进阶内容时，折叠态下整组收起（不摆空组）。
/// </summary>
public partial class HardwareView : UserControl
{
    // ---------------------------------------------------------------- 版式常量

    /// <summary>分组 id → 图标（不完全同名：sys→主板用板卡图标、mem→内存条、display→屏幕）。</summary>
    private static readonly Dictionary<string, Geometry> HwIcons = new(StringComparer.Ordinal)
    {
        ["sys"] = Icons.Hardware,
        ["cpu"] = Icons.Cpu,
        ["gpu"] = Icons.Gpu,
        ["mem"] = Icons.Ram,
        ["disk"] = Icons.Disk,
        ["display"] = Icons.Monitor,
        ["net"] = Icons.Net,
        ["ec"] = Icons.Ec,
    };

    /// <summary>徽标配色：深浅两套主题共用同一组实心色（色块里本来就是白图标）。</summary>
    private static readonly Dictionary<string, Color> HwColors = new(StringComparer.Ordinal)
    {
        ["sys"] = C("#7c86ff"), ["cpu"] = C("#3d8bff"), ["gpu"] = C("#a855f7"),
        ["mem"] = C("#22c1c3"), ["disk"] = C("#34c76a"), ["display"] = C("#8e96a6"),
        ["net"] = C("#f2a12b"), ["ec"] = C("#ef5b4c"),
    };

    /// <summary>显示顺序（照游戏加加）：处理器 → 显卡 → 主板 → 内存 → 存储 → 屏幕 → 网络 → EC 实测。</summary>
    private static readonly string[] HwOrder = ["cpu", "gpu", "sys", "mem", "disk", "display", "net", "ec"];

    /// <summary>顶部横幅要从 sys 组提上来的行（提走后该组不再重复显示）。</summary>
    private static readonly string[] OsKeys = ["系统", "系统内部版本", "计算机名", "系统架构", "安装日期"];

    /// <summary>
    /// 「徽标 + 组名」那一列的宽度，在 1320 视口下的实测值（211）。
    /// </summary>
    private const double GroupColWidth = 211;

    /// <summary>哪一组用哪条行当「主型号」（默认第一条普通行）。</summary>
    private static readonly Dictionary<string, string> HwHeroKey = new(StringComparer.Ordinal)
    {
        ["mem"] = "内存条 1",
    };

    private sealed record HwRow(string K, string V, string? Sub, bool Mono, bool Adv);

    private sealed record HwGroup(string Id, string Name, List<HwRow> Rows);

    private readonly LocalApi _api;
    private readonly DispatcherTimer _poll;

    /// <summary>
    /// busy 轮询间隔。采集本身只有 ~120 ms（八组并行 + 显卡走 NVML），原来 1.5 秒一跳，
    /// 采集 100 ms 就完了用户还得盯着「正在采集…」满一秒半 —— 体感上的"读取慢"
    /// 主要来自这里，不是采集。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>等一次采集的上限（250 ms × 40 = 10 秒）：超时就恢复按钮、提示重试。</summary>
    private const int PollLimit = 40;

    private List<HwGroup> _groups = [];
    private double _at;
    private long _tookMs;
    /// <summary>正在等一次采集出结果（首次进页 / 手动重新采集）。</summary>
    private bool _waiting;
    /// <summary>
    /// 发起等待那一刻的 `at`。手动刷新的完成判据是**"时间戳变了"**，不是"busy 变 false" ——
    /// `POST /api/hardware/refresh` 是丢到后台线程的，紧接着那次 GET 读到的仍是旧帧 + busy:false，
    /// 只看 busy 会立刻判定"已完成"，界面停在旧数据上再也不更新（2026-11-08 修的 bug）。
    /// </summary>
    private double _awaitAt;
    private int _polls;
    private bool _advOn;
    private bool _failed;

    internal HardwareView(LocalApi api)
    {
        _api = api;
        InitializeComponent();

        UiKit.FillViewport(Scroll, Root);
        // 组与组之间 space-evenly：
        // 卡片被页框撑高后多出来的高度**均摊到组距**上，不是让某一组变胖、更不是全堆到
        // 卡片底部（上限给太小就是这样：上面紧凑、下面一大片空白）。
        //   · 上限 40：1320×880 下这个页框的实测余量约 25/组；40 让窗口再高一点也能摊满，
        //     同时不至于把 7 组拉成稀疏清单；
        //   · 下限 6：展开进阶信息后组变多、内容超高时，宁可让页框滚动也不把组挤到一起。
        UiKit.EnableSpaceEvenly(GroupsHost, minGap: 6, maxGap: 40);

        // 展开态跨会话记住（`presets.ui.hw_adv_open`，走通用配置项）。
        // 没写过 = 折叠，与首次打开一致
        _advOn = api.HwAdvOpen;
        AdvButton.IsOn = _advOn;

        // 按钮内容带图标（与其它 mini 按钮同规格）
        AdvButton.Content = AdvLabel();
        RefreshButton.Content = ButtonContent(Icons.Refresh, "重新采集");
        CopyButton.Content = ButtonContent(Icons.Copy, "复制全部");

        // busy 轮询：第一次打开 / 刚点完刷新时后端还在采，250ms 后自己再看一眼。
        // ⚠️ 别改回 1.5 秒：采集现在只有 ~120ms，间隔比采集还长就等于让用户白等一秒多。
        _poll = new DispatcherTimer { Interval = PollInterval };
        // ⚠️ 轮询计数放在 **Tick** 里，不放 Load()：切页时 Refresh() 与 Loaded 会
        //    各触发一次 Load，同一拍重复拉数据不该把 40 次上限吃掉（否则采集才几百
        //    毫秒就被误判成「10 秒没结果」）。计数只在真正过了 250ms 时才加一。
        _poll.Tick += (_, _) => { _poll.Stop(); _polls++; Load(); };

        // 主题变化由 MainWindow 统一转发（ApplyTheme）—— 页面自己不订全局事件，
        // 原因见 DashView.ApplyTheme 的注释（关窗 Unloaded 不触发 ⇒ 订阅泄漏 ⇒
        // 下一个窗口切主题时调到死线程上的控件 ⇒ 闪退）。

        // 页面是缓存复用的：切回来会再触发一次 Loaded，而 MainWindow 的
        // ShowPage(changed) 已经先调过 Refresh() → Load()。无条件再 Load 一遍
        // 会重复重建整页可视树，还把轮询计数吃了（见 _poll.Tick 的注释）。
        Loaded += (_, _) => { if (!_waiting && _groups.Count == 0) Load(); };
    }

    // ================================================================ 数据

    /// <summary>
    /// 重新拉一次（再次进入本页时由 MainWindow 调）——
    /// 第一次的后台采集可能已经跑完了。
    /// </summary>
    public void Refresh()
    {
        // 正在采：让轮询继续跑。以前这里是 `if (_busy) return`，采集中切回本页
        // 就再也没人来看结果了，界面停在「正在采集…」直到下次切页。
        if (_waiting)
        {
            _poll.Start();
            return;
        }
        Load();
    }

    /// <summary>拉一次硬件档案。busy 时挂上轮询，采完自动再来。</summary>
    private void Load()
    {
        var d = _api.Hardware();       // ⚠️ 首次会真起后台采集，只在进页/点刷新时调
        if (d is null)
        {
            _waiting = false;
            _polls = 0;
            _poll.Stop();
            SetBusyUi(false);
            if (!_failed)
            {
                _failed = true;
                StatValue.Text = "读取失败";
                StatSub.Text = "后端没有响应（/api/hardware）";
            }
            return;
        }

        _failed = false;

        // 采集中后端回的是**上一帧**（带 busy:true）：内容照收，别把已有数据清成空；
        // 时间戳只在真拿到新帧时才更新。
        var busy = d.Busy == true;
        var groups = ParseGroups(d.Groups);
        var at = d.At ?? 0;

        if (groups.Count > 0) _groups = groups;
        if (!busy)
        {
            _at = at;
            _tookMs = d.TookMs ?? 0;
        }

        // 手动刷新的完成判据：不再忙 **且** 时间戳已经翻新。
        if (_waiting && !busy && at > 0 && Math.Abs(at - _awaitAt) > 1e-3)
            _waiting = false;

        Render();

        if (busy || _waiting)
        {
            if (_polls > PollLimit)   // 计数只在 _poll.Tick 增长，见构造里的注释
            {
                // 采集卡住了（WMI 卡死 / 后端异常）：恢复按钮，让用户自己再点一次
                _waiting = false;
                _polls = 0;
                SetBusyUi(false);
                Toast("采集超时（10 秒没有结果），可以再点一次「重新采集」", false);
                return;
            }
            SetBusyUi(true);
            _poll.Start();
        }
        else
        {
            _polls = 0;
            _poll.Stop();
            SetBusyUi(false);
        }
    }

    /// <summary>
    /// 采集中：按钮变灰 + 文案「采集中…」。
    /// ⚠️ 之前文案在 `finally` 里立刻被改回「重新采集」（POST 只是起了个后台线程），
    ///    用户点完完全看不到反馈，只剩旧数据挂在屏幕上。
    /// </summary>
    private void SetBusyUi(bool on)
    {
        RefreshButton.IsEnabled = !on;
        RefreshButton.Content = ButtonContent(Icons.Refresh, on ? "采集中…" : "重新采集");
    }

    /// <summary>
    /// 解析 groups。异构数组（rows 里只有部分行带 adv/mono）⇒ 逐行判存在性，
    /// 字段用法：缺 k/v 的行直接丢。
    /// </summary>
    private static List<HwGroup> ParseGroups(JsonElement? groups)
    {
        var list = new List<HwGroup>();
        if (groups is not { } root || root.ValueKind != JsonValueKind.Array) return list;

        foreach (var g in root.EnumerateArray())
        {
            if (g.ValueKind != JsonValueKind.Object) continue;
            var id = g.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "" : "";
            var name = g.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? "" : "";
            var rows = new List<HwRow>();
            if (g.TryGetProperty("rows", out var rEl) && rEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rEl.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    var k = r.TryGetProperty("k", out var kEl) && kEl.ValueKind == JsonValueKind.String
                        ? kEl.GetString() : null;
                    var v = r.TryGetProperty("v", out var vEl) && vEl.ValueKind == JsonValueKind.String
                        ? vEl.GetString() : null;
                    if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v)) continue;
                    var sub = r.TryGetProperty("sub", out var sEl) && sEl.ValueKind == JsonValueKind.String
                        ? sEl.GetString() : null;
                    var mono = r.TryGetProperty("mono", out var mEl) && mEl.ValueKind == JsonValueKind.True;
                    var adv = r.TryGetProperty("adv", out var aEl) && aEl.ValueKind == JsonValueKind.True;
                    rows.Add(new HwRow(k!, v!, sub, mono, adv));
                }
            }
            if (rows.Count > 0) list.Add(new HwGroup(id, name, rows));
        }
        return list;
    }

    // ================================================================ 渲染

    private void Render()
    {
        RenderStat();
        RenderBanner();
        RenderGroups();
    }

    private void RenderStat()
    {
        if (_groups.Count == 0)
        {
            StatValue.Text = _waiting ? "正在采集…" : _failed ? "读取失败" : "--";
            StatSub.Text = _waiting ? "八组 WMI 查询并行跑，通常不到 0.2 秒" : "";
            return;
        }
        var count = 0;
        foreach (var g in _groups) count += g.Rows.Count(r => !r.Adv);
        var sub = _at > 0
            ? $"采集于 {DateTimeOffset.FromUnixTimeSeconds((long)_at).ToLocalTime():yyyy/MM/dd HH:mm:ss}"
              + (_tookMs > 0 ? $" · {_tookMs} ms" : "")
            : "";
        // 有旧数据时点「重新采集」：数量照旧显示（那还是上一帧），副行如实说明正在重采
        if (_waiting) sub = sub.Length > 0 ? sub + " · 正在重新采集…" : "正在重新采集…";

        StatValue.Text = $"{_groups.Count} 类 · {count} 项";
        StatSub.Text = sub;
    }

    /// <summary>系统横幅：系统全名（去掉 "Microsoft " 前缀）+ 版本号/计算机名等提升行。</summary>
    private void RenderBanner()
    {
        var sys = _groups.FirstOrDefault(g => g.Id == "sys");
        var rows = sys?.Rows ?? [];

        var os = rows.FirstOrDefault(r => r.K == "系统");
        OsTitle.Text = os is { } o
            ? (o.V.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? o.V[9..] : o.V)
            : sys?.Name is { Length: > 0 } n ? n : "硬件档案";
        OsTitle.ToolTip = os?.V;

        OsRows.Children.Clear();
        foreach (var key in OsKeys.Skip(1))
        {
            var r = rows.FirstOrDefault(x => x.K == key);
            if (r is null) continue;
            // 「系统内部版本」在横幅里写「版本号」，且这一行不随进阶折叠
            var isVersion = key == "系统内部版本";
            var adv = r.Adv && !isVersion;
            if (adv && !_advOn) continue;
            OsRows.Children.Add(ChipPair(isVersion ? "版本号" : key, r.V, r.Sub));
        }
    }

    private void RenderGroups()
    {
        GroupsHost.Children.Clear();

        // 显示顺序：HW_ORDER 里的按表排，没写的排最后且保持后端原顺序（稳定排序）
        var sorted = _groups
            .Select((g, i) => (g, i))
            .OrderBy(x => Array.IndexOf(HwOrder, x.g.Id) is var r && r < 0 ? HwOrder.Length : r)
            .ThenBy(x => x.i)
            .Select(x => x.g)
            .ToList();

        foreach (var g in sorted)
        {
            // 横幅提走的行不再在这一组里出现（一条信息只出现一次）
            var rest = g.Id == "sys"
                ? g.Rows.Where(r => !OsKeys.Contains(r.K)).ToList()
                : g.Rows;

            // 折叠态下整组只剩进阶内容 ⇒ 整组收起
            if (!_advOn && rest.All(r => r.Adv)) continue;

            var color = HwColors.TryGetValue(g.Id, out var c) ? c : default;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GroupColWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 左：徽标 + 组名
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            left.Children.Add(MakeBadge(g.Id, color));
            left.Children.Add(new TextBlock
            {
                Text = g.Name,
                Style = (Style)FindResource("HwGroupName"),
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            grid.Children.Add(left);

            // 右：主型号 + 指标流
            var want = HwHeroKey.TryGetValue(g.Id, out var heroKey) ? heroKey : null;
            var heroIdx = want is not null ? rest.FindIndex(r => r.K == want && !r.Adv) : -1;
            if (heroIdx < 0) heroIdx = rest.FindIndex(r => !r.Adv);
            var hero = heroIdx >= 0 ? rest[heroIdx] : null;
            var chips = rest.Where((_, i) => i != heroIdx).ToList();

            var right = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
            if (hero is not null) right.Children.Add(HeroBlock(hero));
            right.Children.Add(RowsFlow(chips));
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            // 卡片 padding 12px 10px —— 组的**视觉组距**靠这层内边距撑，
            // 不靠组与组之间的空白。少了它，卡片被页框撑高后多出来的高度只能全灌进
            // space-evenly 的 gap，8 组就变成"每组之间一条大河"（用户眼里的"间隔很怪"）。
            GroupsHost.Children.Add(new Border { Padding = new Thickness(10, 12, 10, 12), Child = grid });
        }
    }

    /// <summary>
    /// 指标流：普通行横排换行（间距 5/24），进阶行**各自独占一行**且折叠时隐藏。
    /// 顺序必须保持原顺序 ⇒ 遇到进阶行就切一段新的流面板，不能"普通行排完再排进阶行"。
    /// </summary>
    private FrameworkElement RowsFlow(List<HwRow> rows)
    {
        var outer = new StackPanel();
        WrapPanel? flow = null;

        // ⚠️ 建流的那一刻就要挂上 outer：只写 `flow ??= new WrapPanel(...)` 而漏了
        //    outer.Children.Add ⇒ WrapPanel 永远进不了可视化树，孩子（指标行）全都白造，
        //    整页只剩每组一行主型号。
        WrapPanel StartFlow()
        {
            var f = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
            outer.Children.Add(f);
            return f;
        }

        foreach (var r in rows)
        {
            if (r.Adv)
            {
                flow = null;
                if (!_advOn) continue;
                outer.Children.Add(ChipPair(r.K, r.V, r.Sub, mono: r.Mono, fullWidth: true));
                continue;
            }
            flow ??= StartFlow();
            flow.Children.Add(ChipPair(r.K, r.V, r.Sub, mono: r.Mono));
        }
        return outer;
    }

    /// <summary>一行「灰标签 + 白值」；sub 跟在值后面当小字。</summary>
    private FrameworkElement ChipPair(string k, string v, string? sub, bool mono = false, bool fullWidth = false)
    {
        // ⚠️ 普通行必须用横向 StackPanel，**不能用 Grid**：ChipPair 的普通行住在 WrapPanel 里，
        //    WrapPanel 量孩子时给的是无限宽约束，而 Grid 在无限宽下会把 Star 列压成 0
        //    ⇒ 值那一列宽 0、文字被裁光，整页只剩主型号，"核心 / 线程 / 基准频率 /
        //    总容量…"这些指标行全部凭空消失。
        var key = new TextBlock { Text = k, Style = (Style)FindResource("HwK") };

        var value = new TextBlock
        {
            Style = (Style)FindResource(mono ? "HwMono" : "HwV"),
            // 右边距 = 列间距 24px（WrapPanel 没有 gap，只能挂在孩子身上）
            Margin = new Thickness(0, 0, fullWidth ? 0 : 24, 0),
        };
        value.Inlines.Add(new Run(v));
        if (!string.IsNullOrEmpty(sub))
        {
            // ⚠️ 副标题的颜色钉 DynamicResource，别抓 PaletteBrush 实例：硬件页是**一次性**
            //    静态档案（页面活着就不再重建），抓下来的画笔会停在进页时的主题上。
            var subRun = new Run("  " + sub) { FontSize = 11.5 };
            subRun.SetResourceReference(TextElement.ForegroundProperty, "DimBrush");
            value.Inlines.Add(subRun);
        }

        if (!fullWidth)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(key);
            line.Children.Add(value);
            return line;
        }

        // 进阶行独占一行（flex-basis:100% 的等价物）：DockPanel 让值那一列
        // 拿到**有限宽度**，长设备 ID / MAC 才能按 HwV 的 TextWrapping 正常折行。
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 5), LastChildFill = true };
        DockPanel.SetDock(key, Dock.Left);
        dock.Children.Add(key);
        dock.Children.Add(value);
        return dock;
    }

    /// <summary>这一组的主型号（.hw-hero：15px / 500 字重 / 最亮的一行）。</summary>
    private FrameworkElement HeroBlock(HwRow hero)
    {
        var text = new TextBlock { Style = (Style)FindResource("HwHero") };
        text.Inlines.Add(new Run(hero.V));
        if (!string.IsNullOrEmpty(hero.Sub))
        {
            var subRun = new Run("  " + hero.Sub) { FontSize = 11.5 };
            subRun.SetResourceReference(TextElement.ForegroundProperty, "DimBrush");
            text.Inlines.Add(subRun);
        }
        return text;
    }

    /// <summary>
    /// 分组徽标：34×34 圆角实心块 + 白色描边图标。**浅色主题必须换装** ——
    /// 白图标在橙/绿/青那几个亮色块上只有 ~2:1，且浅色底下一堆高饱和色块压不住
    /// （CSS 的做法是 color-mix 16% 白 / 68% 掺深，这里照抄结果）。
    /// </summary>
    private Border MakeBadge(string id, Color color)
    {
        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Brush bg;
        Brush fg;
        if (color == default)
        {
            bg = UiTheme.PaletteBrush("AccBrush")!;
            fg = Brushes.White;
        }
        else if (UiTheme.Palette == UiPalette.Light)
        {
            bg = new SolidColorBrush(Mix(color, Colors.White, 0.16));
            fg = new SolidColorBrush(Mix(color, C("#0b1020"), 0.68));
        }
        else
        {
            bg = new SolidColorBrush(color);
            fg = Brushes.White;
        }
        badge.Background = bg;

        if (HwIcons.TryGetValue(id, out var icon))
        {
            var path = new ShapePath
            {
                Data = icon,
                Width = 19,
                Height = 19,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.8,
                Stroke = fg,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badge.Child = path;
        }
        return badge;
    }

    // ================================================================ 交互

    private void Adv_Click(object sender, RoutedEventArgs e)
    {
        _advOn = !_advOn;
        AdvButton.IsOn = _advOn;
        AdvButton.Content = AdvLabel();
        Render();
        SaveAdvOpen();
    }

    /// <summary>「显示 / 隐藏进阶信息」与展开态同义，一处生成两处用（构造初值 + 点击后）。</summary>
    private StackPanel AdvLabel() => ButtonContent(Icons.Sliders, _advOn ? "隐藏进阶信息" : "显示进阶信息");

    /// <summary>
    /// 写回 `presets.ui.hw_adv_open`（通用配置项接口，不动契约）。
    /// 纯界面偏好：成了不出声，只有写失败才提示——切个展开不该弹一堆确认。
    /// </summary>
    private void SaveAdvOpen()
    {
        _ = Task.Run(() =>
        {
            var result = _api.Post("/api/model/preset", new JsonObject
            {
                ["path"] = new JsonArray("ui", "hw_adv_open"),
                ["value"] = _advOn,
            });
            if (!result.Ok)
            {
                var why = result.Why("记住展开态失败");
                Dispatcher.BeginInvoke(() => Toast(why, false));
            }
        });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_waiting) return;           // 已经在采（按钮本来就是灰的）

        _awaitAt = _at;                 // 以当前帧为基准，等时间戳翻新
        _waiting = true;
        _polls = 0;
        SetBusyUi(true);

        // 后台重采（只读）：POST 只是让后端起线程，采完再拉一次 GET。
        // ⚠️ 别在 POST 之后直接 `Load()` 就完事：那时后端多半还没把 `_hwBusy` 置起来，
        //    GET 回的是旧帧 + busy:false，界面会认定"已经刷完"，从此停在旧数据上
        //    （2026-11-08 修的 bug —— 表现就是"点重新采集没反应"）。
        _ = Task.Run(() => _api.Post("/api/hardware/refresh", new JsonObject()));
        _poll.Stop();
        _poll.Start();
        Load();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PlainText());
            Toast("硬件信息已复制到剪贴板", true);
        }
        catch (Exception ex)
        {
            Toast("复制失败（剪贴板被拒绝）：" + ex.Message, false);
        }
    }

    /// <summary>复制成纯文本（发反馈/存档用）—— 含被折叠的进阶行，值原样给、不折行。</summary>
    private string PlainText()
    {
        var lines = new List<string> { "Q-Control 控制中心 · 硬件信息"
            + (_at > 0 ? $"（采集于 {DateTimeOffset.FromUnixTimeSeconds((long)_at).ToLocalTime():yyyy/MM/dd HH:mm:ss}）" : "") };
        foreach (var g in _groups)
        {
            lines.Add("");
            lines.Add("【" + g.Name + "】");
            foreach (var r in g.Rows)
                lines.Add(r.K + ": " + r.V + (r.Sub is { } s ? "（" + s + "）" : ""));
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 主题变了（由 <see cref="MainWindow"/> 统一转发，页面自己不订 <see cref="UiTheme.Changed"/>，
    /// 原因见 <see cref="DashView.ApplyTheme"/> 的注释）。
    /// </summary>
    public void ApplyTheme()
    {
        // 徽标配色跟着主题换（浅色下不能再用白图标压亮色块）
        if (_groups.Count > 0) RenderGroups();
    }

    // ================================================================ 小工具

    /// <summary>按钮内容 = 图标 + 文字（与其它 mini 按钮同规格）。</summary>
    private static StackPanel ButtonContent(Geometry icon, string text)
    {
        var path = new ShapePath
        {
            Data = icon,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        // 描边跟按钮 Foreground 走（悬停/禁用自动变）
        path.SetBinding(ShapePath.StrokeProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(
                System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Control), 1),
        });

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(path);
        line.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return line;
    }

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Color Mix(Color over, Color under, double ratio)
    {
        byte M(byte o, byte u) => (byte)Math.Round(u + (o - u) * ratio);
        return Color.FromRgb(M(over.R, under.R), M(over.G, under.G), M(over.B, under.B));
    }
}
