namespace QControl.Core.Devices;

/// <summary>
/// 「EC 上报的亮度变化是不是用户从外部改的」判定 —— 纯逻辑，不碰 EC / 不碰文件。
///
/// 为什么需要它（2026-11-19 13:41 真机事故）：
/// 老实现拿「`perf.modes[档].light.level` ≠ EC 里的 `backlight_level`」就认定用户按了 FN 亮度键，
/// 并把 EC 值写进档案。但切档 / 开「自定义接管」时，EC 里往往还挂着**上一套**的亮度：
/// 13:41:31 开接管重放「自定义」档 = 50%，13:41:35 一个采样拍读到残留的 100%，
/// 老实现就把它当成「用户把自定义档改成 100%」写盘 —— 从此每次开接管亮度都跳一档。
///
/// 两条规矩：
///   ① **变化检测**：只有相对**上一拍**真的变了才可能是外部动作；值一直没变就是残留，不是意图。
///      （真正的 FN+F1/F2 是 EC 固件直接改的，会让下一拍读数跳变，不会被这条挡掉。）
///   ② **自己写的静默期**：我们自己下发背光后的 <see cref="QuietMs"/> 毫秒内，
///      EC 上报值就算变了也不采信 —— 那是自己写的回响，不是用户意图。
/// </summary>
public static class BacklightLevelWatch
{
    /// <summary>写完 EC 后多久内的亮度变化不采信（毫秒）。覆盖一到两个采样拍。</summary>
    public const int QuietMs = 1500;

    /// <summary>
    /// 这一拍的 EC 亮度要不要当成「用户从外部改的」记进当前那一档的档案。
    /// </summary>
    /// <param name="prev">上一拍观测到的 EC 亮度；null = 还没建立基线（首帧不采信）。</param>
    /// <param name="now">这一拍读到的 EC 亮度。</param>
    /// <param name="sinceWriteMs">距我们自己最后一次写 EC 的毫秒数；null = 从没写过。</param>
    /// <param name="quietMs">静默期长度。</param>
    public static bool IsExternalChange(int? prev, int now, long? sinceWriteMs, int quietMs = QuietMs)
        => prev is not null
           && now != prev
           && (sinceWriteMs is null || sinceWriteMs >= quietMs);
}
