using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QControl.App.Ui.Controls;

/// <summary>
/// 环形占用仪表：conic 渐变环 + 内盘。
///
/// 四层，从下往上：轨道环 → **内盘**（panel2 色，形成真正的甜甜圈）→ 弧光晕 → 进度弧。
/// 中间的数字**不在这里画** —— 由 XAML 叠一层 TextBlock，
/// 字号/字色跟着主题与缩放走，不用在 OnRender 里手搓排版。
///
/// ⚠️ 内盘不是装饰：去掉它，环就只剩一条细弧浮在卡片渐变上，观感明显"薄"一层。
///    内盘色必须是 **Panel2** 不是 Panel：浅色下 Panel 是纯白、卡片也是白，
///    甜甜圈会消失成一条灰带。
/// ⚠️ 数值走 <see cref="AnimatedValue"/> 而不是直接用 Value：CSS 那边
///    <c>transition:background .45s</c> 会把弧扫过去，WPF 不做缓动就是一秒一跳。
/// ⚠️ 360° 时 ArcTo 起点终点重合会退化成空图形（屏幕上一根线都没有），
///    所以 ≥99.6% 走整圆 DrawEllipse；≤0.4% 直接不画弧。
/// ⚠️ 三处配套的观感修（改一处所有环一起生效）：方头端帽在低占比时是一块小方块 ⇒
///    一律 Round 端帽；整圈光晕把轨道糊成 20px 脏灰带、还满溢到圆外被裁硬边 ⇒ 光晕只贴已填充弧；
///    环宽跟着收细（0.105→0.085），半径按光晕宽度内缩，视觉直径基本不变但环干净了。
/// </summary>
internal sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    /// <summary>实际画出来的值（带缓动）。外部只写 <see cref="Value"/>。</summary>
    private static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(
        nameof(AnimatedValue), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>内盘填色（Web 的 <c>.ring-in</c> = var(--panel)）。</summary>
    public static readonly DependencyProperty CoreBrushProperty = DependencyProperty.Register(
        nameof(CoreBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>环宽 / 直径。收细到 0.085（原 0.105）：圆点少一点、弧头才看得出是圆头而不是方块。</summary>
    public static readonly DependencyProperty StrokeRatioProperty = DependencyProperty.Register(
        nameof(StrokeRatio), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.085, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>光晕宽 / 环宽。半径会按这个值内缩，保证光晕不被圆外边界裁掉。</summary>
    private const double HaloRatio = 1.5;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double AnimatedValue
    {
        get => (double)GetValue(AnimatedValueProperty);
        private set => SetValue(AnimatedValueProperty, value);
    }

    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? CoreBrush
    {
        get => (Brush?)GetValue(CoreBrushProperty);
        set => SetValue(CoreBrushProperty, value);
    }

    public double StrokeRatio
    {
        get => (double)GetValue(StrokeRatioProperty);
        set => SetValue(StrokeRatioProperty, value);
    }

    /// <summary>
    /// 扫值死区（百分点）。小于它的变化**不扫**，直接留原样。
    ///
    /// 为什么必须有：转速/温度每秒上下飘几十个单位，换算到 123px 的环上不到 0.5%
    /// （1% ≈ 0.9px 弧长，肉眼分辨不出）。不设死区就等于**每秒都扫一次 360ms**，
    /// 页面常年有 30%~40% 的时间在重画环 —— 软件渲染下这就是实打实的一核的 1/4。
    /// </summary>
    private const double MinDelta = 0.5;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        var target = (double)e.NewValue;
        if (double.IsNaN(target)) target = 0;

        // 从**当前画到的位置**比较/出发：读 AnimatedValue（动画占着它）而不是 Value，
        // 这样"每秒 0.3% 的缓慢漂移"会累积到 0.5% 才扫，不会永远不扫。
        if (Math.Abs(target - gauge.AnimatedValue) < MinDelta) return;

        // 数据是每秒一拍的：从当前位置扫到新值，而不是从 0 重来。
        // 360ms 略短于 1s 采样周期，节拍之间不会互相咬住。
        // ⚠️ 限帧 30：弧扫过 0.5% 才 0.45px，30fps 完全看不出台阶，而软件渲染下
        //    每一帧都要重画整个环 —— 60fps 是白烧一倍 CPU（见 UiMotion 类注释 ④）。
        var animation = UiMotion.At(new DoubleAnimation(gauge.AnimatedValue, target,
            TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        }, 30);
        gauge.BeginAnimation(AnimatedValueProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 6) return;

        var stroke = Math.Max(2.5, size * StrokeRatio);
        // 半径先让出光晕：光晕比环宽一倍半，贴着环中心画就会向外溢出 size/2，
        // 被圆外边界裁掉 ⇒ 外侧出现一条硬边（浅色主题下就是那道"脏灰带"）。
        var haloWidth = stroke * HaloRatio;
        var radius = (size - haloWidth) / 2 - 0.5;
        if (radius <= stroke) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        if (TrackBrush is not null)
            dc.DrawEllipse(null, MakePen(TrackBrush, stroke), center, radius, radius);

        // 内盘：半径正好顶到轨道环的内缘，把卡片渐变隔在外面。
        // ⚠️ 填纯色，**别叠径向渐变**：试过"中心提亮 6% / 边缘压暗 18%"，内盘中心被抬到
        //    和轨道环同一亮度，深色下糊成一团、浅色下整颗变成金属球。
        //    层次靠「轨道亮 / 内盘暗」的既有色差，立体感靠下面那 1px 碟边。
        var coreR = radius - stroke / 2;
        if (CoreBrush is not null)
        {
            dc.DrawEllipse(CoreBrush, null, center, coreR, coreR);

            // 1px 碟边：把"碟"和轨道环的边界钉死。没有它，放大的时候两者交界是一道抗锯齿灰雾。
            // 颜色借轨道色、半透明 —— 深色下是提亮边、浅色下是压深边，两边都对。
            if (TrackBrush is SolidColorBrush trackSolid)
            {
                var edge = new SolidColorBrush(Color.FromArgb(0x40, trackSolid.Color.R, trackSolid.Color.G, trackSolid.Color.B));
                edge.Freeze();
                var edgePen = new Pen(edge, 1);
                edgePen.Freeze();
                dc.DrawEllipse(null, edgePen, center, coreR - 0.5, coreR - 0.5);
            }
        }

        if (RingBrush is null) return;

        var value = Math.Clamp(AnimatedValue, 0, 100);
        if (double.IsNaN(value) || value <= 0.4) return;

        var whole = value >= 99.6;
        var sweep = 360 * value / 100;
        var start = PointAt(center, radius, -90);
        var end = PointAt(center, radius, -90 + sweep);

        // 光晕：只贴**已经画出来的那段弧**，不再绕整圈。
        // 整圈发光会把留白处的轨道环也罩上一层灰，环看起来就"化"了。
        if (RingBrush is SolidColorBrush solid)
        {
            var haloBrush = MakeHaloBrush(solid.Color, start, end, sweep);
            var haloPen = MakePen(haloBrush, haloWidth);
            if (whole) dc.DrawEllipse(null, haloPen, center, radius, radius);
            else dc.DrawGeometry(null, haloPen, Arc(center, radius, -90, sweep));
        }

        if (whole)
        {
            dc.DrawEllipse(null, MakePen(RingBrush, stroke), center, radius, radius);
            return;
        }
        dc.DrawGeometry(null, MakeArcPen(RingBrush, stroke, start, end, sweep), Arc(center, radius, -90, sweep));
    }

    /// <summary>弧头混白的比例。起点是本体色、头部往白里推这么多 ⇐ 弧才有"扫过去"的方向感。</summary>
    private const double ArcHeadLift = 0.18;

    /// <summary>进度弧的笔：沿「起点 → 弧头」的方向由本体色过渡到提亮色。</summary>
    private static Pen MakeArcPen(Brush ringBrush, double stroke, Point start, Point end, double sweep)
    {
        // 太长（起止点几乎重合）或太短（弦长不足几像素）时线性渐变没有意义，直接纯色。
        if (ringBrush is not SolidColorBrush solid || sweep is <= 6 or >= 330) return MakePen(ringBrush, stroke);

        var head = Lift(solid.Color, ArcHeadLift);
        var grad = new LinearGradientBrush(solid.Color, head, start, end) { MappingMode = BrushMappingMode.Absolute };
        grad.Freeze();
        return MakePen(grad, stroke);
    }

    /// <summary>光晕跟着弧一起提亮，否则弧头发亮、外圈光却还是暗的，看着两层。</summary>
    private static Brush MakeHaloBrush(Color color, Point start, Point end, double sweep)
    {
        var tail = Color.FromArgb(0x22, color.R, color.G, color.B);
        if (sweep is <= 6 or >= 330)
        {
            var flat = new SolidColorBrush(tail);
            flat.Freeze();
            return flat;
        }

        var lit = Lift(color, ArcHeadLift);
        var grad = new LinearGradientBrush(tail, Color.FromArgb(0x28, lit.R, lit.G, lit.B), start, end)
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        grad.Freeze();
        return grad;
    }

    /// <summary>往白色靠 <paramref name="t"/>（0~1）：用来做弧头提亮。</summary>
    private static Color Lift(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t),
        (byte)(c.G + (255 - c.G) * t),
        (byte)(c.B + (255 - c.B) * t));

    /// <summary>
    /// 描边笔。端帽必须是圆头：方头在低占比下（比如 CPU 6%）只有几像素长，
    /// 画出来是贴在 12 点位置的一块小方块 —— 这是"圆圈不好看"的主因。
    /// </summary>
    private static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }

    /// <summary>从 <paramref name="startDeg"/> 顺时针扫 <paramref name="sweepDeg"/> 的弧（-90 = 12 点）。</summary>
    private static Geometry Arc(Point center, double radius, double startDeg, double sweepDeg)
    {
        var start = PointAt(center, radius, startDeg);
        var end = PointAt(center, radius, startDeg + sweepDeg);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0,
                      isLargeArc: sweepDeg > 180,
                      SweepDirection.Clockwise,
                      isStroked: true, isSmoothJoin: false);
        }
        geo.Freeze();
        return geo;
    }

    private static Point PointAt(Point center, double radius, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
