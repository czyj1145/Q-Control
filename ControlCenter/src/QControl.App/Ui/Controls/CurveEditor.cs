using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace QControl.App.Ui.Controls;

/// <summary>曲线上的一个点（温度 °C → 占空比 %）。</summary>
internal readonly record struct CurvePt(double T, int D);

/// <summary>
/// 风扇曲线画布：画曲线 + 拖点编辑。
///
/// 绘制顺序（下层压上层），别乱：
///   网格与刻度 → 转速下限虚线 → 温度保护墙（红带 + 虚线 + 标签）→ 折线 → 点 →
///   当前温度竖虚线 + 交点 → 拖动时的数值气泡。
///
/// 拖点规则：温度夹在**左右邻点之间**，保持单调 —— 否则一拖过头排序就会
/// 把索引打乱，正在拖的那个点直接"丢手"。
///
/// ⚠️ 必须自己画一块背景矩形：FrameworkElement 没有背景就不收鼠标事件（HistoryChart 同一条坑）。
/// ⚠️ 配色不在这里硬编码：全部读 <see cref="UiTheme.Palette"/>，换主题由宿主调
///    <see cref="RefreshTheme"/> 触发重绘（画刷在 OnRender 里现造并 Freeze，不缓存也能过）。
/// </summary>
internal sealed class CurveEditor : FrameworkElement
{
    public const double TMin = 40;
    public const double TMax = 100;

    private IReadOnlyList<CurvePt> _points = [];
    private double? _tempNow;
    private int? _dutyNow;
    private bool _running;
    private int _floor = 30;
    private double? _wall;
    private bool _protecting;
    private int? _drag;

    /// <summary>点位被拖动。参数 true = 松手（该落盘了），false = 拖动中（只更新界面/防抖）。</summary>
    public event Action<bool>? Changed;

    private static readonly Typeface Face =
        new(new FontFamily("Microsoft YaHei UI, Segoe UI"), FontStyles.Normal,
            FontWeights.Medium, FontStretches.Normal);

    public void SetData(IReadOnlyList<CurvePt> points, double? tempNow, int? dutyNow,
                        bool running, int floor, double? wall, bool protecting)
    {
        _points = points;
        _tempNow = tempNow;
        _dutyNow = dutyNow;
        _running = running;
        _floor = floor;
        _wall = wall;
        _protecting = protecting;
        InvalidateVisual();
    }

    public void RefreshTheme() => InvalidateVisual();

    /// <summary>
    /// ⚠️ 自定义 OnRender 的元素**不会**因为尺寸变化自动重画：WPF 只在属性/数据变化时
    /// 重跑 OnRender，纯 RenderSize 变化沿用上一趟画好的图形（坐标是旧的）。
    /// 表现就是"曲线画到画布外面去了 / 折线被压成一条"。这一行不能删。
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }

    /// <summary>拖动中改的是同一份列表里的元素（调用方持有 List，见 <see cref="CurvePt"/> 是 record struct）。</summary>
    private bool Mutable => _points is List<CurvePt>;

    /// <summary>
    /// 正在拖点（鼠标已捕获、未松开）。保存响应回来、轮询覆盖本地前都要问它：
    /// 拖到一半把编辑器手里的列表换成新对象，正在拖的点会因服务端去重/排序换下标而跳走
    /// —— 这就是"还没拖完就自动保存并回弹"的半条根因。
    /// </summary>
    public bool IsDragging => _drag is not null;

    // ---------------------------------------------------------------- 坐标换算
    // 与 drawFanCurve 的 fcAxes 同一套：pad = min(34, W*0.09)，x0=pad / x1=W-14 / y0=8 / y1=H-22。

    private (double W, double H, double Pad, double X0, double X1, double Y0, double Y1) Axes()
    {
        var w = Math.Max(1, ActualWidth);
        var h = Math.Max(1, ActualHeight);
        var pad = Math.Min(34, w * 0.09);
        return (w, h, pad, pad, w - 14, 8, h - 22);
    }

    private static double XOf(in (double W, double H, double Pad, double X0, double X1, double Y0, double Y1) a, double t)
        => a.X0 + (ClampT(t) - TMin) / (TMax - TMin) * (a.X1 - a.X0);

    /// <summary>
    /// ⚠️ 除以 100.0 不是 100：<see cref="ClampD"/> 返回 int，`int / 100` 是**整除**，
    /// 于是 0~99% 全变成 0 —— 折线整条被压到底边、只有 100% 那点跳上去（第一版就是这个症状）。
    /// </summary>
    private static double YOf(in (double W, double H, double Pad, double X0, double X1, double Y0, double Y1) a, double d)
        => a.Y1 - ClampD(d, 0) / 100.0 * (a.Y1 - a.Y0);

    private static double ClampT(double v) => double.IsFinite(v)
        ? Math.Round(Math.Max(TMin, Math.Min(TMax, v)))
        : TMin;

    private static int ClampD(double v, int floor)
        => (int)Math.Round(Math.Max(floor, Math.Min(100, double.IsFinite(v) ? v : floor)));

    // ---------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        var a = Axes();
        var p = UiTheme.Palette;
        var light = ReferenceEquals(p, UiPalette.Light);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var grid = Frozen(p.Line);
        var label = Frozen(p.Dim);
        var line = Frozen(p.Acc);
        var point = Frozen(p.Acc2);
        var dragColor = Frozen(p.Warn);
        var temp = Frozen(p.Warn);
        var floorBrush = Frozen(Color.FromRgb(0x2f, 0x6b, 0x4f));
        var wallBrush = Frozen(p.Hot);

        // 背景：既是视觉底（CSS canvas 的 #0d1119 / 近白），也是鼠标事件的命中区
        var bg = Frozen(light ? Color.FromRgb(0xf7, 0xf9, 0xfd) : Color.FromRgb(0x0d, 0x11, 0x19));
        var border = Frozen(p.Line);
        var round = new Pen(border, 1);
        dc.DrawRoundedRectangle(bg, round, new Rect(0.5, 0.5, Math.Max(0, a.W - 1), Math.Max(0, a.H - 1)), 10, 10);

        // ---- 网格 ----
        var gridPen = new Pen(grid, 1);
        var step = a.W < 420 ? 20 : 10;
        for (var t = TMin; t <= TMax; t += step)
        {
            var x = XOf(a, t);
            dc.DrawLine(gridPen, new Point(x, a.Y0), new Point(x, a.Y1));
            dc.DrawText(Text(((int)t).ToString(CultureInfo.InvariantCulture) + "°", 10, label, dpi),
                new Point(x - 8, a.H - 8));
        }
        foreach (var d in new[] { 0, 25, 50, 75, 100 })
        {
            var y = YOf(a, d);
            dc.DrawLine(gridPen, new Point(a.Pad, y), new Point(a.X1, y));
            dc.DrawText(Text(d + "%", 10, label, dpi), new Point(6, y + 3));
        }

        // ---- 转速下限：曲线不会比它更低，画出来"拖不下去"才有解释 ----
        if (_floor > 0)
        {
            var y = YOf(a, _floor);
            var pen = new Pen(floorBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
            dc.DrawLine(pen, new Point(a.Pad, y), new Point(a.X1, y));
            dc.DrawText(Text("下限 " + _floor + "%", 10, floorBrush, dpi), new Point(a.Pad + 4, y - 14));
        }

        // ---- 温度保护墙：红带 + 虚线；触发时整带加深、标签改"保护中" ----
        if (_wall is { } wall && wall >= TMin && wall <= TMax)
        {
            var wx = XOf(a, wall);
            var fill = new SolidColorBrush(Color.FromArgb(
                (byte)(_protecting ? 0x2e : 0x12), p.Hot.R, p.Hot.G, p.Hot.B));
            fill.Freeze();
            dc.DrawRectangle(fill, null,
                new Rect(wx, a.Y0, Math.Max(0, a.X1 - wx), Math.Max(0, a.Y1 - a.Y0)));
            var pen = new Pen(wallBrush, 1) { DashStyle = new DashStyle([5, 3], 0) };
            dc.DrawLine(pen, new Point(wx, a.Y0), new Point(wx, a.Y1));

            var text = (_protecting ? "保护中 " : "保护墙 ")
                       + ((int)wall).ToString(CultureInfo.InvariantCulture) + "°";
            var lx = wx + 46 > a.X1 ? Math.Max(2, wx - 50) : wx + 4;
            dc.DrawText(Text(text, 10, wallBrush, dpi), new Point(lx, 16));
        }

        if (_points.Count == 0)
        {
            DrawTempCross(dc, a, temp, dpi);
            return;
        }

        // ---- 折线 ----
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(XOf(a, _points[0].T), YOf(a, _points[0].D)), false, false);
            for (var i = 1; i < _points.Count; i++)
                ctx.LineTo(new Point(XOf(a, _points[i].T), YOf(a, _points[i].D)), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(line, 2.2), geo);

        // ---- 点（拖动中的那个放大 + 换色） ----
        for (var i = 0; i < _points.Count; i++)
        {
            var dragging = i == _drag;
            var brush = dragging ? dragColor : point;
            dc.DrawEllipse(brush, null,
                new Point(XOf(a, _points[i].T), YOf(a, _points[i].D)), dragging ? 6 : 4, dragging ? 6 : 4);
        }

        DrawTempCross(dc, a, temp, dpi);

        // ---- 拖动时的数值气泡（跟着点走，左右夹在画布里） ----
        if (_drag is { } di && di < _points.Count)
        {
            var pt = _points[di];
            var text = ((int)pt.T).ToString(CultureInfo.InvariantCulture) + "°C → "
                       + pt.D.ToString(CultureInfo.InvariantCulture) + "%";
            var ft = Text(text, 11, dragColor, dpi);
            var w = ft.Width + 12;
            var x = XOf(a, pt.T);
            var y = YOf(a, pt.D);
            var bx = Math.Max(2, Math.Min(a.W - w - 2, x - w / 2));

            // 气泡默认挂在点上方 26px。⚠️ 但点被拖到 100% 时 y 就是 Y0(=8)，上方根本放不下：
            // 原来无条件 `y - 26` 再夹到 2，于是气泡**正好压在点上**——拖动中不透明的气泡把
            // 圆点整个盖住，用户看到的就是"拉到 100% 圆点不见了/被遮挡"（2026-10-24 报障）。
            // 顶部放不下就翻到点下方（点半径最大 6 + 6 间隙），底部再夹一次防止矮画布溢出。
            var by = y - 26;
            if (by < 2) by = y + 12;
            by = Math.Min(by, Math.Max(2, a.H - 20));

            var bgBrush = new SolidColorBrush(light
                ? Color.FromArgb(0xf8, 0xff, 0xff, 0xff)
                : Color.FromArgb(0xf0, 20, 24, 33));
            bgBrush.Freeze();
            dc.DrawRoundedRectangle(bgBrush, new Pen(dragColor, 1), new Rect(bx, by, w, 18), 4, 4);
            dc.DrawText(ft, new Point(bx + 6, by + 2));
        }
    }

    /// <summary>当前温度竖虚线 + 交点（运行中取实测占空比，否则取曲线插值）。</summary>
    private void DrawTempCross(DrawingContext dc,
                               in (double W, double H, double Pad, double X0, double X1, double Y0, double Y1) a,
                               Brush temp, double dpi)
    {
        if (_tempNow is not { } t) return;
        var x = XOf(a, t);
        var pen = new Pen(temp, 1) { DashStyle = new DashStyle([4, 3], 0) };
        dc.DrawLine(pen, new Point(x, a.Y0), new Point(x, a.Y1));

        var d = _running && _dutyNow is { } now ? now : DutyAt(t);
        dc.DrawEllipse(temp, null, new Point(x, YOf(a, d)), 3.5, 3.5);
    }

    private int DutyAt(double t)
    {
        if (_points.Count == 0) return ClampD(60, _floor);
        var sorted = _points.OrderBy(p => p.T).ToList();
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

    private static FormattedText Text(string text, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, brush, dpi);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---------------------------------------------------------------- 拖点

    private int Nearest(Point at)
    {
        if (_points.Count == 0) return -1;
        var a = Axes();
        var best = -1;
        var bestD = 16.0;                    // 判定半径 16 CSS px（与 Web 同值）
        for (var i = 0; i < _points.Count; i++)
        {
            var dx = XOf(a, _points[i].T) - at.X;
            var dy = YOf(a, _points[i].D) - at.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Mutable) return;
        var index = Nearest(e.GetPosition(this));
        if (index < 0) return;               // 没点到点：不拦截（保持外层可滚动）
        _drag = index;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_drag is not { } index || !Mutable || index >= _points.Count)
        {
            Cursor = Nearest(e.GetPosition(this)) >= 0 ? Cursors.Hand : Cursors.Cross;
            return;
        }

        var a = Axes();
        var at = e.GetPosition(this);

        // 温度夹在左右邻点之间（curl 式单调保护），占空比不低于转速下限
        var lo = TMin;
        var hi = TMax;
        if (index > 0) lo = Math.Max(lo, _points[index - 1].T);
        if (index < _points.Count - 1) hi = Math.Min(hi, _points[index + 1].T);

        var t = ClampT(Math.Max(lo, Math.Min(hi,
            TMin + (at.X - a.X0) / Math.Max(1, a.X1 - a.X0) * (TMax - TMin))));
        var d = ClampD((a.Y1 - at.Y) / Math.Max(1, a.Y1 - a.Y0) * 100, _floor);

        if (Math.Abs(_points[index].T - t) < 0.0001 && _points[index].D == d) return;

        ((List<CurvePt>)_points)[index] = new CurvePt(t, d);
        InvalidateVisual();
        Changed?.Invoke(false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is null) return;
        _drag = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Cross;
        InvalidateVisual();
        Changed?.Invoke(true);
    }

    /// <summary>
    /// 捕获被系统抢走（Alt+Tab / Win 键 / UAC 弹窗 / 任务栏预览 / 触摸板手势）时，
    /// `OnMouseLeftButtonUp` **永远不会再来** —— 不在这里收尾的话 `_drag` 会一直是那个下标：
    ///   · `IsDragging` 恒 true ⇒ `<c>CoolView</c>` 的「拖动中不跟随后端」闸门永久生效，
    ///     曲线再也不随温度/切档刷新；
    ///   · `apply_now` 再也发不出去 ⇒ 保存只落盘不生效；
    ///   · 鼠标没按下时在画布上移动也会继续改点。
    /// 当作一次松手处理（`Changed(true)` 让外层 Normalize + 落盘）。
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is null) return;
        _drag = null;
        Cursor = Cursors.Cross;
        InvalidateVisual();
        Changed?.Invoke(true);
    }
}
