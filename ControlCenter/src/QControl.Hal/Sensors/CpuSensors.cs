using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using QControl.Hal.Pdh;
using Microsoft.Win32;

namespace QControl.Hal.Sensors;

/// <summary>
/// CPU 读数：型号 / 占用率 / 频率 / 核数 / 封装功耗。
///
/// CPU 读数：占用率 / 频率 / 核心线程数 / 功耗。**CPU 电压不在本类** ——
/// Windows 没有这个接口、EC 也没有这个字段，唯一来源是 HWiNFO（P5）。
/// </summary>
public sealed class CpuSensors : IDisposable
{
    private readonly RaplPower _rapl = new();
    private readonly object _lock = new();

    private readonly string _name;
    private readonly int _cores;
    private readonly int _threads;

    private long _prevIdle;
    private long _prevKernel;
    private long _prevUser;
    private bool _primed;

    public CpuSensors()
    {
        _name = DetectName();
        (_cores, _threads) = DetectCounts();
    }

    /// <summary>当前 RAPL 计数器路径（诊断用）。</summary>
    public string? PowerSource => _rapl.CounterPath;
    public string? PowerWhy => _rapl.Why;

    public JsonObject Read(double? voltageFromHwInfo = null)
    {
        var d = new JsonObject
        {
            ["name"] = _name,
            // 读不到就是 **null**，不是 0：`0 MHz / 0%` 与"CPU 真的闲着"分不开，
            // 而历史曲线会把 0 当成真值画成一条平线（2026-11-23 审查）。
            ["usage"] = Usage() is { } u ? Math.Round(u, 1) : null,
            ["freq_mhz"] = FreqMhz() is { } f ? Math.Round((double)f, 0) : null,
            ["cores"] = _cores,
            ["threads"] = _threads,
            ["power"] = null,
            ["voltage"] = null,
        };

        // 功耗：系统原生优先（PDH 读平台 RAPL 能量计量器）。
        // PDH 前两轮采不出数，这几轮正好由上层用 HWiNFO 顶上，界面不会空着。
        if (_rapl.Value() is { } p) d["power"] = Math.Round(p, 1);

        // 电压只有 HWiNFO 有（P5 接进来时传进来）
        if (voltageFromHwInfo is { } v) d["voltage"] = Math.Round(v, 3);

        return d;
    }

    // ---------------------------------------------------------------- 占用率

    /// <summary>
    /// 与 `psutil.cpu_percent(interval=None)` 同语义：**基于两次调用的差值**。
    /// 第一次（或读失败）**没有百分比可言 ⇒ 返回 null** —— 以前回 0.0，界面就显示"0%"，
    /// 与"CPU 真的闲着"分不开（2026-11-23 审查）。调用方每秒调一次即可。
    /// </summary>
    private double? Usage()
    {
        lock (_lock)
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;

            if (!_primed)
            {
                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;
                _primed = true;
                // 第一拍没有参照，**没有百分比可言** —— 以前回 0.0，界面上就是"0%"。
                return null;
            }

            var dIdle = idle - _prevIdle;
            var dKernel = kernel - _prevKernel;
            var dUser = user - _prevUser;
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;

            // kernel 时间**已经包含** idle 时间，所以总时间 = kernel + user
            var total = dKernel + dUser;
            if (total <= 0) return null;
            var busy = total - dIdle;
            var pct = 100.0 * busy / total;
            return Math.Clamp(pct, 0.0, 100.0);
        }
    }

    // ---------------------------------------------------------------- 频率

    /// <summary>
    /// 当前主频（MHz）—— 取**第一个逻辑核**的 `CurrentMhz`。
    ///
    /// ⚠️ 这个口径是实测比出来的，不要"想当然改成平均值"：
    ///   2026-10-19 对账发现 C# 2200 / psutil 1466，第一反应是"应该取平均"，
    ///   于是改成 32 核平均 —— 结果更错（平均 2062，psutil 还是 1466）。
    ///   用负载实验同时打三种取法，结论明确：
    /// <code>
    ///   psutil  first_core   avg
    ///    1466      1466      2062
    ///    2200      1466      2039
    /// </code>
    ///   ⇒ **psutil 在 Windows 上返回的就是第一个核的值**（不是平均）。
    ///
    /// ⚠️ 它本身抖动很大：同一台机器在 1466 与 2200 两档之间来回跳，
    ///   两个进程不可能同时刻读到同一个值。所以对账容差必须放到 800 以上，
    ///   否则每次都会误报"不一致"。这个字段的对账价值有限，别拿它当判据。
    /// </summary>
    private uint? FreqMhz()
    {
        try
        {
            var count = Environment.ProcessorCount;
            var one = Marshal.SizeOf<ProcessorPowerInformation>();
            var buf = Marshal.AllocHGlobal(one * count);
            try
            {
                var st = CallNtPowerInformation(11 /* ProcessorInformation */,
                    IntPtr.Zero, 0, buf, (uint)(one * count));
                if (st != 0) return null;

                // 只要第一个核 —— 与 psutil 一致（见上面的实测表）
                var first = Marshal.PtrToStructure<ProcessorPowerInformation>(buf);
                return first.CurrentMhz;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 静态信息

    private static string DetectName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown CPU";
        }
        catch
        {
            return "Unknown CPU";
        }
    }

    /// <summary>物理核 / 逻辑线程。WMI 一次查询约 20~40 ms，构造时取一次就够了。</summary>
    private static (int Cores, int Threads) DetectCounts()
    {
        var threads = Environment.ProcessorCount;
        var cores = threads;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            var c = 0;
            var t = 0;
            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
            {
                c += Convert.ToInt32(mo["NumberOfCores"] ?? 0);
                t += Convert.ToInt32(mo["NumberOfLogicalProcessors"] ?? 0);
                mo.Dispose();
            }
            if (c > 0) cores = c;
            if (t > 0) threads = t;
        }
        catch
        {
            // WMI 不可用时退回 Environment.ProcessorCount（至少线程数是对的）
        }
        return (cores, threads);
    }

    public void Dispose() => _rapl.Dispose();

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel, IntPtr inputBuffer, uint inputBufferSize,
        IntPtr outputBuffer, uint outputBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
