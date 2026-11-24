using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 偏移推断的**合并**那一步：稳态采样一段、加 CPU 负载一段，两段各自出结论再并起来。
/// 并的时候最容易出的错是"只增不减" —— 新结论进来了，被它取代的旧结论还挂在报告上，
/// 同一个偏移于是有两行互相打脸的话（2026-11-23 23:03 真机就是 status@8 双份结论）。
/// </summary>
public class EcLayoutAdvisorTests
{
    private static LayoutAdvice Advice(
        LayoutGuess[]? guesses = null,
        Dictionary<string, (int, int)>? layout = null,
        string[]? notes = null,
        bool verified = false)
        => new([],
               guesses ?? [],
               layout ?? new Dictionary<string, (int, int)>(StringComparer.Ordinal),
               notes ?? [], verified, []);

    /// <summary>稳态段那条泛温度结论（本机 21:53 那份报告的原文）。</summary>
    private static LayoutGuess GenericTemp()
        => new("temp", "status", 8, "medium",
               "值域 49~53（幅度 4），与 CPU 功耗 相关 0.79；与 CPU、显卡都相关，分不清是哪一路");

    [Fact]
    public void 负载段补上显卡温度后撤回同一偏移的泛温度结论()
    {
        // 2026-11-23 23:03 真机：稳态段把 status@8 记成"分不清哪一路"，
        // 加负载那段又把它认成 gpu_temp[high] —— 两行都留在了报告的「自动识别出的偏移」里。
        var steady = Advice([GenericTemp()]);

        var loaded = Advice(
            [new LayoutGuess("gpu_temp", "status", 8, "high",
                             "值域 48~53（幅度 5），与 显卡温度 相关 0.82")],
            new Dictionary<string, (int, int)>(StringComparer.Ordinal) { ["gpu_temp"] = (8, 4) });

        var r = EcLayoutAdvisor.WithMissingTemps(steady, loaded, 116);

        Assert.Equal((8, 4), r.Layout["gpu_temp"]);
        // 同一偏移只能剩一条结论，而且是更具体的那条
        Assert.DoesNotContain(r.Guesses, g => g.Field == "temp");
        var only = r.Guesses.Single(g => g.Cmd == "status" && g.Offset == 8);
        Assert.Equal("gpu_temp", only.Field);
        Assert.Contains("加 CPU 负载", only.Why);
        Assert.DoesNotContain(r.Guesses, g => g.Why.Contains("分不清是哪一路"));
    }

    [Fact]
    public void 别的偏移上的泛温度结论照旧留着()
    {
        // 不同偏移上的"也像温度"是**另一条**证据（那个寄存器确实像温度），
        // 撤回只能是同一个 (命令, 偏移)，不能顺手清一片。
        var steady = Advice([GenericTemp()]);

        var loaded = Advice(
            [new LayoutGuess("gpu_temp", "status", 12, "high", "值域 48~53，与 显卡温度 相关 0.9")],
            new Dictionary<string, (int, int)>(StringComparer.Ordinal) { ["gpu_temp"] = (12, 4) });

        var r = EcLayoutAdvisor.WithMissingTemps(steady, loaded, 116);

        Assert.Contains(r.Guesses, g => g.Field == "temp" && g.Offset == 8);
        Assert.Contains(r.Guesses, g => g.Field == "gpu_temp" && g.Offset == 12);
    }

    [Fact]
    public void 补进来的是CPU温度时同样撤回泛结论()
    {
        // 分支对称：不管补的是 cpu_temp 还是 gpu_temp，被它取代的泛结论都要撤
        var steady = Advice([GenericTemp()]);

        var loaded = Advice(
            [new LayoutGuess("cpu_temp", "status", 8, "high", "值域 60~78，与 CPU 温度 相关 0.88")],
            new Dictionary<string, (int, int)>(StringComparer.Ordinal) { ["cpu_temp"] = (8, 4) });

        var r = EcLayoutAdvisor.WithMissingTemps(steady, loaded, 116);

        Assert.Equal((8, 4), r.Layout["cpu_temp"]);
        Assert.DoesNotContain(r.Guesses, g => g.Field == "temp");
    }

    // ---------------------------------------------------------------- 合并后旧结论要撤回

    private const string StaleNote =
        "一路温度都没高置信认出来 ⇒ 档案里 layout_verified 保持 false：程序只回原始字节、"
        + "不显示温度。这是故意的 —— 偏移猜错就是把别的寄存器当温度显示。";

    private static LayoutAdvice GpuFromLoad()
        => Advice([new LayoutGuess("gpu_temp", "status", 8, "high", "值域 48~53（幅度 5），与 显卡温度 相关 0.82")],
                  new Dictionary<string, (int, int)>(StringComparer.Ordinal) { ["gpu_temp"] = (8, 4) });

    [Fact]
    public void 补上温度后撤掉layout_verified保持false那句()
    {
        // 2026-11-23 23:03 真机：报告上方写着"保持 false、程序不显示温度"，
        // 同一个包里 layout_verified = true、layout 里两路温度都在。
        var steady = Advice(notes: [StaleNote]);

        var r = EcLayoutAdvisor.WithMissingTemps(steady, GpuFromLoad(), 116);

        Assert.True(r.LayoutVerified);
        Assert.DoesNotContain(r.Notes, n => n.Contains("layout_verified 保持 false"));
    }

    [Fact]
    public void 背光写探针证实后也撤掉那句()
    {
        // 同一条纪律：只要合并让 Verified 变 true，那句就过期。这里由写探针置的 true。
        var steady = Advice(notes: [StaleNote]);

        var r = EcLayoutAdvisor.WithBacklightProbe(steady, 20, 4, "写入 0/2 档后读回一致");

        Assert.True(r.LayoutVerified);
        Assert.DoesNotContain(r.Notes, n => n.Contains("layout_verified 保持 false"));
    }

    [Fact]
    public void 只认出一路时不断言缺的那路读数不显示()
    {
        // 推断层看不到档案：只读采集不覆盖已有档案，档案里本来就有那一项的话读数照旧。
        // 原来写死"缺的那路读数不会出现"，而 23:03 那份包里的 profile_draft 就带着 cpu_temp@4。
        var r = EcLayoutAdvisor.WithMissingTemps(Advice(), GpuFromLoad(), 116);

        var note = r.Notes.Single(n => n.StartsWith("只认出了", StringComparison.Ordinal));
        Assert.Contains("显卡温度", note);
        Assert.DoesNotContain("不会出现", note);
    }

    // ---------------------------------------------------------------- 风扇布局被换掉之后

    private const string FanOrderNote =
        "两路风扇（status@12 / status@16）谁对 CPU 是按偏移顺序假设的；"
        + "两路转速一直相同就分不出来 —— 看散热页两个数字跟原厂对不对得上。";

    private static LayoutAdvice TwoFans() => Advice(
        layout: new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["fan1_rpm"] = (12, 4),
            ["fan2_rpm"] = (16, 4),
        },
        notes: [FanOrderNote]);

    [Fact]
    public void 判出风扇归属后撤掉按偏移顺序假设那句()
    {
        // 布局被重排到 @16/@12 之后，那句 note 里的两个偏移已经不代表实际结论了。
        var roles = new FanRoleAdvice(
            new Dictionary<string, (int, int)>(StringComparer.Ordinal)
            {
                ["fan1_rpm"] = (16, 4),
                ["fan2_rpm"] = (12, 4),
            },
            [new LayoutGuess("fan1_rpm", "status", 16, "medium", "加负载后这一路涨得更多")],
            ["负载法认定：CPU 风扇在 status@16（加 CPU 负载后 +192 转）"],
            [], true);

        var r = EcLayoutAdvisor.WithFanRoles(TwoFans(), roles);

        Assert.DoesNotContain(r.Notes, n => n.StartsWith("两路风扇（", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n => n.Contains("负载法认定"));
        Assert.Equal(16, r.Layout["fan1_rpm"].Offset);
    }

    [Fact]
    public void 引导认出转速后也撤掉按偏移顺序假设那句()
    {
        // 引导那一步（靠变速形状）把转速认到 @20/@24 ⇒ 旧 note 的两个偏移不在 layout 里了。
        var fans = new FanAdvice(
            new Dictionary<string, (int, int)>(StringComparer.Ordinal)
            {
                ["fan1_rpm"] = (20, 4),
                ["fan2_rpm"] = (24, 4),
            },
            [new LayoutGuess("fan1_rpm", "status", 20, "medium", "靠变速形状认的")],
            ["风扇转速认在 fan1_rpm ← status@20"], [], 2);

        var r = EcLayoutAdvisor.WithFans(TwoFans(), fans);

        Assert.DoesNotContain(r.Notes, n => n.StartsWith("两路风扇（", StringComparison.Ordinal));
        Assert.Equal(20, r.Layout["fan1_rpm"].Offset);
        Assert.Equal(24, r.Layout["fan2_rpm"].Offset);
    }

    [Fact]
    public void 尾部只补了一路转速时保留温度那条解释()
    {
        // 标准槽没有可用温度（@8 在 40~50 之间来回、与任何参照都不相关），
        // 数据区尾部 @24 是一路平滑升速的转速 ⇒ 合并只 added={fan1_rpm}，
        // layout_verified 仍为 false，那句"一路温度都没高置信认出来 ⇒ 保持 false"必须留着。
        // 以前 MergeTail 无条件删它：用户看到"温度不显示"，报告里却一个原因都没有。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 20; i++)
        {
            var arr = new uint[7];
            arr[1] = (uint)(70 + i % 6 * 2);      // @8：像温度、但与参照（45/40）离得远、也不相关
            arr[5] = (uint)(1600 + i * 100);      // @24：平滑升速
            samples.Add(new LayoutSample(8, 45, 40,
                new Dictionary<string, uint[]>(StringComparer.Ordinal) { ["status"] = arr }));
        }

        var r = EcLayoutAdvisor.Advise(samples);

        Assert.False(r.LayoutVerified);
        Assert.Equal(24, r.Layout["fan1_rpm"].Offset);
        Assert.Contains(r.Notes, n => n.StartsWith("一路温度都没高置信认出来", StringComparison.Ordinal));
    }
}
