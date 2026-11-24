using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Ec;

namespace QControl.Tests;

/// <summary>
/// EC 命令的强类型层（2026-10-23 技术债收口）。
///
/// 这一层是给"发错字节"设的围栏，所以测试不测"函数能跑通"，只测
/// **字节落在哪个偏移** —— 旧版是黄金基准，偏移错一个字节
/// 就是"右风扇不动"或"背光变色"那种实机事故。
/// </summary>
public class EcCommandTests
{
    private static EcSpec Spec(string json = """{"read_group":"0xFA00","write_group":"0xFB00"}""")
        => new(JsonNode.Parse(json));

    // ---------------------------------------------------------------- 帧布局

    [Fact]
    public void 帧头是组号加子命令各两字节小端()
    {
        var frame = EcFrame.Build(0xFB00, EcCommand.Write(0x0205));

        Assert.Equal(0x00, frame[0]);
        Assert.Equal(0xFB, frame[1]);
        Assert.Equal(0x05, frame[2]);
        Assert.Equal(0x02, frame[3]);
    }

    [Fact]
    public void 四个32位参数各占四字节()
    {
        var args = EcArgs.Empty
            .With(EcSlot.D0, 0x11223344)
            .With(EcSlot.D2, 0x55667788)
            .With(EcSlot.D3, 0x99AABBCC);

        var frame = EcFrame.Build(0xFB00, EcCommand.Write(0x0205, args));

        Assert.Equal(0x11223344u, frame.U32(EcFrame.Dat2));
        Assert.Equal(0x55667788u, frame.U32(EcFrame.Dat4));
        Assert.Equal(0x99AABBCCu, frame.U32(EcFrame.Dat5));
    }

    [Fact]
    public void abcd四个字节覆盖d1的低四字节()
    {
        // 这是旧版 build_buf 的原样行为，也是"右风扇停转"那次事故的现场：
        // 把 UFA2 写进 d1，再让 a=0 覆盖掉 ⇒ EC 收到 0。
        var args = EcArgs.Empty
            .With(EcSlot.D1, 0xFFFFFFFF)
            .With(EcSlot.A, 0x11)
            .With(EcSlot.B, 0x22)
            .With(EcSlot.C, 0x33)
            .With(EcSlot.D, 0x44);

        var frame = EcFrame.Build(0xFB00, EcCommand.Write(0x0205, args));

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44 }, frame.Span.Slice(8, 4).ToArray());
        // 覆盖是**整四字节**：d1 一位都留不下（所以把 UFA2 放 d1 = 白写）
        Assert.Equal(0x44332211u, frame.U32(EcFrame.Dat3));
    }

    [Fact]
    public void 本机风扇槽位把三个转速放到d0_a_d2()
    {
        // 机型档案 ec.fan_slots = ["d0","a","d2"]（本机实测：UFA2 在 offset 8 那个字节）
        var slots = new[] { "d0", "a", "d2" };
        int[] duties = [40, 50, 60];

        var args = EcArgs.Empty;
        for (var i = 0; i < slots.Length; i++)
            args = args.With(slots[i], (uint)duties[i]);

        var frame = EcFrame.Build(0xFB00, EcCommand.Write(0x0205, args));

        Assert.Equal(40u, frame[4]);          // 风扇 1 = d0
        Assert.Equal(50u, frame[8]);          // 风扇 2 = a（覆盖区，不是 d1！）
        Assert.Equal(60u, frame[12]);         // 风扇 3 = d2
        Assert.Equal(0u, frame[9]);           // b/c/d 必须留在 0，否则会改写 d1 高字节
        Assert.Equal(0u, frame[16]);          // d3 不参与风扇
    }

    [Fact]
    public void 本机背光槽位是地址加BGR加DT3D()
    {
        // 0x0100：d0 = LED 地址，a/b/c = B/G/R，d = (effect<<4)|level
        const int addr = 6, red = 255, green = 128, blue = 0, level = 2, effect = 1;
        var dt3d = (byte)((effect << 4) | (level & 0x0F));

        var args = EcArgs.Empty
            .With(EcSlot.D0, (uint)addr)
            .With(EcSlot.A, (uint)blue)
            .With(EcSlot.B, (uint)green)
            .With(EcSlot.C, (uint)red)
            .With(EcSlot.D, dt3d);

        var frame = EcFrame.Build(0xFB00, EcCommand.Write(0x0100, args));

        Assert.Equal(addr, frame[4]);
        Assert.Equal(blue, frame[8]);
        Assert.Equal(green, frame[9]);
        Assert.Equal(red, frame[10]);
        Assert.Equal(0x12, frame[11]);        // effect=1 level=2
    }

    // ---------------------------------------------------------------- 槽位

    [Theory]
    [InlineData("d0", EcSlot.D0)]
    [InlineData("D3", EcSlot.D3)]
    [InlineData(" a ", EcSlot.A)]
    [InlineData("d", EcSlot.D)]
    public void 槽名大小写和空格都认(string name, EcSlot expected)
    {
        Assert.Equal(expected, EcArgs.ParseSlot(name));
    }

    [Theory]
    [InlineData("e")]
    [InlineData("")]
    [InlineData("d5")]
    public void 未知槽名直接报错而不是当0(string name)
    {
        // 档案里写错一个槽名，旧实现 (byte)Val(...) 会静默按 0 发 —— 等于不写
        var e = Assert.Throws<ArgumentException>(() => EcArgs.Empty.With(name, 40));
        Assert.Contains(name, e.Message);
    }

    [Fact]
    public void D4槽落在帧偏移20也就是协议里的a6()
    {
        // 2026-11-22：Win 键锁（0x0200 写 a6）是第一条用到这段的命令。
        // 偏移错一位就写到 DAT5（a5）上 —— 那是风扇槽，后果不是一个开关失灵。
        var args = EcArgs.Empty.With("d4", 1);
        Assert.Equal(1u, args.D4);
        var frame = EcFrame.Build(0xFB00, EcCommand.Write(0x0200, args));
        Assert.Equal(1, frame[20]);
        Assert.Equal(0, frame[16]);           // d3 没被误写
        Assert.Equal(0, frame[24]);           // 也没越过帧尾
    }

    [Fact]
    public void 字节槽超过255直接拒绝()
    {
        // 旧实现 (byte)300 = 44，EC 会照着 44 执行
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => EcArgs.Empty.With(EcSlot.A, 300));
        Assert.Contains("1 字节", e.Message);
    }

    [Fact]
    public void 字节槽255是合法上界()
    {
        Assert.Equal((byte)255, EcArgs.Empty.With(EcSlot.A, 255).A);
    }

    [Fact]
    public void 从数组填槽按d0到d的顺序()
    {
        var args = EcArgs.FromSequence(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Equal(1u, args.D0);
        Assert.Equal(4u, args.D3);
        Assert.Equal((byte)5, args.A);
        Assert.Equal((byte)8, args.D);
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 }, args.ToSequence());
    }

    [Fact]
    public void 从数组填槽时超长部分忽略()
    {
        var args = EcArgs.FromSequence(new uint[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 });
        Assert.Equal((byte)9, args.D);
    }

    [Fact]
    public void FromD0只改d0其余保持0()
    {
        var args = EcArgs.FromD0(3);
        Assert.Equal(3u, args.D0);
        Assert.Equal(new uint[] { 3, 0, 0, 0, 0, 0, 0, 0 }, args.ToSequence());
    }

    // ---------------------------------------------------------------- 方向 / 组号

    [Fact]
    public void 读命令走档案的读组写命令走写组()
    {
        var spec = Spec();

        Assert.False(EcCommand.Read(0x0200).IsWrite);
        Assert.Equal(0xFA00, EcCommand.Read(0x0200).GroupFor(spec));

        Assert.True(EcCommand.Write(0x0300).IsWrite);
        Assert.Equal(0xFB00, EcCommand.Write(0x0300).GroupFor(spec));
    }

    [Fact]
    public void 同一条子命令的读写是两个不同的命令对象()
    {
        // 0x0203 双面：读组只读状态，写组才真改显卡输出。
        // 靠"组号等不等于读组"去猜方向，就是"点了没反应也不报错"的来源。
        var read = EcCommand.Read(0x0203);
        var write = EcCommand.Write(0x0203);

        Assert.NotEqual(read.Direction, write.Direction);
        Assert.NotEqual(read.GroupFor(Spec()), write.GroupFor(Spec()));
    }

    [Fact]
    public void 组号可以显式覆盖供raw调试()
    {
        var cmd = new EcCommand(0x0200, EcDirection.Read, EcArgs.Empty, 0x1234);

        Assert.Equal(0x1234, cmd.GroupFor(Spec()));
    }

    [Fact]
    public void 组号来自档案不是写死的()
    {
        var other = Spec("""{"read_group":"0xAA00","write_group":"0xBB00"}""");

        Assert.Equal(0xAA00, EcCommand.Read(0x0200).GroupFor(other));
        Assert.Equal(0xBB00, EcCommand.Write(0x0200).GroupFor(other));
    }

    [Fact]
    public void 命令的日志写法带方向()
    {
        Assert.Equal("0x0300 write", EcCommand.Write(0x0300).ToString());
        Assert.Equal("0x0200 read", EcCommand.Read(0x0200).ToString());
    }

    // ---------------------------------------------------------------- 帧解析

    [Fact]
    public void 长度不是32字节直接拒绝()
    {
        Assert.Throws<ArgumentException>(() => new EcFrame(new byte[16]));
        Assert.Throws<ArgumentException>(() => new EcFrame(new byte[33]));
    }

    [Fact]
    public void U32越界读回0而不是抛()
    {
        var frame = EcFrame.Build(0xFA00, EcCommand.Read(0x0200));

        Assert.Equal(0u, frame.U32(EcFrame.Size));      // 正好越界
        Assert.Equal(0u, frame.U32(-1));
        Assert.Equal(0u, frame.U32(29));                // 只差一个字节
    }

    [Fact]
    public void 全0帧可以被识别()
    {
        // EC 未就绪 / 缓冲被抢时就是这个样子，不能当数据用
        Assert.True(EcFrame.Empty.IsAllZero);
        Assert.False(EcFrame.Build(0xFA00, EcCommand.Read(0x0200)).IsAllZero);
    }

    [Fact]
    public void 默认构造的帧当全0处理不崩()
    {
        EcFrame frame = default;

        Assert.Equal(EcFrame.Size, frame.Span.Length);
        Assert.True(frame.IsAllZero);
        Assert.Equal(0u, frame.U32(EcFrame.Dat2));
    }

    [Fact]
    public void Hex按字节大写空格分隔()
    {
        // 日志/回包里 grep 靠这个格式
        Assert.Equal("00 FA 00 02", EcFrame.Build(0xFA00, EcCommand.Read(0x0200)).Hex[..11]);
    }
}
