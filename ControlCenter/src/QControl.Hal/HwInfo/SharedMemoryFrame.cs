using System.Runtime.InteropServices;
using System.Text;

namespace QControl.Hal.HwInfo;

/// <summary>一条读数。</summary>
/// <remarks>
/// `LabelUpper` / `UnitUpper` 是**按需算一次就记住**的大写副本（2026-11-02 加）。
///
/// 为什么：`HwInfoReader` 每帧要跑 8 个 `Find*`，每个都把**每条读数**的 label/unit
/// `ToUpperInvariant()` 一遍。本机几百条读数 ⇒ 每帧上千个字符串垃圾（实测每拍
/// 200~400 KB，占整套快照分配量的一大块）。改成算一次、8 个 Find 共用。
/// ⚠️ 用 `??=` 而不是 `Lazy<>`：帧可能被多个线程共读，最坏是算两遍、其中一个赢，
///    结果一样；`Lazy` 会多两个对象和一把锁，不值。
/// ⚠️ 缓存的**只是大写**，逐字语义与原来 `ToUpperInvariant()` 完全一致（不是改成
///    `OrdinalIgnoreCase` —— 那是另一种比较，改法不同、风险不同，别混）。
/// </remarks>
public sealed record HwReading(int Id, int SensorIndex, int Type, string Label, string Unit, double Value)
{
    private string? _labelUpper;
    private string? _unitUpper;
    private string? _labelUpperTrim;

    /// <summary>`(Label ?? "").ToUpperInvariant()` 的缓存。</summary>
    public string LabelUpper => _labelUpper ??= (Label ?? "").ToUpperInvariant();

    /// <summary>`(Label ?? "").Trim().ToUpperInvariant()` 的缓存（SMART 那张表按去空格后匹配）。</summary>
    public string LabelUpperTrim => _labelUpperTrim ??= (Label ?? "").Trim().ToUpperInvariant();

    /// <summary>`(Unit ?? "").Trim().ToUpperInvariant()` 的缓存（与 <c>UnitOf(r).ToUpperInvariant()</c> 同义）。</summary>
    public string UnitUpper => _unitUpper ??= (Unit ?? "").Trim().ToUpperInvariant();
}

/// <summary>一个传感器组。</summary>
public sealed record HwSensor(int Id, int Inst, string Name)
{
    private string? _nameUpper;

    /// <summary>`(Name ?? "").ToUpperInvariant()` 的缓存（每个 Find* 都要按组名筛，别每帧算 8 遍）。</summary>
    public string NameUpper => _nameUpper ??= (Name ?? "").ToUpperInvariant();
}

/// <summary>解析出来的一帧共享内存数据。</summary>
public sealed class HwFrame
{
    public required IReadOnlyList<HwSensor> Sensors { get; init; }
    public required IReadOnlyList<HwReading> Readings { get; init; }
    public required string Layout { get; init; }
    public required int SensorElemSize { get; init; }
    public required int ReadingElemSize { get; init; }
    public required int Version { get; init; }
    public required int Revision { get; init; }
    /// <summary>HWiNFO 每次轮询写下的时间戳（Unix 秒）。判"段是不是残留"全靠它。</summary>
    public required long PollTime { get; init; }

    /// <summary>组名（按**下标**查，`dwSensorIndex` 是下标不是 ID）。</summary>
    public string SensorName(int index)
        => index >= 0 && index < Sensors.Count ? Sensors[index].Name : "?";

    /// <summary>组名的大写副本（缓存，见 <see cref="HwSensor.NameUpper"/>）。</summary>
    public string SensorUpper(int index)
        => index >= 0 && index < Sensors.Count ? Sensors[index].NameUpper : "?";

    /// <summary>按组名 + 读数遍历（`_iter_rows` 的等价物）。</summary>
    public IEnumerable<(string Group, HwReading Reading)> Rows()
    {
        foreach (var r in Readings)
            yield return (SensorName(r.SensorIndex), r);
    }
}

/// <summary>
/// HWiNFO 共享内存的读取：打开映射 / 解析布局 / 判数据新鲜度。
///
/// **三个必须守住的事实**：
/// <list type="number">
/// <item>段名是 `Global\HWiNFO_SENS_SM2`（v8.x 实测）。SDK 文档上那个
///   `HWiNFO_SENSORS_SHARED_MEM2` 在 v8.x 里**根本不存在**，只按文档名找会一直
///   FILE_NOT_FOUND，误判成"SM 没创建"。</item>
/// <item>段头 `poll_time` 在 **+12**（`pack(4)`，8 字节但只按 4 对齐）。用默认对齐
///   会把它推到 +16，后面所有 DWORD 全错位 4 字节。</item>
/// <item>元素尺寸 v8.x 是 **sensor 392 / reading 460**（比 SDK 多一份 UTF-8 名字）。
///   尺寸对不上就按"reading 区紧接 sensor 区"这个自洽性挑布局。</item>
/// </list>
///
/// ⚠️ **HWiNFO 退出后段会残留，数据依然结构完整、能解析**（就是它退出前最后一次写的值）。
/// 只判"解析得出来"会把冻结的假数据端给界面 —— 显示一个永远不动的温度，
/// 比什么都不显示更糟。所以每次都要判 `poll_time` 的年龄。
/// </summary>
public static class SharedMemoryFrame
{
    private const uint FileMapRead = 0x0004;
    private const uint Sig = 0x53695748;      // "HWiS"
    private static readonly string[] ShmNames =
    [
        @"Global\HWiNFO_SENS_SM2",              // v8.x 实测
        @"Global\HWiNFO_SENSORS_SHARED_MEM2",   // SDK 文档名，兜底
    ];

    /// <summary>数据新鲜度上限（秒）。HWiNFO 默认 2 秒轮询，45 秒足够宽松。</summary>
    public const double FreshSeconds = 45.0;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMapping, uint desiredAccess,
                                               uint offsetHigh, uint offsetLow, UIntPtr bytesToMap);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr baseAddress);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public IntPtr State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern UIntPtr VirtualQuery(IntPtr address,
                                               out MemoryBasicInformation info, UIntPtr length);

    /// <summary>
    /// 这个映射视图有多大（字节）。拿不到返回 0。
    ///
    /// ⚠️ 必须有它：`Parse` 里 `offS / nS / szS / offR / nR / szR` 全部来自**段头那几个
    /// DWORD**，是"对面写进来的数据"。`MapViewOfFile(..., UIntPtr.Zero)` 只映射段本身的
    /// 大小，一旦这几个数被写歪（HWiNFO 正在重写、同名段被别的程序建、残留段的旧布局），
    /// `IntPtr.Add(p, offR + i * strideR)` 就跑到映射外面 —— 那是**访问违例**，
    /// .NET 里直接结束进程（不是异常、catch 不住），表现是"开着控制中心突然没了"。
    /// </summary>
    private static long MappedSize(IntPtr p)
    {
        try
        {
            var n = (UIntPtr)System.Runtime.InteropServices.Marshal.SizeOf<MemoryBasicInformation>();
            if (VirtualQuery(p, out var mbi, n) == UIntPtr.Zero) return 0;
            return (long)mbi.RegionSize.ToUInt64();
        }
        catch { return 0; }
    }

    /// <summary>段存在吗（**不代表数据可用** —— 残留段也会为 true）。</summary>
    public static bool SegmentExists()
    {
        foreach (var n in ShmNames)
        {
            var h = OpenFileMappingW(FileMapRead, false, n);
            if (h == IntPtr.Zero) continue;
            CloseHandle(h);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 解析统计（只为回答一个问题：**解析一帧到底分配了多少**）。
    ///
    /// 2026-11-02 加：那天本来想用"段头 `poll_time` 没变就复用上一帧"来省掉一半解析，
    /// 结果**测不出收益**（535 → 530 KB/s，噪声量级），却要背"内存里常驻一帧 +
    /// 依赖 HWiNFO 一定改时间戳"两笔账 —— 于是撤掉了。撤掉之后用这两个计数把
    /// 解析的开销量到字节：`parse_alloc_mb` 的增量 ÷ 秒数 = 解析对分配速率的贡献。
    /// 只有真确认它是热点，才值得去做"按需只解析需要的那几条读数"（那是一个大改动）。
    /// ⚠️ 计数只在 `/api/debug/mem` 里露面，不参与任何判断。
    /// </summary>
    private static long _parseCalls;
    private static long _parseAllocBytes;

    public static long ParseCalls => Interlocked.Read(ref _parseCalls);
    public static double ParseAllocMb => Interlocked.Read(ref _parseAllocBytes) / 1048576.0;

    /// <summary>
    /// 读一帧。
    /// <paramref name="requireFresh"/> = true（默认）时，残留的冻结段返回 null。
    /// 只有诊断口才传 false，好让人看见段里到底残留了什么。
    /// </summary>
    public static HwFrame? Read(bool requireFresh = true)
    {
        foreach (var name in ShmNames)
        {
            var h = OpenFileMappingW(FileMapRead, false, name);
            if (h == IntPtr.Zero) continue;
            try
            {
                var p = MapViewOfFile(h, FileMapRead, 0, 0, UIntPtr.Zero);
                if (p == IntPtr.Zero) continue;
                try
                {
                    var allocBefore = GC.GetTotalAllocatedBytes();
                    var frame = Parse(p);
                    Interlocked.Increment(ref _parseCalls);
                    Interlocked.Add(ref _parseAllocBytes, GC.GetTotalAllocatedBytes() - allocBefore);

                    if (frame is null) continue;
                    // 不新鲜就**试下一个段名**，别直接 return：第一个段名常常是上一次会话
                    // 留下的残留段（能解析、但 poll_time 停在几小时前），真正的数据在第二个
                    // 段名里。直接返回 null 等于让兜底数据源整个失效（2026-11-23 审查）。
                    if (requireFresh && !IsFresh(frame)) continue;
                    return frame;
                }
                finally
                {
                    UnmapViewOfFile(p);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }
        return null;
    }

    /// <summary>`poll_time` 距现在是否不超过 <see cref="FreshSeconds"/>。`poll &lt;= 0` 也算不新鲜。</summary>
    public static bool IsFresh(HwFrame frame, double? nowUnix = null)
    {
        if (frame.PollTime <= 0) return false;
        var now = nowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return now - frame.PollTime <= FreshSeconds;
    }

    /// <summary>
    /// **只读段头**判"现在有没有新鲜数据"，不解析整帧（2026-11-03 加）。
    ///
    /// 为什么需要它：看门狗每 2 秒问一次"共享内存还在出数吗"，答案只需要段头里那四个字段，
    /// 而 <see cref="Read"/> 会把几百条读数的 label/unit 全解成字符串 —— 实测
    /// **194 KB／次、0.5 次/秒**，占托盘态全部分配量的一半以上（`parse_alloc_mb` 量到的）。
    /// 判据与 `Read(requireFresh: true) is not null` 等价：签名对、条数在合理区间、
    /// `poll_time` 不过期 —— 布局错位是**机器固有的**，不会时好时坏，不需要每次重验。
    /// </summary>
    public static bool ProbeFresh()
    {
        foreach (var name in ShmNames)
        {
            var h = OpenFileMappingW(FileMapRead, false, name);
            if (h == IntPtr.Zero) continue;
            try
            {
                var p = MapViewOfFile(h, FileMapRead, 0, 0, UIntPtr.Zero);
                if (p == IntPtr.Zero) continue;
                try
                {
                    if ((uint)Marshal.ReadInt32(p, 0) != Sig) continue;

                    var poll = Marshal.ReadInt64(p, 12);
                    var nS = Marshal.ReadInt32(p, 28);
                    var nR = Marshal.ReadInt32(p, 40);
                    if (nS <= 0 || nS >= 512 || nR <= 0 || nR >= 8192) continue;
                    if (poll <= 0) continue;

                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    if (now - poll > FreshSeconds) continue;      // 残留段（HWiNFO 已退出）
                    return true;
                }
                finally
                {
                    UnmapViewOfFile(p);
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }
        return false;
    }

    /// <summary>段的年龄（秒）；`poll &lt;= 0` 时返回 -1。</summary>
    public static double AgeSeconds(HwFrame frame)
    {
        if (frame.PollTime <= 0) return -1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return now - frame.PollTime;
    }

    // ================================================================ 解析

    private enum Layout { V8, Packed, Native }

    private static (int S, int R) SizeOf(Layout l) => l switch
    {
        Layout.V8 => (392, 460),
        Layout.Packed => (264, 316),
        _ => (264, 316),      // Native 与 Packed 字段相同、只是对齐不同
    };

    private static HwFrame? Parse(IntPtr p)
    {
        // 段头（pack=4）：签名/版本/修订/poll_time(8B@+12)/六个 DWORD
        if ((uint)Marshal.ReadInt32(p, 0) != Sig) return null;

        var version = Marshal.ReadInt32(p, 4);
        var revision = Marshal.ReadInt32(p, 8);
        var poll = Marshal.ReadInt64(p, 12);

        var offS = Marshal.ReadInt32(p, 20);
        var szS = Marshal.ReadInt32(p, 24);
        var nS = Marshal.ReadInt32(p, 28);
        var offR = Marshal.ReadInt32(p, 32);
        var szR = Marshal.ReadInt32(p, 36);
        var nR = Marshal.ReadInt32(p, 40);

        if (nS <= 0 || nS >= 512 || nR <= 0 || nR >= 8192) return null;

        // 映射视图的真实大小：后面每一个 `IntPtr.Add(p, off + i*stride)` 都要靠它兜底
        var mapSize = MappedSize(p);

        // 按声明尺寸挑布局；对不上就把三套都试一遍，
        // 最强判据是**结构自洽**（reading 区紧接 sensor 区），自洽的排前面。
        var picks = new List<Layout>();
        if ((szS, szR) == SizeOf(Layout.V8)) picks.Add(Layout.V8);
        else if ((szS, szR) == SizeOf(Layout.Packed)) picks.Add(Layout.Packed);
        else
        {
            picks.AddRange([Layout.V8, Layout.Packed, Layout.Native]);
            picks.Sort((a, b) =>
            {
                var (sa, _) = SizeOf(a);
                var (sb, _) = SizeOf(b);
                var selfA = offS + nS * sa == offR ? 0 : 1;
                var selfB = offS + nS * sb == offR ? 0 : 1;
                return selfA.CompareTo(selfB);
            });
        }

        foreach (var layout in picks)
        {
            var (strideS, strideR) = SizeOf(layout);

            // 段头给的偏移/条数必须落在**真实映射**之内（见 MappedSize 的注释）。
            // 留 300 字节余量：reading 元素内部还要读到 +284 的 double。
            if (mapSize <= 0) return null;
            if (offS < 0 || offR < 0) continue;
            if (offS + (long)nS * strideS > mapSize) continue;
            if (offR + (long)nR * strideR + 300 > mapSize) continue;

            var sensors = new List<HwSensor>(nS);
            for (var i = 0; i < nS; i++)
            {
                var at = IntPtr.Add(p, offS + i * strideS);
                var id = Marshal.ReadInt32(at, 0);
                var inst = Marshal.ReadInt32(at, 4);
                var name = ReadCString(at, 8, 128);
                if (name.Length == 0) name = ReadCString(at, 136, 128);
                if (name.Length == 0 && layout == Layout.V8) name = ReadCString(at, 264, 128);
                sensors.Add(new HwSensor(id, inst, name));
            }
            // 组名大部分读不出来 ⇒ 布局错位，换下一个
            if (!MostlyPrintable(sensors.Select(s => s.Name))) continue;

            var readings = new List<HwReading>(nR);
            for (var i = 0; i < nR; i++)
            {
                var at = IntPtr.Add(p, offR + i * strideR);
                var u0 = Marshal.ReadInt32(at, 0);
                var u1 = Marshal.ReadInt32(at, 4);
                var u2 = Marshal.ReadInt32(at, 8);

                // ⚠️ 两套布局**字段顺序不同**：SDK 是 ID/SensorIndex/Type，
                //    v8.x 实测是 Type/SensorIndex/ID。前 12 字节之外完全一致。
                var (type, sidx, id) = layout == Layout.V8 ? (u0, u1, u2) : (u2, u1, u0);

                var label = ReadCString(at, 12, 128);
                if (label.Length == 0) label = ReadCString(at, 140, 128);
                if (label.Length == 0 && layout == Layout.V8) label = ReadCString(at, 316, 128);

                var unit = ReadCString(at, 268, 16);
                var value = Marshal.PtrToStructure<double>(IntPtr.Add(at, 284));

                readings.Add(new HwReading(id, sidx, type, label, unit, value));
            }
            if (readings.Count > 0 && !MostlyPrintable(readings.Select(r => r.Label))) continue;

            return new HwFrame
            {
                Sensors = sensors,
                Readings = readings,
                Layout = layout switch
                {
                    Layout.V8 => "SensorV8/ReadingV8",
                    Layout.Packed => "SensorP/ReadingP",
                    _ => "SensorN/ReadingN",
                },
                SensorElemSize = szS,
                ReadingElemSize = szR,
                Version = version,
                Revision = revision,
                PollTime = poll,
            };
        }
        return null;
    }

    /// <summary>读固定长度区里的 C 字符串。HWiNFO 存的是 latin1（与早先实现 `_dec` 一致）。</summary>
    private static string ReadCString(IntPtr baseAddr, int offset, int maxLen)
    {
        var start = IntPtr.Add(baseAddr, offset);
        var len = 0;
        while (len < maxLen && Marshal.ReadByte(start, len) != 0) len++;
        if (len == 0) return "";
        var buf = new byte[len];
        Marshal.Copy(start, buf, 0, len);
        return Encoding.Latin1.GetString(buf).Trim();
    }

    /// <summary>
    /// 大部分字符串可读吗。错位时第 0 条常常仍可读，所以用**比例**而不是 any/all：
    /// any 会放行错误布局；all 会被一条中文 label 误杀。
    /// </summary>
    private static bool MostlyPrintable(IEnumerable<string> rows, double ratio = 0.9)
    {
        var total = 0;
        var printable = 0;
        foreach (var s in rows)
        {
            total++;
            if (s.Length > 0 && s.Any(c => c >= 0x20 && c < 0x7F)) printable++;
        }
        return total == 0 || (double)printable / total >= ratio;
    }
}
