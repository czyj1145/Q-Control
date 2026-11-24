using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Ec;

namespace QControl.Tests;

/// <summary>
/// 三风扇判定（`ec.fans_by_sku` + `0x0201` 的 SKU）与 SYS 路偏移表。
///
/// 为什么值得单测：这两个键都**只影响别人的机器**（本机两风扇，跑不出第三路），
/// 一旦写错，症状是"某台三风扇机型上 SYS 圈永远 0 转 / 那一路风扇不受控"，
/// 我们手上没有那台机器，只能靠这里把语义钉死。
///
/// 钉死四件事：
///   · 档案没写 `fans_by_sku` ⇒ SKU 探测**一个字都不改**（别的模具行为不变）；
///   · 命中了才改，且只认 1..4 路；键写 `0x33` / `51` 两种都认；
///   · `caps.fan_count` 以 `ec.fans` 为准 —— SKU 判出来的三路不能被一个写死的 2 盖掉；
///   · `sys_layout` 是**独立**的一张表，且 `sys_info` 没声明时 `SysSub == -1`。
/// </summary>
public class FanCountSkuTests
{
    private static JsonObject Profile(string ecJson) => new()
    {
        ["id"] = "test-" + Guid.NewGuid().ToString("N")[..6],
        ["name"] = "测试机型",
        ["verified"] = true,
        ["ec"] = JsonNode.Parse(ecJson),
        ["caps"] = new JsonObject { ["kb_zones"] = 0 },
    };

    private const string WithMap = """
    {
      "fans": 2,
      "fans_by_sku": { "0x33": 3, "51": 3, "0x34": 3, "0x35": 3 },
      "layout_verified": true,
      "layout": { "cpu_temp": [4, 4] },
      "sys_layout": { "sys_temp": [4, 1], "fan3_rpm": [8, 2] },
      "cmd": { "status": "0x0200", "sku": "0x0201", "sys_info": "0x0207" },
      "fan_slots": ["d0", "a", "d2"]
    }
    """;

    private const string WithoutMap = """
    {
      "fans": 2,
      "layout_verified": true,
      "layout": { "cpu_temp": [4, 4] },
      "cmd": { "status": "0x0200" }
    }
    """;

    [Fact]
    public void 没有SKU表时探到什么都不改()
    {
        var dev = new Device(Profile(WithoutMap));
        Assert.Equal(2, dev.Ec.Fans);
        Assert.Empty(dev.Ec.FansBySku);
        Assert.False(dev.Ec.ApplySku(0x33));           // 命中不了 → 不改
        Assert.Equal(2, dev.Ec.Fans);
    }

    [Theory]
    [InlineData(0x33u, 3)]
    [InlineData(0x34u, 3)]
    [InlineData(0x35u, 3)]                             // 原厂 daemon 的判据是 (sku-52)<=1 ⇒ 0x34/0x35
    [InlineData(0x32u, 2)]                             // 两风扇机型：表里没有 → 保持 2
    public void 命中SKU表就改风扇路数(uint sku, int expect)
    {
        var dev = new Device(Profile(WithMap));
        var changed = dev.Ec.ApplySku(sku);
        Assert.Equal(expect, dev.Ec.Fans);
        Assert.Equal(expect != 2, changed);
    }

    [Fact]
    public void 能力表的风扇路数跟着SKU走()
    {
        var dev = new Device(Profile(WithMap));
        Assert.Equal(2, dev.Caps()["fan_count"]!.GetValue<int>());

        dev.Ec.ApplySku(0x33);
        // ⚠️ 这里是最容易被写死值盖掉的地方：档案里没有 caps.fan_count，
        //    但即使有，也必须让位给 ec.fans（SKU 判出来的三路）。
        Assert.Equal(3, dev.Caps()["fan_count"]!.GetValue<int>());
    }

    [Fact]
    public void 档案写死的fan_count不会盖掉SKU判定()
    {
        var raw = Profile(WithMap);
        raw["caps"]!["fan_count"] = 2;                 // 采集包曾经写死过 2
        var dev = new Device(raw);

        dev.Ec.ApplySku(0x34);
        Assert.Equal(3, dev.Caps()["fan_count"]!.GetValue<int>());
    }

    [Fact]
    public void SYS读数是一张独立的偏移表()
    {
        var dev = new Device(Profile(WithMap));
        Assert.Equal(0x0207, dev.Ec.SysSub);
        // SYS 的偏移和主 layout 是两回事：主表里 4 是 CPU 温度（4 字节），
        // SYS 表里 4 是 SYS 温度（1 字节）—— 混用会读成别的寄存器。
        Assert.Equal(4, dev.Ec.SysLayout["sys_temp"].Offset);
        Assert.Equal(1, dev.Ec.SysLayout["sys_temp"].Length);
        Assert.Equal(8, dev.Ec.SysLayout["fan3_rpm"].Offset);
        Assert.Equal(2, dev.Ec.SysLayout["fan3_rpm"].Length);
        Assert.Equal(4, dev.Ec.Layout["cpu_temp"].Length);
    }

    [Fact]
    public void 没声明sys_info就没有SYS子命令()
    {
        var dev = new Device(Profile(WithoutMap));
        Assert.Equal(-1, dev.Ec.SysSub);
        Assert.Empty(dev.Ec.SysLayout);
    }

    [Fact]
    public void 内嵌的NL5档案仍然按两风扇跑()
    {
        // 本机 SKU 不是 0x33/0x34，所以哪怕档案里带了 fans_by_sku，
        // 也必须是两风扇 —— 这是"改别人的机型不能顺手改到自己身上"的防线。
        var raw = DeviceResolver.AllProfiles.First(p => p.Id == "quanta-nl5").Raw;
        var dev = new Device(raw);

        Assert.Equal(2, dev.Ec.Fans);
        Assert.False(dev.Ec.ApplySku(0x00));           // 读不到 SKU 时的兜底值
        Assert.Equal(2, dev.Ec.Fans);
        Assert.Equal(2, dev.Caps()["fan_count"]!.GetValue<int>());
    }

    [Fact]
    public void 能判三风扇的档案必须声明SYS读数()
    {
        // 判出三路却不声明 `cmd.sys_info` / `sys_layout` ⇒ 那一路永远没读数，
        // 用户看到的是"三风扇但 SYS 圈一直 --"。这条防的是**漏写**（2026-11-18 通用档案就是漏的）。
        // 偏移口径来自 FanControlPortable v3 的 ReadSysInfo：a2=温度(1B) / a3=转速(2B)。
        foreach (var profile in DeviceResolver.AllProfiles)
        {
            var dev = new Device(profile.Raw);
            if (!dev.Ec.FansBySku.Values.Any(n => n >= 3)) continue;

            Assert.True(dev.Ec.SysSub >= 0, $"{profile.Id}：三风扇 SKU 已声明，但缺 ec.cmd.sys_info");
            Assert.True(dev.Ec.SysLayout.TryGetValue("sys_temp", out var t),
                $"{profile.Id}：缺 sys_layout.sys_temp");
            Assert.Equal((4, 1), t);
            Assert.True(dev.Ec.SysLayout.TryGetValue("fan3_rpm", out var r),
                $"{profile.Id}：缺 sys_layout.fan3_rpm");
            Assert.Equal((8, 2), r);
        }
    }

    // ================================================================ 运行时三风扇判定
    //
    // 这一段只影响**别人的机器**：三风扇机（猎刃16 等）我们手上没有，验不了。
    // 所以判据必须一条条钉死在假时钟上，尤其是"两风扇机上永远不升级"这条 ——
    // 判错了就是无中生有给用户画一个不存在的风扇。

    [Fact]
    public void 两风扇机的a3恒0时永远不升级()
    {
        // 本机实测就是这种形态：0x0207 回 `00 00 00 00 37 00 00 00 00…`，a3 = 0。
        var probe = new ThirdFanProbe();
        var now = 1000.0;
        for (var i = 0; i < 500; i++)
        {
            now += probe.IntervalSeconds;
            Assert.Equal(ThirdFanVerdict.Waiting, probe.Observe(now, 55, 0));
        }
        Assert.Equal(0, probe.Hits);
        Assert.True(probe.Pending);
    }

    [Fact]
    public void 连续两次可信转速才升级()
    {
        var probe = new ThirdFanProbe();
        Assert.Equal(ThirdFanVerdict.Waiting, probe.Observe(0, 55, 2400));   // 第一次不够
        Assert.Equal(ThirdFanVerdict.HasThirdFan, probe.Observe(30, 56, 2500));
        Assert.False(probe.Pending);                                        // 判出来就不再采
        Assert.False(probe.ShouldSample(10_000));
    }

    [Fact]
    public void 中间夹一次不可信就重新计数()
    {
        var probe = new ThirdFanProbe();
        probe.Observe(0, 55, 2400);
        probe.Observe(30, 55, 0);          // 风扇停转 / 脏值
        Assert.Equal(0, probe.Hits);
        Assert.Equal(ThirdFanVerdict.Waiting, probe.Observe(60, 55, 2400));
        Assert.Equal(ThirdFanVerdict.HasThirdFan, probe.Observe(90, 55, 2400));
    }

    [Theory]
    [InlineData(55, 299)]        // 低于下限：当"停转/没这路"
    [InlineData(55, 8001)]       // 越上限：脏值
    [InlineData(126, 2400)]      // 温度越界：整帧不可信
    [InlineData(55, 0)]
    public void 越界读数不算证据(long temp, long rpm)
    {
        Assert.False(ThirdFanProbe.LooksLikeThirdFan(temp, rpm));
    }

    [Theory]
    [InlineData(55, 300)]
    [InlineData(92, 8000)]
    [InlineData(0, 1200)]        // 温度 0 也算（传感器未就绪不该挡转速）
    public void 量程内的转速算证据(long temp, long rpm)
    {
        Assert.True(ThirdFanProbe.LooksLikeThirdFan(temp, rpm));
    }

    [Fact]
    public void 采样节奏先密后疏()
    {
        var probe = new ThirdFanProbe();
        var now = 0.0;
        Assert.True(probe.ShouldSample(now));
        for (var i = 0; i < 20; i++)
        {
            now += probe.IntervalSeconds;
            Assert.True(probe.ShouldSample(now));
            probe.Observe(now, 55, 0);
        }
        Assert.Equal(300, probe.IntervalSeconds);      // 20 次之后放慢到 5 min
    }

    [Fact]
    public void 两风扇机a2镜像GPU温度时探测降速到30分钟()
    {
        // 本机实测形态：a2 逐拍等于 GPU 温度、a3=0（52/52、53/53，一拍不差）。
        var probe = new ThirdFanProbe();
        var now = 0.0;
        for (var i = 0; i < ThirdFanProbe.QuietAfterMirrors; i++)
        {
            probe.Observe(now, 52 + i, 0, 52 + i);      // 差 0 度
            now += probe.IntervalSeconds;
        }
        Assert.True(probe.Quiet);
        Assert.Equal(ThirdFanProbe.QuietIntervalSeconds, probe.IntervalSeconds);
        Assert.Equal(1800, probe.IntervalSeconds);

        // 降速后仍然会采（只是慢），不是停掉
        Assert.False(probe.ShouldSample(now - 60));
        Assert.True(probe.ShouldSample(now + 1800));
        Assert.True(probe.Pending);
    }

    [Fact]
    public void 降速后温度偏离GPU立刻恢复密集采样()
    {
        // 三风扇机上 SYS 有独立传感器，迟早会偏离 GPU —— 那一刻必须回到 30 s。
        var probe = new ThirdFanProbe();
        var now = 0.0;
        for (var i = 0; i < ThirdFanProbe.QuietAfterMirrors; i++)
        {
            probe.Observe(now, 60, 0, 60);
            now += probe.IntervalSeconds;
        }
        Assert.True(probe.Quiet);

        probe.Observe(now, 66, 0, 55);                  // 偏了 11 度
        Assert.False(probe.Quiet);
        Assert.Equal(30, probe.IntervalSeconds);
    }

    [Fact]
    public void 降速不是判决_读到转速照样升级()
    {
        var probe = new ThirdFanProbe();
        var now = 0.0;
        for (var i = 0; i < ThirdFanProbe.QuietAfterMirrors; i++)
        {
            probe.Observe(now, 45, 0, 45);
            now += probe.IntervalSeconds;
        }
        Assert.True(probe.Quiet);

        // 风扇起转：第一拍只是打断降速，第二拍才升级
        Assert.Equal(ThirdFanVerdict.Waiting, probe.Observe(now, 45, 2100, 45));
        Assert.False(probe.Quiet);
        Assert.Equal(ThirdFanVerdict.HasThirdFan, probe.Observe(now + 30, 46, 2200, 46));
    }

    [Fact]
    public void 没有GPU温度可对比时不降速()
    {
        // 布局未标定的机型连 GPU 温度都没有 ⇒ 只按 30 s / 5 min 慢慢探，不做镜像推断。
        var probe = new ThirdFanProbe();
        var now = 0.0;
        for (var i = 0; i < 50; i++)
        {
            probe.Observe(now, 55, 0);                  // 不传 gpuTempC
            now += probe.IntervalSeconds;
        }
        Assert.False(probe.Quiet);
        Assert.Equal(0, probe.MirrorHits);
    }

    [Fact]
    public void 差一度以内算镜像_差两度不算()
    {
        var near = new ThirdFanProbe();
        near.Observe(0, 55, 0, 54);                     // 容差内
        Assert.Equal(1, near.MirrorHits);

        var far = new ThirdFanProbe();
        far.Observe(0, 55, 0, 53);                      // 超容差
        Assert.Equal(0, far.MirrorHits);
    }

    [Fact]
    public void 档案默认按能不能读SYS来开探测器()
    {
        Assert.True(new Device(Profile(WithMap)).Ec.SysProbe);          // 有 sys_info + sys_layout
        Assert.False(new Device(Profile(WithoutMap)).Ec.SysProbe);      // 通用档案：不探

        var off = Profile(WithMap.Replace("\"fans\": 2,", "\"fans\": 2, \"sys_probe\": false,"));
        Assert.False(new Device(off).Ec.SysProbe);                      // 档案显式关掉
    }

    [Fact]
    public void 实测升级后能力表立刻变成三路()
    {
        var dev = new Device(Profile(WithMap));
        Assert.Equal(2, dev.Caps()["fan_count"]!.GetValue<int>());
        Assert.Equal("profile", dev.Ec.FansSource);

        Assert.True(dev.Ec.RaiseFans(3, "sys_probe"));
        Assert.Equal(3, dev.Caps()["fan_count"]!.GetValue<int>());      // 界面据此长出 SYS 行
        Assert.Equal("sys_probe", dev.Ec.FansSource);
        Assert.Equal(3, dev.Ec.View()["fans"]!.GetValue<int>());        // /api/device 同步
        Assert.Equal("sys_probe", dev.Ec.View()["fans_source"]!.GetValue<string>());
    }

    [Fact]
    public void 风扇路数只升不降()
    {
        var dev = new Device(Profile(WithMap));
        dev.Ec.ApplySku(0x33);                                          // SKU 判成 3 路
        Assert.Equal(3, dev.Ec.Fans);

        // 探测器某一拍没读到转速，也不许把用户的风扇行拆掉
        Assert.False(dev.Ec.RaiseFans(3, "sys_probe"));
        Assert.False(dev.Ec.RaiseFans(2, "sys_probe"));
        Assert.Equal(3, dev.Ec.Fans);
        Assert.Equal("sku", dev.Ec.FansSource);                         // 来源不被覆盖
    }
}
