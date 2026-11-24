using QControl.Core.Power;

namespace QControl.Tests;

/// <summary>
/// 电源计划卡片的悬停提示。原先写在 <c>PerfView.PlanTip</c> 里，界面项目不进测试工程。
/// 这里钉的是**缺值时不写半句**：电池范围缺一个就整段不拼、散热缺一个就用「—」占位。
/// </summary>
public class PlanTooltipTests
{
    private static string? Build(
        string? name = "平衡",
        string? guid = "{1111}",
        bool dupName = false,
        int? minAc = null, int? maxAc = null, int? minDc = null, int? maxDc = null,
        int? coolAc = null, int? coolDc = null, int? brightAc = null, int? brightDc = null)
        => PlanTooltip.Build(new PlanTipInput(
            name, guid, dupName, minAc, maxAc, minDc, maxDc, coolAc, coolDc, brightAc, brightDc));

    [Fact]
    public void 只有名字时不给提示()
        => Assert.Null(Build(name: "平衡"));

    [Fact]
    public void 空名字也当没有内容()
        => Assert.Null(Build(name: "   "));

    [Fact]
    public void 处理器范围_交流与电池都给()
    {
        var tip = Build(minAc: 5, maxAc: 100, minDc: 5, maxDc: 80);
        Assert.Equal("平衡\n处理器 交流 5–100% · 电池 5–80%", tip);
    }

    [Fact]
    public void 电池范围缺一个就整段不写_不留半句()
    {
        Assert.Equal("平衡\n处理器 交流 5–100%", Build(minAc: 5, maxAc: 100, minDc: 5));
    }

    [Fact]
    public void 只有最大值没有最小值_整行不显示()
        => Assert.Null(Build(maxAc: 100));

    [Fact]
    public void 重名时补GUID()
    {
        var tip = Build(dupName: true, minAc: 5, maxAc: 100);
        Assert.Equal("平衡\n{1111}\n处理器 交流 5–100%", tip);
    }

    [Fact]
    public void 重名但GUID为空_不补()
    {
        var tip = Build(dupName: true, guid: "", minAc: 5, maxAc: 100);
        Assert.DoesNotContain("\n\n", tip);
        Assert.Equal(2, tip!.Split('\n').Length);
    }

    [Fact]
    public void 散热策略_被动主动与自定义值()
    {
        Assert.Contains("散热 交流 被动 · 电池 被动", Build(coolAc: 0, coolDc: 0));
        Assert.Contains("散热 交流 主动 · 电池 被动", Build(coolAc: 1, coolDc: 0));
        Assert.Contains("散热 交流 7 · 电池 主动", Build(coolAc: 7, coolDc: 1));
    }

    [Fact]
    public void 散热只读到一个_另一个用占位符()
        => Assert.Contains("散热 交流 — · 电池 主动", Build(coolDc: 1));

    [Fact]
    public void 散热两个都没读到_整行不显示()
        => Assert.Null(Build(name: "平衡", coolAc: null, coolDc: null));

    [Fact]
    public void 屏幕亮度_两个都有()
        => Assert.Contains("屏幕亮度 交流 50% · 电池 30%", Build(brightAc: 50, brightDc: 30));

    [Fact]
    public void 屏幕亮度_缺一个用占位_保留百分号()
        => Assert.Contains("屏幕亮度 交流 —% · 电池 40%", Build(brightDc: 40));

    [Fact]
    public void 全套信息按固定顺序排列()
    {
        var tip = Build(
            name: "游戏", dupName: true, guid: "{G}",
            minAc: 5, maxAc: 100, minDc: 5, maxDc: 80,
            coolAc: 1, coolDc: 0, brightAc: 60, brightDc: 40);

        var lines = tip!.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal("游戏", lines[0]);
        Assert.Equal("{G}", lines[1]);
        Assert.StartsWith("处理器 交流 5–100%", lines[2]);
        Assert.Equal("散热 交流 主动 · 电池 被动", lines[3]);
        Assert.Equal("屏幕亮度 交流 60% · 电池 40%", lines[4]);
    }
}
