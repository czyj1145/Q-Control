using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace QControl.Hal.Pdh;

/// <summary>
/// GPU 引擎占用率（%），来自 Windows 性能计数器 `\GPU Engine(*)\Utilization Percentage`。
///
/// 只用于**独显读数拿不到时的回退**（切集显输出后系统里只剩核显那块适配器）：
/// 独显正常时一秒一次 NVML 就够，没必要常挂一组 PDH 查询。
///
/// 口径（与任务管理器一致）：同一 **engtype** 的实例按进程求和，再取各引擎里的最大值。
/// 例：3D 引擎上三个进程各 20% ⇒ 3D = 60%；同时 Copy = 5% ⇒ 取 60。
/// ⚠️ 别改成"全部实例求和"：那样开两个小游戏就能显示 120%。
///
/// ⚠️ 实例名形如 `pid_1234_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D`。
///    这里**不按 LUID 过滤** —— 走到这条路的前提就是 NVIDIA 已被固件隐藏，
///    系统里只剩一块显示适配器；要按 LUID 过滤得先拿 DXGI 的 LUID，代价远大于收益
///    （混合模式下 NVIDIA 正常，根本走不到这里）。
/// </summary>
public sealed class GpuEngineUsage : IDisposable
{
    private const string Wildcard = @"\GPU Engine(*)\Utilization Percentage";

    /// <summary>速率计数器第一次采集没有参照，至少要两拍。</summary>
    private const int MinCollects = 2;

    private static readonly Regex EngType = new(@"_engtype_([A-Za-z0-9]+)$", RegexOptions.Compiled);

    private readonly object _lock = new();
    private IntPtr _query;
    private IntPtr _counter;
    private int _collects;
    private double _retryAt;

    /// <summary>最近一次失败原因（诊断口用）。读得到值时是 null。</summary>
    public string? Why { get; private set; }

    /// <summary>最高引擎的占用率（0~100）；没有实例 / 计数器打不开 / 还没攒够采样 ⇒ null。</summary>
    public double? Value()
    {
        lock (_lock)
        {
            var now = Now();
            if (_query == IntPtr.Zero)
            {
                if (now < _retryAt) return null;
                if (!Open())
                {
                    _retryAt = now + PdhNative.RetrySeconds;
                    return null;
                }
            }

            if (PdhNative.PdhCollectQueryData(_query) != PdhNative.ErrorSuccess)
            {
                Reset("PdhCollectQueryData 失败");
                return null;
            }

            if (++_collects < MinCollects) return null;

            return ReadArray();
        }
    }

    /// <summary>取整组实例，按 engtype 聚合后取最大值。</summary>
    private double? ReadArray()
    {
        const uint fmt = PdhNative.FmtDouble | PdhNative.FmtNoCap100;   // 百分比本就会被 100 截顶，这里统一不截
        uint size = 0, count = 0;

        var st = PdhNative.PdhGetFormattedCounterArrayW(_counter, fmt, ref size, ref count, IntPtr.Zero);
        if (st != PdhNative.ErrorMoreData || size == 0)
        {
            // 实例一个都没有（驱动刚切换 / 计数器未注册）不是错误，只是这一拍没数
            if (st != PdhNative.ErrorSuccess) Why = $"取数组失败 0x{st:X}";
            return null;
        }

        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            st = PdhNative.PdhGetFormattedCounterArrayW(_counter, fmt, ref size, ref count, buf);
            if (st != PdhNative.ErrorSuccess)
            {
                Reset($"取数组失败 0x{st:X}");
                return null;
            }

            var stride = Marshal.SizeOf<PdhNative.PdhFmtCounterValueItemW>();
            var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhNative.PdhFmtCounterValueItemW>(
                    IntPtr.Add(buf, i * stride));

                // 单个实例失效（进程刚退出）只丢这一条，别让整组作废
                if (item.Value.CStatus != 0) continue;

                var name = Marshal.PtrToStringUni(item.SzName) ?? "";
                var m = EngType.Match(name);
                if (!m.Success) continue;

                var kind = m.Groups[1].Value;
                perEngine[kind] = perEngine.GetValueOrDefault(kind) + item.Value.DoubleValue;
            }

            if (perEngine.Count == 0) return null;

            var max = 0.0;
            foreach (var v in perEngine.Values) if (v > max) max = v;
            return Math.Clamp(max, 0.0, 100.0);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private bool Open()
    {
        var st = PdhNative.PdhOpenQueryW(null, 0, out var hq);
        if (st != PdhNative.ErrorSuccess)
        {
            Why = $"PdhOpenQuery 失败 0x{st:X}";
            return false;
        }

        // 通配符路径直接加：PDH 允许，取值时用 PdhGetFormattedCounterArrayW 拿全组
        st = PdhNative.PdhAddEnglishCounterW(hq, Wildcard, 0, out var hc);
        if (st != PdhNative.ErrorSuccess)
        {
            Why = $"PdhAddEnglishCounter 失败 0x{st:X}";
            PdhNative.PdhCloseQuery(hq);
            return false;
        }

        _query = hq;
        _counter = hc;
        _collects = 0;
        Why = null;
        return true;
    }

    private void Reset(string why)
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhNative.PdhCloseQuery(_query); } catch { }
        }
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
        _collects = 0;
        Why = why;
        _retryAt = Now() + PdhNative.RetrySeconds;
    }

    public void Dispose()
    {
        lock (_lock) Reset("disposed");
    }
}
