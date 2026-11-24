using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 风扇转速标定 —— 一键采集能不能在包里带上 `fan1_rpm` / `fan2_rpm`，全看这一段。
///
/// 判据和温度那套**完全不同**：温度靠"跟谁一起动"（相关性），转速靠"变速的形状"
/// （一段平滑的斜坡）。这里钉死三件事：
///   · 平滑斜坡（真风扇）能被认出来，双风扇按偏移升序排成 fan1 / fan2；
///   · 一步跳到头的字段（显卡频率）不能被认成转速 —— 认错了界面上就多一个假转速表；
///   · 已经认给温度的偏移、或者和主路不同步的第二路，都不许再当转速。
/// </summary>
public class FanLayoutAdvisorTests
{
    /// <summary>一帧：帧里 5 个 u32（偏移 4/8/12/16/20），默认全 0。</summary>
    private static Dictionary<string, uint[]> Frame(params (int Index, uint Value)[] cells)
    {
        var arr = new uint[5];
        foreach (var (i, v) in cells) arr[i] = v;
        return new Dictionary<string, uint[]>(StringComparer.Ordinal) { ["status"] = arr };
    }

    private static LayoutSample S(Dictionary<string, uint[]> f)
        => new(double.NaN, double.NaN, double.NaN, f);

    /// <summary>一段平滑升速：10 帧里从 <paramref name="from"/> 一路挪到 <paramref name="to"/>。</summary>
    private static List<LayoutSample> Ramp(int index, uint from, uint to, int frames = 10)
    {
        var list = new List<LayoutSample>();
        for (var i = 0; i < frames; i++)
        {
            var v = (uint)(from + (to - from) * i / (frames - 1));
            list.Add(S(Frame((0, 60), (index, v), (1, 2))));   // 同一帧里带一个恒定的温度、一个恒定的档位
        }
        return list;
    }

    // ---------------------------------------------------------------- 正路

    [Fact]
    public void 两路平滑升速被认成双风扇_按偏移升序()
    {
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
        {
            // 偏移 12 与 16 一起升：真双风扇就是这样（一起升、一起降）
            var a = (uint)(1800 + i * 190);
            var b = (uint)(1750 + i * 200);
            samples.Add(S(Frame((0, 62), (2, a), (3, b), (1, 2))));
        }

        var r = FanLayoutAdvisor.Advise(samples);

        Assert.Equal(2, r.Count);
        Assert.Equal(12, r.Layout["fan1_rpm"].Offset);
        Assert.Equal(16, r.Layout["fan2_rpm"].Offset);
        // 两路同步 ⇒ high（有第二路交叉印证，不是单条曲线自说自话）
        Assert.Equal("high", r.Guesses.First(g => g.Field == "fan1_rpm").Confidence);
        // 恒定的温度/档位不能被当成转速
        Assert.DoesNotContain(r.Layout.Keys, k => k is not ("fan1_rpm" or "fan2_rpm"));
        Assert.Empty(r.Missing);
    }

    [Fact]
    public void 只认出一路时只给fan1_并且不改路数结论()
    {
        var r = FanLayoutAdvisor.Advise(Ramp(3, 1600, 3400));

        Assert.Equal(1, r.Count);
        Assert.True(r.Layout.ContainsKey("fan1_rpm"));
        Assert.False(r.Layout.ContainsKey("fan2_rpm"));
        Assert.Equal("medium", r.Guesses.Single(g => g.Field == "fan1_rpm").Confidence);
    }

    [Fact]
    public void 三风扇机型三路同步升速时都认出来()
    {
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
        {
            // 三路一起升（三风扇机型就是同一套散热逻辑驱动三路）+ 一个恒定温度
            var a = (uint)(1600 + i * 130);
            var b = (uint)(1550 + i * 135);
            var c = (uint)(1500 + i * 140);
            samples.Add(S(Frame((0, 62), (2, a), (3, b), (4, c))));
        }

        var r = FanLayoutAdvisor.Advise(samples);

        Assert.Equal(3, r.Count);
        Assert.Equal(12, r.Layout["fan1_rpm"].Offset);
        Assert.Equal(16, r.Layout["fan2_rpm"].Offset);
        Assert.Equal(20, r.Layout["fan3_rpm"].Offset);
        // 三路都同步 ⇒ 不能只当"恰好一起动"
        Assert.Contains(r.Notes, n => n.Contains("只画 CPU/GPU 两路"));
    }

    // ---------------------------------------------------------------- 别的模具：数据区尾部

    [Fact]
    public void 数据区尾部的风扇转速也认得出()
    {
        // 有些模具不把转速放在 DAT3/DAT4，而是放在数据区最后两个 u32（@24/@28）。
        // 早先只扫 @4~@20 ⇒ 这类机器无论怎么采都"认不出转速"。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
        {
            var arr = new uint[7];
            arr[0] = 62;                                // 恒定温度
            arr[1] = 2;                                 // 恒定背光档位
            arr[5] = (uint)(1600 + i * 200);            // @24 平滑升速
            samples.Add(S(new Dictionary<string, uint[]>(StringComparer.Ordinal) { ["status"] = arr }));
        }

        var r = FanLayoutAdvisor.Advise(samples);

        Assert.Equal(1, r.Count);
        Assert.Equal(24, r.Layout["fan1_rpm"].Offset);
    }

    // ---------------------------------------------------------------- 不能认的

    [Fact]
    public void 一步跳到底的字段不算转速_那更像显卡频率()
    {
        // 显卡时钟：210 → 900 → 1800 来回跳。值域落在 600~9000 里，但形状是"跳"不是"挪"。
        var samples = new List<LayoutSample>();
        foreach (var v in new uint[] { 210, 900, 1800, 210, 900, 1800, 210, 900, 1800, 210 })
            samples.Add(S(Frame((3, v), (0, 62))));

        var r = FanLayoutAdvisor.Advise(samples);

        Assert.Equal(0, r.Count);
        Assert.Empty(r.Layout);
        Assert.Contains(r.Missing, m => m.StartsWith("风扇转速", StringComparison.Ordinal));
    }

    [Fact]
    public void 已经认给温度的偏移不再当转速()
    {
        var taken = new (string, int)[] { ("status", 12) };

        var r = FanLayoutAdvisor.Advise(Ramp(2, 1600, 3400), taken);

        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void 两路不同步时只认变化更大的那一路()
    {
        // 一路升、一路降：真风扇不会这样。两条斜率都平滑，靠"同步性"把假的剔掉。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(S(Frame((2, (uint)(1200 + i * 200)), (3, (uint)(3200 - i * 200)))));

        var r = FanLayoutAdvisor.Advise(samples);

        Assert.Equal(1, r.Count);
        Assert.True(r.Layout.ContainsKey("fan1_rpm"));
        Assert.Contains(r.Notes, n => n.Contains("不同步", StringComparison.Ordinal));
    }

    [Fact]
    public void 样本太少时直说没标定_不硬猜()
    {
        var r = FanLayoutAdvisor.Advise(Ramp(3, 1600, 3400, frames: 4).Take(3).ToList());

        Assert.Equal(0, r.Count);
        Assert.Empty(r.Layout);
        Assert.Contains(r.Notes, n => n.Contains("样本", StringComparison.Ordinal));
    }
}
