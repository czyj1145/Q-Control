using QControl.Core.Ec;

namespace QControl.Tests;

/// <summary>
/// EC 读数合理性判据。2026-11-22 引入：未适配机型借 NL5 的 layout 读温度/转速，
/// 偏移对不上时只能靠"越界"识别垃圾值，越界的交给 HWiNFO 兜底。
/// </summary>
public class EcSanityTests
{
    [Theory]
    [InlineData(25, true)]
    [InlineData(58, true)]
    [InlineData(95, true)]
    [InlineData(115, true)]      // 边界含
    [InlineData(116, false)]
    [InlineData(999, false)]
    [InlineData(0, false)]       // 0 ℃ 只可能是没读到
    [InlineData(-1, false)]
    public void 温度越界一律当没读到(double c, bool ok)
        => Assert.Equal(ok, EcSanity.Temp(c));

    [Fact]
    public void 没有温度节点时判不可信()
        => Assert.False(EcSanity.Temp(null));

    [Theory]
    [InlineData(0, true)]        // 停转合法
    [InlineData(1200, true)]
    [InlineData(5400, true)]
    [InlineData(8000, true)]     // 边界含
    [InlineData(8001, false)]
    [InlineData(-1, false)]
    public void 转速只拒负数与超大值(double rpm, bool ok)
        => Assert.Equal(ok, EcSanity.Rpm(rpm));

    [Fact]
    public void 没有转速节点时判不可信()
        => Assert.False(EcSanity.Rpm(null));
}
