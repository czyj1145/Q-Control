using System.Text.Json.Nodes;
using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// 出厂默认 presets 与深合并。
///
/// 这块的坑在于「共享引用」：JsonNode 是可变树，
/// `CloneDefaults()` 如果漏了 DeepClone，改一份配置会污染全局默认值 ——
/// 而且症状极隐蔽（第一次运行正常，恢复出厂之后就变成上次的值）。
/// </summary>
public class PresetsTests
{
    [Fact]
    public void 默认配置包含全部已知配置节()
    {
        // 这些节名是 /api/model/preset 的合法第一级 path 白名单，少一个就写不进配置。
        // 清单来自导出的 presets.default.json（不是我凭印象列的 —— 第一版就列错了
        // osd/app/hwmon/gpu/power，实际这五项都不在 DEFAULT_PRESETS 里）。
        // hwinfo 是传感器数据源三档（presets.hwinfo.mode）的节，与 DEFAULT_PRESETS 同步带入。
        var expected = new[]
        {
            "version", "fan", "perf", "backlight", "gfx", "ec", "ui", "vendor", "log", "hwinfo", "light",
        };
        foreach (var s in expected)
            Assert.True(PresetDefaults.HasSection(s), $"默认配置缺配置节 {s}");

        // 反向也钉死：多出来的节说明导出与预期脱节，同样要看见
        Assert.Equal(expected.Length, PresetDefaults.Sections.Count);
    }

    [Fact]
    public void 风扇默认值在安全区间内()
    {
        var fan = PresetDefaults.Defaults["fan"]!.AsObject();
        var minDuty = fan["min_duty"]!.GetValue<int>();
        var maxDuty = fan["max_duty"]!.GetValue<int>();

        // 下限低于 30 = 允许停转；上限低于 100 = 满转都到不了。
        // 这不是"风格问题"，是散热安全。
        Assert.InRange(minDuty, 30, 60);
        Assert.InRange(maxDuty, 70, 100);
        Assert.True(minDuty < maxDuty, "min_duty 必须小于 max_duty");
    }

    [Fact]
    public void CloneDefaults是深拷贝()
    {
        var a = PresetDefaults.CloneDefaults();
        var originalMin = PresetDefaults.Defaults["fan"]!["min_duty"]!.GetValue<int>();

        a["fan"]!["min_duty"] = originalMin + 5;
        a["fan"]!["curve"]!.AsArray().Clear();

        // 改副本绝不能碰到全局默认值
        Assert.Equal(originalMin, PresetDefaults.Defaults["fan"]!["min_duty"]!.GetValue<int>());
        Assert.NotEmpty(PresetDefaults.Defaults["fan"]!["curve"]!.AsArray());
    }

    [Fact]
    public void 两次Clone互不影响()
    {
        var a = PresetDefaults.CloneDefaults();
        var b = PresetDefaults.CloneDefaults();
        a["fan"]!["min_duty"] = 99;
        Assert.NotEqual(99, b["fan"]!["min_duty"]!.GetValue<int>());
    }

    [Fact]
    public void DeepMerge递归覆盖对象保留兄弟键()
    {
        var bases = new JsonObject
        {
            ["fan"] = new JsonObject { ["min_duty"] = 30, ["max_duty"] = 100 },
            ["perf"] = new JsonObject { ["mode"] = "office" },
        };
        var over = new JsonObject
        {
            ["fan"] = new JsonObject { ["min_duty"] = 40 },      // 只覆盖一个键
            ["ec"] = new JsonObject { ["enabled"] = true },      // 新增节
        };

        PresetDefaults.DeepMerge(bases, over);

        Assert.Equal(40, bases["fan"]!["min_duty"]!.GetValue<int>());   // 覆盖了
        Assert.Equal(100, bases["fan"]!["max_duty"]!.GetValue<int>());  // 兄弟键还在
        Assert.Equal("office", bases["perf"]!["mode"]!.GetValue<string>());
        Assert.True(bases.ContainsKey("ec"));                            // 新节加上了
    }

    [Fact]
    public void DeepMerge标量直接替换对象()
    {
        var bases = new JsonObject { ["fan"] = new JsonObject { ["min_duty"] = 30 } };
        var over = new JsonObject { ["fan"] = 42 };      // 标量覆盖对象
        PresetDefaults.DeepMerge(bases, over);
        Assert.Equal(42, bases["fan"]!.GetValue<int>());
    }

    [Fact]
    public void 出厂兜底曲线长度与数值都在边界内()
    {
        var fb = PresetDefaults.FactoryFallback;
        var curve = fb["curve"]?.AsArray() ?? fb["fan_curve"]!.AsArray();
        Assert.NotEmpty(curve);
        foreach (var n in curve)
        {
            var d = n!.GetValue<int>();
            Assert.InRange(d, 30, 100);
        }
    }
}
