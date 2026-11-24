using QControl.Core.Ec;

namespace QControl.Tests;

/// <summary>
/// 采集期「时序与回显自检」的判定规则。
///
/// 这两条以前只能作者手工标：回显要看回帧前 4 字节对不对，读间隔要连发几十次找拐点。
/// 规则写错的方向都很糟 —— 把不回显的命令标成回显，会让每次读都在"回显不符"上重试三次；
/// 把安全值挑成 0，会让整帧读数偶发脏帧，而症状是"温度偶尔跳到别的值"，根本查不出来。
/// </summary>
public class EcEchoTests
{
    [Theory]
    [InlineData("00 FA 00 02 3A 00 00 00", 0x0200, true)]   // status 回显
    [InlineData("00 FA 05 02 2D 00 00 00", 0x0205, true)]   // 写组同一个形状
    [InlineData("00 00 00 00 02 00 00 00", 0x0203, false)]  // gfx 实测不回显
    [InlineData("14 01 02 02 00 00 00 00", 0x0202, false)]  // read_perf 实测不回显
    [InlineData("00 FA 00 02 3A 00 00 00", 0x0202, false)]  // 回显是别人的命令号 ⇒ 不算
    [InlineData("", 0x0200, false)]
    [InlineData("00 FA", 0x0200, false)]                    // 帧太短
    public void 只有前四字节等于组与子命令才算回显(string resp, int sub, bool expect)
        => Assert.Equal(expect, EcEcho.Matches(resp, 0xFA00, (ushort)sub));

    [Fact]
    public void 回显判定不区分大小写()
        => Assert.True(EcEcho.Matches("00 fa 00 02 3a", 0xFA00, 0x0200));

    [Fact]
    public void 建议值取第一个零失败档再加余量()
    {
        var rows = new[] { (0.0, 5), (0.005, 2), (0.01, 0), (0.02, 0) };

        var suggest = EcEcho.PickReadGap(rows, margin: 0.01, max: 0.2);

        Assert.Equal(0.02, suggest);        // 0.01 零失败 + 0.01 余量
    }

    [Fact]
    public void 连最小档都零失败时建议值就是余量本身()
    {
        var rows = new[] { (0.0, 0), (0.01, 0) };

        Assert.Equal(0.01, EcEcho.PickReadGap(rows, margin: 0.01, max: 0.2));
    }

    [Fact]
    public void 建议值不超过上限()
    {
        var rows = new[] { (0.0, 9), (0.05, 0) };

        Assert.Equal(0.06, EcEcho.PickReadGap(rows, margin: 0.01, max: 0.2));
    }

    [Fact]
    public void 所有档位都失败时不建议任何值()
    {
        // 这里最容易写错：`.Where(...).FirstOrDefault()` 会返回 0，
        // 于是"全都失败"被当成"0 也可行"—— 正好是最激进的值。
        var rows = new[] { (0.0, 9), (0.01, 4), (0.05, 1) };

        Assert.Null(EcEcho.PickReadGap(rows, margin: 0.01, max: 0.2));
    }

    [Fact]
    public void 没有数据时不建议任何值()
        => Assert.Null(EcEcho.PickReadGap([], margin: 0.01, max: 0.2));
}
