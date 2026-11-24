using System.Globalization;
using System.Text.Json.Nodes;
using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// 数值边界：逐条比旧版 `model.clamp` 的结果。
///
/// 这是**安全边界**，不是普通工具函数 —— 一个数钳错了，风扇就可能停转。
/// 所以不写"我认为 30 是下限"这种自证式断言，而是拿原实现算出来的 206 条向量对。
/// </summary>
public class GoldenBoundsTests
{
    [Fact]
    public void Clamp逐条对齐边界()
    {
        var doc = Golden.Load("bounds.json");
        var failures = new List<string>();

        foreach (var c in doc["clamp"]!.AsArray())
        {
            var key = c!["key"]!.GetValue<string>();
            var value = c["value"]!.GetValue<double>();
            var expected = c["expected"]!.GetValue<int>();

            var got = Bounds.Clamp(key, value);
            if (got != expected)
                failures.Add($"{key}({value.ToString(CultureInfo.InvariantCulture)}) "
                             + $"期望 {expected} 实得 {got}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} 条不一致：\n" + string.Join("\n", failures.Take(30)));
    }

    [Fact]
    public void 非数值一律抛OutOfBounds()
    {
        var doc = Golden.Load("bounds.json");
        foreach (var c in doc["errors"]!.AsArray())
        {
            var key = c!["key"]!.GetValue<string>();
            // no_such_key 是**刻意的差异**（旧版原样返回，C# 抛 KeyNotFound），
            // 单独在下面一条测试里断言，不参与这里比对。
            if (key == "no_such_key") continue;

            var raw = c["value"];
            Assert.Throws<OutOfBoundsException>(() => Bounds.ClampLoose(key, JsonToClr(raw)));
        }
    }

    [Fact]
    public void 未知边界键必须抛KeyNotFound而不是静默放过()
    {
        // 旧版对未知 key 原样返回 —— 它的弱类型允许这么做。
        // C# 侧刻意收紧：一个拼错的 key 会让风扇跑满而无人察觉，宁可炸。
        Assert.Throws<KeyNotFoundException>(() => Bounds.Clamp("no_such_key", 1));
        Assert.Throws<KeyNotFoundException>(() => Bounds.ClampLoose("fan_dutyy", 50));
    }

    [Fact]
    public void 宽松转换能吃掉字符串形式的数值()
    {
        // 前端/JSON 里 "45" 和 45 都要能用（"45" 也要能当 45 用）
        Assert.Equal(45, Bounds.ClampLoose("fan_duty", "45"));
        Assert.Equal(45, Bounds.ClampLoose("fan_duty", 45));
        Assert.Equal(45, Bounds.ClampLoose("fan_duty", 45.0));
        Assert.Equal(45, Bounds.ClampLoose("fan_duty", JsonValue.Create("45")));
        // 布尔按旧版语义 True=1 → 会被下限钳到 30
        Assert.Equal(30, Bounds.ClampLoose("fan_duty", true));
    }

    [Fact]
    public void 舍入必须是银行家舍入()
    {
        // 银行家舍入（round-half-to-even）：0.5→0、1.5→2、2.5→2、3.5→4。
        // 用 AwayFromZero 会让背光/转速与旧版差一档，这条测试就是防它。
        Assert.Equal(2, Bounds.Clamp("backlight_level", 1.5));   // 1.5 → 2
        Assert.Equal(2, Bounds.Clamp("backlight_level", 2.5));   // 2.5 → 2（再被上界钳）
        Assert.Equal(0, Bounds.Clamp("backlight_level", 0.5));   // 0.5 → 0
        // fan_duty 下限 30：3.5 先被钳到 30，再 round → 30
        Assert.Equal(30, Bounds.Clamp("fan_duty", 3.5));
        // 30.5 → 30（偶）/ 31.5 → 32（偶）—— 这两条能区分 ToEven 与 AwayFromZero
        Assert.Equal(30, Bounds.Clamp("fan_duty", 30.5));
        Assert.Equal(32, Bounds.Clamp("fan_duty", 31.5));
    }

    [Fact]
    public void 边界表覆盖全部十个键()
    {
        // 键少了意味着某个写操作失去了边界保护。清单在这里钉死。
        var expected = new[]
        {
            "fan_duty", "backlight_level", "backlight_rgb", "backlight_alpha",
            "gfx_mode", "hysteresis", "min_interval",
            "gpu_oc_core", "gpu_oc_mem", "temp_wall",
        };
        foreach (var k in expected)
            Assert.True(Bounds.All.ContainsKey(k), $"边界表缺 {k}");
        Assert.Equal(expected.Length, Bounds.All.Count);
    }

    private static object? JsonToClr(JsonNode? n)
    {
        if (n is null) return null;
        return n.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => n.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => n.GetValue<double>(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => null,
        };
    }
}
