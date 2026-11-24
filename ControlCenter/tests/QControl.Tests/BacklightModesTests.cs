using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// 「键盘背光按系统模式记忆」的档案解析规则（`presets.perf.modes[档].light`）。
///
/// 这些规则护的是两台机器上的真实后果：
///   · 某档没有档案时**不许拿别的档凑** —— 否则"只调过办公档"会变成三档全变成办公配色；
///   · 手改 model.json 写进去的越界值（level 5 / rgb 300 / addr 0）**不许原样下发** ——
///     addr=0 在 EC 上是"整条灯 + logo + 灯条"，写错会把别的灯也点亮；
///   · 重放时整表重建**必须保留 `name`** —— 否则背光页的 A/B/C tab 下次就变成没名字的。
/// </summary>
public class BacklightModesTests
{
    private static JsonObject Light(int level, int effect, params (int Addr, int[] Rgb)[] zones)
    {
        var arr = new JsonArray();
        foreach (var (addr, rgb) in zones)
            arr.Add(new JsonObject { ["addr"] = addr, ["rgb"] = new JsonArray(rgb[0], rgb[1], rgb[2]) });

        return new JsonObject { ["level"] = level, ["effect"] = effect, ["zones"] = arr };
    }

    /// <summary>一份最小 presets：只有 office 档有灯效档案 + 当前生效的三区表（带名字）。</summary>
    private static JsonObject Presets() => new()
    {
        ["backlight"] = new JsonObject
        {
            ["effect"] = 1,
            ["zones"] = new JsonArray(
                new JsonObject { ["addr"] = 5, ["name"] = "A", ["rgb"] = new JsonArray(255, 255, 255) },
                new JsonObject { ["addr"] = 4, ["name"] = "B", ["rgb"] = new JsonArray(255, 255, 255) },
                new JsonObject { ["addr"] = 3, ["name"] = "C", ["rgb"] = new JsonArray(255, 255, 255) }),
        },
        ["perf"] = new JsonObject
        {
            ["modes"] = new JsonObject
            {
                ["office"] = new JsonObject
                {
                    ["apmd"] = 2,
                    ["light"] = Light(1, 1, (5, [0, 0, 255]), (4, [0, 0, 255]), (3, [0, 0, 255])),
                },
                ["game"] = new JsonObject { ["apmd"] = 1 },
            },
        },
    };

    [Fact]
    public void 读得到某一档的档案()
    {
        var p = BacklightModes.ForMode(Presets(), "office");

        Assert.NotNull(p);
        Assert.Equal(1, p!.Level);
        Assert.Equal(1, p.Effect);
        Assert.Equal([5, 4, 3], p.Zones.Select(z => z.Addr).ToArray());
    }

    [Fact]
    public void 读得到自定义那套档案()
    {
        var presets = Presets();
        var custom = ModeBundle.EnsureLightBlock(presets, "custom")!;
        custom["effect"] = 6;
        custom["level"] = 1;
        custom["zones"] = new JsonArray(
            new JsonObject { ["addr"] = 5, ["rgb"] = new JsonArray(1, 2, 3) });

        var p = BacklightModes.ForMode(presets, "custom");

        Assert.NotNull(p);
        Assert.Equal(6, p!.Effect);
        Assert.Equal(1, p.Level);
        Assert.Equal(5, p.Zones[0].Addr);
    }

    [Fact]
    public void 没有这一档就返回null而不是拿别的档顶()
    {
        var presets = Presets();

        Assert.Null(BacklightModes.ForMode(presets, "game"));    // 有档位但还没灯效档案
        Assert.Null(BacklightModes.ForMode(presets, "beast"));   // 连档位都没有
        Assert.Null(BacklightModes.ForMode(presets, null));      // 档位没读到
        Assert.Null(BacklightModes.ForMode(presets, "custom"));  // 第四套也还没档案，同样不凑
    }

    [Fact]
    public void 结构不成形时不会崩也不会凑()
    {
        Assert.Null(BacklightModes.ForMode(new JsonObject { ["perf"] = 1 }, "office"));
        Assert.Null(BacklightModes.ForMode(new JsonObject(), "office"));
        Assert.Null(BacklightModes.ForMode(null!, "office"));
    }

    [Fact]
    public void 越界值当场被钳制()
    {
        var presets = new JsonObject
        {
            ["perf"] = new JsonObject
            {
                ["modes"] = new JsonObject
                {
                    ["game"] = new JsonObject
                    {
                        ["light"] = new JsonObject
                        {
                            ["level"] = 9,          // EC 只有 0/1/2
                            ["effect"] = 99,        // 4 位灯效码
                            ["zones"] = new JsonArray(
                                new JsonObject { ["addr"] = 5, ["rgb"] = new JsonArray(300, -5, 128) }),
                        },
                    },
                },
            },
        };

        var p = BacklightModes.ForMode(presets, "game");

        Assert.NotNull(p);
        Assert.Equal(2, p!.Level);
        Assert.Equal(15, p.Effect);
        Assert.Equal([255, 0, 128], p.Zones[0].Rgb);
    }

    [Fact]
    public void 地址0的条目被丢掉_它是整条灯不是分区()
    {
        var p = BacklightModes.Parse(Light(2, 1, (0, [255, 0, 0]), (5, [0, 255, 0])));

        Assert.NotNull(p);
        Assert.Single(p!.Zones);
        Assert.Equal(5, p.Zones[0].Addr);
    }

    [Fact]
    public void 分区数按机型声明裁剪()
    {
        var p = BacklightModes.Parse(
            Light(2, 1, (8, [1, 1, 1]), (5, [2, 2, 2]), (4, [3, 3, 3]), (3, [4, 4, 4])),
            maxZones: 3);

        Assert.Equal(3, p!.Zones.Length);
        Assert.Equal([8, 5, 4], p.Zones.Select(z => z.Addr).ToArray());
    }

    [Fact]
    public void 一条分区都凑不出来时整份不可用()
    {
        Assert.Null(BacklightModes.Parse(new JsonObject { ["level"] = 2, ["zones"] = new JsonArray() }));
        Assert.Null(BacklightModes.Parse(Light(2, 1, (0, [1, 2, 3]))));
        Assert.Null(BacklightModes.Parse(null));
    }

    [Fact]
    public void 同一地址只留第一条()
    {
        var p = BacklightModes.Parse(Light(2, 1, (5, [1, 1, 1]), (5, [9, 9, 9])));

        Assert.Single(p!.Zones);
        Assert.Equal([1, 1, 1], p.Zones[0].Rgb);
    }

    [Fact]
    public void 快照能读当前生效的那套_并兼容老的单色结构()
    {
        var live = new JsonObject
        {
            ["effect"] = 3,
            ["zones"] = new JsonArray(
                new JsonObject { ["addr"] = 5, ["rgb"] = new JsonArray(255, 0, 0) }),
        };

        var p = BacklightModes.Snapshot(live, level: 1);
        Assert.NotNull(p);
        Assert.Equal(1, p!.Level);
        Assert.Equal(3, p.Effect);
        Assert.Single(p.Zones);

        // 老结构：一个 addr + 一个 rgb（没有 zones）
        var legacy = new JsonObject
        {
            ["effect"] = 1,
            ["addr"] = 6,
            ["rgb"] = new JsonArray(10, 20, 30),
        };
        var q = BacklightModes.Snapshot(legacy, level: 2);
        Assert.NotNull(q);
        Assert.Equal(6, q!.Zones[0].Addr);
        Assert.Equal([10, 20, 30], q.Zones[0].Rgb);
    }

    [Fact]
    public void 快照读不到分区时返回null()
    {
        Assert.Null(BacklightModes.Snapshot(new JsonObject { ["effect"] = 1 }, level: 2));
        Assert.Null(BacklightModes.Snapshot(null, level: 2));
    }

    [Fact]
    public void 原厂自定义默认档案是常亮半亮全白_不沿用当前颜色()
    {
        // 原厂 LED_Custom = 1100FFFF...：点 Custom 只加载 Custom 自己的存档，
        // 拿当前键盘颜色去播种它是我们之前的错（2026-11-18 撤掉）。
        var live = new JsonObject
        {
            ["effect"] = 6,
            ["zones"] = new JsonArray(
                new JsonObject { ["addr"] = 5, ["rgb"] = new JsonArray(1, 2, 3) }),
            ["logo"] = new JsonArray(9, 8, 7),
        };

        var oem = BacklightModes.OemCustomDefault(live, hasLogo: true);

        Assert.NotNull(oem);
        Assert.Equal(1, oem!.Effect);
        Assert.Equal(1, oem.Level);
        Assert.Equal(5, oem.Zones[0].Addr);
        Assert.Equal([255, 255, 255], oem.Zones[0].Rgb);
        Assert.Equal([255, 255, 255], oem.Logo!);
        Assert.Null(oem.Tail);
    }

    [Fact]
    public void 写回生效配置时保留原有分区的名字()
    {
        var presets = Presets();
        var section = (JsonObject)presets["backlight"]!;
        var p = BacklightModes.ForMode(presets, "office")!;

        var wrote = BacklightModes.WriteSection(section, p);

        Assert.Equal(3, wrote);
        Assert.Equal(1, (int)section["effect"]!);
        var zones = (JsonArray)section["zones"]!;
        Assert.Equal("A", (string?)zones[0]!["name"]);
        Assert.Equal("B", (string?)zones[1]!["name"]);
        Assert.Equal("C", (string?)zones[2]!["name"]);
        Assert.Equal([0, 0, 255], zones[0]!["rgb"]!.AsArray().Select(x => (int)x!).ToArray());
    }

    [Fact]
    public void 写回时不会把档案里没有的分区悄悄留下()
    {
        // 三区档案写回一个曾经被探针撑成四条的配置：第四条必须消失（它就是探针留下的）
        var section = (JsonObject)Presets()["backlight"]!;
        ((JsonArray)section["zones"]!).Add(
            new JsonObject { ["addr"] = 8, ["name"] = "D", ["rgb"] = new JsonArray(1, 1, 1) });

        BacklightModes.WriteSection(section, BacklightModes.Parse(Light(2, 1, (5, [1, 2, 3])))!);

        var zones = (JsonArray)section["zones"]!;
        Assert.Single(zones);
        Assert.Equal(5, (int)zones[0]!["addr"]!);
    }

    [Fact]
    public void 序列化再解析回来是同一份()
    {
        var original = BacklightModes.Parse(Light(1, 6, (5, [10, 20, 30]), (4, [40, 50, 60])))!;

        var round = BacklightModes.Parse(BacklightModes.ToJson(original))!;

        Assert.Equal(original.Level, round.Level);
        Assert.Equal(original.Effect, round.Effect);
        Assert.True(BacklightModes.Same(original, round));
    }

    [Fact]
    public void Same只认档位灯效与各地址颜色()
    {
        var a = BacklightModes.Parse(Light(2, 1, (5, [1, 2, 3]), (4, [4, 5, 6])))!;
        var b = BacklightModes.Parse(Light(2, 1, (4, [4, 5, 6]), (5, [1, 2, 3])))!;   // 顺序不同
        var c = BacklightModes.Parse(Light(2, 1, (5, [1, 2, 3]), (4, [4, 5, 7])))!;   // 颜色不同

        Assert.True(BacklightModes.Same(a, b));
        Assert.False(BacklightModes.Same(a, c));
        Assert.False(BacklightModes.Same(a, null));
        Assert.True(BacklightModes.Same(null, null));
    }

    [Fact]
    public void 四套档案键_前三套跟系统档_custom是第四套()
    {
        Assert.Equal(["office", "game", "beast", "custom"], BacklightModes.Keys);
        Assert.Equal("自定义", BacklightModes.Label("custom"));
        Assert.Equal("custom", BacklightModes.Key("custom"));      // 灯效档案认它
        Assert.False(ModeBundle.IsKey("custom"));                  // 系统性能档不认它
        Assert.True(ModeBundle.IsLightKey("custom"));
        Assert.Null(BacklightModes.Key("turbo"));
        Assert.Equal("狂暴", BacklightModes.Label("beast"));
    }

    [Fact]
    public void 自定义接管的重放目标()
    {
        Assert.Equal("custom", BacklightModes.ReplayKey("game", customOverride: true));
        Assert.Equal("beast", BacklightModes.ReplayKey("beast", customOverride: false));
        // custom 本身不是系统档：读不到系统档时接管也不能凭空点灯
        Assert.Null(BacklightModes.ReplayKey("custom", customOverride: true));
        Assert.Null(BacklightModes.ReplayKey(null, customOverride: true));
    }

    [Fact]
    public void custom档案只写light_不会多出一个风扇档位()
    {
        var presets = new JsonObject
        {
            ["perf"] = new JsonObject { ["modes"] = new JsonObject() },
        };

        var light = ModeBundle.EnsureLightBlock(presets, "custom");
        Assert.NotNull(light);
        light!["effect"] = 6;

        Assert.NotNull(ModeBundle.LightBlock(presets, "custom"));
        Assert.Null(ModeBundle.Block(presets, "custom", "fan"));
        Assert.Null(ModeBundle.Mode(presets, "turbo"));
    }

    [Fact]
    public void 强类型int与JsonElement两种节点都读得出来()
    {
        // 本进程写回的节点包着 int，磁盘读回的包着 JsonElement —— 两者都不能读成 null
        var presets = new JsonObject
        {
            ["perf"] = new JsonObject
            {
                ["modes"] = new JsonObject
                {
                    ["beast"] = new JsonObject
                    {
                        ["light"] = new JsonObject
                        {
                            ["level"] = JsonValue.Create(2),
                            ["effect"] = JsonValue.Create(7),
                            ["zones"] = new JsonArray(new JsonObject
                            {
                                ["addr"] = JsonValue.Create(5),
                                ["rgb"] = new JsonArray(JsonValue.Create(1), JsonValue.Create(2), JsonValue.Create(3)),
                            }),
                        },
                    },
                },
            },
        };

        var p = BacklightModes.ForMode(presets, "beast");

        Assert.NotNull(p);
        Assert.Equal(2, p!.Level);
        Assert.Equal(7, p.Effect);
        Assert.Equal(5, p.Zones[0].Addr);
        Assert.Equal([1, 2, 3], p.Zones[0].Rgb);
    }
    [Fact]
    public void Logo与尾灯颜色随档案读写_老机型没有这两个键()
    {
        var node = Light(2, 1, (5, [1, 2, 3]));
        node["logo"] = new JsonArray(10, 20, 30);
        node["tail"] = new JsonArray(40, 50, 60);

        var p = BacklightModes.Parse(node)!;
        Assert.Equal([10, 20, 30], p.Logo!);
        Assert.Equal([40, 50, 60], p.Tail!);

        var round = BacklightModes.Parse(BacklightModes.ToJson(p))!;
        Assert.True(BacklightModes.Same(p, round));

        var section = new JsonObject();
        BacklightModes.WriteSection(section, p);
        Assert.Equal([10, 20, 30], section["logo"]!.AsArray().Select(x => (int)x!).ToArray());
        Assert.Equal([40, 50, 60], section["tail"]!.AsArray().Select(x => (int)x!).ToArray());

        var old = BacklightModes.Parse(Light(1, 3, (5, [9, 9, 9])))!;
        var json = BacklightModes.ToJson(old);
        Assert.False(json.ContainsKey("logo"));
        Assert.False(json.ContainsKey("tail"));
    }

    [Fact]
    public void Same比较独立灯珠颜色()
    {
        var a = BacklightModes.Parse(Light(2, 1, (5, [1, 2, 3])))! with { Logo = [1, 2, 3] };
        var b = BacklightModes.Parse(Light(2, 1, (5, [1, 2, 3])))! with { Logo = [4, 5, 6] };

        Assert.False(BacklightModes.Same(a, b));
        b = b with { Logo = [1, 2, 3] };
        Assert.True(BacklightModes.Same(a, b));
    }

    [Fact]
    public void 强类型int与JsonElement两种节点都读得出独立灯珠()
    {
        var node = new JsonObject
        {
            ["level"] = 2,
            ["effect"] = 1,
            ["zones"] = new JsonArray(new JsonObject
            {
                ["addr"] = 5, ["rgb"] = new JsonArray(1, 2, 3),
            }),
            ["logo"] = new JsonArray(JsonValue.Create(7), JsonValue.Create(8), JsonValue.Create(9)),
        };

        var p = BacklightModes.Parse(node)!;

        Assert.Equal([7, 8, 9], p.Logo!);
        Assert.Null(p.Tail);
    }

    /// <summary>
    /// 自动熄灭策略跟着四套档案走：每档一份，互不粘；没设过返回 null（调用方退回全局默认）。
    /// 若写成"读不到就拿别的档顶上"，办公档设的 5 分钟会传染到游戏档。
    /// </summary>
    [Fact]
    public void 自动熄灭策略按档各一份()
    {
        var presets = Presets();

        Assert.Null(BacklightModes.AutoOffOf(presets, "office"));   // 没设过
        Assert.Null(BacklightModes.AutoOffOf(presets, "custom"));

        Assert.True(BacklightModes.SetAutoOff(presets, "office", new BacklightModes.AutoOff(5, false)));

        Assert.Equal(new BacklightModes.AutoOff(5, false), BacklightModes.AutoOffOf(presets, "office"));
        Assert.Null(BacklightModes.AutoOffOf(presets, "game"));     // 别的档没被传染
        Assert.Null(BacklightModes.AutoOffOf(presets, "custom"));

        // 写策略不能破坏这一档已有的配色档案
        var profile = BacklightModes.ForMode(presets, "office");
        Assert.NotNull(profile);
        Assert.Equal(1, profile!.Level);
        Assert.Equal([0, 0, 255], profile.Zones[0].Rgb);
    }

    /// <summary>
    /// 某一档还只有自动熄灭、没有配色时，那不是一个"配色档案" ——
    /// 缺档不凑：读不到 zones 就是 null，切到该档不能凭空写一套别人的颜色。
    /// </summary>
    [Fact]
    public void 只有自动熄灭不算配色档案()
    {
        var presets = Presets();

        Assert.True(BacklightModes.SetAutoOff(presets, "game", new BacklightModes.AutoOff(0, true)));

        Assert.Null(BacklightModes.ForMode(presets, "game"));
        Assert.Equal(new BacklightModes.AutoOff(0, true), BacklightModes.AutoOffOf(presets, "game"));
    }

    /// <summary>手改 model.json 写进来的越界分钟数要当场收进 0~60；认不出的档位键一个字节不写。</summary>
    [Fact]
    public void 自动熄灭写入时钳制_不认得的档位不创建()
    {
        var presets = Presets();

        // 手改的越界值：读回来就是 60，不许原样当成 999 分钟
        var beast = ModeBundle.EnsureLightBlock(presets, "beast")!;
        beast["auto_off_min"] = 999;
        beast["auto_off_on_lock"] = true;
        Assert.Equal(60, BacklightModes.AutoOffOf(presets, "beast")!.Value.Minutes);

        // 认不出的档位键：不创建节点，返回 false（绝不悄悄生成一个 turbo 档）
        Assert.False(BacklightModes.SetAutoOff(presets, "turbo", new BacklightModes.AutoOff(5, false)));
        Assert.Null(ModeBundle.Mode(presets, "turbo"));

        // 负数按 0（= 不自动熄灭）收进来
        Assert.True(BacklightModes.SetAutoOff(presets, "beast", new BacklightModes.AutoOff(-3, false)));
        Assert.Equal(0, BacklightModes.AutoOffOf(presets, "beast")!.Value.Minutes);
    }
}

/// <summary>
/// 档位档案的迁移：老位置（`fan.mode_curves*` / `backlight.mode_profiles`）搬进
/// `perf.modes[档].{fan,light}`，搬完删老键。
///
/// 这段护的是**用户已经调好的东西**：搬错 = 三档风扇曲线或三套灯效凭空消失，
/// 而用户不会知道是谁干的（老位置已经被删了，连对照都没有）。
/// </summary>
public class ModeBundleTests
{
    private static JsonArray Curve(int duty) => new()
    {
        new JsonObject { ["t"] = 50, ["d"] = duty },
        new JsonObject { ["t"] = 90, ["d"] = 100 },
    };

    private static JsonObject Legacy()
    {
        var presets = new JsonObject
        {
            ["backlight"] = new JsonObject
            {
                ["effect"] = 1,
                ["mode_profiles"] = new JsonObject
                {
                    ["game"] = new JsonObject
                    {
                        ["effect"] = 3,
                        ["level"] = 2,
                        ["zones"] = new JsonArray(
                            new JsonObject { ["addr"] = 5, ["rgb"] = new JsonArray(0, 255, 0) }),
                    },
                },
            },
            ["fan"] = new JsonObject
            {
                ["curve_cpu"] = Curve(40),
                ["mode_curves"] = new JsonObject { ["office"] = Curve(30) },
                ["mode_curves_cpu"] = new JsonObject { ["office"] = Curve(31), ["beast"] = Curve(50) },
                ["mode_curves_gpu"] = new JsonObject { ["game"] = Curve(44) },
            },
            ["perf"] = new JsonObject
            {
                ["modes"] = new JsonObject
                {
                    ["office"] = new JsonObject { ["apmd"] = 2, ["plan"] = "办公" },
                    ["game"] = new JsonObject { ["apmd"] = 1 },
                    ["beast"] = new JsonObject { ["apmd"] = 0 },
                },
            },
        };
        return presets;
    }

    [Fact]
    public void 风扇曲线与灯效都搬进档位那一包_老键被删()
    {
        var presets = Legacy();

        var moved = ModeBundle.Migrate(presets);

        Assert.True(moved.Changed);
        Assert.Equal(1, moved.Lights);

        // CPU：office 用 mode_curves_cpu 的值（比 mode_curves 新），beast 也有
        Assert.Equal(31, FirstDuty(ModeBundle.FanCurve(presets, "office", ModeBundle.FanCpu)));
        // GPU：office 没有自己的 ⇒ 退回 mode_curves（两路共用那条）
        Assert.Equal(30, FirstDuty(ModeBundle.FanCurve(presets, "office", ModeBundle.FanGpu)));
        Assert.Equal(44, FirstDuty(ModeBundle.FanCurve(presets, "game", ModeBundle.FanGpu)));
        Assert.Null(ModeBundle.FanCurve(presets, "game", ModeBundle.FanCpu));

        // 灯效整块搬过去
        var light = ModeBundle.Block(presets, "game", "light");
        Assert.NotNull(light);
        Assert.Equal(3, (int)light!["effect"]!);

        // 老位置全部清掉
        var fan = (JsonObject)presets["fan"]!;
        Assert.False(fan.ContainsKey("mode_curves"));
        Assert.False(fan.ContainsKey("mode_curves_cpu"));
        Assert.False(fan.ContainsKey("mode_curves_gpu"));
        Assert.False(((JsonObject)presets["backlight"]!).ContainsKey("mode_profiles"));

        // 别的键一个都不许动
        Assert.NotNull(fan["curve_cpu"]);
        Assert.Equal(1, (int)((JsonObject)presets["backlight"]!)["effect"]!);
    }

    [Fact]
    public void 迁移是幂等的_第二次什么都不做()
    {
        var presets = Legacy();
        ModeBundle.Migrate(presets);

        var again = ModeBundle.Migrate(presets);

        Assert.False(again.Changed);
    }

    [Fact]
    public void 没有老键时完全不动()
    {
        var presets = new JsonObject { ["fan"] = new JsonObject { ["curve_cpu"] = Curve(40) } };

        var moved = ModeBundle.Migrate(presets);

        Assert.False(moved.Changed);
        Assert.False(presets.ContainsKey("perf"));
    }

    [Fact]
    public void perf结构被手改成标量时宁可什么都不搬也不删老数据()
    {
        var presets = Legacy();
        presets["perf"] = JsonValue.Create(1);

        var moved = ModeBundle.Migrate(presets);

        Assert.False(moved.Changed);
        var fan = (JsonObject)presets["fan"]!;
        Assert.True(fan.ContainsKey("mode_curves_cpu"));     // 老数据必须还在
        Assert.True(((JsonObject)presets["backlight"]!).ContainsKey("mode_profiles"));
    }

    [Fact]
    public void 档位节点不存在时会补出来()
    {
        var presets = new JsonObject
        {
            ["fan"] = new JsonObject { ["mode_curves_cpu"] = new JsonObject { ["game"] = Curve(55) } },
        };

        var moved = ModeBundle.Migrate(presets);

        Assert.True(moved.Changed);
        Assert.Equal(55, FirstDuty(ModeBundle.FanCurve(presets, "game", ModeBundle.FanCpu)));
        // 补出来的档位节点只该有这个键
        Assert.NotNull(ModeBundle.Mode(presets, "game")!["fan"]);
    }

    [Fact]
    public void 写曲线不会误伤档位里的其它设置()
    {
        var presets = Legacy();
        ModeBundle.Migrate(presets);

        ModeBundle.SetFanCurve(presets, "office", ModeBundle.FanCpu, Curve(66));

        var mode = ModeBundle.Mode(presets, "office")!;
        Assert.Equal(2, (int)mode["apmd"]!);              // apmd 还在
        Assert.Equal("办公", (string?)mode["plan"]);       // plan 还在
        Assert.Equal(66, FirstDuty(ModeBundle.FanCurve(presets, "office", ModeBundle.FanCpu)));
        Assert.Equal(30, FirstDuty(ModeBundle.FanCurve(presets, "office", ModeBundle.FanGpu)));
    }

    [Fact]
    public void 三路槽位互不干扰()
    {
        var presets = new JsonObject { ["perf"] = new JsonObject { ["modes"] = new JsonObject() } };

        ModeBundle.SetFanCurve(presets, "beast", ModeBundle.FanCpu, Curve(10));
        ModeBundle.SetFanCurve(presets, "beast", ModeBundle.FanGpu, Curve(20));
        ModeBundle.SetFanCurve(presets, "beast", ModeBundle.FanSys, Curve(30));

        Assert.Equal(10, FirstDuty(ModeBundle.FanCurve(presets, "beast", ModeBundle.FanCpu)));
        Assert.Equal(20, FirstDuty(ModeBundle.FanCurve(presets, "beast", ModeBundle.FanGpu)));
        Assert.Equal(30, FirstDuty(ModeBundle.FanCurve(presets, "beast", ModeBundle.FanSys)));
    }

    [Fact]
    public void 认不出的档位键不创建节点()
    {
        var presets = new JsonObject { ["perf"] = new JsonObject { ["modes"] = new JsonObject() } };

        ModeBundle.SetFanCurve(presets, "turbo", ModeBundle.FanCpu, Curve(10));

        Assert.Null(ModeBundle.Mode(presets, "turbo"));
    }

    /// <summary>
    /// 调的是不是"键盘上现在这一档"：只有相等才当场下发，编辑别的档只记档案。
    /// 2026-11-19 用户报的 bug 就卡在这条判定缺失上 —— 编辑办公档时键盘在游戏档，灯却当场变了。
    /// </summary>
    [Fact]
    public void 只有编辑当前这一档才当场下发()
    {
        Assert.True(BacklightModes.AppliesLive("office", "office"));
        Assert.False(BacklightModes.AppliesLive("office", "game"));   // 系统在游戏档，调办公档 ⇒ 只存
        Assert.False(BacklightModes.AppliesLive("custom", "beast"));
        // 自定义接管开着时"当前那一套"是 custom（liveKey 已过 ReplayKey 解析）
        Assert.True(BacklightModes.AppliesLive("custom", "custom"));
        // 读不到当前档 / 没带 mode（Fn 热键、托盘、老接口）⇒ 按老行为下发
        Assert.True(BacklightModes.AppliesLive("game", null));
        Assert.True(BacklightModes.AppliesLive(null, "office"));
    }

    /// <summary>
    /// 非当前档调色时这一档该记哪个亮度：
    ///   · 界面在那一档真的拖了亮度（`level_explicit`）⇒ 采信，0 = 关灯也要记；
    ///   · 只调了颜色 ⇒ **一个字节都不动**这一档已有的 level（否则串档，切回来自动变亮/变暗）；
    ///   · 这一档从没建过档案 ⇒ 用出生时键盘的亮度做初值，读不到就 100%。
    /// 2026-11-19 用户报"亮度还是统一的"：界面摆的是 EC 当前值、后端又不吃 level，
    /// 结果每一档看着都一样、也没法单独设。
    /// </summary>
    [Fact]
    public void 非当前档只在显式设了才记亮度()
    {
        // 显式设了：采用（关灯 0 也照样记）
        Assert.Equal(1, BacklightModes.DraftLevel(true, 1, hasExisting: true, ecLevel: 2));
        Assert.Equal(0, BacklightModes.DraftLevel(true, 0, hasExisting: true, ecLevel: 2));
        // 越界钳到 0..2
        Assert.Equal(2, BacklightModes.DraftLevel(true, 9, hasExisting: true, ecLevel: 2));
        Assert.Equal(0, BacklightModes.DraftLevel(true, -3, hasExisting: true, ecLevel: 2));

        // 没显式设：已有档案 ⇒ 别动
        Assert.Null(BacklightModes.DraftLevel(false, 0, hasExisting: true, ecLevel: 2));
        // 没显式设、也没有档案 ⇒ 拿 EC 当出生值；读不到 / 越界就是 100%
        Assert.Equal(1, BacklightModes.DraftLevel(false, null, hasExisting: false, ecLevel: 1));
        Assert.Equal(2, BacklightModes.DraftLevel(false, null, hasExisting: false, ecLevel: null));
        Assert.Equal(2, BacklightModes.DraftLevel(false, null, hasExisting: false, ecLevel: 7));
    }

    private static int FirstDuty(JsonNode? curve)
    {
        Assert.NotNull(curve);
        return (int)((JsonArray)curve!)[0]!["d"]!;
    }
}
