using QControl.Core.FanCurve;

namespace QControl.Tests;

/// <summary>
/// 原厂 UserFan 双向算术的回归测试：原厂只认 30/40/…/100 八个温度，
/// 且 UserFanN 是 "CPU;GPU" 合并串、UserFanN_SYS 是单段串。
/// 不碰注册表 —— 注册表那层靠 IL 实证 + 真机验证。
/// </summary>
public sealed class OemFanCurveRoundTripTests
{
    [Fact]
    public void 按原厂8档温度采样并拼成UserFan合并串()
    {
        var cpu = OemFanCurve.Sample([new CurvePoint(30, 50), new CurvePoint(100, 50)], 30, 100);
        var gpu = OemFanCurve.Sample([new CurvePoint(30, 40), new CurvePoint(100, 40)], 30, 100);

        Assert.Equal([30, 40, 50, 60, 70, 80, 90, 100], OemFanCurve.Temperatures);
        Assert.Equal(8, cpu.Length);
        Assert.All(cpu, d => Assert.Equal(50, d));

        var text = OemFanCurve.UserFanString(cpu, gpu);
        var parts = text.Split(';');
        Assert.Equal(2, parts.Length);
        Assert.Equal(8, parts[0].Split(',').Length);
        Assert.Equal(8, parts[1].Split(',').Length);
        Assert.Equal(string.Join(",", Enumerable.Repeat("50", 8)), parts[0]);
        Assert.Equal(string.Join(",", Enumerable.Repeat("40", 8)), parts[1]);
    }

    [Fact]
    public void 空曲线在每档都给安全下限()
    {
        var duty = OemFanCurve.Sample(null, 35, 100);
        Assert.All(duty, d => Assert.Equal(35, d));
        Assert.Equal(string.Join(",", Enumerable.Repeat("35", 8)), OemFanCurve.SysFanString(duty));
    }

    [Fact]
    public void SYS没配曲线时逐档跟最热的一路()
    {
        var cpu = Enumerable.Repeat(40, 8).ToArray();
        var gpu = Enumerable.Repeat(65, 8).ToArray();
        Assert.Equal(Enumerable.Repeat(65, 8), OemFanCurve.FollowHotter(cpu, gpu));
    }

    [Fact]
    public void 解析UserFan合并串还原成三档曲线()
    {
        const string text = "30,35,40,50,60,70,85,100;25,30,40,45,55,65,80,95";
        Assert.True(OemFanCurve.TryParseUserFan(text, out var cpu, out var gpu, out var err), err);

        Assert.Equal([30, 35, 40, 50, 60, 70, 85, 100], cpu);
        Assert.Equal([25, 30, 40, 45, 55, 65, 80, 95], gpu);

        var points = OemFanCurve.ToCurve(cpu);
        Assert.Equal(8, points.Count);
        Assert.Equal(30, points[0].T);
        Assert.Equal(100, points[^1].T);
        Assert.Equal(85, points[6].D);
    }

    [Fact]
    public void 坏串缺段缺档或越界一律拒绝()
    {
        Assert.False(OemFanCurve.TryParseUserFan(null, out _, out _, out _));
        Assert.False(OemFanCurve.TryParseUserFan("", out _, out _, out _));
        Assert.False(OemFanCurve.TryParseUserFan("30,40", out _, out _, out _));                 // 不是两段
        Assert.False(OemFanCurve.TryParseUserFan("1,2,3;4,5,6", out _, out _, out _));           // 不足 8 档
        Assert.False(OemFanCurve.TryParseUserFan("30,35,40,50,60,70,85,101;1,2,3,4,5,6,7,8", out _, out _, out _)); // duty>100
        Assert.False(OemFanCurve.TryParseUserFan("30,35,40,50,60,70,85,x;1,2,3,4,5,6,7,8", out _, out _, out _));   // 非数字
    }

    [Fact]
    public void 全零表示原厂没配必须被识别出来()
    {
        Assert.True(OemFanCurve.AllZero([0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(OemFanCurve.AllZero([0, 0, 0, 0, 0, 0, 0, 30]));
    }

    [Fact]
    public void 采样再解析能还原原始占空比()
    {
        int[] original = [30, 40, 50, 60, 70, 80, 90, 100];
        var text = OemFanCurve.SysFanString(original);
        Assert.True(OemFanCurve.TryParseDuties(text, out var parsed, out var err), err);
        Assert.Equal(original, parsed);
    }
}
