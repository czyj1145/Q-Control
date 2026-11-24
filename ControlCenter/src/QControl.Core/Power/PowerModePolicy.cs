namespace QControl.Core.Power;

/// <summary>
/// 插拔电源适配器时的性能模式自动切换规则（原厂控制中心的同款行为：
/// 拔掉适配器 ⇒ 切办公模式省电，插回去 ⇒ 恢复原来那一档）。
///
/// 这里**只做判断、不碰硬件**：真正写 EC / 切电源计划在 App 层的
/// `AppState.PowerMode.cs`。分成两半的理由是这一半是纯函数，
/// 能被单元测试逐条钉住（写 EC 的那一半只能在真机上验）。
/// </summary>
public static class PowerModePolicy
{
    /// <summary>省电档的键名（与 `presets.perf.modes` 的键一致）。</summary>
    public const string Office = "office";

    /// <summary>
    /// 刚拔掉适配器：该切到哪一档。null = 什么都不做。
    /// 认不出当前档（<paramref name="currentMode"/> 为 null，例如换过机型/固件改了 APMD 码）
    /// 时也不动 —— 不知道现在是什么档，就没有"切走再切回"可言。
    /// </summary>
    public static string? TargetOnBattery(string? currentMode)
        => currentMode is null || currentMode == Office ? null : Office;

    /// <summary>
    /// 刚插回适配器：该恢复到哪一档。null = 什么都不做。
    /// <paramref name="currentMode"/> 与记住的档一致 ⇒ 本来就在那一档（用户自己切回去了），
    /// 不必再写一次 EC。
    /// </summary>
    public static string? TargetOnAc(string? remembered, string? currentMode)
        => remembered is null || remembered == Office || remembered == currentMode ? null : remembered;

    /// <summary>
    /// 电池上用户自己换了档（点界面上别的模式 / 按 FN+3）⇒ 之前记下的
    /// "插回电恢复成哪一档"应当作废：那是我们替他记的，他既然自己选了，就别再拿旧档覆盖他。
    /// </summary>
    public static bool UserTookOver(string? currentMode, string? remembered)
        => remembered is not null && currentMode is not null && currentMode != Office;

    /// <summary>
    /// 翻转确认：这一拍读到的 <paramref name="plugged"/> 能不能当真。
    ///
    /// 为什么需要（2026-11-17）：本机的 `GetSystemPowerStatus` 会偶发把 `ACLineStatus`
    /// 报成 0（电池），0.92~1.72 秒后又自己回 1 —— 实测四次，每次都触发了一次
    /// "拔电 → 切办公 → 插回 → 恢复"的 EC 写入。用户的原话是"我没断开电源"。
    ///
    /// 判据只有两条：要么这个值本来就已确认（<paramref name="confirmed"/> 相同），
    /// 要么它已经连续保持够 <paramref name="confirmMs"/>（真拔插不会 2 秒内插回去）。
    /// </summary>
    public static bool FlipConfirmed(bool? confirmed, bool plugged, long nowMs, long sinceMs, int confirmMs)
        => confirmed == plugged || nowMs - sinceMs >= confirmMs;
}
