using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「加负载看哪路风扇起来」—— 这是唯一能给"哪一路对 CPU"的**因果**证据，
/// 也是最容易自欺的一段：固件按整机最高温同时驱动两路时，两路都会升，
/// 这时候要是还挑个"升得多的"当 CPU 风扇，就是把猜测包装成证据。
///
/// 钉死四件事：
///   · 只有一路明显响应 ⇒ 判成 CPU 风扇，并把它的偏移排到 fan1_rpm；
///   · 两路一起升 ⇒ **不判**（只留原因）；
///   · 负载没把功耗/温度拉起来 ⇒ 不判（离电、限频时常见）；
///   · 样本不足 ⇒ 不判。
/// </summary>
public class FanRoleAdvisorTests
{
    private static LayoutSample S(double power, double cpuTemp, double gpuTemp,
                                  params (int Index, uint Value)[] cells)
    {
        var arr = new uint[5];
        foreach (var (i, v) in cells) arr[i] = v;
        return new LayoutSample(power, cpuTemp, gpuTemp,
            new Dictionary<string, uint[]>(StringComparer.Ordinal) { ["status"] = arr });
    }

    /// <summary>空载：两路都在 1800 上下，CPU 8W / 45°C。</summary>
    private static List<LayoutSample> Idle(int frames = 8, uint fan1 = 1800, uint fan2 = 1750)
        => Enumerable.Range(0, frames).Select(_ => S(8, 45, 40, (2, fan1), (3, fan2))).ToList();

    /// <summary>负载：CPU 55W / 80°C，两路各按给定曲线走。</summary>
    private static List<LayoutSample> Loaded(Func<int, uint> fan1, Func<int, uint> fan2, int frames = 20)
        => Enumerable.Range(0, frames)
                     .Select(i => S(55, 82, 45, (2, fan1(i)), (3, fan2(i)))).ToList();

    private static readonly Dictionary<string, (int, int)> Both =
        new(StringComparer.Ordinal) { ["fan1_rpm"] = (12, 4), ["fan2_rpm"] = (16, 4) };

    [Fact]
    public void 只有一路跟着负载起来时判成CPU风扇()
    {
        var r = FanRoleAdvisor.Advise(Idle(), Loaded(i => (uint)(1800 + i * 60), _ => 1750), null, Both);

        Assert.True(r.Decided);
        Assert.Equal(12, r.Layout["fan1_rpm"].Offset);     // CPU 风扇
        Assert.Equal(16, r.Layout["fan2_rpm"].Offset);     // 剩下的那路
        Assert.Equal("high", r.Guesses.Single(g => g.Field == "fan1_rpm").Confidence);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void 一路升一路降时判升的那一路()
    {
        // 2026-11-15 第二次真机：status@12 +153 转、status@16 **−78** 转（另一路反而降了）。
        // 差值只有 231 转，但它不是"涨幅接近"—— 方向相反本来就说明得了问题。
        // 老实现用"差值 <300 就算接近"，会把这条也判成"分不清"。
        var r = FanRoleAdvisor.Advise(Idle(fan1: 3400, fan2: 2400),
                                      Loaded(i => (uint)(3400 + i * 12), i => (uint)(2400 - i * 5)),
                                      null, Both);

        Assert.True(r.Decided, string.Join(" | ", r.Notes));
        Assert.Equal(12, r.Layout["fan1_rpm"].Offset);
    }

    [Fact]
    public void 另一路的变化按方向写进结论()
    {
        // 2026-11-23 21:54 真机报告里这半句是 "另一路只升 -353 转" —— Gain 是有符号的，
        // 文案却写死了"只升"，方向和数字打架。这段 Why 会进 txt 报告、采集包、档案 guesses。
        var down = FanRoleAdvisor.Advise(Idle(fan1: 3800, fan2: 3900),
                                         Loaded(i => (uint)(3800 + i * 20), i => (uint)(3900 - i * 40)));
        var downWhy = down.Guesses.Single(g => g.Field == "fan1_rpm").Why;
        Assert.Contains("另一路反而降了 580 转", downWhy);
        Assert.DoesNotContain("只升 -", downWhy);

        // 抖动（±50 转以内）说"基本没动"，不报假涨幅
        var flat = FanRoleAdvisor.Advise(Idle(fan1: 3800, fan2: 3900),
                                         Loaded(i => (uint)(3800 + i * 60), _ => 3900));
        Assert.Contains("另一路基本没动", flat.Guesses.Single(g => g.Field == "fan1_rpm").Why);

        // 真的涨了才说"只升"，数字不带符号
        var up = FanRoleAdvisor.Advise(Idle(fan1: 3800, fan2: 3900),
                                       Loaded(i => (uint)(3800 + i * 60), i => (uint)(3900 + i * 20)));
        Assert.Contains("另一路只升 290 转", up.Guesses.Single(g => g.Field == "fan1_rpm").Why);
    }

    [Fact]
    public void 两路一起升时不判归属()
    {
        // 固件按整机最高温同时驱动：两路涨幅接近（这里 55/60 ≈ 92%）⇒ 谁对 CPU 看不出来
        var r = FanRoleAdvisor.Advise(Idle(), Loaded(i => (uint)(1800 + i * 60), i => (uint)(1750 + i * 55)));

        Assert.False(r.Decided);
        Assert.Empty(r.Layout);
        Assert.Contains(r.Missing, m => m.Contains("一起升"));
    }

    [Fact]
    public void 两路都在升但差得开时判涨幅大的为CPU风扇()
    {
        // 2026-11-15 真机（用户观察推动）：CPU 负载下 status@12 +1770、status@16 +1128，
        // 用户肉眼看 CPU 那路明显更快。以前"第二路 ≥50% 就算一起升"，把这条证据丢掉了。
        var r = FanRoleAdvisor.Advise(Idle(fan1: 3300, fan2: 2600),
                                      Loaded(i => (uint)(3300 + i * 90), i => (uint)(2600 + i * 57)),
                                      null, Both);

        Assert.True(r.Decided, string.Join(" | ", r.Notes));
        Assert.Equal(12, r.Layout["fan1_rpm"].Offset);
        Assert.Equal(16, r.Layout["fan2_rpm"].Offset);
        // 固件毕竟同时驱动了两路 ⇒ 只给 medium，让作者按原厂界面核一眼
        Assert.Equal("medium", r.Guesses.Single(g => g.Field == "fan1_rpm").Confidence);
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void 负载没把CPU拉起来时不判()
    {
        // 离电/限频：功耗与温度都没动，风扇当然也不动
        var idle = Idle();
        var loaded = Enumerable.Range(0, 20).Select(_ => S(8, 45, 40, (2, 1800u), (3, 1750u))).ToList();

        var r = FanRoleAdvisor.Advise(idle, loaded, null, Both);

        Assert.False(r.Decided);
        Assert.Contains(r.Missing, m => m.Contains("没把 CPU"));
    }

    [Fact]
    public void 样本不足时不判()
    {
        var r = FanRoleAdvisor.Advise(Idle(frames: 2), Loaded(i => (uint)(1800 + i * 60), _ => 1750, frames: 3));

        Assert.False(r.Decided);
        Assert.Contains(r.Missing, m => m.Contains("样本不足"));
    }

    [Fact]
    public void 没有已知的另一路时只认CPU风扇()
    {
        var r = FanRoleAdvisor.Advise(Idle(), Loaded(i => (uint)(1800 + i * 60), _ => 1750));

        Assert.True(r.Decided);
        Assert.True(r.Layout.ContainsKey("fan1_rpm"));
        Assert.False(r.Layout.ContainsKey("fan2_rpm"));
        Assert.Contains(r.Notes, n => n.Contains("只认出一路"));
    }

    [Fact]
    public void 涨得最多的字段不是转速时不改风扇顺序()
    {
        // 有些机器把温度按 ×100 存在 status 里：45°C → 82°C 就是 +3700，比风扇涨得猛得多。
        // 已知转速是 {12,16}，涨最多的在 8 号偏移 ⇒ 这说明"涨最多的不是风扇"，不判。
        var idle = Enumerable.Range(0, 8)
            .Select(_ => S(8, 45, 40, (1, 4500u), (2, 1800u), (3, 1750u))).ToList();
        var loaded = Enumerable.Range(0, 20)
            .Select(i => S(55, 82, 45, (1, (uint)(4500 + i * 180)), (2, (uint)(1800 + i * 60)), (3, 1750u)))
            .ToList();

        var r = FanRoleAdvisor.Advise(idle, loaded, null, Both);

        Assert.False(r.Decided);
        Assert.Contains(r.Missing, m => m.Contains("不在已认出的转速"));
    }

    [Fact]
    public void 没有已知转速时按涨得最多认但只给medium()
    {
        var r = FanRoleAdvisor.Advise(Idle(), Loaded(i => (uint)(1800 + i * 60), _ => 1750));

        Assert.True(r.Decided);
        Assert.Equal("medium", r.Guesses.Single(g => g.Field == "fan1_rpm").Confidence);
        Assert.Contains(r.Notes, n => n.Contains("先怀疑这一条"));
    }

    [Fact]
    public void 三风扇机型只定CPU那一路_其余注明分不出来()
    {
        // 三路都在跑，只有 CPU 那一路跟着负载起来
        var idle = Enumerable.Range(0, 8)
            .Select(_ => S(8, 45, 40, (2, 1600u), (3, 1550u), (4, 1500u))).ToList();
        var loaded = Enumerable.Range(0, 20)
            .Select(i => S(55, 82, 45, (2, (uint)(1600 + i * 60)), (3, 1550u), (4, 1500u)))
            .ToList();
        var known = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["fan1_rpm"] = (12, 4), ["fan2_rpm"] = (16, 4), ["fan3_rpm"] = (20, 4),
        };

        var r = FanRoleAdvisor.Advise(idle, loaded, null, known);

        Assert.True(r.Decided);
        Assert.Equal(12, r.Layout["fan1_rpm"].Offset);
        Assert.Equal(16, r.Layout["fan2_rpm"].Offset);
        Assert.Equal(20, r.Layout["fan3_rpm"].Offset);
        Assert.Contains(r.Notes, n => n.Contains("分不出来"));
    }

    // ---------------------------------------------------------------- 并进布局

    [Fact]
    public void 并进布局时按角色重排fan1fan2()
    {
        // 原来按偏移升序排成了 fan1@16 / fan2@12（反的）
        var advice = new LayoutAdvice(
            [],
            [new LayoutGuess("fan1_rpm", "status", 16, "medium", "按偏移升序"),
             new LayoutGuess("fan2_rpm", "status", 12, "medium", "按偏移升序")],
            new Dictionary<string, (int, int)>(StringComparer.Ordinal)
            {
                ["fan1_rpm"] = (16, 4),
                ["fan2_rpm"] = (12, 4),
            },
            [], true, []);

        var roles = FanRoleAdvisor.Advise(Idle(), Loaded(i => (uint)(1800 + i * 60), _ => 1750), null, Both);
        var merged = EcLayoutAdvisor.WithFanRoles(advice, roles);

        Assert.Equal(12, merged.Layout["fan1_rpm"].Offset);   // CPU 风扇排到 fan1
        Assert.Equal(16, merged.Layout["fan2_rpm"].Offset);
        // 旧的两条"按偏移升序"推断被替换掉，不留矛盾结论
        Assert.DoesNotContain(merged.Guesses, g => g.Why.Contains("按偏移升序"));
    }

    [Fact]
    public void 判不出来时布局一点都不动()
    {
        var advice = new LayoutAdvice(
            [],
            [new LayoutGuess("fan1_rpm", "status", 16, "medium", "按偏移升序")],
            new Dictionary<string, (int, int)>(StringComparer.Ordinal) { ["fan1_rpm"] = (16, 4) },
            [], true, []);

        var roles = FanRoleAdvisor.Advise(Idle(), Loaded(i => (uint)(1800 + i * 60), i => (uint)(1750 + i * 55)));
        var merged = EcLayoutAdvisor.WithFanRoles(advice, roles);

        Assert.Equal(16, merged.Layout["fan1_rpm"].Offset);    // 原样
        Assert.Contains(merged.Missing, m => m.Contains("一起升"));
    }
}
