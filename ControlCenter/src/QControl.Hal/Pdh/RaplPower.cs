using System.Text;
using System.Text.RegularExpressions;

namespace QControl.Hal.Pdh;

/// <summary>
/// CPU 封装功耗（W），来自 Windows 性能计数器 `\Energy Meter(*)\Power`。
///
/// 实测（2026-10-16，本机 i9-13900HX）：
/// <list type="bullet">
/// <item>实例有 `RAPL_Package0_DRAM / _PKG / _PP0 / _PP1 / _Total`；
///   `_PKG` = 封装（要的就是它）、`_PP0` = 计算核、`_DRAM` 本机恒 0。</item>
/// <item>单位 **mW**（实得 ~46000~54000，与 HWiNFO 的 CPU Package Power 同量级）。</item>
/// <item>⚠️ **要连续采集 3 次才有有效值**：第 1 次 `CStatus` 报错、第 2 次为 0、
///   第 3 次起才是真值。所以前两轮返回 null（期间上层退回 HWiNFO，界面不会空着）。</item>
/// <item>⚠️ 别的平台可能没有 `_PKG` ⇒ 枚举不到就整条路作废，
///   **不许拿 `_PP0`/`_Total` 冒充封装功耗** —— 那是另一个物理量。</item>
/// </list>
/// </summary>
public sealed class RaplPower : IDisposable
{
    private const string Wildcard = @"\Energy Meter(*)\Power";

    private readonly object _lock = new();
    private IntPtr _query;
    private IntPtr _counter;
    private int _collects;
    private double _retryAt;

    /// <summary>最近一次失败原因（诊断口用）。</summary>
    public string? Why { get; private set; }

    /// <summary>当前用的计数器路径（`null` = 还没打开成功）。</summary>
    public string? CounterPath { get; private set; }

    /// <summary>当前封装功耗（W）；读不到 / 还没攒够采样返回 null。</summary>
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

            if (++_collects < PdhNative.MinCollects) return null;

            var st = PdhNative.PdhGetFormattedCounterValue(
                _counter, PdhNative.FmtDouble | PdhNative.FmtNoCap100, out _, out var val);

            if (st != PdhNative.ErrorSuccess || val.CStatus != 0)
            {
                // 计数器暂时失效（睡眠唤醒 / 平台模式切换）⇒ 重建查询。
                // 宁可这一轮没数，也不拿无效值当真值。
                Reset($"取值无效 st=0x{st:X} cs=0x{val.CStatus:X}");
                return null;
            }

            var w = val.DoubleValue / 1000.0;      // 计数器单位是 mW
            if (!(w >= 0.5 && w < 500)) return null;   // 0 / 越界都当「没读到」
            return w;
        }
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static string InstanceOf(string path)
    {
        var m = Regex.Match(path, @"\(([^)]*)\)");
        return (m.Success ? m.Groups[1].Value : "").ToUpperInvariant();
    }

    private static List<string> Expand()
    {
        uint need = 0;
        PdhNative.PdhExpandWildCardPathW(null, Wildcard, null, ref need, 0);
        if (need == 0) return [];

        // MULTI_SZ：每项以 \0 结尾，整块以额外一个 \0 收尾。
        // ⚠️ 必须用 char[]（不能用 StringBuilder，见 PdhNative 的注释）
        var buf = new char[need];
        if (PdhNative.PdhExpandWildCardPathW(null, Wildcard, buf, ref need, 0) != 0)
            return [];

        var all = new string(buf, 0, (int)Math.Min(need, (uint)buf.Length));
        return all.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private bool Open()
    {
        var paths = Expand();
        var pkg = paths.Where(p =>
        {
            var inst = InstanceOf(p);
            return inst.EndsWith("_PKG", StringComparison.Ordinal) || inst == "PKG";
        }).ToList();

        // 只枚举到一个实例 ⇒ 认它（别的平台命名不同）
        if (pkg.Count == 0 && paths.Count == 1) pkg = paths;

        if (pkg.Count == 0)
        {
            Why = $"没枚举到 PKG 实例（共 {paths.Count} 个）";
            return false;
        }

        var st = PdhNative.PdhOpenQueryW(null, 0, out var hq);
        if (st != PdhNative.ErrorSuccess)
        {
            Why = $"PdhOpenQuery 失败 0x{st:X}";
            return false;
        }

        st = PdhNative.PdhAddEnglishCounterW(hq, pkg[0], 0, out var hc);
        if (st != PdhNative.ErrorSuccess)
        {
            Why = $"PdhAddEnglishCounter 失败 0x{st:X}";
            PdhNative.PdhCloseQuery(hq);
            return false;
        }

        _query = hq;
        _counter = hc;
        CounterPath = pkg[0];
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
        CounterPath = null;
        _collects = 0;
        Why = why;
        _retryAt = Now() + PdhNative.RetrySeconds;
    }

    public void Dispose()
    {
        lock (_lock) Reset("disposed");
    }
}
