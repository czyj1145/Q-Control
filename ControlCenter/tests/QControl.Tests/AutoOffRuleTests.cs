using QControl.Core.Lights;

namespace QControl.Tests;

/// <summary>
/// 空闲熄灯的判定顺序。原先埋在 <c>AppState.TryAutoOffBacklight</c> 的采样循环里 ——
/// 这里钉的是三条**顺序敏感性**：先恢复再熄、冷却期什么都不做、锁屏优先于闲置。
/// </summary>
public class AutoOffRuleTests
{
    private const int OneMinuteMs = 60_000;

    private static AutoOffAction Decide(
        int offBy = 0,
        bool locked = false,
        long idleMs = 0,
        bool coolingDown = false,
        int minutes = 5,
        bool onLock = true)
        => AutoOffRule.Decide(offBy, locked, idleMs, coolingDown, minutes, onLock);

    // ---- 灯亮着：什么时候熄 ----

    [Fact]
    public void 没人动也没锁屏_不动()
        => Assert.Equal(AutoOffAction.None, Decide(idleMs: OneMinuteMs));

    [Theory]
    [InlineData(5, 5 * OneMinuteMs)]   // 刚好到点就熄（>= 不是 >）
    [InlineData(5, 6 * OneMinuteMs)]
    [InlineData(1, OneMinuteMs)]
    public void 闲置到点熄灯(int minutes, long idle)
        => Assert.Equal(AutoOffAction.TurnOffIdle, Decide(idleMs: idle, minutes: minutes));

    [Fact]
    public void 差一毫秒不熄()
        => Assert.Equal(AutoOffAction.None, Decide(idleMs: 5 * OneMinuteMs - 1, minutes: 5));

    [Fact]
    public void 锁屏熄灯不受闲置分钟影响()
        => Assert.Equal(AutoOffAction.TurnOffLock, Decide(locked: true, idleMs: 0, minutes: 0, onLock: true));

    [Fact]
    public void 没开锁屏熄_锁着也不熄()
        => Assert.Equal(AutoOffAction.None, Decide(locked: true, idleMs: 0, minutes: 0, onLock: false));

    [Fact]
    public void 锁屏与闲置同时满足_按锁屏记原因()
        => Assert.Equal(AutoOffAction.TurnOffLock, Decide(locked: true, idleMs: 10 * OneMinuteMs, minutes: 5, onLock: true));

    [Fact]
    public void 冷却期内即使到点也不写()
        => Assert.Equal(AutoOffAction.None, Decide(idleMs: 10 * OneMinuteMs, coolingDown: true, minutes: 5));

    [Fact]
    public void 冷却期内锁屏也不写()
        => Assert.Equal(AutoOffAction.None, Decide(locked: true, coolingDown: true, minutes: 5, onLock: true));

    // ---- 灯已黑：什么时候恢复 ----

    [Fact]
    public void 解锁且刚有输入才恢复()
        => Assert.Equal(AutoOffAction.Restore, Decide(offBy: 1, idleMs: 0));

    [Fact]
    public void 解锁但两秒没动_继续黑着()
        => Assert.Equal(AutoOffAction.None, Decide(offBy: 1, idleMs: AutoOffRule.IdleResetMs));

    [Fact]
    public void 还锁着就不恢复()
        => Assert.Equal(AutoOffAction.None, Decide(offBy: 2, locked: true, idleMs: 0));

    [Fact]
    public void 熄灯状态下冷却期不阻止恢复()
        => Assert.Equal(AutoOffAction.Restore, Decide(offBy: 1, idleMs: 0, coolingDown: true));

    [Fact]
    public void 熄灯状态下不会再熄一次()
    {
        Assert.Equal(AutoOffAction.Restore, Decide(offBy: 1, idleMs: 0, locked: false));
        Assert.NotEqual(AutoOffAction.TurnOffIdle, Decide(offBy: 1, idleMs: 10 * OneMinuteMs, minutes: 5));
        Assert.NotEqual(AutoOffAction.TurnOffLock, Decide(offBy: 2, locked: true, onLock: true));
    }

    [Fact]
    public void 闲置阈值常量是两秒()
        => Assert.Equal(2_000, AutoOffRule.IdleResetMs);
}
