using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QControl.App.Http;
using QControl.App.Http.Contract;
using QControl.Core.Text;
using QControl.Core.Updates;

namespace QControl.App.Ui.Views;

/// <summary>
/// 关于页：关于作者 / 应用信息 / 日志。
///
/// 数据是**切页时拉一次**（800ms 防抖：连点两次导航
/// 不会发两次请求）+ 每次写操作后对齐一次。区间记录的计时按后端给的 since 自己数
/// （每秒一跳，写成"37 秒"那种会跳变的样子就没法看）。
///
/// 唯一例外是日志卡的两格记录状态：**页面可见时每 2 秒对一次**（<see cref="_pollTimer"/>）——
/// 自动档会自己开一段、自己收一段，只靠切页拉一次就会挂着早就不成立的状态。
///
/// 写口五个，全不碰 EC：
///   · 日志总开关 → <c>/api/app/settings {log_enabled}</c>（关了就**根本不再写**，重启后仍关）；
///   · 区间记录开始/结束（同一颗按钮两态）→ <c>/api/app/log/capture/start|stop</c>；
///   · 自动记录开关与触发温度 → <c>/api/app/settings</c>；
///   · 清理日志 → <c>/api/app/log/clear</c>（**先弹确认框摆出删几个、多大**，删除不可逆）；
///     有采集包或本机机型档案时确认框是三选一：只清日志 / 都清 / 取消 ——
///     采集包（`packs:false`）与档案（`profiles:false`）默认**都不动**：
///     前者要发给作者，后者删了要重启才回落内置档案；
///   · 打开目录 → <c>/api/app/log/open</c>。
/// </summary>
public partial class AboutView : UserControl
{
    /// <summary>作者主页地址。**要改链接只改这里**（与 README 顶部两颗星标 badge 一致）：
    /// GitHub 与 Gitee 双仓同步，同一个 repo 名 Q-Control、同一个账号 czyj1145；
    /// 地址填错或留空 ⇒ 界面画成不可点的「待补充」，绝不"点了没反应"。</summary>
    private static readonly Dictionary<string, string> AuthorLinks = new(StringComparer.Ordinal)
    {
        ["github"] = "https://github.com/czyj1145/Q-Control",
        ["gitee"] = "https://gitee.com/czyj1145/Q-Control",
        ["bilibili"] = "https://space.bilibili.com/439446505",
    };

    private readonly LocalApi _api;
    private readonly DispatcherTimer _capTimer;

    /// <summary>两格记录状态的对时器（2 秒一次）。自动档**什么时候开一段 / 什么时候收**
    /// 都不是用户点的，只在切页时拉一次 `about` 就永远停在那一帧 —— 会写出"记录中 12:31"
    /// 这种早就不存在的状态。进程内读快照，不起请求。</summary>
    private readonly DispatcherTimer _pollTimer;

    /// <summary>「已运行」每秒往后走一格（数据只在切页时拉一次，见 RenderUptime）。</summary>
    private readonly DispatcherTimer _uptimeTimer;
    private long _upBaseSec = -1;         // 后端快照里的 uptime_s
    private long _upBaseTick;             // 拿到那份快照时的 Environment.TickCount64

    // 应用信息四行（全站通用的 KvRow / KvValue，别再单独做小号）。
    // 「版本」不在这里 —— 它跟着「检查更新」搬到左边作者卡底部了（见 UpdateRow）。
    private readonly TextBlock _devValue;
    private readonly TextBlock _portValue;
    private readonly TextBlock _startedValue;
    private readonly TextBlock _uptimeValue;

    private AboutDto? _about;
    private bool _capActive;
    private bool _capAuto;
    private DateTime _capSince;
    private bool _busy;

    /// <summary>日志此刻能不能记（总开关开着 **且** 文件写得进去）；由 <see cref="ApplyLogGate"/> 维护。
    /// 只影响「开始记录」这类会写日志的控件是否可点。</summary>
    private bool _logReady = true;

    /// <summary>回写触发温度滑块真值时闸住 ValueChanged（否则拉一次数据就写一次盘）。</summary>
    private bool _autoTempSyncing;

    /// <summary>拖滑块时的防抖：停手 350ms 才写一次（每格都发会把 presets.log 写爆）。</summary>
    private DispatcherTimer? _autoTempTimer;

    internal AboutView(LocalApi api)
    {
        _api = api;
        InitializeComponent();
        UiKit.ApplyCardGap(CardGrid);
        _adoptSegs = [AdoptSeg1, AdoptSeg2, AdoptSeg3, AdoptSeg4, AdoptSeg5];

        _devValue = MakeInfoRow("机型");
        _portValue = MakeInfoRow("监听端口");
        _startedValue = MakeInfoRow("启动时间");
        _uptimeValue = MakeInfoRow("已运行");

        // 五行在卡里铺开（与总览的两张读数卡、系统页的电池/运行状态卡同一套分布规则）。
        // 不加就是"五行挤在上半截、下面空一大块"，留白节奏不统一。
        UiKit.EnableSpaceEvenly(InfoKv, minGap: 8, maxGap: 26);

        // 区间计时：记录中每秒刷一次「正在记录 mm:ss」
        _capTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _capTimer.Tick += (_, _) => RenderCapStateText();

        // 记录状态对时（手动 / 自动两格）：2 秒一次，后台优先级，只改几行字
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _pollTimer.Tick += (_, _) => SyncCapture();

        // 「已运行」的秒表（与上面那个分开：区间计时只在记录中跑，这个要一直跑）
        _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += (_, _) => RenderUptime();

        // 触发温度滑块：点轨道直接跳
        AutoTempSlider.IsMoveToPointEnabled = true;
        AutoTempSlider.ValueChanged += AutoTemp_Changed;

        RenderAuthorLinks();

        Loaded += (_, _) =>
        {
            LoadAbout();                 // 记录中时会把 _capTimer 接回来
            LoadUsage();
            // 更新状态是纯内存快照（不发请求），直接读；正在查就把轮询接上
            RenderUpdate(_api.Update());
            _uptimeTimer.Start();
            _pollTimer.Start();
            if (_adoptBusy)                 // 采集中切回来：真实阶段进度接着走
            {
                AdoptProgressBox.Visibility = Visibility.Visible;
                RenderAdoptProgress(Math.Max(1, _api.AdoptStep), _api.AdoptStage);
                StartAdoptPoll();
            }
        };

        // 页被移出可视树就**把六块表全停了**（页面是缓存复用的，只停两块的话
        // 更新轮询 / 区间计时 / 采集进度会继续 Tick 并改已卸载的控件）。
        // 不置 null：切回来时 Loaded 会按当前状态把它们接上。
        Unloaded += (_, _) =>
        {
            _uptimeTimer.Stop();
            _pollTimer.Stop();
            _capTimer.Stop();
            _autoTempTimer?.Stop();
            _adoptPoll?.Stop();
            _updatePoll?.Stop();
        };
    }

    // ================================================================ 构建

    /// <summary>
    /// 应用信息一行 —— 就是全站那套读数行（<see cref="UiKit.MakeKv"/> 的 KvRow / KvLabel / KvValue）。
    ///
    /// ⚠️ 别再造一套「关于页专用」的小号读数行：11.5/12 号字 + 值加粗 + hover 提亮，
    ///    跟系统页、总览页的 12.5/14 摆在一起就是"这张卡小一号"。
    ///    这里不用 MakeKv 只因为机型名会很长，要 Auto + * 两列把值右对齐并允许截断。
    /// </summary>
    private TextBlock MakeInfoRow(string label)
    {
        var line = new Border { Style = (Style)FindResource("KvRow") };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("KvLabel") });
        var value = new TextBlock
        {
            Text = "读取中…",
            Style = (Style)FindResource("KvValue"),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        line.Child = grid;
        InfoKv.Children.Add(line);
        return value;
    }

    /// <summary>三颗外链按钮：地址空/不合法 ⇒ 降透明度 + 「待补充」徽章（.lnkbtn.off 的语义）。</summary>
    private void RenderAuthorLinks()
    {
        SetupLink(GithubButton, "GitHub 仓库", AuthorLinks["github"]);
        SetupLink(GiteeButton, "Gitee 仓库", AuthorLinks["gitee"]);
        SetupLink(BiliButton, "B 站主页", AuthorLinks["bilibili"]);
    }

    private void SetupLink(Button button, string label, string url)
    {
        var ok = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https";
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (!ok)
        {
            var tag = new TextBlock
            {
                Text = "待补充",
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };
            tag.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
            line.Children.Add(new Border
            {
                Style = (Style)FindResource("CardTag"),
                Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(7, 0, 0, 0),
                Child = tag,
            });
            button.IsEnabled = false;
            button.ToolTip = label + "：地址还没填（填好 AboutView 的 AuthorLinks 就生效）";
        }
        else
        {
            // ↗ 只是"会在浏览器打开"的提示，不是图标（Icons.cs 里没有品牌图标，别硬画）；
            // 只在可点时出现 —— 待补充的按钮挂个外链箭头会自相矛盾。
            line.Children.Add(new TextBlock
            {
                Text = "↗",
                Margin = new Thickness(5, 0, 0, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75,
            });
            button.Tag = uri!.ToString();
            button.ToolTip = label + "：" + uri;
        }
        button.Content = line;
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || url.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            // 浏览器可能被静默拦截（企业策略 / 默认浏览器没设），给一句反馈，别让人以为按钮坏了
            Toast("已在浏览器打开 " + Humanize.ShortUrl(url), true);
        }
        catch (Exception ex)
        {
            Toast("打不开浏览器：" + ex.Message, false);
        }
    }

    // ================================================================ 机型适配
    //
    // 给**别的广达机型**用户留的出口。**一次采集做完两件事**（2026-11-08）：
    //   · 读：多帧采样 → 自动判 EC 偏移（layout）→ 本机档案；
    //   · 写：性能档 APMD 逐值写入读回 + 键盘背光换档读回（写前读原值、写完恢复）。
    // 以前这两半分在两次采集里，中间还要求用户自己去设置页开总闸试写 —— 三跳，
    // 绝大多数人走到第二跳就散了，于是采集包里永远只有"读"的证据，那台机器就永远适配不了。
    //
    // 三个入口：
    //   · 一键采集 → POST {adopt:true, save:true, probe:true}，**先过原厂门槛**（没开就提醒打开、
    //     开着但界面关着就提醒打开界面，见 Ui/VendorPrompt），**再弹一次确认**（会写 EC）；
    //   · 只读采集 → 同一条接口 probe:false：一个字节都不写，所以不弹确认（但一样过原厂门槛 ——
    //     引导那一段是性能档 / 分区 / 槽位 / 转速的唯一来源，原厂没开就白采）；
    //   · 复制全文 → 只复制**刚才那一份**采集的人读全文（不再偷偷再采一次：
    //     以前它调 save:false 重新采一份，用户拿到的是"和发给我们的不一样"的另一份）。

    private bool _adoptBusy;
    private string? _adoptPackPath;
    private string? _adoptText;
    private string? _adoptIssueTitle;
    private string? _adoptIssueBody;
    private DispatcherTimer? _adoptPoll;

    /// <summary>五段进度的格子（构造时按 XAML 里的顺序填，别在这里 new 一堆 Border）。</summary>
    private Border[] _adoptSegs = [];

    /// <summary>已经启动脉动动画的那一段（-1 = 没有）。见 <see cref="PaintAdoptProgress"/>。</summary>
    private int _pulseStep = -1;

    private static int IntOf(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
        }
        return 0;
    }

    private async void AdoptSave_Click(object sender, RoutedEventArgs e)
        // 引导观察给 150 秒：每一步都要在原厂界面里翻一下，60 秒常常不够。
        // 步骤走完会自己收尾（见 GuideWindow.Mark），所以放宽不会让用户干等。
        => await AdoptAsync(probe: true, observeSeconds: MachineObserve.GuidedSeconds);

    private async void AdoptReadonly_Click(object sender, RoutedEventArgs e)
        // 只读也带引导：引导那一段**一个字节都不写**（只 peek 缓冲区 + 读 status），
        // 但它是风扇分区/槽位/三档 APMD/转速的唯一来源。以前这里 observe:0，
        // 于是"不愿意让程序写 EC"的用户交上来的包永远只有温度偏移 —— 那台机器
        // 就永远差一半。不愿意写的人，恰恰最需要靠引导把语义补上。
        => await AdoptAsync(probe: false, observeSeconds: MachineObserve.GuidedSeconds);

    private async void AdoptEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_adoptBusy) return;      // 采集还在跑：此刻的档案与配置都不完整

        var ok = await UiDialog.ConfirmAsync(
            this,
            "按这次引导启用本机？",
            "按这次引导认出的能力打开 EC 写入，并重启加载新档案。\n\n"
            + "这是照原厂写命令推出来的，没经作者复核：写错重启即可恢复，"
            + "风扇写入仍只观察过、没自己试过。\n\n"
            + "随时可在设置页「EC 控制」关掉。",
            okText: "启用并重启",
            danger: true);
        if (!ok) return;

        var r = _api.Post("/api/app/settings", new JsonObject { ["ec_enabled"] = true });
        if (!r.Ok)
        {
            Toast(r.Why(Text(r.Raw?["error"]) ?? "打开 EC 写入失败"), false);
            return;
        }

        // 与「重启生效」同一条路（零 UAC 任务代启），只是不再问第二次
        var restart = UiElevate.RestartElevated("about");
        switch (restart.Status)
        {
            case QControl.Hal.Sys.ElevateStatus.Started:
                Toast("已启用，正在重启加载新档案…", true);
                break;
            case QControl.Hal.Sys.ElevateStatus.Cancelled:
                Toast("已启用 EC 写入，但重启被取消 —— 手动重开一次控制中心才会用上新档案", false);
                break;
            default:
                Toast("已启用 EC 写入；重启失败：" + (restart.Detail ?? "原因未知"), false);
                break;
        }
    }

    private void AdoptCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_adoptText is not { Length: > 0 })
        {
            Toast("先采集一次，再复制全文", false);
            return;
        }
        try
        {
            Clipboard.SetText(_adoptText);
            Toast("已复制到剪贴板", true);
        }
        catch (Exception ex)
        {
            Toast("复制失败：" + ex.Message, false);
        }
    }

    /// <summary>
    /// 「提交采集包」：打开新建 Issue 页，标题与摘要已经按这次采集填好 ——
    /// 采集包本身在资源管理器里已经选中，用户把它拖进正文就交完了。
    ///
    /// 为什么不做成"一键上传"：控制中心没有服务端，也**不该**为收一个文件开一条上行通道
    /// （那会把"只读本地、不外联"这条底线破掉，还得处理鉴权 / 配额 / 隐私）。所以这里
    /// 只做两步能做的事：把该说的话填好、把文件选好。**唯一要用户亲手做的是拖一次文件**
    /// —— 浏览器不允许网页替用户选本地文件，这一步谁也没法省。
    ///
    /// 没有 GitHub 账号 / 打不开浏览器时不要变成死路：正文会落到剪贴板，旁边还有
    /// 「复制全文」和系统日志目录，用户贴到任何渠道都能交（README 里也是这么写的）。
    /// </summary>
    private void AdoptSubmit_Click(object sender, RoutedEventArgs e)
    {
        if (_adoptPackPath is not { Length: > 0 })
        {
            Toast("这次没写出采集包文件，没法提交（看提示行里的原因）", false);
            return;
        }

        var url = QControl.App.Updates.UpdateChecker.GitHubRepo + "/issues/new"
                  + "?title=" + Uri.EscapeDataString(_adoptIssueTitle ?? "[机型适配] 采集包")
                  + "&body=" + Uri.EscapeDataString(_adoptIssueBody ?? "");

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            // 顺手把正文留在剪贴板：GitHub 未登录时会跳登录页、预填的正文就丢了 ——
            // 那一步用户没法预防，我们至少让他能粘回来（提示里会说明，不做暗地里的搬运）。
            try { Clipboard.SetText(_adoptIssueBody ?? ""); } catch { /* 剪贴板被占着就算了 */ }

            Toast("已打开提交页：把刚选中的 .json 拖进去（摘要在剪贴板，正文丢了就粘回去）", true);
        }
        catch (Exception ex)
        {
            // 打不开浏览器就把该说的话留在剪贴板里（用户去哪都能交，别卡死在一步）
            try { Clipboard.SetText((_adoptIssueTitle ?? "") + "\n\n" + (_adoptIssueBody ?? "")); }
            catch { /* 剪贴板也被占着：至少下面的 toast 说了文件在哪 */ }
            Toast("打不开浏览器：" + ex.Message + "（摘要已复制到剪贴板）", false);
        }
    }

    /// <summary>
    /// 提交页正文。**只写适配需要的、且不含隐私的**：机型名、程序版本、采集结论、
    /// 文件名（不带完整路径 —— 路径里有用户名）。
    /// </summary>
    private static string IssueBody(JsonObject? raw, bool probe, bool layoutVerified)
    {
        var name = Text(raw?["name"]);
        var samples = raw?["samples"]?.ToJsonString() ?? "?";
        var recognized = (raw?["recognized"] as JsonArray)?.Count ?? 0;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("**机型**：" + (name is { Length: > 0 } n ? n : "（见采集包）"));
        sb.AppendLine("**程序**：Q-Control " + AppState.AppVersion);
        sb.Append("**采集**：").Append(samples).Append(" 帧采样 · 认出 ")
          .Append(recognized).Append(" 项偏移 · ")
          .Append(layoutVerified ? "温度/转速偏移已认出" : "温度偏移没高置信认出（跑点负载重采更好）");
        sb.AppendLine();

        if (!probe) sb.AppendLine("**写入**：这次是只读采集，没有验证写入。");
        else
        {
            var perf = raw?["writes_verified"]?["perf"] is JsonNode p && p.GetValue<bool>();
            var light = raw?["writes_verified"]?["backlight"] is JsonNode b && b.GetValue<bool>();
            var skip = Text(raw?["probe_skip"]);
            sb.Append("**写入**：").AppendLine(skip is { Length: > 0 }
                ? "没验（" + skip + "）"
                : $"性能档 {(perf ? "读回一致" : "没对上")} / 键盘背光 {(light ? "读回一致" : "没对上")} / 风扇按设计未验证");
        }

        sb.AppendLine();
        sb.AppendLine("**采集包**：`logs\\adopt-*.json` 已在本机资源管理器中选中，请把它拖到下面"
                      + "（这一个文件就够：采集结论 + 逐帧采样 + 写命令的读回值都在里面）。");
        sb.AppendLine();
        sb.AppendLine("另外想补充的（可选）：这台机器上原厂 ControlCenter 能不能正常调风扇 / 切性能模式？");
        return sb.ToString();
    }

    /// <summary>
    /// 采集前的确认现在由 <see cref="VendorPrompt"/> 一处负责（原厂门槛 + 会不会写 EC 合成一屏）。
    /// 分家的老写法留在这里只会变成两份文案，迟早走散。
    /// </summary>
    private async Task AdoptAsync(bool probe, int observeSeconds)
    {
        if (_adoptBusy) return;

        // 对话框期间就锁上：连点两次会叠两个遮罩，而后端只跑得了一个采集任务
        // （用户看到的是"我点了两次，只有一次出了结果"）。
        _adoptBusy = true;
        var restore = AdoptHint.Text;

        try
        {
            // ---- 采集前的那一道确认（2026-11-22 合并成一屏）----
            // 原来这里是「写 EC」确认框，正文末尾捎带一句"要求原厂正在运行"等于没提醒；
            // 现在原厂门槛与写 EC 合成同一屏（见 Ui/VendorPrompt）：没开就提醒先打开、
            // 开着但界面关在托盘里就提醒打开界面。用户选「跳过引导」就把观察窗口传 0，
            // 不让他对着一个关着的界面干等 150 秒。
            var gate = await VendorPrompt.AskAsync(this, probe, () => _api.VendorUi);
            if (gate == VendorGate.Cancel) return;

            SetAdoptBusy(true);
            // 上一轮的产物按钮必须先收掉：它们指向的是**上一次**的采集包/档案，
            // 这一轮还在跑的时候点「提交」「重启」等于拿旧结果当新结果用
            // （2026-11-13 用户实测：采集进行中这两颗按钮还能点）。
            AdoptEnableButton.Visibility = Visibility.Collapsed;
            AdoptSubmitButton.Visibility = Visibility.Collapsed;
            AdoptRestartButton.Visibility = Visibility.Collapsed;

            // 采集期间这行只说"要多久、现在可以做什么"；真正的阶段交给下面的分段进度
            // （一行字既当说明又当进度，用户得盯着它变才看得懂自己等到哪了）。
            AdoptState.Text = probe ? "采集中（会写两次 EC）" : "采集中（只读，不写 EC）";
            AdoptHint.Text = gate == VendorGate.SkipGuide
                ? "全程约 1 分半（跳过引导，只采本机这一段）"
                : "全程约 2 分钟，别关原厂控制中心";
            AdoptProgressBox.Visibility = Visibility.Visible;
            RenderAdoptProgress(1, "读机型信息…");
            StartAdoptPoll();

            var result = await Task.Run(() => _api.Post("/api/app/machine-report", new JsonObject
            {
                ["save"] = true,
                ["adopt"] = true,
                ["probe"] = probe,
                ["observe"] = gate == VendorGate.SkipGuide ? 0 : observeSeconds,
            }));

            if (!result.Ok)
            {
                var why = Text(result.Raw?["error"]);
                Toast(result.Why(why ?? "采集失败"), false);
                AdoptHint.Text = restore;
                return;
            }

            var text = Text(result.Raw?["text"]) ?? "";
            if (text.Length == 0)
            {
                Toast("采集结果为空，请重启控制中心后再试", false);
                AdoptHint.Text = restore;
                return;
            }
            _adoptText = text;

            // ---- 本机档案不一定写了：源码目录 / 已有已验证档案时会拒绝覆盖 ----
            var id = Text(result.Raw?["id"]) ?? "档案";
            var verified = result.Raw?["layout_verified"]?.GetValue<bool>() == true;
            // 缺省按「写了」：旧后端没有这个键，别把一次成功的采集说成没写
            var written = true;
            if (result.Raw?["profile_written"] is JsonValue pv && pv.TryGetValue<bool>(out var pw))
                written = pw;
            // 采完要定位到**交付包**（logs\adopt-*.json），不是 devices\ 里那份档案：
            // 用户接下来要做的是"把文件发给我们"，发档案过来没有证据、复核不了。
            _adoptPackPath = Text(result.Raw?["pack"]);
            _adoptIssueTitle = "[机型适配] " + (Text(result.Raw?["name"]) ?? id) + " 采集包";
            _adoptIssueBody = IssueBody(result.Raw, probe, verified);

            // 观察被跳过时一定要明说：用户等了引导弹窗却一次没出现，
            // 只说「已生成档案」会让人以为程序没让他操作就自己适配完了。
            // 两种跳过要分开说：原厂没跑（后端报 skipped）/ 用户自己在门槛上选了「跳过引导」。
            var skipped = Text(result.Raw?["observation"]?["skipped"]);
            var skipNote = skipped is { Length: > 0 } ? "原厂控制中心没在运行，引导已跳过"
                         : gate == VendorGate.SkipGuide ? "已跳过引导"
                         : null;
            Toast(skipNote is not null
                    ? skipNote + "（这次只做了读取适配）"
                    : written ? $"已生成机型档案 {id}（重启后生效）"
                              : "已生成采集包（本机档案没写，原因见报告）",
                  skipNote is null);
            AdoptState.Text = skipNote is not null ? "已采集 · 引导已跳过"
                : written ? "已生成 " + id : "已生成采集包";
            var summary = AdoptSummary(result.Raw, probe, verified, written, _api.EcWritesAllowed);
            if (gate == VendorGate.SkipGuide && skipNote is not null) summary += " · 引导跳过";
            AdoptHint.Text = summary;
            AdoptHint.ToolTip = (result.Raw?["missing"] is JsonArray miss && miss.Count > 0)
                ? string.Join("\n", miss.Select(m => Text(m)))
                : null;
            // 没写档案 ⇒ 重启不会改变任何东西，别摆一颗点了没反应的「重启」按钮
            AdoptRestartButton.Visibility = written ? Visibility.Visible : Visibility.Collapsed;
            AdoptSubmitButton.Visibility = _adoptPackPath is { Length: > 0 }
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 「启用本机」只在"档案真写进去了、而且引导确认过东西"时才摆：
            // 没确认任何能力就摆一颗"启用"，点下去只会打开一个什么都不多的总闸。
            var confirmed = IntOf(result.Raw?["guide"]?["confirmed"]);
            AdoptEnableButton.Visibility = written && confirmed > 0 && !_api.EcWritesAllowed
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 让用户一眼看到刚生成的**报告**（而不是自己去 logs / ProgramData 里翻）
            RevealInExplorer(_adoptPackPath ?? (result.Raw?["files"] is JsonArray l && l.Count > 0
                ? Text(l[0]) : null));
        }
        catch (Exception ex)
        {
            // 异常路径以前什么都不写：提示行停在"正在采集…"，而 finally 已经把按钮放开了。
            // 结果就是"看着在采、其实早挂了"，还能反复再点（现场连点了三次）。
            Toast("采集中断：" + ex.Message, false);
            AdoptHint.Text = restore;
            AdoptHint.ToolTip = null;
            AdoptState.Text = "采集失败";
        }
        finally
        {
            StopAdoptPoll();
            HideAdoptProgress();
            _adoptBusy = false;
            SetAdoptBusy(false);
        }
    }

    /// <summary>
    /// 采完那一行。只留四件事：**偏移认没认出** → **写入验没验成** → **引导拿到几项** → **下一步**。
    /// 抓了几条写命令、风扇归属怎么判的，都在报告里 —— 提示行堆满六段，用户就不看了。
    /// </summary>
    private static string AdoptSummary(JsonObject? raw, bool probe, bool layoutVerified, bool profileWritten,
                                       bool writesOn)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(layoutVerified ? "偏移已认出" : "温度偏移没认出（重采时跑点负载）");

        // 风扇转速单独报一句：它只在那一步真把风扇拉起来时才认得出（靠变速的形状），
        // 不说这句，用户会以为顺手做的那个动作没派上用场。
        var fans = IntOf(raw?["fan_rpm"]?["count"]);
        if (fans > 0) sb.Append($" · 风扇 {fans} 路");

        if (!probe) sb.Append(" · 只读（没验写入）");
        else
        {
            var perf = raw?["writes_verified"]?["perf"] is JsonNode p && p.GetValue<bool>();
            var light = raw?["writes_verified"]?["backlight"] is JsonNode b && b.GetValue<bool>();
            var skip = Text(raw?["probe_skip"]);
            sb.Append(skip is { Length: > 0 } ? " · 写入没验：" + skip
                : perf && light ? " · 写入读回一致"
                : perf || light ? " · 只有" + (perf ? "性能档" : "背光") + "读回一致"
                : " · 写入没对上（见报告）");
        }

        // 观察跑了、但一步都没标 ⇒ 那 150 秒等于白采（性能档 / 背光 / 风扇全没结论）。
        // 这句必须说，否则用户只会看到"已生成档案"，不知道能力表为什么还是空的。
        if (raw?["observation"]?["skipped"] is JsonValue sv && sv.TryGetValue<string>(out var obsSkip)
            && obsSkip.Length > 0)
            sb.Append(" · 引导跳过");
        else if (raw?["guide"] is JsonObject gv)
        {
            var confirmed = IntOf(gv["confirmed"]);
            sb.Append(confirmed > 0 ? $" · 引导 {confirmed}/{IntOf(gv["total"])}"
                                    : " · 引导没拿到能力");
        }
        else if (raw?["observation"] is not null)
            sb.Append(" · 引导 0/" + QControl.Core.Devices.AdoptGuide.Steps.Count + " 步");

        sb.Append(profileWritten
            ? writesOn ? " · 重启后生效" : " · 重启后生效（点「启用本机」）"
            : " · 档案没写（见报告）");
        return sb.ToString();
    }

    private void SetAdoptBusy(bool busy)
    {
        AdoptSaveButton.IsEnabled = !busy;
        AdoptReadonlyButton.IsEnabled = !busy;
        AdoptCopyButton.IsEnabled = !busy && _adoptText is { Length: > 0 };
        // 结果按钮同理：采集进行中一律不可点（它们指向上一轮的产物）
        AdoptSubmitButton.IsEnabled = !busy;
        AdoptRestartButton.IsEnabled = !busy;
        AdoptEnableButton.IsEnabled = !busy;
    }

    /// <summary>
    /// 采集期间每 300 ms 把**真实阶段**画成五段进度（后端在同一进程里报"走到第几段"，
    /// 见 <c>LocalApi.AdoptStep / AdoptStage</c>）。
    /// 为什么不是一根百分比条：总时长取决于机器（引导要用户手点），百分比编不出来，
    /// 编出来就是骗人 —— 项目规矩是宁可不显示，只把**段边界**摆出来。
    /// </summary>
    private void StartAdoptPoll()
    {
        if (!IsLoaded) return;      // 页面卸载后不许回读把计时器重新点着
        StopAdoptPoll();
        _adoptPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _adoptPoll.Tick += (_, _) =>
        {
            // 后端还没报第一段（刚点下按钮的头几百毫秒）就先亮第 1 格，别让进度条空着
            var step = Math.Max(1, _api.AdoptStep);
            var stage = _api.AdoptStage;
            var warn = false;

            var gv = _api.GuideView;
            // 引导走完时给一句明确的话，别让文字停在"照小窗做"
            if (gv?["finished"] is JsonValue fv && fv.TryGetValue<bool>(out var done) && done)
            {
                stage = "步骤做完了，接着验证写入…";
                RenderAdoptProgress(step, stage);
                return;
            }

            // 观察是**固定窗口**：到点没标的步骤按"没做到"收尾。最后 15 秒必须在主界面上
            // 也说一遍（小窗可能被原厂窗口压住），并告诉他唯一的挽回办法是那颗「再等 60 秒」。
            var remaining = IntOf(gv?["remaining"]);
            if (gv?["phase"] is JsonValue pv && pv.TryGetValue<string>(out var phase)
                && phase == "observe" && remaining is > 0 and <= 15)
            {
                stage = $"还剩 {remaining} 秒 —— 没点的按「没做到」算，要延长就点小窗「再等 60 秒」";
                warn = true;
            }

            RenderAdoptProgress(step, stage, warn);
        };
        _adoptPoll.Start();
    }

    /// <summary>
    /// 画五段进度。<paramref name="step"/> 是**后端报的第几段**（1..5），不是算出来的。
    /// 已走过的段压暗铺实色、当前段脉动、还没到的段保持轨道色 —— 一眼就能看出"走到哪、还剩几段"。
    /// </summary>
    private void RenderAdoptProgress(int step, string? stage, bool warn = false)
    {
        if (_adoptSegs.Length == 0) return;

        for (var i = 0; i < _adoptSegs.Length; i++)
        {
            var seg = _adoptSegs[i];
            var n = i + 1;

            // ⚠️ 只在"当前段变了"时才清动画。这段进度每 300ms 轮询重画一次，
            //    旧写法每拍 `BeginAnimation(null)` + 新建 720ms `Forever` 动画 ⇒
            //    呼吸被每 300ms 掐断，看到的是 1↔0.85 闪跳（"当前段一直在闪"），
            //    还每秒白建三条动画时钟。
            var keepPulse = n == step && _pulseStep == step;
            if (!keepPulse)
            {
                seg.BeginAnimation(OpacityProperty, null);   // 清掉上一帧的脉动
                seg.Opacity = 1;
            }

            if (n < step)
            {
                seg.SetResourceReference(Border.BackgroundProperty, "AccBrush");
                seg.Opacity = 0.5;                      // 走完的段：压暗，别和"正在做"抢眼
            }
            else if (n == step)
            {
                seg.SetResourceReference(Border.BackgroundProperty, "AccBrush");
                // 脉动只在 0.85~1：当前格必须始终比"已完成"(0.5) 亮一档，
                // 否则慢一拍的时刻看起来就是两段一样的深蓝，分不清走到哪
                if (!keepPulse)
                    seg.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(720))
                        {
                            AutoReverse = true,
                            RepeatBehavior = RepeatBehavior.Forever,
                        });
            }
            else
            {
                seg.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            }
        }

        var names = QControl.App.MachineAdopt.StepNames;
        _pulseStep = step;
        var head = step >= 1 && step <= names.Length ? $"{step}/{names.Length} {names[step - 1]}" : "";
        AdoptProgressText.Text = stage is { Length: > 0 }
            ? head.Length > 0 ? head + " · " + stage : stage
            : head;
        AdoptProgressText.SetResourceReference(TextBlock.ForegroundProperty, warn ? "WarnBrush" : "DimBrush");
    }

    /// <summary>采集结束（成功/失败）把进度区收掉：那些格子只属于"正在采"这一刻。</summary>
    private void HideAdoptProgress()
    {
        foreach (var seg in _adoptSegs)
        {
            seg.BeginAnimation(OpacityProperty, null);
            seg.Opacity = 1;
        }
        _pulseStep = -1;
        AdoptProgressBox.Visibility = Visibility.Collapsed;
    }

    private void StopAdoptPoll()
    {
        _adoptPoll?.Stop();
        _adoptPoll = null;
    }

    /// <summary>
    /// 「重启生效」：真的按新档案重启控制中心。
    ///
    /// 为什么要重启而不是热切换：当前 Device 已经被 EcHal / FanCurveEngine / HotkeyService
    /// 一路持有（GmWmi 也是照它的 spec 建的），换档案等于重建整条链。那件事在用户点按钮的
    /// 那一刻做，出问题就是"点了下按钮，风扇曲线没了" —— 不如老老实实换一个进程。
    ///
    /// 走的是既有的「零 UAC 任务代启」（`UiElevate`）：新进程会请本进程退出再接管端口，
    /// 本进程**不要**自己抢着退，否则会暴露"新进程还没绑上端口"的空窗。
    /// </summary>
    private async void AdoptRestart_Click(object sender, RoutedEventArgs e)
    {
        var ok = await UiDialog.ConfirmAsync(
            this,
            "重启控制中心？",
            "新档案只在启动时读取：重启后这台机器才会按它显示温度与转速。"
            + "重启期间风扇曲线会短暂交回 EC 自动控制（约 1 秒），窗口会自动回来。",
            okText: "重启");
        if (!ok) return;

        var result = UiElevate.RestartElevated("about");
        switch (result.Status)
        {
            case QControl.Hal.Sys.ElevateStatus.Started:
                Toast("正在重启：窗口会自动回来", true);
                break;

            case QControl.Hal.Sys.ElevateStatus.Cancelled:
                Toast("已取消（UAC 上点了「否」）", false);
                break;

            default:
                Toast("重启失败：" + (result.Detail ?? "原因未知"), false);
                break;
        }
    }

    /// <summary>在资源管理器里选中刚导出那份文件（打开目录 ≠ 让人一眼看到文件）。</summary>
    private void RevealInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch
        {
            // 打不开资源管理器不影响导出本身，文件已经在磁盘上了
        }
    }

    /// <summary>卡头 tag 与说明文案：验证过 / 未适配两种口气，别让人以为"没适配就废了"。</summary>
    private void RenderAdopt(bool verified)
    {
        AdoptTagBox.Style = (Style)FindResource(verified ? "CardTag" : "CardTagWarn");
        AdoptTag.Style = (Style)FindResource(verified ? "CardTagText" : "CardTagTextWarn");
        AdoptTag.Text = verified ? "已验证 · 报告可选" : "未适配 · 需要这份报告";

        AdoptDesc.Text = verified
            ? "本机已标定。要回归对照就采一份：报告含 EC 原始应答与写入读回值。只看读数点「只读采集」。"
            : "本机还没适配：温度与转速由 HWiNFO 兜底，EC 写入关闭。点「一键采集」，"
              + "照小窗在原厂里做几步，能确认的能力才打开，没证据的如实关掉。"
              + "采完点「启用本机」重启生效。";
    }

    // ================================================================ 版本更新
    //
    // 这一块干两件事：**检测**（后端查一次最新发布，GitHub → Gitee 回退）和
    // **下载 / 安装**（把安装包下到本机、校验、再起安装器）。
    //
    // 为什么不再"交给浏览器"（2026-11-14 用户要求）：点「前往下载」要跳出去、在对的附件里挑、
    // 下完再回来关程序 —— 三步里任一步走散都是"更新失败"（下成便携版 zip、下到旧版本、
    // 忘了先退出导致覆盖安装失败）。现在点一次就下到本机，SHA256 对上了才允许安装。
    //
    // ⚠️ 界面从不自己判断"能不能装"：能装与否由后端在 install 里再核一遍
    //    （文件还在不在、哈希是否通过），这里只负责把后端状态画出来。
    // ⚠️ 检查是**异步**的：后端起后台任务立刻返回，这里每 500 ms 拉一次内存状态直到出结果。
    //    为什么不在 UI 线程等：两个源各自 8 秒超时，最坏 16 秒，等到最后窗口就是假死。
    //    下载期间同样靠这个轮询画进度（后端读的是内存计数，不打网络）。

    private UpdateDto? _update;
    private DispatcherTimer? _updatePoll;
    private int _updatePollTicks;

    /// <summary>下载相关的三个瞬时态 —— 只有它们需要界面继续轮询。</summary>
    private static bool DlBusy(UpdateDownloadDto? d)
        => d?.State is "downloading" or "cancelling" or "verifying";

    /// <summary>
    /// 把 `/api/app/update` 快照画成作者卡底部那一块：**版本号 + 状态短标签 + 说明 + 按钮**。
    ///
    /// 为什么版本号和更新检查挤在同一块：它们是同一件事的两面（"我这一版" / "有没有下一版"），
    /// 拆到两张卡里就得来回看；而右边「应用信息」本就该只讲运行信息
    /// （机型 / 端口 / 启动时间 / 已运行），六项全堆那儿太挤。
    /// </summary>
    private void RenderUpdate(UpdateDto? u)
    {
        _update = u;

        if (u is null)
        {
            VerValue.Text = "—";
            VerValue.ToolTip = null;
            PaintUpdate(dot: "DimBrush", title: "DimBrush");
            UpdateTitle.Text = "";
            UpdateDesc.Text = "读不到后端状态，请重启控制中心";
            UpdateDesc.ToolTip = null;
            HideAlt();
            SetPrimary("检查更新", enabled: false, tip: null);
            StopUpdatePoll();
            return;
        }

        var latest = u.Latest;
        var current = string.IsNullOrEmpty(u.Current) ? "—" : u.Current!;
        VerValue.Text = current;
        VerValue.ToolTip = "当前版本 " + current;

        switch (u.State)
        {
            case "checking":
                PaintUpdate(dot: "AccBrush", title: "DimBrush");
                UpdateTitle.Text = "正在查询…";
                UpdateDesc.Text = "向 GitHub / Gitee 查一次最新发布";
                UpdateDesc.ToolTip = null;
                HideAlt();
                SetPrimary("检查中…", enabled: false, tip: "正在查询：两个源各 8 秒超时，最长 16 秒");
                StartUpdatePoll();
                return;      // 还在查：保持轮询，别在这里停表

            case "newer" when latest is not null:
                // 下载 / 校验 / 已下好这几态都在这里画；只有"还没下"才是纯提示
                if (RenderNewer(u, latest))
                {
                    StartUpdatePoll();
                    return;
                }
                break;

            case "latest":
                PaintUpdate(dot: "OkBrush", title: "OkBrush");   // 绿 = 这一路通了、且已是最新
                UpdateTitle.Text = "已是最新";
                UpdateDesc.Text = string.IsNullOrEmpty(u.CheckedAt)
                    ? "与更新源上的版本一致"
                    : "上次检查 " + u.CheckedAt;
                UpdateDesc.ToolTip = null;
                HideAlt();
                SetPrimary("检查更新", enabled: true, tip: "再查一次（自动检查每天一次）");
                break;

            case "empty":
                // 两家更新源都 404：还没对外发布过任何版本（仓库私有也是这个响应）。
                // 说法要中性、颜色也要中性 —— 这不是错误，别让人以为控制中心坏了。
                PaintUpdate(dot: "DimBrush", title: "DimBrush");
                UpdateTitle.Text = "还未发布";
                UpdateDesc.Text = "更新源上还没有发布过版本";
                UpdateDesc.ToolTip = "仓库可能仍为私有，或尚未创建 release；发布首个版本后此处会自动提示";
                HideAlt();
                SetPrimary("再查一次", enabled: true, tip: "尚未对外发布；发布首个版本后将在此显示新版本提示与下载入口");
                break;

            case "error":
                PaintUpdate(dot: "HotBrush", title: "HotBrush",
                            border: "HotBrush", background: "HotSoftBrush");
                UpdateTitle.Text = "检查失败";
                var why = ErrorText(u) ?? "更新源没给原因";
                UpdateDesc.Text = why;
                UpdateDesc.ToolTip = why;      // 一行放不下的部分靠悬停看
                HideAlt();
                SetPrimary("重试", enabled: true, tip: "重新查询 GitHub / Gitee");
                break;

            default:   // idle：进程刚起来、还没查过
                PaintUpdate(dot: "DimBrush", title: "DimBrush");
                UpdateTitle.Text = "还没检查";
                UpdateDesc.Text = "打开窗口时每天自动查一次";
                UpdateDesc.ToolTip = null;
                HideAlt();
                SetPrimary("检查更新", enabled: true,
                           tip: "向 GitHub 查一次最新发布（不通再走 Gitee）");
                break;
        }

        StopUpdatePoll();
    }

    /// <summary>
    /// 「有新版本」这一态的全部子状态：未下载 / 下载中 / 取消中 / 校验中 / 已就绪 / 安装中 / 下载失败。
    ///
    /// 返回 true = 还在动（下载 / 取消 / 校验），调用方要继续轮询；false = 停在等用户动手。
    ///
    /// ⚠️ 判断"这份下载是不是属于当前这个新版本"必须比 tag：用户可能先下过 v0.2.0，
    ///    之后更新源又发了 v0.3.0 —— 拿旧包去装就是把版本装回去。
    /// </summary>
    private bool RenderNewer(UpdateDto u, UpdateLatestDto latest)
    {
        var dl = u.Download;
        // 判定在 Core（UpdateCard）：这份下载属不属于当前这一版、停在哪个子状态。
        // 文案与配色仍在这里 —— Core 只回答"是哪一种"。
        var stage = UpdateCard.Stage(UpdateCard.SameRelease(dl?.Version, latest.Tag), dl?.State);
        var d = dl!;   // stage ≠ Newer 时必不为 null：SameRelease 要求版本能比上

        var lv = string.IsNullOrEmpty(latest.Version) ? "新版本" : latest.Version!;
        var pre = latest.Prerelease == true ? "（预发布）" : "";

        if (stage == UpdateCardStage.Downloading)
        {
            // 下载中是**唯一**该把整行点亮又带进度的时候：强调色 + 实心圆点，
            // 但按钮降级成普通样式（这时候主动作是"取消"，不是"继续"）。
            PaintUpdate(dot: "AccBrush", title: "AccBrush", border: "AccBrush", background: "AccSoftBrush");
            UpdateTitle.Text = $"正在下载 {d.Percent ?? 0}%";
            UpdateDesc.Text = $"{Humanize.BytesOrDash(d.Received)} / {Humanize.BytesOrDash(d.Total)} · {Humanize.Rate(d.Speed)}";
            UpdateDesc.ToolTip = d.File;
            SetPrimary("取消", enabled: true, tip: "停止下载并删掉半截文件");
            ShowAlt("发布页", latest.Url);
            return true;
        }

        if (stage == UpdateCardStage.Cancelling)
        {
            PaintUpdate(dot: "DimBrush", title: "DimBrush");
            UpdateTitle.Text = "正在取消…";
            UpdateDesc.Text = "停掉下载并删掉半截文件";
            UpdateDesc.ToolTip = null;
            SetPrimary("取消中…", enabled: false, tip: null);
            ShowAlt("发布页", latest.Url);
            return true;
        }

        if (stage == UpdateCardStage.Verifying)
        {
            PaintUpdate(dot: "AccBrush", title: "AccBrush", border: "AccBrush", background: "AccSoftBrush");
            UpdateTitle.Text = "正在校验…";
            UpdateDesc.Text = "核对 SHA256 与文件体积（几秒）";
            UpdateDesc.ToolTip = d.File;
            SetPrimary("校验中…", enabled: false, tip: null);
            ShowAlt("发布页", latest.Url);
            return true;
        }

        if (stage is UpdateCardStage.Ready or UpdateCardStage.Installing)
        {
            var installing = stage == UpdateCardStage.Installing;
            PaintUpdate(dot: "AccBrush", title: "AccBrush", border: "AccBrush", background: "AccSoftBrush",
                        accentButton: !installing);
            UpdateTitle.Text = installing ? "安装程序已启动" : $"已下载 {lv}{pre}";
            UpdateDesc.Text = installing
                ? "向导里点「安装」，写文件前控制中心会自动退出"
                : d.Verified == true
                    ? "SHA256 校验通过 · 点「安装」启动安装向导"
                    : "发布未提供 SHA256，只核对了体积 · 点「安装」";
            UpdateDesc.ToolTip = UpdateCard.ReadyTip(
                d.Target, d.Portable == true, d.File, d.ExpectedSha256, latest.Tag, latest.Version);
            SetPrimary(installing ? "再次打开" : "安装", enabled: true,
                       tip: installing ? "重新打开安装向导" : "校验过才允许安装；会弹管理员授权");
            ShowAlt("发布页", latest.Url);
            return false;
        }

        if (stage == UpdateCardStage.DownloadFailed)
        {
            PaintUpdate(dot: "HotBrush", title: "HotBrush", border: "HotBrush", background: "HotSoftBrush");
            UpdateTitle.Text = "下载失败";
            var why = DlError(d) ?? "更新源没给原因";
            UpdateDesc.Text = why;
            UpdateDesc.ToolTip = why;
            ShowAlt("发布页", latest.Url);
            SetPrimary(InstallerAsset(latest) is null ? "打开发布页" : "重试下载", enabled: true,
                       tip: "重新下载安装包");
            return false;
        }

        // ---- 还没下：正常的新版本提示 ----
        // 这一态是**唯一**值得把整行点亮的时候：强调色圆点 + 淡强调底 + 实心按钮。
        // 圆点**不脉冲** —— 脉冲表示"正在进行"，而"有新版本"是已经出结果、等你动手。
        PaintUpdate(dot: "AccBrush", title: "AccBrush", border: "AccBrush",
                    background: "AccSoftBrush", accentButton: true);
        UpdateTitle.Text = $"有新版本 {lv}{pre}";

        var bits = new List<string>();
        if (Humanize.Day(latest.PublishedAt) is { } day) bits.Add(day + " 发布");
        if (latest.Notes is { Length: > 0 } note) bits.Add(Humanize.OneLine(note, 48));
        UpdateDesc.Text = bits.Count > 0 ? string.Join(" · ", bits) : "点「下载」把安装包下到本机";
        // 说明只占一行，长的部分会被省略号吃掉 —— 完整发布说明挂 ToolTip
        UpdateDesc.ToolTip = latest.Notes is { Length: > 0 } full ? full : null;

        var exe = InstallerAsset(latest);
        SetPrimary(exe is null ? "打开发布页" : "下载", enabled: true,
                   tip: exe is null
                       ? "该版本没附安装包，只能到发布页手动下载"
                       : $"下载安装包（{(exe.Size is long sz && sz > 0 ? Humanize.BytesOrDash(sz) : "体积未知")}）并校验 SHA256");
        ShowAlt("发布页", latest.Url);
        return false;
    }

    /// <summary>下载失败的原因（DTO 里是 JsonElement?，可能为 null）。</summary>
    private static string? DlError(UpdateDownloadDto? dl)
        => dl?.Error is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    /// <summary>发布附件里的安装包：选包规则在 Core（优先 setup，其次第一个 exe）。</summary>
    private static UpdateLatestAssetsItemDto? InstallerAsset(UpdateLatestDto latest)
    {
        var assets = latest.Assets;
        if (assets is not { Count: > 0 }) return null;
        var idx = UpdateCard.PickInstallerIndex(assets.Select(a => a.Name).ToList());
        return idx < 0 ? null : assets[idx];
    }

    private void SetPrimary(string text, bool enabled, string? tip)
    {
        UpdateButton.Content = text;
        UpdateButton.IsEnabled = enabled;
        if (tip is null) UpdateButton.ClearValue(FrameworkElement.ToolTipProperty);
        else UpdateButton.ToolTip = tip;
    }

    /// <summary>显示次按钮（目前只有「发布页」）：地址缺失就整颗藏掉，别留一个点了没反应的控件。</summary>
    private void ShowAlt(string text, string? url)
    {
        var ok = !string.IsNullOrEmpty(url);
        UpdateAltButton.Content = text;
        UpdateAltButton.Tag = ok ? url : null;
        UpdateAltButton.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        UpdateAltButton.IsEnabled = ok;
    }

    private void HideAlt() => UpdateAltButton.Visibility = Visibility.Collapsed;

    /// <summary>
    /// 给这一块上状态色：圆点 + 状态标签字色 +（必要时）行底色/描边 + 按钮等级。
    ///
    /// ⚠️ 一律 <c>SetResourceReference</c> / <c>ClearValue</c>，**不要**直接赋 SolidColorBrush：
    ///    本地写死的画刷在切主题后会停在旧颜色上（CapBox / AutoBox 踩过同一个坑）。
    /// ⚠️ 只有「有新版本」「检查失败」两态才染底色与描边，其余 ClearValue 回样式默认 ——
    ///    本地值会压掉 SetRow 的 hover 描边 Trigger，还回去才有点击反馈。
    /// </summary>
    private void PaintUpdate(string dot, string title,
                             string? border = null, string? background = null,
                             bool accentButton = false)
    {
        UpdateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
        UpdateTitle.SetResourceReference(TextBlock.ForegroundProperty, title);

        // 原「正在查」的脉冲呼吸已删（2026-10-28 全部装饰动效下架）：那一态仍然靠
        // AccBrush 亮点 + 「正在检查…」文案区分，只是不再闪。
        UpdateDot.Opacity = dot == "DimBrush" ? 0.45 : 1;

        if (border is null) UpdateRow.ClearValue(Border.BorderBrushProperty);
        else UpdateRow.SetResourceReference(Border.BorderBrushProperty, border);

        if (background is null) UpdateRow.ClearValue(Border.BackgroundProperty);
        else UpdateRow.SetResourceReference(Border.BackgroundProperty, background);

        UpdateButton.Style = (Style)FindResource(accentButton ? "MiniButtonAccent" : "MiniButton");
    }

    /// <summary>
    /// 点按钮：**有新版时它只负责去下载**（一个控件只表达一件事），
    /// 其余状态都是"查一次"。
    /// </summary>
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var u = _update;
        var latest = u?.Latest;

        if (u?.State == "newer" && latest is not null)
        {
            var dl = u.Download;
            var same = dl is not null && latest.Tag is { Length: > 0 } tag
                       && string.Equals(dl.Version, tag, StringComparison.OrdinalIgnoreCase);

            if (same && dl!.State is "downloading" or "verifying")
            {
                await PostUpdateAsync("/api/app/update/cancel", "取消下载失败");
                return;
            }

            if (same && dl!.State is "ready" or "installing")
            {
                await InstallAsync(latest, dl);
                return;
            }

            // 发布里没有 exe 附件：只能去发布页（别让用户点一下只收到"没有安装包"的报错）
            if (InstallerAsset(latest) is null)
            {
                OpenUrl(latest.Url, "发布页");
                return;
            }

            await PostUpdateAsync("/api/app/update/download", "下载失败");
            return;
        }

        // 其余状态：查一次
        UpdateButton.IsEnabled = false;
        var result = await Task.Run(() => _api.Post("/api/app/update/check", new JsonObject()));
        if (!result.Ok)
        {
            Toast(result.Why("检查更新失败"), false);
            UpdateButton.IsEnabled = true;
            return;
        }

        _updatePollTicks = 0;
        RenderUpdate(_api.Update());     // 后端已置 checking，立刻切态并开始轮询
    }

    /// <summary>次按钮：打开发布页（地址在 Render 时写进 Tag，这里不再猜）。</summary>
    private void UpdateAlt_Click(object sender, RoutedEventArgs e)
        => OpenUrl(UpdateAltButton.Tag as string ?? _update?.Latest?.Url, "发布页");

    /// <summary>
    /// 起下载 / 取消下载。两者都是"后端只置状态、活儿在后台"，所以发完请求立刻重画状态。
    ///
    /// ⚠️ 后端回 200 也可能是失败（`ok:false` + error）—— 那不是 HTTP 错，是"这下下不了"，
    ///    所以统一从快照的 `download.error` 取人话，而不是只看 HTTP 结果。
    /// </summary>
    private async Task PostUpdateAsync(string path, string failLabel)
    {
        UpdateButton.IsEnabled = false;
        var r = await Task.Run(() => _api.Post(path, new JsonObject()));
        var now = _api.Update();
        RenderUpdate(now);

        var why = DlError(now?.Download);
        if (why is { Length: > 0 }) Toast(why, false);
        else if (!r.Ok) Toast(r.Why(failLabel), false);
    }

    /// <summary>
    /// 点「安装」：先确认，再交给后端提权起安装器。
    ///
    /// 为什么要确认：这一步会弹 UAC，并在向导点「安装」后让控制中心退出
    /// （风扇会交回 EC 固件，按固件自己的曲线调速）。一句话说清"会发生什么"比让人自己猜强。
    /// </summary>
    private async Task InstallAsync(UpdateLatestDto latest, UpdateDownloadDto dl)
    {
        var ver = string.IsNullOrEmpty(latest.Version) ? "新版本" : latest.Version!;
        var lines = new List<string>
        {
            "点「启动安装」会打开安装向导；向导一开始写文件，控制中心就会自动退出"
            + "（风扇会交回 EC 固件按它自己的曲线调速，装完可从开始菜单再打开）。",
        };
        if (dl.Target is { Length: > 0 } target) lines.Insert(0, "安装到：" + target);
        if (dl.Portable == true)
            lines.Insert(1, "当前这份不是安装版：装完会在上面的目录里多出一份，当前位置的文件不会被动。");

        var ok = await UiDialog.ConfirmAsync(
            this,
            $"安装 {ver}？",
            string.Join("\n\n", lines),
            okText: "启动安装");
        if (!ok) return;

        UpdateButton.IsEnabled = false;
        var r = await Task.Run(() => _api.Post("/api/app/update/install", new JsonObject()));
        var now = _api.Update();
        RenderUpdate(now);

        var why = DlError(now?.Download);
        if (why is { Length: > 0 }) Toast(why, false);
        else if (!r.Ok) Toast(r.Why("启动安装程序失败"), false);
        else Toast("安装程序已启动：在向导里点「安装」", true);
    }

    /// <summary>在浏览器里打开一个地址。非 https 一律拦下（更新源被劫持时这是第一道闸）。</summary>
    private void OpenUrl(string? url, string what)
    {
        if (string.IsNullOrEmpty(url))
        {
            Toast(what + "地址缺失", false);
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            Toast(what + "地址不是 https，已拦下：" + url, false);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
            Toast("已在浏览器打开" + what, true);
        }
        catch (Exception ex)
        {
            Toast("打不开浏览器：" + ex.Message, false);
        }
    }

    /// <summary>检查 / 下载期间每 500 ms 看一眼后端状态。快照是内存拼的，不打网络。</summary>
    private void StartUpdatePoll()
    {
        if (!IsLoaded) return;      // 卸载态别起表，切回来由 Loaded 接上
        if (_updatePoll is null)
        {
            _updatePoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _updatePoll.Tick += (_, _) =>
            {
                _updatePollTicks++;
                var now = _api.Update();

                // 下载不设上限：一个包在慢网里要十几分钟，超时判死只会把正在走的进度抹掉
                if (DlBusy(now?.Download))
                {
                    _updatePollTicks = 0;
                    RenderUpdate(now);
                    return;
                }

                if (now?.State != "checking")
                {
                    RenderUpdate(now);   // 出结果了：渲染 + 内部停表
                    return;
                }
                if (_updatePollTicks < 40) return;   // 20 秒还不出结果 = 后端那边卡住了

                StopUpdatePoll();
                SetPrimary("重试", enabled: true, tip: "重新查询 GitHub / Gitee");
                UpdateDesc.Text = "查询超时（网络不通或更新源无响应），请稍后重试";
            };
        }
        if (!_updatePoll.IsEnabled) _updatePoll.Start();
    }

    private void StopUpdatePoll()
    {
        _updatePoll?.Stop();
        _updatePollTicks = 0;
    }

    /// <summary>错误文案：`error` 在 DTO 里是 JsonElement?（采样时可能是 null）。</summary>
    private static string? ErrorText(UpdateDto u)
        => u.Error is { ValueKind: System.Text.Json.JsonValueKind.String } e ? e.GetString() : null;

    // ================================================================ 读取

    /// <summary>
    /// 重新拉一遍（再次进入本页时由 MainWindow 调）——
    /// 日志尾巴在长、区间记录状态可能变了。
    /// </summary>
    public void Refresh()
    {
        LoadAbout();
        LoadUsage();
        RenderUpdate(_api.Update());
    }

    private void LoadAbout() => Offload(() => _api.About(), RenderAbout);

    private void LoadUsage() => Offload(() => _api.LogUsage(), RenderUsage);

    /// <summary>
    /// 记录状态对时（每 2 秒一次，见 <see cref="_pollTimer"/>）：自动档会**自己**开一段、
    /// 自己收一段，界面必须跟上，否则那两格会挂着早就不成立的状态
    /// （"记录中 12:31"，其实早就导出完了）。
    ///
    /// ⚠️ 只重画那两格：日志尾巴 / 占用 / 更新状态不在轮询里刷 —— 它们只在切页与写操作后拉一次，
    ///    每 2 秒读一遍日志文件不是这个页面该付的代价。
    /// </summary>
    private void SyncCapture()
    {
        if (_busy) return;              // 有写操作在飞：别拿（提交前的）旧状态盖掉刚回来的结果
        var cap = _api.Capture();
        if (cap is null) return;        // 纯桩模式（没有 AppState）：保持原样
        RenderCapture(cap);
        RenderAuto(cap);
    }

    /// <summary>
    /// 后台读一次、回 UI 线程渲染。三种情况都要吞掉：窗口已关、后台抛异常、
    /// 读不到（null）—— 否则每次关窗都会在日志里留一条"看起来像 bug"的异常。
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

    private void RenderAbout(AboutDto? v)
    {
        if (v is null)
        {
            RuntimeTag.Text = "读不到";
            LogPath.Text = "读取失败：后端可能还没就绪（重启控制中心后再试）";
            LogTagBox.Style = (Style)FindResource("CardTagWarn");
            LogTag.Text = "未知";
            LogTag.Style = (Style)FindResource("CardTagTextWarn");
            CapButton.IsEnabled = false;
            return;
        }

        _about = v;

        // ---------------- 应用信息 ----------------
        RuntimeTag.Text = v.Runtime ?? "--";
        _devValue.Text = (string.IsNullOrEmpty(v.Device) ? "未知机型" : v.Device!)
            + (v.DeviceVerified == true ? " · 已验证" : " · 未验证");
        _devValue.ToolTip = _devValue.Text;
        _portValue.Text = v.Port?.ToString(CultureInfo.InvariantCulture) ?? "--";
        _startedValue.Text = string.IsNullOrEmpty(v.Started) ? "--" : v.Started!;
        // 「已运行」不直接落文本：这一页的数据只在切进来时拉一次，而"已运行"是**随时在走**的。
        // 直接落一次的结果是——开机就停在关于页时永远显示「0 秒」（2026-10-31 截图发现），
        // 或者盯着一页看十分钟它还是「1 分」。这里记下基准（后端给的秒数 + 当时的单调时钟），
        // 由每秒一拍自己往后数（见 RenderUptime）。
        _upBaseSec = v.UptimeS ?? -1;
        _upBaseTick = Environment.TickCount64;
        RenderUptime();

        // ---------------- 机型适配卡 ----------------
        RenderAdopt(v.DeviceVerified == true);

        // ---------------- 日志卡：总开关 ----------------
        // 开关读 `enabled`（用户要不要写），状态标签读 `active` / `healthy`（到底写没写）——
        var log = v.Log ?? new AboutLogDto();
        // 两件事分开说：目录不可写时开关仍是开、标签写「写入失败」，
        // 否则用户点了开关会被弹回"关"，以为开关坏了。
        var logOn = log.Enabled == true;
        var offReason = log.OffReason;   // 后端给的原文（"日志总开关已关闭" / "--no-log" / 目录不可写）
        LogSwitch.IsOn = logOn;
        LogSwitchHint.Text = logOn
            ? "程序输出同时写入日志目录（按天分文件，保留 7 天）"
            : "关闭中：不再写日志文件；打开后从下一行开始记，历史文件保留";

        // ---------------- 日志卡：文件现状 ----------------
        var active = log.Active == true;
        var healthy = log.Healthy == true;
        LogTagBox.Style = (Style)FindResource(!logOn ? "CardTag"
            : healthy ? "CardTagOk" : "CardTagWarn");
        LogTag.Style = (Style)FindResource(!logOn ? "CardTagText"
            : healthy ? "CardTagTextOk" : "CardTagTextWarn");
        LogTag.Text = !logOn ? "已关闭" : !active ? "未启用" : healthy ? "已启用" : "写入失败";

        var path = Str(log.Path);
        var file = Str(log.File);
        LogPath.Text = active
            ? $"{(string.IsNullOrEmpty(file) ? "（今天还没有内容）" : file)} · {log.Dir ?? ""}"
            : "没有在写 · " + (string.IsNullOrEmpty(offReason) ? "日志未启用" : offReason!);
        LogPath.ToolTip = path ?? log.Dir ?? "";

        LogSize.Text = active ? Humanize.TryBytes(log.Bytes) : "";
        LogTail.Text = log.Tail is { Count: > 0 } tail
            ? string.Join("\n", tail)
            : active ? "（今天的日志文件还没有内容）"
            : logOn ? "（日志没有在写：目录写不进去）" : "（日志已关闭）";
        TailScroll.ScrollToEnd();       // 看最新的一条要滚到底
        OpenButton.IsEnabled = !string.IsNullOrEmpty(log.Dir);

        // ---------------- 区间记录 ----------------
        RenderCapture(v.Capture ?? new AboutCaptureDto());
        // ---------------- 自动记录 ----------------
        RenderAuto(v.Capture ?? new AboutCaptureDto());
        // ---------------- 日志没在写 ⇒ 记录类控件一律禁用 ----------------
        ApplyLogGate(active && healthy, offReason);
    }

    // ---------------------------------------------------------------- 区间记录

    private void RenderCapture(AboutCaptureDto cap)
    {
        _capActive = cap.Active == true;
        _capAuto = cap.AutoRunning == true;

        // ⚠️ 这一格**只说手动那一段**（2026-11-08 用户报：没有温度数据时，自动记录一开，
        //    左边「手动记录」也跟着变"记录中"）。原来两格共用一个"当前这一段"的渲染：
        //    自动档自己开的那段会写进左格（状态字 / 绿点 / 描边 / 按钮变「结束并导出」），
        //    于是看起来就是"手动记录被自动记录打开了"。
        //    现在各说各的：自动段只在右边那格显示（见 RenderAuto）。
        var manualOn = _capActive && !_capAuto;
        var autoOn = _capActive && _capAuto;

        // 状态点/边框也走 DynamicResource：抓 PaletteBrush 实例切主题后会留在旧色上。
        // ⚠️ 与右边「自动记录」用**同一套信号**（活跃 = OkBrush 实心点 + 同色描边，
        //    不活跃 = DimBrush 半透明 + LineBrush）：两块是同一类东西，别一边红一边绿。
        CapBox.SetResourceReference(Border.BorderBrushProperty, manualOn ? "OkBrush" : "LineBrush");
        CapDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            manualOn ? "OkBrush" : "DimBrush");
        CapDot.Opacity = manualOn ? 1 : 0.35;
        // 呼吸脉冲已删（2026-10-28 用户要求删掉全部装饰动效）：「正在录」靠颜色
        // （OkBrush 实心点 + 边框）表达，不再闪。真在录这一点看计时器更准。

        CapButton.Content = manualOn ? "结束并导出" : "开始记录";
        if (!manualOn) CapState.Text = "手动记录 · 未开始";    // 记录中由计时器每秒刷

        // 计时起点：后端给的 since（本机本地时间）；解析不了就按秒数倒推
        _capSince = DateTime.TryParse(Str(cap.Since), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var since)
            ? since
            : DateTime.Now - TimeSpan.FromSeconds(cap.Seconds ?? 0);

        if (_capActive)
        {
            // Offload 回读可能在页面已经切走之后才回来：卸载态不许把计时器点着
            if (IsLoaded) _capTimer.Start();
            RenderCapStateText();
        }
        else
        {
            _capTimer.Stop();
        }
        UpdateCapEnabled();

        // 两块的说明统一成一句：**怎么开始 → 怎么结束 → 产物**，都用"存成文件"收尾。
        // ⚠️ 每句控制在 ~26 字内（一行放得下）：左盒右侧还有按钮，可用宽度只有 ~330px，
        //    多两个换行出来的字就是"最后一行只剩一个字"那种难看。
        // ⚠️ **不报"已存几段"**（2026-10-29 用户："不用记存了几段"）：段数是开发者视角的记账，
        //    用户只关心"这一段怎么标、存哪去了"。后端 seq 还在（日志标记 #N + 导出文件防撞名），
        //    只是不再往界面上搬。
        CapHint.Text = manualOn
            ? "正在写入当前日志；点「结束并导出」单独存成文件"
            : autoOn
                ? "自动记录正在录这一段，关掉右边的开关即结束"
                : cap.Auto == true
                    ? "点「开始记录」标出这一段，自动档也会自己开一段"
                    : "点「开始记录」标出这一段，结束时单独存成文件";

        var last = cap.Last;
        if (last is { File: { } lf })
        {
            CapLast.Visibility = Visibility.Visible;
            CapLastText.Text = "最近导出 " + lf
                + $" · {(last.Lines?.ToString(CultureInfo.InvariantCulture)) ?? "--"} 行"
                + $" · {Humanize.TryBytes(last.Bytes) ?? "--"}";
            CapLast.ToolTip = last.Path;
        }
        else
        {
            CapLast.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>「&lt;模式&gt;记录 · 记录中 mm:ss」—— 记录中每秒刷一次。
    /// ⚠️ 自动段**只写右边那格**，左边保持「手动记录 · 未开始」；手动段反之。
    ///    两格各表达自己那种记录，别再把自动段写成"手动记录 · 记录中"。</summary>
    private void RenderCapStateText()
    {
        if (!_capActive) return;
        var sec = Math.Max(0, (DateTime.Now - _capSince).TotalSeconds);
        if (_capAuto)
        {
            AutoState.Text = "自动记录 · 记录中 " + Humanize.Clock((int)sec);
            return;
        }
        CapState.Text = "手动记录 · 记录中 " + Humanize.Clock((int)sec);
    }

    /// <summary>
    /// 日志没在写（总开关关着 / 目录写不进去）时，「开始记录」和「自动记录」都不会有任何产出 ——
    /// 直接禁用并写明原因，比让用户点了再弹一句"没有日志可截"干净（一个控件只表达一件事）。
    ///
    /// ⚠️ 只闸这三件跟"写"有关的控件：清理、打开目录、总开关本身都不受影响 ——
    ///    它们操作的是**磁盘上已经存在的文件**，日志关着也该能用。
    /// ⚠️ **正在记的那一段例外**（2026-10-29 修）：sink 中途写坏（盘满 / 权限变了）时
    ///    `active` 还是 true、`healthy` 已经 false —— 这时若把「结束并导出」也禁用，
    ///    这一段就**永远收不了尾**（按钮灰着，只能重启，而重启等于把这段丢掉）。
    ///    开始要闸、结束不闸：结束只读文件 + 写一个导出文件，不依赖 sink。
    /// ⚠️ 同一条例外也适用于**自动档的开关**（2026-11-08）：自动段在录时，手动那颗按钮
    ///    是灰的（不是它那一段），此时若把开关也禁用，就没有任何入口能结束这一段了 ——
    ///    关掉开关正是「立刻收尾并导出」（见 LogCapture.SetAuto）。反向（打开）不需要例外：
    ///    sink 坏着时 StartLocked 本来就不会开录。
    /// </summary>
    private void ApplyLogGate(bool ready, string? reason)
    {
        _logReady = ready;
        UpdateCapEnabled();
        AutoSwitch.IsEnabled = ready || (_capActive && _capAuto);
        AutoTempSlider.IsEnabled = ready;
        if (ready || _capActive) return;

        var why = string.IsNullOrEmpty(reason) ? "日志没在写" : reason;
        CapHint.Text = $"{why}；先打开上面的「写日志文件」";
        AutoHint.Text = $"{why}；先打开上面的「写日志文件」";
    }

    /// <summary>
    /// 「开始记录」这颗按钮什么时候可点 —— 三条规则的唯一出口。
    ///
    /// ① 日志没在写 ⇒ 不许**开始**（没有任何产出）；
    /// ② 正在记的那一段**永远可以结束**：结束只读文件 + 写一个导出文件，不依赖 sink；
    /// ③ 自动档正在录时不许再手动开一段（后端也会拒："已经在记录了"）——
    ///    按钮灰着、说明由 <see cref="RenderCapture"/> 写在 hint 里，别让用户点了才挨一句错。
    /// </summary>
    private void UpdateCapEnabled()
    {
        var manualOn = _capActive && !_capAuto;
        CapButton.IsEnabled = !_busy && (manualOn || (!_capActive && _logReady));
    }

    // ---------------------------------------------------------------- 自动记录

    private void RenderAuto(AboutCaptureDto cap)
    {
        var on = cap.Auto == true;
        // 自动档**这一刻是不是正在录一段**（与"开关开着"是两件事：开着也可能没触发）
        var running = cap.AutoRunning == true;
        var temp = (int)(cap.AutoTemp ?? 90);

        // ⚠️ 点的颜色只表达一件事：**这一刻有没有在记录**（2026-10-29 用户问「怎么手动是灰点、
        //    自动是绿点」）。原来这里的点跟"开关开着"走，左边跟"正在录"走 —— 同一张卡里
        //    两个点两种语义，看着就是"凭什么它绿我灰"。开关状态由标题文字 + 开关自己表达，
        //    不占这个点；两块统一：绿 = 正在写这一段，灰 = 没在写。
        AutoBox.SetResourceReference(Border.BorderBrushProperty, running ? "OkBrush" : "LineBrush");
        AutoDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            running ? "OkBrush" : "DimBrush");
        AutoDot.Opacity = running ? 1 : 0.35;
        // 同上：呼吸脉冲已删。

        // 「记录中 mm:ss」由计时器每秒刷（RenderCapStateText）：自动段**只在这一格**显示，
        // 不再借左边「手动记录」那格。起点优先用 RenderCapture 记下的 since，没有就退回后端秒数。
        var secs = _capSince == default
            ? (int)(cap.Seconds ?? 0)
            : (int)Math.Max(0, (DateTime.Now - _capSince).TotalSeconds);
        AutoState.Text = running
            ? "自动记录 · 记录中 " + Humanize.Clock(secs)
            : on ? $"自动记录 · 开（≥{temp}°C）" : "自动记录 · 关";

        // 与左边手动那块同一句式：怎么开始 → 怎么结束 → 产物。
        // ⚠️ 正在录的那几秒换成「这一次是为什么开的」（2026-11-08 用户报「没到温度又开始记录了」：
        //    真正触发的是 HWiNFO 那条 ERR，而界面只有"记录中"，看着就像阈值算错了）。
        //    这一行只放短句：右侧钉着开关、盒子只有半幅宽，长句会折成两行把整张卡顶高。
        //    完整说法（回落后自动结束并存成文件）放 ToolTip。
        var why = cap.AutoReason;
        if (running)
        {
            AutoHint.Text = "本次自动触发：" + (string.IsNullOrEmpty(why) ? "已触发" : why);
            AutoHint.ToolTip = "达到阈值或出现新的错误即自动开始这一段的记录；回落后自动结束并存成 capture-*.log";
        }
        else
        {
            AutoHint.Text = on
                ? $"达到 {temp}°C 或出现新错误即自动记一段，回落后自动结束并存成文件"
                : "打开后，达到阈值或出现新错误即自动记一段，回落后自动结束并存成文件";
            AutoHint.ToolTip = null;
        }

        AutoSwitch.IsOn = on;

        // 滑块真值回写：闸住 ValueChanged，否则每次拉数据都会走一遍「防抖 → 提交」。
        // 正在拖的时候不回写（否则拉到一半被刷回旧值，两边打架），与背光档位同一处理。
        if (AutoTempSlider.IsMouseCaptureWithin) return;

        _autoTempSyncing = true;
        try
        {
            AutoTempSlider.Value = Math.Clamp(temp, (int)AutoTempSlider.Minimum, (int)AutoTempSlider.Maximum);
            AutoTempValue.Text = temp.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _autoTempSyncing = false;
        }
    }

    // ---------------------------------------------------------------- 占用

    private void RenderUsage(LogUsageDto? u)
    {
        if (u is null || u.Ok != true)
        {
            LogUsage.Text = "读取失败：后端可能未启动，请重启控制中心";
            ClearButton.IsEnabled = false;
            return;
        }

        // 采集包躺在同一个 logs\ 里，但**不是日志**：不单列的话这行会报"共 0 B · 1 个文件"，
        // 而目录里已经堆了几百 KB 的 adopt-*，用户看到的就是"清理清不掉的东西越积越多"。
        var logFiles = u.Files ?? 0;
        var logBytes = u.Bytes ?? 0;
        var packFiles = u.PackFiles ?? 0;
        var packBytes = u.PackBytes ?? 0;

        var parts = new List<string>
        {
            $"共 {Humanize.TryBytes(logBytes + packBytes) ?? "0 B"} · {logFiles + packFiles} 个文件",
        };
        if (u.CcFiles is { } ccf && ccf > 0) parts.Add($"日志 {ccf} 个（{Humanize.TryBytes(u.CcBytes)}）");
        if (u.OldFiles is { } of && of > 0) parts.Add($"轮转旧档 {of} 个（{Humanize.TryBytes(u.OldBytes)}）");
        if (u.CapFiles is { } cf && cf > 0) parts.Add($"区间导出 {cf} 个（{Humanize.TryBytes(u.CapBytes)}）");
        if (packFiles > 0) parts.Add($"采集包 {packFiles} 个（{Humanize.TryBytes(packBytes)}）");
        // 机型档案**不在**"共 N 个文件"里（那在 devices\，不在这条目录下）：单独说
        if (u.ProfileFiles is { } pf && pf > 0)
            parts.Add($"机型档案 {pf} 个（{Humanize.TryBytes(u.ProfileBytes)}，另存）");
        LogUsage.Text = string.Join(" · ", parts);
        LogUsage.ToolTip = u.ProfileFiles is { } n && n > 0
            ? u.Dir + "\n机型档案：" + u.ProfileDir
            : u.Dir;

        // 日志清空了但还留着采集包 / 本机档案时按钮仍要可点 —— 否则"清理它们"没有入口
        ClearButton.IsEnabled = logFiles > 0 || packFiles > 0 || (u.ProfileFiles ?? 0) > 0;
    }

    // ================================================================ 写入

    /// <summary>开始 / 结束（同一颗按钮两态）：状态以**后端返回**为准，不靠界面自己翻。</summary>
    private async void Cap_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var stopping = _capActive;
        _busy = true;
        CapButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => _api.Post(
                stopping ? "/api/app/log/capture/stop" : "/api/app/log/capture/start", new JsonObject()));

            if (!result.Ok)
            {
                Toast(result.Why(stopping ? "结束记录失败" : "开始记录失败"), false);
                // 失败也要把状态对齐：后端说没在记就回到"未在记录"，别卡在"记录中"
                if (result.Raw?["capture"] is JsonObject cap)
                    RenderCapture(cap.Deserialize<AboutCaptureDto>(ApiResponse.JsonOptions) ?? new AboutCaptureDto());
                else LoadAbout();
                return;
            }

            if (stopping)
            {
                var ex = result.Raw?["export"];
                var file = ex?["file"] is JsonValue fv && fv.TryGetValue(out string? f) ? f : "日志";
                var lines = ex?["lines"]?.ToString();
                Toast($"已导出 {file}（{(lines ?? "?")} 行）", true);
            }
            else
            {
                Toast("已开始记录：复现问题后按「结束并导出」", true);
            }

            if (result.Raw?["capture"] is JsonObject cap2)
                RenderCapture(cap2.Deserialize<AboutCaptureDto>(ApiResponse.JsonOptions) ?? new AboutCaptureDto());
            else LoadAbout();
            LoadUsage();     // 顺手对齐占用（导出会多一个文件）
        }
        finally
        {
            _busy = false;
            UpdateCapEnabled();   // 日志关着时别顺手把它又点亮；自动段在录时也不许手动再开一段
        }
    }

    /// <summary>
    /// 日志总开关：关 = 后端立刻放开文件句柄（此后一行都不落盘），开 = 立刻接着写。
    /// 状态**以返回值为准**（成功后重新拉一次 about），开关不自己翻 —— 与自动记录那颗同一纪律。
    /// 关掉不删任何历史文件，要腾空间请用下面的「清理」。
    /// </summary>
    private async void LogSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var want = !LogSwitch.IsOn;
        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/app/settings",
                new JsonObject { ["log_enabled"] = want }));
            var note = result.Raw?["note"] is JsonValue nv && nv.TryGetValue(out string? ns) ? ns : null;
            Toast(result.Ok ? (note ?? (want ? "已开始写日志" : "已停止写日志"))
                : result.Why("切换日志开关失败"), result.Ok);
        }
        finally
        {
            _busy = false;
            LoadAbout();      // 无论成败都回到真值
        }
    }

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var want = !AutoSwitch.IsOn;
        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/app/settings",
                new JsonObject { ["log_auto"] = want }));
            var note = result.Raw?["note"] is JsonValue nv && nv.TryGetValue(out string? ns) ? ns : null;
            Toast(result.Ok ? (note ?? (want ? "已开启自动记录" : "已关闭自动记录"))
                : result.Why("自动记录保存失败"), result.Ok);
        }
        finally
        {
            _busy = false;
            LoadAbout();      // 无论成败都回到真值
        }
    }

    /// <summary>
    /// 触发温度滑块：只改读数，停手 350ms 才提交（拖动中每一格都发 POST 会把
    /// presets.log 写爆，而且返回的快照会把滑块拽回去 —— 与背光档位同一个处理）。
    /// </summary>
    private void AutoTemp_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var t = (int)Math.Round(AutoTempSlider.Value);
        AutoTempValue.Text = t.ToString(CultureInfo.InvariantCulture);
        if (_autoTempSyncing) return;

        _autoTempTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _autoTempTimer.Stop();
        _autoTempTimer.Tick -= AutoTempTimer_Tick;
        _autoTempTimer.Tick += AutoTempTimer_Tick;
        _autoTempTimer.Start();
    }

    private void AutoTempTimer_Tick(object? sender, EventArgs e)
    {
        _autoTempTimer?.Stop();
        if (_busy) { _autoTempTimer?.Start(); return; }   // 有别的写在跑：下一拍再试
        _ = CommitAutoTempAsync((int)Math.Round(AutoTempSlider.Value));
    }

    private async Task CommitAutoTempAsync(int t)
    {
        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/app/settings",
                new JsonObject { ["log_auto_temp"] = t }));
            var back = result.Raw?["log_auto_temp"]?.ToString();
            Toast(result.Ok ? $"触发温度：{back ?? t.ToString(CultureInfo.InvariantCulture)}°C"
                : result.Why("触发温度保存失败"), result.Ok);
        }
        finally
        {
            _busy = false;
            LoadAbout();      // 无论成败都回到真值
        }
    }

    /// <summary>
    /// 清理：先拉实时占用 → 确认框摆出"删几个、多大" → 才真删（删除不可逆）。
    ///
    /// 采集包（logs\adopt-*）与机型档案（devices\*.json）**默认都不清**：前者是要发给作者的
    /// 交付物，后者是本机采出来的偏移 —— 只有确认框里明确选"都清"才带上
    /// `packs:true` / `profiles:true`。
    /// 档案目录是源码仓库时后端会拒（那是手改真源），这里连选项都不摆。
    /// ⚠️ 采集包原来**根本清不掉**（只认三类日志文件，界面还报 0 B，2026-11-08 用户报）。
    /// </summary>
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearButton.IsEnabled = false;
        LogUsageDto? usage;
        try
        {
            usage = await Task.Run(() => _api.LogUsage());
        }
        catch
        {
            usage = null;
        }
        ClearButton.IsEnabled = true;

        if (usage is null || usage.Ok != true)
        {
            Toast("读取日志占用失败，请稍后重试", false);
            return;
        }

        var cap = usage.CapFiles is { } cf && cf > 0 ? $"（其中区间导出 {cf} 个）" : "";
        var logFiles = usage.Files ?? 0;
        var logText = $"日志：{logFiles} 个文件，合计 {Humanize.TryBytes(usage.Bytes) ?? "0 B"}{cap}。\n"
                      + "会删掉 logs 目录里的三类文件：当天与历史日志（cc-*.log）、轮转旧档（cc-*.old.log）、"
                      + "区间记录导出（capture-*.log）。正在写的那份当天日志也会被清掉，随后立刻新建一份空的继续写。";

        var profFiles = usage.ProfileFiles ?? 0;
        var profCleanable = usage.ProfileCleanable != false;
        var packFiles = usage.PackFiles ?? 0;
        var withProfiles = false;
        var withPacks = false;

        var hasProfiles = profFiles > 0 && profCleanable;
        var hasPacks = packFiles > 0;

        if (hasPacks || hasProfiles)
        {
            // 三个问题一次问清：只清日志（保守默认） / 连采集包、档案一起清 / 取消
            // ⚠️ 默认必须是「只清日志」：采集包是要发给作者的交付物、档案是本机标定结果，
            //    回车就把它们删掉是绝不能接受的默认态。
            var extra = new System.Text.StringBuilder(logText);

            if (hasPacks)
            {
                var names = usage.PackNames ?? [];
                var list = string.Join("\n", names.Take(4).Select(n => "  · " + n));
                if (names.Count > 4) list += $"\n  · 另有 {names.Count - 4} 个";
                extra.Append("\n\n采集包：").Append(packFiles)
                     .Append(" 个，合计 ").Append(Humanize.TryBytes(usage.PackBytes) ?? "0 B")
                     .Append("（logs\\adopt-*.json 与同名 .txt —— 「一键采集」生成、要发给作者的那份）")
                     .Append(list.Length > 0 ? "\n" + list : "");
            }

            if (hasProfiles)
            {
                var names = usage.ProfileNames ?? [];
                var list = string.Join("\n", names.Take(4).Select(n => "  · " + n));
                if (names.Count > 4) list += $"\n  · 另有 {names.Count - 4} 个";
                extra.Append("\n\n机型档案：").Append(profFiles)
                     .Append(" 个，合计 ").Append(Humanize.TryBytes(usage.ProfileBytes) ?? "0 B")
                     .Append("（一键采集给本机存的温度/转速偏移）")
                     .Append(list.Length > 0 ? "\n" + list : "")
                     .Append("\n删掉后要重启控制中心才会回落到内置档案；重启之前仍在用内存里那份。");
            }

            extra.Append("\n\n删除后都不可恢复。");

            var allLabel = hasPacks && hasProfiles ? "全部清掉"
                : hasPacks ? "日志和采集包都清"
                : "日志和档案都清";

            var pick = await UiDialog.ChooseAsync(this, "清理日志，还是连采集包/档案一起清？",
                extra.ToString(),
                [
                    ("logs", "只清日志", true, false),
                    ("all", allLabel, false, true),
                    ("cancel", "取消", false, false),
                ]);
            if (pick is null or "cancel") return;
            withPacks = pick == "all" && hasPacks;
            withProfiles = pick == "all" && hasProfiles;
        }
        else
        {
            // 没有采集包 / 档案要清（或档案在源码目录里不给清）：维持原来的二选一
            var profNote = profFiles > 0 && !profCleanable
                ? $"\n\n机型档案 {profFiles} 个在源码仓库目录里，本次不会动它们。"
                : "";
            var go = await UiDialog.ConfirmAsync(this, "清理日志文件？",
                logText + "\n\n删除后不可恢复。" + profNote,
                "确认清理", "取消", danger: true);
            if (!go) return;
        }

        _busy = true;
        try
        {
            var result = await Task.Run(() => _api.Post("/api/app/log/clear",
                new JsonObject { ["packs"] = withPacks, ["profiles"] = withProfiles }));
            if (result.Ok)
            {
                var deleted = Long(result.Raw?["deleted"]);
                var freed = Long(result.Raw?["freed"]);
                var packDeleted = Long(result.Raw?["packs_deleted"]);
                var packFreed = Long(result.Raw?["packs_freed"]);
                var profDeleted = Long(result.Raw?["profiles_deleted"]);
                var profFreed = Long(result.Raw?["profiles_freed"]);

                // 回执要把"删了几类"说全 —— 只报日志会让用户以为采集包还躺在那里
                var what = new List<string> { $"{deleted} 个日志" };
                if (packDeleted > 0) what.Add($"{packDeleted} 个采集包");
                if (profDeleted > 0) what.Add($"{profDeleted} 个机型档案");

                var tail = profDeleted > 0 ? "；重启后档案才回落" : "";
                Toast($"已清理 {string.Join(" + ", what)}，"
                      + $"释放 {Humanize.TryBytes(freed + packFreed + profFreed) ?? "0 B"}{tail}", true);

                // 采集包删掉了就别再在界面上指着它（「提交采集包」那个按钮会指向不存在的文件）
                if (packDeleted > 0 && _adoptPackPath is { Length: > 0 } pack
                    && !System.IO.File.Exists(pack))
                {
                    _adoptPackPath = null;
                    AdoptSubmitButton.Visibility = Visibility.Collapsed;
                    AdoptHint.Text = "采集包已随这次清理删除 —— 要发给作者的话请重新采集。";
                }

                if (profDeleted > 0)
                {
                    AdoptRestartButton.Visibility = Visibility.Collapsed;
                    AdoptState.Text = "本机档案已清理（重启后回落内置档案）";
                }

                // 后端拒了档案/没删掉（源码目录 / 被占用）：日志已经清了，这里只补一句实话
                if (Text(result.Raw?["profile_error"]) is { Length: > 0 } why)
                    Toast(why, false);
                if (Text(result.Raw?["packs_error"]) is { Length: > 0 } pwhy)
                    Toast(pwhy, false);
            }
            else
            {
                Toast(result.Why("清理失败"), false);
            }
        }
        finally
        {
            _busy = false;
            LoadUsage();
            LoadAbout();
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var result = await Task.Run(() => _api.Post("/api/app/log/open", new JsonObject()));
        if (!result.Ok) Toast(result.Why("打不开日志目录"), false);
    }

    // ================================================================ 小工具

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);

    /// <summary>JsonElement? → 字符串（log.path/file、capture.since 采样时可能是 null）。</summary>
    private static string? Str(System.Text.Json.JsonElement? el)
        => el is { } e && e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : null;

    /// <summary>
    /// JsonNode? → 字符串。
    /// ⚠️ 与 <see cref="Str"/> 分开：`ActResult.Raw` 是 **JsonObject**（JsonNode 家族），
    ///    不是 JsonElement —— 混用会得到一串"无法从 JsonNode 转换为 JsonElement?"的编译错。
    /// </summary>
    private static string? Text(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// JsonNode? → long。数字节点可能是 int / long / double（后端用哪个类型装箱不一定），
    /// 所以按**字面量**解析，不用 `TryGetValue&lt;long&gt;`（强类型节点会抛）。
    /// </summary>
    private static long Long(JsonNode? n)
        => n is not null
           && long.TryParse(n.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;

    /// <summary>
    /// 把「已运行」按基准 + 单调时钟算出来。基准是**后端给的秒数**，不是本页打开的时刻 ——
    /// 这样切页拉到的快照与秒表严格一致，不会因为"这一页什么时候构造的"而错位。
    /// 时钟用 <see cref="Environment.TickCount64"/>（不是 DateTime.Now）：改系统时间/夏令时不会把它算歪。
    /// </summary>
    private void RenderUptime()
    {
        if (_upBaseSec < 0) { _uptimeValue.Text = "--"; return; }
        var extra = (Environment.TickCount64 - _upBaseTick) / 1000;
        _uptimeValue.Text = Humanize.Uptime(_upBaseSec + Math.Max(0, extra));
    }
}
