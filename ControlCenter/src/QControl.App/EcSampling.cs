using System.Text.Json.Nodes;
using QControl.Core.Ec;

namespace QControl.App;

/// <summary>
/// 采集期共用的两个"读一小口"的动作：一帧 `status`、一份状态快照。
///
/// 为什么单独拎出来：`MachineObserve`（引导里风扇那一步）与 `CpuLoadProbe`（加负载看风扇）
/// 都要**按节拍读同一帧**，两处各写一份的话，偏移取法一旦分叉，同一台机器会算出两套结论。
///
/// ⚠️ 只读 `status` 这一条命令。**不要**顺手多读别的读命令来"多拿点样本"：
///    档案里的 `layout` 没有"来源命令"这一维（`EcHal.ReadStatus` 永远读 status），
///    别的命令里的偏移写进 layout 就会被当成 status 的偏移读出来 ——
///    2026-11-16 真机就这么把 `gpu_temp` 标成了 `gpu_info@4`，与 cpu_temp 撞在同一个槽。
/// </summary>
internal static class EcSampling
{
    /// <summary>数据区的 7 个 u32：DAT2..DAT8 = 偏移 4/8/12/16/20/24/28（与 <see cref="EcFrame"/> 一致）。</summary>
    private static readonly (string Key, int Offset)[] Slots =
    [
        ("DAT2@4", 4), ("DAT3@8", 8), ("DAT4@12", 12), ("DAT5@16", 16),
        ("DAT6@20", 20), ("DAT7@24", 24), ("DAT8@28", 28),
    ];

    /// <summary>
    /// 读一帧 `status`，取数据区 7 个 u32。
    /// 与 <c>MachineAdopt.Sample</c> 同一口径 —— 三处必须一致。
    /// </summary>
    public static Dictionary<string, uint[]>? StatusFrame(AppState st)
    {
        var sub = st.Ec.Spec.Sub("status");
        if (sub is <= 0 or > 0xFFFF) return null;

        try
        {
            var raw = st.Ec.Raw(new EcCommand((ushort)sub, EcDirection.Read));
            if (raw["u32"] is not JsonObject u) return null;

            var vals = new uint[Slots.Length];
            for (var i = 0; i < Slots.Length; i++)
                vals[i] = (uint)Math.Max(0, Num(u[Slots[i].Key]) ?? 0);

            return new Dictionary<string, uint[]>(StringComparer.Ordinal) { ["status"] = vals };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>最近一份状态快照（拿 CPU 功耗 / CPU 温度 / 显卡温度当参照）。读不到就 null。</summary>
    public static JsonObject? Snapshot(AppState st)
    {
        try { return st.CachedStatus(); }
        catch { return null; }
    }

    /// <summary>
    /// 快照里的数字：CPU 功耗（W）/ CPU 温度 / 显卡温度。读不到一律 null，不补 0。
    ///
    /// ⚠️ CPU 温度这一路**已标定的机型必须换外部读数**（<see cref="AppState.HwCpuTempC"/>）：
    /// 快照里的 <c>ec.cpu_temp</c> 在那种机器上就是 EC 自己的偏移读数，拿它当参照属于
    /// 循环论证 —— 逐帧完全相等 ⇒ 方差 0 ⇒ 相关系数 NaN ⇒ 自动标定永远说"认不出温度"。
    /// 未标定机型上那个字段是 HWiNFO 兜底值，本身就是外部的，照用。
    /// </summary>
    public static (double? CpuPower, double? CpuTemp, double? GpuTemp) Refs(AppState st, JsonObject? snap)
    {
        if (snap is null) return (null, null, null);
        try
        {
            var fromHw = snap["ec"]?["layout_unverified"] is JsonValue lv
                         && lv.TryGetValue<bool>(out var lu) && lu;
            var cpuT = fromHw ? Num(snap["ec"]?["cpu_temp"]) : st.HwCpuTempC();
            return (Num(snap["cpu"]?["power"]), cpuT, Num(snap["gpu"]?["temp"]));
        }
        catch
        {
            return (null, null, null);
        }
    }

    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<uint>(out var x)) return x;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }
}
