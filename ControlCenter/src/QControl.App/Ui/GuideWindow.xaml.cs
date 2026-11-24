using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using QControl.Core.Devices;

namespace QControl.App.Ui;

/// <summary>
/// 「引导适配」的置顶小窗。**只管显示与转发点击**：步骤定义、时间窗切分、能力认定
/// 全在 <see cref="AdoptGuide"/> 和 <see cref="GuideSession"/> 里，这里一行推断都不做。
///
/// 数据靠 300ms 轮询 <c>LocalApi.GuideView</c> 拿，不做回调、不让观察线程碰 UI ——
/// 与 <c>AboutView</c> 的阶段进度同一条规矩（宁可只报真事，也不编百分比）。
/// </summary>
internal sealed partial class GuideWindow : Window
{
    private readonly LocalApi? _api;
    private readonly Func<JsonObject?> _view;
    private readonly DispatcherTimer _poll;

    private int _index = -1;
    private int _frames;
    /// <summary>当前步骤 0 帧时已经点过一次「完成这步」：再点一次才真的提交。</summary>
    private bool _emptyConfirm;
    /// <summary>刚点过「再等 60 秒」：在那之后的几秒里把这句留在提示行上，别让用户以为没点着。</summary>
    private DateTimeOffset _extendedUntil = DateTimeOffset.MinValue;

    /// <summary>五段步骤条的格子（构造时按 XAML 顺序填）。</summary>
    private Border[] _segs = [];

    /// <summary>已经启动脉动动画的那一格（-1 = 没有）。见 <see cref="PaintStepBar"/>。</summary>
    private int _pulseIndex = -1;

    public GuideWindow(LocalApi api) : this(() => api.GuideView)
    {
        _api = api;
    }

    private GuideWindow(Func<JsonObject?> view)
    {
        InitializeComponent();
        UiTheme.AttachPalette(this);
        _view = view;
        _segs = [StepSeg1, StepSeg2, StepSeg3, StepSeg4, StepSeg5];

        // 默认落在工作区右上角：原厂控制中心一般居中，这样不挡它。
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left + 8, area.Right - Width - 24);
        Top = Math.Max(area.Top + 8, area.Top + 24);

        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();
        Refresh();
    }

    /// <summary>截图 / 自检用的固定快照：不接真数据源，按钮点了也没人收事件。</summary>
    internal GuideWindow(JsonObject demoView) : this(() => demoView)
    {
    }

    /// <summary>关窗时把轮询停掉，别让计时器把这个 Window 钉在内存里。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        // ⚠️ 必须把 `RepeatBehavior.Forever` 的脉动动画摘掉：WPF 的动画时钟由静态
        //    计时管理器持有、**不随窗口关闭停止**，留着它就把整棵可视树钉到进程结束
        //    （每秒还在按帧 tick）。AboutView.HideAdoptProgress 也是这么收的。
        foreach (var seg in _segs) seg.BeginAnimation(OpacityProperty, null);
        _pulseIndex = -1;
        base.OnClosed(e);
    }

    private void DragBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* 窗口正在关闭 / 没前台：拖动失败不影响步骤 */ }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Mark(skip: false);
    private void Skip_Click(object sender, RoutedEventArgs e) => Mark(skip: true);

    /// <summary>
    /// 记一次标记；步骤走完就**主动结束观察** —— 让用户对着剩下的几十秒干等，
    /// 是这条流程里最容易让人以为程序卡死的地方。
    /// </summary>
    private void Mark(bool skip)
    {
        if (_api is null) return;       // 演示快照：没人收事件

        // 这一步一条相关写命令都没抓到时，第一次点击只拦一下：最常见的原因是
        // 用户点早了、或原厂那一下没走到 EC。直接放行会白白废掉这一档的证据，
        // 只能整轮重采两分钟。给一次"再试一次 / 我再点一次"的机会。
        //
        // ⚠️ 2026-11-14 起这句话必须**留在原地且变色**：以前只换按钮文字，用户以为
        //    按钮点坏了（真机就是这么被误会的 —— 他点了一次没反应，就去干别的了）。
        if (!skip && _frames == 0 && !_emptyConfirm)
        {
            _emptyConfirm = true;
            Frames.Text = "这一步没抓到写命令 —— 回原厂再切一次，或点「没有这项」。仍要跳过请再点一次「完成这步」。";
            SetForeground(Frames, "WarnBrush");
            DoneButton.Content = "没抓到，仍继续";
            return;
        }

        try
        {
            _emptyConfirm = false;
            DoneButton.Content = "完成这步";
            if (_api.GuideMark(skip))
            {
                _api.RequestObserveStop();
                StepTitle.Text = "步骤做完了";
                StepHint.Text = "正在生成档案，不用再操作原厂了";
            }
        }
        catch (Exception ex)
        {
            Frames.Text = "记步骤失败：" + ex.Message;
            SetForeground(Frames, "HotBrush");
        }
    }

    /// <summary>
    /// 「再等 60 秒」：观察是**固定窗口**，到点没标的步骤一律按"没走到"收尾。
    /// 用户正在原厂里翻那个开关时，延长一次远比整轮重采便宜（见 MachineObserve 的延长分支）。
    /// </summary>
    private void Extend_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        _api.RequestObserveExtend(60);
        _extendedUntil = DateTimeOffset.Now.AddSeconds(6);
        Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        // 退回重做时把"没抓到"的二次确认也清掉：新窗口应该从头数。
        _emptyConfirm = false;
        DoneButton.Content = "完成这步";
        try { _api.GuideBack(); }
        catch (Exception ex) { Frames.Text = "退回失败：" + ex.Message; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_api is null) return;
        _api.RequestObserveStop();
        StepHint.Text = "已请求结束，正在生成档案…";
    }

    /// <summary>按真实快照渲染。抓到几条是数出来的，一步没抓到就直说。</summary>
    private void Refresh()
    {
        var g = _view();
        if (g is null) return;

        var phase = Text(g["phase"]) ?? "observe";

        // ---------- 采集前段（Warmup）：小窗已经在，但还没轮到用户操作 ----------
        // ⚠️ 2026-11-17 起主窗口**只在观察真开始时**才把这扇窗叫起来，所以实际不会走到
        //    这一支；留着是因为 --ui-shot --ui-dialog guide-warmup 还拿它当快照样本。
        if (phase == "warmup")
        {
            StepNo.Text = "准备";
            StepTitle.Text = "正在采集，先别动原厂";
            StepHint.Text = Text(g["stage"]) is { Length: > 0 } s0 ? s0 : "读机型、采样 EC 帧，跑完自动进第 1 步";
            Frames.Text = "采样跑完自动进第 1 步，按钮才会亮";
            SetForeground(Frames, "DimBrush");
            DoneButton.Content = "完成这步";
            DoneButton.IsEnabled = SkipButton.IsEnabled = false;
            BackButton.IsEnabled = StopButton.IsEnabled = ExtendButton.IsEnabled = false;
            _emptyConfirm = false;
            _index = -1;
            _frames = 0;
            PaintStepBar(0, AdoptGuide.Steps.Count, finished: false, steps: null);
            return;
        }

        var finished = Bool(g["finished"]);
        var index = IntOf(g["index"]);
        var total = IntOf(g["total"]);
        if (total <= 0) total = AdoptGuide.Steps.Count;

        StepNo.Text = finished ? "完成" : $"{index + 1}/{total}";
        StepTitle.Text = finished ? "步骤做完了" : Text(g["title"]) ?? "";
        StepHint.Text = finished ? "正在生成档案，不用再操作原厂了" : Text(g["hint"]) ?? "";

        var frames = IntOf(g["frames"]);
        // -1 = 还没开始观察（后端没交 deadline）。用 IntOfOr，别把"没有"当成"还剩 0 秒"。
        var remaining = IntOfOr(g["remaining"], -1);
        var extended = DateTimeOffset.Now < _extendedUntil;

        if (index != _index)
        {
            _index = index;
            _emptyConfirm = false;
            DoneButton.Content = "完成这步";
        }
        _frames = frames;

        // 倒计时是**真事**（后端交过来的 deadline 现算），不是编的进度条。
        // 最后 15 秒必须把"到点怎么处理"说出来 —— 上次就是这么静默走完的。
        var countdown = remaining >= 0 ? $" · 还剩 {remaining} 秒" : "";
        var warn = _emptyConfirm || (remaining >= 0 && remaining <= 15);

        if (finished)
        {
            Frames.Text = "";
            SetForeground(Frames, "DimBrush");
        }
        else if (_emptyConfirm)
        {
            Frames.Text = "这一步没抓到写命令 —— 回原厂再切一次，或点「没有这项」。仍要跳过请再点一次「完成这步」。";
            SetForeground(Frames, "WarnBrush");
        }
        else
        {
            var head = extended ? "已延长 60 秒 —— " : "";
            Frames.Text = head
                + (frames > 0 ? $"抓到 {frames} 条写命令{countdown}"
                              : $"还没抓到写命令（真的切过去了吗）{countdown}")
                + (remaining is >= 0 and <= 15 ? "。到点没标的步骤按「没做到」处理，还没做完就点「再等 60 秒」" : "");
            SetForeground(Frames, remaining is >= 0 and <= 5 ? "HotBrush" : warn ? "WarnBrush" : "DimBrush");
        }

        DoneButton.IsEnabled = !finished;
        SkipButton.IsEnabled = !finished;
        BackButton.IsEnabled = !finished && index > 0;
        StopButton.IsEnabled = !finished;
        // 这颗**始终在**（2026-11-17）：跟着状态隐藏/出现会把第二排按钮整排挤动，
        // 而且观察里它是唯一能挽回"到点收尾"的入口，藏起来等于让人找不到。
        ExtendButton.IsEnabled = !finished;

        PaintStepBar(index, total, finished, g["steps"] as JsonArray);
    }

    /// <summary>
    /// 画小窗顶部的五段步骤条。已完成段压暗铺实色、当前段脉动、还没到的是灰轨道 ——
    /// 与关于页那条采集进度同一套语言，用户在两处看到的是同一个意思。
    /// <paramref name="index"/> 是**后端报的当前步**（不是我们数的）。
    /// </summary>
    private void PaintStepBar(int index, int total, bool finished, JsonArray? steps)
    {
        if (_segs.Length == 0) return;

        // 该脉动的格子：完成/结束时 -1（全部停掉）
        var pulse = finished ? -1 : index;

        for (var i = 0; i < _segs.Length; i++)
        {
            var seg = _segs[i];
            // ⚠️ 只在"当前格变了"时才清动画。这个方法每 300ms 被轮询调一次，
            //    旧写法每拍 `BeginAnimation(null)` + 新建一条 720ms `Forever` 动画 ⇒
            //    呼吸动画每 300ms 被掐断重来，肉眼看到的是 1↔0.85 的**闪跳**，
            //    每秒还白建 3 条动画时钟。
            var keepPulse = pulse == i && _pulseIndex == i;
            if (!keepPulse)
            {
                seg.BeginAnimation(OpacityProperty, null);   // 清掉上一帧的脉动
                seg.Opacity = 1;
            }

            // 步骤数少于格子数时把多出来的藏掉，别留一截空轨道
            seg.Visibility = total <= 0 || i < total ? Visibility.Visible : Visibility.Collapsed;

            var done = finished || i < index;
            if (done)
            {
                seg.SetResourceReference(Border.BackgroundProperty, "AccBrush");
                seg.Opacity = 0.5;
            }
            else if (i == index)
            {
                seg.SetResourceReference(Border.BackgroundProperty, "AccBrush");
                // 脉动只在 0.85~1 之间：当前格必须始终比"已完成"(0.5) 亮一档。
                // 压到 0.35 那种写法在慢一拍的时刻看起来和已完成一模一样，
                // 用户反而分不清自己在哪一步。
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

            // 悬停看全名与状态：小窗放不下五个步骤名，但"第几格是哪一步"得能问出来
            var step = steps is not null && i < steps.Count ? steps[i] as JsonObject : null;
            var title = Text(step?["title"]) ?? "";
            var state = Bool(step?["skipped"]) && i < index ? "没有这项"
                : done ? "已完成"
                : i == index ? "正在做"
                : "还没到";
            seg.ToolTip = title.Length > 0 ? $"{i + 1} {title} · {state}" : null;
        }

        _pulseIndex = pulse;
    }

    /// <summary>换前景色（走 DynamicResource，换主题跟着变）。</summary>
    private static void SetForeground(System.Windows.Controls.TextBlock block, string key)
        => block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, key);

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static int IntOf(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
        }
        return 0;
    }

    /// <summary>缺省值与 <see cref="IntOf"/> 分开：`remaining` 的 -1（还没开始）不能被读成 0。</summary>
    private static int IntOfOr(JsonNode? n, int fallback)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
        }
        return fallback;
    }

    private static string? Text(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
