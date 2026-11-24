using System.Text.Json.Nodes;
using QControl.Core.Data;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「一键机型档案采集」的三块纯逻辑：偏移推断 / 档案草案 / 用户档案加载。
///
/// 为什么这些必须有测试：它们决定"别的机型能不能显示出正确的温度"。
/// 偏移判错不会报错、不会崩，只会把某个寄存器当温度显示出来 —— 项目规矩里
/// 最不能接受的就是这个（宁可不显示，也不显示错的）。
/// </summary>
public class MachineAdoptTests
{
    // ---------------------------------------------------------------- 样本构造

    private static LayoutSample Sample(
        double cpuW, double cpuT, double gpuT,
        uint cpuTec, uint gpuTec, uint fan1, uint fan2, uint light)
        => new(cpuW, cpuT, gpuT, new Dictionary<string, uint[]>(StringComparer.Ordinal)
        {
            ["status"] = [cpuTec, gpuTec, fan1, fan2, light],
        });

    /// <summary>
    /// 一段"只压 CPU"的采样：CPU 功耗从 20 W 爬到 90 W，CPU 温度跟着涨，
    /// 显卡在待机（温度一点没动）。
    ///
    /// 为什么拿这个当主样本：这才是**真实的一键采集场景** —— 用户在窗口里点一下按钮，
    /// 机器上跑着的是浏览器/编译，不是游戏。CPU 与显卡一起升温的采样反而无法区分两路
    /// （两条单调曲线彼此相关都接近 1，那是数学上的必然，不是算法不够聪明）。
    /// </summary>
    private static List<LayoutSample> CpuLoadSamples()
    {
        double[] cpuW = [20, 28, 38, 48, 58, 68, 76, 84, 88, 90];
        double[] cpuTec = [52, 56, 61, 66, 70, 74, 77, 79, 80, 81];

        var list = new List<LayoutSample>();
        for (var i = 0; i < cpuW.Length; i++)
            list.Add(Sample(cpuW[i], double.NaN, gpuT: 45,
                (uint)cpuTec[i], gpuTec: 46u,
                fan1: (uint)(2000 + cpuTec[i] * 25), fan2: (uint)(2200 + cpuTec[i] * 18),
                light: 2));
        return list;
    }

    /// <summary>只压显卡的采样：CPU 功耗与 EC 的 CPU 温度都不动，显卡温度线性上升。</summary>
    private static List<LayoutSample> GpuLoadSamples()
    {
        double[] gpuT = [40, 45, 50, 55, 60, 65, 68, 71, 73, 74];
        double[] gpuTec = [38, 43, 48, 53, 58, 63, 66, 69, 71, 72];

        var list = new List<LayoutSample>();
        for (var i = 0; i < gpuT.Length; i++)
            list.Add(Sample(cpuW: 15, cpuT: double.NaN, gpuT[i],
                cpuTec: 48u, gpuTec: (uint)gpuTec[i],
                fan1: (uint)(2000 + gpuTec[i] * 20), fan2: 2200, light: 2));
        return list;
    }

    // ---------------------------------------------------------------- 推断

    [Fact]
    public void 只压CPU时认出cpu_temp()
    {
        var advice = EcLayoutAdvisor.Advise(CpuLoadSamples());

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), "应认出 cpu_temp：" + Dump(advice));
        Assert.Equal(4, advice.Layout["cpu_temp"].Offset);
        Assert.True(advice.LayoutVerified, "认出一路温度就该生效：" + Dump(advice));
        // 显卡在待机、温度没动 ⇒ 不该硬塞一个 gpu_temp 进去
        Assert.False(advice.Layout.ContainsKey("gpu_temp"), Dump(advice));
    }

    [Fact]
    public void 只压显卡时认出gpu_temp()
    {
        var advice = EcLayoutAdvisor.Advise(GpuLoadSamples());

        Assert.True(advice.Layout.ContainsKey("gpu_temp"), "应认出 gpu_temp：" + Dump(advice));
        Assert.Equal(8, advice.Layout["gpu_temp"].Offset);
        Assert.False(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
    }

    [Fact]
    public void 有负载时两路风扇按偏移升序填()
    {
        var advice = EcLayoutAdvisor.Advise(CpuLoadSamples());

        Assert.True(advice.Layout.ContainsKey("fan1_rpm"));
        Assert.True(advice.Layout.ContainsKey("fan2_rpm"));
        Assert.Equal(12, advice.Layout["fan1_rpm"].Offset);
        Assert.Equal(16, advice.Layout["fan2_rpm"].Offset);
    }

    [Fact]
    public void 只认出CPU温度也算标定成功()
    {
        // 真实场景：采集那十几秒里只有 CPU 在忙，显卡在待机（温度一点没动）。
        // 这时硬要求"两路齐"等于这功能在正常使用下永远不生效 —— 认出一路就该生效。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(Sample(cpuW: 20 + i * 8, cpuT: double.NaN, gpuT: 45,
                               cpuTec: (uint)(50 + i * 3), gpuTec: 45,
                               fan1: 2000, fan2: 2100, light: 2));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
        // 显卡那一路全程 45 恒温、也差不到 1°C，但**没有 CPU 参照**可用 ⇒
        // 绝对对齐判据直接不启动（两路参照都在才判），所以这里仍不许填 gpu_temp。
        Assert.False(advice.Layout.ContainsKey("gpu_temp"));
        Assert.True(advice.LayoutVerified, "认出一路温度就该让 layout_verified 为 true：" + Dump(advice));
    }

    [Fact]
    public void 真机采样里两路温度靠逐帧对齐认出来()
    {
        // 2026-11-14 19:49 本机只读采集的真实 16 帧（cpu_temp 取自 HWiNFO 的 CPU Package）：
        // EC status@4 = 69…66，HWiNFO 65…67 —— 中位差 1°C，但头两帧差 4°C（两边更新节奏不同）。
        // 90 分位卡 3°C 会把这一整路判掉，而它明明就是 CPU 温度。
        uint[] ecCpu = [69, 69, 69, 69, 69, 66, 66, 66, 66, 66, 66, 66, 66, 66, 66, 66];
        double[] hwCpu = [65, 65, 67, 67, 67, 67, 67, 65, 65, 65, 67, 67, 65, 65, 65, 64];
        uint[] ecGpu = [58, 58, 58, 58, 58, 58, 58, 58, 58, 58, 58, 58, 58, 58, 57, 58];
        double[] nvGpu = [59, 58, 60, 60, 60, 60, 58, 60, 59, 59, 60, 59, 60, 59, 60, 58];

        var samples = new List<LayoutSample>();
        for (var i = 0; i < ecCpu.Length; i++)
            samples.Add(Sample(cpuW: 30 + i, hwCpu[i], nvGpu[i],
                               ecCpu[i], ecGpu[i], fan1: 3400, fan2: 2500, light: 2));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
        Assert.Equal(4, advice.Layout["cpu_temp"].Offset);
        Assert.True(advice.Layout.ContainsKey("gpu_temp"), Dump(advice));
        Assert.Equal(8, advice.Layout["gpu_temp"].Offset);
        Assert.DoesNotContain(advice.Missing, m => m.Contains("没有任何偏移像温度"));
    }

    [Fact]
    public void 参照中途跳变十几度也认得出CPU温度()
    {
        // 2026-11-15 13:38 真机采集：EC 的 status@4 从 65 走到 66，而 HWiNFO 的 CPU Package
        // 在 4 帧里跳到了 77（瞬时值先动、EC 的平滑值后跟）。中位差 0.0°C，但 16 帧里 4 帧差 11~12。
        // 要求"九成帧都对得上"会把这一整路判掉 —— 而它明明就是 CPU 温度。
        uint[] ecCpu = [65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 66, 66, 66, 66];
        double[] hwCpu = [65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 65, 77, 77, 77, 77, 64];
        uint[] ecGpu = [56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56];
        double[] nvGpu = [57, 57, 58, 58, 58, 57, 58, 57, 58, 58, 58, 57, 58, 57, 56, 58];

        var samples = new List<LayoutSample>();
        for (var i = 0; i < ecCpu.Length; i++)
            samples.Add(Sample(cpuW: 25 + i, hwCpu[i], nvGpu[i],
                               ecCpu[i], ecGpu[i], fan1: 3400, fan2: 2500, light: 2));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
        Assert.Equal(4, advice.Layout["cpu_temp"].Offset);
        Assert.True(advice.Layout.ContainsKey("gpu_temp"), Dump(advice));
        Assert.Equal(8, advice.Layout["gpu_temp"].Offset);
        Assert.DoesNotContain(advice.Missing, m => m.Contains("CPU 温度"));
    }

    [Fact]
    public void 温度全程不动时靠逐帧相等认出CPU温度()
    {
        // 2026-11-14 本机真实采集的样子：20 秒里 CPU 一直是 58°C、显卡 46/47°C，
        // EC 的 status@4 也一直是 58。相关性因为方差为 0 算不出来（NaN），
        // 旧实现于是写"这段采样里没有任何偏移像温度，跑个负载再采一次"——
        // 可温度明明就在眼前，逐帧都对得上。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 16; i++)
            samples.Add(Sample(cpuW: 16 + i % 5, cpuT: 58, gpuT: i % 2 == 0 ? 47 : 46,
                               cpuTec: 58, gpuTec: 46,
                               fan1: 2600, fan2: 2000, light: 2));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
        Assert.Equal(4, advice.Layout["cpu_temp"].Offset);
        Assert.True(advice.LayoutVerified, Dump(advice));
        // 关键：不许一边列出认出来的温度、一边劝用户"跑个负载再采一次"
        Assert.DoesNotContain(advice.Missing, m => m.Contains("没有任何偏移像温度"));
    }

    [Fact]
    public void 空载时温度不动就一条温度都不填()
    {
        // 功耗/显卡温度都在动（刚开机还在爬），但 EC 里的读数是平的 —— 这时"认不出来"才是正确答案
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(Sample(cpuW: 20 + i * 3, cpuT: double.NaN, gpuT: 30 + i, cpuTec: 50, gpuTec: 40,
                               fan1: 2000, fan2: 2100, light: 2));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.False(advice.Layout.ContainsKey("cpu_temp"));
        Assert.False(advice.Layout.ContainsKey("gpu_temp"));
        Assert.False(advice.LayoutVerified);
        Assert.Contains(advice.Missing, m => m.Contains("没动") || m.Contains("采样"));
    }

    [Fact]
    public void 没有原生参照时字段在动也不判温度()
    {
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(Sample(cpuW: double.NaN, cpuT: double.NaN, gpuT: double.NaN,
                               cpuTec: (uint)(45 + i * 3), gpuTec: (uint)(40 + i * 2),
                               fan1: (uint)(2000 + i * 100), fan2: 2100, light: 2));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.False(advice.Layout.ContainsKey("cpu_temp"), "没有参照就不该下结论：" + Dump(advice));
        Assert.False(advice.LayoutVerified);
    }

    [Fact]
    public void 中等相关也认出cpu_temp且标为medium()
    {
        // 真实场景：EC 温度寄存器更新慢、与快照参照错位半秒，相关只能到 0.6~0.75。
        // 这种不该"认不出"（旧阈值 0.75 会漏掉），但也不能标成 high 骗人 —— 标 medium 交用户核对。
        double[] cpuW = [50, 56, 62, 68, 74, 80, 86, 92, 98, 104];
        double[] cpuTec = cpuW.Select((w, i) => w + 20 * Math.Sin(i * 1.3)).ToArray();

        var samples = new List<LayoutSample>();
        for (var i = 0; i < cpuW.Length; i++)
            samples.Add(Sample(cpuW[i], double.NaN, gpuT: 45,
                               (uint)Math.Round(cpuTec[i]), gpuTec: 46u,
                               fan1: 2000, fan2: 2100, light: 2));

        // 自证这条序列的相关确实落在区间内（否则测试本身构造错了）
        var r = Pearson(cpuW, cpuTec);
        Assert.True(r is > 0.60 and < 0.75, $"测试样本相关应为 0.60~0.75，实际 {r:0.00}");

        var advice = EcLayoutAdvisor.Advise(samples);
        Assert.True(advice.Layout.ContainsKey("cpu_temp"), "中等相关也应认出 cpu_temp：" + Dump(advice));
        var g = advice.Guesses.FirstOrDefault(x => x.Field == "cpu_temp");
        Assert.NotNull(g);
        Assert.Equal("medium", g.Confidence);
    }

    private static double Pearson(double[] a, double[] b)
    {
        var n = a.Length;
        var ma = a.Average();
        var mb = b.Average();
        var num = a.Zip(b, (x, y) => (x - ma) * (y - mb)).Sum();
        var da = Math.Sqrt(a.Sum(x => (x - ma) * (x - ma)));
        var db = Math.Sqrt(b.Sum(y => (y - mb) * (y - mb)));
        return num / (da * db);
    }

    [Fact]
    public void 采样太少时直接如实说采不到()
    {
        var advice = EcLayoutAdvisor.Advise([Sample(20, 50, 40, 50, 40, 2000, 2100, 2)]);

        Assert.Empty(advice.Layout);
        Assert.False(advice.LayoutVerified);
        Assert.NotEmpty(advice.Missing);
    }

    [Fact]
    public void 采样期间背光档位真变了才认()
    {
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
            samples.Add(Sample(20, double.NaN, 30, 50, 40, 2000, 2100, light: (uint)(i < 5 ? 1 : 2)));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("backlight_level"));
        Assert.Equal(20, advice.Layout["backlight_level"].Offset);
    }

    [Fact]
    public void 标准槽没有温度时补扫数据区尾部()
    {
        // 别的广达模具把 CPU 温度放在 @24（DAT7），标准槽 @4~@20 全是 0。
        // 早先只扫前 5 个 u32 ⇒ 这台永远"没有任何偏移像温度"，自动标定等于失效。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
        {
            var cells = new uint[7];
            cells[5] = (uint)(50 + i * 3);          // @24 跟着 CPU 功耗走
            samples.Add(new LayoutSample(20 + i * 8, double.NaN, 45,
                new Dictionary<string, uint[]>(StringComparer.Ordinal) { ["status"] = cells }));
        }

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
        Assert.Equal(24, advice.Layout["cpu_temp"].Offset);
        Assert.True(advice.LayoutVerified, Dump(advice));
        // 不许一边认出温度、一边留着"没有任何偏移像温度"
        Assert.DoesNotContain(advice.Missing, m => m.Contains("没有任何偏移像温度"));
        Assert.Contains(advice.Notes, n => n.Contains("后半段"));
    }

    [Fact]
    public void 标准槽已认出温度时不看尾部槽()
    {
        // 标准槽 @4 已经认出来了 ⇒ 尾部 @24 那个同样像温度的值一次都不该被采用：
        // 尾部那一遍只做兜底，绝不能把已验证机型上更可信的结论挤掉。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
        {
            var cells = new uint[7];
            cells[0] = (uint)(50 + i * 3);              // @4 真 CPU 温度（跟着功耗走）
            cells[5] = (uint)(70 + (i % 3) * 5);        // @24 也在 70~80 之间抖，但跟功耗无关
            samples.Add(new LayoutSample(20 + i * 8, double.NaN, 45,
                new Dictionary<string, uint[]>(StringComparer.Ordinal) { ["status"] = cells }));
        }

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
        Assert.Equal(4, advice.Layout["cpu_temp"].Offset);
        Assert.DoesNotContain(advice.Notes, n => n.Contains("后半段"));
    }

    [Fact]
    public void 只认status帧里的偏移别的命令不参与layout()
    {
        // 2026-11-16 真机踩到：`gpu_info` 帧的 DAT2 确实是显卡温度，被写成 `gpu_temp: [4,4]`，
        // 与 `cpu_temp: [4,4]` 撞在同一个槽 —— layout 没有"来源命令"这一维，
        // 读的时候永远读 status ⇒ 界面上"显卡温度"显示的是 CPU 温度，而且不报错。
        var samples = new List<LayoutSample>();
        for (var i = 0; i < 10; i++)
        {
            var status = new uint[7];
            status[0] = 62;                        // @4 CPU 温度
            status[1] = (uint)(56 + i % 2);        // @8 显卡温度（差 1°C）
            status[2] = 2100;
            status[3] = 2200;
            status[4] = 2;

            var gpuInfo = new uint[7];
            gpuInfo[0] = 55;                       // @4 恰好等于显卡温度（最容易骗过判据）

            samples.Add(new LayoutSample(double.NaN, 62, 55,
                new Dictionary<string, uint[]>(StringComparer.Ordinal)
                {
                    ["status"] = status,
                    ["gpu_info"] = gpuInfo,
                }));
        }

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("gpu_temp"), Dump(advice));
        Assert.Equal(8, advice.Layout["gpu_temp"].Offset);
        Assert.DoesNotContain(advice.Guesses, g => g.Cmd == "gpu_info");
    }

    [Fact]
    public void 恒定相差三度的EC读数也认作CPU温度()
    {
        // 2026-11-17 13:42 真机（没开原厂那次）真实 16 帧：
        // EC 的 status@4 一直是 68，HWiNFO 的 CPU Package 在 64~67、偶尔跳到 78/80。
        // 逐帧差：11 帧 ≤4°C（这些帧中位差 **3.0**）、其余是跳变。旧规则"中位差 ≤2"把这一整路
        // 判掉，结果一份完整采集写不出 cpu_temp —— 而它明明就是 CPU 温度。
        // EC 是平滑值，和外部读数有 2~4°C 的固定偏差是常态。
        uint[] ecCpu = [68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68, 68];
        double[] hwCpu = [65, 64, 64, 64, 64, 64, 80, 80, 80, 80, 67, 67, 67, 67, 67, 78];
        uint[] ecGpu = [54, 54, 54, 54, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55, 55];
        double[] nvGpu = [55, 55, 55, 55, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56, 56];

        var samples = new List<LayoutSample>();
        for (var i = 0; i < ecCpu.Length; i++)
            samples.Add(Sample(cpuW: 20 + i, hwCpu[i], nvGpu[i],
                               ecCpu[i], ecGpu[i], fan1: 3900, fan2: 2400, light: 2));

        var advice = EcLayoutAdvisor.Advise(samples);

        Assert.True(advice.Layout.ContainsKey("cpu_temp"), Dump(advice));
        Assert.Equal(4, advice.Layout["cpu_temp"].Offset);
        Assert.True(advice.Layout.ContainsKey("gpu_temp"), Dump(advice));
        Assert.Equal(8, advice.Layout["gpu_temp"].Offset);
    }

    // ---------------------------------------------------------------- 草案

    private static JsonObject Identity() => new()
    {
        ["system_vendor"] = "COLORFUL",
        ["system_product"] = "G16 Pro",
        ["board_vendor"] = "COLORFUL",
        ["vendor_registry"] = new JsonObject { ["NL5"] = true },
    };

    private static JsonObject Candidates() => new()
    {
        ["NL5"] = new JsonObject
        {
            ["bits"] = "64",
            ["sub"] = new JsonArray("ControlCenter"),
        },
    };

    private static JsonObject Draft(LayoutAdvice advice) => DeviceProfileDraft.Build(
        Identity(), Candidates(),
        EmbeddedData.ReadObject("devices/quanta-gmwmi-generic.json"),
        advice, "0.1.0", samples: 10,
        refs: new JsonObject { ["cpu_power"] = true, ["cpu_temp"] = false, ["gpu_temp"] = true },
        selfCheck: []);

    [Fact]
    public void 草案用项目号生成本机档案且写入恒关()
    {
        var advice = EcLayoutAdvisor.Advise(CpuLoadSamples());
        var profile = Draft(advice);

        Assert.Equal("quanta-nl5", profile["id"]!.GetValue<string>());
        Assert.False(profile["verified"]!.GetValue<bool>(), "采集不能给自己发写权限");
        Assert.False(profile["writes_allowed"]!.GetValue<bool>(), "采集不能给自己发写权限");
        Assert.True(profile["priority"]!.GetValue<double>() >= DeviceProfile.UserPriorityFloor);

        var any = profile["match"]!["any"]!.AsArray();
        Assert.Equal("^NL5$", any[0]!["vendor_registry"]![0]!.GetValue<string>());

        var ec = profile["ec"]!.AsObject();
        Assert.True(ec["layout_verified"]!.GetValue<bool>());
        Assert.Equal(4, ec["layout"]!["cpu_temp"]![0]!.GetValue<int>());
        // 命令号照抄 generic：能采到帧就说明这台机器本来就在用 generic 的命令表
        Assert.Equal("0x0205", ec["cmd"]!["fan"]!.GetValue<string>());
    }

    [Fact]
    public void 备注里的高置信只说guesses里真是high的那几项()
    {
        // 2026-11-23 12:21 真机：`cpu_temp` 在 adopt.guesses 里是 medium（16 帧全是 55，
        // EC 那一路一次没变，只证明数值对得上），档案备注却写"已高置信认出：CPU 温度 + …"。
        // 这句话是给作者/用户看的结论，必须由 guesses 推出来，不能写死。
        var profile = Draft(EcLayoutAdvisor.Advise(CpuLoadSamples()));

        var guesses = profile["adopt"]!["guesses"]!.AsArray()
            .Where(g => g is JsonObject)
            .Select(g => (Field: g!["field"]!.GetValue<string>(),
                          Conf: g["confidence"]!.GetValue<string>()))
            .ToList();
        var line = profile["notes"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Single(n => n.Contains("layout_verified=true"));
        var head = line.Split('⇒')[0];

        (string Field, string Label)[] map =
            [("cpu_temp", "CPU 温度"), ("gpu_temp", "显卡温度"), ("backlight_level", "背光")];
        var high = map.Where(m => guesses.Any(g => g.Field == m.Field && g.Conf == "high"))
                      .Select(m => m.Label).ToList();

        if (high.Count > 0) Assert.Contains(string.Join(" + ", high), head);
        else Assert.DoesNotContain("已高置信认出", head);
    }

    [Fact]
    public void 草案认不准时layout_verified保持false()
    {
        var advice = EcLayoutAdvisor.Advise([]);   // 空采样 = 什么都没认出来
        var profile = Draft(advice);

        Assert.False(profile["ec"]!["layout_verified"]!.GetValue<bool>());
        Assert.Empty(profile["ec"]!["layout"]!.AsObject());
        Assert.NotEmpty(profile["adopt"]!["missing"]!.AsArray());
    }

    // ---------------------------------------------------------------- 用户档案加载

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qcontrol-adopt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 用户档案会被加载且不会抢掉已验证机型()
    {
        var dir = NewDir();
        try
        {
            var advice = EcLayoutAdvisor.Advise(CpuLoadSamples());
            var profile = Draft(advice);
            profile["id"] = "quanta-test01";   // 换 id：模拟"另一台未适配机器"采出来的档案
            profile["priority"] = 1;           // 故意写小：必须被抬到下限，否则会被 generic 抢先
            File.WriteAllText(Path.Combine(dir, "quanta-test01.json"), profile.ToJsonString());

            var (profiles, skipped) = DeviceProfile.LoadAllDetailed(dir);

            Assert.Empty(skipped);
            var mine = profiles.Single(p => p.Id == "quanta-test01");
            Assert.True(mine.FromUser, "这份是从用户目录加载的");
            Assert.True(mine.Priority >= DeviceProfile.UserPriorityFloor);

            var order = profiles.Select(p => p.Id).ToList();
            Assert.True(order.IndexOf("quanta-test01") > order.IndexOf("quanta-nl5"),
                "已验证的机型档案必须排在用户采集的档案前面（否则采集会把正确标定顶掉）："
                + string.Join(" > ", order));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 未验证采集产物不得顶掉已验证内置档案()
    {
        var dir = NewDir();
        try
        {
            // 模拟「一键采集」产物：同 id、verified=false、priority 50（被抬到下限）
            File.WriteAllText(Path.Combine(dir, "quanta-nl5.json"),
                """{"id":"quanta-nl5","name":"（一键采集产物）","verified":false,"priority":50}""");

            var profiles = DeviceProfile.LoadAll(dir);
            var nl5 = profiles.Single(p => p.Id == "quanta-nl5");

            // 内置那份 verified=true；未验证的采集产物顶不掉它
            Assert.True(nl5.Verified, "已验证内置档案必须保留，未验证采集产物不得覆盖");
            Assert.NotEqual("（一键采集产物）", nl5.Raw["name"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 已验证且更高优先级的用户档案可以覆盖内置()
    {
        var dir = NewDir();
        try
        {
            // 用户手调、明确已验证、优先级高于内置 —— 这种才可以覆盖
            File.WriteAllText(Path.Combine(dir, "quanta-nl5.json"),
                """{"id":"quanta-nl5","name":"（用户手调、已验证）","verified":true,"priority":200}""");

            var profiles = DeviceProfile.LoadAll(dir);
            var nl5 = profiles.Single(p => p.Id == "quanta-nl5");

            Assert.True(nl5.Verified);
            Assert.Equal("（用户手调、已验证）", nl5.Raw["name"]!.GetValue<string>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 坏档案只跳过不影响其它档案()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ 这不是 json");
            File.WriteAllText(Path.Combine(dir, "no-id.json"), """{"name":"没有 id"}""");

            var (profiles, skipped) = DeviceProfile.LoadAllDetailed(dir);

            Assert.Equal(2, skipped.Count);
            Assert.Contains(profiles, p => p.Id == "quanta-gmwmi-generic");
            Assert.Contains(profiles, p => p.Id == "quanta-nl5");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------- 小工具

    private static string Dump(LayoutAdvice a)
        => string.Join(" | ", a.Guesses.Select(g => $"{g.Field}@{g.Offset}:{g.Confidence}"))
           + " || " + string.Join(" ; ", a.Notes);
}
