using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QControl.Core.Lights;

namespace QControl.App.Ui.Controls;

/// <summary>
/// HSV 色盘 + 指示环。
///
/// ⚠️ **逐像素画**，不用 CSS/LinearGradient 近似：点在哪儿必须就是哪儿的颜色
///    （项目铁律「宁慢也不显示错的」）。位图只在尺寸/DPI 变化时重建，拖动时不重画。
/// ⚠️ 指示环是**空心**的：环里透出色盘本色。填成当前色会让人以为"色盘变色了"。
/// ⚠️ 拖动中只改本地预览（<c>commit=false</c>），**松手才写 EC** —— 一条背光命令 ~46ms
///    外加 0.2s 节流，逐帧写会把写入队列堵住。
/// </summary>
internal sealed class ColorWheel : FrameworkElement
{
    /// <summary>取色。commit=false 是拖动中的预览，true 是松手（这时才下发）。</summary>
    public event Action<int, int, int, bool>? Picked;

    private WriteableBitmap? _bitmap;
    private int _bw, _bh;
    private double _dpr = 1;

    /// <summary>
    /// 色盘像素只跟「像素尺寸 + DPI」有关，与实例无关 ⇒ 静态缓存一份给后续实例（换窗口会话）复用。
    /// Frozen 的位图可以跨线程/跨窗口共享（与 <see cref="RingPen"/> 那种 static readonly Frozen Pen 同理）；
    /// 实测 176DIP / 150% 缩放（264×264）逐像素算一次约 11ms，只有第一次
    /// 打开背光页会付 —— 关窗再开、切主题都不该再算一遍。
    /// </summary>
    private static WriteableBitmap? _cacheBitmap;
    private static int _cacheW, _cacheH;
    private static double _cacheDpr;

    private Color _color = Color.FromRgb(255, 255, 255);
    private bool _dragging;

    private static readonly Pen RingPen = MakePen(Color.FromArgb(0x73, 0x80, 0x8c, 0xa5), 1);
    private static readonly Pen DotWhite = MakePen(Colors.White, 2);
    private static readonly Pen DotBlack = MakePen(Color.FromArgb(0xb3, 0, 0, 0), 1);
    private static readonly Pen DotHalo = MakePen(Color.FromArgb(0xcc, 255, 255, 255), 1);

    public ColorWheel()
    {
        SnapsToDevicePixels = true;
        Focusable = false;
        Cursor = Cursors.Cross;
    }

    /// <summary>当前选中的颜色（画指示环用）。不改位图 —— 位图只跟尺寸有关。</summary>
    public void SetColor(Color color)
    {
        if (_color == color) return;
        _color = color;
        InvalidateVisual();
    }

    public Color Color => _color;

    // ---------------------------------------------------------------- 交互

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        Pick(e.GetPosition(this), commit: false);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Pick(e.GetPosition(this), commit: false);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Pick(e.GetPosition(this), commit: true);
        e.Handled = true;
    }

    /// <summary>拖动被系统打断（窗口失活等）：当作松手提交，别把最后一次预览丢掉。</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;
        _dragging = false;
        Picked?.Invoke(_color.R, _color.G, _color.B, true);
    }

    private void Pick(Point pos, bool commit)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var r = Math.Min(w, h) / 2;
        if (r <= 0) return;

        var dx = pos.X - w / 2;
        var dy = pos.Y - h / 2;
        var s = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy) / r);
        var hue = Math.Atan2(dy, dx) * 180 / Math.PI;

        var (red, green, blue) = HsvToRgb(hue, s, 1);
        SetColor(Color.FromRgb(red, green, blue));
        Picked?.Invoke(red, green, blue, commit);
    }

    // ---------------------------------------------------------------- 绘制

    protected override Size MeasureOverride(Size availableSize)
    {
        // 正方形：宽高取同一个约束
        var w = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? w : availableSize.Height;
        var side = Math.Max(40, Math.Min(w, h));
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var r = Math.Min(w, h) / 2;
        if (r < 4) return;

        EnsureBitmap(w, h);
        if (_bitmap is not null) dc.DrawImage(_bitmap, new Rect(0, 0, w, h));

        dc.DrawEllipse(null, RingPen, new Point(w / 2, h / 2), r, r);

        // 指示环：白 2px + 里/外各一圈（色盘从纯白到纯黑都有，单色描边总有一段隐身）
        var (hue, sat, _) = RgbToHsv(_color);
        var rad = hue * Math.PI / 180;
        var dot = new Point(
            w / 2 + Math.Cos(rad) * sat * r,
            h / 2 + Math.Sin(rad) * sat * r);
        dc.DrawEllipse(null, DotHalo, dot, 10.5, 10.5);
        dc.DrawEllipse(null, DotBlack, dot, 9.5, 9.5);
        dc.DrawEllipse(null, DotWhite, dot, 8.5, 8.5);
    }

    /// <summary>按当前尺寸 + DPI 重画一次位图；尺寸没变直接返回。</summary>
    private void EnsureBitmap(double w, double h)
    {
        var dpr = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var bw = Math.Max(8, (int)Math.Round(w * dpr));
        var bh = Math.Max(8, (int)Math.Round(h * dpr));
        if (_bitmap is not null && bw == _bw && bh == _bh && Math.Abs(dpr - _dpr) < 0.01) return;

        // 同一尺寸 + 同一 DPI 的位图已经算过（上一次窗口会话）⇒ 直接复用
        if (_cacheBitmap is not null && bw == _cacheW && bh == _cacheH && Math.Abs(dpr - _cacheDpr) < 0.01)
        {
            _bitmap = _cacheBitmap;
            _bw = bw;
            _bh = bh;
            _dpr = dpr;
            return;
        }

        _bw = bw;
        _bh = bh;
        _dpr = dpr;

        var pixels = new byte[bw * bh * 4];
        var cx = (bw - 1) / 2.0;
        var cy = (bh - 1) / 2.0;
        var radius = Math.Min(bw, bh) / 2.0 - 1;

        for (var y = 0; y < bh; y++)
        {
            var dy = y - cy;
            for (var x = 0; x < bw; x++)
            {
                var dx = x - cx;
                var rr = Math.Sqrt(dx * dx + dy * dy);
                if (rr > radius) continue;                      // 圆外保持透明

                var (red, green, blue) = HsvToRgb(Math.Atan2(dy, dx) * 180 / Math.PI,
                    Math.Min(1, rr / radius), 1);

                var i = (y * bw + x) * 4;
                pixels[i] = blue;                                // BGRA
                pixels[i + 1] = green;
                pixels[i + 2] = red;
                // 最外 1.5px 做淡出，免得边缘是硬锯齿
                pixels[i + 3] = rr > radius - 1.5
                    ? (byte)Math.Round(Math.Max(0, (radius - rr) / 1.5) * 255)
                    : (byte)255;
            }
        }

        _bitmap = new WriteableBitmap(bw, bh, 96 * dpr, 96 * dpr, PixelFormats.Bgra32, null);
        _bitmap.WritePixels(new Int32Rect(0, 0, bw, bh), pixels, bw * 4, 0);
        _bitmap.Freeze();
        _cacheBitmap = _bitmap;
        _cacheW = bw;
        _cacheH = bh;
        _cacheDpr = dpr;
    }

    // ---------------------------------------------------------------- 颜色换算

    /// <summary>
    /// hsv2rgb（h 度、s/v 0~1）。数学在 <see cref="ColorMath"/>，这里只把结果收成 byte ——
    /// 调色盘画的是像素缓冲（BGRA 每通道一个字节）。
    /// ⚠️ 别在这里再写一份换算：色块与调色盘必须同源，否则同一色相会显示成两个颜色。
    /// </summary>
    private static (byte R, byte G, byte B) HsvToRgb(double hue, double s, double v)
    {
        var (r, g, b) = ColorMath.HsvToRgb(hue, s, v);
        return ((byte)r, (byte)g, (byte)b);
    }

    private static (double H, double S, double V) RgbToHsv(Color color)
        => ColorMath.RgbToHsv(color.R, color.G, color.B);


    private static Pen MakePen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
