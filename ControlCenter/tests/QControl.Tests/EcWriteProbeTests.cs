using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「采集里的写入验证」那几条纯逻辑：背光档位偏移的定位判据、该写哪几个 APMD、
/// 以及把"写进去读回来"证实的偏移并进 layout。
///
/// 为什么这些必须有测试：它们决定一份采集包里"写这条路通不通"的**结论**。
/// 判错的后果不是崩，而是让作者以为 0x0100 在这台机器上可用，
/// 或者反过来——明明写进去了却报"没对上"，于是那台机器永远开不了背光页。
/// </summary>
public class EcWriteProbeTests
{
    /// <summary>status 帧的 5 个 u32（偏移 → 值）。</summary>
    private static Dictionary<int, uint> Frame(uint cpuT, uint gpuT, uint fan1, uint fan2, uint light)
        => new()
        {
            [4] = cpuT,
            [8] = gpuT,
            [12] = fan1,
            [16] = fan2,
            [20] = light,
        };

    // ---------------------------------------------------------------- 背光定位

    [Fact]
    public void 只有档位跟着写进去的值变时能定位到那个偏移()
    {
        // 写前是 2 档；写 0 档 → 读回 0；写 2 档 → 读回 2。温度在这 1 秒里也动了（真实情况）
        var before = Frame(52, 46, 2100, 2200, 2);
        var afterLow = Frame(53, 46, 2110, 2200, 0);
        var afterHigh = Frame(54, 46, 2120, 2200, 2);

        var scan = EcWriteProbe.ScanLevel(before, afterLow, 0, afterHigh, 2);

        Assert.Equal(20, scan.Offset);
        Assert.Single(scan.Matched);
        Assert.Contains("status@20", scan.Why);
    }

    [Fact]
    public void 标准槽没对上时尾部槽兜底()
    {
        // 别的模具把背光档位放在 @24（DAT7）：标准槽 @4~@20 一个都没跟着变。
        // 早先只看标准槽 ⇒ 这台永远"没在任何偏移上读回"，背光页也就永远开不了。
        var before = new Dictionary<int, uint> { [4] = 52, [8] = 46, [12] = 2100, [16] = 2200, [20] = 0, [24] = 2, [28] = 0 };
        var afterLow = new Dictionary<int, uint> { [4] = 53, [8] = 46, [12] = 2110, [16] = 2200, [20] = 0, [24] = 0, [28] = 0 };
        var afterHigh = new Dictionary<int, uint> { [4] = 54, [8] = 46, [12] = 2120, [16] = 2200, [20] = 0, [24] = 2, [28] = 0 };

        var scan = EcWriteProbe.ScanLevel(before, afterLow, 0, afterHigh, 2);

        Assert.Equal(24, scan.Offset);
        Assert.Single(scan.Matched);
        Assert.Contains("后半段", scan.Why);
    }

    [Fact]
    public void 标准槽对上时不去尾部槽找()
    {
        // @20 与 @24 都跟着变：只认标准槽 —— 那是本机与多数模具验证过的放法，
        // 尾部那一遍只做兜底，不能把标准槽的结论挤成"分不清"。
        var before = new Dictionary<int, uint> { [4] = 52, [8] = 46, [12] = 2100, [16] = 2200, [20] = 2, [24] = 2, [28] = 0 };
        var afterLow = new Dictionary<int, uint> { [4] = 52, [8] = 46, [12] = 2100, [16] = 2200, [20] = 0, [24] = 0, [28] = 0 };
        var afterHigh = new Dictionary<int, uint> { [4] = 52, [8] = 46, [12] = 2100, [16] = 2200, [20] = 2, [24] = 2, [28] = 0 };

        var scan = EcWriteProbe.ScanLevel(before, afterLow, 0, afterHigh, 2);

        Assert.Equal(20, scan.Offset);
        Assert.Equal(new[] { 20 }, scan.Matched);
    }

    [Fact]
    public void 两个偏移都跟着变时不猜()
    {
        // 背光和高位一起变（比如灯效也跟着动）：分不清哪个是档位 ⇒ 一个都不填
        var before = Frame(52, 46, 2100, 2200, 2);
        var afterLow = Frame(53, 46, 2110, 2200, 0);
        var afterHigh = Frame(54, 46, 2120, 2200, 2);
        afterLow[4] = 0;          // 让偏移 4 的低字节也等于 0
        afterHigh[4] = 2;         // 并且等于 2

        var scan = EcWriteProbe.ScanLevel(before, afterLow, 0, afterHigh, 2);

        Assert.Null(scan.Offset);
        Assert.Equal(2, scan.Matched.Count);
        Assert.Contains("分不清", scan.Why);
    }

    [Fact]
    public void 写进去的档位一个偏移都没读回时如实说没对上()
    {
        // 0x0100 被 SW 选择子吃掉：状态帧里没有任何低字节是 0/2 的字段在变
        var before = Frame(52, 46, 2100, 2200, 2);
        var afterLow = Frame(53, 46, 2110, 2200, 2);
        var afterHigh = Frame(54, 46, 2120, 2200, 2);

        var scan = EcWriteProbe.ScanLevel(before, afterLow, 0, afterHigh, 2);

        Assert.Null(scan.Offset);
        Assert.Empty(scan.Matched);
        Assert.Contains("没在任何偏移上读回", scan.Why);
    }

    [Theory]
    [InlineData(0u, 0)]
    [InlineData(1u, 1)]
    [InlineData(2u, 2)]
    [InlineData(0x12u, 2)]      // (effect<<4)|level：本机实测就是这个形状
    [InlineData(0x10u, 0)]
    public void 从档位字段里抠出档位(uint raw, int level)
        => Assert.Equal(level, EcWriteProbe.LevelOf(raw));

    [Fact]
    public void 认不出的值不瞎猜档位()
    {
        // 写回去的时候靠这个值恢复原档；认错就等于把别人的灯写到别的档
        Assert.Null(EcWriteProbe.LevelOf(52));       // 温度
        Assert.Null(EcWriteProbe.LevelOf(2100));     // 转速
    }

    [Fact]
    public void 缺了写前那一帧也能判出来()
    {
        var afterLow = Frame(53, 46, 2110, 2200, 0);
        var afterHigh = Frame(54, 46, 2120, 2200, 2);

        var scan = EcWriteProbe.ScanLevel(new Dictionary<int, uint>(), afterLow, 0, afterHigh, 2);

        Assert.Equal(20, scan.Offset);
    }

    [Fact]
    public void 只比低字节整字里有别的高位也认得出()
    {
        // 实测 EC 把 (effect<<4)|level 塞在同一个字里，整字不可能等于 0/2
        var before = Frame(52, 46, 2100, 2200, 0x12);
        var afterLow = Frame(53, 46, 2110, 2200, 0x10);
        var afterHigh = Frame(54, 46, 2120, 2200, 0x12);

        var scan = EcWriteProbe.ScanLevel(before, afterLow, 0, afterHigh, 2);

        Assert.Equal(20, scan.Offset);
    }

    // ---------------------------------------------------------------- APMD 候选

    [Fact]
    public void 只写配置里已经在用的APMD值()
    {
        var list = EcWriteProbe.ApmdCandidates([2, 1, 0, 1]);

        Assert.Equal([0, 1, 2], list);       // 去重 + 升序
    }

    [Fact]
    public void 配置里没有就退回0到2()
    {
        Assert.Equal([0, 1, 2], EcWriteProbe.ApmdCandidates([null, null]));
        Assert.Equal([0, 1, 2], EcWriteProbe.ApmdCandidates([]));
    }

    [Fact]
    public void 越界值不写()
    {
        // 负数 / 大于 15 的都不是"配置里在用的值"，写出去等于拿别人的功耗墙做实验
        Assert.Equal([1], EcWriteProbe.ApmdCandidates([-1, 16, 1, 99]));
    }

    // ---------------------------------------------------------------- 并进 layout

    [Fact]
    public void 写探针认出的背光偏移并进layout并置verified()
    {
        // 采样期间键盘灯没动过 ⇒ 原来那一路认不出背光（layout 里没有 backlight_level）
        var advice = EcLayoutAdvisor.Advise([]);
        Assert.False(advice.Layout.ContainsKey("backlight_level"));

        var merged = EcLayoutAdvisor.WithBacklightProbe(advice, 20, 4, "写入 0/2 档后读回一致");

        Assert.Equal((20, 4), merged.Layout["backlight_level"]);
        Assert.True(merged.LayoutVerified);
        var guess = Assert.Single(merged.Guesses, g => g.Field == "backlight_level");
        Assert.Equal("high", guess.Confidence);
        Assert.Equal(20, guess.Offset);
        Assert.Contains(merged.Notes, n => n.Contains("写进去再读回来"));
    }

    [Fact]
    public void 并进layout时替换掉靠值域猜的那一条()
    {
        // 采样里那一路（值域 0~2）会先给一个 medium；写探针证实后只该留一条结论
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(new LayoutSample(20 + i, double.NaN, 45, new Dictionary<string, uint[]>(StringComparer.Ordinal)
            {
                ["status"] = [52u, 46u, 2100u, 2200u, (uint)(i % 3)],
            }));

        var advice = EcLayoutAdvisor.Advise(samples);
        Assert.Contains(advice.Guesses, g => g.Field == "backlight_level" && g.Confidence == "medium");

        var merged = EcLayoutAdvisor.WithBacklightProbe(advice, 20, 4, "写入 0/2 档后读回一致");

        var all = merged.Guesses.Where(g => g.Field == "backlight_level").ToList();
        Assert.Single(all);
        Assert.Equal("high", all[0].Confidence);
    }

    // ---------------------------------------------------------------- 档案草案里带上结论

    [Fact]
    public void 背光偏移会进到档案草案的layout里()
    {
        var advice = EcLayoutAdvisor.WithBacklightProbe(
            EcLayoutAdvisor.Advise([]), 20, 4, "写入 0/2 档后读回一致");

        var profile = DeviceProfileDraft.Build(
            new JsonObject { ["system_vendor"] = "ACME", ["system_product"] = "X1" },
            new JsonObject(),
            QControl.Core.Data.EmbeddedData.ReadObject("devices/quanta-gmwmi-generic.json"),
            advice, "0.1.0", samples: 0,
            refs: new JsonObject(),
            selfCheck: []);

        Assert.Equal(20, profile["ec"]!["layout"]!["backlight_level"]![0]!.GetValue<int>());
        Assert.True(profile["ec"]!["layout_verified"]!.GetValue<bool>());
        // 写入权限仍然恒关（写探针也不例外）
        Assert.False(profile["verified"]!.GetValue<bool>());
        Assert.False(profile["writes_allowed"]!.GetValue<bool>());
    }
}
