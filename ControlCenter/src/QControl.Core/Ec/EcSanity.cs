namespace QControl.Core.Ec;

/// <summary>
/// EC 读数的**合理性判据**：什么算"读到了"，什么算"偏移错位读到的垃圾"。
///
/// <para>
/// 为什么需要它（2026-11-22）：未适配机型现在借 NL5 的 `layout` 去读温度与转速
/// （见 `quanta-gmwmi-generic`）。偏移对不上时，读回来往往不是 0，而是一个
/// "看着正常但毫无意义"的数 —— 程序没法识别那种。能自己识别的只有越界：
/// 温度 ≤0 / &gt;115 ℃、转速 &lt;0 / &gt;8000 rpm。越界的值一律当"没读到"，
/// 由 HWiNFO 兜底（见 `AppState.Readings`）。
/// </para>
/// <para>
/// ⚠️ 温度 0 与转速 0 是**两件事**：温度 0 只可能是没读到（运行中的机器不可能 0 ℃）；
/// 转速 0 是合法的（停转 / 待机 / 自动模式下的低速停转）。
/// </para>
/// </summary>
public static class EcSanity
{
    /// <summary>可信温度上限。超过这个数只可能是偏移读错（或传感器故障）。</summary>
    public const double MaxTempC = 115;

    /// <summary>可信转速上限。广达机型的风扇满转约 5000~6000，8000 留足余量。</summary>
    public const double MaxRpm = 8000;

    /// <summary>温度是否可信：0 &lt; t ≤ 115 ℃。</summary>
    public static bool Temp(double? c) => c is { } v && v > 0 && v <= MaxTempC;

    /// <summary>转速是否可信：0 ≤ rpm ≤ 8000（**0 合法**）。</summary>
    public static bool Rpm(double? rpm) => rpm is { } v && v >= 0 && v <= MaxRpm;
}
