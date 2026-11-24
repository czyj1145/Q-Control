namespace QControl.Core.Devices;

/// <summary>
/// 「哪些帧可以参与 layout 推断」—— 全项目唯一的判定口。
///
/// 为什么必须限制：机型档案里的 `layout` 只有 `字段 → (偏移, 长度)`，**没有"来源命令"这一维**，
/// 运行时 `EcHal.ReadStatus` 永远读 `status` 那一条命令的 32 字节帧。所以：
///
///   · 从 `status` 里认出来的偏移 → 读的时候读的就是同一个位置，结论成立；
///   · 从 `0x0202` / `0x0207` 等别的命令里认出来的偏移 → 写进 layout 后会被当成
///     **status 的同一个偏移**去读，显示出来的就是另一个值。
///
/// 2026-11-16 真机踩到：采集里让其它读命令也拿到足够样本后，`gpu_info@4`（那帧的 DAT2
/// 确实是显卡温度）被写成 `gpu_temp: [4,4]`，与 `cpu_temp: [4,4]` 撞在同一个槽 ——
/// 界面上"显卡温度"会显示 CPU 温度，而且不报错。
///
/// 所以：样本里有 `status` 就**只认 status**；一条 `status` 都没有（离线夹具 / 将来换主命令名）
/// 才退回全部命令 —— 那种情况下没有"正确位置"可言，至少别让整段推断直接失效。
/// </summary>
internal static class LayoutFrames
{
    public const string StatusCommand = "status";

    public static IReadOnlyList<string> Commands(IReadOnlyList<LayoutSample> samples)
    {
        var keys = samples.SelectMany(s => s.Frames.Keys)
                          .Distinct(StringComparer.Ordinal)
                          .ToList();
        if (keys.Count == 0) return keys;

        return keys.Contains(StatusCommand, StringComparer.Ordinal)
            ? [StatusCommand]
            : keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }
}
