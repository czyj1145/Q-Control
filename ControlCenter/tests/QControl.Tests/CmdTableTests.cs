using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// 命令分级表。这张表决定"这条命令要不要拦一道"，
/// 少一条 0x0300 = 用户点一下性能模式就直接改了 EC。
/// </summary>
public class CmdTableTests
{
    [Theory]
    [InlineData(0x0200)]
    [InlineData(0x0202)]
    [InlineData(0x0203)]
    [InlineData(0x0207)]
    [InlineData(0x0400)]
    public void 只读命令必须登记在案(ushort cmd)
    {
        Assert.True(CmdTable.IsReadOnly(cmd), $"0x{cmd:X4} 应当是只读命令");
    }

    [Theory]
    [InlineData(0x0300)]
    [InlineData(0x0206)]
    [InlineData(0x0100)]
    [InlineData(0x0204)]
    [InlineData(0x0205)]
    [InlineData(0x0203)]
    public void 危险写命令必须有风险描述(ushort cmd)
    {
        // 没有描述的写命令 = 前端弹窗没内容可说 = 用户不知道自己在点什么
        var risk = CmdTable.Risk(cmd);
        Assert.NotNull(risk);
        Assert.NotEmpty(risk!);
    }

    [Theory]
    [InlineData(0x0200)]
    [InlineData(0x0202)]
    public void 只读命令没有风险描述(ushort cmd)
    {
        Assert.Null(CmdTable.Risk(cmd));
    }

    [Theory]
    [InlineData(0x0300)]
    [InlineData(0x0206)]
    [InlineData(0x0203)]
    public void 高危命令必须二次确认(ushort cmd)
    {
        Assert.True(CmdTable.NeedsConfirm(cmd), $"0x{cmd:X4} 应当需要 arm() 二次确认");
        // 而且它们在危险表里也得有描述 —— 两道门必须同时存在
        Assert.NotNull(CmdTable.Risk(cmd));
    }

    [Theory]
    [InlineData(0x0205)]     // 手动风扇只危险、不需二次确认
    [InlineData(0x0100)]     // 背光同理
    public void 普通写命令不需要二次确认(ushort cmd)
    {
        Assert.NotNull(CmdTable.Risk(cmd));
        Assert.False(CmdTable.NeedsConfirm(cmd));
    }

    [Theory]
    [InlineData("0x0300", 0x0300)]
    [InlineData("0X0300", 0x0300)]
    [InlineData("768", 0x0300)]
    [InlineData(" 0x0200 ", 0x0200)]
    public void 十六进制与十进制两种写法都认(string text, ushort expected)
    {
        Assert.Equal(expected, CmdTable.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0xZZZZ")]
    [InlineData(null)]
    public void 非法输入返回null而不是猜一个(string? text)
    {
        Assert.Null(CmdTable.Parse(text));
    }

    [Fact]
    public void Hex格式化固定四位大写()
    {
        // 日志里 0x300 和 0x0300 混着出现，grep 会漏
        Assert.Equal("0x0300", CmdTable.Hex(0x0300));
        Assert.Equal("0x0100", CmdTable.Hex(0x0100));
    }
}
