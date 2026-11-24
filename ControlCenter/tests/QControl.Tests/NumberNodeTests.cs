using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// 配置里的数字：**什么装箱类型都得读得出来，一个异常都不许抛**。
///
/// 回归的是 2026-11-15 真机上的一条断链：一键适配用
/// `Model.Set(["perf","modes",key,"apmd"], JsonValue.Create(apmd))` 把 APMD 写进
/// 内存里的 model（Int32 装箱），而 `Bounds.TryToDouble` 当时用的是
/// `JsonValue.GetValue&lt;double&gt;()` —— 跨类型直接抛
/// `InvalidOperationException`。于是那一拍采样读档位就抛，`status.perf` 一直是 null，
/// 性能页的「系统模式」永远停在"读取中…"，点切换也只回一句看不懂的类型错误，
/// 重启才恢复。用户报的"有时候系统模式半天都读不到"就是这条。
///
/// 所以这里不测"能不能转"，测的是**同一个数字的六种装箱形态结果一致**。
/// </summary>
public class NumberNodeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-7)]
    public void 整数装箱的节点也能转成double(int value)
    {
        // 这一条就是真机上炸的那个形态：JsonValue.Create(int) ⇒ Int32 装箱
        var node = JsonValue.Create(value);

        Assert.True(Bounds.TryToDouble(node, out var got));
        Assert.Equal(value, got);
    }

    [Fact]
    public void 各种数值装箱形态结果一致()
    {
        var cases = new (JsonNode? Node, double Want)[]
        {
            (JsonValue.Create(0), 0),                       // Int32   ← 真机踩到的那个
            (JsonValue.Create(0u), 0),                      // UInt32
            (JsonValue.Create(0L), 0),                      // Int64
            (JsonValue.Create(0UL), 0),                     // UInt64
            (JsonValue.Create(0.0), 0),                     // Double
            (JsonValue.Create(0f), 0),                      // Single
            (JsonValue.Create(0m), 0),                      // Decimal
            (JsonValue.Create((byte)0), 0),                 // Byte
            (JsonValue.Create((short)0), 0),                // Int16
            (JsonNode.Parse("0"), 0),                       // 文件读进来的 JsonElement
            (JsonNode.Parse("""{"apmd":0}""")!["apmd"], 0), // 同上，二级节点
            (JsonValue.Create("2"), 2),                     // 数字字符串
            (JsonValue.Create(true), 1),
            (JsonValue.Create(false), 0),
        };

        foreach (var (node, want) in cases)
        {
            Assert.True(Bounds.TryToDouble(node, out var got),
                $"转不出来：{node?.ToJsonString() ?? "null"}（{node?.GetType().Name ?? "null"}）");
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void 小数形态不被整数分支截断()
    {
        Assert.True(Bounds.TryToDouble(JsonValue.Create(0.45), out var gap));
        Assert.Equal(0.45, gap, 6);

        // 从文件读进来的 0.45 走的是 JsonElement 分支，两条路必须给同一个数
        var fromFile = JsonNode.Parse("""{"gap":0.45}""")!["gap"];
        Assert.True(Bounds.TryToDouble(fromFile, out var gap2));
        Assert.Equal(0.45, gap2, 6);
    }

    [Fact]
    public void 非数字照旧转不出来而不是抛()
    {
        Assert.False(Bounds.TryToDouble(null, out _));
        Assert.False(Bounds.TryToDouble("abc", out _));
        Assert.False(Bounds.TryToDouble(JsonNode.Parse("""{"a":1}"""), out _));
        Assert.False(Bounds.TryToDouble(JsonNode.Parse("[1,2]"), out _));
    }

    /// <summary>
    /// 端到端形状：把一键适配写出来的那段 JSON 原样喂进"档位键 → 模式名"的查找逻辑，
    /// 三种装箱形态（int / double / 文件解析）都必须认出 beast。
    /// 这里复刻 `EcHal.PerfModeKey` 的循环 —— 那个方法本身要连 WMI，单测构造不出来。
    /// </summary>
    [Fact]
    public void 一键适配写出来的int档位能认回模式名()
    {
        var modes = new JsonObject
        {
            ["office"] = new JsonObject { ["apmd"] = 2 },              // Int32（适配真的这么写）
            ["game"] = new JsonObject { ["apmd"] = JsonValue.Create(1.0) },  // Double（配置文件形态）
            ["beast"] = new JsonObject { ["apmd"] = JsonNode.Parse("0") },   // JsonElement
        };

        string? KeyOf(uint apmd)
        {
            foreach (var (k, v) in modes)
                if (v is JsonObject o && o["apmd"] is JsonNode n
                    && Bounds.TryToDouble(n, out var val) && (uint)val == apmd)
                    return k;
            return null;
        }

        Assert.Equal("office", KeyOf(2));
        Assert.Equal("game", KeyOf(1));
        Assert.Equal("beast", KeyOf(0));
        Assert.Null(KeyOf(9));
    }

    // ---------------------------------------------------------------- read_gap

    [Fact]
    public void 档案没写read_gap时退回min_gap_不替未知机型提速()
    {
        var spec = new EcSpec(JsonNode.Parse("""{"min_gap":0.2}"""));

        Assert.Equal(0.2, spec.MinGap, 6);
        // 读间隔与 min_gap 是两件事，但**没测过的机型**必须保持原节奏：
        // 只有档案显式写了 read_gap 才提速（见 EcSpec.ReadGap 的注释）。
        Assert.Equal(0.2, spec.ReadGap, 6);
    }

    [Fact]
    public void 档案写了read_gap就按档案来()
    {
        var spec = new EcSpec(JsonNode.Parse("""{"min_gap":0.2,"read_gap":0.08}"""));
        Assert.Equal(0.08, spec.ReadGap, 6);
    }

    // ---------------------------------------------------------------- burst_gap

    [Fact]
    public void 档案没写burst_gap时写批关闭_退回逐条写()
    {
        var spec = new EcSpec(JsonNode.Parse("""{"min_gap":0.2,"read_gap":0.05}"""));

        // 0 是"不支持连发"的哨兵值（EcHal 据此走老路径），不是"间隔 0 秒"——
        // 别的广达模具没测过 0x0100 内部的忙等，不能替它们把三条命令堆在一起。
        Assert.Equal(0, spec.BurstGap, 6);
    }

    [Fact]
    public void 档案写了burst_gap就按档案来()
    {
        var spec = new EcSpec(JsonNode.Parse("""{"min_gap":0.2,"burst_gap":"0.02"}"""));

        // 字符串也要吃（档案里数字常写成带引号的，见 NumDouble 的注释）
        Assert.Equal(0.02, spec.BurstGap, 6);
    }

    // ---------------------------------------------------------------- kb_trunk_addr

    [Fact]
    public void 档案没写整条地址时不给用_分区同色只能逐条写()
    {
        var spec = new EcSpec(JsonNode.Parse("{\"min_gap\":0.2}"));
        Assert.Null(spec.KbTrunkAddr);
        Assert.Null(spec.KbTrunkAddr);
    }

    [Fact]
    public void 整条地址按档案来_但0和越界一律不认()
    {
        Assert.Equal(6, new EcSpec(JsonNode.Parse("""{"kb_trunk_addr":6}""")).KbTrunkAddr);
        Assert.Equal(6, new EcSpec(JsonNode.Parse("{\"kb_trunk_addr\":\"6\"}")).KbTrunkAddr);

        // 0 会灭灯、>0xFF 没有意义（见 EcSpec.KbTrunkAddr 的 ⚠️）—— 都当"没标定"
        Assert.Null(new EcSpec(JsonNode.Parse("""{"kb_trunk_addr":0}""")).KbTrunkAddr);
        Assert.Null(new EcSpec(JsonNode.Parse("""{"kb_trunk_addr":300}""")).KbTrunkAddr);
    }
}
