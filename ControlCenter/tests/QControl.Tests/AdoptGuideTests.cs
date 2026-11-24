using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「引导适配」的语义切窗 —— 这是"一键采集"和"完全适配"之间唯一的新逻辑，
/// 也是最容易悄悄错的地方：切错一个窗口，就会把游戏档的 APMD 绑到办公档上，
/// 表现出来是"点办公，机器反而更烫"，没人能从界面看出是这里错了。
///
/// 钉死六件事：
///   · 窗口边界（含标记那一刻、不含上一步标记那一刻）；
///   · 标记后的宽限（原厂延迟下发）只在"这一段一条都没抓到"时生效；
///   · 三档没凑齐不开性能模式；
///   · 分区数按抓到的地址个数定（1~6），超过上限才不开背光；
///   · 风扇槽位和通用档案不一致不开手动风扇，且 0xFF（= 忽略）不算槽位；
///   · 标了"没有这项"的窗口里即使有帧，也一条都不用；
///   · 显卡**默认开**（2026-11-22 起不再问用户：程序验不了切档，问出来的回答不算证据）——
///     观察到的 0x0203 与读数只当佐证，改不了这个结论，也不算"确认到能力"。
/// </summary>
public class AdoptGuideTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(8));

    // 组一条原厂写帧：GWF0 恒 0xFB00，GWF1 = sub，fields 覆盖指定偏移。
    private static ObservedWrite W(ushort sub, DateTimeOffset at, params (int Off, byte Val)[] fields)
    {
        var raw = new byte[32];
        raw[0] = 0x00; raw[1] = 0xFB;
        raw[2] = (byte)(sub & 0xFF); raw[3] = (byte)(sub >> 8);
        foreach (var (off, val) in fields) raw[off] = val;
        return new ObservedWrite(0xFB00, sub, raw, at);
    }

    private static GuideMark M(string step, double atSeconds, bool skipped = false)
        => new(step, T0.AddSeconds(atSeconds), skipped);

    private static ObservedWrite Perf(int apmd, double atSeconds)
        => W(OemObservation.SubPerf, T0.AddSeconds(atSeconds), (4, (byte)apmd));

    private static ObservedWrite Light(int addr, double atSeconds)
        => W(OemObservation.SubBacklight, T0.AddSeconds(atSeconds), (4, (byte)addr), (11, 0x12));

    /// <summary>手动风扇一帧：d0=偏移4、a/b/c/d=偏移8..11、d2=偏移12。</summary>
    private static ObservedWrite Fan(double atSeconds, int d0, int a, int d2)
        => W(OemObservation.SubFan, T0.AddSeconds(atSeconds),
             (4, (byte)d0), (8, (byte)a), (12, (byte)d2));

    private static ObservedWrite Gfx(int value, double atSeconds)
        => W(OemObservation.SubGfx, T0.AddSeconds(atSeconds), (4, (byte)value));

    /// <summary>一次"五步都做对"的完整场景。显卡帧故意落在所有标记之后：它靠整轮观察当佐证。</summary>
    private static (List<ObservedWrite> Writes, List<GuideMark> Marks) Perfect()
    {
        var writes = new List<ObservedWrite>
        {
            Perf(2, 2),
            Perf(1, 5),
            Perf(0, 8),
            Light(5, 11), Light(4, 12), Light(3, 13),
            Fan(16, 60, 50, 40),
            Gfx(2, 19),
        };
        var marks = new List<GuideMark>
        {
            M("perf_office", 3), M("perf_game", 6), M("perf_beast", 9),
            M("keyboard", 14), M("fan", 17),
        };
        return (writes, marks);
    }

    private static GuideResult Analyze(List<ObservedWrite> writes, List<GuideMark> marks)
        => AdoptGuide.Analyze(writes, marks, T0);

    /// <summary>findings 里那条显卡结论（它没有引导步骤，单独看）。</summary>
    private static JsonObject GfxFinding(GuideResult r)
        => r.Findings.OfType<JsonObject>().Single(x => x["step"]?.GetValue<string>() == "gfx");

    [Fact]
    public void 已确认的分区地址之外不算键盘分区()
    {
        // 原厂改键盘颜色时会把 logo 灯 / 灯条一起写一条 0x0100（本机 NL5 的 7/8）：
        // 观察里有 5 个地址，但配置里已确认的是 5/4/3、且这次**全都出现** ⇒ 只认 3 区。
        var (w0, m) = Perfect();
        var writes = new List<ObservedWrite>(w0) { Light(8, 12.2), Light(7, 12.4) };

        var r = AdoptGuide.Analyze(writes, m, T0, knownZoneAddrs: [5, 4, 3]);

        Assert.Equal(3, r.Caps["kb_zones"]!.GetValue<int>());
        var addrs = r.Presets["backlight"]!["zones"]!.AsArray()
                     .Select(z => z!["addr"]!.GetValue<int>()).OrderBy(a => a).ToArray();
        Assert.Equal(new[] { 3, 4, 5 }, addrs);
        Assert.Contains(r.Notes, n => n.Contains("logo"));
    }

    [Fact]
    public void 只确认到部分已知分区时表外地址仍按灯珠处理()
    {
        // 2026-11-23 12:28 真机：已知分区 5/4/3，用户这次点的是 C 区，
        // 原厂换色时把 logo 灯 7/8 一起写了 ⇒ 抓到 3/4/7/8。
        // 按"必须全部已知地址都出现"才裁，就会把 7/8 当成分区：判成 4 区、
        // 四个 tab 里两个是灯，还把 7/8 写进用户配置（那次已经写进去了）。
        var writes = new List<ObservedWrite>
        {
            Perf(2, 2), Perf(1, 5), Perf(0, 8),
            Light(4, 11), Light(3, 11.2), Light(7, 11.4), Light(8, 11.6),
        };
        var marks = new List<GuideMark>
        {
            M("perf_office", 3), M("perf_game", 6), M("perf_beast", 9),
            M("keyboard", 12), M("fan", 13, skipped: true),
        };

        var r = AdoptGuide.Analyze(writes, marks, T0, knownZoneAddrs: [5, 4, 3]);

        // 能对上一半以上的已知分区 ⇒ 分区表就是已知那套，表外地址按灯珠处理
        Assert.Equal(3, r.Caps["kb_zones"]!.GetValue<int>());
        var addrs = r.Presets["backlight"]!["zones"]!.AsArray()
                     .Select(z => z!["addr"]!.GetValue<int>()).OrderBy(a => a).ToArray();
        Assert.Equal(new[] { 3, 4, 5 }, addrs);
        Assert.Contains(r.Notes, n => n.Contains("logo"));

        // 结论行也必须说"3 区"：以前 value 印的是原始观察 3/4/7/8，
        // 与同一屏的"键盘背光 3 区"打架（2026-11-23 21:37 真机）。
        var kb = r.Findings.OfType<JsonObject>().First(o => o["step"]!.GetValue<string>() == "keyboard");
        var val = kb["value"]!.GetValue<string>();
        Assert.Contains("分区地址 3/4/5", val);
        Assert.Contains("按 logo 灯", val);
        Assert.DoesNotContain("分区地址 3/4/7/8", val);
    }

    [Fact]
    public void 已确认的地址没全出现时不裁分区()
    {
        // 用户只点了一个区（观察里只有 4）：这次没覆盖全部分区，"已知 5/4/3"不能拿来裁 ——
        // 那会把"其实是四区、这次只点了两区"的机器永远锁死。
        var writes = new List<ObservedWrite> { Perf(2, 2), Perf(1, 5), Perf(0, 8), Light(4, 11) };
        var marks = new List<GuideMark>
        {
            M("perf_office", 3), M("perf_game", 6), M("perf_beast", 9),
            M("keyboard", 12), M("fan", 13, skipped: true),
        };

        var r = AdoptGuide.Analyze(writes, marks, T0, knownZoneAddrs: [5, 4, 3]);

        Assert.Equal(1, r.Caps["kb_zones"]!.GetValue<int>());
        // 但别把话说死：调用方会把 caps.kb_zones 抬回已知的 3 区、presets 也保留 3 个 tab，
        // 报告里再写"1 区键盘"就和档案自己打架了（2026-11-23 00:04 真机）。
        Assert.DoesNotContain(r.Notes, n => n.Contains("1 区键盘"));
        Assert.DoesNotContain(r.Notes, n => n.Contains("单区"));
        Assert.Contains(r.Notes, n => n.Contains("这时确认的键盘分区地址") || n.Contains("这次确认的键盘分区地址"));

        // 只点到部分已知分区时，结论行要说"这是这次点到的地址"，不能让读者以为分区表只剩一个
        var kb = r.Findings.OfType<JsonObject>().First(o => o["step"]!.GetValue<string>() == "keyboard");
        var val = kb["value"]!.GetValue<string>();
        Assert.Contains("这次点到的地址 4", val);
        Assert.Contains("档案分区表仍按 5/4/3", val);
    }

    // ---------------------------------------------------------------- 正路

    [Fact]
    public void 三档都标对时把APMD绑到正确的档位()
    {
        var (w, m) = Perfect();
        var r = Analyze(w, m);

        Assert.Equal(3, r.Caps["perf_modes"]!.AsArray().Count);
        Assert.Equal(new[] { "office", "game", "beast" },
            r.Caps["perf_modes"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());

        var apmd = r.Presets["perf"]!["modes"]!;
        Assert.Equal(2, apmd["office"]!["apmd"]!.GetValue<int>());
        Assert.Equal(1, apmd["game"]!["apmd"]!.GetValue<int>());
        Assert.Equal(0, apmd["beast"]!["apmd"]!.GetValue<int>());
    }

    [Fact]
    public void 五步都做对时四项能力全开()
    {
        var (w, m) = Perfect();
        var r = Analyze(w, m);

        Assert.True(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(3, r.Caps["kb_zones"]!.GetValue<int>());
        Assert.True(r.Caps["fan_manual"]!.GetValue<bool>());
        Assert.True(r.Caps["fan_curve"]!.GetValue<bool>());
        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        Assert.True(r.AnyConfirmed);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void 三个分区地址按降序写成ABC三区()
    {
        var (w, m) = Perfect();
        var r = Analyze(w, m);

        var zones = r.Presets["backlight"]!["zones"]!.AsArray();
        Assert.Equal(new[] { 5, 4, 3 }, zones.Select(z => z!["addr"]!.GetValue<int>()).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, zones.Select(z => z!["name"]!.GetValue<string>()).ToArray());
        Assert.True(r.Presets["backlight"]!["zone_addrs_confirmed"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- 不该给的

    [Fact]
    public void 三档没凑齐就不开性能模式()
    {
        // 只标了办公和游戏：第三档没做（原厂可能只有两档，也可能用户跳过了）
        var writes = new List<ObservedWrite> { Perf(2, 2), Perf(1, 5) };
        var marks = new List<GuideMark> { M("perf_office", 3), M("perf_game", 6) };

        var r = Analyze(writes, marks);

        Assert.Empty(r.Caps["perf_modes"]!.AsArray());
        Assert.Contains(r.Missing, x => x.Contains("三档没凑齐"));
        // 少一档也把确认到的两档记下来（给作者的证据），只是不开性能页
        var modes = r.Presets["perf"]!["modes"]!.AsObject();
        Assert.Equal(2, modes.Count);
        Assert.Equal(2, modes["office"]!["apmd"]!.GetValue<int>());
    }

    [Fact]
    public void 两个分区就按两区开背光页()
    {
        // 2026-11-14 起界面按 caps.kb_zones 画 tab：双区键盘不再被"必须三区"挡在门外
        var writes = new List<ObservedWrite> { Light(5, 2), Light(4, 3) };
        var marks = new List<GuideMark> { M("keyboard", 4) };

        var r = Analyze(writes, marks);

        Assert.True(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(2, r.Caps["kb_zones"]!.GetValue<int>());

        var zones = r.Presets["backlight"]!["zones"]!.AsArray();
        Assert.Equal(new[] { 5, 4 }, zones.Select(z => z!["addr"]!.GetValue<int>()).ToArray());
        Assert.Equal(new[] { "A", "B" }, zones.Select(z => z!["name"]!.GetValue<string>()).ToArray());
        // 非三区时要说清"图上的分区边界是均分示意、不是实测位置"
        Assert.Contains(r.Notes, n => n.Contains("示意"));
    }

    [Fact]
    public void 单区键盘也能开背光页()
    {
        var writes = new List<ObservedWrite> { Light(5, 2) };
        var marks = new List<GuideMark> { M("keyboard", 4) };

        var r = Analyze(writes, marks);

        Assert.True(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(1, r.Caps["kb_zones"]!.GetValue<int>());
        Assert.Single(r.Presets["backlight"]!["zones"]!.AsArray());
    }

    [Fact]
    public void 超过四区的地址不当键盘分区()
    {
        // 世上**没有五分区键盘**（2026-11-17 用户原话）：五个以上只可能是把
        // logo 灯 / 灯条也算进来了 ⇒ 不开背光页，交给作者核对
        var writes = new List<ObservedWrite>();
        for (var a = 1; a <= 7; a++) writes.Add(Light(a, 2));
        var marks = new List<GuideMark> { M("keyboard", 9) };

        var r = Analyze(writes, marks);

        Assert.False(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(0, r.Caps["kb_zones"]!.GetValue<int>());
        Assert.Contains(r.Missing, x => x.Contains("上限"));
    }

    [Fact]
    public void 五个地址也按超上限处理()
    {
        var writes = new List<ObservedWrite>();
        for (var a = 1; a <= 5; a++) writes.Add(Light(a, 2));
        var marks = new List<GuideMark> { M("keyboard", 9) };

        var r = Analyze(writes, marks);

        Assert.False(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(0, r.Caps["kb_zones"]!.GetValue<int>());
    }

    [Fact]
    public void 只抓到熄灯帧时不报已确认()
    {
        // 原厂打开灯效页会先把各 LED 组熄灭一遍（level=0 / RGB 同值，IsBacklightInit）。
        // 这种窗口里一个分区染色帧都没有：以前会造一条 "整键盘（地址 0）" 的 value，
        // 报告就印 `[已确认] 键盘背光：整键盘（地址 0）`，而同页 caps 是 kb_zones=0。
        var writes = new List<ObservedWrite>
        {
            W(OemObservation.SubBacklight, T0.AddSeconds(2),
              (4, (byte)5), (8, (byte)0), (9, (byte)0), (10, (byte)0), (11, (byte)0)),
        };
        var marks = new List<GuideMark> { M("keyboard", 4) };

        var r = Analyze(writes, marks);

        var kb = r.Findings.OfType<JsonObject>().First(o => o["step"]!.GetValue<string>() == "keyboard");
        Assert.Null(kb["value"]);                       // 没确认到 ⇒ 不能印 [已确认]
        Assert.Contains("熄灯帧", kb["why"]!.GetValue<string>());
        Assert.False(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(0, r.Caps["kb_zones"]!.GetValue<int>());
    }

    [Fact]
    public void 键盘只有一个地址时不开背光页()
    {
        var writes = new List<ObservedWrite> { Light(0, 2), Light(0, 3) };   // 地址 0 = 整键盘
        var marks = new List<GuideMark> { M("keyboard", 4) };

        var r = Analyze(writes, marks);

        Assert.False(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Equal(0, r.Caps["kb_zones"]!.GetValue<int>());
        Assert.Null(r.Presets["backlight"]);
        Assert.Contains(r.Missing, x => x.Contains("键盘背光"));
    }

    [Fact]
    public void 两风扇机只抓到两路槽位也开手动风扇()
    {
        // 2026-11-22 23:52 真机（本机 NL5，两风扇）：原厂 Max 帧是 `d0=100 a=100 d2=255`，
        // 观察口径只统计"真写了转速"的槽位 ⇒ 抓到 [d0/a]。按"必须三路齐"判，这台机器
        // 每采一次都会把手动风扇判成不支持 —— 而内置档案对同一台机器写的是
        // fan_slots [d0/a/d2] + fan_manual=true（d2 恒写 255 = 不用）。
        // 所以判据是"落在档案这张表里"，不是"把这张表填满"。
        var writes = new List<ObservedWrite> { Fan(2, 60, 50, 0) };
        var marks = new List<GuideMark> { M("fan", 3) };

        var r = Analyze(writes, marks);

        Assert.True(r.Caps["fan_manual"]!.GetValue<bool>());
        Assert.True(r.Caps["fan_curve"]!.GetValue<bool>());
        Assert.DoesNotContain(r.Missing, x => x.Contains("槽位"));
    }

    [Fact]
    public void 风扇槽位的0xFF不算使用中的槽位()
    {
        // 2 风扇机型在原厂里常给第三路写 0xFF（= 忽略）。把它当槽位会多数一路：
        // 证据里只该出现 d0/a，但"能不能手动"照常开。
        var writes = new List<ObservedWrite>
        {
            W(OemObservation.SubFan, T0.AddSeconds(2), (4, 60), (8, 50), (12, 0xFF)),
        };
        var marks = new List<GuideMark> { M("fan", 3) };

        var r = Analyze(writes, marks);

        Assert.Equal(new[] { "a", "d0" },
            r.Evidence["fan_slots"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.True(r.Caps["fan_manual"]!.GetValue<bool>());

        // 结论行别让读者以为风扇表就这两个字节：观察只看得见"真写了转速"的那几路，
        // 档案表仍是 [d0/a/d2]（不用的那一路写 255）。
        var fan = r.Findings.OfType<JsonObject>().First(o => o["step"]!.GetValue<string>() == "fan");
        Assert.Contains("档案表仍按 d0/a/d2", fan["value"]!.GetValue<string>());
    }

    [Fact]
    public void 槽位里出现通用表没有的字节就不开手动风扇()
    {
        // 真正的风险在这：原厂写的是 d0 + b（不是 a）⇒ 档案的 [d0/a/d2] 里没有 b，
        // 照档案写 UFA 会填错字节（一个风扇满转、另一个不动）。
        var writes = new List<ObservedWrite>
        {
            W(OemObservation.SubFan, T0.AddSeconds(2), (4, 60), (9, 50)),
        };
        var marks = new List<GuideMark> { M("fan", 3) };

        var r = Analyze(writes, marks);

        Assert.False(r.Caps["fan_manual"]!.GetValue<bool>());
        Assert.Contains(r.Missing, x => x.Contains("槽位"));

        // 出现表外字节时不能再说"档案表仍按…" —— 那种情况要作者改表，不是沿用
        var fan = r.Findings.OfType<JsonObject>().First(o => o["step"]!.GetValue<string>() == "fan");
        Assert.DoesNotContain("档案表仍按", fan["value"]!.GetValue<string>());
    }

    [Fact]
    public void 标了没有这项就不读那一段的帧()
    {
        // 用户在"键盘灯"这一步点了「这台没有这项」，但那段时间窗口里正好有一条 0x0100
        var writes = new List<ObservedWrite> { Light(5, 2), Light(4, 3), Light(3, 4) };
        var marks = new List<GuideMark> { M("keyboard", 5, skipped: true) };

        var r = Analyze(writes, marks);

        Assert.False(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Contains(r.Notes, x => x.Contains("没有这项"));
    }

    [Fact]
    public void 没有标记时什么都不确认()
    {
        // 没有标记 ⇒ 五步一步都不推断：否则会把五步全写成"没走到"、caps 全部显式 false，
        // 等于用"没观察"覆盖 OemObservation 已经认到的能力（2026-11-14 改）。
        // 显卡那条恒开（默认能切），但它不算"确认"，所以 AnyConfirmed 仍为 false。
        var writes = new List<ObservedWrite> { Perf(2, 2), Light(5, 3), Fan(4, 50, 50, 40) };
        var r = AdoptGuide.Analyze(writes, [], T0);

        Assert.False(r.AnyConfirmed);
        Assert.Empty(r.Caps["perf_modes"]!.AsArray());
        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        // 五步一条都不出现，findings 里只有显卡那一条
        var only = Assert.Single(r.Findings.OfType<JsonObject>());
        Assert.Equal("gfx", only["step"]?.GetValue<string>());
    }

    // ---------------------------------------------------------------- 窗口边界

    [Fact]
    public void 刚好落在标记那一刻的帧算这一步()
    {
        var writes = new List<ObservedWrite> { Perf(1, 3) };
        var marks = new List<GuideMark> { M("perf_office", 3) };

        var r = Analyze(writes, marks);

        Assert.Equal(1, r.Presets["perf"]!["modes"]!["office"]!["apmd"]!.GetValue<int>());
    }

    [Fact]
    public void 标记之后一秒内下发的帧算这一步的宽限()
    {
        // 原厂延迟：用户点"做完了"之后 800ms 才写下去
        var writes = new List<ObservedWrite> { Perf(1, 3.8) };
        var marks = new List<GuideMark> { M("perf_office", 3) };

        var r = Analyze(writes, marks);

        Assert.Equal(1, r.Presets["perf"]!["modes"]!["office"]!["apmd"]!.GetValue<int>());
        Assert.Contains(r.Notes, x => x.Contains("宽限"));
    }

    [Fact]
    public void 宽限不会越过下一个标记()
    {
        // 办公段的宽限窗口只有 0.2 秒（下一个标记马上就到），之后那条属于游戏段
        var writes = new List<ObservedWrite> { Perf(2, 3.5), Perf(1, 4.5) };
        var marks = new List<GuideMark> { M("perf_office", 3), M("perf_game", 4) };

        var r = Analyze(writes, marks);

        Assert.Equal(2, r.Presets["perf"]!["modes"]!["office"]!["apmd"]!.GetValue<int>());
        Assert.Equal(1, r.Presets["perf"]!["modes"]!["game"]!["apmd"]!.GetValue<int>());
    }

    [Fact]
    public void 一步迟发两条帧时下一步不会认领剩下的那条()
    {
        // 办公段点了完成之后原厂连发两条（3.2 / 3.4 秒），都在宽限窗口里；
        // 游戏段用户没切档却点了完成。以前只认领第一条，剩下的 3.4 落进游戏段的窗口，
        // 于是 perf["game"] 记成办公档的 APMD —— 界面点"游戏"写下去的却是办公功耗。
        var writes = new List<ObservedWrite> { Perf(1, 3.2), Perf(2, 3.4) };
        var marks = new List<GuideMark>
        {
            M("perf_office", 3), M("perf_game", 6), M("perf_beast", 9),
        };

        var r = Analyze(writes, marks);

        // 两条都归办公段，取最后一条
        Assert.Equal(2, r.Presets["perf"]!["modes"]!["office"]!["apmd"]!.GetValue<int>());
        // 游戏段宁可判"没抓到"，也不能拿办公段的帧顶上
        Assert.False(r.Presets["perf"]!["modes"]!.AsObject().ContainsKey("game"));
    }

    [Fact]
    public void 第一步窗口从观察开始算起()
    {
        // 观察一开始原厂就在写（用户还没点第一步）
        var writes = new List<ObservedWrite> { Perf(2, 0.5) };
        var marks = new List<GuideMark> { M("perf_office", 2) };

        var r = Analyze(writes, marks);

        Assert.Equal(2, r.Presets["perf"]!["modes"]!["office"]!["apmd"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- 显卡（需重启，不排步骤）
    // 2026-11-22 起口径变了：采集页那句「原厂里有没有显卡模式」的提问去掉 ——
    // 切换要重启，观察抓不到 0x0203 写字，读数也证明不了"能切"，问用户等于拿回答当证据。
    // 现在**默认按能切处理**，写档案时注明"未逐档验证"；观察到的写帧与读数只当佐证。

    [Fact]
    public void 显卡模式默认开_不依赖回答也不依赖写帧()
    {
        var r = AdoptGuide.Analyze([], [], T0);

        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        var f = GfxFinding(r);
        var value = f["value"]?.GetValue<string>() ?? "";
        Assert.Contains("默认开启", value);
        Assert.Contains("未逐档验证", value);
        Assert.DoesNotContain(r.Missing, x => x.Contains("显卡模式"));
        // 它不算"确认到能力"：只有显卡这一条时，「启用本机」不该被这一项撑出来。
        Assert.False(r.AnyConfirmed);
    }

    [Fact]
    public void 显卡帧落在所有步骤窗口之外也能当佐证()
    {
        // 原厂切显卡模式要重启，引导里不排这一步；观察到 0x0203 就把值写进结论（只是佐证）。
        var writes = new List<ObservedWrite> { Gfx(2, 40) };
        var marks = new List<GuideMark>
        {
            M("perf_office", 3), M("perf_game", 6), M("perf_beast", 9),
            M("keyboard", 14), M("fan", 17),
        };

        var r = Analyze(writes, marks);

        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        Assert.Contains("0x0203", GfxFinding(r)["value"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public void 读数只当佐证_读到二且系统里没有核显时写进结论()
    {
        var read = new GfxRead(Mode: 2, ReadOk: true, Stable: true, HasIgpu: false, HasDgpu: true);
        var r = AdoptGuide.Analyze([], [], T0, gfxRead: read);

        var f = GfxFinding(r);
        Assert.Contains("独显直连", f["value"]?.GetValue<string>() ?? "");
        Assert.Contains(r.Notes, x => x.Contains("与固件行为一致"));
    }

    [Fact]
    public void 读数与显示拓扑对不上只在备注里说_不影响默认开()
    {
        // 读到 2（独显直连）却还看得见核显 ⇒ 这个值没在生效（刚写过没重启 / 没有 MUX）。
        // 能力照样默认开（程序验不了），但备注必须写清读数的矛盾，作者复核时能看见。
        var read = new GfxRead(Mode: 2, ReadOk: true, Stable: true, HasIgpu: true, HasDgpu: true);
        var r = AdoptGuide.Analyze([], [], T0, gfxRead: read);

        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        Assert.Contains(r.Notes, x => x.Contains("对不上"));
    }

    [Fact]
    public void 读到一不能当证据_没有MUX的机型也一样()
    {
        var read = new GfxRead(Mode: 1, ReadOk: true, Stable: true, HasIgpu: true, HasDgpu: true);
        var r = AdoptGuide.Analyze([], [], T0, gfxRead: read);

        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        Assert.Contains(r.Notes, x => x.Contains("不能据此认定能切"));
    }

    [Fact]
    public void 显卡值不在1到3之间不当佐证_能力仍默认开()
    {
        var r = AdoptGuide.Analyze([Gfx(9, 2)], [], T0);

        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        // 9 不在 1~3 里（未标定 / 缓冲残留）⇒ 结论里不写"观察到值 9"，只保留默认那半句
        var value = GfxFinding(r)["value"]?.GetValue<string>() ?? "";
        Assert.Contains("抓不到 0x0203", value);
        Assert.DoesNotContain("值 9", value);
    }

    [Fact]
    public void 引导步骤不含需要重启的显卡切换()
    {
        Assert.Equal(5, AdoptGuide.Steps.Count);
        Assert.DoesNotContain(AdoptGuide.Steps, x => x.Id == "graphics");
        Assert.Contains("快捷键", AdoptGuide.Steps.Single(x => x.Id == "fan").Hint);
    }

    [Fact]
    public void 只有AutoMax预设时备注写明原厂没有手动拉条()
    {
        // 只有 0 / 100 / 0xFF：许多广达模具只有 Auto / Max 快捷键，没有手动调速条。
        var writes = new List<ObservedWrite>
        {
            W(OemObservation.SubFan, T0.AddSeconds(2), (4, 100), (8, 100), (12, 100)),
            W(OemObservation.SubFan, T0.AddSeconds(2.5), (4, 0), (8, 0), (12, 0xFF)),
        };
        var marks = new List<GuideMark> { M("fan", 3) };

        var r = Analyze(writes, marks);

        Assert.Contains(r.Notes, x => x.Contains("Auto / Max"));
    }

    // ---------------------------------------------------------------- 落档案

    [Fact]
    public void 并进档案时没确认的项被显式写成false()
    {
        // 关键：Device.Caps() 在档案缺键时会按命令表推导。不显式写 false，
        // 用户一开 EC 总闸，没验过的风扇就会自己冒出来。
        // 显卡是唯一例外：程序验不了切档，按默认开处理（2026-11-22 用户定）。
        var profile = new JsonObject
        {
            ["caps"] = new JsonObject(),
            ["notes"] = new JsonArray(),
            ["adopt"] = new JsonObject { ["missing"] = new JsonArray() },
        };

        var r = Analyze([], [M("perf_office", 2, skipped: true)]);
        AdoptGuide.Apply(profile, r);

        var caps = profile["caps"]!.AsObject();
        Assert.False(caps["fan_manual"]!.GetValue<bool>());
        Assert.False(caps["fan_curve"]!.GetValue<bool>());
        Assert.True(caps["gfx_switch"]!.GetValue<bool>());
        Assert.False(caps["keyboard_rgb"]!.GetValue<bool>());
        Assert.Empty(caps["perf_modes"]!.AsArray());
        Assert.NotNull(profile["adopt"]!["guide"]);
    }

    // ---------------------------------------------------------------- 真机回放（2026-11-13 16:49 NL5）

    /// <summary>从采集包 write_cmds 的原始 32 字节还原一帧，时间换算成相对观察开始的秒数。</summary>
    private static ObservedWrite Raw(string hex, double atSeconds)
    {
        var bytes = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                       .Select(x => Convert.ToByte(x, 16)).ToArray();
        return new ObservedWrite((ushort)(bytes[0] | (bytes[1] << 8)),
                                 (ushort)(bytes[2] | (bytes[3] << 8)),
                                 bytes, T0.AddSeconds(atSeconds));
    }

    /// <summary>
    /// 一次真实采集的 20 条原厂写帧 + 5 个用户标记。留在这里当回归基线：
    /// 这轮里游戏档没抓到 0x0300、键盘夹了 4 条熄灯初始化帧、显卡整轮没出现，
    /// 正是三个容易让"完全适配"悄悄落空的地方。
    /// </summary>
    private static (List<ObservedWrite> Writes, List<GuideMark> Marks) RealCapture()
        => ([
            Raw("00 FB 05 02 2E 00 00 00 28 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 1.818),
            Raw("00 FB 00 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 23.839),
            Raw("00 FB 00 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 24.649),
            Raw("00 FB 00 03 02 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 28.377),
            Raw("00 FB 05 02 21 00 00 00 1E 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 29.835),
            Raw("00 FB 05 02 28 00 00 00 23 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 35.276),
            Raw("00 FB 00 03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 42.283),
            Raw("00 FB 05 02 31 00 00 00 28 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 43.985),
            Raw("00 FB 00 01 07 00 00 00 01 01 01 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 48.383),
            Raw("00 FB 00 01 08 00 00 00 01 01 01 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 48.412),
            Raw("00 FB 00 01 05 00 00 00 01 01 01 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 48.504),
            Raw("00 FB 00 01 04 00 00 00 01 01 01 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 48.537),
            Raw("00 FB 00 01 05 00 00 00 2B 2B FF 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 51.447),
            Raw("00 FB 00 01 05 00 00 00 2B 2B FF 12 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 61.433),
            Raw("00 FB 00 01 04 00 00 00 2C FF 50 12 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 61.465),
            Raw("00 FB 00 01 03 00 00 00 FF 56 56 12 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 61.642),
            Raw("00 FB 05 02 64 00 00 00 64 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 74.58),
            Raw("00 FB 05 02 32 00 00 00 28 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 82.05),
            Raw("00 FB 05 02 64 00 00 00 64 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 84.309),
            Raw("00 FB 05 02 31 00 00 00 28 00 00 00 1E 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00", 91.74),
           ], [
            M("perf_office", 32.56),
            M("perf_game", 40.262),
            M("perf_beast", 45.023),
            M("keyboard", 68.045),
            M("fan", 98.032),
           ]);

    [Fact]
    public void 真机回放_熄灯初始化帧不再把键盘分区数撑成5个()
    {
        var (w, m) = RealCapture();
        var r = Analyze(w, m);

        Assert.Equal(3, r.Caps["kb_zones"]!.GetValue<int>());
        Assert.True(r.Caps["keyboard_rgb"]!.GetValue<bool>());
        var zones = r.Presets["backlight"]!["zones"]!.AsArray();
        Assert.Equal(new[] { 5, 4, 3 }, zones.Select(z => z!["addr"]!.GetValue<int>()).ToArray());
        Assert.Contains(r.Notes, x => x.Contains("非键盘分区跳过"));
        Assert.DoesNotContain(r.Missing, x => x.Contains("键盘背光抓到 5 个"));
    }

    [Fact]
    public void 真机回放_游戏档空窗照样如实不开_显卡按默认开()
    {
        var (w, m) = RealCapture();
        var r = Analyze(w, m);

        // 整轮 0x0300 只有 0 和 2，游戏档的 1 一次都没出现 ⇒ 性能页不能开。
        Assert.Empty(r.Caps["perf_modes"]!.AsArray());
        Assert.Contains(r.Missing, x => x.Contains("三档没凑齐"));
        Assert.Equal(2, r.Presets["perf"]!["modes"]!.AsObject().Count);
        Assert.Equal(2, r.Findings.Count(f => f is JsonObject o
                                              && o["value"] is JsonValue v
                                              && v.TryGetValue<string>(out var text)
                                              && text.StartsWith("APMD")));

        // 这一轮整轮没出现 0x0203 —— 以前会写"显卡没确认"，现在按默认开（未逐档验证）。
        Assert.True(r.Caps["gfx_switch"]!.GetValue<bool>());
        Assert.True(r.Caps["fan_manual"]!.GetValue<bool>());
        Assert.Contains(r.Notes, x => x.Contains("Max 预设"));
        Assert.Contains(r.Notes, x => x.Contains("自动曲线"));
    }

    [Fact]
    public void 引导界面只数当前步骤关心的子命令()
    {
        // 回归：真机那次游戏档窗口里只有一条 0x0205（原厂自动风扇曲线），
        // 旧逻辑把它显示成"抓到 1 条写命令"，用户以为切档成功就点了「完成这步」。
        Assert.True(AdoptGuide.CountsForStep(1, OemObservation.SubPerf));
        Assert.False(AdoptGuide.CountsForStep(1, OemObservation.SubFan));
        Assert.False(AdoptGuide.CountsForStep(1, OemObservation.SubBacklight));
        Assert.True(AdoptGuide.CountsForStep(3, OemObservation.SubBacklight));
        Assert.False(AdoptGuide.CountsForStep(3, OemObservation.SubFan));
        Assert.True(AdoptGuide.CountsForStep(4, OemObservation.SubFan));
        Assert.False(AdoptGuide.CountsForStep(99, OemObservation.SubPerf));
    }

    // ---------------------------------------------------------------- 逐帧证据

    /// <summary>
    /// 结论（APMD=2 / 地址 5/4/3 / 槽位 d0-a-d2）不够写档案用：作者还要知道"那一帧原样长什么样"。
    /// 少了这一段，收到包的人只能照着结论再猜一遍，等于把已经抓到的证据又丢了。
    /// </summary>
    [Fact]
    public void 每步的原始报文都留在evidence里()
    {
        var (w, m) = Perfect();
        var r = Analyze(w, m);

        var perf = r.Evidence["perf"]!.AsObject();
        Assert.Equal(2, perf["office"]!["apmd"]!.GetValue<int>());
        Assert.Equal(1, perf["game"]!["apmd"]!.GetValue<int>());
        Assert.Equal(0, perf["beast"]!["apmd"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(perf["office"]!["raw"]!.GetValue<string>()));

        // 三个分区各一条（按内容去重，同一地址不同颜色才算两条）
        Assert.Equal(3, r.Evidence["keyboard"]!.AsArray().Count);
        Assert.NotEmpty(r.Evidence["fan"]!.AsArray());
        Assert.Equal(new[] { "a", "d0", "d2" },
            r.Evidence["fan_slots"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    /// <summary>
    /// 0x0300 里除了 d0（APMD）还有字节随档位变：那多半就是原厂的第二个参数。
    /// 我们只写 d0 ⇒ 这种机器"档位换了、功耗墙没换"。发现就要在 notes 里点名，
    /// 不能等用户报"切了狂暴还是烫"再回头查。
    /// </summary>
    [Fact]
    public void d0之外的字节随档变化时点名要补第二参数()
    {
        var writes = new List<ObservedWrite>
        {
            W(OemObservation.SubPerf, T0.AddSeconds(2), (4, 2), (8, 2)),
            W(OemObservation.SubPerf, T0.AddSeconds(5), (4, 1), (8, 1)),
            W(OemObservation.SubPerf, T0.AddSeconds(8), (4, 0), (8, 0)),
        };
        var marks = new List<GuideMark> { M("perf_office", 3), M("perf_game", 6), M("perf_beast", 9) };

        var r = Analyze(writes, marks);

        Assert.Contains(r.Notes, n => n.Contains("偏移 8"));
        Assert.Contains(r.Notes, n => n.Contains("dat2"));
    }

    /// <summary>
    /// 槽位与通用档案不一致时 caps 是关的，但**档案里必须留下抓到的真实槽位** ——
    /// 否则作者只知道"对不上"，不知道对不上的是哪几个字节，只能再让对方采一次。
    /// </summary>
    [Fact]
    public void 落档时把原厂槽位写进ec_fan_slots()
    {
        var (w, m) = Perfect();
        var r = Analyze(w, m);

        var profile = new JsonObject
        {
            ["ec"] = new JsonObject(),
            ["caps"] = new JsonObject(),
            ["adopt"] = new JsonObject(),
            ["notes"] = new JsonArray(),
        };

        AdoptGuide.Apply(profile, r);

        Assert.Equal(new[] { "a", "d0", "d2" },
            profile["ec"]!["fan_slots"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.NotNull(profile["adopt"]!["guide"]!["frames"]);
        Assert.Equal(3, profile["adopt"]!["guide"]!["frames"]!["perf"]!.AsObject().Count);
    }

    /// <summary>
    /// 观察到的槽位只是档案表的**子集**时，档案表不许被改。
    ///
    /// 档案表表达的是"写 UFA 该填哪几个字节、不用的那一路写 255"；观察只看得见
    /// "这次真写了转速的几路"。拿后者覆盖前者，两风扇机的 [d0/a/d2] 会被砍成
    /// [a/d0]（还按字典序排），导入成正式档案后按它写 UFA 就错位了（23:52 真机）。
    /// </summary>
    [Fact]
    public void 观察到的槽位只是档案表的子集时不改ec_fan_slots()
    {
        var writes = new List<ObservedWrite> { Fan(2, 60, 50, 0xFF) };
        var marks = new List<GuideMark> { M("fan", 3) };
        var r = Analyze(writes, marks);

        var profile = new JsonObject
        {
            ["ec"] = new JsonObject { ["fan_slots"] = new JsonArray("d0", "a", "d2") },
            ["caps"] = new JsonObject(),
            ["adopt"] = new JsonObject(),
            ["notes"] = new JsonArray(),
        };

        AdoptGuide.Apply(profile, r);

        Assert.Equal(new[] { "d0", "a", "d2" },
            profile["ec"]!["fan_slots"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
        Assert.Contains(profile["notes"]!.AsArray(), n => n!.ToString().Contains("保持不变"));
    }
}
