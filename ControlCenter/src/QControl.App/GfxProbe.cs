using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Hal.Sensors;

namespace QControl.App;

/// <summary>
/// 显卡模式的**读侧证据**：EC 的 `0x0203` 当前值 + 系统里看得见哪几块显卡。
///
/// 为什么单独一步：原厂切显卡模式必须重启，观察会话里抓不到 `0x0203` 写字，
/// 所以"这台有没有显卡切换"只能由用户回答（采集页那颗三态选项）。这里读到的值
/// 不单独开能力，只当**佐证**写进采集包：
///   · 独显直连时固件会把核显整个藏起来 ⇒ 读到 2 且系统里没有核显，说明这个值在生效；
///   · 反过来不成立：读到 1（混合）既可能是混合输出，也可能是这台根本没有 MUX。
///
/// ⚠️ 读两次是刻意的：WMI 缓冲是共享的，连续不同命令之间要留 ≥0.5 秒
/// （见 `docs/PROTOCOL.md`「安全注意事项」）；两次不一致时只写"不稳定"，不取平均。
/// </summary>
internal static class GfxProbe
{
    /// <summary>两次读之间的间隔（毫秒）：0.5 秒是实测稳定下限，留 100ms 余量。</summary>
    private const int GapMs = 600;

    public static JsonObject Read(AppState st)
    {
        var reads = new List<JsonObject>();
        for (var i = 0; i < 2; i++)
        {
            if (i > 0) Thread.Sleep(GapMs);
            try
            {
                reads.Add(st.Ec.ReadGfxProbe());
            }
            catch (Exception e)
            {
                // 读不到是**有效结论**（这台 EC 不认 0x0203），不是"采集失败"：
                // 照样记下原因，让报告能说"读不到"，而不是整段消失。
                reads.Add(new JsonObject { ["error"] = e.Message });
            }
        }

        var last = reads[^1];
        var ok = ModeOf(last) is not null;
        var mode = ModeOf(last);
        var stable = reads.Count == 2 && ModeOf(reads[0]) is { } a && ModeOf(reads[1]) is { } b && a == b;

        // 显示拓扑：物理显卡清单（虚拟显示适配器不算），与总览页共用 60 秒缓存
        var disp = DisplaySensors.Read();
        var dispOk = disp["ok"]?.GetValue<bool>() == true;
        var igpu = dispOk && disp["igpu"]?.GetValue<bool>() == true;
        var dgpu = dispOk && disp["has_dgpu"]?.GetValue<bool>() == true;

        return new JsonObject
        {
            ["ok"] = ok,
            ["raw"] = last["raw"]?.DeepClone(),
            ["mode"] = mode,
            ["dat4"] = last["dat4"]?.DeepClone(),
            ["stable"] = stable,
            ["reads"] = new JsonArray(reads.Select(r => (JsonNode?)r).ToArray()),
            // 拓扑拿不到就写 null（"不知道"），不要写成 false —— 那会被读成"没有核显"
            ["igpu"] = dispOk ? igpu : null,
            ["has_dgpu"] = dispOk ? dgpu : null,
            ["dgpu"] = disp["dgpu"]?.DeepClone(),
            ["physical"] = disp["physical"]?.DeepClone(),
            ["verdict"] = Verdict(mode, stable, dispOk, igpu, dgpu),
        };
    }

    /// <summary>把采集包里那段 JSON 变成 Core 用的纯数据（Core 不引用 Hal）。</summary>
    public static GfxRead ToEvidence(JsonObject probe) => new(
        probe["mode"] is JsonValue mv && mv.TryGetValue<int>(out var m) ? m : null,
        probe["ok"]?.GetValue<bool>() == true,
        probe["stable"]?.GetValue<bool>() == true,
        probe["igpu"] is JsonValue iv && iv.TryGetValue<bool>(out var i) && i,
        probe["has_dgpu"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d);

    private static int? ModeOf(JsonObject read)
        => read["gfx_mode"] is JsonValue v && v.TryGetValue<uint>(out var m) && m is > 0 and <= 0xFF
            ? (int)m
            : null;

    /// <summary>一句话结论（给采集包与报告用；与 AdoptGuide 里的措辞同源）。</summary>
    private static string Verdict(int? mode, bool stable, bool dispOk, bool igpu, bool dgpu)
    {
        if (mode is null) return "读不到 0x0203（EC 没应答）";
        if (mode is < 1 or > 3) return $"读到 {mode}，不在 1~3 里（0x0203 未标定或缓冲残留）";
        if (!stable) return $"读到 {mode}，但两次不一致（缓冲被抢）——不能当证据";
        if (!dispOk) return $"读到 {mode}；显示拓扑拿不到，无法交叉核对";

        return mode switch
        {
            2 when !igpu => "读到 2 = 独显直连，且系统里看不到核显 ⇒ 与固件行为一致",
            3 when !dgpu => "读到 3 = 集显输出，且系统里看不到独显 ⇒ 与固件行为一致",
            1 when igpu && dgpu => "读到 1 = 混合输出，核显与独显都在 ⇒ 一致"
                                   + "（但没有 MUX 的机型也一样，不能据此认定能切）",
            _ => $"读到 {mode}，但核显{(igpu ? "在" : "不在")}、独显{(dgpu ? "在" : "不在")}"
                 + "，与当前模式对不上（可能刚写过还没重启，或这台没有 MUX）",
        };
    }
}
