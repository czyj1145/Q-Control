using System.Buffers.Binary;

namespace QControl.Core.Ec;

/// <summary>
/// EC 的 32 字节命令帧 —— 构造与解析都在这里（原 `Hal/Ec/EcBuffer.cs`，2026-10-23 上移）。
///
/// <para>
/// 上移到 Core 的原因：帧布局是**协议知识**，不是 Windows 知识。
/// 放 Core 才能在没有真机、没有管理员的环境里逐字节单测（旧位置只能靠实机试）。
/// </para>
///
/// 布局（DSDT 实证，见 `../../docs/PROTOCOL.md`）：
/// <code>
///   off  len  含义
///    0    2   命令组 GWF0：0xFA00 读 / 0xFB00 写
///    2    2   子命令 GWF1
///    4    4   GWD0（d0）= DAT2
///    8    4   GWD1（d1）= DAT3 —— 但 a/b/c/d 四个字节会**覆盖**它的低 4 字节
///   12    4   GWD2（d2）= DAT4
///   16    4   GWD3（d3）= DAT5
/// </code>
/// </summary>
public readonly struct EcFrame
{
    public const int Size = 32;

    // 应答里数据区的偏移（与原 `/api/ec/raw` 的 `u32` 键 DATx@y 一一对应）。
    //
    // 4..28 = 数据区全部 7 个 u32（32 字节帧去掉 4 字节命令头）。
    // 本机 NL5 的温度/转速/背光都在前 5 个（DAT2..DAT6），自动标定也先只看这 5 个；
    // DAT7/DAT8 是给**别的广达模具**留的兜底 —— 它们完全可能把温度放在数据区后半段
    // （2026-11-16 加，见 `EcLayoutAdvisor.TailOffsets`：只在标准槽一路温度都没认出时才扫）。
    public const int Dat2 = 4;
    public const int Dat3 = 8;
    public const int Dat4 = 12;
    public const int Dat5 = 16;
    public const int Dat6 = 20;
    public const int Dat7 = 24;
    public const int Dat8 = 28;

    private static readonly byte[] EmptyBytes = new byte[Size];

    private readonly byte[]? _bytes;

    /// <summary>
    /// 包一帧。长度必须正好 32 字节 —— GmWmi 已经把"长度不符"当失败重试过了，
    /// 走到这里还不是 32 就是代码问题，早点炸比让上层读越界的 0 好。
    /// </summary>
    public EcFrame(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != Size)
            throw new ArgumentException($"EC 帧必须是 {Size} 字节（收到 {bytes.Length} 字节）", nameof(bytes));
        _bytes = bytes;
    }

    /// <summary>全 0 帧。写命令传输失败时 EcHal 用它兜底（与旧实现一致）。</summary>
    public static EcFrame Empty => new(EmptyBytes);

    /// <summary>底层字节（<b>不复制</b>，与传输层共享同一份）。</summary>
    private byte[] Bytes => _bytes ?? EmptyBytes;

    public ReadOnlySpan<byte> Span => Bytes;

    public byte this[int index] => Bytes[index];

    /// <summary>EC 未就绪 / 缓冲被抢时会是全 0。**全 0 不能当数据用**（见 `/api/ec/raw` 的 note）。</summary>
    public bool IsAllZero => !Bytes.AsSpan().ContainsAnyExcept((byte)0);

    /// <summary>`B6 01 00 03 …` 形式（日志与回包里的 `raw` / `resp`）。</summary>
    public string Hex => string.Join(" ", Bytes.Select(x => x.ToString("X2")));

    /// <summary>小端 32 位（DAT2/DAT3/DAT4… 的取法）；越界返回 0。</summary>
    public uint U32(int offset)
        => offset >= 0 && offset + 4 <= Size
            ? BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(offset, 4))
            : 0;

    /// <summary>交给传输层的字节数组（不复制）。</summary>
    public byte[] ToArray() => Bytes;

    /// <summary>
    /// 按命令构造帧。
    /// </summary>
    /// <remarks>
    /// ⚠️ a/b/c/d 写在 offset 8..11，**故意覆盖 d1** —— 这是早先实现的原有行为：
    /// 背光命令把 B/G/R/DT3D 塞在这里，风扇命令把 UFA2 塞在 offset 8（`a`）。
    /// 照搬，不"修正"，否则与现有实现字节不一致（早先实现是黄金基准）。
    /// </remarks>
    public static EcFrame Build(ushort group, EcCommand cmd) => Build(group, cmd.Sub, cmd.Args);

    public static EcFrame Build(ushort group, ushort sub, EcArgs args)
    {
        var buf = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), group);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), sub);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), args.D0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), args.D1);
        buf[8] = args.A;
        buf[9] = args.B;
        buf[10] = args.C;
        buf[11] = args.D;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), args.D2);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), args.D3);
        // 偏移 20 = 协议里的 a6 / DAT6。原实现根本写不到这一段（EcArgs 只有 4 个 u32），
        // Win 键锁 (0x0200 写 a6) 是第一个用到它的命令。
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(EcFrame.Dat6), args.D4);
        return new EcFrame(buf);
    }
}
