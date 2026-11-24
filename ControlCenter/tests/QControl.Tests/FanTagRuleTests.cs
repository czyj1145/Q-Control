using QControl.Core.FanCurve;

namespace QControl.Tests;

/// <summary>
/// 卡头标签的判定顺序。这里钉的是**三条踩过坑的优先级**，不是文案：
/// 1. 临时满转（`fan_oem_max`）不能显示成"交回失败"；
/// 2. 自定义风扇关着时，开关优先于手动；
/// 3. 手动模式下 `stuck_duty` 是用户自己的选择，不能说成"卡住"。
/// </summary>
public class FanTagRuleTests
{
    private static FanTagKind Decide(
        bool statusLoaded = true,
        bool ecPresent = true,
        bool oemMax = false,
        bool enabled = true,
        bool manual = false,
        bool running = false,
        long? stuckDuty = null)
        => FanTagRule.Decide(statusLoaded, ecPresent, oemMax, enabled, manual, running, stuckDuty);

    [Fact]
    public void 快照还没到_读取中()
        => Assert.Equal(FanTagKind.Loading, Decide(statusLoaded: false, ecPresent: false));

    [Fact]
    public void 快照到了但EC读不到_EC读取失败()
        => Assert.Equal(FanTagKind.EcFailed, Decide(ecPresent: false, enabled: false));

    // ---- 顺序 1：临时满转不是"交回失败" ----

    [Fact]
    public void 临时满转_即使EC里钉着100也说最大转速()
        => Assert.Equal(FanTagKind.OemMax, Decide(oemMax: true, enabled: false, stuckDuty: 100));

    [Fact]
    public void 临时满转_曲线开着在跑也是最大转速()
        => Assert.Equal(FanTagKind.OemMax, Decide(oemMax: true, enabled: true, running: true, stuckDuty: 100));

    [Fact]
    public void 读取中与EC失败_都排在临时满转之前()
    {
        Assert.Equal(FanTagKind.Loading, Decide(statusLoaded: false, oemMax: true));
        Assert.Equal(FanTagKind.EcFailed, Decide(ecPresent: false, oemMax: true));
    }

    // ---- 顺序 2：开关优先 ----

    [Fact]
    public void 自定义风扇关着_EC里没留值_固件控制()
        => Assert.Equal(FanTagKind.Firmware, Decide(enabled: false));

    [Fact]
    public void 自定义风扇关着_但EC里还留着值_已停写()
        => Assert.Equal(FanTagKind.StoppedStillStuck, Decide(enabled: false, stuckDuty: 45));

    [Fact]
    public void 自定义风扇关着_即便后端还说手动_也按固件控制说()
        => Assert.Equal(FanTagKind.Firmware, Decide(enabled: false, manual: true));

    // ---- 顺序 3：手动优先于"卡住" ----

    [Fact]
    public void 手动模式_曲线没跑_stuck是用户自己选的_说手动()
        => Assert.Equal(FanTagKind.Manual, Decide(manual: true, running: false, stuckDuty: 60));

    [Fact]
    public void 手动模式_但曲线居然在跑_按自动说()
        => Assert.Equal(FanTagKind.AutoRunning, Decide(manual: true, running: true, stuckDuty: 60));

    // ---- 自动档的四条 ----

    [Fact]
    public void 曲线在跑_带stuck也不显示卡住()
        => Assert.Equal(FanTagKind.AutoRunning, Decide(running: true, stuckDuty: 60));

    [Fact]
    public void 曲线没跑_EC里还钉着旧值_卡住()
        => Assert.Equal(FanTagKind.Stuck, Decide(running: false, stuckDuty: 45));

    [Fact]
    public void 曲线没跑_EC里也没有值_自动未运行()
        => Assert.Equal(FanTagKind.AutoIdle, Decide(running: false));

    [Fact]
    public void 典型现场_曲线关着交回成功_显示固件控制()
        => Assert.Equal(FanTagKind.Firmware, Decide(enabled: false, running: false, stuckDuty: null));
}
