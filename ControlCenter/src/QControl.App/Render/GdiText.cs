using System.Runtime.InteropServices;
using System.Text;

namespace QControl.App.Render;

/// <summary>
/// 纯 GDI 文字渲染（<c>GetGlyphOutline</c> 取灰度字形，自己贴进
/// <see cref="SoftCanvas"/>）—— 用来替代 GDI+ 的 <c>DrawString</c>。
///
/// 为什么不直接上 DirectWrite：DWrite 的 COM 接口 GUID 一旦写错就是静默失败，
/// 而这里只需要"单行、不换行、按像素定位"的最小能力（OSD 卡片标题 ≤32 字），
/// <c>GetGlyphOutline(GGO_GRAY8_BITMAP)</c> 给出的 64 级灰度字形完全够用，
/// 且全部是 gdi32 的普通 P/Invoke，任何 Windows 进程里都已经加载了它。
///
/// ⚠️ <c>GGO_GRAY8_BITMAP</c> 的灰度值域是 **0..64**（不是 0..255），
///    贴图前必须 ×4 放大 —— 这是最容易踩的一脚。
/// </summary>
internal static class GdiText
{
    private const uint GgoMetrics = 0x0000;
    private const uint GgoGray8 = 0x0006;
    private const uint GdiError = 0xFFFFFFFF;
    private const int FwNormal = 400;
    private const uint DefaultCharset = 1;
    private const uint OutTtPrecis = 4;
    private const uint ClipDefaultPrecis = 0;
    private const uint AntialiasedQuality = 4;
    private const uint DefaultPitch = 0;
    private const uint FfDontcare = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Fixed { public short fract; public short value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mat2
    {
        public Fixed e11, e12, e21, e22;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public uint gmBlackBoxX;
        public uint gmBlackBoxY;
        public PointI gmptGlyphOrigin;
        public short gmCellIncX;
        public short gmCellIncY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeI { public int cx; public int cy; }

    /// <summary>
    /// TEXTMETRICSW 原生大小（x64 = 60 字节）。
    /// ⚠️ <c>out</c> 一个"只声明前几个字段"的小结构体是**内存越界**：
    ///    marshal 出来的原生缓冲按小结构体分配，GDI 却按整块 60 字节写，
    ///    冲掉邻堆内存 —— 崩溃会延后到任意一次分配/DispatchMessage 才现形
    ///    （2026-10-21 实测：Internal CLR error at OsdWindow.DispatchMessage）。
    ///    所以这里用 AllocHGlobal + 手工偏移读，只取 tmAscent(+4)/tmDescent(+8)。
    /// </summary>
    private const int TextMetricsNativeSize = 80;   // 给足，宁大勿小

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFontW(int nHeight, int nWidth, int nEscapement,
        int nOrientation, int fnWeight, uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut,
        uint fdwCharSet, uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality,
        uint fdwPitchAndFamily, string lpszFace);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetTextFaceW(IntPtr hdc, int nCount, StringBuilder lpFaceName);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetGlyphOutlineW(IntPtr hdc, uint uChar, uint fuFormat,
        out GlyphMetrics lpgm, uint cbBuffer, byte[]? lpBuffer, ref Mat2 lpmat2);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int c, out SizeI lpSize);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetTextMetricsW(IntPtr hdc, IntPtr lptm);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int SetMapMode(IntPtr hdc, int mode);

    /// <summary>字体句柄 + 它所在的内存 DC。用完必须 Dispose。</summary>
    private sealed class FontPen : IDisposable
    {
        public readonly IntPtr Dc;
        public readonly IntPtr Font;
        public bool Ok;

        public FontPen(string family, float px)
        {
            var screen = GetDC(IntPtr.Zero);
            Dc = CreateCompatibleDC(screen);
            ReleaseDC(IntPtr.Zero, screen);
            if (Dc == IntPtr.Zero) return;

            // MM_TEXT = 1：逻辑单位即像素，负高度 = 字面像素高度
            SetMapMode(Dc, 1);
            var h = (int)MathF.Round(px);
            Font = CreateFontW(-h, 0, 0, 0, FwNormal, 0, 0, 0,
                               DefaultCharset, OutTtPrecis, ClipDefaultPrecis,
                               AntialiasedQuality, DefaultPitch | FfDontcare, family);
            if (Font == IntPtr.Zero) return;

            var old = SelectObject(Dc, Font);
            if (old == IntPtr.Zero) { DeleteObject(Font); return; }
            Ok = true;
        }

        /// <summary>实际选中的字体族名（GDI 找不到会替换，用它判断回退到哪了）。</summary>
        public string RealizedFamily()
        {
            var sb = new StringBuilder(64);
            return GetTextFaceW(Dc, sb.Capacity, sb) > 0 ? sb.ToString() : "";
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero)
            {
                if (Font != IntPtr.Zero) { SelectObject(Dc, IntPtr.Zero); DeleteObject(Font); }
                DeleteDC(Dc);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    /// <summary>候选字体族（与旧 GDI+ 版同一张表；系统上必有一个存在）。</summary>
    private static readonly string[] Families = ["Microsoft YaHei UI", "Microsoft YaHei", "SimHei"];

    /// <summary>
    /// 启动时定一次字体族：按顺序试，取第一个"真的选上了"的
    /// （<see cref="FontPen.RealizedFamily"/> 与请求一致才算选上；
    /// 全落空就用最后一个，让 GDI 自己兜底替换）。
    /// </summary>
    private static readonly string ResolvedFamily = InitFamily();

    private static string InitFamily()
    {
        foreach (var fam in Families)
        {
            using var probe = new FontPen(fam, 20f);
            if (!probe.Ok) continue;
            if (probe.RealizedFamily().StartsWith(fam, StringComparison.OrdinalIgnoreCase))
                return fam;
        }
        return Families[^1];
    }

    /// <summary>量一段单行文字的像素宽。</summary>
    public static float Measure(string text, float px)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        using var pen = new FontPen(ResolvedFamily, px);
        if (!pen.Ok) return text.Length * px * 0.6f;
        return GetTextExtentPoint32W(pen.Dc, text, text.Length, out var sz)
            ? sz.cx
            : text.Length * px * 0.6f;
    }

    /// <summary>行高（ascent + descent），用于把文字按视觉中心摆位。</summary>
    public static (float Ascent, float Descent) VMetrics(float px)
    {
        using var pen = new FontPen(ResolvedFamily, px);
        if (!pen.Ok) return (px * 0.9f, px * 0.25f);

        var buf = Marshal.AllocHGlobal(TextMetricsNativeSize);
        try
        {
            if (!GetTextMetricsW(pen.Dc, buf))
                return (px * 0.9f, px * 0.25f);
            var ascent = Marshal.ReadInt32(buf, 4);    // tmAscent
            var descent = Marshal.ReadInt32(buf, 8);   // tmDescent
            return (ascent > 0 ? ascent : px * 0.9f, descent > 0 ? descent : px * 0.25f);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// 把一行文字画进画布。<paramref name="baselineY"/> = 文字基线 Y（逻辑像素）。
    /// 默认自带 1px 暗影（与旧 GDI+ 版一致：白字压在渐变上边缘会发灰）。
    ///
    /// ⚠️ 浅色界面必须传 <c>shadow: false</c>：黑字右下角再压一条半透明黑影，
    ///    在白底上就是"字发虚 + 糊"，暗色下的那点立体感在浅色下完全不成立。
    /// </summary>
    public static void Draw(SoftCanvas canvas, string text, float px, float x, float baselineY,
                            byte r, byte g, byte b, byte a, bool shadow = true)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (shadow)
            RenderRun(canvas, text, px, x + 1f, baselineY + 1f, 0, 0, 0, 110);
        RenderRun(canvas, text, px, x, baselineY, r, g, b, a);
    }

    private static void RenderRun(SoftCanvas canvas, string text, float px, float x, float baselineY,
                                  byte r, byte g, byte b, byte a)
    {
        using var pen = new FontPen(ResolvedFamily, px);
        if (!pen.Ok) return;

        var mat2 = new Mat2
        {
            e11 = new Fixed { value = 1 },
            e12 = new Fixed(),
            e21 = new Fixed(),
            e22 = new Fixed { value = 1 },
        };

        var penX = x;
        foreach (var ch in text)
        {
            var u = (uint)ch;

            // ⚠️ 必须先以 **GGO_GRAY8_BITMAP 本身**问一次尺寸 + 拿度量：
            //    实测同一个字形，GGO_METRICS 报的墨盒能和灰度位图差 1px
            //    （"能" 16x16 vs 16x17）。拿 METRICS 的 box 去算灰度缓冲会把
            //    缓冲算小，GDI 回 GDI_ERROR、整枚字形静默丢失 —— 表现为
            //    "副标题缺前两个字"这种查半天查不到哪去的 bug（2026-10-21 实测）。
            //    单格式两趟式（null 问尺寸 → 分配 → 重取）是 MSDN 的正路。
            var need = GetGlyphOutlineW(pen.Dc, u, GgoGray8, out var gm, 0, null, ref mat2);
            if (need is 0 or GdiError)
            {
                // 空格 / 无字形：推进量还是得要（GGO_METRICS 的 advance 可靠，
                // 不可靠的只有 box，所以这里只借 advance）
                if (GetGlyphOutlineW(pen.Dc, u, GgoMetrics, out var m, 0, null, ref mat2) is not GdiError)
                    penX += m.gmCellIncX;
                else
                    penX += px;   // 兜底：按一个字宽推进，别把后面的字叠上来
                continue;
            }

            var bw = (int)gm.gmBlackBoxX;
            var bh = (int)gm.gmBlackBoxY;
            if (bw > 0 && bh > 0)
            {
                var buf = new byte[need];
                if (GetGlyphOutlineW(pen.Dc, u, GgoGray8, out gm, need, buf, ref mat2) is not GdiError)
                {
                    // 行距用"实际尺寸 ÷ 行数"反推（含 GDI 自己的对齐），别自己算
                    var pitch = (int)need / bh;
                    if (pitch >= bw)
                    {
                        // 0..64 → 0..255，抽成紧密排列的 mask 交给画布
                        var mask = new byte[bw * bh];
                        for (var row = 0; row < bh; row++)
                        {
                            for (var col = 0; col < bw; col++)
                            {
                                var v = buf[row * pitch + col];
                                mask[row * bw + col] = (byte)Math.Min(255, v * 4);
                            }
                        }

                        var gx = (int)MathF.Round(penX + gm.gmptGlyphOrigin.x);
                        var gy = (int)MathF.Round(baselineY - gm.gmptGlyphOrigin.y);
                        canvas.BlendMask(gx, gy, bw, bh, mask, r, g, b, a);
                    }
                }
            }

            penX += gm.gmCellIncX;
        }
    }
}
