using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShapePath = System.Windows.Shapes.Path;

namespace QControl.App.Ui.Controls;

/// <summary>
/// 风扇图案：进度环 + 环内淡叶轮（转速越快转得越快）+ 正中读数 + 下方名字。
///
/// 结构：竖 StackPanel = [ 圆盘 Grid + 名字 ]。
/// 圆盘 Grid 从下往上：<see cref="RingGauge"/>（轨道 + 内盘 + 进度弧）→ 叶轮 → 读数。
///
/// ⚠️ 满量程 <see cref="RpmMax"/> = 4845 是**实测刻度**，不是估算：本机 100% 占空比两路峰值
///    5026 / 4845，取较低那一路 ⇒ 满转时两个环都画满。换机型要跟着实测改。
/// ⚠️ 转速为 0 / 读不到时**停转**并把弧归零 —— 让它空转就是"显示错的"。
/// ⚠️ 叶轮要挖空中间（CSS 用 radial-gradient 做 mask），否则叶片线条压在数字上互相干扰。
/// </summary>
internal sealed class FanDial : StackPanel
{
    /// <summary>满量程刻度（见类注释里的实测依据）。</summary>
    private const double RpmMax = 4845;

    private readonly RingGauge _ring;
    private readonly ShapePath _blades;
    private readonly RotateTransform _spin = new(0, 0.5, 0.5);
    private readonly TextBlock _value;
    private readonly TextBlock _caption;

    /// <summary>
    /// 叶轮那支**永不重建**的时钟（见 <see cref="Spin"/>）。转速变化只改它的
    /// <c>SpeedRatio</c>，绝不 <c>BeginAnimation</c> 新动画。
    /// </summary>
    private AnimationClock? _clock;

    /// <summary>当前时钟速率（圈/秒）；0 = 冻结在当前位置。<c>-1</c> = 还没设过。</summary>
    private double _rate = -1;

    /// <summary>最近一次写进来的转速（重新进页时用它接着转）。</summary>
    private long _rpm;


    public FanDial(string caption, double size = 123)
    {
        Orientation = Orientation.Vertical;
        HorizontalAlignment = HorizontalAlignment.Center;

        _ring = new RingGauge
        {
            Width = size,
            Height = size,
            // 实测：直径 123、环宽 13（内盘 97）⇒ 0.106
            // 环宽 0.09（原 0.106）：13px 的粗环 + 整圈光晕在青色下会糊成一整坨。
            StrokeRatio = 0.09,
        };
        _ring.SetResourceReference(RingGauge.RingBrushProperty, "AccBrush");
        _ring.SetResourceReference(RingGauge.TrackBrushProperty, "RingTrackBrush");
        // 内盘用 Panel2 不用 Panel：浅色下 Panel 是纯白、卡片也是白，甜甜圈会消失成一条灰带
        _ring.SetResourceReference(RingGauge.CoreBrushProperty, "RingCoreBrush");

        // 叶轮：24×24 的图标铺满 92%，用不透明度掩膜把中间挖空（叶片只在数字外侧成圈）
        _blades = new ShapePath
        {
            Data = Icons.Fan,
            Stretch = Stretch.Uniform,
            // ⚠️ WPF 的 Stretch 只缩几何、**不缩描边**，而图标是
            //    viewBox 24 放大到 99px（1.5 × 99/24 ≈ 6.2）。写 1.5 会得到一根发丝
            //    （写小了叶轮看着像几根细线，不像风扇）。
            StrokeThickness = size * 0.05,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            // 叶片必须完全落在内盘里（内缘直径 = size - 光晕1.5w - 环宽 w ≈ 77%），
            // 原来 0.0975（占 80.5%）会让叶片压到轨道环上、12 点方向还探出环外。
            Margin = new Thickness(size * 0.116),
            IsHitTestVisible = false,
            RenderTransform = _spin,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Opacity = 0.32,
        };
        _blades.SetResourceReference(ShapePath.StrokeProperty, "AccBrush");
        // ⚠️ 试过给叶轮挂 <c>BitmapCache</c>（指望"转=贴图"），**放弃了**：
        //    缓存层按 DIP 尺寸栅格化，在 150% 缩放的屏上要按 RenderAtScale 再放大，
        //    放大 1.5 倍就是一圈虚边（叶片本来就只有 32% 不透明度，糊了很难看）；
        //    而实测收益全在**限帧**上（见 <see cref="Spin"/>），缓不缓存差别不大。
        var mask = new RadialGradientBrush { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.29));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.58));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1.0));
        mask.Freeze();
        _blades.OpacityMask = mask;

        _value = new TextBlock
        {
            Text = "--",
            // 读数：1050 宽下 17.85px ⇒ 直径的 14.5%
            FontSize = size * 0.145,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = size * 0.152,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        };
        _value.SetResourceReference(TextBlock.ForegroundProperty, "TxtBrush");

        var unit = new TextBlock
        {
            Text = "RPM",
            FontSize = 9.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        unit.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");

        var mid = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        mid.Children.Add(_value);
        mid.Children.Add(unit);

        var dial = new Grid { Width = size, Height = size };
        dial.Children.Add(_ring);
        dial.Children.Add(_blades);
        dial.Children.Add(mid);
        Children.Add(dial);

        _caption = new TextBlock
        {
            Text = caption,
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "DimBrush");
        Children.Add(_caption);

        // 「看不见就别转」：页面被换下来（Unloaded）时把时钟摘掉 —— 否则这两支永不停的
        // 动画会一直跟着页面对象跑到下次进页，白白占着合成帧（软件渲染下每帧都要重画叶轮）。
        // 重新进树（Loaded）时按最近一次转速接着转。
        Loaded += (_, _) => Spin(_rpm, restart: true);
        Unloaded += (_, _) =>
        {
            _spin.BeginAnimation(RotateTransform.AngleProperty, null);
            _clock = null;
            _rate = -1;
        };
    }

    /// <summary>写一次转速。null = 这一路读不到（弧归零、叶轮停、读数留占位横线）。</summary>
    public void SetRpm(long? rpm)
    {
        var ok = rpm is { } v && v > 0;
        _value.Text = rpm?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "--";
        _ring.Value = ok ? Math.Min(100, rpm!.Value / RpmMax * 100) : 0;
        _rpm = ok ? rpm!.Value : 0;
        Spin(_rpm);
    }

    /// <summary>
    /// 叶轮转动。CSS 是 <c>animation-duration: max(.55s, 2.6s - rpm/4845*2s)</c> + play-state 切换。
    ///
    /// ⚠️ 2026-10-28 深夜第三改（用户报「还是一卡一卡」）：**不再重建动画，只改速度**。
    ///    前两版（30fps → 60fps 限帧、满转放慢到 0.9s/圈）治的是"每帧步进太大"，
    ///    但实测抓到真正的卡点：转速每秒刷新一次，而旧代码按 <c>rpm/200</c> 分桶当重启判据 ——
    ///    风扇转速在负载下抖几十到几百 RPM，一旦跨过桶边界就 <c>BeginAnimation</c> 一支
    ///    **从 0° 开始**的新动画，叶轮当场跳回去。探针在真实散热页上量到：8 秒内 0~3 次
    ///    **124° / 244° / 339°** 的角度突跳，肉眼就是"隔几秒顿一下"。
    ///    现在用一支 <see cref="AnimationClock"/>（1s/圈基准 + <c>RepeatBehavior.Forever</c>），
    ///    转速只写 <c>SpeedRatio</c>：角度绝对连续，停转时冻结在原位（0 转速也不回 0°）。
    ///
    /// ⚠️ 限帧 60 保留：叶片 3 叶对称 ⇒ 视觉周期只有 120°，60fps 下每帧 ≤7° 才连贯
    ///    （30fps 时每帧 21.8°，看到的是"卡针"）。成本实测 30fps ≈ 13% 单核 / 60fps ≈ 26%
    ///    （软件渲染，1180×780@150%，两个叶轮），只在散热页可见时跑（Unloaded 摘钟）。
    /// </summary>
    private void Spin(long rpm, bool restart = false)
    {
        if (restart)
        {
            // 重新进页：旧时钟已经跟着 Unloaded 摘掉了，这里从 0° 起步（页刚出现，看不出来）
            _clock = null;
            _rate = -1;
        }

        // 满转 0.9s/圈（400°/s），慢转 2.6s/圈
        var rate = rpm > 0 ? 1.0 / Math.Max(0.9, 2.6 - rpm / RpmMax * 1.7) : 0;

        if (_clock is null)
        {
            if (rate <= 0)
            {
                // 从没转过：静态停在 0°，不用建时钟
                _spin.BeginAnimation(RotateTransform.AngleProperty, null);
                _spin.Angle = 0;
                _rate = 0;
                return;
            }

            var anim = UiMotion.At(
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                }, 60);
            _clock = anim.CreateClock();
            _spin.ApplyAnimationClock(RotateTransform.AngleProperty, _clock);
            _clock.Controller.SpeedRatio = rate;
            _rate = rate;
            return;
        }

        // 已经在转：只改速率。SpeedRatio 的改动**不影响当前进度**，所以角度不会跳。
        if (Math.Abs(rate - _rate) < 0.0005) return;
        _clock.Controller.SpeedRatio = rate;
        _rate = rate;
    }
}
