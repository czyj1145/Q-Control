using System.Runtime.InteropServices;
using System.Text;

namespace QControl.Hal.Pdh;

/// <summary>
/// `pdh.dll` 的最小 interop —— 只够读 `\Energy Meter(*)\Power`。
/// </summary>
internal static class PdhNative
{
    internal const uint FmtDouble = 0x00000200;
    /// <summary>电源类数值不该被 PDH 按 100 截顶。</summary>
    internal const uint FmtNoCap100 = 0x00008000;

    /// <summary>失败重试的退避（秒）：枚举不到别每秒重试一次。</summary>
    internal const double RetrySeconds = 60.0;

    /// <summary>实测：前两次采集拿不到有效差分（第 1 次 CStatus 报错、第 2 次为 0）。</summary>
    internal const int MinCollects = 3;

    /// <summary>
    /// 展开通配符计数器路径，结果是 **MULTI_SZ**（多个 \0 结尾的字符串）。
    ///
    /// ⚠️ 第三个参数**不能**用 <c>StringBuilder</c>：P/Invoke 的 StringBuilder 封送
    /// 在调用返回后按**第一个 \0** 截断，MULTI_SZ 会被压成一条 —— 于是
    /// "只枚举到一个实例"的兜底逻辑会把 `RAPL_Package0_DRAM` 当成封装功耗。
    /// （2026-10-18 实测踩到：CPU 功耗读到的是 DRAM 的计数器。）
    /// 用 <c>char[]</c> 才拿得到完整 MULTI_SZ。
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhExpandWildCardPathW(
        string? machineName, string wildCardPath, [Out] char[]? buffer,
        ref uint bufferSize, uint flags);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath,
                                                      nuint userData, out IntPtr counter);

    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern uint PdhGetFormattedCounterValue(
        IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    /// <summary>
    /// 通配符计数器（`\GPU Engine(*)\…`）的**整组**取值：一次拿回所有实例的名字 + 值。
    /// 第一次传 `itemBuffer = NULL` 会返回 <see cref="ErrorMoreData"/> 并把需要的字节数写进
    /// <c>bufferSize</c>；照这个大小分配再调一次才拿得到数据。
    /// </summary>
    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", SetLastError = true)]
    internal static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>
    /// `PDH_FMT_COUNTERVALUE`。只用得上 `doubleValue` 那一支 union ——
    /// 显式布局把它放在 offset 8（4 字节 CStatus + 4 字节对齐），与 C 侧一致。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    /// <summary>
    /// `PDH_FMT_COUNTERVALUE_ITEM_W`：一个实例名 + 一个值。
    /// 布局必须与 C 侧一致（x64 下 8 + 16 = 24 字节，union 里的 LONGLONG 决定 8 字节对齐）。
    /// ⚠️ `SzName` 是 PDH 自己那块缓冲里的指针，**只在本次调用后有效** ——
    ///   要留就 `Marshal.PtrToStringUni` 拷出来，别保存指针。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PdhFmtCounterValueItemW
    {
        public IntPtr SzName;
        public PdhFmtCounterValue Value;
    }

    internal const uint ErrorSuccess = 0;

    /// <summary>缓冲区不够：先探大小再取值时必然拿到这个。</summary>
    internal const uint ErrorMoreData = 0x800007D2;
}
