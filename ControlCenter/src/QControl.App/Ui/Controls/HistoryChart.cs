using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QControl.App.Http.Contract;

namespace QControl.App.Ui.Controls;

/// <summary>
/// 历史曲线：折线 + 悬停提示。
///
/// 六条线共用一张图，各自有满量程（温度 100°C / 风扇 6000RPM / 功耗 180W），
/// 所以**不能**用统一坐标轴 —— 每条的取值都先除自己的 max，再映射到同一块画布高度。
///
/// ⚠️ 六条 = CPU 温度 / GPU 温度 / **CPU 风扇 / GPU 风扇** / CPU 功耗 / GPU 功耗。
///    风扇是两条：`fan` = fan1 = CPU 风扇、`fan2` = GPU 风扇 —— 只画一条的话
///    看不出画的是哪一个。
///
/// 几处必须照做的细节（别凭感觉调）：
///   · 网格 y = 10 + i*(H-30)/rows，rows = H&lt;120 ? 2 : 4；
///   · X 按**真实时间戳**映射（见 <see cref="Axis"/>），不再是 i/(N-1) 均匀铺开；
///     只有 ts 全缺/全相同时才退回索引轴；
///   · 缺数据的点 **断开**（独显直连时没有 GPU 温度，连线跨过去就是假数据）；
///   · 悬停钉的是"距最新点第 N 个"（相对偏移），不是绝对下标 —— 滑动窗口里下标会平移；
///   · 提示框默认放右、放不下翻左、再夹进画布。
///
/// ⚠️ 为什么不用 LiveCharts/ScottPlot：一个折线图不值得再进一个 NuGet 依赖（项目零依赖），
///    而且这图要的"暗/亮两套配色 + 中文提示框"用原生 DrawingContext 就够。
/// ⚠️ 必须自己画一块透明底矩形，否则 FrameworkElement 没有背景、鼠标事件打不进来（悬停会失效）。
/// </summary>
internal sealed class HistoryChart : FrameworkElement
{
    /// <summary>
    /// 一条线：取值器 + 满量程 + 暗/亮两套颜色 + 提示框里的名字与格式。
    ///
    /// <paramref name="Dash"/> 只给 GPU 风扇用：本机两个风扇常年只差几十 RPM，
    /// 两条实线会完全叠在一起、看不出有两条。画成虚线（4 实 3 空）后，重合处
    /// 是"绿底上透出青点"，才真的能看出是两路。⚠️ 两路的 dash 参数必须一致（4 实 3 空）。
    /// </summary>
    private sealed record Series(
        string Key,
        double Max,
        Color Dark,
        Color Light,
        string Label,
        Func<double, string> Format,
        Func<HistoryPointsItemDto, double?> Pick,
        double[]? Dash = null);

    // 顺序 = 图例顺序 = 提示框行顺序
    private static readonly Series[] SeriesList =
    [
        new("cpu_t", 100, Hex("#4dd6ff"), Hex("#0f86cc"), "CPU 温度",
            v => Math.Round(v) + " °C", p => p.CpuT),
        new("gpu_t", 100, Hex("#7c5cff"), Hex("#6a45f0"), "GPU 温度",
            v => Math.Round(v) + " °C", p => p.GpuT),
        // ⚠️ 「风扇」改叫「CPU 风扇」：散热页两个图案、曲线编辑器两条曲线都按
        //    CPU/GPU 分路（UFA1/UFA2），这里写「风扇」是唯一没跟上的一处。
        //    色号与 UiPalette.Ok / 图例 OkBrush 同源（暗 #39d98a / 亮 #12a05c）。
        new("fan", 6000, Hex("#39d98a"), Hex("#12a05c"), "CPU 风扇",
            v => Math.Round(v) + " RPM", p => p.Fan),
        new("fan2", 6000, Hex("#2dd4bf"), Hex("#0f8f86"), "GPU 风扇",
            v => Math.Round(v) + " RPM", p => p.Fan2, Dash: [4, 3]),
        new("cpu_p", 180, Hex("#ff5c6c"), Hex("#d63a4c"), "CPU 功耗",
            v => v.ToString("F1", CultureInfo.InvariantCulture) + " W", p => p.CpuP),
        new("gpu_p", 180, Hex("#ffb547"), Hex("#b0700a"), "GPU 功耗",
            v => v.ToString("F1", CultureInfo.InvariantCulture) + " W", p => p.GpuP),
    ];

    private static readonly Typeface Face =
        new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
            FontWeights.Medium, FontStretches.Normal);

    private IReadOnlyList<HistoryPointsItemDto>? _points;
    private int? _hover;                 // 距最新点的偏移（0 = 最新）
    private Point _hoverAt;
    private bool _light;

    /// <summary>
    /// 被用户点掉的线（图例里点一下隐藏，再点显示）。
    /// 为什么要这个（2026-11-03）：六条线共用一张图，空闲时它们各自占一条水平线，
    /// 想看某一条的细节得先在脑子里把另外五条过滤掉。图例本来就是给"某条线"用的入口。
    /// 状态放在这里而不是 XAML：换页/换主题都会重建视图，而"我刚把功耗线关掉了"应该跟着会话活。
    /// </summary>
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    /// <summary>切换一条线的显隐，返回切换后是否可见（调用方拿它渲染图例的透明度）。</summary>
    public bool ToggleSeries(string key)
    {
        if (!_hidden.Remove(key)) _hidden.Add(key);
        InvalidateVisual();
        return !_hidden.Contains(key);
    }

    public bool IsSeriesVisible(string key) => !_hidden.Contains(key);

    public void SetData(IReadOnlyList<HistoryPointsItemDto>? points)
    {
        _points = points;
        if (points is null) _hover = null;
        InvalidateVisual();
    }

    public void SetLight(bool light)
    {
        if (_light == light) return;
        _light = light;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 30) return;

        // 透明底：让这个元素能吃到鼠标（悬停取数是这张图的一半功能）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        var pal = UiTheme.Palette;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = MakePen(pal.Line, 1);
        // 左边留出温度刻度的位置（「100°C」这一格最宽）。原来是 30，只够画网格。
        var pad = PlotPad(w);

        var rows = h < 120 ? 2 : 4;
        for (var i = 0; i <= rows; i++)
        {
            var y = 10 + i * (h - 30) / rows;
            dc.DrawLine(gridPen, new Point(pad, y), new Point(w - 10, y));

            // 温度刻度：这张图六条线各按自己的满量程归一（100°C / 6000RPM / 180W），
            // 一根轴标不了三个量纲 —— 温度是主线（也正好是满量程 100），标它。
            // 只有顶格带单位，其余只写数字：五格都写 °C 是纯噪音。
            // ⚠️ 转速与功耗仍然只能靠悬停读数，别以为这里标了温度就"整张图能读数了"。
            var temp = Math.Round(100.0 * (1 - (double)i / rows));
            var label = i == 0
                ? temp.ToString("F0", CultureInfo.InvariantCulture) + "°C"
                : temp.ToString("F0", CultureInfo.InvariantCulture);
            var lt = MakeText(label, 9.5, MakeSolid(pal.Dim), dpi);
            dc.DrawText(lt, new Point(pad - 6 - lt.Width, y - lt.Height / 2));
        }

        var points = _points;
        if (points is null || points.Count < 2)
        {
            DrawHint(dc, pal, dpi, w, h, "等一秒，曲线就有第二个点了");
            return;
        }

        var n = points.Count;
        var ax = BuildAxis(points);
        foreach (var s in SeriesList)
        {
            if (_hidden.Contains(s.Key)) continue;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                var started = false;
                for (var i = 0; i < n; i++)
                {
                    var v = s.Pick(points[i]);
                    if (v is null) { started = false; continue; }        // 缺数据 ⇒ 断线
                    var pt = new Point(XAt(i, points, ax, w, pad), YAt(h, v.Value, s.Max));
                    if (!started)
                    {
                        ctx.BeginFigure(pt, isFilled: false, isClosed: false);
                        started = true;
                    }
                    else
                    {
                        ctx.LineTo(pt, isStroked: true, isSmoothJoin: false);
                    }
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, MakePen(SeriesColor(s), 1.8, s.Dash), geo);
        }

        DrawScaleNote(dc, pal, dpi, w);

        DrawHover(dc, pal, dpi, w, h, pad, points);
    }

    /// <summary>
    /// 满量程说明（原本是左上角一行灰字「100°C / 6000RPM / 180W」）。
    ///
    /// 2026-11-03 改：① 挪到**右上角**，把左上角让给温度刻度；② 三段各用自己那条线的颜色
    /// —— 灰字读不出"哪个量程属于哪条线"，而这正是六线同图最需要说清的一件事。
    /// 分隔符用暗色，别让"/"抢走注意力。
    /// </summary>
    private void DrawScaleNote(DrawingContext dc, UiPalette pal, double dpi, double w)
    {
        // 温度 / 转速 / 功耗各取一条代表线，颜色与图例、曲线同源（别在这里写死色号）
        var parts = new[]
        {
            ("100°C", SeriesColor(SeriesList[0])),
            ("6000RPM", SeriesColor(SeriesList[2])),
            ("180W", SeriesColor(SeriesList[4])),
        };

        const double y = 8;
        var texts = parts.Select(p => MakeText(p.Item1, 10, MakeSolid(p.Item2), dpi)).ToList();
        var sep = MakeText(" / ", 10, MakeSolid(pal.Dim), dpi);
        var total = texts.Sum(t => t.Width) + sep.Width * (texts.Count - 1);

        var x = Math.Max(4, w - 12 - total);
        for (var i = 0; i < texts.Count; i++)
        {
            if (i > 0)
            {
                dc.DrawText(sep, new Point(x, y));
                x += sep.Width;
            }
            dc.DrawText(texts[i], new Point(x, y));
            x += texts[i].Width;
        }
    }

    private void DrawHover(DrawingContext dc, UiPalette pal, double dpi, double w, double h,
                           double pad, IReadOnlyList<HistoryPointsItemDto> points)
    {
        if (_hover is not int back) return;
        var n = points.Count;
        var index = Math.Clamp(n - 1 - back, 0, n - 1);
        var point = points[index];
        var x = XAt(index, points, BuildAxis(points), w, pad);

        // 竖线：细、暗，贯穿全高（跟曲线区分开）
        dc.DrawLine(MakePen(Color.FromArgb(0x52, pal.Txt.R, pal.Txt.G, pal.Txt.B), 1),
                    new Point(x, 8), new Point(x, h - 20));

        var edge = MakePen(pal.CardBottom, 1.5);
        foreach (var s in SeriesList)
        {
            if (_hidden.Contains(s.Key)) continue;      // 关掉的线：点上也不画
            var v = s.Pick(point);
            if (v is null) continue;
            dc.DrawEllipse(MakeSolid(SeriesColor(s)), edge, new Point(x, YAt(h, v.Value, s.Max)), 3, 3);
        }

        var age = (int)Math.Max(0, Math.Round((points[n - 1].T ?? 0) - (point.T ?? 0)));
        var rows = new List<(string Text, Brush Brush)> { (age + " 秒前", MakeSolid(pal.Dim)) };
        foreach (var s in SeriesList)
        {
            if (_hidden.Contains(s.Key)) continue;      // 提示框里也照着隐藏，免得"图上看不见却有数"
            var v = s.Pick(point);
            rows.Add(($"{s.Label} {(v is null ? "—" : s.Format(v.Value))}", MakeSolid(SeriesColor(s))));
        }

        var sizes = rows.Select(r => MakeText(r.Text, 11, r.Brush, dpi)).ToList();
        var width = sizes.Max(t => t.Width) + 16;
        var height = rows.Count * 15 + 10;

        var bx = x + 12;
        if (bx + width > w - 4) bx = x - 12 - width;
        bx = Math.Max(4, Math.Min(w - width - 4, bx));
        var by = Math.Max(4, Math.Min(h - height - 4, _hoverAt.Y - height / 2));

        dc.DrawRoundedRectangle(MakeSolid(pal.Panel), MakePen(pal.Line, 1),
                                new Rect(bx, by, width, height), 6, 6);
        for (var i = 0; i < sizes.Count; i++)
        {
            // 第一行（"N 秒前"）字号小一档，与 JS 里 10px/11px 的混排一致
            var line = i == 0 ? MakeText(rows[i].Text, 10, rows[i].Brush, dpi) : sizes[i];
            dc.DrawText(line, new Point(bx + 8, by + 6 + i * 15));
        }
    }

    /// <summary>
    /// 绘图区左内边距 —— 左边留给温度刻度（「100°C」这一格最宽）。
    ///
    /// ⚠️ **绘制与命中测试必须同源**（2026-11-03 修）：加刻度那一次只改了 <see cref="OnRender"/>
    ///    里的 30/0.08 → 40/0.10，<see cref="OnMouseMove"/> 反查下标时还在用旧值 ⇒ 同一根横轴
    ///    在两处差 10px（≈ 两个采样点），鼠标指着某一点，高亮的却是它左边那一两个
    ///    （图上表现为「悬停读数比实际位置早几秒」，越靠左越明显）。
    ///    这类"画一遍、再反算一遍"的坐标常量只许有一个出口 —— 与 CurveEditor.Axes() 同规矩。
    /// </summary>
    private static double PlotPad(double w) => Math.Min(40, w * 0.10);

    private static void DrawHint(DrawingContext dc, UiPalette pal, double dpi, double w, double h, string text)
    {
        var t = MakeText(text, 11, MakeSolid(pal.Dim), dpi);
        dc.DrawText(t, new Point((w - t.Width) / 2, (h - t.Height) / 2));
    }

    // ---------------------------------------------------------------- 鼠标

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var points = _points;
        if (points is null || points.Count < 2) return;

        var pos = e.GetPosition(this);
        var w = ActualWidth;
        var pad = PlotPad(w);       // ⚠️ 必须与 OnRender 同一份（见 PlotPad 的说明）

        // 反查：找横坐标离鼠标最近的那个点。
        // 按时间映射之后 i 与 x 不再等比（间隔在 1.0~1.9 秒之间抖），
        // 所以不能用 (pos.X - pad)/span*(N-1) 反算下标 —— 那会指到隔壁点上。
        // 150 个点的线性扫描只在鼠标移动时跑，不值得上二分。
        var ax = BuildAxis(points);
        var best = 0;
        var bestDx = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var dx = Math.Abs(XAt(i, points, ax, w, pad) - pos.X);
            if (dx < bestDx) { bestDx = dx; best = i; }
        }

        _hover = points.Count - 1 - best;
        _hoverAt = pos;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>
    /// 本帧横轴的**时间域**：首点 ts、跨度、是否真的按时间画。
    ///
    /// ⚠️ 2026-11-01 改（用户报"卡片说最近 150 秒，悬停却查到 154 秒前"）：
    ///    以前 X(i) = pad + i/(N-1)*(W-pad-10)，把"150 个点"当成"150 秒"画。
    ///    采样间隔实际在 1.0~1.9 秒之间抖（EC 0.45s 节流那一拍必然超时），
    ///    于是 1.9 秒的间隔被画得和 1 秒一样宽，图上时间与悬停的「N 秒前」互相矛盾。
    ///    现在缓冲是 150 秒窗口（<see cref="AppState.HistoryWindowSec"/>），
    ///    横轴按 T 的比例铺：左端 = 窗口起点，右端 = 最新点，两边严格对得上。
    /// </summary>
    private readonly record struct Axis(double T0, double Span, bool UseTime);

    private static Axis BuildAxis(IReadOnlyList<HistoryPointsItemDto> p)
    {
        var t0 = p[0].T ?? 0;
        var span = (p[^1].T ?? 0) - t0;
        // ts 全缺 / 全都一样 ⇒ 退回索引轴，否则除零
        return new Axis(t0, span, span > 0.5);
    }

    private static double XAt(int i, IReadOnlyList<HistoryPointsItemDto> p, Axis ax, double w, double pad)
    {
        var ratio = ax.UseTime ? ((p[i].T ?? ax.T0) - ax.T0) / ax.Span : (double)i / (p.Count - 1);
        // 钳 0..1：墙钟回拨会让 T 比前一点还小，不钳曲线就会往回折出一段竖线
        return pad + Math.Clamp(ratio, 0, 1) * (w - pad - 10);
    }

    /// <summary>与 JS 的 histY 同一份映射：v/max 夹到 0..1，10px 是顶、H-20 是底。</summary>
    private static double YAt(double h, double v, double max)
        => 10 + (1 - Math.Min(1, Math.Max(0, v / max))) * (h - 30);

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s)!;

    private Color SeriesColor(Series s) => _light ? s.Light : s.Dark;

    private static SolidColorBrush MakeSolid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen MakePen(Color c, double thickness, double[]? dash = null)
        => MakePen(MakeSolid(c), thickness, dash);

    private static Pen MakePen(Brush brush, double thickness, double[]? dash = null)
    {
        var pen = new Pen(brush, thickness);
        // DashStyle 的单位是**笔宽的倍数**：想要 4px 实 / 3px 空，得先除掉线宽。
        // ⚠️ 必须在 Freeze 之前设 —— 冻结后任何属性都改不动了。
        if (dash is { Length: > 0 })
        {
            var scale = thickness > 0 ? thickness : 1;
            pen.DashStyle = new DashStyle(dash.Select(d => d / scale).ToList(), 0);
        }
        pen.Freeze();
        return pen;
    }

    private static FormattedText MakeText(string s, double size, Brush brush, double dpi)
        => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, dpi);
}
