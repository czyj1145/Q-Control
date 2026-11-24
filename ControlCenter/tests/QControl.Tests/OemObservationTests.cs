using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「观察原厂」抓到的写命令 → caps 推断。
///
/// 钉死三件事：
///   · 看到子命令就确定「有这能力」（keyboard_rgb / gfx_switch / fan_manual / fan_curve）；
///   · 数量类（分区数 / 路数 / APMD 值域）只进 summary，**不写死进 caps**；
///   · 读组帧 / 全 0 帧一律不当写命令。
/// </summary>
public class OemObservationTests
{
    // 组一条写命令：GWF0 恒 0xFB00，GWF1 = sub，fields 覆盖指定偏移。
    private static ObservedWrite W(ushort sub, params (int Off, byte Val)[] fields)
    {
        var raw = new byte[32];
        raw[0] = 0x00; raw[1] = 0xFB;                       // GWF0 = 0xFB00
        raw[2] = (byte)(sub & 0xFF); raw[3] = (byte)(sub >> 8);
        foreach (var (off, val) in fields) raw[off] = val;
        return new ObservedWrite(0xFB00, sub, raw);
    }

    [Fact]
    public void 看到子命令就确定有对应能力()
    {
        var r = OemObservation.Analyze([
            W(OemObservation.SubBacklight, (4, 4)),
            W(OemObservation.SubGfx, (4, 1)),
            W(OemObservation.SubFan, (8, 0x28), (9, 0x30)),
            W(OemObservation.SubOmcc),
        ]);

        Assert.True(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        Assert.True(r.Caps["fan_manual"]!.GetValue<bool>());
        Assert.True(r.Caps["fan_curve"]!.GetValue<bool>());
    }

    [Fact]
    public void 背光分区地址与档位灯效进摘要()
    {
        var r = OemObservation.Analyze([
            W(OemObservation.SubBacklight, (4, 3), (11, 0x12)),   // 地址 3，灯效 1、档位 2
            W(OemObservation.SubBacklight, (4, 4), (11, 0x10)),   // 地址 4，灯效 1、档位 0
            W(OemObservation.SubBacklight, (4, 5), (11, 0x02)),   // 地址 5，灯效 0、档位 2
        ]);

        var addrs = r.Summary["kb_zone_addrs"]!.AsArray();
        Assert.Equal([3, 4, 5], addrs.Select(x => x!.GetValue<int>()).ToArray());
        Assert.Equal([0, 2], r.Summary["kb_levels"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
        Assert.Equal([0, 1], r.Summary["kb_effects"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
    }

    [Fact]
    public void 背光全零地址不计入分区数()
    {
        var r = OemObservation.Analyze([W(OemObservation.SubBacklight, (4, 0), (11, 0x02))]);

        Assert.True(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Empty(r.Summary["kb_zone_addrs"]!.AsArray());
    }

    [Fact]
    public void 性能档APMD值域进摘要()
    {
        var r = OemObservation.Analyze([
            W(OemObservation.SubPerf, (4, 0)),
            W(OemObservation.SubPerf, (4, 1)),
            W(OemObservation.SubPerf, (4, 2)),
        ]);

        Assert.Equal([0, 1, 2], r.Summary["perf_apmd_values"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
        // 性能档的数值→名称映射不猜，所以 caps 里不该有 perf_modes
        Assert.False(r.Caps.ContainsKey("perf_modes"));
    }

    [Fact]
    public void 读组帧与全零帧一律不当写命令()
    {
        var r = OemObservation.Analyze([
            new ObservedWrite(0xFA00, 0x0200, new byte[32]),      // 读组（自己的 status 读）
            new ObservedWrite(0xFB00, 0x0000, new byte[32]),      // 全 0 清除帧
        ]);

        Assert.Empty(r.Caps);
        Assert.Empty(r.Summary["perf_apmd_values"]!.AsArray());
    }

    [Fact]
    public void 空输入不产生任何结论()
    {
        var r = OemObservation.Analyze([]);

        Assert.Empty(r.Caps);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Apply把结论并进档案且同一个节点不挂两处()
    {
        var profile = new JsonObject
        {
            ["caps"] = new JsonObject { ["kb_zones"] = 0 },
            ["adopt"] = new JsonObject { ["self_check"] = new JsonArray() },
        };
        var observation = new JsonObject
        {
            ["write_cmds"] = new JsonArray(new JsonObject { ["gwf1"] = "0x0100" }),
        };

        // 这条一旦回归成「同一个 Summary 节点挂两处」就会抛
        // The node already has a parent —— 那正是 11-10 用户报「验证失败」的真因。
        OemObservation.Apply(profile, observation,
        [
            W(OemObservation.SubBacklight, (4, 4)),
            W(OemObservation.SubGfx, (4, 1)),
        ]);

        Assert.True(profile["caps"]!["keyboard_rgb"]!.GetValue<bool>());
        Assert.True(profile["caps"]!["gfx_switch"]!.GetValue<bool>());

        // 报告顶层与档案 adopt 各持一份副本，互不共享节点
        Assert.NotNull(observation["summary"]);
        var adoptObs = profile["adopt"]!["observation"]!;
        Assert.NotNull(adoptObs["summary"]);
        Assert.NotNull(adoptObs["write_cmds"]);
        Assert.Equal(2, profile["adopt"]!["self_check"]!.AsArray().Count);
    }

    [Fact]
    public void Apply在档案没有adopt段时自己建一个()
    {
        var profile = new JsonObject { ["caps"] = new JsonObject() };
        var observation = new JsonObject { ["write_cmds"] = new JsonArray() };

        OemObservation.Apply(profile, observation, [W(OemObservation.SubFan, (8, 0x28))]);

        Assert.True(profile["caps"]!["fan_manual"]!.GetValue<bool>());
        Assert.NotNull(profile["adopt"]!["observation"]);
    }

    /// <summary>
    /// 2026-11-10 真机抓包（原厂 ControlCenter 切三档 + 调灯）的原始报文：
    /// 性能档 APMD 0/1/2、背光地址 3/5/6、DT3D 高 4 位灯效 / 低 4 位档位。
    /// 这条钉住"真实抓包能被解析成正确语义"，而不只是手搓的对称样本。
    /// </summary>
    [Fact]
    public void 真实抓包报文能解析出性能档与背光语义()
    {
        var r = OemObservation.Analyze([
            W(OemObservation.SubPerf, (4, 1)),          // 00 FB 00 03 01 游戏
            W(OemObservation.SubPerf, (4, 2)),          // 00 FB 00 03 02 办公
            W(OemObservation.SubPerf, (4, 0)),          // 00 FB 00 03 00 狂暴
            W(OemObservation.SubBacklight, (4, 6), (8, 0xE9), (9, 0xFF), (10, 0x05), (11, 0x11)),
            W(OemObservation.SubBacklight, (4, 3), (11, 0x32)),
            W(OemObservation.SubBacklight, (4, 5), (11, 0x12)),
        ]);

        Assert.True(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal([0, 1, 2], r.Summary["perf_apmd_values"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
        Assert.Equal([3, 5, 6], r.Summary["kb_zone_addrs"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
        // DT3D：0x11→灯效1/档1、0x32→灯效3/档2、0x12→灯效1/档2
        Assert.Equal([1, 3], r.Summary["kb_effects"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
        Assert.Equal([1, 2], r.Summary["kb_levels"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
    }
}
