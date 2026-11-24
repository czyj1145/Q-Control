using QControl.App.Render;
using QControl.App.Ui;

namespace QControl.App.Osd;

/// <summary>OSD 显示内容。<c>Kind</c> 决定徽章里画什么图标（backlight / perf / fan / 其它→圆点）。</summary>
internal sealed record OsdSpec(string Kind, string Title, string Subtitle);

/// <summary>画好的一帧卡片：预乘 BGRA，调用方直接建位图贴 <c>UpdateLayeredWindow</c>。</summary>
internal sealed record CardBitmap(int W, int H, byte[] PremulBgra);

/// <summary>
/// 把一条提示画成 32bpp 预乘位图。版面数值集中在这张常量表里
/// （逻辑像素，乘 <c>scale</c> 后变物理像素）。
///
/// 底板**不透明**（2026-10-13 用户定的）：卡片很小，半透明会让文字和背后的
/// 窗口内容糊在一起。要改回半透明只动 <see cref="BgAlpha"/>。
///
/// 配色**不再写死**：全部来自 <see cref="OsdPalette"/>（暗/亮两套，2026-10-24 加），
/// 否则浅色模式下这块黑卡片会跟整个界面格格不入。
///
/// ⚠️ 为什么从 GDI+ 换成自绘：GDI+ 一进进程就拖着整个 WindowsDesktop 框架
///    （+7 MB 工作集起步）。这里形状走 <see cref="SoftCanvas"/> 的 SDF 光栅化，
///    文字走 <see cref="GdiText"/> 的 GDI 灰度字形，效果对齐早先实现。
/// ⚠️ 顺序仍是"底板 → 装饰 → 文字"，带 alpha 的装饰都画在不透明底上。
/// </summary>
internal static class OsdCard
{
    // ---------------- 版面（逻辑像素）----------------
    public const int Margin = 20;
    public const int CardH = 66;
    private const int CardMinW = 224;
    private const int CardPadX = 18;
    private const int BarW = 4;
    private const int BarInset = 14;
    private const int Gap = 13;
    private const int Badge = 34;
    private const int BadgeR = 10;
    private const int CardR = 14;
    private const float TitleSz = 21f;
    private const float SubSz = 13.5f;

    /// <summary>底板不透明度。配色在 <see cref="OsdPalette"/>，这里只管"要不要透"。</summary>
    private const byte BgAlpha = 255;

    public static CardBitmap Render(OsdSpec spec, float scale, OsdPalette pal)
    {
        var s = scale <= 0 ? 1f : scale;
        var title = spec.Title ?? "";
        var sub = spec.Subtitle ?? "";

        // 量文字（GDI 度量，与旧 GDI+ MeasureString 差别在 1~2px，肉眼无感）
        var (tw, th) = TextMetrics(title, TitleSz * s);
        var (sw, sh) = TextMetrics(sub, SubSz * s);

        var badge = Badge * s;
        var px = CardPadX * s;
        var stripeX = px;
        var badgeX = stripeX + BarW * s + Gap * s;
        var textX = badgeX + badge + Gap * s;

        var w = (int)MathF.Max(textX + MathF.Max(tw, sw) + px, CardMinW * s);
        var h = (int)(CardH * s);
        w = Math.Max(1, w);
        h = Math.Max(1, h);

        var c = new SoftCanvas(w, h);

        // ---- 底板：圆角 + 竖向渐变（BgAlpha 作用于两端颜色）----
        var bar = pal.Bar;
        var top = (pal.BgTop.R, pal.BgTop.G, pal.BgTop.B, BgAlpha);
        var bottom = (pal.BgBottom.R, pal.BgBottom.G, pal.BgBottom.B, BgAlpha);
        c.FillRoundedGradient(0, 0, w, h, CardR * s, top, bottom);

        // ---- 左侧强调竖条（比卡片矮，读起来像"标记"而不是"边框"）----
        var barInset = BarInset * s;
        c.FillRounded((int)MathF.Round(stripeX), (int)MathF.Round(barInset),
                      Math.Max(1, (int)MathF.Round(BarW * s)),
                      Math.Max(1, (int)MathF.Round(h - 2 * barInset)),
                      BarW * s / 2f, bar.R, bar.G, bar.B, 255);

        // ---- 图标徽章 ----
        var badgeBox = new RectangleF(badgeX, (h - badge) / 2f, badge, badge);
        c.FillRounded((int)MathF.Round(badgeBox.X), (int)MathF.Round(badgeBox.Y),
                      Math.Max(1, (int)MathF.Round(badgeBox.Width)),
                      Math.Max(1, (int)MathF.Round(badgeBox.Height)),
                      BadgeR * s, bar.R, bar.G, bar.B, pal.BadgeAlpha);

        DrawIcon(c, spec.Kind, badgeBox, s, bar, pal.BgTop);

        // ---- 文字（有副标题时整体上移一点，让两行视觉居中）----
        var fg = (pal.Fg.R, pal.Fg.G, pal.Fg.B, (byte)255);
        var fg2 = (pal.Fg2.R, pal.Fg2.G, pal.Fg2.B, (byte)255);
        var ty = sub.Length > 0 ? h * 0.5f - 12.5f * s : h * 0.5f;
        DrawText(c, title, TitleSz * s, textX, ty, th, fg);
        if (sub.Length > 0)
            DrawText(c, sub, SubSz * s, textX, h * 0.5f + 13.5f * s, sh, fg2);

        // ---- 1px 内描边（暗色是极淡的高光，浅色是界面那条分割线）----
        c.StrokeRounded(0, 0, w, h, MathF.Max(1f, CardR * s - 1),
                        pal.Edge.R, pal.Edge.G, pal.Edge.B, pal.EdgeAlpha);

        return new CardBitmap(w, h, c.Buffer);
    }

    // ---------------------------------------------------------------- 开发用截图

    /// <summary>
    /// 把三种典型内容 × 暗/亮两套配色各渲一张 PNG（<c>&lt;stem&gt;-dark.png</c> / <c>-light.png</c>），
    /// 底衬一段模拟桌面 —— 卡片边缘、描边、字重全靠看图判断。
    ///
    /// 只在 <c>--osd-shot</c> 时调用（纯渲染，不起后端、不碰 EC、不提权）。
    /// 之所以要这个口子：OSD 是分层窗口 + 位图，走不了 <c>--ui-shot</c> 的窗口截图路径。
    /// </summary>
    public static int Shot(string path)
    {
        var samples = new (string Kind, string Title, string Sub)[]
        {
            ("backlight", "键盘背光", "三档"),
            ("perf", "性能模式", "平衡"),
            ("fan", "风扇", "最大（飓风）"),
        };
        var variants = new (string Name, OsdPalette Pal, Rgb DeskTop, Rgb DeskBottom)[]
        {
            ("dark", OsdPalette.Dark, Rgb.Hex("#2a2f3a"), Rgb.Hex("#1a1e26")),
            ("light", OsdPalette.Light, Rgb.Hex("#dfe4ec"), Rgb.Hex("#cfd6e2")),
        };

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(full);
        Directory.CreateDirectory(dir);

        const float s = 1.25f;         // 常见缩放，字才看得清
        const int pad = 18;
        const int gapY = 16;

        foreach (var v in variants)
        {
            var cards = new List<CardBitmap>(samples.Length);
            foreach (var x in samples)
                cards.Add(Render(new OsdSpec(x.Kind, x.Title, x.Sub), s, v.Pal));

            var w = cards.Max(c => c.W) + pad * 2;
            var h = cards.Sum(c => c.H) + gapY * (cards.Count - 1) + pad * 2;
            var c = new SoftCanvas(w, h);
            // 模拟桌面底色：白卡片必须有背景才看得出描边，深色卡片才看得出圆角
            c.FillRoundedGradient(0, 0, w, h, 0f,
                                  (v.DeskTop.R, v.DeskTop.G, v.DeskTop.B, (byte)255),
                                  (v.DeskBottom.R, v.DeskBottom.G, v.DeskBottom.B, (byte)255));

            var y = pad;
            foreach (var card in cards)
            {
                c.BlitPremul(card.PremulBgra, card.W, card.H, pad, y);
                y += card.H + gapY;
            }

            PngSave.FromCanvas(c, Path.Combine(dir, stem + "-" + v.Name + ".png"));
        }

        Console.WriteLine($"[shot] 已写入 {Path.Combine(dir, stem)}-dark.png / -light.png");
        return 0;
    }

    // ---------------------------------------------------------------- 文字

    private static (float Width, float LineH) TextMetrics(string text, float px)
    {
        if (string.IsNullOrEmpty(text)) return (0f, 0f);
        var (asc, desc) = GdiText.VMetrics(px);
        return (GdiText.Measure(text, px), asc + desc);
    }

    /// <summary>把一行文字按"行盒视觉居中于 <paramref name="centerY"/>"画上去。</summary>
    private static void DrawText(SoftCanvas c, string text, float px, float x, float centerY,
                                 float lineH, (byte R, byte G, byte B, byte A) color)
    {
        if (string.IsNullOrEmpty(text) || lineH <= 0f) return;
        var (asc, _) = GdiText.VMetrics(px);
        var baseline = centerY - lineH / 2f + asc;
        GdiText.Draw(c, text, px, x, baseline, color.R, color.G, color.B, color.A);
    }

    // ---------------------------------------------------------------- 图标

    /// <summary>极简图标：键盘（背光）/ 闪电（性能）/ 风车（风扇）/ 圆点（通用）。</summary>
    private static void DrawIcon(SoftCanvas c, string kind, RectangleF box, float s, Rgb accent, Rgb badgeBg)
    {
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var size = box.Width * 0.56f;
        var h = size / 2f;
        var stroke = MathF.Max(1.2f, 2f * s);
        var r = accent.R;
        var g = accent.G;
        var b = accent.B;

        switch (kind)
        {
            case "backlight":
            {
                // 键盘：圆角机身 + 两排键帽（下排是长空格）
                var body = new RectangleF(cx - h, cy - h * 0.72f, h * 2, h * 1.44f);
                StrokeRounded(c, body, 3f * s, stroke, accent, badgeBg);
                for (var i = 0; i < 4; i++)
                {
                    var kx = cx - h * 0.66f + i * h * 0.44f;
                    c.FillRounded((int)(kx - 1.6f * s), (int)(cy - h * 0.42f),
                                  Math.Max(1, (int)MathF.Ceiling(3.2f * s)),
                                  Math.Max(1, (int)MathF.Ceiling(h * 0.22f)),
                                  1f * s, r, g, b, 255);
                }
                c.FillRounded((int)(cx - h * 0.52f), (int)(cy + h * 0.02f),
                              Math.Max(1, (int)MathF.Ceiling(h * 1.04f)),
                              Math.Max(1, (int)MathF.Ceiling(h * 0.22f)),
                              1f * s, r, g, b, 255);
                break;
            }
            case "perf":
            {
                var pts = new[]
                {
                    new PointF(cx - h * 0.18f, cy - h),
                    new PointF(cx - h * 0.72f, cy + h * 0.18f),
                    new PointF(cx - h * 0.02f, cy + h * 0.18f),
                    new PointF(cx - h * 0.38f, cy + h),
                    new PointF(cx + h * 0.82f, cy - h * 0.26f),
                    new PointF(cx + h * 0.12f, cy - h * 0.26f),
                    new PointF(cx + h * 0.58f, cy - h),
                };
                c.FillPolygon(pts, r, g, b, 255);
                break;
            }
            case "fan":
            {
                c.StrokeEllipse(new PointF(cx, cy), h, stroke, r, g, b, 255);
                foreach (var deg in new[] { 90, 210, 330 })
                {
                    var t = deg * Math.PI / 180.0;
                    var rr = h * 0.74f;
                    c.StrokeLine(new PointF(cx, cy),
                                 new PointF((float)(cx + rr * Math.Cos(t)),
                                            (float)(cy + rr * Math.Sin(t))),
                                 stroke, r, g, b, 255);
                }
                c.FillEllipse(new PointF(cx, cy), MathF.Max(1.2f, 2.4f * s), r, g, b, 255);
                break;
            }
            default:
                c.FillEllipse(new PointF(cx, cy), h * 0.6f, r, g, b, 255);
                break;
        }
    }

    /// <summary>圆角矩形描边：外框实心 + 内框用徽章底"擦"回，得到 stroke 效果。</summary>
    private static void StrokeRounded(SoftCanvas c, RectangleF box, float radius, float width,
                                      Rgb stroke, Rgb badgeBg)
    {
        c.FillRounded((int)MathF.Round(box.X - width / 2), (int)MathF.Round(box.Y - width / 2),
                      Math.Max(1, (int)MathF.Round(box.Width + width)),
                      Math.Max(1, (int)MathF.Round(box.Height + width)),
                      radius + width / 2, stroke.R, stroke.G, stroke.B, 255);
        // 徽章底 ≈ BgTop（不透明），把中间擦回来
        c.FillRounded((int)MathF.Round(box.X + width / 2), (int)MathF.Round(box.Y + width / 2),
                      Math.Max(1, (int)MathF.Round(box.Width - width)),
                      Math.Max(1, (int)MathF.Round(box.Height - width)),
                      MathF.Max(0.5f, radius - width / 2),
                      badgeBg.R, badgeBg.G, badgeBg.B, 255);
    }
}
