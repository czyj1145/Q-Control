namespace QControl.Core.Lights;

/// <summary>
/// HSV ↔ RGB 与 0~255 取整。**纯函数，无 WPF 依赖**（入参出参都是数字，不是 Color）。
///
/// 为什么放在 Core：这套数学原先在两处各写了一遍 —— `LightView` 的预设色块
/// （`int[]`）与 `ColorWheel` 的调色盘（`byte`）。两份实现只要有一处被"顺手优化"，
/// 同一个色相在色块和调色盘上就会显示成两个颜色，而这种偏差肉眼很难归因。
///
/// ⚠️ **取整用 <see cref="Math.Round(double)"/> 的默认行为（银行家舍入）**，
///    不是 `(int)(v*255)` 截断：0.5 → 128 而不是 127。改这里等于改所有颜色的实际 RGB。
/// </summary>
public static class ColorMath
{
    /// <summary>hsv（h 度、s/v 0~1）→ rgb 0~255。色相自动绕回 0~360（-30 与 330 等价）。</summary>
    public static (int R, int G, int B) HsvToRgb(double hue, double s, double v)
    {
        hue = (hue % 360 + 360) % 360;
        var c = v * s;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = v - c;
        var (r1, g1, b1) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (ToByte(r1 + m), ToByte(g1 + m), ToByte(b1 + m));
    }

    /// <summary>rgb 0~255 → hsv（h 度 0~360、s/v 0~1）。灰色与黑色返回 h=0,s=0。</summary>
    public static (double H, double S, double V) RgbToHsv(int r, int g, int b)
    {
        double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
        var max = Math.Max(rr, Math.Max(gg, bb));
        var min = Math.Min(rr, Math.Min(gg, bb));
        var d = max - min;

        double h = 0;
        if (d > 0)
        {
            if (max == rr) h = ((gg - bb) / d + 6) % 6;
            else if (max == gg) h = (bb - rr) / d + 2;
            else h = (rr - gg) / d + 4;
            h *= 60;
        }
        return (h, max > 0 ? d / max : 0, max);
    }

    /// <summary>0~1 的强度 → 0~255 字节（钳住越界，银行家舍入）。</summary>
    public static int ToByte(double v) => (int)Math.Clamp(Math.Round(v * 255), 0, 255);
}
