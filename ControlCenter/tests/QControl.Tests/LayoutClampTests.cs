using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 机型档案里 `ec.layout` 的边界裁剪。
///
/// 钉死一件事：**越界项必须被夹到帧尾，不能整条照收**。
/// 旧实现只判 `off &lt; 32`，于是 `"cpu_temp": [30, 4]`（30+4=34）通过校验进了 layout；
/// 而 `EcFrame.U32(30)` 因 `offset + 4 &lt;= 32` 不成立**返回 0 且不抛** ——
/// 症状是"温度恒为 0、那一路风扇永远不写、界面还宣称布局已验证"，
/// 比崩溃难查得多（典型的手改档案 / 采集脚本写错一位）。
/// </summary>
public class LayoutClampTests
{
    private static Device Load(string layoutJson)
    {
        var raw = new JsonObject
        {
            ["id"] = "test-clamp",
            ["name"] = "边界裁剪",
            ["verified"] = true,
            ["ec"] = new JsonObject
            {
                ["layout_verified"] = true,
                ["layout"] = JsonNode.Parse(layoutJson),
                ["cmd"] = new JsonObject { ["status"] = "0x0200" },
            },
            ["caps"] = new JsonObject { ["kb_zones"] = 0 },
        };
        return new Device(raw);
    }

    [Fact]
    public void 越界长度被夹到帧尾()
    {
        var dev = Load("""{ "cpu_temp": [30, 4], "gpu_temp": [28, 4] }""");

        // 30 + 4 = 34 > 32 ⇒ 夹成 2 字节
        Assert.Equal(30, dev.Ec.Layout["cpu_temp"].Offset);
        Assert.Equal(2, dev.Ec.Layout["cpu_temp"].Length);
        // 正好到帧尾的项原样保留
        Assert.Equal(28, dev.Ec.Layout["gpu_temp"].Offset);
        Assert.Equal(4, dev.Ec.Layout["gpu_temp"].Length);
    }

    [Fact]
    public void 起点就在帧外或长度非法的项()
    {
        var dev = Load("""{ "a": [32, 2], "b": [-1, 2], "c": [0, 0], "d": [4, 4] }""");

        Assert.False(dev.Ec.Layout.ContainsKey("a"));    // off 必须 < 32
        Assert.False(dev.Ec.Layout.ContainsKey("b"));
        // c 的长度 0 不在 1..4 ⇒ 按 4 处理（既有语义），不丢项
        Assert.Equal(0, dev.Ec.Layout["c"].Offset);
        Assert.Equal(4, dev.Ec.Layout["c"].Length);
        Assert.Equal(4, dev.Ec.Layout["d"].Offset);
        Assert.Equal(4, dev.Ec.Layout["d"].Length);
    }
}
