using System.Text.RegularExpressions;

namespace QControl.Hal.Sys;

/// <summary>这块显卡算核显、独显，还是判不出来。</summary>
public enum GpuKind
{
    /// <summary>信息不足（例如只拿到 AMD 的厂商 ID 又没有可识别的型号）。**不要猜**。</summary>
    Unknown,
    Integrated,
    Discrete,
}

/// <summary>
/// 核显 / 独显的**唯一判定口**（2026-11-12 建）。
///
/// 为什么要有它：显卡归属被两处各判一遍 —— `DisplaySensors`（显卡模式页）与
/// `HardwareProfile`（硬件档案）—— 而两处都拿 PCI 厂商 ID 当判据。厂商 ID 只能分
/// NVIDIA / Intel / AMD，**分不开 AMD 的核显和独显**：
/// 于是 `VEN_1002` 在 `DisplaySensors` 里被当成核显（AMD 独显机型上"独显直连"永远
/// 显示成混合输出），在 `HardwareProfile` 里又被当成独显（AMD 核显被标成"独立显卡"）。
///
/// 判据顺序：
/// <list type="number">
/// <item>Intel：`VEN_8086` —— 默认核显；名字带 ARC 的是 Intel 独显（Arc A770 也是 8086）；</item>
/// <item>NVIDIA：`VEN_10DE` —— 只有独显；</item>
/// <item>AMD：`VEN_1002` / `VEN_1022` —— 只能看型号名（`RX/Pro/FirePro/VII` = 独显，
///   `Vega` / `…Graphics` / `780M` 这类三位数+M = 核显），判不出来就返回
///   <see cref="GpuKind.Unknown"/>，让调用方别把它算进任何一边。</item>
/// </list>
/// </summary>
public static class GpuClassification
{
    /// <summary>AMD 移动核显的命名：`Radeon 780M` / `760M` / `680M`…（前面不能是数字，
    /// 否则 `RX 7600M` 里的 `600M` 会被误命中）。</summary>
    private static readonly Regex AmdMobileIgpu = new(@"(?<![\d.])\d{3}M\b", RegexOptions.Compiled);

    /// <summary>
    /// 按 PCI 厂商 ID + 型号名判定。
    /// <paramref name="vendorId"/> 允许空串（拿不到 PNPDeviceID 时），此时退回名字关键字。
    /// </summary>
    public static GpuKind Of(string? vendorId, string? name)
    {
        var ven = (vendorId ?? "").Trim().ToUpperInvariant();
        var n = (name ?? "").ToUpperInvariant();

        if (ven == "8086" || (ven.Length == 0 && n.Contains("INTEL", StringComparison.Ordinal)))
            return n.Contains("ARC", StringComparison.Ordinal) ? GpuKind.Discrete : GpuKind.Integrated;

        if (ven == "10DE"
            || (ven.Length == 0
                && (n.Contains("NVIDIA", StringComparison.Ordinal)
                    || n.Contains("GEFORCE", StringComparison.Ordinal))))
            return GpuKind.Discrete;

        if (ven is "1002" or "1022" || n.Contains("RADEON", StringComparison.Ordinal)
            || n.Contains("AMD", StringComparison.Ordinal))
            return Amd(n);

        return GpuKind.Unknown;
    }

    /// <summary>
    /// AMD 的核显 / 独显只能靠型号名分。**分不出来就 Unknown** —— 这是刻意的：
    /// 判错比不判更糟（界面会把核显写成"独立显卡"，显卡模式页会把独显写成混合输出）。
    /// </summary>
    private static GpuKind Amd(string n)
    {
        // 独显：RX / Radeon Pro / FirePro / Radeon VII（核显的命名里不会出现这几个词）
        if (n.Contains("RX ", StringComparison.Ordinal)
            || n.Contains("RADEON RX", StringComparison.Ordinal)
            || n.Contains("RADEON PRO", StringComparison.Ordinal)
            || n.Contains("FIREPRO", StringComparison.Ordinal)
            || n.Contains("RADEON VII", StringComparison.Ordinal))
            return GpuKind.Discrete;

        // 核显：Vega、不带型号的 "Radeon Graphics"、以及 780M / 760M 这种移动核显
        if (n.Contains("VEGA", StringComparison.Ordinal)
            || n.Contains("GRAPHICS", StringComparison.Ordinal)
            || AmdMobileIgpu.IsMatch(n))
            return GpuKind.Integrated;

        return GpuKind.Unknown;
    }
}
