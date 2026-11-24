using QControl.Core.Power;

namespace QControl.Tests;

/// <summary>
/// 插拔电源自动切档的判断规则（<see cref="PowerModePolicy"/>）。
///
/// 这些结论每一条都对应一个真机上会出问题的场景，不是"为了凑覆盖率":
///   · 已经在办公档还写一次 = 平白多一次高危 EC 写；
///   · 认不出当前档还硬切 = 把未知状态当成 game/beast 处理，风扇曲线会跟着换一张；
///   · 插回电把 office 当"原档"恢复 = 用户永远回不到自己那一档；
///   · 用户在电池上自己换了档，插回电还被改回去 = 替他做决定。
/// </summary>
public class PowerModePolicyTests
{
    [Theory]
    [InlineData("game")]
    [InlineData("beast")]
    public void 拔电时切到办公档(string current)
        => Assert.Equal(PowerModePolicy.Office, PowerModePolicy.TargetOnBattery(current));

    [Fact]
    public void 本来就在办公档就不再写()
        => Assert.Null(PowerModePolicy.TargetOnBattery("office"));

    [Fact]
    public void 认不出当前档就不动()
        => Assert.Null(PowerModePolicy.TargetOnBattery(null));

    [Theory]
    [InlineData("game")]
    [InlineData("beast")]
    public void 插回电恢复拔电前那一档(string remembered)
        => Assert.Equal(remembered, PowerModePolicy.TargetOnAc(remembered, "office"));

    [Fact]
    public void 没记过原档就不恢复()
        => Assert.Null(PowerModePolicy.TargetOnAc(null, "office"));

    [Fact]
    public void 原档就是办公档不算恢复()
        => Assert.Null(PowerModePolicy.TargetOnAc("office", "office"));

    [Fact]
    public void 已经在原档就不重复写()
        => Assert.Null(PowerModePolicy.TargetOnAc("game", "game"));

    [Fact]
    public void 电池上用户换了档则作废旧的原档()
        => Assert.True(PowerModePolicy.UserTookOver("beast", "game"));

    [Fact]
    public void 还在办公档时不算用户接管()
        => Assert.False(PowerModePolicy.UserTookOver("office", "game"));

    [Fact]
    public void 没记过原档时谈不上接管()
        => Assert.False(PowerModePolicy.UserTookOver("beast", null));

    [Fact]
    public void 读不到当前档时不敢判成接管()
        => Assert.False(PowerModePolicy.UserTookOver(null, "game"));

    // ================================================================ 翻转确认门
    // 2026-11-17：本机 ACLineStatus 偶发报 0 一秒多（实测 0.92 / 1.40 / 1.69 / 1.72 秒），
    // 每次都让程序"自己切到了办公模式"。判据要求新读数连续保持够久才算一次插拔。

    [Fact]
    public void 刚翻过来的读数不算数()
        => Assert.False(PowerModePolicy.FlipConfirmed(confirmed: true, plugged: false,
                                                      nowMs: 1_000, sinceMs: 900, confirmMs: 2500));

    [Fact]
    public void 保持够时长才认这次翻转()
        => Assert.True(PowerModePolicy.FlipConfirmed(confirmed: true, plugged: false,
                                                     nowMs: 3_500, sinceMs: 900, confirmMs: 2500));

    [Fact]
    public void 与已确认值一致的读数立刻可信()
        => Assert.True(PowerModePolicy.FlipConfirmed(confirmed: true, plugged: true,
                                                     nowMs: 1_000, sinceMs: 999_999, confirmMs: 2500));

    [Fact]
    public void 还没确认过任何值时要先观察()
        => Assert.False(PowerModePolicy.FlipConfirmed(confirmed: null, plugged: true,
                                                      nowMs: 10_000, sinceMs: 9_900, confirmMs: 2500));
}
