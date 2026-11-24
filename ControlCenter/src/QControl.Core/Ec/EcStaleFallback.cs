using System.Text.Json.Nodes;

namespace QControl.Core.Ec;

/// <summary>
/// EC 偶发读失败那一拍的"沿用上次有效读数"规则（2026-11-19）。
///
/// <para>
/// 背景：EC 是跨进程共享的单槽缓冲，插拔电写 0x0300、睡眠唤醒、原厂 daemon 抢缓冲时
/// 主状态帧会偶发读失败。此前 AppState 把 ec 段整个换成 <c>{"error":...}</c>，
/// fan1_rpm 等字段随之消失；界面的 <c>FanDial.SetRpm(null)</c> 于是把转速环归零、
/// 数字变 "--"，下一拍读到真值再扫回来 —— 用户看到的就是"风扇圈突然变 0 又恢复"。
/// </para>
/// <para>
/// 同一个 BuildSnapshot 里 gfx / pch / perf 读失败都沿用上次值，只有 ec 段是特例；
/// 这里把它对齐。纯判定放 Core，App 只负责喂数据，测试只测这里。
/// </para>
/// </summary>
public static class EcStaleFallback
{
    /// <summary>属于"这一拍"的诊断字段，绝不能从上一拍继承。</summary>
    private static readonly HashSet<string> DiagnosticKeys = new(StringComparer.Ordinal)
    {
        "error", "raw", "sys_error", "sys_replay", "sys_zero_frame",
    };

    /// <summary>
    /// 把上一拍的读数补进这一拍的失败帧。这一拍已有的值优先，不被覆盖
    /// （比如 HWiNFO 兜底刚补进来的温度）。
    /// </summary>
    /// <returns>是否补进了至少一个字段。</returns>
    public static bool Merge(JsonObject failed, JsonObject? previous)
    {
        if (previous is null) return false;

        var added = false;
        foreach (var (key, value) in previous)
        {
            if (DiagnosticKeys.Contains(key)) continue;
            if (failed[key] is not null) continue;
            failed[key] = value?.DeepClone();
            added = true;
        }
        return added;
    }
}
