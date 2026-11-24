using QControl.Core.Devices;
using QControl.Core.Model;

namespace QControl.Core.Ec;

/// <summary>
/// EC 命令的 8 个参数槽。
/// <para>
/// ⚠️ **枚举顺序就是 `/api/ec/raw` 的 args 顺序**（d0,d1,d2,d3,a,b,c,d）——
/// <see cref="EcArgs.FromSequence"/> 直接按下标映射，改顺序等于改 HTTP 契约。
/// </para>
/// </summary>
public enum EcSlot
{
    D0 = 0,
    D1 = 1,
    D2 = 2,
    D3 = 3,
    A = 4,
    B = 5,
    C = 6,
    D = 7,

    /// <summary>
    /// 帧偏移 20 的整 32 位（= 协议里的 a6 / DAT6）。
    ///
    /// <para>
    /// **不进 `/api/ec/raw` 的 args 契约**（那条仍然是 8 个：d0..d3,a..d），
    /// 只给代码内部的专用写用 —— 目前唯一用户在 <c>EcHal.SetWinKeyLock</c>（Win 键锁走 a6）。
    /// 加在枚举末尾而不是插在中间，就是为了不改变 <see cref="EcArgs.FromSequence"/> 的下标映射。
    /// </para>
    /// </summary>
    D4 = 8,
}

/// <summary>命令方向（决定发 0xFA00 还是 0xFB00），由机型档案给出具体组号。</summary>
public enum EcDirection
{
    Read,
    Write,
}

/// <summary>
/// 一条 EC 命令的参数（GWD0..GWD3 + 覆盖区 A..D）。
///
/// **为什么要有这个类型**（2026-10-23 技术债）：旧实现全程是
/// <c>Exchange(sub, group, d0, d1, d2, d3, a, b, c, d)</c> 这样的裸位置参数，
/// 两种事故都发生过：
/// <list type="number">
/// <item><b>槽位写错没人拦</b>：`set_fans` 早期把 UFA2 塞进 `d1`，被 `a=0` 覆盖，
///   结果是"左风扇满转、右风扇不动"。位置参数下 d1 和 a 长得一样，编译期查不出。</item>
/// <item><b>字节槽被静默截断</b>：`(byte)300` 悄悄变成 44，EC 照 44 执行。</item>
/// </list>
/// 现在改成<b>按名字给值</b>（槽名同时是机型档案 `ec.fan_slots` 里的写法和日志里的叫法），
/// 字节槽越界直接抛，而不是把错误数字发给硬件。
/// </summary>
public readonly record struct EcArgs
{
    /// <summary>全 0（绝大多数读命令和"只用 d0"的写命令的常态）。</summary>
    public static readonly EcArgs Empty = default;

    public uint D0 { get; init; }
    public uint D1 { get; init; }
    public uint D2 { get; init; }
    public uint D3 { get; init; }
    public byte A { get; init; }
    public byte B { get; init; }
    public byte C { get; init; }
    public byte D { get; init; }

    /// <summary>帧偏移 20 的整 32 位（协议里的 a6）。见 <see cref="EcSlot.D4"/>。</summary>
    public uint D4 { get; init; }

    /// <summary>按槽写入。<b>字节槽（a..d）超过 255 直接抛</b>，绝不静默截断。</summary>
    public EcArgs With(EcSlot slot, uint value) => slot switch
    {
        EcSlot.D0 => this with { D0 = value },
        EcSlot.D1 => this with { D1 = value },
        EcSlot.D2 => this with { D2 = value },
        EcSlot.D3 => this with { D3 = value },
        EcSlot.A => this with { A = ByteOf(slot, value) },
        EcSlot.B => this with { B = ByteOf(slot, value) },
        EcSlot.C => this with { C = ByteOf(slot, value) },
        EcSlot.D => this with { D = ByteOf(slot, value) },
        EcSlot.D4 => this with { D4 = value },
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "未知的 EC 参数槽"),
    };

    /// <summary>按档案里的槽名写入（`"d0"` / `"a"` …，见 <see cref="EcSlot"/>）。</summary>
    public EcArgs With(string slot, uint value) => With(ParseSlot(slot), value);

    public uint Get(EcSlot slot) => slot switch
    {
        EcSlot.D0 => D0,
        EcSlot.D1 => D1,
        EcSlot.D2 => D2,
        EcSlot.D3 => D3,
        EcSlot.A => A,
        EcSlot.B => B,
        EcSlot.C => C,
        EcSlot.D => D,
        EcSlot.D4 => D4,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "未知的 EC 参数槽"),
    };

    public uint Get(string slot) => Get(ParseSlot(slot));

    /// <summary>
    /// 只给 D0 的快捷写法 —— 性能模式 / 显卡模式这两条命令就只吃这一个参数。
    /// </summary>
    public static EcArgs FromD0(uint d0) => Empty.With(EcSlot.D0, d0);

    /// <summary>
    /// `/api/ec/raw` 的 args 数组（顺序 = <see cref="EcSlot"/> 枚举顺序）。
    /// 超过 8 个的尾部丢弃（HTTP 层已经按 8 个解析）。
    /// </summary>
    public static EcArgs FromSequence(IReadOnlyList<uint> values)
    {
        var args = Empty;
        for (var i = 0; i < values.Count && i <= (int)EcSlot.D; i++)
            args = args.With((EcSlot)i, values[i]);
        return args;
    }

    /// <summary>反向：按枚举顺序摊成 8 个数（回显给 `/api/ec/raw` 的 `req` 字段）。</summary>
    public uint[] ToSequence()
        => [D0, D1, D2, D3, A, B, C, D];

    /// <summary>槽名 → 枚举。槽名的真源是机型档案的 `ec.fan_slots`，大小写不敏感。</summary>
    public static EcSlot ParseSlot(string name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "d0" => EcSlot.D0,
        "d1" => EcSlot.D1,
        "d2" => EcSlot.D2,
        "d3" => EcSlot.D3,
        "d4" => EcSlot.D4,
        "a" => EcSlot.A,
        "b" => EcSlot.B,
        "c" => EcSlot.C,
        "d" => EcSlot.D,
        _ => throw new ArgumentException(
            $"未知的 EC 参数槽「{name}」—— 只认 d0/d1/d2/d3/a/b/c/d（机型档案 ec.fan_slots 用的是这套写法）",
            nameof(name)),
    };

    /// <summary>枚举 → 槽名（日志/报错里用，和档案写法一致）。</summary>
    public static string SlotName(EcSlot slot) => slot switch
    {
        EcSlot.D0 => "d0",
        EcSlot.D1 => "d1",
        EcSlot.D2 => "d2",
        EcSlot.D3 => "d3",
        EcSlot.D4 => "d4",
        EcSlot.A => "a",
        EcSlot.B => "b",
        EcSlot.C => "c",
        EcSlot.D => "d",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "未知的 EC 参数槽"),
    };

    private static byte ByteOf(EcSlot slot, uint value)
    {
        if (value > byte.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(value), value,
                $"{SlotName(slot)} 槽是 1 字节（0..255），{value} 会被静默截断成 {(byte)value} —— "
                + "拒绝下发。先确认调用方是不是把 32 位值塞进了字节槽。");
        return (byte)value;
    }
}

/// <summary>
/// 一条 EC 命令 = **子命令号 + 方向 + 参数**。
///
/// <para>
/// 方向是命令自身的属性，不是调用现场临时判的：同一条 0x0203 在读组只读状态、
/// 在写组才真改显示输出（"切换显卡模式"点了一下既没报错也没效果，假成功过一次）。
/// 现在读和写是两个不同的构造入口（<see cref="Read"/> / <see cref="Write"/>），
/// 传错了编译期就看得出，运行时守卫也不必再靠"组号等不等于读组"去猜。
/// </para>
/// <para>
/// 具体组号（0xFA00 / 0xFB00）仍然来自机型档案，由 <see cref="GroupFor"/> 取，
/// **不在命令对象里写死** —— 换机型只改 json。
/// </para>
/// </summary>
public readonly record struct EcCommand
{
    public ushort Sub { get; }
    public EcDirection Direction { get; }
    public EcArgs Args { get; }

    /// <summary>
    /// 组号覆盖：**只给 `/api/ec/raw` 逆向调试用**（要能发档案以外的组号去试）。
    /// 正常路径一律 null，组号由档案决定。
    /// </summary>
    public ushort? GroupOverride { get; }

    public EcCommand(ushort sub, EcDirection direction, EcArgs args = default, ushort? groupOverride = null)
    {
        Sub = sub;
        Direction = direction;
        Args = args;
        GroupOverride = groupOverride;
    }

    public static EcCommand Read(ushort sub) => new(sub, EcDirection.Read);

    public static EcCommand Write(ushort sub, EcArgs args = default) => new(sub, EcDirection.Write, args);

    public bool IsWrite => Direction == EcDirection.Write;

    /// <summary>本次往返实际用的组号（档案的读组/写组，或 raw 显式指定的覆盖值）。</summary>
    public ushort GroupFor(EcSpec spec)
        => GroupOverride ?? (IsWrite ? spec.WriteGroup : spec.ReadGroup);

    /// <summary>日志/报错里的写法（`0x0300 write`）。</summary>
    public override string ToString()
        => $"{CmdTable.Hex(Sub)} {(IsWrite ? "write" : "read")}";
}
