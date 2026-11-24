using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 猎刃 16 的 Logo 灯是**独立灯珠**，不是键盘分区；尾灯不存在。
/// 这两条规矩护的是 2026-11-17 那次事故：探针往 8 / 7 试色，分区表从 5/4/3 涨成五条，
/// 界面冒出 A~E 五个 tab、键盘按五等分画。现在只有档案明确声明的机型才认它们。
/// </summary>
public class ExtraLightsTests
{
    private static DeviceProfile Profile(string id)
        => DeviceResolver.AllProfiles.First(p => p.Id == id);

    [Fact]
    public void 猎刃16档案只声明了Logo灯_没有尾灯()
    {
        var dev = new Device(Profile("thunderobot-leiren16").Raw);

        Assert.Equal(8, dev.ExtraLightAddrs["logo"]);
        Assert.False(dev.ExtraLightAddrs.ContainsKey("tail"));
        Assert.Single(dev.ExtraLightAddrs);
    }

    [Fact]
    public void 家族通用档案没有额外灯珠()
    {
        var dev = new Device(Profile("quanta-nl5").Raw);

        Assert.Empty(dev.ExtraLightAddrs);
    }

    [Fact]
    public void 内置档案里没有任何机型声明尾灯()
    {
        foreach (var p in DeviceResolver.AllProfiles)
            Assert.False(new Device(p.Raw).ExtraLightAddrs.ContainsKey("tail"), p.Id);
    }

    [Fact]
    public void 猎刃16专用档案优先级高于家族档案()
    {
        var specific = Profile("thunderobot-leiren16");
        var family = Profile("quanta-nl5");

        Assert.True(specific.Priority > family.Priority);
    }

    [Fact]
    public void 只有雷神猎刃16命中专用档案()
    {
        var leiren = Profile("thunderobot-leiren16");
        var ident = new JsonObject
        {
            ["system_vendor"] = "雷神",
            ["system_product"] = "猎刃16",
        };

        Assert.NotNull(DeviceMatcher.Match(leiren, ident));
        Assert.Null(DeviceMatcher.Match(leiren,
            new JsonObject { ["system_vendor"] = "COLORFUL", ["system_product"] = "G16 Pro" }));
    }

    [Fact]
    public void 非法地址与野名字不会被认成灯珠()
    {
        var raw = Profile("thunderobot-leiren16").Raw.DeepClone().AsObject();
        var ec = (JsonObject)raw["ec"]!;
        ec["kb_extra_lights"] = new JsonObject
        {
            ["logo"] = 8,
            ["tail"] = 0,          // 0 = 整条灯，不是独立灯珠
            ["keyboard"] = 5,      // 野名字
        };

        var dev = new Device(raw);

        Assert.Equal(8, dev.ExtraLightAddrs["logo"]);
        Assert.False(dev.ExtraLightAddrs.ContainsKey("tail"));
        Assert.False(dev.ExtraLightAddrs.ContainsKey("keyboard"));
    }
}
