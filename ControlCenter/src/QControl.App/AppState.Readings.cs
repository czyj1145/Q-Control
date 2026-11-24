using System.Text.Json.Nodes;
using QControl.Core.Ec;

namespace QControl.App;

/// <summary>
/// AppState 分片：**EC 读不到时的读数兜底**。
///
/// 背景（2026-11-02 用户定）：别的广达机型没有标定 EC 偏移，`ec.cpu_temp` /
/// `fan*_rpm` 一律不返回 ⇒ 总览页温度空着、顶栏挂「--」、历史曲线一条断线。
/// 而 HWiNFO 用它的内核驱动能直接读到 CPU 温度与风扇转速 —— 与其"什么都没有"，
/// 不如把这些读数接过来（读数本身是真的，不是猜的）。
///
/// 三条纪律：
/// <list type="number">
/// <item>**原生优先**：EC 给了值就一个字节都不动。这样机型档案（内置或用户「一键采集」
///   出来的那份）一旦带上 layout，下一次重启就自动回到 EC 原生读数，不用改任何开关；</item>
/// <item>**只补缺**：哪个键缺补哪个，不整段替换 —— 半标定（只有 cpu_temp）的档案照样能用；</item>
/// <item>**不碰控制**：`EcFanHardware` 取温度走的是 `EcHal.CachedStatus()`（EC 原始快照），
///   这里补的是给界面/历史看的那份。**风扇曲线不会拿 HWiNFO 的温度去写 EC** ——
///   写硬件的判据只认自己标定过的那条路。</item>
/// </list>
///
/// ⚠️ 温度来自 HWiNFO 这件事必须能被界面说清楚：判据就是 `device.caps.ec_read`
///    （false = 偏移没标定 = 现在看到的是兜底值）。所以这里**不加新的状态键**，
///    契约一个字段都不动。
/// </summary>
public sealed partial class AppState
{
    /// <summary>
    /// HWiNFO 的 CPU 温度（**外部**参照）。采集期标定 EC 偏移只许用它。
    ///
    /// 为什么不能直接用快照里的 <c>ec.cpu_temp</c>：已标定的机型上那个值本来就是从
    /// EC 某个偏移读出来的 —— 拿它当参照等于拿自己证自己。2026-11-14 真机踩到的样子：
    /// `status@4`=58 与参照 58 逐帧完全相等 ⇒ 方差为 0 ⇒ 相关系数算不出 ⇒
    /// 报告写"这段采样里没有任何偏移像温度，跑个负载再采一次"，而温度其实一直在眼前。
    ///
    /// 未标定机型反过来：那里 <c>ec.cpu_temp</c> 就是 HWiNFO 兜底值（见
    /// <see cref="FillEcFromHwInfo"/>），本身就是外部读数，可以照用 —— 判断在调用方。
    /// 读不到就返回 null：宁可没有参照（说"判不了"），也不要一个自证的参照。
    /// </summary>
    internal double? HwCpuTempC()
    {
        try { return _hw.FindCpuTemp().Value; } catch { return null; }
    }

    /// <summary>
    /// 把 EC 段里缺的（或明显不合理的）温度/转速用 HWiNFO 补上。
    /// 精简档也走这条 —— 未标定机型的历史曲线全靠它，缺了就是一条断线。
    ///
    /// 2026-11-22 改：未适配机型现在**借 NL5 的偏移表**读 EC（见 `quanta-gmwmi-generic`），
    /// 偏移不对时读出来是个"看着正常但没意义"的数 —— 程序只能识别**越界**这一种：
    /// 温度 ≤0 或 >115 ℃、转速 <0 或 >8000 一律当没读到，交给 HWiNFO。
    /// </summary>
    private void FillEcFromHwInfo(JsonObject snap)
    {
        if (snap["ec"] is not JsonObject ec) return;

        // 原生值都在且合理 ⇒ 一次 HWiNFO 都不问（已标定机型的常态，别为它加开销）
        var needCpu = !EcSanity.Temp(Num(ec["cpu_temp"]));
        var needGpu = !EcSanity.Temp(Num(ec["gpu_temp"]));
        var needFan = !EcSanity.Rpm(Num(ec["fan1_rpm"])) || !EcSanity.Rpm(Num(ec["fan2_rpm"]));
        if (!needCpu && !needGpu && !needFan) return;

        if (needCpu && _hw.FindCpuTemp().Value is { } ct)
            ec["cpu_temp"] = (long)Math.Round(ct);

        if (needGpu)
        {
            // 显卡温度先问原生：nvidia-smi 直读核心温度，比 HWiNFO 的标签匹配可靠。
            if (Num(snap["gpu"]?["temp"]) is { } native)
                ec["gpu_temp"] = (long)Math.Round(native);
            else if (_hw.FindGpuTemp().Value is { } hw)
                ec["gpu_temp"] = (long)Math.Round(hw);
        }

        if (needFan)
        {
            var (fan1, fan2, _) = _hw.FindFanRpm();
            if (!EcSanity.Rpm(Num(ec["fan1_rpm"])) && fan1 is { } f1) ec["fan1_rpm"] = (long)Math.Round(f1);
            if (!EcSanity.Rpm(Num(ec["fan2_rpm"])) && fan2 is { } f2) ec["fan2_rpm"] = (long)Math.Round(f2);
        }
    }
}
