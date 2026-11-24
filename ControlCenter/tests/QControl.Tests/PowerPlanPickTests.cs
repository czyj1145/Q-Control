using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// 「同名电源计划挑哪一条」。
///
/// 这块的坑是**本机真的有两行「高性能」**（2026-11-03 用户在性能页看到的）：
/// `8c5e7fda…` 是 Windows 自带的，`a9932169…` 是原厂另建的。以前"遇到第一个同名就 break"，
/// 切到哪条完全取决于 `powercfg /list` 的输出顺序 —— 换一台机器、换一个系统语言就可能变。
///
/// 这里钉住三条：① 唯一命中直接用它；② 同名时优先"正在用的"；③ 都不在用就优先系统自带的。
/// </summary>
public class PowerPlanPickTests
{
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerfStock = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string HighPerfOem = "a9932169-f1fb-4dff-9034-fe1c4ca1886e";
    private const string Game = "653fbb7f-248b-41d0-aa49-03fabbfd5e8e";

    [Fact]
    public void 唯一命中时就用它()
    {
        (string? Guid, string? Name)[] plans =
        [
            (Balanced, "平衡"),
            (HighPerfStock, "高性能"),
        ];

        var guid = PowerPlanPick.PickByName(plans, "高性能", Balanced, out var matches);

        Assert.Equal(HighPerfStock, guid);
        Assert.Equal(1, matches);
    }

    [Fact]
    public void 同名时优先正在用的那条()
    {
        // 本机的真实形态：两条同名，正在用的是原厂那条
        (string? Guid, string? Name)[] plans =
        [
            (HighPerfOem, "高性能"),
            (HighPerfStock, "高性能"),
        ];

        var guid = PowerPlanPick.PickByName(plans, "高性能", HighPerfOem, out var matches);

        Assert.Equal(HighPerfOem, guid);
        Assert.Equal(2, matches);
    }

    [Fact]
    public void 同名且都不在用时优先系统自带的()
    {
        // 顺序故意把原厂那条放前面：结果不能受列表顺序影响
        (string? Guid, string? Name)[] plans =
        [
            (HighPerfOem, "高性能"),
            (HighPerfStock, "高性能"),
        ];

        var guid = PowerPlanPick.PickByName(plans, "高性能", Balanced, out var matches);

        Assert.Equal(HighPerfStock, guid);
        Assert.Equal(2, matches);
    }

    [Fact]
    public void 没有同名项时返回空()
    {
        (string? Guid, string? Name)[] plans =
        [
            (Balanced, "平衡"),
            (Game, "游戏"),
        ];

        Assert.Null(PowerPlanPick.PickByName(plans, "高性能", Balanced, out var matches));
        Assert.Equal(0, matches);
    }

    [Fact]
    public void 名字两端空白不影响匹配_坏GUID不参与挑选()
    {
        (string? Guid, string? Name)[] plans =
        [
            ("不是GUID", " 高性能 "),
            (HighPerfOem, "高性能"),
        ];

        var guid = PowerPlanPick.PickByName(plans, "高性能", Balanced, out var matches);

        // 坏 GUID 被剔掉 ⇒ 只剩一条候选，matches 记 1
        Assert.Equal(HighPerfOem, guid);
        Assert.Equal(1, matches);
    }

    [Fact]
    public void 空名字不报错()
    {
        Assert.Null(PowerPlanPick.PickByName([(HighPerfStock, "高性能")], "", null, out var m));
        Assert.Equal(0, m);

        Assert.Null(PowerPlanPick.PickByName([(HighPerfStock, "高性能")], null, null, out m));
        Assert.Equal(0, m);
    }

    [Fact]
    public void 标配GUID表覆盖四个系统计划()
    {
        Assert.True(PowerPlanPick.IsStock(Balanced));
        Assert.True(PowerPlanPick.IsStock(HighPerfStock));
        Assert.True(PowerPlanPick.IsStock("a1841308-3541-4fab-bc81-f71556f20b4a"));
        Assert.True(PowerPlanPick.IsStock("e9a42b02-d5df-448d-aa00-03f14749eb61"));

        // 原厂另建的那条不是标配 —— 这正是"同名时怎么分"的判据
        Assert.False(PowerPlanPick.IsStock(HighPerfOem));
        Assert.False(PowerPlanPick.IsStock(null));
    }
}
