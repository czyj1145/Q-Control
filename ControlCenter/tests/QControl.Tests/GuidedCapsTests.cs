using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「这次没开原厂」时能力表的继承规则。
///
/// 为什么要有测试：这条规则决定一份**没有观察**的采集会不会把上一次确认过的能力抹掉。
/// 2026-11-17 真机踩过：先跑没开原厂的那次，草案的 `kb_zones` 从 3 变成 0、性能档变空，
/// 用户看到的却是"采集成功"。既不报错、也不影响读数，只是本机档案悄悄退化了。
/// </summary>
public class GuidedCapsTests
{
    private static JsonObject OldWithGuide() => new()
    {
        ["id"] = "quanta-nl5",
        ["caps"] = new JsonObject
        {
            ["kb_zones"] = 3,
            ["keyboard_rgb"] = true,
            ["perf_modes"] = new JsonArray("office", "game", "beast"),
            ["fan_manual"] = true,
            ["fan_curve"] = true,
        },
        ["presets_overrides"] = new JsonObject
        {
            ["perf"] = new JsonObject
            {
                ["modes"] = new JsonObject { ["office"] = new JsonObject { ["apmd"] = 2 } },
            },
        },
        ["ec"] = new JsonObject { ["fan_slots"] = new JsonArray("d0", "a", "d2") },
        ["adopt"] = new JsonObject
        {
            ["guide"] = new JsonObject
            {
                ["findings"] = new JsonArray(
                    new JsonObject { ["step"] = "perf_office", ["value"] = "APMD 2" },
                    new JsonObject { ["step"] = "keyboard", ["value"] = "分区地址 5/4/3" }),
            },
        },
    };

    private static JsonObject NewProfile() => new()
    {
        ["id"] = "quanta-nl5",
        ["caps"] = new JsonObject { ["kb_zones"] = 0 },
        ["ec"] = new JsonObject
        {
            ["layout"] = new JsonObject { ["cpu_temp"] = new JsonArray(4, 4) },
            ["fan_slots"] = new JsonArray("d0", "a", "d2"),
        },
    };

    [Fact]
    public void 没开原厂时沿用上次的能力表()
    {
        var profile = NewProfile();

        Assert.True(GuidedCaps.Inherit(profile, OldWithGuide()));

        Assert.Equal(3, profile["caps"]!["kb_zones"]!.GetValue<int>());
        Assert.True(profile["caps"]!["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(3, profile["caps"]!["perf_modes"]!.AsArray().Count);
        Assert.NotNull(profile["presets_overrides"]!["perf"]!["modes"]!["office"]!["apmd"]);
        // layout 是本次实测的，不许被旧草案覆盖
        Assert.Equal(4, profile["ec"]!["layout"]!["cpu_temp"]![0]!.GetValue<int>());
    }

    [Fact]
    public void 旧草案没有更强的东西时不搬()
    {
        // 旧草案也是一份"什么都没确认"的（caps 全 0 / 无预设 / 无布局）⇒ 没有可取的强项，
        // 一个字节都不动。判据不是"旧档案带没带过引导"，而是"它有没有比新档案更强的东西"。
        var old = new JsonObject
        {
            ["caps"] = new JsonObject { ["kb_zones"] = 0, ["keyboard_rgb"] = false, ["perf_modes"] = new JsonArray() },
            ["adopt"] = new JsonObject
            {
                ["guide"] = new JsonObject
                {
                    ["findings"] = new JsonArray(new JsonObject { ["step"] = "perf_office", ["value"] = null }),
                },
            },
        };

        var profile = NewProfile();

        Assert.False(GuidedCaps.Inherit(profile, old));
        Assert.Equal(0, profile["caps"]!["kb_zones"]!.GetValue<int>());
    }

    [Fact]
    public void 没有旧草案时什么都不动()
    {
        var profile = NewProfile();

        Assert.False(GuidedCaps.Inherit(profile, null));
        Assert.Equal(0, profile["caps"]!["kb_zones"]!.GetValue<int>());
    }

    [Fact]
    public void 搬过来的是副本改旧的不会连带改新的()
    {
        // JsonNode 单父节点：直接搬会抛「node already has a parent」，所以必须 DeepClone。
        var old = OldWithGuide();
        var profile = NewProfile();

        Assert.True(GuidedCaps.Inherit(profile, old));

        old["caps"]!["kb_zones"] = 1;
        Assert.Equal(3, profile["caps"]!["kb_zones"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------ 只升不降
    //
    // 2026-11-22 真机：19:04 确认了「性能三档 + 背光 3 区 + 显卡」，19:06 又跑一次引导、
    // 只切了显卡 ⇒ caps 被整块重写成 `perf_modes=[]` / `kb_zones=0`；22:53 再"沿用"这份残表，
    // 草案里只剩显卡模式，而 txt 正文还写着"性能档写入 3 次读回一致"。下面两条把口径钉死。

    /// <summary>19:06 那种"引导跑了、但只确认了显卡"的 caps。</summary>
    private static JsonObject ResidualProfile() => new()
    {
        ["id"] = "quanta-nl5",
        ["caps"] = new JsonObject
        {
            ["perf_modes"] = new JsonArray(),
            ["kb_zones"] = 0,
            ["keyboard_rgb"] = false,
            ["fan_manual"] = false,
            ["fan_curve"] = false,
            ["gfx_switch"] = true,
        },
    };

    [Fact]
    public void 引导只确认了显卡时保留上次确认过的三档与分区()
    {
        var profile = ResidualProfile();

        var kept = GuidedCaps.Merge(profile, OldWithGuide());

        Assert.Contains("性能三档", kept);
        Assert.Equal(3, profile["caps"]!["perf_modes"]!.AsArray().Count);
        Assert.Equal(3, profile["caps"]!["kb_zones"]!.GetValue<int>());
        Assert.True(profile["caps"]!["keyboard_rgb"]!.GetValue<bool>());
        Assert.True(profile["caps"]!["fan_manual"]!.GetValue<bool>());
        Assert.True(profile["caps"]!["fan_curve"]!.GetValue<bool>());
        Assert.True(profile["caps"]!["gfx_switch"]!.GetValue<bool>());   // 本次确认的，不许被旧表盖回 false
        // 三档 APMD 的映射也要回来，否则界面按钮点下去写的是别的机器的默认值
        Assert.NotNull(profile["presets_overrides"]!["perf"]!["modes"]!["office"]!["apmd"]);
    }

    [Fact]
    public void 新表比旧表强时不被旧残表拉低()
    {
        var profile = new JsonObject
        {
            ["caps"] = new JsonObject
            {
                ["perf_modes"] = new JsonArray("office", "game", "beast"),
                ["kb_zones"] = 3,
                ["keyboard_rgb"] = true,
                ["fan_manual"] = true,
                ["fan_curve"] = true,
            },
        };
        var old = OldWithGuide();
        old["caps"]!["kb_zones"] = 0;
        old["caps"]!["perf_modes"] = new JsonArray();
        old["caps"]!["keyboard_rgb"] = false;
        old["caps"]!["fan_manual"] = false;
        old["caps"]!["fan_curve"] = false;

        var kept = GuidedCaps.Merge(profile, old);

        // 旧表在这四项上比新表弱 ⇒ 一项都不许"赢"。（预设块仍会按块补，
        // 那不是 caps 的降级，见 `旧草案没有引导段也要搬它的预设与偏移`。）
        Assert.DoesNotContain("性能三档", kept);
        Assert.DoesNotContain("键盘背光", kept);
        Assert.DoesNotContain("手动风扇", kept);
        Assert.DoesNotContain("风扇曲线", kept);
        Assert.Equal(3, profile["caps"]!["perf_modes"]!.AsArray().Count);
        Assert.Equal(3, profile["caps"]!["kb_zones"]!.GetValue<int>());
        Assert.True(profile["caps"]!["keyboard_rgb"]!.GetValue<bool>());
    }

    [Fact]
    public void 旧草案没有引导段也要搬它的预设与偏移()
    {
        // 2026-11-22 23:35 真机：上一份草案的 caps / presets 是 OemObservation 与手改出来的，
        // 根本没有 `adopt.guide` 段 —— 旧判据（必须带引导 findings）把它的三档 APMD 映射
        // 和背光档位偏移全挡在外面，新档案比旧的更差。
        var old = new JsonObject
        {
            ["caps"] = new JsonObject { ["kb_zones"] = 3, ["keyboard_rgb"] = true },
            ["presets_overrides"] = new JsonObject
            {
                ["perf"] = new JsonObject
                {
                    ["modes"] = new JsonObject { ["office"] = new JsonObject { ["apmd"] = 2 } },
                },
            },
            ["ec"] = new JsonObject
            {
                ["layout_verified"] = true,
                ["layout"] = new JsonObject { ["cpu_temp"] = new JsonArray(4, 4), ["backlight_level"] = new JsonArray(20, 4) },
            },
        };
        var profile = new JsonObject
        {
            ["caps"] = new JsonObject { ["kb_zones"] = 3, ["keyboard_rgb"] = true },
            ["ec"] = new JsonObject { ["layout"] = new JsonObject { ["cpu_temp"] = new JsonArray(4, 4) } },
            ["notes"] = new JsonArray(),
        };

        Assert.True(GuidedCaps.Inherit(profile, old));

        Assert.NotNull(profile["presets_overrides"]!["perf"]!["modes"]!["office"]!["apmd"]);
        Assert.Equal(20, profile["ec"]!["layout"]!["backlight_level"]![0]!.GetValue<int>());
        Assert.Equal(4, profile["ec"]!["layout"]!["cpu_temp"]![0]!.GetValue<int>());   // 本次认出的不被覆盖
        Assert.True(profile["ec"]!["layout_verified"]!.GetValue<bool>());             // 标定状态跟着旧表走
        Assert.Contains(profile["notes"]!.AsArray(), n => n!.ToString().Contains("读数偏移"));
    }

    [Fact]
    public void 本次认出的偏移不会被旧档案覆盖()
    {
        var old = new JsonObject
        {
            ["ec"] = new JsonObject { ["layout"] = new JsonObject { ["cpu_temp"] = new JsonArray(8, 4) } },
        };
        var profile = new JsonObject
        {
            ["ec"] = new JsonObject { ["layout"] = new JsonObject { ["cpu_temp"] = new JsonArray(4, 4) } },
        };

        Assert.False(GuidedCaps.Inherit(profile, old));
        Assert.Equal(4, profile["ec"]!["layout"]!["cpu_temp"]![0]!.GetValue<int>());
    }

    // ------------------------------------------------- 「没确认到」的说法要对上能力表

    /// <summary>
    /// 2026-11-23 真机：引导五步全被标成"这台没有这项"，AdoptGuide 写出
    /// "性能档一档都没抓到 ⇒ 性能页保持关闭"，而 caps 从上一份档案取强回来是三档齐的，
    /// 同一份 txt 里还写着"本次确认的能力：性能三档" —— 自己跟自己打架。
    /// 能力在 caps 里是开的，文案就必须说"照常开"。
    /// </summary>
    [Fact]
    public void 能力取强回来之后缺失说明不许再说保持关闭()
    {
        var caps = new JsonObject
        {
            ["perf_modes"] = new JsonArray("office", "game", "beast"),
            ["kb_zones"] = 3,
            ["keyboard_rgb"] = true,
            ["fan_manual"] = true,
            ["fan_curve"] = true,
        };

        var perf = GuidedCaps.ReconcileMissing(
            "性能档一档都没抓到（原厂没在观察里切档，或这台没有性能模式）⇒ 性能页保持关闭", caps);
        var kb = GuidedCaps.ReconcileMissing("键盘背光没抓到分区地址 ⇒ 背光页保持关闭", caps);
        var fan = GuidedCaps.ReconcileMissing("风扇没抓到手动转速命令 ⇒ 手动风扇与曲线保持关闭", caps);

        Assert.Contains("照常开", perf);
        Assert.DoesNotContain("保持关闭", perf);
        Assert.Contains("3 区", kb);
        Assert.Contains("照常开", kb);
        Assert.Contains("照常开", fan);
    }

    [Fact]
    public void 能力确实是关的时候缺失说明原样保留()
    {
        var caps = new JsonObject
        {
            ["perf_modes"] = new JsonArray(),      // 一档都没确认
            ["kb_zones"] = 0,
            ["keyboard_rgb"] = false,
            ["fan_manual"] = false,
        };

        const string perf = "性能档一档都没抓到 ⇒ 性能页保持关闭";
        const string kb = "键盘背光没抓到分区地址 ⇒ 背光页保持关闭";
        const string fan = "风扇没抓到手动转速命令 ⇒ 手动风扇与曲线保持关闭";

        Assert.Equal(perf, GuidedCaps.ReconcileMissing(perf, caps));
        Assert.Equal(kb, GuidedCaps.ReconcileMissing(kb, caps));
        Assert.Equal(fan, GuidedCaps.ReconcileMissing(fan, caps));
    }

    [Fact]
    public void 分区地址超过上限那条是真问题不许被校正成照常开()
    {
        // 抓到 4 个地址（混进 logo 灯）时 kb_zones 可能被后续逻辑抬回 3，
        // 但"超过上限"这条说的是**观察证据**有问题，不能因为页面开着就说没事。
        var caps = new JsonObject { ["kb_zones"] = 3, ["keyboard_rgb"] = true };
        const string m = "键盘背光抓到 4 个分区地址（3/4/7/8），超过界面能画的上限 3 ⇒ 先不开背光页"
                         + "（多半把 logo 灯 / 灯条也算成了键盘分区，需要作者核对）";

        Assert.Equal(m, GuidedCaps.ReconcileMissing(m, caps));
    }

    [Fact]
    public void 沿用旧风扇槽位时要记进沿用清单()
    {
        // 搬了 `ec.fan_slots` 却不记进 kept：报告就不会说"本次没确认到的项沿用上次的"，
        // 档案里静默带着上一轮的槽位表、回执一个字都不提（2026-11-23 审查）。
        var profile = new JsonObject
        {
            ["ec"] = new JsonObject(),        // 本次没抓到写帧
            ["caps"] = new JsonObject(),
        };
        var old = new JsonObject
        {
            ["ec"] = new JsonObject { ["fan_slots"] = new JsonArray("d0", "a", "d2") },
        };

        var kept = GuidedCaps.Merge(profile, old);

        Assert.Contains("风扇槽位", kept);
        Assert.Equal("d0", profile["ec"]!["fan_slots"]![0]!.GetValue<string>());
    }
}
