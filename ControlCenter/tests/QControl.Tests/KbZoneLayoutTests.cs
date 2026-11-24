using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 键盘三区的切法（原厂实拍）。这里钉的是**哪颗键亮哪一色**，
/// 因为它错一格就是"灯亮错一格"，而界面那边改坐标/比例时是最容易顺手改坏的。
///
/// 坐标是键帽中心（u）：主键区 15u，整键盘 19u。F 行有自己的两条边界。
/// </summary>
public class KbZoneLayoutTests
{
    // F 行：Esc 0.5 / F1 1.5 / … / F12 12.5 / PrtSc 13.5 / Del 14.5 / Home 15.5 …
    // 其它行：` 0.5 / 1..0 → 1.5..10.5 / - 11.5 / = 12.5 / Backspace 14
    //        Tab 0.75 / Q 2 / W 3 / E 4 / R 5 / T 6 … / \ 14.25
    //        Caps 0.875 / A 2.25 / S 3.25 / D 4.25 / F 5.25 / G 6.25 … / Enter 13.875
    //        Shift 1.3 / Z 3.1 / X 4.1 / C 5.1 / V 6.1 … / 右 Shift 13.8
    //        Ctrl 0.8 / Fn 2.1 / ⊞ 3.1 / Alt 4.1 / Space 6.8

    [Theory]
    [InlineData(4.5, 0)]      // F4
    [InlineData(5.5, 0)]      // F5 —— 用户："F5 是一区的"
    [InlineData(6.5, 1)]      // F6
    [InlineData(12.5, 1)]     // F12
    [InlineData(13.5, 1)]     // 截图键 PrtSc —— 用户："截图键是二区的"
    [InlineData(14.5, 2)]     // Del
    [InlineData(18.5, 2)]     // PgDn
    public void F行_绿到F5_蓝到截图键(double centerU, int expect)
        => Assert.Equal(expect, KbZoneLayout.ZoneOf(centerU, 3, topRow: true));

    [Theory]
    [InlineData(0.5, 0)]      // Esc 下面那一列的 `
    [InlineData(4.5, 0)]      // 4
    [InlineData(5.5, 1)]      // 5（同一列在 F 行是绿的，这里必须是蓝）
    [InlineData(5.0, 0)]      // R
    [InlineData(6.0, 1)]      // T
    [InlineData(5.25, 0)]     // F
    [InlineData(6.25, 1)]     // G
    [InlineData(5.1, 0)]      // C
    [InlineData(6.1, 1)]      // V
    [InlineData(4.1, 0)]      // 左 Alt
    [InlineData(6.8, 1)]      // 空格
    [InlineData(13.0, 1)]     // ]
    [InlineData(14.0, 2)]     // Backspace
    [InlineData(13.875, 2)]   // 主键区 Enter
    [InlineData(13.8, 2)]     // 右 Shift
    public void 其它行_绿到第五列_红从右边缘起(double centerU, int expect)
        => Assert.Equal(expect, KbZoneLayout.ZoneOf(centerU, 3));

    [Fact]
    public void 单区键盘全都算第一区()
    {
        Assert.Equal(0, KbZoneLayout.ZoneOf(0.5, 1, topRow: true));
        Assert.Equal(0, KbZoneLayout.ZoneOf(18.5, 1));
    }

    [Theory]
    [InlineData(1.0, 0)]      // 双区：0~7.5 第一区
    [InlineData(7.0, 0)]
    [InlineData(8.0, 1)]
    [InlineData(14.0, 1)]
    public void 双区按主键等分(double centerU, int expect)
        => Assert.Equal(expect, KbZoneLayout.ZoneOf(centerU, 2));

    [Theory]
    [InlineData(1.0, 0)]      // 四区：每 3.75u 一区
    [InlineData(4.0, 1)]
    [InlineData(8.0, 2)]
    [InlineData(12.0, 3)]
    public void 四区按主键等分(double centerU, int expect)
        => Assert.Equal(expect, KbZoneLayout.ZoneOf(centerU, 4));
}
