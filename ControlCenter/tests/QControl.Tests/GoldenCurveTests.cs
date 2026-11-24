using QControl.Core.FanCurve;

namespace QControl.Tests;

/// <summary>
/// 风扇曲线插值：逐条比旧版 `FanCurve.interp` / `_norm_curve`。
///
/// 这段算术直接决定转速。插值偏一格，用户在 80°C 就会听到不该有的噪音，
/// 或者更糟 —— 转得不够。所以同样不写自证式断言，只对原实现算出的向量。
/// </summary>
public class GoldenCurveTests
{
    [Fact]
    public void Interp逐条对齐黄金样本()
    {
        var doc = Golden.Load("curve.json");
        var failures = new List<string>();
        var index = 0;

        foreach (var c in doc["interp"]!.AsArray())
        {
            index++;
            var curve = ReadCurve(c!["curve"]);
            var temp = c["temp"]!.GetValue<double>();
            var min = c["min"]!.GetValue<int>();
            var max = c["max"]!.GetValue<int>();
            var expected = c["expected"]!.GetValue<int>();

            var got = CurveInterpolator.Interp(curve, temp, min, max);
            if (got != expected)
            {
                var pts = string.Join(",", curve.Select(p => $"{p.T}/{p.D}"));
                failures.Add($"#{index} [{pts}] @{temp}°C min={min} max={max} "
                             + $"期望 {expected} 实得 {got}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{index} 条不一致：\n" + string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void Normalize逐条对齐黄金样本()
    {
        var doc = Golden.Load("curve.json");
        foreach (var c in doc["normalize"]!.AsArray())
        {
            var input = ReadCurve(c!["input"]);
            var expected = ReadCurve(c["expected"]);
            var got = CurveInterpolator.Normalize(input);

            Assert.Equal(expected.Count, got.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].T, got[i].T);
                Assert.Equal(expected[i].D, got[i].D);
            }
        }
    }

    [Theory]
    [InlineData(20, 30)]     // 低于首点 → 首点转速
    [InlineData(90, 100)]    // 高于末点 → 末点转速
    [InlineData(65, 65)]     // 40/30 → 90/100 的中点 = 65
    public void 关键插值点(double temp, int expected)
    {
        var curve = new[]
        {
            new CurvePoint(40, 30),
            new CurvePoint(90, 100),
        };
        Assert.Equal(expected, CurveInterpolator.Interp(curve, temp, 30, 100));
    }

    [Fact]
    public void 空曲线回落到下限而不是零()
    {
        // ⚠️ 这条是防"除零/空数组 → 0 转速"的：
        // 0 转速在笔记本上意味着风扇停转，而不是"安静"。
        Assert.Equal(30, CurveInterpolator.Interp(null, 70, 30, 100));
        Assert.Equal(30, CurveInterpolator.Interp([], 70, 30, 100));
        Assert.Equal(35, CurveInterpolator.Interp([], 70, 35, 90));
    }

    [Fact]
    public void 配置上下限优先于全局边界()
    {
        // 曲线点本身过的是全局 fan_duty(30~100)，但插值结果过的是配置的 min/max。
        var curve = new[] { new CurvePoint(40, 100) };
        // temp=10 低于首点 ⇒ 取首点转速 100，再按配置上限 90 钳住
        Assert.Equal(90, CurveInterpolator.Interp(curve, 10, 35, 90));
        // 配置下限 35 ⇒ 低于首点时不会被抬到 35，仍是（钳住的）首点值
        Assert.Equal(35, CurveInterpolator.Interp([new CurvePoint(40, 30)], 10, 35, 90));
        Assert.Equal(40, CurveInterpolator.Interp(curve, 100, 40, 40));   // min==max 时钉死
    }

    [Fact]
    public void 同温重复点不除零()
    {
        var curve = new[]
        {
            new CurvePoint(60, 40),
            new CurvePoint(60, 80),
        };
        var got = CurveInterpolator.Interp(curve, 60, 30, 100);
        Assert.InRange(got, 30, 100);
    }

    private static List<CurvePoint> ReadCurve(System.Text.Json.Nodes.JsonNode? node)
    {
        var list = new List<CurvePoint>();
        if (node is not System.Text.Json.Nodes.JsonArray arr) return list;
        foreach (var p in arr)
            list.Add(new CurvePoint(p!["t"]!.GetValue<double>(), p["d"]!.GetValue<int>()));
        return list;
    }
}
