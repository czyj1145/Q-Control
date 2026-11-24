namespace QControl.App.Render;

/// <summary>浮点点（原 System.Drawing.PointF；框架移除后自带给）。</summary>
internal readonly record struct PointF(float X, float Y);

/// <summary>浮点矩形（原 System.Drawing.RectangleF）。</summary>
internal readonly record struct RectangleF(float X, float Y, float Width, float Height);

/// <summary>
/// 预乘 alpha 的 BGRA 软件画布（32bpp，行主序，与 <c>CreateBitmap</c> /
/// <c>UpdateLayeredWindow</c> 要的字节序一致）。
///
/// 为什么不用 GDI+/WinForms：那两样会把整个 <c>Microsoft.WindowsDesktop.App</c>
/// 框架拖进进程（工作集 +7 MB 起步，还多一个 STA 消息循环线程）。
/// 托盘图标和 OSD 卡片都是**一次画好、整张贴上去**的位图，自己光栅化完全够用：
/// 这里只做圆角矩形 / 渐变 / 多边形 / 线 / 椭圆 / 字形 alpha 蒙版，
/// 全部走 SDF（有符号距离场）+ 2×2 超采样抗锯齿。
///
/// 颜色一律用**直通色**（straight RGBA）进、内部按 SourceOver 混到预乘缓冲里；
/// <see cref="Buffer"/> 出去时就是现成的预乘 BGRA，调用方直接建位图即可。
/// </summary>
internal sealed class SoftCanvas
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>预乘 BGRA，4 字节/像素。整帧画完后交给 CreateBitmap。</summary>
    private readonly byte[] _px;

    public SoftCanvas(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _px = new byte[Width * Height * 4];
    }

    public byte[] Buffer => _px;

    /// <summary>
    /// 把另一张**预乘 BGRA** 位图按 SourceOver 贴进来（左上角对齐 <paramref name="ox"/>/<paramref name="oy"/>）。
    /// 只给开发用截图拼版使（<c>TrayMenu.Shot</c> / <c>OsdCard.Shot</c>）：把画好的卡片
    /// 摞到一块模拟桌面上，才看得出圆角、描边和投影对不对。正常渲染路径一行都不调。
    /// </summary>
    public void BlitPremul(byte[] src, int sw, int sh, int ox, int oy)
    {
        for (var y = 0; y < sh; y++)
        {
            var dy = oy + y;
            if (dy < 0 || dy >= Height) continue;
            for (var x = 0; x < sw; x++)
            {
                var dx = ox + x;
                if (dx < 0 || dx >= Width) continue;

                var si = (y * sw + x) * 4;
                var sa = src[si + 3];
                if (sa == 0) continue;

                var di = (dy * Width + dx) * 4;
                var inv = 255 - sa;
                _px[di + 0] = (byte)Math.Min(255, src[si + 0] + _px[di + 0] * inv / 255);
                _px[di + 1] = (byte)Math.Min(255, src[si + 1] + _px[di + 1] * inv / 255);
                _px[di + 2] = (byte)Math.Min(255, src[si + 2] + _px[di + 2] * inv / 255);
                _px[di + 3] = (byte)Math.Min(255, sa + _px[di + 3] * inv / 255);
            }
        }
    }

    // ================================================================ 基本形

    /// <summary>实心圆角矩形（radius ≤ 0 当直角）。内部按 SDF 逐像素算覆盖度。</summary>
    public void FillRounded(int x, int y, int w, int h, float radius,
                            byte r, byte g, byte b, byte a)
    {
        var cx = x + w / 2f;
        var cy = y + h / 2f;
        var hx = w / 2f;
        var hy = h / 2f;
        var rad = MathF.Max(0f, MathF.Min(radius, MathF.Min(hx, hy)));

        for (var py = y; py < y + h; py++)
        {
            if (py < 0 || py >= Height) continue;
            for (var pxi = x; pxi < x + w; pxi++)
            {
                if (pxi < 0 || pxi >= Width) continue;
                var c = RoundedRectCoverage(pxi + 0.5f, py + 0.5f, cx, cy, hx, hy, rad);
                if (c <= 0f) continue;
                BlendPixel(pxi, py, r, g, b, a, c);
            }
        }
    }

    /// <summary>竖向渐变圆角矩形（top→bottom，直通色）。</summary>
    public void FillRoundedGradient(int x, int y, int w, int h, float radius,
                                    (byte R, byte G, byte B, byte A) top,
                                    (byte R, byte G, byte B, byte A) bottom)
    {
        var cx = x + w / 2f;
        var cy = y + h / 2f;
        var hx = w / 2f;
        var hy = h / 2f;
        var rad = MathF.Max(0f, MathF.Min(radius, MathF.Min(hx, hy)));
        var span = h <= 1 ? 1f : h - 1f;

        for (var py = y; py < y + h; py++)
        {
            if (py < 0 || py >= Height) continue;
            var t = Math.Clamp((py - y) / span, 0f, 1f);
            var r = (byte)MathF.Round(top.R + (bottom.R - top.R) * t);
            var g = (byte)MathF.Round(top.G + (bottom.G - top.G) * t);
            var bl = (byte)MathF.Round(top.B + (bottom.B - top.B) * t);
            var al = (byte)MathF.Round(top.A + (bottom.A - top.A) * t);
            for (var pxi = x; pxi < x + w; pxi++)
            {
                if (pxi < 0 || pxi >= Width) continue;
                var c = RoundedRectCoverage(pxi + 0.5f, py + 0.5f, cx, cy, hx, hy, rad);
                if (c <= 0f) continue;
                BlendPixel(pxi, py, r, g, bl, al, c);
            }
        }
    }

    /// <summary>1px 圆角矩形描边（极淡高光，边缘不发虚）。</summary>
    public void StrokeRounded(int x, int y, int w, int h, float radius,
                              byte r, byte g, byte b, byte a)
    {
        var cx = x + w / 2f;
        var cy = y + h / 2f;
        var hx = w / 2f;
        var hy = h / 2f;
        var rad = MathF.Max(0f, MathF.Min(radius, MathF.Min(hx, hy)));
        var half = 0.5f;

        for (var py = y - 1; py <= y + h; py++)
        {
            if (py < 0 || py >= Height) continue;
            for (var pxi = x - 1; pxi <= x + w; pxi++)
            {
                if (pxi < 0 || pxi >= Width) continue;
                var c = RoundedRectCoverage(pxi + 0.5f, py + 0.5f, cx, cy, hx, hy, rad);
                // 只在"轮廓带"上画：内缩 half 与整形的覆盖差 ≈ 边缘 1px
                var cIn = RoundedRectCoverage(pxi + 0.5f, py + 0.5f, cx, cy,
                                              hx - half, hy - half, MathF.Max(0f, rad - half));
                var edge = Math.Clamp(c - cIn, 0f, 1f);
                if (edge <= 0f) continue;
                BlendPixel(pxi, py, r, g, b, a, edge);
            }
        }
    }

    /// <summary>填充多边形（扫描线 4 次超采样，覆盖度按**像素**累积）。</summary>
    public void FillPolygon(ReadOnlySpan<PointF> pts, byte r, byte g, byte b, byte a)
    {
        if (pts.Length < 3) return;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        foreach (var p in pts) { minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y); }

        var y0 = Math.Max(0, (int)MathF.Floor(minY));
        var y1 = Math.Min(Height - 1, (int)MathF.Ceiling(maxY));
        var xs = new List<float>(8);
        var rowCov = new float[Width];   // ⚠️ 每行独立的像素覆盖度，别拿行总量当像素量

        for (var py = y0; py <= y1; py++)
        {
            Array.Clear(rowCov);
            // 4 个次像素采样点，平均覆盖度
            for (var s = 0; s < 4; s++)
            {
                var sy = py + 0.125f + s * 0.25f;
                xs.Clear();
                for (var i = 0; i < pts.Length; i++)
                {
                    var p1 = pts[i];
                    var p2 = pts[(i + 1) % pts.Length];
                    if (p1.Y == p2.Y) continue;
                    if ((sy >= p1.Y && sy < p2.Y) || (sy >= p2.Y && sy < p1.Y))
                        xs.Add(p1.X + (sy - p1.Y) / (p2.Y - p1.Y) * (p2.X - p1.X));
                }
                if (xs.Count < 2) continue;
                xs.Sort();
                for (var i = 0; i + 1 < xs.Count; i += 2)
                {
                    var l = MathF.Max(0f, xs[i]);
                    var rr = MathF.Min(Width, xs[i + 1]);
                    if (rr <= l) continue;
                    var i0 = (int)MathF.Floor(l);
                    var i1 = (int)MathF.Ceiling(rr);
                    for (var px = i0; px < i1; px++)
                    {
                        if (px < 0 || px >= Width) continue;
                        var pl = MathF.Max(l, px);
                        var pr = MathF.Min(rr, px + 1f);
                        if (pr <= pl) continue;
                        rowCov[px] += (pr - pl) / 4f;   // 4 次采样的平均
                    }
                }
            }
            for (var px = 0; px < Width; px++)
            {
                if (rowCov[px] <= 0f) continue;
                BlendPixel(px, py, r, g, b, a, MathF.Min(1f, rowCov[px]));
            }
        }
    }

    /// <summary>圆头线段（SDF 胶囊）。</summary>
    public void StrokeLine(PointF a, PointF b, float width, byte r, byte g, byte bl, byte alpha)
    {
        var minX = (int)MathF.Floor(MathF.Min(a.X, b.X) - width);
        var maxX = (int)MathF.Ceiling(MathF.Max(a.X, b.X) + width);
        var minY = (int)MathF.Floor(MathF.Min(a.Y, b.Y) - width);
        var maxY = (int)MathF.Ceiling(MathF.Max(a.Y, b.Y) + width);
        var half = width / 2f;

        for (var py = Math.Max(0, minY); py <= Math.Min(Height - 1, maxY); py++)
        {
            for (var px = Math.Max(0, minX); px <= Math.Min(Width - 1, maxX); px++)
            {
                var d = SegDistance(px + 0.5f, py + 0.5f, a, b) - half;
                var c = Math.Clamp(0.5f - d, 0f, 1f);
                if (c <= 0f) continue;
                BlendPixel(px, py, r, g, bl, alpha, c);
            }
        }
    }

    /// <summary>空心圆（SDF）。</summary>
    public void StrokeEllipse(PointF center, float radius, float width,
                              byte r, byte g, byte b, byte a)
    {
        var minX = (int)MathF.Floor(center.X - radius - width);
        var maxX = (int)MathF.Ceiling(center.X + radius + width);
        var minY = (int)MathF.Floor(center.Y - radius - width);
        var maxY = (int)MathF.Ceiling(center.Y + radius + width);

        for (var py = Math.Max(0, minY); py <= Math.Min(Height - 1, maxY); py++)
        {
            for (var px = Math.Max(0, minX); px <= Math.Min(Width - 1, maxX); px++)
            {
                var d = MathF.Abs(MathF.Sqrt(Sq(px + 0.5f - center.X) + Sq(py + 0.5f - center.Y)) - radius) - width / 2f;
                var c = Math.Clamp(0.5f - d, 0f, 1f);
                if (c <= 0f) continue;
                BlendPixel(px, py, r, g, b, a, c);
            }
        }
    }

    /// <summary>实心圆（SDF）。</summary>
    public void FillEllipse(PointF center, float radius, byte r, byte g, byte b, byte a)
    {
        var minX = (int)MathF.Floor(center.X - radius);
        var maxX = (int)MathF.Ceiling(center.X + radius);
        var minY = (int)MathF.Floor(center.Y - radius);
        var maxY = (int)MathF.Ceiling(center.Y + radius);

        for (var py = Math.Max(0, minY); py <= Math.Min(Height - 1, maxY); py++)
        {
            for (var px = Math.Max(0, minX); px <= Math.Min(Width - 1, maxX); px++)
            {
                var d = MathF.Sqrt(Sq(px + 0.5f - center.X) + Sq(py + 0.5f - center.Y)) - radius;
                var c = Math.Clamp(0.5f - d, 0f, 1f);
                if (c <= 0f) continue;
                BlendPixel(px, py, r, g, b, a, c);
            }
        }
    }

    /// <summary>竖向渐变实心圆（top→bottom，直通色）。托盘图标的圆环用。</summary>
    public void FillEllipseGradient(PointF center, float radius,
                                    (byte R, byte G, byte B, byte A) top,
                                    (byte R, byte G, byte B, byte A) bottom)
    {
        var top0 = Math.Max(0, (int)MathF.Floor(center.Y - radius));
        var bottom0 = Math.Min(Height - 1, (int)MathF.Ceiling(center.Y + radius));
        var left0 = Math.Max(0, (int)MathF.Floor(center.X - radius));
        var right0 = Math.Min(Width - 1, (int)MathF.Ceiling(center.X + radius));
        var span = bottom0 - top0 <= 0 ? 1f : bottom0 - top0;

        for (var py = top0; py <= bottom0; py++)
        {
            var t = Math.Clamp((py - top0) / span, 0f, 1f);
            var r = (byte)MathF.Round(top.R + (bottom.R - top.R) * t);
            var g = (byte)MathF.Round(top.G + (bottom.G - top.G) * t);
            var bl = (byte)MathF.Round(top.B + (bottom.B - top.B) * t);
            var al = (byte)MathF.Round(top.A + (bottom.A - top.A) * t);
            for (var px = left0; px <= right0; px++)
            {
                var d = MathF.Sqrt(Sq(px + 0.5f - center.X) + Sq(py + 0.5f - center.Y)) - radius;
                var c = Math.Clamp(0.5f - d, 0f, 1f);
                if (c <= 0f) continue;
                BlendPixel(px, py, r, g, bl, al, c);
            }
        }
    }

    // ================================================================ 字形

    /// <summary>
    /// 把一份 0..255 的 alpha 蒙版按直通色混进画布（字形渲染用）。
    /// 蒙版行主序、尺寸 <paramref name="mw"/>×<paramref name="mh"/>，
    /// 左上角贴在 (<paramref name="x0"/>, <paramref name="y0"/>)（可为负）。
    /// </summary>
    public void BlendMask(int x0, int y0, int mw, int mh, ReadOnlySpan<byte> mask,
                          byte r, byte g, byte b, byte a)
    {
        for (var my = 0; my < mh; my++)
        {
            var py = y0 + my;
            if (py < 0 || py >= Height) continue;
            for (var mx = 0; mx < mw; mx++)
            {
                var c = mask[my * mw + mx] / 255f;
                if (c <= 0f) continue;
                var px = x0 + mx;
                if (px < 0 || px >= Width) continue;
                BlendPixel(px, py, r, g, b, a, c);
            }
        }
    }

    // ================================================================ 内部

    private static float Sq(float v) => v * v;

    /// <summary>圆角矩形 SDF 覆盖度（0..1）。</summary>
    private static float RoundedRectCoverage(float px, float py, float cx, float cy,
                                             float hx, float hy, float rad)
    {
        var qx = MathF.Abs(px - cx) - hx + rad;
        var qy = MathF.Abs(py - cy) - hy + rad;
        var outside = MathF.Sqrt(Sq(MathF.Max(qx, 0f)) + Sq(MathF.Max(qy, 0f)));
        var d = (MathF.Max(qx, qy) < 0f ? MathF.Min(MathF.Max(qx, qy), 0f) : outside) - rad;
        return Math.Clamp(0.5f - d, 0f, 1f);
    }

    /// <summary>点到线段的距离。</summary>
    private static float SegDistance(float px, float py, PointF a, PointF b)
    {
        var vx = b.X - a.X;
        var vy = b.Y - a.Y;
        var wx = px - a.X;
        var wy = py - a.Y;
        var len = vx * vx + vy * vy;
        var t = len <= 0f ? 0f : Math.Clamp((wx * vx + wy * vy) / len, 0f, 1f);
        return MathF.Sqrt(Sq(wx - t * vx) + Sq(wy - t * vy));
    }

    /// <summary>SourceOver 混一个直通色像素（覆盖度 <paramref name="c"/> 0..1）。</summary>
    private void BlendPixel(int x, int y, byte r, byte g, byte b, byte a, float c)
    {
        var i = (y * Width + x) * 4;
        var sa = (a / 255f) * Math.Clamp(c, 0f, 1f);
        var sr = r * sa;
        var sg = g * sa;
        var sb = b * sa;
        // 目标已是预乘：out = src + dst*(1-sa)
        _px[i] = (byte)Math.Clamp(sb + _px[i] * (1f - sa), 0f, 255f);          // B
        _px[i + 1] = (byte)Math.Clamp(sg + _px[i + 1] * (1f - sa), 0f, 255f);  // G
        _px[i + 2] = (byte)Math.Clamp(sr + _px[i + 2] * (1f - sa), 0f, 255f);  // R
        _px[i + 3] = (byte)Math.Clamp(sa * 255f + _px[i + 3] * (1f - sa), 0f, 255f);  // A
    }
}
