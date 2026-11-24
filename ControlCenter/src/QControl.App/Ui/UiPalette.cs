using System.Windows;
using System.Windows.Media;

namespace QControl.App.Ui;

/// <summary>
/// WPF 侧的颜色令牌 —— **全站唯一的一套色号**。
///
/// 为什么不在 XAML 里散写颜色：双主题（暗/亮）需要一处改、全局跟着走。这里只做一件事：
/// 把色号翻成 Brush，键名与 CSS 变量同名（AccBrush ↔ --acc）。
///
/// ⚠️ 浅色主题那三档（Dim / Line / Panel2）是全局可读性的根，别自己调浅：
///    Dim 7.4:1、Line 1.34→加深、Panel2 要与卡片渐变末端拉开。
/// ⚠️ 卡片底色不是纯色而是**渐变**（--panel → #11151d / #f4f7fc），所以 CardBrush
///    必须是 LinearGradientBrush；WPF 的 Border 只在 BorderBrush 上用画刷，Background 同样吃渐变。
/// </summary>
internal sealed class UiPalette
{
    public required Color Bg { get; init; }
    public required Color BgBottom { get; init; }
    public required Color Panel { get; init; }
    public required Color Panel2 { get; init; }
    public required Color CardBottom { get; init; }
    public required Color Line { get; init; }
    public required Color Txt { get; init; }
    public required Color Dim { get; init; }
    public required Color Acc { get; init; }
    public required Color Acc2 { get; init; }
    public required Color Ok { get; init; }

    /// <summary>
    /// 「第二个绿」—— 历史曲线里 GPU 风扇那条线的颜色（CPU 风扇走 <see cref="Ok"/>）。
    ///
    /// 为什么不是纯绿：两个风扇的转速常年只差几十 RPM，两条线几乎重合；同色系里再挑一个
    /// 偏向青的（暗 #2dd4bf / 亮 #0f8f86），配合**虚线**（HistoryChart 的 Dash）才分得开。
    /// ⚠️ 与 HistoryChart.SeriesList 是同一个色号，改一处要改两处。
    /// </summary>
    public required Color Ok2 { get; init; }

    public required Color Warn { get; init; }
    public required Color Hot { get; init; }
    public required Color Rail { get; init; }
    public required Color Topbar { get; init; }
    public required Color RingTrack { get; init; }

    /// <summary>
    /// 环形仪表的内盘色（RingGauge.CoreBrush）—— 必须和 <see cref="RingTrack"/> 拉开明确色差，
    /// 否则甜甜圈会糊成一个实心圆饼。
    /// 深色取 Panel（比轨道暗 ⇒ 凹盘），浅色取 Panel2（比轨道亮 ⇒ 浅碟）。
    /// ⚠️ 两端都写 Panel 会翻车：浅色下 Panel 是纯白、卡片也是白，环内看不出边界。
    /// </summary>
    public required Color RingCore { get; init; }

    /// <summary>卡片悬停时的描边（CSS: .card:hover border-color #2f3949 / #b7c4da）。</summary>
    public required Color LineHover { get; init; }

    /// <summary>
    /// 滑块（input[type=range]）空轨的颜色。
    ///
    /// ⚠️ **不能复用 <see cref="RingTrack"/>**：
    ///    RingTrack 的暗色 #222a38 是给环形仪表内圈衬底用的，而滑块的底是 Panel2 #1b2130 ——
    ///    两者几乎同色，4px 的轨道在面板里完全看不出来，只剩一颗孤立的小圆点，
    ///    用户以为"滑块没了"。暗色取 #2f3949（比 Panel2 亮两档），浅色 #cbd5e5。
    /// </summary>
    public required Color SliderTrack { get; init; }

    /// <summary>侧栏选中项的淡蓝底（CSS: rgba(77,214,255,.10) / rgba(15,134,204,.12)）。</summary>
    public required Color NavOnFill { get; init; }

    /// <summary>侧栏选中项的描边（CSS: rgba(77,214,255,.22) / rgba(15,134,204,.30)）。</summary>
    public required Color NavOnBorder { get; init; }

    /// <summary>
    /// 选中态按钮的渐变（CSS: <c>button.on</c> 的 <c>linear-gradient(135deg,--acc,--acc2)</c>）。
    /// ⚠️ 浅色主题**不是**把 --acc/--acc2 直接拿来用：CSS 那边另给了 #0c74b2→#5c36e2，
    ///    因为深蓝紫压白字才够对比（浅色下 --acc 是 #0f86cc，配白字只有 2.9:1）。
    /// </summary>
    public required Color AccGradStart { get; init; }
    public required Color AccGradEnd { get; init; }

    /// <summary>选中态按钮上的文字色（暗色近黑 / 浅色纯白，见 <see cref="AccGradStart"/> 的注释）。</summary>
    public required Color OnAccText { get; init; }

    /// <summary>背景流光（背景里那两团 radial-gradient，暗色 20%/16%、浅色 13%/16%）。</summary>
    public required Color GlowPrimary { get; init; }
    public required Color GlowSecondary { get; init; }

    // ---------------------------------------------------------------- 背光页键位图
    // KbDeck / KbKey 是三层里的外层两层；浅色下键区底要压深一档（#d8e1f0），
    // 白键帽落在浅灰底上才读得出来（浅色直接用 Panel 当键区底，整块键盘会"浮在白纸上"）。

    /// <summary>机身底盘（比卡片底更暗/更灰一档，三层里最里面那层）。</summary>
    public required Color KbDeck { get; init; }

    /// <summary>键帽填充（暗色 #1c2434 / 浅色纯白）。</summary>
    public required Color KbKey { get; init; }

    /// <summary>键盘底座（键帽所在的那块板）。</summary>
    public required Color KbPad { get; init; }

    /// <summary>
    /// 键帽上字母的颜色。null = **跟着本区灯色走**（暗色）；浅色下必须钉成深色 ——
    /// 键帽是白的，灯设成白色时就是"白底白字"，一个字母都看不见
    /// （CSS: html[data-theme="light"] .kb-keytxt text{fill:#2b3448}）。
    /// </summary>
    public Color? KbKeyText { get; init; }

    public static readonly UiPalette Dark = new()
    {
        Bg = C("#0b0d12"),
        BgBottom = C("#0a0c10"),
        // 下面这 5 个与托盘自绘菜单共用，色号钉在 ThemeHex（那里不含 WPF 类型）
        Panel = C(ThemeHex.DarkPanel),
        Panel2 = C("#1b2130"),
        CardBottom = C(ThemeHex.DarkCardBottom),
        Line = C(ThemeHex.DarkLine),
        Txt = C(ThemeHex.DarkTxt),
        Dim = C("#8b97ad"),
        Acc = C(ThemeHex.DarkAcc),
        Acc2 = C("#7c5cff"),
        Ok = C("#39d98a"),
        Ok2 = C("#2dd4bf"),
        Warn = C("#ffb547"),
        Hot = C("#ff5c6c"),
        Rail = C("#0d1017"),
        Topbar = C("#0f121a"),
        RingTrack = C("#222a38"),
        SliderTrack = C("#2f3949"),
        RingCore = C("#141821"),
        LineHover = C("#2f3949"),
        NavOnFill = A(77, 214, 255, 0x1a),
        NavOnBorder = A(77, 214, 255, 0x38),
        AccGradStart = C("#4dd6ff"),
        AccGradEnd = C("#7c5cff"),
        OnAccText = C("#0b0d12"),
        GlowPrimary = A(124, 92, 255, 0x33),
        GlowSecondary = A(77, 214, 255, 0x29),
        KbDeck = C("#0d1016"),
        KbKey = C("#1c2434"),
        KbPad = C("#141821"),
    };

    public static readonly UiPalette Light = new()
    {
        Bg = C("#eef1f7"),
        BgBottom = C("#e7ecf5"),
        Panel = C(ThemeHex.LightPanel),
        Panel2 = C("#eef2f9"),
        CardBottom = C(ThemeHex.LightCardBottom),
        Line = C(ThemeHex.LightLine),
        Txt = C(ThemeHex.LightTxt),
        // 浅色 Txt 取 #3b4553（比 #4b5566 再深一档）：
        // 7.4:1 虽然过了 AA 小字线，但 11px 常规字重的细笔画压在白/淡灰底上，
        // 视觉重量仍然只有"灰雾" —— 加深到 9.7:1（白底）/ 8.7:1（#eef2f9）才"立得住"。
        // ⚠️ Dim 也是浅色下所有次要文字的色号，ChartCanvas 的轴标签同取这一个值。
        Dim = C("#3b4553"),
        Acc = C(ThemeHex.LightAcc),
        Acc2 = C("#6a45f0"),
        Ok = C("#12a05c"),
        Ok2 = C("#0f8f86"),
        Warn = C("#b0700a"),
        Hot = C("#d63a4c"),
        Rail = C("#fbfcfe"),
        Topbar = C("#ffffff"),
        RingTrack = C("#cbd5e5"),
        SliderTrack = C("#cbd5e5"),
        RingCore = C("#eef2f9"),
        LineHover = C("#b7c4da"),
        NavOnFill = A(15, 134, 204, 0x1f),
        NavOnBorder = A(15, 134, 204, 0x4d),
        AccGradStart = C("#0c74b2"),
        AccGradEnd = C("#5c36e2"),
        OnAccText = C("#ffffff"),
        GlowPrimary = A(124, 92, 255, 0x21),
        GlowSecondary = A(77, 214, 255, 0x29),
        KbDeck = C("#ccd7e8"),
        KbKey = C("#ffffff"),
        KbPad = C("#d8e1f0"),
        KbKeyText = C("#2b3448"),
    };

    /// <summary>
    /// 把整份令牌灌进一个 ResourceDictionary。
    /// 界面里一律用 <c>{DynamicResource XxxBrush}</c> 引 —— 换主题就是重灌一遍字典，
    /// 绑了的控件自己跟着变，不需要谁去挨个通知（等价于换一套主题变量）。
    /// </summary>
    public void Apply(ResourceDictionary res)
    {
        res["BgBrush"] = Solid(Bg);
        res["BgGradBrush"] = new LinearGradientBrush(Bg, BgBottom, 90);
        res["PanelBrush"] = Solid(Panel);
        res["Panel2Brush"] = Solid(Panel2);
        res["CardBrush"] = new LinearGradientBrush(Panel, CardBottom, 90);
        res["LineBrush"] = Solid(Line);
        res["LineHoverBrush"] = Solid(LineHover);
        res["TxtBrush"] = Solid(Txt);
        res["DimBrush"] = Solid(Dim);
        res["AccBrush"] = Solid(Acc);
        res["Acc2Brush"] = Solid(Acc2);
        res["OkBrush"] = Solid(Ok);
        res["Ok2Brush"] = Solid(Ok2);
        res["WarnBrush"] = Solid(Warn);
        res["HotBrush"] = Solid(Hot);
        res["RailBrush"] = Solid(Rail);
        res["TopbarBrush"] = Solid(Topbar);
        res["RingTrackBrush"] = Solid(RingTrack);
        res["SliderTrackBrush"] = Solid(SliderTrack);
        res["RingCoreBrush"] = Solid(RingCore);
        res["NavOnFillBrush"] = Solid(NavOnFill);
        res["NavOnBorderBrush"] = Solid(NavOnBorder);
        res["NavHoverBrush"] = Solid(Blend(Txt, Bg, 0.045));
        res["LogoBrush"] = new LinearGradientBrush(Acc, Acc2, 45);
        res["GlowPrimaryBrush"] = Radial(GlowPrimary);
        res["GlowSecondaryBrush"] = Radial(GlowSecondary);
        res["AccSoftBrush"] = Solid(WithAlpha(Acc, 0x1a));
        // 紫调淡底：散热页「编辑中」那一档的底（CSS: rgba(124,92,255,.11)）
        res["Acc2SoftBrush"] = Solid(WithAlpha(Acc2, 0x1c));
        res["HotSoftBrush"] = Solid(WithAlpha(Hot, 0x24));
        res["AccGradBrush"] = new LinearGradientBrush(AccGradStart, AccGradEnd, 45);   // 135deg
        res["OnAccFgBrush"] = Solid(OnAccText);

        // 背光页键位图的三层底色（键帽描边/文字由 KeyboardView 按各区灯色现画）
        res["KbDeckBrush"] = Solid(KbDeck);
        res["KbKeyBrush"] = Solid(KbKey);
        res["KbPadBrush"] = Solid(KbPad);

        // 少数地方要的是 **Color** 不是 Brush（渐变 Stop、圆角条的描边）。
        // XAML 里 Color 属性的默认值类型是 Color，喂 Brush 会当场类型转换失败。
        res["AccColor"] = Acc;
        res["Acc2Color"] = Acc2;
        res["TxtColor"] = Txt;
        res["DimColor"] = Dim;
        res["LineColor"] = Line;
        res["PanelColor"] = Panel;
        res["BgColor"] = Bg;
        res["OnAccColor"] = OnAccText;
    }

    // ---------------------------------------------------------------- 小工具

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    private static Color A(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>按比例把 <paramref name="over"/> 叠在 <paramref name="under"/> 上（模拟 CSS 的半透明填充）。</summary>
    private static Color Blend(Color over, Color under, double ratio)
    {
        byte Mix(byte o, byte u) => (byte)Math.Round(u + (o - u) * ratio);
        return Color.FromRgb(Mix(over.R, under.R), Mix(over.G, under.G), Mix(over.B, under.B));
    }

    private static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static RadialGradientBrush Radial(Color c)
    {
        var b = new RadialGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B))
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        b.Freeze();
        return b;
    }
}
