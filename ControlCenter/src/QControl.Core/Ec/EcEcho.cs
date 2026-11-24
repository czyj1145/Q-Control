using System.Globalization;

namespace QControl.Core.Ec;

/// <summary>
/// 「回帧对不对得上」和「读间隔能压到多快」—— 采集期自动标定这两项时用的纯逻辑。
///
/// 为什么单独拎出来：这两条以前只能靠作者手工标（连发几十次看什么时候出脏帧、
/// 逐条比对回帧前 4 字节），而它们决定两件事：
/// <list type="bullet">
///   <item>回显可校验的读能在缓冲区被原厂抢占时**重试**，而不是把别人的帧当温度显示；</item>
///   <item>读间隔写小了会偶发脏帧，写大了每帧白等（本机 0.45 s 时整帧 515 ms 里有 90% 是等待）。</item>
/// </list>
/// 判定规则放在这里，App 只负责"读 + 喂数据"，测试只测这里。
/// </summary>
public static class EcEcho
{
    /// <summary>
    /// 回帧前 4 字节是不是 `group(LE) + sub(LE)`。
    /// 读组是 0xFA00 ⇒ 帧头形如 `00 FA 00 02`（status）；写组 0xFB00 同理。
    /// 实测本机 0x0202 / 0x0203 / 0x0207 **不回显**（回的是 `14 01 02 02` / 全 0 之类），
    /// 所以这个判定必须逐命令做一次，不能按"读命令都回显"一刀切。
    /// </summary>
    public static bool Matches(string? respHex, ushort group, ushort sub)
    {
        if (string.IsNullOrWhiteSpace(respHex)) return false;

        var b = respHex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (b.Length < 4) return false;

        return Byte(b[0], group & 0xFF) && Byte(b[1], group >> 8)
               && Byte(b[2], sub & 0xFF) && Byte(b[3], sub >> 8);
    }

    /// <summary>
    /// 从"档位 → 失败次数"里挑建议值：**第一个零失败的档**再加一档余量。
    ///
    /// 为什么加余量：探测只跑十几次，零失败不等于长期零失败；余量一档的成本是每帧几毫秒，
    /// 而脏帧的代价是一次错误的温度读数。全档位都失败时返回 null —— 那说明这条命令根本
    /// 不该靠间隔去降，保持档案原值。
    /// </summary>
    /// <param name="rows">(间隔秒, 失败次数)，按间隔从大到小或从小到大都行，内部会排序。</param>
    /// <param name="margin">余量（秒）。</param>
    /// <param name="max">建议值上限（秒）。</param>
    public static double? PickReadGap(IEnumerable<(double Gap, int Fail)> rows, double margin, double max)
    {
        var list = rows.ToList();
        if (list.Count == 0) return null;

        // ⚠️ 不能写成 `.Where(...).Select(...).FirstOrDefault()`：没有零失败档时它返回 0，
        //    会被当成"最小的安全间隔是 0"—— 那正好是最激进的值，方向完全反了。
        var safeList = list.Where(r => r.Fail == 0).Select(r => r.Gap).OrderBy(g => g).ToList();
        if (safeList.Count == 0) return null;

        var suggest = Math.Round(Math.Min(max, safeList[0] + margin), 3);
        return suggest < 0 ? 0 : suggest;
    }

    private static bool Byte(string hex, int value)
        => string.Equals(hex, value.ToString("X2", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
}
