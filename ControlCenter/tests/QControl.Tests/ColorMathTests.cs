using QControl.Core.Lights;

namespace QControl.Tests;

/// <summary>
/// HSV ↔ RGB。这套数学原先在 `LightView`（预设色块）与 `ColorWheel`（调色盘）各写一份，
/// 一旦分叉，同一个色相在两个控件上会显示成两种颜色 —— 这里把它钉死。
/// </summary>
public class ColorMathTests
{
    // ---- 六个基色：色相必须落在整格上 ----

    [Theory]
    [InlineData(0, 255, 0, 0)]        // 红
    [InlineData(60, 255, 255, 0)]     // 黄
    [InlineData(120, 0, 255, 0)]      // 绿
    [InlineData(180, 0, 255, 255)]    // 青
    [InlineData(240, 0, 0, 255)]      // 蓝
    [InlineData(300, 255, 0, 255)]    // 品红
    [InlineData(360, 255, 0, 0)]      // 绕回红
    public void 基色(double hue, int r, int g, int b)
        => Assert.Equal((r, g, b), ColorMath.HsvToRgb(hue, 1, 1));

    [Fact]
    public void 色相自动绕回()
    {
        Assert.Equal(ColorMath.HsvToRgb(330, 1, 1), ColorMath.HsvToRgb(-30, 1, 1));
        Assert.Equal(ColorMath.HsvToRgb(30, 1, 1), ColorMath.HsvToRgb(390, 1, 1));
        Assert.Equal(ColorMath.HsvToRgb(0, 1, 1), ColorMath.HsvToRgb(720, 1, 1));
    }

    [Fact]
    public void 饱和度为0是灰阶()
    {
        Assert.Equal((255, 255, 255), ColorMath.HsvToRgb(123, 0, 1));
        Assert.Equal((128, 128, 128), ColorMath.HsvToRgb(123, 0, 0.5));   // 127.5 银行家舍入 → 128
        Assert.Equal((0, 0, 0), ColorMath.HsvToRgb(123, 0, 0));
    }

    [Fact]
    public void 亮度为0永远是黑()
        => Assert.Equal((0, 0, 0), ColorMath.HsvToRgb(200, 1, 0));

    [Fact]
    public void 半格色相给出的中间值()
    {
        // 30°：c=1, x=0.5 → (1, 0.5, 0)；0.5*255=127.5 → 128
        Assert.Equal((255, 128, 0), ColorMath.HsvToRgb(30, 1, 1));
    }

    // ---- 反向：RGB → HSV ----

    [Theory]
    [InlineData(255, 0, 0, 0)]
    [InlineData(255, 255, 0, 60)]
    [InlineData(0, 255, 0, 120)]
    [InlineData(0, 255, 255, 180)]
    [InlineData(0, 0, 255, 240)]
    [InlineData(255, 0, 255, 300)]
    public void 反向色相(int r, int g, int b, double hue)
    {
        var (h, s, v) = ColorMath.RgbToHsv(r, g, b);
        Assert.Equal(hue, h, 3);
        Assert.Equal(1, s, 3);
        Assert.Equal(1, v, 3);
    }

    [Fact]
    public void 灰色没有色相()
    {
        var (h, s, v) = ColorMath.RgbToHsv(128, 128, 128);
        Assert.Equal(0, h);
        Assert.Equal(0, s);
        Assert.Equal(128 / 255.0, v, 3);
    }

    [Fact]
    public void 黑色全零()
        => Assert.Equal((0, 0, 0), ColorMath.RgbToHsv(0, 0, 0));

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(137)]
    [InlineData(210)]
    [InlineData(300)]
    public void 往返一圈色相基本不变(double hue)
    {
        var (r, g, b) = ColorMath.HsvToRgb(hue, 0.8, 0.9);
        var (h2, s2, v2) = ColorMath.RgbToHsv(r, g, b);
        Assert.Equal(hue, h2, 1);       // 只差整数量化的误差
        Assert.Equal(0.8, s2, 2);
        Assert.Equal(0.9, v2, 2);
    }

    // ---- 取整：这里改一位，所有颜色的实际 RGB 都会变 ----

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 255)]
    [InlineData(0.5, 128)]      // 银行家舍入，不是 127
    [InlineData(0.5019607843, 128)]
    [InlineData(-1.0, 0)]       // 钳住下界
    [InlineData(2.0, 255)]      // 钳住上界
    public void 取整规则(double v, int want)
        => Assert.Equal(want, ColorMath.ToByte(v));
}
