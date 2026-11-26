namespace QControl.Core.FanCurve;

/// <summary>散热卡卡头那句「风扇整体状态」到底是哪一种。</summary>
public enum FanTagKind
{
    /// <summary>快照还没到（第一拍）。</summary>
    Loading,

    /// <summary>EC 读不到（档案不匹配 / 驱动没起来）。</summary>
    EcFailed,

    /// <summary>临时拉满（`fan_oem_max`），EC 里钉的是我们自己写的 100%。</summary>
    OemMax,

    /// <summary>自定义风扇关着、EC 里也没有我们写的值 ⇒ 真·固件控制。</summary>
    Firmware,

    /// <summary>
    /// 开关关着，但引擎在跑 —— **只有风扇守护的 `Start(force:true)` 会出现这种组合**：
    /// 固件调速被判定失效，本程序已按曲线接管（写 EC），用户的开关仍保持关闭。
    /// </summary>
    GuardForced,

    /// <summary>自定义风扇关着，但这次没交回成功，EC 还钉着上一次写的值。</summary>
    StoppedStillStuck,

    /// <summary>手动模式、曲线没在跑 ⇒ 转速就是用户选的值。</summary>
    Manual,

    /// <summary>曲线在跑（可能带温度保护）。</summary>
    AutoRunning,

    /// <summary>不是手动、曲线没跑，但 EC 里还钉着旧值 ⇒ 异常。</summary>
    Stuck,

    /// <summary>不是手动、曲线没跑、EC 里也没留值。</summary>
    AutoIdle,
}

/// <summary>
/// 卡头标签的**判定顺序**（文案留在界面层，这里只回答"是哪一种"）。
///
/// ⚠️ 这个顺序本身踩过三次坑，改之前先看注释：
///   1. **`oemMax` 必须排在 stuck 之前** —— 临时满转时 EC 里当然钉着 100，
///      先判 stuck 会让用户看到「已停写 · EC 仍为 100%」这种假故障（2026-11-25）。
///   2. **`!enabled` 必须排在 manual 之前** —— 关掉「自定义风扇」才是"现在听谁"的
///      根本答案；后端会把 mode 归到 auto，但即便没归也对不上"手动"。
///   3. **`manual` 必须排在 stuck 之前** —— 手动模式下后端必然给 `stuck_duty`
///      （那就是我们写进 EC 的值），先判 stuck 会把用户**自己选的**转速说成"卡在 60%"。
///
/// 三条都是"顺序错了就说假话"，不是"顺序错了也不影响"。
/// </summary>
public static class FanTagRule
{
    public static FanTagKind Decide(
        bool statusLoaded,
        bool ecPresent,
        bool oemMax,
        bool enabled,
        bool manual,
        bool running,
        long? stuckDuty)
    {
        if (!statusLoaded) return FanTagKind.Loading;
        if (!ecPresent) return FanTagKind.EcFailed;

        // ④ 守护接管：开关是关的、引擎却在跑。写硬件的事实必须压过"没开"的说法，
        //    否则界面会一边说「固件控制」一边按曲线写 UFA（2026-11-26 日志实证）。
        if (running && !enabled) return FanTagKind.GuardForced;

        // 顺序 1：临时满转不是"交回失败"
        if (oemMax) return FanTagKind.OemMax;

        // 顺序 2：风扇听谁，先看开关
        if (!enabled)
            return stuckDuty is null ? FanTagKind.Firmware : FanTagKind.StoppedStillStuck;

        // 顺序 3：手动模式下 stuck_duty 是用户自己的选择，不是异常
        if (manual && !running) return FanTagKind.Manual;

        if (running) return FanTagKind.AutoRunning;
        if (stuckDuty is not null) return FanTagKind.Stuck;
        return FanTagKind.AutoIdle;
    }
}
