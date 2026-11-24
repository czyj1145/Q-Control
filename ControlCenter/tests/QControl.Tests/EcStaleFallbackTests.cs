using System.Text.Json.Nodes;
using QControl.Core.Ec;

namespace QControl.Tests;

/// <summary>
/// EC 读失败那一拍沿用上次读数的规则（2026-11-19，"风扇圈突然变 0"）。
/// </summary>
public class EcStaleFallbackTests
{
    [Fact]
    public void 读失败帧补进上次转速和温度()
    {
        var prev = new JsonObject
        {
            ["cpu_temp"] = 61L,
            ["fan1_rpm"] = 2345L,
            ["fan2_rpm"] = 1980L,
        };
        var failed = new JsonObject { ["error"] = "WMI timeout" };

        var added = EcStaleFallback.Merge(failed, prev);

        Assert.True(added);
        Assert.Equal("WMI timeout", (string?)failed["error"]);
        Assert.Equal(61L, (long?)failed["cpu_temp"]);
        Assert.Equal(2345L, (long?)failed["fan1_rpm"]);
        Assert.Equal(1980L, (long?)failed["fan2_rpm"]);
    }

    [Fact]
    public void 这一拍已有的值不被上一拍覆盖()
    {
        var prev = new JsonObject { ["fan1_rpm"] = 2345L, ["cpu_temp"] = 61L };
        var failed = new JsonObject { ["fan1_rpm"] = 3000L };   // HWiNFO 刚补进来的

        EcStaleFallback.Merge(failed, prev);

        Assert.Equal(3000L, (long?)failed["fan1_rpm"]);
        Assert.Equal(61L, (long?)failed["cpu_temp"]);
    }

    [Fact]
    public void 诊断字段不继承()
    {
        var prev = new JsonObject
        {
            ["error"] = "十分钟前的错",
            ["raw"] = "00 00 00 00",
            ["sys_error"] = "old",
            ["sys_replay"] = true,
            ["sys_zero_frame"] = true,
            ["fan3_rpm"] = 1200L,
        };
        var failed = new JsonObject { ["error"] = "这一拍的错" };

        EcStaleFallback.Merge(failed, prev);

        Assert.Equal("这一拍的错", (string?)failed["error"]);
        Assert.Null(failed["raw"]);
        Assert.Null(failed["sys_error"]);
        Assert.Null(failed["sys_replay"]);
        Assert.Null(failed["sys_zero_frame"]);
        Assert.Equal(1200L, (long?)failed["fan3_rpm"]);
    }

    [Fact]
    public void 没有上次快照时什么都不做()
    {
        var failed = new JsonObject { ["error"] = "first frame" };

        Assert.False(EcStaleFallback.Merge(failed, null));
        Assert.Single(failed);
    }
}
