using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 键盘分区表的合并规则。这条规则护的是用户的配置：一键采集的引导只能看到
/// 用户在原厂里点过的那一个分区，拿它替换整张表，三区键盘就被砍成一区
/// （2026-11-14 真机实测：`5/4/3` 变成只剩 `4`，背光页从 3 个 tab 变 1 个）。
/// </summary>
public class BacklightZonesTests
{
    private static JsonArray Zones(params (int Addr, string Name)[] items)
    {
        var arr = new JsonArray();
        foreach (var (addr, name) in items)
            arr.Add(new JsonObject { ["addr"] = addr, ["name"] = name, ["rgb"] = new JsonArray(1, 2, 3) });
        return arr;
    }

    [Fact]
    public void 已有的多区配置不会被一个区的观察结果覆盖()
    {
        var existing = Zones((5, "A"), (4, "B"), (3, "C"));
        var seen = Zones((4, "A"));

        var (zones, added) = BacklightZones.Merge(existing, seen);

        Assert.Equal(0, added);
        Assert.Equal(3, zones.Count);
        Assert.Equal([5, 4, 3], zones.Select(z => (int)z!["addr"]!).ToArray());
        // 已有条目的名字没被采集结果改写（采集只给了地址，不该动用户起的名字）
        Assert.Equal("B", (string?)zones[1]!["name"]);
    }

    [Fact]
    public void 新机型第一次采集时分区表会被写进去()
    {
        var (zones, added) = BacklightZones.Merge(null, Zones((5, "A")));

        Assert.Equal(1, added);
        Assert.Single(zones);
        Assert.Equal(5, (int)zones[0]!["addr"]!);
    }

    [Fact]
    public void 采集到的新地址追加在末尾且不改动原有顺序()
    {
        var existing = Zones((5, "A"), (4, "B"));
        var seen = Zones((4, "A"), (6, "C"));

        var (zones, added) = BacklightZones.Merge(existing, seen);

        Assert.Equal(1, added);
        Assert.Equal([5, 4, 6], zones.Select(z => (int)z!["addr"]!).ToArray());
    }

    [Fact]
    public void 合并不是直接搬引用源数组还能接着用()
    {
        // JsonNode 只能有一个父节点：Merge 里若直接把 existing 的元素 Add 进新数组，
        // 第二句就会抛「node already has a parent」—— 整次采集会因为这个失败。
        var existing = Zones((5, "A"));
        var (zones, _) = BacklightZones.Merge(existing, Zones((4, "B")));

        zones.Add(new JsonObject { ["addr"] = 1 });
        Assert.Equal(5, (int)existing[0]!["addr"]!);
        Assert.Equal(3, zones.Count);
    }

    // ---------------------------------------------------------------- Apply（下发颜色落盘）

    [Fact]
    public void 只发一个区的颜色不会把三区配置砍成一区()
    {
        // 2026-11-15 真机踩的坑：探针只发 addr=3 一个区，旧实现拿请求整表重建，
        // 配置从此只剩一个区（背光页只剩一个 tab，用户看不出是谁干的）。
        var existing = Zones((5, "A"), (4, "B"), (3, "C"));

        var zones = BacklightZones.Apply(existing, [(3, new[] { 0, 0, 255 })]);

        Assert.Equal([5, 4, 3], zones.Select(z => (int)z!["addr"]!).ToArray());
        // 点名的那个换了颜色，另外两个**原样保留**（含颜色与名字）
        Assert.Equal([0, 0, 255], zones[2]!["rgb"]!.AsArray().Select(x => (int)x!).ToArray());
        Assert.Equal([1, 2, 3], zones[0]!["rgb"]!.AsArray().Select(x => (int)x!).ToArray());
        Assert.Equal("A", (string?)zones[0]!["name"]);
    }

    [Fact]
    public void 下发颜色时表里没有的地址追加在末尾()
    {
        var existing = Zones((5, "A"), (4, "B"));

        var zones = BacklightZones.Apply(existing, [(6, new[] { 9, 9, 9 })]);

        Assert.Equal([5, 4, 6], zones.Select(z => (int)z!["addr"]!).ToArray());
        Assert.Equal([9, 9, 9], zones[2]!["rgb"]!.AsArray().Select(x => (int)x!).ToArray());
    }

    [Fact]
    public void 表装够机型声明的分区数后陌生地址不再追加()
    {
        // 2026-11-17：探针往 addr 8 / 7 试色，把 5/4/3 撑成 5 条 ——
        // 背光页冒出五个 tab、键盘按五等分画（用户："怎么变成五区了"）。
        var existing = Zones((5, "A"), (4, "B"), (3, "C"));

        var zones = BacklightZones.Apply(existing, [(8, new[] { 255, 0, 0 })], maxZones: 3);

        Assert.Equal([5, 4, 3], zones.Select(z => (int)z!["addr"]!).ToArray());
    }

    // ---------------------------------------------------------------- Limit（按机型声明收表）

    [Theory]
    [InlineData(0, 0)]      // 没声明：不裁
    [InlineData(1, 1)]      // 整块一区
    [InlineData(2, 2)]      // 极罕见的双区
    [InlineData(3, 3)]
    [InlineData(4, 4)]      // 上限
    [InlineData(5, 3)]      // 世上没有五分区 ⇒ 脏数据，按三区
    [InlineData(6, 3)]
    public void 机型声明的分区数被收进真实范围(int declared, int expected)
        => Assert.Equal(expected, BacklightZones.Sanitize(declared));

    [Fact]
    public void 超出机型声明分区数的条目被裁掉()
    {
        var dirty = Zones((5, "A"), (4, "B"), (3, "C"), (8, "A"), (7, "B"));

        var (zones, removed) = BacklightZones.Limit(dirty, 3);

        Assert.Equal(2, removed);
        Assert.Equal([5, 4, 3], zones.Select(z => (int)z!["addr"]!).ToArray());
    }

    [Fact]
    public void 同名的重复分区只留第一条()
    {
        var dirty = Zones((5, "A"), (4, "B"), (8, "A"), (3, "C"));

        var (zones, removed) = BacklightZones.Limit(dirty, 0);

        Assert.Equal(1, removed);
        Assert.Equal([5, 4, 3], zones.Select(z => (int)z!["addr"]!).ToArray());
    }

    [Fact]
    public void 机型没声明分区数时只去重不裁数量()
    {
        var zonesIn = Zones((5, "A"), (4, "B"), (3, "C"), (2, "D"));

        var (zones, removed) = BacklightZones.Limit(zonesIn, 0);

        Assert.Equal(0, removed);
        Assert.Equal(4, zones.Count);
    }
}
