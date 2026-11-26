namespace QControl.Core.Lights;

/// <summary>采样循环这一拍该对背光做什么。</summary>
public enum AutoOffAction
{
    /// <summary>不动。</summary>
    None,

    /// <summary>把人叫醒：解锁且真的在用 ⇒ 把灯开回来。</summary>
    Restore,

    /// <summary>闲置到点，熄灯（原因码 1）。</summary>
    TurnOffIdle,

    /// <summary>锁屏，立刻熄灯（原因码 2）。</summary>
    TurnOffLock,
}

/// <summary>
/// 空闲熄灯的状态机判定（只回答"这一拍做什么"，写 EC 与计时器都在 <c>AppState</c>）。
///
/// 顺序是有讲究的，三条都踩过：
///   1. **已经在熄灯状态时先判恢复**：只有「解锁 + 最近 2 秒内有输入」才恢复；
///      否则一直黑着 —— 否则锁屏期间会反复开关。
///   2. **冷却期内什么都不做**：熄灯/恢复都是写 EC，来回抖动比"晚几秒"难受得多。
///   3. **锁屏优先于闲置**：两个条件同时成立时按「锁屏」记原因，界面上那句话才对得上。
///
/// ⚠️ 「功能被关掉 ⇒ 立刻恢复」这一步**在调用方**（`AppState`）先做，因为它决定了要不要
///    去查锁屏/闲置 —— 关着这个功能时不该每拍都去问系统状态。
/// </summary>
public static class AutoOffRule
{
    /// <summary>闲置多久算"人回来了"（毫秒）。比它短就恢复，比它长就继续黑着。</summary>
    public const long IdleResetMs = 2_000;

    /// <param name="offBy">0 = 灯亮着；1 = 因闲置熄的；2 = 因锁屏熄的。</param>
    /// <param name="coolingDown">还在冷却窗口内（刚写过 EC）。</param>
    /// <param name="minutes">本档配置的闲置分钟数，0 = 不按闲置熄。</param>
    /// <param name="onLock">本档是否"锁屏就熄"。</param>
    public static AutoOffAction Decide(
        int offBy,
        bool locked,
        long idleMs,
        bool coolingDown,
        int minutes,
        bool onLock)
    {
        // ① 已经黑着：只考虑恢复
        if (offBy != 0)
            return !locked && idleMs < IdleResetMs ? AutoOffAction.Restore : AutoOffAction.None;

        // ② 冷却期内不写
        if (coolingDown) return AutoOffAction.None;

        // ③ 锁屏优先
        if (locked && onLock) return AutoOffAction.TurnOffLock;

        if (minutes > 0 && idleMs >= minutes * 60_000L) return AutoOffAction.TurnOffIdle;

        return AutoOffAction.None;
    }
}
