using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using QControl.Core.Devices;
using ShapePath = System.Windows.Shapes.Path;

namespace QControl.App.Ui.Controls;

/// <summary>
/// 键盘示意图：机身底盘 + 键盘底座 + 触控板 + 分区发光键帽。
///
/// 为什么不用 <c>OnRender</c> 一把画完：发光需要一层模糊滤镜，
/// WPF 的对应物是挂在元素上的 <see cref="BlurEffect"/> —— 只有"元素"能挂效果，
/// 所以每区的柔光必须是一层独立元素；键帽文字则另一个自绘层（<see cref="TextLayer"/>）。
/// 每区三层各管一件事：soft（整区柔光，一张预乘颜色的位图，见 <see cref="GlowLayer"/>）/
/// cap（键帽描边+底）/ txt（字母）。
///
/// ⚠️ 键位表照这台机器的实际布局，别按"标准 ANSI 15u"想当然改：
///    F 行 19 个 1u 键横跨 19u、底行只到 11u、方向键 1.2u 右对齐到 15u、
///    小键盘 5 行且 + / ↵ 各跨两行。
/// ⚠️ **分区数不是固定的三区**（2026-11-14）：机型档案的 `caps.kb_zones` 说了算，
///    1 / 2 / 3 / 4 都要能画（真实的只有 1 和 3，2 / 4 极罕见，5 以上不存在 ——
///    见 <see cref="BacklightZones.MaxSupported"/>）。三区按**原厂实拍**的切法
///    （<see cref="KbZoneLayout"/>：F 行和下面几行还是两套边界）；其它数量按主键区 0~15u 等分 ——
///    原厂只给了地址、没给物理位置，这是示意图上唯一不撒谎的画法（tab 的提示里会写明"位置按色彩数均分示意"）。
///    方向键与小键盘固定算最后一区（三区时就是 C，与原标定一致）。
/// ⚠️ 尺寸比例由 <see cref="MeasureOverride"/> 自己算（宽定高 = 宽 × 360/620），
///    对应 SVG 的 <c>preserveAspectRatio="xMidYMid meet"</c>。改了比例键帽会被压扁。
/// </summary>
internal sealed class KeyboardView : Grid
{
    // ---------------------------------------------------------------- 画布坐标
    // 全部是 SVG viewBox（0 0 620 360）里的逻辑坐标，由外层 Viewbox 等比缩到实际尺寸。
    private const double ViewWidth = 620;
    private const double ViewHeight = 360;
    private const double Aspect = ViewHeight / ViewWidth;

    private const double PadX = 30, PadY = 62, PadW = 560, PadH = 216;
    private const double Pin = 8, Gap = 5;

    /// <summary>
    /// 分区切法（绿到哪、红从哪起）**不在本文件**：那是原厂实拍下来的事实，
    /// 放在 <see cref="KbZoneLayout"/>，由单测看着。这里只管画。
    /// （2026-11-17 的事故现场：切法散在画法里，改一次坐标就可能悄悄改掉"灯亮错一格"。）
    /// </summary>
    private const double ArrowW = 1.2;

    private readonly Viewbox _view;
    private readonly Canvas _canvas;

    private readonly ShapePath _deck;
    private readonly ShapePath _hinge;
    private readonly ShapePath _pad;
    private readonly ShapePath _touch;
    private readonly ShapePath _mark;

    private ZoneLayer[] _zones = [];
    private TextLayer[] _text = [];

    /// <summary>
    /// 当前画着的键位表（与 <see cref="EnsureZones"/> 最后一次用的一致）。
    /// 只有一个用途：把点击坐标换算成"点到哪一区"（见 <see cref="ZoneAt"/>）。
    /// </summary>
    private List<KeyBox> _layout = [];

    private static readonly int[] White = [255, 255, 255];

    private int[][] _rgb = [[255, 255, 255], [255, 255, 255], [255, 255, 255]];
    private int _active;

    /// <summary>每区一支**可变**的实色画笔（拖着色盘连续改色时不换实例，只改 Color）。</summary>
    private SolidColorBrush[] _zoneBrush = [];

    /// <summary>上次真正画上去的分区颜色 / 当前区 / 主题 —— 用来"没变就别动"。</summary>
    private int[][] _painted = [];
    private int _paintedActive = -1;
    private bool? _paintedLight;

    /// <summary>当前画着几个分区（0 = 还没建过）。换机型分区数不同时才重建图层。</summary>
    private int _zoneCount;

    /// <summary>
    /// 所有分区共用的两层模糊（半径相同 ⇒ 一个实例够）。
    ///
    /// ⚠️ <b>必须共享 + 冻结，别在每个 Path 上各 new 一个</b>：未冻结的 Effect 挂在元素上
    ///    要建表达式监听，每个实例约 8ms —— 6 个路径各建一个实测把背光页首次进入
    ///    的 XAML 构造从 10ms 顶到 58ms（用户反馈"点进变光页有点卡"就是这个）。
    ///    这两个实例只在 <see cref="RenderMask"/> 里把几何栅格化成 alpha 遮罩时用一次，
    ///    用完就不再挂在任何元素上（更不会进每帧的绘制路径）。
    /// </summary>
    private static readonly BlurEffect BlurHalo = FrozenEffect(new BlurEffect { Radius = 14 });
    private static readonly BlurEffect BlurGlow = FrozenEffect(new BlurEffect { Radius = 8 });

    private static BlurEffect FrozenEffect(BlurEffect effect)
    {
        effect.Freeze();
        return effect;
    }

    public KeyboardView()
    {
        _canvas = new Canvas { Width = ViewWidth, Height = ViewHeight };

        // 点键盘图上任意位置 = 切到那一区（见 ZoneClicked / ZoneAt）。
        // Background 必须给一支**透明**画笔：Grid/Canvas 的 Background 为 null 时整块区域
        // 根本不参与命中测试（只有键帽描边那几条细线能点到），点了就像没反应。
        _canvas.Background = Brushes.Transparent;
        _canvas.MouseLeftButtonUp += Canvas_MouseLeftButtonUp;
        _canvas.MouseMove += Canvas_MouseMove;

        _deck = Chassis(new RectangleGeometry(new Rect(14, 14, 592, 332), 18, 18),
            "KbDeckBrush", "LineBrush");
        _hinge = Chassis(new RectangleGeometry(new Rect(44, 24, 532, 14), 5, 5), "LineBrush", null);
        _hinge.Opacity = 0.55;
        _pad = Chassis(new RectangleGeometry(new Rect(PadX, PadY, PadW, PadH), 10, 10),
            "KbPadBrush", "LineBrush");
        _pad.StrokeThickness = 1;
        _pad.Opacity = 0.92;
        _touch = Chassis(new RectangleGeometry(new Rect(250, PadY + PadH + 12, 120, 42), 9, 9),
            null, "LineBrush");
        _mark = Chassis(MarkGeometry(), null, "LineBrush");

        _canvas.Children.Add(_deck);
        _canvas.Children.Add(_hinge);
        _canvas.Children.Add(_pad);
        _canvas.Children.Add(_touch);
        _canvas.Children.Add(_mark);

        EnsureZones(3);

        _view = new Viewbox
        {
            Child = _canvas,
            Stretch = Stretch.Fill,      // 比例已由本控件自己保证，这里只负责铺满
            StretchDirection = StretchDirection.Both,
        };
        Children.Add(_view);

        RefreshTheme();
    }

    /// <summary>示意图上最多画几个分区。与 <see cref="AdoptGuide.MaxZones"/> 同一口径（tab 再多也挤不下）。</summary>
    internal const int MaxZones = AdoptGuide.MaxZones;

    /// <summary>
    /// 按分区数建/重建发光图层（柔光 + 键帽 + 字母）。分区数没变就什么都不做。
    ///
    /// 为什么要能重建：机型档案的 `caps.kb_zones` 可能是 1/2/4 区（见类注释）。
    /// 重建只摘自己建的图层，底盘 / 触控板那些静态元素原样留着；位置顺序也与构造时一致
    /// （每区 soft → cap → text），否则后加的柔光会盖住前一区的字母。
    /// </summary>
    private void EnsureZones(int count)
    {
        count = Math.Clamp(count, 1, MaxZones);
        if (count == _zoneCount && _zones.Length == count) return;

        foreach (var z in _zones)
        {
            _canvas.Children.Remove(z.Soft.Rect);
            _canvas.Children.Remove(z.Cap);
        }
        foreach (var t in _text) _canvas.Children.Remove(t);

        _zoneCount = count;
        _zones = new ZoneLayer[count];
        _text = new TextLayer[count];
        _zoneBrush = new SolidColorBrush[count];
        _painted = new int[count][];
        for (var i = 0; i < count; i++) _painted[i] = [-1, -1, -1];

        var layout = CachedLayout(count);
        _layout = layout;
        var masks = EnsureMasks(count, layout);

        for (var i = 0; i < count; i++)
        {
            var keys = layout.Where(k => k.Zone == i).ToList();

            // 光晕 + 键帽发光合成**一层**：两层的颜色永远相同、只有 alpha 不同，
            // 所以可以在位图里直接合成（A = A1 + A2·(1−A1)），逐像素结果与"两层叠着画"完全一致。
            // ⚠️ 2026-10-28 晚：原来这里是两个「纯色矩形 + OpacityMask(ImageBrush)」，
            //    软件光栅下六张遮罩矩形实测吃掉 12.9ms（整块键盘 17.5ms 里最大的一笔），
            //    而同样的区域画一张**预乘颜色位图**只要 ~1.5ms —— OpacityMask 每帧都要
            //    建中间表面 + 逐像素双线性采样 32bpp 遮罩，比直接 alpha 混合贵一个数量级。
            var soft = new GlowLayer(masks[i], fade: 1.0, light: false);
            var cap = new ShapePath
            {
                Data = RectsGeometry(keys),
                IsHitTestVisible = false,
            };

            _canvas.Children.Add(soft.Rect);
            _canvas.Children.Add(cap);
            _zones[i] = new ZoneLayer(soft, cap);
            _zoneBrush[i] = new SolidColorBrush();

            // 字母层跟着这一区的键帽包围盒走（见 TextLayer 注释：分层就为了让脏区变小）
            var box = UnionRects(keys);
            var text = new TextLayer(keys, box.X, box.Y) { Width = box.Width, Height = box.Height };
            Canvas.SetLeft(text, box.X);
            Canvas.SetTop(text, box.Y);
            _canvas.Children.Add(text);
            _text[i] = text;
        }

        // 新图层一律从"什么都没画"开始：painted 的哨兵值已经清了，主题/当前区也得重判
        _paintedActive = -1;
        _paintedLight = null;
    }

    // ---------------------------------------------------------------- 预模糊遮罩

    /// <summary>
    /// 一区的两张柔光**原始 alpha**（各 0~255，与 <see cref="Area"/> 同像素尺寸）+ 落位。
    ///
    /// 为什么只存 alpha 不存位图：三层柔光的颜色永远等于区色，只有 alpha 不同，
    /// 所以真正需要"每变一次颜色就重算"的只有 alpha × 颜色的乘积
    /// （见 <see cref="GlowLayer"/>），位图里不存任何颜色信息。
    /// </summary>
    private sealed record SoftMask(byte[] Halo, byte[] Glow, int W, int H, Rect Area);

    /// <summary>
    /// 遮罩缓存（进程级，**按分区数分开存**）。键位表、画布尺寸、遮罩像素尺寸都是常量
    /// ⇒ 与 DPI 无关，换窗口会话、切主题、拖色盘都不该重算。
    /// </summary>
    private static readonly Dictionary<int, SoftMask[]> MaskCache = [];

    /// <summary>
    /// 预热：三区柔光遮罩（见 <see cref="EnsureMasks"/>）+ 键位表。窗口空闲时由 MainWindow 调一次，
    /// 把那 6 次高斯模糊和 130 个键帽几何从"点击背光页那一帧"挪到用户不会注意的时刻。
    /// 只预热三区（本机就是三区）；别的分区数在第一次进页时按需算，一次几毫秒。
    /// </summary>
    internal static void Prewarm() => EnsureMasks(3, CachedLayout(3));

    private static SoftMask[] EnsureMasks(int zones, IReadOnlyList<KeyBox> layout)
    {
        if (MaskCache.TryGetValue(zones, out var cached)) return cached;

        var masks = new SoftMask[zones];
        for (var i = 0; i < zones; i++)
        {
            var keys = layout.Where(k => k.Zone == i).ToList();
            if (keys.Count == 0)
            {
                // 空分区（N ≤ 6 的等分下不会出现）：给一张 1×1 空遮罩，
                // 别让 GlowLayer 因为 Rect.Empty / 全零位图崩掉。
                masks[i] = new SoftMask([0], [0], 1, 1, new Rect(0, 0, 1, 1));
                continue;
            }

            var haloGeo = HaloGeometry(keys);
            // 模糊会向外扩散（≈3σ，Radius≈2σ ⇒ 约 1.5×Radius），留足边距免得被裁
            var area = haloGeo.Bounds;
            area.Inflate(BlurPad, BlurPad);

            masks[i] = RenderMask(haloGeo, area, BlurHalo, RectsGeometry(keys), BlurGlow);
        }
        MaskCache[zones] = masks;
        return masks;
    }

    /// <summary>模糊向外扩散的余量（画布单位）：glow 半径 8 ⇒ σ4 ⇒ ~12，halo 半径 14 ⇒ σ7 ⇒ ~21。</summary>
    private const double BlurPad = 24;

    /// <summary>
    /// 把两层的纯白几何按各自模糊渲成位图，只留 alpha。
    ///
    /// ⚠️ 位图按 **1.0 像素/DIP** 栅格化（不再是 1.5 倍 DPI）：整块画布最后还要被外层
    ///    <see cref="Viewbox"/> 缩一次（620 → 约 505 DIP，0.81），所以按 DIP 存的图在显示时
    ///    仍然是**缩小**采样，不会糊；内存却只有原来的 1/2.25。柔光本来就是模糊图。
    /// ⚠️ 平移必须做在**几何**上，不能靠 Arrange 的负偏移、也不能靠 RenderTransform：
    ///    元素的内容从自己的局部坐标画，几何的绝对坐标一旦超出 RenderSize 就被裁掉
    ///    （实测第三区整片柔光凭空消失）。Geometry.Transform 是数据层面的，稳。
    /// </summary>
    private static SoftMask RenderMask(Geometry haloGeo, Rect area, BlurEffect haloBlur,
                                       Geometry glowGeo, BlurEffect glowBlur)
    {
        var w = Math.Max(1, (int)Math.Ceiling(area.Width));
        var h = Math.Max(1, (int)Math.Ceiling(area.Height));

        return new SoftMask(Alpha(haloGeo, haloBlur), Alpha(glowGeo, glowBlur), w, h, area);

        byte[] Alpha(Geometry geo, BlurEffect blur)
        {
            var shifted = geo.Clone();
            shifted.Transform = new TranslateTransform(-area.X, -area.Y);
            shifted.Freeze();

            var path = new ShapePath
            {
                Data = shifted,
                Fill = Brushes.White,
                Effect = blur,
            };
            path.Measure(new Size(area.Width, area.Height));
            path.Arrange(new Rect(0, 0, area.Width, area.Height));

            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(path);

            var stride = w * 4;
            var pixels = new byte[stride * h];
            bitmap.CopyPixels(pixels, stride, 0);

            var alpha = new byte[w * h];
            for (var i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3];
            return alpha;
        }
    }

    /// <summary>
    /// 一区的柔光层：一张**预乘颜色的位图**（halo 与 glow 已在像素里合成）贴在一块
    /// <see cref="Rectangle"/> 的 Fill 上。
    ///
    /// 为什么不用「纯色 Fill + OpacityMask」：软件光栅下 OpacityMask 的每一帧都要
    /// 建中间表面、再逐像素双线性采样一张 32bpp 遮罩 —— 实测六张矩形 12.9ms，
    /// 是整块键盘（17.5ms）里最大的一笔，也是"切进背光页卡一下"的主因。
    /// 换成预乘位图后同一区域直接 alpha 混合，六张合计 ~1.5ms（缩小到 1/8）。
    ///
    /// 代价是改颜色要重烙一次位图 —— 但只有"颜色/明暗/主题真的变了"才烙，
    /// 且一区一次约 0.2ms（拖色盘时只烙正在拖的那一区，比原来还快）。
    /// 数学上与两层叠加**逐像素等价**（两层同色 ⇒ A = A1 + A2·(1−A1)）。
    /// </summary>
    private sealed class GlowLayer
    {
        private readonly byte[] _halo;
        private readonly byte[] _glow;
        private readonly int _w, _h;
        private readonly WriteableBitmap _bitmap;
        private readonly byte[] _buffer;

        /// <summary>上次烙进位图的参数（没变就跳过整个像素循环）。</summary>
        private Color _color;
        private double _op1 = -1, _op2 = -1;

        public Rectangle Rect { get; }

        public GlowLayer(SoftMask mask, double fade, bool light)
        {
            _halo = mask.Halo;
            _glow = mask.Glow;
            _w = mask.W;
            _h = mask.H;

            _bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Pbgra32, null);
            _buffer = new byte[_w * _h * 4];

            // ⚠️ 画笔不 Freeze：底下的 WriteableBitmap 要能原地改写（Freeze 之后就写不进去了）。
            //    全进程只有 3 个实例，且都是长命的 ⇒ 不会像"每帧 new 一支画笔"那样把元素全部作废。
            Rect = new Rectangle
            {
                Width = mask.Area.Width,
                Height = mask.Area.Height,
                Fill = new ImageBrush(_bitmap) { Stretch = Stretch.Fill },
                IsHitTestVisible = false,
            };

            // ⚠️ 采样档位钉成双线性：位图是 DIP 尺寸，显示时还要被 Viewbox 缩一次，
            //    默认档在软件渲染下会逐像素做高质量重采样（柔光看不出差别，纯烧 CPU）。
            RenderOptions.SetBitmapScalingMode(Rect, BitmapScalingMode.LowQuality);
            Canvas.SetLeft(Rect, mask.Area.X);
            Canvas.SetTop(Rect, mask.Area.Y);

            Paint(Colors.White, fade, light);
        }

        /// <summary>
        /// 把「区色 × 两层遮罩 × 明暗系数」烙进位图。没变就整段跳过（连像素循环都不进）。
        /// <paramref name="fade"/> = 这一区现在的明暗（当前区 1.0，其它区 .26 / 浅色 .55），
        /// <paramref name="light"/> 决定光晕的两个基础系数 —— 这两个值原来写在元素的
        /// Opacity 上（halo .20 / glow .78，浅色 .09 / .77），现在直接乘进像素。
        /// </summary>
        public void Paint(Color color, double fade, bool light)
        {
            var op1 = (light ? 0.09 : 0.20) * fade;
            var op2 = 0.78 * fade * (light ? 0.77 : 1.0);
            if (color == _color && Math.Abs(op1 - _op1) < 1e-6 && Math.Abs(op2 - _op2) < 1e-6) return;

            _color = color;
            _op1 = op1;
            _op2 = op2;

            var r = color.R;
            var g = color.G;
            var b = color.B;
            var n = _w * _h;

            for (var i = 0; i < n; i++)
            {
                // A = A1 + A2·(1−A1)：先 halo 再 glow 叠上去的等价式（两层同色）
                var a1 = _halo[i] * op1;
                var a2 = _glow[i] * op2;
                var a = a1 + a2 - a1 * a2 / 255.0;
                if (a <= 0) continue;

                var o = i * 4;
                _buffer[o] = (byte)(b * a / 255.0);        // BGRA，且已是预乘
                _buffer[o + 1] = (byte)(g * a / 255.0);
                _buffer[o + 2] = (byte)(r * a / 255.0);
                _buffer[o + 3] = (byte)a;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), _buffer, _w * 4, 0);
        }
    }

    // ---------------------------------------------------------------- 对外

    /// <summary>
    /// 写分区颜色 + 当前在编辑哪一区（只是界面状态，不写 EC）。
    /// 分区数与当前图层不同（换机型）时会先重建图层，再整块重刷。
    /// </summary>
    public void SetZones(int[][] zones, int active)
    {
        if (zones.Length == 0) return;
        EnsureZones(zones.Length);
        _rgb = zones;
        _active = Math.Clamp(active, 0, zones.Length - 1);
        PaintZones(force: true);
    }

    /// <summary>换主题重新上色：键帽/机身底色来自令牌，键帽字母在浅色下要钉成深色。</summary>
    public void RefreshTheme()
    {
        var pal = UiTheme.Palette;
        var line = Brush(pal.Line);

        _deck.StrokeThickness = 1;
        _deck.Stroke = line;
        _hinge.Fill = line;
        // CSS: .kb-pad{stroke-opacity:.6} —— 只有描边半透明，填充保持实色，
        // 所以这里换一支带 alpha 的线色，而不是给整个 Path 设 Opacity。
        _pad.Stroke = Brush(Color.FromArgb(0x99, pal.Line.R, pal.Line.G, pal.Line.B));
        _pad.StrokeThickness = 1;
        _touch.Stroke = line;
        _mark.Stroke = line;
        _mark.Opacity = 0.7;      // .kb-mark{opacity:.7}（无填充，整块设透明度等价）

        // 浅色下光晕要收敛（白底上柔光只会发灰），未选中区也别压到 .26（白底上等于消失）
        // —— 这两个系数现在由 GlowLayer.Paint 直接算进像素（light 参数），这里不再存一份
        var light = ReferenceEquals(pal, UiPalette.Light);

        // 键帽底色 / 描边粗细只跟主题有关 ⇒ 只在这里赋，逐帧上色不再重复（见 PaintZones）
        var capFill = Brush(pal.KbKey);
        foreach (var zone in _zones)
        {
            zone.Cap.Fill = capFill;
            zone.Cap.StrokeThickness = light ? 1.4 : 1.0;
        }

        PaintZones(force: true);      // 主题变了：整块重刷（连字母层）
    }

    /// <summary>当前哪一区在编辑（切 tab / 上色时用）。</summary>
    public int Active
    {
        get => _active;
        set { _active = value; PaintZones(); }
    }

    /// <summary>
    /// 点了键盘图上的某一区（参数 = 区号，从 0 起）。这里只是**界面动作**：
    /// 页面收到后把"色盘在改哪一区"切过去，写不写 EC 由页面决定（与点分区 tab 完全同一件事）。
    ///
    /// ⚠️ **无条件抛**，包括"点的就是当前这一区"：`_active` 只表示键盘图上高亮哪一块，
    /// 色盘此刻可能在改 Logo / 尾灯（那是页面的状态，这个控件不知道）。在这里按 `_active`
    /// 拦一次，用户选中 Logo 后再点"键盘上亮着的那一区"就永远回不来（色盘一直改 Logo）。
    /// 幂等由页面负责（见 <c>LightView.SelectZone</c> 的早退）。
    /// </summary>
    public event EventHandler<int>? ZoneClicked;

    /// <summary>
    /// 画布坐标 → 分区号。
    ///
    /// 先看键帽本身（逐颗矩形命中）；点在键帽之间的缝里（Gap 5 画布单位）时退到
    /// **最近的键帽** —— 一格缝就 1~2 像素，让用户瞄准键缝是欺负人。
    /// 点在键盘底座之外（机身两侧、触控板、掌托）返回 null：那是"没点到键盘"，不猜一个区出来。
    /// </summary>
    private int? ZoneAt(Point p)
    {
        if (_layout.Count == 0) return null;

        foreach (var k in _layout)
            if (k.Cap.Rect.Contains(p)) return k.Zone;

        if (!new Rect(PadX, PadY, PadW, PadH).Contains(p)) return null;

        var bestD2 = double.MaxValue;
        var best = -1;
        for (var i = 0; i < _layout.Count; i++)
        {
            var k = _layout[i];
            var dx = p.X - k.Cx;
            var dy = p.Y - k.Cy;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = i; }
        }

        // 兜底只负责"键缝 / 底座内边距"这点距离（一颗键帽的尺度），再远就不认了
        return best >= 0 && bestD2 <= SnapRadius * SnapRadius ? _layout[best].Zone : null;
    }

    /// <summary>键缝兜底的最大距离（画布单位）≈ 一颗键帽的半径。</summary>
    private const double SnapRadius = 26;

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ZoneAt(e.GetPosition(_canvas)) is not { } zone) return;
        e.Handled = true;                       // 别再让外面的卡片/滚动条当成一次点击
        ZoneClicked?.Invoke(this, zone);         // 同一区再点一次也照发：页面自己判重
    }

    /// <summary>鼠标停在键盘上给手型（可点），离开键盘面/机身两侧回到箭头。</summary>
    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var want = ZoneAt(e.GetPosition(_canvas)) is null ? Cursors.Arrow : Cursors.Hand;
        if (!ReferenceEquals(_canvas.Cursor, want)) _canvas.Cursor = want;
    }

    // ---------------------------------------------------------------- 上色

    /// <summary>
    /// 分区上色。**只碰真正变了的那一区** —— 拖色盘时每帧只改一区的颜色，但柔光一旦重烙
    /// 就会把整块"那一区的柔光矩形"弄脏，一次全刷就是 3 倍的钱（实测：只刷一区 6.7ms/帧，
    /// 全刷是它的三倍）。改写画笔也是同一个道理：换画笔实例等于把元素全部作废，
    /// 所以每区只有一支长命的可变画笔（现在只剩键帽描边在用）。
    /// </summary>
    /// <param name="force">换主题 / 重新进页时整块重刷（见 <see cref="RefreshTheme"/>）。</param>
    private void PaintZones(bool force = false)
    {
        var pal = UiTheme.Palette;
        var light = ReferenceEquals(pal, UiPalette.Light);

        // 换当前区 ⇒ 所有分区的明暗（fade）全变；换主题同理 ⇒ 整块重刷。
        var all = force || _paintedLight != light || _paintedActive != _active;
        var textDirty = all;

        for (var i = 0; i < _zones.Length; i++)
        {
            var rgb = i < _rgb.Length ? _rgb[i] : White;
            var painted = _painted[i];
            if (!all && painted[0] == rgb[0] && painted[1] == rgb[1] && painted[2] == rgb[2]) continue;

            // 非当前区压暗：暗色 .26 / 浅色 .55
            var fade = i == _active ? 1.0 : light ? 0.55 : 0.26;
            var zone = _zones[i];
            var color = Color.FromRgb(ClampByte(rgb[0]), ClampByte(rgb[1]), ClampByte(rgb[2]));

            // 柔光：明暗与颜色一起烙进预乘位图（没有任何"元素 Opacity"了，全在像素里）
            zone.Soft.Paint(color, fade, light);

            // 改颜色不换画笔实例：Fill/Stroke 认的是对象，换实例就是元素全部作废
            _zoneBrush[i].Color = color;
            zone.Cap.Stroke = _zoneBrush[i];
            zone.Cap.Opacity = fade;

            painted[0] = rgb[0];
            painted[1] = rgb[1];
            painted[2] = rgb[2];
            textDirty = true;
        }

        _paintedActive = _active;
        _paintedLight = light;

        // 字母层：一次画 130 多个 FormattedText（≈12ms），颜色没变就别理它；
        // 变了也只刷真正变色的那几区（SetColor 自己比颜色，没变的不重画）。
        if (textDirty)
        {
            for (var i = 0; i < _text.Length; i++)
            {
                var rgb = i < _rgb.Length ? _rgb[i] : White;
                // 浅色主题钉死深色（白键帽 + 白灯 = 看不见字），暗色跟着本区灯色（= 旧 FixedColor 语义）
                var fill = (light ? pal.KbKeyText : null)
                    ?? Color.FromRgb(ClampByte(rgb[0]), ClampByte(rgb[1]), ClampByte(rgb[2]));
                var fade = i == _active ? 1.0 : light ? 0.55 : 0.26;
                _text[i].SetColor(fill, (byte)Math.Round(255 * fade));
            }
        }
    }

    // ---------------------------------------------------------------- 几何

    private static ShapePath Chassis(Geometry geo, string? fillKey, string? strokeKey)
    {
        var path = new ShapePath { Data = geo, IsHitTestVisible = false };
        if (fillKey is not null) path.SetResourceReference(ShapePath.FillProperty, fillKey);
        if (strokeKey is not null)
        {
            path.SetResourceReference(ShapePath.StrokeProperty, strokeKey);
            path.StrokeThickness = 1;
        }
        return path;
    }

    /// <summary>触控板两侧那两条短线（CSS 的 <c>.kb-mark</c>）。</summary>
    private static Geometry MarkGeometry()
    {
        var y = PadY + PadH + 32;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(96, y), false, false);
            ctx.LineTo(new Point(182, y), true, false);
            ctx.BeginFigure(new Point(438, y), false, false);
            ctx.LineTo(new Point(524, y), true, false);
        }
        geo.Freeze();
        return geo;
    }

    /// <summary>一堆键帽矩形合成一条几何（每颗都缩 2.5px、圆角 3）。</summary>
    private static Geometry RectsGeometry(IEnumerable<KeyBox> keys)
    {
        var group = new GeometryGroup();
        foreach (var k in keys) group.Children.Add(k.Cap);
        group.Freeze();
        return group;
    }

    /// <summary>一堆键帽的包围盒（字母层的布局框；字母都在键帽里居中，框住就够）。</summary>
    private static Rect UnionRects(IReadOnlyList<KeyBox> keys)
    {
        if (keys.Count == 0) return Rect.Empty;
        var r = keys[0].Cap.Rect;
        foreach (var k in keys) r.Union(k.Cap.Rect);
        return r;
    }

    /// <summary>整区的柔光底板：所有键帽的包围盒再外扩 6px、圆角 8。</summary>
    private static Geometry HaloGeometry(IReadOnlyList<KeyBox> keys)
    {
        if (keys.Count == 0) return RectangleGeometry.Empty;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var k in keys)
        {
            x0 = Math.Min(x0, k.Cap.Rect.X);
            y0 = Math.Min(y0, k.Cap.Rect.Y);
            x1 = Math.Max(x1, k.Cap.Rect.Right);
            y1 = Math.Max(y1, k.Cap.Rect.Bottom);
        }
        return new RectangleGeometry(new Rect(x0 - 6, y0 - 6, x1 - x0 + 12, y1 - y0 + 12), 8, 8);
    }

    // ---------------------------------------------------------------- 键位表

    /// <summary>一颗键的最终落位：键帽矩形 + 属于哪一区 + 标签（空位 = 没有标签）+ 字号。</summary>
    private sealed record KeyBox(RectangleGeometry Cap, int Zone, string? Label, double Cx, double Cy, double Size);

    /// <param name="W">宽度（单位 u）</param>
    /// <param name="Label">标签；null = 空位（被跨行键占着的地方）</param>
    /// <param name="Rows">跨行数（小键盘 + / ↵ 是 2）</param>
    private readonly record struct Spec(double W, string? Label, int Rows);

    private static Spec K(string label) => new(1, label, 1);

    private static Spec W(double width, string label) => new(width, label, 1);

    private static readonly Spec Null = new(1, null, 1);

    private static readonly Spec[][] Rows =
    [
        [K("Esc"), K("F1"), K("F2"), K("F3"), K("F4"), K("F5"), K("F6"), K("F7"), K("F8"), K("F9"),
         K("F10"), K("F11"), K("F12"), K("PrtSc"), K("Del"), K("Home"), K("End"), K("PgUp"), K("PgDn")],
        [K("`"), K("1"), K("2"), K("3"), K("4"), K("5"), K("6"), K("7"), K("8"), K("9"), K("0"),
         K("-"), K("="), W(2, "⌫")],
        [W(1.5, "Tab"), K("Q"), K("W"), K("E"), K("R"), K("T"), K("Y"), K("U"), K("I"), K("O"),
         K("P"), K("["), K("]"), W(1.5, "\\")],
        [W(1.75, "Caps"), K("A"), K("S"), K("D"), K("F"), K("G"), K("H"), K("J"), K("K"), K("L"),
         K(";"), K("'"), W(2.25, "Enter")],
        [W(2.6, "Shift"), K("Z"), K("X"), K("C"), K("V"), K("B"), K("N"), K("M"), K(","), K("."),
         K("/"), W(2.4, "Shift")],
        [W(1.6, "Ctrl"), K("Fn"), K("⊞"), K("Alt"), W(4.4, "Space"), K("Alt"), K("Ctrl")],
    ];

    private static readonly Spec[][] Arrows =
    [
        [Null, K("↑"), Null],
        [K("←"), K("↓"), K("→")],
    ];

    private static readonly Spec[][] Numpad =
    [
        [K("Num"), K("/"), K("*"), K("-")],
        [K("7"), K("8"), K("9"), new Spec(1, "+", 2)],
        [K("4"), K("5"), K("6"), Null],
        [K("1"), K("2"), K("3"), new Spec(1, "↵", 2)],
        [W(2, "0"), K("."), Null, Null],
    ];

    /// <summary>
    /// 键位表的进程级缓存。键位表是**常量**（画布 620×360、键帽都是冻结的 RectangleGeometry），
    /// 但每建一次背光页就要重新造 130 个几何 —— 那是"首次进页 60ms+"里实打实的一部分。
    /// 冻结对象可以安全跨实例共享（与 <see cref="EnsureMasks"/> 的遮罩缓存同一个理由）。
    /// </summary>
    private static readonly Dictionary<int, List<KeyBox>> LayoutCache = [];

    private static List<KeyBox> CachedLayout(int zones)
    {
        if (LayoutCache.TryGetValue(zones, out var cached)) return cached;
        var built = BuildLayout(zones);
        LayoutCache[zones] = built;
        return built;
    }

    private static List<KeyBox> BuildLayout(int zones)
    {
        var u = (PadW - Pin * 2) / 19;            // 主键区 15u + 小键盘 4u
        var rowH = (PadH - Pin * 2) / 7;          // 7 行：6 行按键 + 方向键那一行
        var keyH = rowH - Gap;
        var x0 = PadX + Pin;
        var y0 = PadY + Pin;

        var list = new List<KeyBox>(140);

        void Put(double x, double y, double w, double h, string? label, int? zone, bool topRow = false)
        {
            var z = zone ?? ZoneOf((x + w / 2 - x0) / u, zones, topRow);
            var cap = new RectangleGeometry(new Rect(x + 2.5, y + 2.5, w - 5, h - 5), 3, 3);
            cap.Freeze();
            // 长标签（PrtSc / Enter / Space）在小键帽里必须缩号，否则压出格
            var size = label is null ? 7 : label.Length >= 5 ? 5 : label.Length >= 4 ? 5.6 : 7;
            list.Add(new KeyBox(cap, z, label, x + w / 2, y + h / 2, size));
        }

        for (var ri = 0; ri < Rows.Length; ri++)
        {
            // F 行的分区比下面几行各宽一格（见 ZoneAFMax / ZoneCFMin）
            var top = ri == 0;
            var x = x0;
            foreach (var k in Rows[ri])
            {
                Put(x, y0 + ri * rowH, k.W * u, keyH, k.Label, null, top);
                x += k.W * u;
            }
        }

        // 方向键：主键区右下角，每颗 1.2u，**右边缘对齐到 15u**
        var arrowX = x0 + (15 - ArrowW * 3) * u;
        for (var ri = 0; ri < Arrows.Length; ri++)
        {
            var x = arrowX;
            foreach (var k in Arrows[ri])
            {
                // 方向键 = 最后一区（三区时就是 C，与真机标定一致）
                if (k.Label is not null) Put(x, y0 + (ri + 5) * rowH, ArrowW * u, keyH, k.Label, zones - 1);
                x += ArrowW * u;
            }
        }

        // 数字小键盘：从第 2 行起、5 行
        var numX = x0 + 15 * u;
        for (var ri = 0; ri < Numpad.Length; ri++)
        {
            var x = numX;
            foreach (var k in Numpad[ri])
            {
                if (k.Label is null)
                {
                    x += u;
                    continue;
                }
                Put(x, y0 + (ri + 1) * rowH, k.W * u, keyH + (k.Rows - 1) * rowH, k.Label, zones - 1);
                x += k.W * u;
            }
        }

        return list;
    }

    /// <summary>
    /// 主键区里某个横坐标（单位 u）属于哪一区 —— 规则在 <see cref="KbZoneLayout.ZoneOf"/>
    /// （原厂实拍的切法，单测盯着），这里只做一次转发。
    /// </summary>
    private static int ZoneOf(double centerU, int zones, bool topRow = false)
        => KbZoneLayout.ZoneOf(centerU, zones, topRow);

    // ---------------------------------------------------------------- 小工具

    private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>一区的两层（柔光 / 键帽）。柔光是「预乘颜色的位图」，键帽是描边几何。</summary>
    private sealed record ZoneLayer(GlowLayer Soft, ShapePath Cap);

    /// <summary>
    /// 一区的键帽字母层：<c>text-anchor:middle</c> + 垂直居中靠 FormattedText 量出来再对中，
    /// 不用 TextBlock —— 100 多颗键各建一个 TextBlock 会让可视树变得很重。
    ///
    /// ⚠️ <b>一区一层，而不是"一层画三区"</b>（2026-10-28 改）：这一层每变一次颜色就要重画
    ///    它**全部**字母 —— 软件渲染下 FormattedText 的描边是跟着颜色栅格化的，颜色一变
    ///    就得重来。一层画三区时，随便哪一区改色都会把整块画布（含另两区的大块柔光矩形）
    ///    一起弄脏，拖色盘实测 21.8ms/帧（46fps）；拆开后只有那一区的字母与柔光变脏。
    /// ⚠️ 每层的布局框 = 这一区键帽的包围盒（Canvas.SetLeft/Top 落到画布坐标），
    ///    绘制时减掉这个偏移 —— 字的位置与 SVG 的 text-anchor:middle 完全一致。
    /// ⚠️ <b>居中只能用「左对齐 + 自己减半个字宽」这一种算法</b>（= SVG 的 text-anchor:middle）。
    ///    别顺手再写 <c>TextAlignment = Center</c>：WPF 在 Center 对齐下**把绘制原点当布局盒中心**，
    ///    再减一次 Width/2 就整体左移半个字宽（F1 偏左 3.5px、Space 偏左 6.6px）。
    /// ⚠️ <b>FormattedText 必须按字形缓存</b>：标签/字号是常量，只有颜色会变。
    ///    每帧重建 130 多个 FormattedText 要 5ms 左右，而拖动色盘时是**逐帧**重绘的
    ///    （<see cref="KeyboardView.PaintZones"/>），这笔钱付不起。
    ///    换颜色走 <see cref="FormattedText.SetForegroundBrush"/>，字形 run 保留；
    ///    只有 DPI（PixelsPerDip）变了才重建 —— 那时字号栅格化本来就得重来。
    /// </summary>
    private sealed class TextLayer(IReadOnlyList<KeyBox> keys, double boxX, double boxY) : FrameworkElement
    {
        private static readonly Typeface Face = new(
            new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        /// <summary>当前字母色与透明度（由 <see cref="KeyboardView.PaintZones"/> 算好后写进来）。</summary>
        private Color _fill = Colors.White;
        private byte _alpha = 255;

        private readonly List<(KeyBox Key, FormattedText Ft)> _glyphs = [];

        /// <summary>建缓存时用的 PixelsPerDip；-1 = 还没建。</summary>
        private double _glyphPpd = -1;

        public void SetColor(Color color, byte alpha)
        {
            if (_fill == color && _alpha == alpha) return;
            _fill = color;
            _alpha = alpha;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (_glyphPpd < 0 || Math.Abs(ppd - _glyphPpd) > 0.0001) BuildGlyphs(ppd);

            var brush = Brush(Color.FromArgb(_alpha, _fill.R, _fill.G, _fill.B));
            foreach (var (key, ft) in _glyphs)
            {
                // ⚠️ 保持默认的 TextAlignment.Left：见类注释，Center 会让下面的减半宽变成双重补偿
                ft.SetForegroundBrush(brush);
                dc.DrawText(ft, new Point(key.Cx - ft.Width / 2 - boxX, key.Cy - ft.Height / 2 - boxY));
            }
        }

        private void BuildGlyphs(double ppd)
        {
            _glyphPpd = ppd;
            _glyphs.Clear();

            foreach (var key in keys)
            {
                if (key.Label is null) continue;
                // 颜色先给白色占位：每次 OnRender 会按当前灯色 SetForegroundBrush
                var ft = new FormattedText(key.Label, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, key.Size, Brushes.White, ppd);
                _glyphs.Add((key, ft));
            }
        }
    }

    // ---------------------------------------------------------------- 自适应

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? ViewWidth : availableSize.Width;
        if (w <= 0) w = ViewWidth;
        var h = w * Aspect;

        // 高度也被限死时按高度反算宽度（= SVG 的 meet：取能装下的那一档）
        if (!double.IsInfinity(availableSize.Height) && h > availableSize.Height && availableSize.Height > 0)
        {
            h = availableSize.Height;
            w = h / Aspect;
        }

        var size = new Size(w, h);
        _view.Measure(size);
        return size;
    }
}
