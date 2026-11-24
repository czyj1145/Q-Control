using System.Text.Json.Nodes;
using QControl.Core.FanCurve;
using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// 风扇曲线状态机的回归测试 —— 全部用假硬件 + 假时钟，不需要真机。
///
/// 这些用例对应的都是**真实事故**：拿 0°C 插值把风扇钉在 30%、切模式后 6 秒没反应、
/// 停止时写下限导致 84°C 下 1500 RPM 锁死、写入被拒后界面仍显示"运行中"。
/// 曲线是散热安全阀，改这里必须过这些测试。
/// </summary>
public sealed class FanCurveEngineTests
{
    /// <summary>假硬件：记账所有写入，可注入异常。</summary>
    private sealed class FakeHw : IFanHardware
    {
        public bool WritesEnabled { get; set; } = true;
        public bool WritesAllowed { get; set; } = true;
        public bool AdminOk { get; set; } = true;
        public string? AdminHint => null;
        public int? DutyApplied { get; set; }

        /// <summary>临时满转态（未开曲线时点了「最大转速」，见 EcHal.SetOemFanMax）。</summary>
        public bool OemFanMax { get; set; }

        /// <summary>CPU 那一路（UFA1）真正写进 EC 的值序列 —— 兼容旧断言。</summary>
        public List<int> Writes { get; } = [];
        /// <summary>GPU 那一路（UFA2）真正写进 EC 的值序列。</summary>
        public List<int> GpuWrites { get; } = [];
        /// <summary>SYS 那一路（UFA3）真正写进 EC 的值序列。</summary>
        public List<int> SysWrites { get; } = [];
        /// <summary>每次调用的原始入参（null = 该路保持 EC 当前值，语义同 EcHal）。</summary>
        public List<(int? Cpu, int? Gpu, int? Sys)> Calls { get; } = [];

        public double? Cpu { get; set; }
        public double? Gpu { get; set; }
        /// <summary>第三路温度；默认 null = 两风扇机型，引擎不该控制 SYS。</summary>
        public double? Sys { get; set; }
        public Exception? ThrowOnWrite { get; set; }

        private int? _cpu;
        private int? _gpu;

        public void SetFans(int? cpu, int? gpu, int? sys = null)
        {
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            Calls.Add((cpu, gpu, sys));
            _cpu = cpu ?? _cpu;
            _gpu = gpu ?? _gpu;
            if (cpu is not null) Writes.Add(cpu.Value);
            if (gpu is not null) GpuWrites.Add(gpu.Value);
            if (sys is not null) SysWrites.Add(sys.Value);
            DutyApplied = _cpu;
        }

        public (double? Cpu, double? Gpu) Temps(TimeSpan maxAge) => (Cpu, Gpu);
        public double? SysTemp(TimeSpan maxAge) => Sys;

        /// <summary>交回固件（0x0206=0 + 三路 UFA=0xFF）被调用了几次。</summary>
        public int Releases { get; private set; }

        public void ReleaseToEc()
        {
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            Releases++;
            _cpu = null;
            _gpu = null;
            DutyApplied = null;      // 交回后 EC 里不再有"我们钉的值"
        }
    }

    /// <summary>建一个引擎 + 一条固定曲线。时间由 `now` 变量控制。</summary>
    private static (FanCurveEngine Engine, FakeHw Hw, double[] Now) Make(
        double? cpu = 60, double? gpu = 55, Action<JsonObject>? tweak = null,
        Action<double>? sleep = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-fan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var model = new ModelStore(Path.Combine(dir, "model.json"));

        if (model.Presets["fan"] is not JsonObject fan)
            model.Presets["fan"] = fan = new JsonObject();

        fan["curve"] = new JsonArray(
            new JsonObject { ["t"] = 50, ["d"] = 40 },
            new JsonObject { ["t"] = 60, ["d"] = 55 },
            new JsonObject { ["t"] = 70, ["d"] = 75 },
            new JsonObject { ["t"] = 82, ["d"] = 95 },
            new JsonObject { ["t"] = 90, ["d"] = 100 });
        fan["min_duty"] = 30;
        fan["max_duty"] = 100;
        fan["hysteresis"] = 2.0;
        fan["min_interval"] = 6.0;
        fan["temp_wall"] = 95;
        fan["source"] = "cpu";
        // 软件曲线总开关（2026-11-18 起默认 false = 风扇交给 EC 固件）。
        // 这些回归用例测的是"曲线在跑时的状态机"，所以统一打开；要测"关着"的
        // 行为用 tweak 显式关掉（见下面的「软件曲线总开关」一节）。
        fan["curve_enabled"] = true;
        tweak?.Invoke(fan);
        var hw = new FakeHw { Cpu = cpu, Gpu = gpu };
        var now = new double[] { 1000.0 };          // 用一个大数，避免 lastWrite=0 的边界巧合
        var engine = new FanCurveEngine(hw, model, () => now[0], sleep ?? (_ => { }));
        return (engine, hw, now);
    }

    // ================================================================ 基本写入

    [Fact]
    public void 首轮按曲线写入目标转速()
    {
        var (e, hw, _) = Make(cpu: 60);
        Assert.True(e.Step());
        Assert.Equal([55], hw.Writes);              // 曲线在 60°C 上正好是 55
        Assert.Equal(55, e.LastDuty);
        Assert.Equal(60, e.LastTemp);
    }

    [Fact]
    public void 温度在曲线两端之外时取端点值()
    {
        var (low, hwLow, _) = Make(cpu: 45);        // 低于首点 50 → 40
        low.Step();
        Assert.Equal([40], hwLow.Writes);

        var (high, hwHigh, _) = Make(cpu: 99);      // 高于末点 90 → 100
        high.Step();
        Assert.Equal([100], hwHigh.Writes);
    }

    // ================================================================ 坏读数

    [Theory]
    [InlineData(0)]      // EC 回全 0 buffer 的经典形态
    [InlineData(0.5)]    // 开区间下界之外
    [InlineData(115)]    // 上界之外
    public void 温度不合理时一个字节都不写(double bad)
    {
        var (e, hw, _) = Make(cpu: bad, gpu: null);
        Assert.True(e.Step());
        Assert.Empty(hw.Writes);
        // 坏读数也**不进 LastTemp** —— 否则 Stop 会拿它算交回转速
        Assert.Null(e.LastTemp);
    }

    [Fact]
    public void 读不到温度时不写()
    {
        var (e, hw, _) = Make(cpu: null, gpu: null);
        Assert.True(e.Step());
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void 只有一路读坏时那一路保持不写_另一路照常()
    {
        // 回归（2026-10-31）：旧代码把坏读数那一路算成 `LastDuty ?? MinDuty`，
        // 首轮 LastDuty 还是 null ⇒ 直接把这一路钉在 min_duty(30%)。
        // 这正是「读数坏 → 风扇被压到最低」的事故形态，只是伪装成了"另一边正常"。
        var (e, hw, _) = Make(cpu: 0, gpu: 60);     // CPU 回 0（经典坏帧），GPU 正常
        Assert.True(e.Step());

        Assert.Empty(hw.Writes);                    // CPU 风扇一个字节都不写
        Assert.Equal([55], hw.GpuWrites);           // GPU 照常按自己的曲线走
        Assert.Null(e.LastDuty);                    // 没写过 ⇒ 没有"钉住过"的转速
        Assert.Null(e.LastTempCpu);
        Assert.Equal(60, e.LastTempGpu);

        // 反过来：CPU 正常、GPU 坏，CPU 一路照常写
        var (e2, hw2, _) = Make(cpu: 60, gpu: 0);
        Assert.True(e2.Step());
        Assert.Equal([55], hw2.Writes);
        Assert.Empty(hw2.GpuWrites);
    }

    // ================================================================ 空曲线（2026-11-23 审查）

    [Fact]
    public void CPU曲线为空时这一路不写_不把风扇钉在下限()
    {
        // 空曲线在 Interp 里的语义是"返回 min_duty" ⇒ 接管后 50~95 ℃ 一直 30%（约 1500 RPM），
        // 只有撞墙才 100% —— 与 2026-10-13 那次事故同形。SYS 那一路早有 `Count > 0` 守卫，
        // CPU/GPU 两路漏了（`curve_cpu: []` 不会被迁移补上，迁移只补 null）。
        var (e, hw, _) = Make(cpu: 70, tweak: fan => fan["curve_cpu"] = new JsonArray());

        Assert.True(e.Step());
        Assert.Empty(hw.Writes);        // CPU 一路一个字节都不写
        Assert.Null(e.LastDuty);
        Assert.Equal(70, e.LastTemp);   // 温度本身照记，只是没有可写的目标
    }

    [Fact]
    public void 两条曲线都空时拒绝启动并说明原因()
    {
        var (e, hw, _) = Make(cpu: 70, tweak: fan =>
        {
            fan["curve_cpu"] = new JsonArray();
            fan["curve_gpu"] = new JsonArray();
        });

        var r = e.Start();

        Assert.False((bool)r["ok"]!);
        Assert.Contains("曲线是空的", (string)r["error"]!);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void CPU与GPU各听自己的曲线_不再有source这个概念()
    {
        // 原厂语义（ControlCenterDaemon.MonitorAndControlFan 实证）：
        // CPU 温度 → CPU 曲线 → UFA1；GPU 温度 → GPU 曲线 → UFA2。
        // 这里两条曲线故意不同：CPU 60°C → 55，GPU 85°C → 45（GPU 曲线末点）。
        var (e, hw, _) = Make(cpu: 60, gpu: 85, f =>
        {
            f["source"] = "gpu";                     // 旧字段：必须被无视
            f["curve_cpu"] = new JsonArray(
                new JsonObject { ["t"] = 50, ["d"] = 40 },
                new JsonObject { ["t"] = 60, ["d"] = 55 },
                new JsonObject { ["t"] = 90, ["d"] = 100 });
            f["curve_gpu"] = new JsonArray(
                new JsonObject { ["t"] = 50, ["d"] = 45 },
                new JsonObject { ["t"] = 90, ["d"] = 45 });
        });

        Assert.True(e.Step());
        Assert.Equal([55], hw.Writes);               // CPU 风扇
        Assert.Equal([45], hw.GpuWrites);            // GPU 风扇 —— 与 CPU 完全无关
        Assert.Equal(60, e.LastTempCpu);
        Assert.Equal(85, e.LastTempGpu);
    }

    [Fact]
    public void 单条旧curve迁移后两路都跟着它()
    {
        // 老配置只有 curve：迁移必须同时喂两路，升级不能悄悄改变行为
        var (e, hw, _) = Make(cpu: 60, gpu: 60);
        Assert.Equal(e.CurveCpu.Count, e.CurveGpu.Count);
        Assert.True(e.Step());
        Assert.Equal([55], hw.Writes);
        Assert.Equal([55], hw.GpuWrites);
    }

    [Fact]
    public void 只有一边温度变化时只写那一路()
    {
        var (e, hw, now) = Make(cpu: 60, gpu: 60);
        e.Step();
        Assert.Equal([55], hw.Writes);
        Assert.Equal([55], hw.GpuWrites);

        now[0] += 100;
        hw.Cpu = 70;                                 // 目标 75，差 20；GPU 不动
        e.Step();
        Assert.Equal([55, 75], hw.Writes);
        Assert.Equal([55], hw.GpuWrites);            // GPU 一路一个字节都没多写
        Assert.Equal((75, null, null), hw.Calls[^1]); // 没变的那路传 null = 保持 EC 当前值
    }

    // ================================================================ 节流与迟滞

    [Fact]
    public void 差值不到迟滞就不重写()
    {
        var (e, hw, now) = Make(cpu: 60);
        e.Step();
        Assert.Single(hw.Writes);

        // 60 → 60.5°C：55 + 20*(0.5/10) = 56，差值 1 < 迟滞 2 → 不写
        now[0] += 100;                              // 时间早就够了
        hw.Cpu = 60.5;
        e.Step();
        Assert.Single(hw.Writes);
    }

    [Fact]
    public void 达到迟滞就重写()
    {
        var (e, hw, now) = Make(cpu: 60);
        e.Step();

        now[0] += 100;
        hw.Cpu = 62;                                // 55 → 59，差 4 ≥ 2
        e.Step();
        Assert.Equal([55, 59], hw.Writes);
    }

    [Fact]
    public void 不到最小间隔就不写_哪怕温度变化很大()
    {
        var (e, hw, now) = Make(cpu: 60);
        e.Step();
        Assert.Single(hw.Writes);

        now[0] += 5;                                // 间隔 6 秒，只过了 5 秒
        hw.Cpu = 80;                                // 目标 92，差 37
        e.Step();
        Assert.Single(hw.Writes);

        now[0] += 1.5;                              // 累计 6.5 秒
        e.Step();
        Assert.Equal([55, 92], hw.Writes);
    }

    /// <summary>
    /// 拖动曲线的保存分两种：拖动途中每 500 ms 一次自动保存（不带 `apply_now`，仍受
    /// `min_interval` 节流，免得风扇跟着手指跳），松手那次带 `apply_now`（打断节流，
    /// ≤1 秒就用上）。2026-10-29 之前两条路径都不清节流 ⇒ 改完曲线要等最多 6 秒才见效。
    /// </summary>
    [Fact]
    public void 松手保存带_apply_now_立刻写_拖动途中仍受节流()
    {
        var (e, hw, now) = Make(cpu: 60);
        e.Step();
        Assert.Equal([55], hw.Writes);

        now[0] += 1;                                // 只过 1 秒，远不到 min_interval（6 s）
        hw.Cpu = 80;                                // 原曲线上 80°C → 92

        // ① 拖动途中：把同样一条曲线存回去，不带 apply_now ⇒ 一个字节都不写
        e.Update(curve: SameCurve());
        e.Step();
        Assert.Equal([55], hw.Writes);

        // ② 松手那次：带 apply_now ⇒ 下一轮立刻写
        e.Update(curve: SameCurve(), applyNow: true);
        e.Step();
        Assert.Equal([55, 92], hw.Writes);
    }

    [Fact]
    public void 不带曲线正文时_apply_now_不清节流()
    {
        var (e, hw, now) = Make(cpu: 60);
        e.Step();

        now[0] += 1;
        hw.Cpu = 80;
        e.Update(tempWall: 90, applyNow: true);      // 只改温度墙，没动曲线正文
        e.Step();
        Assert.Equal([55], hw.Writes);               // 仍受节流，不提前写
    }

    /// <summary>与 `Make` 里初始化的那条曲线完全一致（"原样存回"是拖动保存的常态）。</summary>
    private static List<CurvePoint> SameCurve() =>
    [
        new CurvePoint(50, 40), new CurvePoint(60, 55), new CurvePoint(70, 75),
        new CurvePoint(82, 95), new CurvePoint(90, 100),
    ];

    // ================================================================ 温度墙
    [Fact]
    public void 达到温度墙强制最高转速并置保护位()
    {
        var (e, hw, _) = Make(cpu: 96);             // 墙 95
        e.Step();
        Assert.Equal([100], hw.Writes);             // 不是曲线值，是 max_duty
        Assert.True(e.Protecting);
        Assert.Equal(96, e.ProtectTemp);
    }

    [Fact]
    public void 保护用的是cpu与gpu的最高值_两路一起顶到max()
    {
        // CPU 只有 60，但 GPU 摸到 96 —— 保险丝必须管，而且两路都要拉满
        var (e, hw, _) = Make(cpu: 60, gpu: 96);
        e.Step();
        Assert.Equal([100], hw.Writes);
        Assert.Equal([100], hw.GpuWrites);
        Assert.True(e.Protecting);
    }

    [Fact]
    public void 保护退出带迟滞_防止墙边抖动()
    {
        var (e, hw, now) = Make(cpu: 96);
        e.Step();
        Assert.True(e.Protecting);

        // 94°C：墙 95 - 迟滞 2 = 93，94 ≥ 93 ⇒ 仍在保护（否则会 100%↔90% 反复横跳）
        now[0] += 100;
        hw.Cpu = 94;
        e.Step();
        Assert.True(e.Protecting);

        // 92°C < 93 ⇒ 退出保护，回到曲线值
        now[0] += 100;
        hw.Cpu = 92;
        e.Step();
        Assert.False(e.Protecting);
        Assert.Equal(100, e.LastDuty);              // 92°C 曲线插值 ≈ 100
        // 退出保护后按正常曲线算，这里曲线末端就是 100，所以不再重复写
    }

    // ================================================================ 停止与交回

    [Fact]
    public void 停止时交回固件而不是写一个曲线值()
    {
        // 2026-11-18 改：交回 = `0x0206=0` + 三路 UFA=`0xFF`（EcHal.ReleaseToEc）。
        // 旧实现写"当前温度对应的曲线转速"，那一笔同样是钉住 —— EC 拿到 UFA 就不接管，
        // 退出程序后风扇停在那个 % 上直到重启。
        var (e, hw, _) = Make(cpu: 84);
        e.Step();
        Assert.NotNull(e.LastDuty);
        var writesBefore = hw.Writes.Count;

        var r = e.Stop(release: true);
        Assert.True(r["released"]!.GetValue<bool>());
        Assert.Equal(1, hw.Releases);                  // 走了交回，而不是再写一脚 UFA
        Assert.Equal(writesBefore, hw.Writes.Count);   // 一个字节的 UFA 都没多写
        Assert.False(r["stuck"]!.GetValue<bool>());
    }

    [Fact]
    public void 温度未知时停止不写任何值()
    {
        var (e, hw, _) = Make(cpu: null, gpu: null);
        e.Step();                                   // 没读到温度 ⇒ 一个字节没写
        var r = e.Stop(release: true);
        Assert.Empty(hw.Writes);
        Assert.Equal(0, hw.Releases);               // 从没钉过，就不需要交回
        // released = null 表示"没有需要交回的东西"
        Assert.Null(r["released"]);
    }

    [Fact]
    public void 交回失败且之前写过手动值才算stuck()
    {
        var (e, hw, _) = Make(cpu: 84);
        e.Step();                                   // 写成功 → 有手动值
        hw.ThrowOnWrite = new Exception("boom");

        var r = e.Stop(release: true);
        Assert.False(r["released"]!.GetValue<bool>());
        Assert.True(r["stuck"]!.GetValue<bool>());
        Assert.Contains("交回固件失败", r["last_error"]!.GetValue<string>());
    }

    [Fact]
    public void 没写过手动值时连交回都不发()
    {
        // 写一直失败 ⇒ DutyApplied 恒为 null（= 从没钉住过转速）。
        // 这时不该发交回命令：EC 本来就在自己调速，多写两条只是噪音。
        var (e, hw, _) = Make(cpu: 84);
        hw.ThrowOnWrite = new Exception("boom");
        e.Step();
        Assert.Null(e.LastDuty);

        var r = e.Stop(release: true);
        Assert.Null(r["released"]);                      // 没交回
        Assert.Equal(0, hw.Releases);
        Assert.False(r["stuck"]!.GetValue<bool>());
        Assert.Null(r["last_error"]);
    }

    // ================================================================ 自停

    [Fact]
    public void 写入被拒立即自停并记录原因()
    {
        var (e, hw, _) = Make(cpu: 60);
        hw.ThrowOnWrite = new FanWriteDeniedException("0x0205 被拒");

        Assert.False(e.Step());                     // false = 循环该退出
        Assert.False(e.Running);
        Assert.Contains("0x0205", e.LastError);
    }

    [Fact]
    public void 写入开关关掉时下一轮立即自停()
    {
        // 不依赖"上一轮还开着"：引擎每轮现读 WritesEnabled，不缓存。
        var (e, hw, _) = Make(cpu: 60);
        hw.WritesEnabled = false;

        Assert.False(e.Step());                     // false = 循环该退出
        Assert.Contains("EC 写入开关已关闭", e.LastError);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void 连续异常达到阈值才自停()
    {
        var (e, hw, now) = Make(cpu: 60);
        hw.ThrowOnWrite = new Exception("WMI 挂了");

        // 前 29 轮都只是计数，不能停
        for (var i = 0; i < FanCurveEngine.FailHaltRounds - 1; i++)
        {
            Assert.True(e.Step());
            now[0] += 10;
        }
        Assert.Null(e.LastError);

        Assert.False(e.Step());                     // 第 30 轮
        Assert.Contains("连续", e.LastError);
    }

    [Fact]
    public void 飓风接管时自己写满转且只写一次()
    {
        // 2026-10-20 真机改的语义：以前是"让位、一个字节都不写"，实测固件的 max
        // 经常被我们写过的 UFA 压掉（按了键风扇不动）。现在改成自己写 max_duty，
        // 转速与固件飓风峰值一致（5002 RPM），行为确定。
        var (e, hw, _) = Make(cpu: 60);
        e.YieldHw = true;

        Assert.True(e.Step());
        Assert.Single(hw.Writes);
        Assert.Equal(e.MaxDuty, hw.Writes[0]);      // 钉到配置里的最高档（默认 100）
        Assert.Null(e.LastTemp);                    // 飓风不走曲线，也不取温度

        // 幂等：再来一轮不该重复写 EC
        Assert.True(e.Step());
        Assert.Single(hw.Writes);
    }

    [Fact]
    public void 退出飓风后曲线按温度接着写()
    {
        var (e, hw, now) = Make(cpu: 60);
        e.Running = true;

        e.ApplyHwMax(true);
        Assert.Equal(e.MaxDuty, hw.Writes[^1]);

        e.ApplyHwMax(false);
        now[0] += 10;                       // 过掉 min_interval（6 s），否则本轮本来就不写
        Assert.True(e.Step());
        // 60°C 落在曲线 50→40 / 60→55 的第二点
        Assert.Equal(55, hw.Writes[^1]);
    }

    [Fact]
    public void 退出飓风恢复EC里真正在转的转速_不是曲线自己的缓存()
    {
        var (e, hw, now) = Make(cpu: 70);     // 70°C 落在曲线第三点 → 75
        e.Running = true;
        now[0] += 10;
        Assert.True(e.Step());
        Assert.Equal(75, hw.DutyApplied);

        // 模拟「曲线以为写进去了、EC 其实还停在 55」：写传输失败被吞掉时
        // LastDuty 会先被更新成 75，而 DutyApplied 才是 EC 真正在转的值。
        // 此时退出飓风若恢复成 75，风扇就被钉在一个从未生效过的占空比上，
        // 而且曲线没跑时再也没人把它纠正回来。
        hw.DutyApplied = 55;
        e.Running = false;

        e.ApplyHwMax(true);
        e.ApplyHwMax(false);

        Assert.Equal(55, hw.Writes[^1]);
    }

    [Fact]
    public void 曲线没跑时FN1满速照写_切回auto按曲线降速()
    {
        // 曲线没在跑（关掉自定义风扇后 Stop 把 LastDuty 清成 null、EC 里也没写过值）
        // ⇒ DutyApplied 也是 null，FN+1 切回 auto 必须按温度兜底写回，不能留在满速。
        var (e, hw, _) = Make(cpu: 60);
        e.Running = false;
        e.YieldHw = false;
        hw.DutyApplied = null;

        // FN+1 → 满速：固件 FMAX 单独压不过已写的 UFA，必须我们写 100
        e.ApplyHwMax(true);
        Assert.True(e.YieldHw);
        Assert.Equal([100], hw.Writes);

        // FN+1 → 自动：DutyApplied/LastDuty 都是 null，必须按当前温度从曲线兜底写回
        // （60°C 落在曲线 50→40 / 60→55 的第二点），绝不能什么都不写留在满速。
        hw.Writes.Clear();
        e.ApplyHwMax(false);
        Assert.False(e.YieldHw);
        Assert.Equal([55], hw.Writes);
    }

    [Fact]
    public void 曲线没跑时EC留有旧转速_切回auto优先恢复那个值()
    {
        // DutyApplied 有值时优先恢复 EC 真正在转的值，不用曲线兜底
        var (e, hw, _) = Make(cpu: 60);
        e.Running = false;
        hw.DutyApplied = 90;

        e.ApplyHwMax(true);
        Assert.Equal(100, hw.Writes[^1]);

        hw.Writes.Clear();
        e.ApplyHwMax(false);
        Assert.Equal([90], hw.Writes);   // 不是曲线的 55 —— EC 真值优先
    }

    [Fact]
    public void 临时满转时FN1进出都不碰EC_否则按钮会丢掉恢复自动()
    {
        // 未开自定义风扇时点了「最大转速」：EC 里钉着 100，但那笔**不归引擎**（EcHal.OemFanMax）。
        // 引擎若在这里 SetFans(MaxDuty)，EcHal.SetFans 会把临时满转标志清掉 ⇒
        // 按钮从「恢复自动」退回「最大转速」，风扇还在 100%，一键交回的入口就没了
        // （2026-11-25 审查发现的真实状态机漏洞）。
        var (e, hw, _) = Make(cpu: 60, tweak: fan => fan["curve_enabled"] = false);
        e.Running = false;
        e.YieldHw = false;
        hw.DutyApplied = 100;
        hw.OemFanMax = true;

        e.ApplyHwMax(true);
        e.ApplyHwMax(false);

        Assert.Empty(hw.Writes);     // 一个字节都不写：满转已经是这个状态的效果
        Assert.Equal(0, hw.Releases); // 也不交回：交回等于把用户的临时满转撤了
        Assert.False(e.YieldHw);
    }

    [Fact]
    public void 采样线程的null镜像不翻YieldHw也不写EC()
    {
        // 采样线程每帧调 ApplyHwMax(FanHwMax)。没收到过 FN+1 事件时是 null：
        // 绝不能当成 false 把 YieldHw 清掉，否则 max 期间被清 ⇒ 转速掉一半。
        var (e, hw, _) = Make(cpu: 60);
        e.Running = true;

        e.ApplyHwMax(true);               // 事件：进 max，写 100
        Assert.True(e.YieldHw);
        Assert.Equal(100, hw.Writes[^1]);

        hw.Writes.Clear();
        e.ApplyHwMax(null);               // 采样线程读到「未知」
        Assert.True(e.YieldHw);           // 标志不能被清
        Assert.Empty(hw.Writes);          // 一个字节都不写
        e.ApplyHwMax(null);
        Assert.True(e.YieldHw);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void 重复同一个状态幂等_不重复写EC()
    {
        // 采样线程每帧重放同一个 true/false，只在第一次翻转时写
        var (e, hw, _) = Make(cpu: 60);
        e.Running = false;
        hw.DutyApplied = 70;

        e.ApplyHwMax(true);               // false → true：写 100
        Assert.Single(hw.Writes);
        e.ApplyHwMax(true);               // 重放：不写
        e.ApplyHwMax(true);
        Assert.Single(hw.Writes);

        e.ApplyHwMax(false);              // true → false：写回 70
        Assert.Equal(2, hw.Writes.Count);
        e.ApplyHwMax(false);              // 重放：不写
        Assert.Equal(2, hw.Writes.Count);
    }

    // ================================================================ 启动前置条件

    [Fact]
    public void 非管理员拒绝启动()
    {
        var (e, hw, _) = Make(cpu: 60);
        hw.AdminOk = false;
        var r = e.Start();
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("管理员", r["error"]!.GetValue<string>());
        Assert.False(e.Running);
    }

    [Fact]
    public void EC控制关闭时拒绝启动()
    {
        var (e, hw, _) = Make(cpu: 60);
        hw.WritesAllowed = false;
        var r = e.Start();
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("EC 控制已关闭", r["error"]!.GetValue<string>());
    }

    [Fact]
    public void 手动模式不写EC但保持当前转速()
    {
        var (e, hw, _) = Make(cpu: 60);
        e.Step();
        var before = hw.Writes.Count;

        var r = e.SetMode("manual");
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal("manual", r["mode"]!.GetValue<string>());
        Assert.Equal(before, hw.Writes.Count);      // release=false ⇒ 不写
    }

    [Fact]
    public void 未知模式被拒绝()
    {
        var (e, _, _) = Make();
        var r = e.SetMode("turbo");
        Assert.False(r["ok"]!.GetValue<bool>());
    }

    // ================================================================ 软件曲线总开关（2026-11-18）

    [Fact]
    public void 没有总开关键时默认关闭_启动被拒且一个字节都不写()
    {
        // 出厂默认 = 风扇由 EC 固件按温度调速。缺 `curve_enabled` 就等于关 ——
        // 「默认交给固件」只能靠"从开机起就没写过 UFA"实现，所以这里必须一个字都不写。
        var (e, hw, _) = Make(cpu: 60, tweak: fan => fan.Remove("curve_enabled"));
        Assert.False(e.CurveEnabled);
        Assert.False(e.State()["enabled"]!.GetValue<bool>());

        var r = e.Start();
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("自定义风扇", r["error"]!.GetValue<string>());
        Assert.False(e.Running);
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void 曲线关着时自动与手动都被拒绝()
    {
        // 用户明确要求：手动风扇控制必须先开软件曲线。
        var (e, hw, _) = Make(cpu: 60, tweak: fan => fan["curve_enabled"] = false);

        foreach (var mode in new[] { "manual", "auto" })
        {
            var r = e.SetMode(mode);
            Assert.False(r["ok"]!.GetValue<bool>());
            Assert.True(r["need_curve"]!.GetValue<bool>());
            Assert.Contains("自定义风扇", r["error"]!.GetValue<string>());
        }
        Assert.Empty(hw.Writes);
    }

    [Fact]
    public void 打开总开关后落盘并接管()
    {
        var (e, _, _) =
            Make(cpu: 60, tweak: fan => fan["curve_enabled"] = false, sleep: _ => Thread.Sleep(1));

        var r = e.SetEnabled(true);
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.True(e.CurveEnabled);          // 落盘了（自愈线程认这个键）
        Assert.Equal("auto", e.FanMode);      // 不会停在手动上
        Assert.True(e.Running);

        e.Stop(release: false);               // 收尾，别把循环线程留到测试结束之后
    }

    [Fact]
    public void 强制接管时enabled镜像不跟着写死成true()
    {
        // 卡死自救的 force 接管跳过总开关闸门，但 curve_enabled 仍是 false（用户没开
        // 「自定义风扇」）。enabled 只是镜像，写死 true 会让旧脚本误判曲线已开（2026-11-19 修）。
        var dir = Path.Combine(Path.GetTempPath(), "cc-fan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var model = new ModelStore(Path.Combine(dir, "model.json"));
        model.Presets["fan"] = new JsonObject
        {
            ["curve"] = new JsonArray(
                new JsonObject { ["t"] = 50, ["d"] = 40 },
                new JsonObject { ["t"] = 90, ["d"] = 100 }),
            ["curve_enabled"] = false,
            ["enabled"] = false,
            ["min_duty"] = 30,
            ["max_duty"] = 100,
            ["hysteresis"] = 2.0,
            ["min_interval"] = 6.0,
            ["temp_wall"] = 95,
            ["source"] = "cpu",
        };
        var hw = new FakeHw { Cpu = 60, Gpu = 55 };
        var e = new FanCurveEngine(hw, model, () => 1000.0, _ => { });

        var r = e.Start(force: true);
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.False(e.CurveEnabled);
        // 关键断言：落盘的镜像字段必须跟真源一致，不能被 force 写成 true。
        Assert.False(model.Presets["fan"]!["enabled"]!.GetValue<bool>());

        e.Stop(release: false);                       // 收尾，不留后台线程
    }

    [Fact]
    public void 关掉总开关真的把风扇交回固件()
    {
        // 用户 2026-11-18：「关掉就切回 EC 控制」。旧实现只停表不写 EC，
        // 结果风扇一直按最后那个 UFA 转 —— 现在关掉 = `0x0206=0` + 三路 UFA=`0xFF`。
        var (e, hw, _) = Make(cpu: 60);
        Assert.True(e.Step());                        // 先跑一轮，让 EC 里确实有我们的值
        Assert.Equal([55], hw.Writes);

        var r = e.SetEnabled(false);

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.False(e.CurveEnabled);
        Assert.False(e.Running);
        Assert.True(r["released"]!.GetValue<bool>()); // 真交了
        Assert.Equal(1, hw.Releases);
        Assert.Equal([55], hw.Writes);                // 交回不走 0x0205 的百分比写
        Assert.Null(r["keep_duty"]);                  // EC 里不再有我们钉的值
        Assert.Contains("已交回 EC 固件", r["note"]!.GetValue<string>());
    }

    [Fact]
    public void 手动模式下退出飓风不拿曲线值顶掉用户转速()
    {
        // 用户报「手动风扇控制会乱跳」的成因之一：FN+1 进出一次后，退出飓风按
        // 曲线值写回（60°C 的曲线是 55%），用户钉的 60% 就没了。
        // 手动模式只认进 max 前 EC 里那个值。
        var (e, hw, _) = Make(cpu: 60, tweak: fan => fan["mode"] = "manual");
        hw.DutyApplied = 60;                          // 用户钉的转速

        e.ApplyHwMax(true);
        Assert.Equal([100], hw.Writes);

        e.ApplyHwMax(false);
        Assert.Equal([100, 60], hw.Writes);           // 回到 60，不是 55
    }

    [Fact]
    public void 手动模式下不知道用户钉了多少就一个字节都不写()
    {
        // 进程刚起来时 EcHal 的 _duty 是空的：此时退出飓风宁可保持 EC 原值，
        // 也不能按曲线猜一个数写下去（那就是"手动自己变了"）。
        var (e, hw, _) = Make(cpu: 60, tweak: fan => fan["mode"] = "manual");
        hw.DutyApplied = null;

        e.ApplyHwMax(true);
        Assert.Equal([100], hw.Writes);

        e.ApplyHwMax(false);
        Assert.Equal([100], hw.Writes);               // 不写
    }

    // ================================================================ 2026-11-12 审查修复

    [Fact]
    public void 温度乱码不应触发温度保护()
    {
        // EC 的 cpu_temp 是 4 字节 U32，坏帧常见 `0xFFFFFFFF`。旧实现把**原始**读数
        // 喂给 ProtectTempOf ⇒ ptemp 恒 ≥ 墙 ⇒ 两路永久顶到 max_duty，而且永远退不出来
        // （释放条件要求 ptemp 落回墙以下），界面横幅显示"测得 4294967295 °C"。
        var (e, hw, _) = Make(cpu: 4_294_967_295, gpu: 60);
        Assert.True(e.Step());

        Assert.False(e.Protecting);
        Assert.Null(e.ProtectTemp);
        Assert.Empty(hw.Writes);                    // CPU 那路读数坏 ⇒ 不写
        Assert.Equal([55], hw.GpuWrites);           // GPU 那路照常按自己的曲线走
    }

    [Fact]
    public void 时钟被回拨后仍然继续调速()
    {
        // NTP 校时 / 用户改时间会把 `_now() - _lastWrite` 变成负数。旧写法
        // `>= MinInterval` 于是恒为 false ⇒ 曲线显示"运行中"但一整段时间不再写 EC。
        var (e, hw, now) = Make(cpu: 60);
        Assert.True(e.Step());
        Assert.Equal([55], hw.Writes);

        now[0] -= 3600;                             // 时钟往回调一小时
        hw.Cpu = 70;                                // 目标变成 75
        Assert.True(e.Step());
        Assert.Equal([55, 75], hw.Writes);          // 该写就得写，不能等回拨区间过去
    }

    [Fact]
    public void 进入温度保护那一拍不等最小写入间隔()
    {
        // 保护是跳变，不是缓调：60 → 99 °C 在 1 秒内发生时，EC 必须立刻拉到 max，
        // 而不是等满 min_interval（默认 6 秒）—— 那 6 秒正是最热的时候。
        var (e, hw, now) = Make(cpu: 60, gpu: null);
        Assert.True(e.Step());
        Assert.Equal([55], hw.Writes);

        now[0] += 1;                                // 远小于 min_interval(6s)
        hw.Cpu = 99;
        Assert.True(e.Step());

        Assert.True(e.Protecting);
        Assert.Equal([55, 100], hw.Writes);         // 立刻拉满
        Assert.Equal([100], hw.GpuWrites);
    }

    [Theory]
    [InlineData(20.0, 0.0, 10.0, 2.0)]              // 迟滞超上限 + 间隔低于下限
    [InlineData(0.0, 0.1, 0.5, 2.0)]                // 迟滞低于下限（0.5 °C 不能被抹成 0）
    public void 迟滞与写入间隔按边界表钳制(double hy, double gap,
                                          double wantHy, double wantGap)
    {
        // limits.json 里 `hysteresis` 0.5~10、`min_interval` 2~60。以前这两个键**裸读配置**：
        // hysteresis=20 ⇒ 首轮之后再不改速、温度墙也退不出来；
        // min_interval=0 ⇒ 每轮刷一次 EC，和采样线程抢队列。
        var (e, _, _) = Make(tweak: f =>
        {
            f["hysteresis"] = hy;
            f["min_interval"] = gap;
        });

        Assert.Equal(wantHy, e.Hysteresis, 3);
        Assert.Equal(wantGap, e.MinInterval, 3);
    }

    [Fact]
    public void 手动模式下停止也走交回固件而不是按旧温度补写()
    {
        // 用户把转速钉到 90%（presets.fan.mode=manual），曲线的 LastTemp 还停在上次
        // 自动时的 84 °C。旧实现会写 curve(84)=95 —— 直接把用户钉的值盖掉。
        // 2026-11-18 起：手动模式也**交回固件**（0x0206=0 + UFA=0xFF），
        // 因为我们既然要走了，风扇就该由 EC 按温度自己调，而不是留一个钉死的值。
        var (e, hw, _) = Make(cpu: 84, tweak: f => f["mode"] = "manual");
        e.Step();                                   // 先让 LastTemp 有值
        hw.DutyApplied = 90;                        // 用户钉的转速
        var writesBefore = hw.Writes.Count;

        var r = e.Stop(release: true);
        Assert.True(r["released"]!.GetValue<bool>());
        Assert.Equal(1, hw.Releases);
        Assert.Equal(writesBefore, hw.Writes.Count);  // 没有按旧温度补写 95
    }

    [Fact]
    public void 温度过期后停止仍然交回固件()
    {
        // EC 读停摆 / 曲线早就停了：LastTemp 是 10 分钟前的，不能拿它算"交回值"。
        // 但**交回固件不需要温度** —— 写的是 0x0206=0 + 0xFF，与温度无关。
        // 所以这条路径现在照样交回（旧实现因为怕用陈旧温度，干脆一个字节都不写）。
        var (e, hw, now) = Make(cpu: 84);
        e.Step();
        var writesBefore = hw.Writes.Count;

        now[0] += 600;                              // 十分钟后
        var r = e.Stop(release: true);
        Assert.True(r["released"]!.GetValue<bool>());
        Assert.Equal(1, hw.Releases);
        Assert.Equal(writesBefore, hw.Writes.Count);
    }

    [Fact]
    public void 过期代际的循环不再写EC()
    {
        // Stop 只在 while 条件上判代际，已经进入 Step 的旧线程会把这一轮跑完 ——
        // 于是切手动之后旧一轮仍会写一次 EC，把 LastDuty / Protecting 重新置上。
        // 现在 Step 开头与每次 SetFans 之前都验身份，过期代际直接返回 false。
        var (e, hw, _) = Make(cpu: 84);
        Assert.False(e.Step(gen: 999));             // 999 不是当前代际
        Assert.Empty(hw.Writes);

        // 不带代际（单测 / 手动调）照旧能跑，不能被这条守卫误伤
        Assert.True(e.Step());
        Assert.Equal([96], hw.Writes);              // 84 °C 的曲线值（82→95 与 90→100 之间）
    }

    [Fact]
    public void 关掉EC再打开后能重新接管风扇()
    {
        // 设置页关「EC 控制」走的是 Stop(release:false)，而它会把 LastError 清成 null。
        // 重新打开时必须还能 Start —— 否则 running:false / last_error:null / mode:auto，
        // 没有任何路径把曲线接回来（散热页停在"启动中…"，风扇定住）。
        var (e, _, _) = Make(cpu: 60, sleep: _ => Thread.Sleep(1));

        Assert.True(e.Start()["ok"]!.GetValue<bool>());
        Assert.True(e.Running);

        e.Stop(release: false);                     // = 关掉 EC 总闸
        Assert.False(e.Running);
        Assert.Null(e.LastError);                   // 停止本身不是故障

        Assert.True(e.Start()["ok"]!.GetValue<bool>());   // = 重新打开后立刻接回来
        Assert.True(e.Running);

        e.Stop(release: false);                     // 收尾，别把循环线程留到测试结束之后
    }
    // ================================================================ 第三路（SYS）独立曲线

    private static JsonArray SysCurve()
    {
        var arr = new JsonArray();
        foreach (var (t, d) in new (double, int)[] { (50, 45), (70, 75), (90, 100) })
            arr.Add(new JsonObject { ["t"] = t, ["d"] = d });
        return arr;
    }

    [Fact]
    public void 第三路按自己的曲线和自己的温度独立调速()
    {
        var (e, hw, _) = Make(tweak: fan => fan["curve_sys"] = SysCurve());
        hw.Sys = 70;                                 // SYS 温度与 CPU(60)/GPU(55) 都不同

        Assert.True(e.Step());

        Assert.Equal([75], hw.SysWrites);            // SYS 曲线@70°C = 75
        Assert.Equal(70, e.LastTempSys);
        Assert.Equal(75, e.LastDutySys);
        var call = Assert.Single(hw.Calls);
        Assert.Equal(75, call.Sys);                  // 三路同帧下发，各算各的
    }

    [Fact]
    public void 没有SYS曲线时第三路交给底层兜底()
    {
        var (e, hw, _) = Make();                     // 配置里没有 curve_sys
        hw.Sys = 70;

        Assert.True(e.Step());

        Assert.Empty(hw.SysWrites);
        var call = Assert.Single(hw.Calls);
        Assert.Null(call.Sys);                       // EcHal 决定：三风扇跟最热 / 两风扇写 255
        Assert.Null(e.LastTempSys);
        Assert.Null(e.LastDutySys);
    }

    [Fact]
    public void 显式空数组清掉SYS曲线且不影响CPU和GPU()
    {
        var (e, hw, _) = Make(tweak: fan => fan["curve_sys"] = SysCurve());
        Assert.NotEmpty(e.CurveSys);

        var r = e.Update(curveSys: Array.Empty<CurvePoint>());
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Empty(e.CurveSys);

        hw.Sys = 70;
        Assert.True(e.Step());
        Assert.Equal([55], hw.Writes);               // CPU 仍按 60°C = 55%
        Assert.Empty(hw.SysWrites);                  // SYS 已回到底层兜底
    }

}

/// <summary>
/// `update()` / `set_bind()` 的回归 —— 这段最容易在"重构"时被顺手改坏：
/// 三处顺序（绑定先于曲线、固定档先于曲线、只切固定档要换全局曲线）都有实际事故背景。
/// </summary>
public sealed class FanCurveConfigTests
{
    private static (FanCurveEngine Engine, JsonObject Fan) MakeCfg()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-fancfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var model = new ModelStore(Path.Combine(dir, "model.json"));

        if (model.Presets["fan"] is not JsonObject fan)
            model.Presets["fan"] = fan = new JsonObject();

        fan["curve"] = Curve((50, 40), (60, 55), (70, 75), (82, 95), (90, 100));
        fan["min_duty"] = 30;
        fan["max_duty"] = 100;
        fan["hysteresis"] = 2.0;
        fan["min_interval"] = 6.0;
        fan["temp_wall"] = 95;
        fan["bind_mode"] = false;
        // 档位档案现在统一在 `perf.modes[档].fan`（2026-11-17 归位）。
        // 这里给的是"一条曲线管两路"的老语义，所以 cpu/gpu 都写同一份。
        var modeCurves = new Dictionary<string, JsonArray>
        {
            ["office"] = Curve((50, 30), (90, 85)),
            ["game"] = Curve((50, 35), (90, 100)),
            ["beast"] = Curve((50, 41), (90, 99)),
        };
        foreach (var (key, curve) in modeCurves)
        {
            QControl.Core.Model.ModeBundle.SetFanCurve(model.Presets, key, QControl.Core.Model.ModeBundle.FanCpu, curve);
            QControl.Core.Model.ModeBundle.SetFanCurve(model.Presets, key, QControl.Core.Model.ModeBundle.FanGpu, curve);
        }

        var hw = new FakeHw();
        var engine = new FanCurveEngine(hw, model, () => 1000, _ => { });
        return (engine, fan);
    }

    private static JsonArray Curve(params (double T, int D)[] pts)
    {
        var arr = new JsonArray();
        foreach (var (t, d) in pts) arr.Add(new JsonObject { ["t"] = t, ["d"] = d });
        return arr;
    }

    private sealed class FakeHw : IFanHardware
    {
        public bool WritesEnabled => true;
        public bool WritesAllowed => true;
        public bool AdminOk => true;
        public string? AdminHint => null;
        public int? DutyApplied => null;
        public void SetFans(int? cpu, int? gpu, int? sys = null) { }
        public (double? Cpu, double? Gpu) Temps(TimeSpan maxAge) => (60, 55);
    }

    [Fact]
    public void 温度墙会被夹到_hysteresis_边界内()
    {
        var (e, _) = MakeCfg();
        e.Update(tempWall: 200);
        Assert.Equal(100, e.Wall);          // temp_wall 上界 100
        e.Update(tempWall: 10);
        Assert.Equal(60, e.Wall);           // 下界 60
    }

    [Fact]
    public void 曲线会被规范化_温度与转速都夹边界并按温度排序()
    {
        var (e, fan) = MakeCfg();
        e.Update(curve: [new CurvePoint(200, 500), new CurvePoint(10, -50)]);

        var pts = e.Curve;
        Assert.Equal(2, pts.Count);
        Assert.Equal(20, pts[0].T);         // 点温度夹 20~110
        Assert.Equal(30, pts[0].D);         // 转速夹 fan_duty 30~100
        Assert.Equal(110, pts[1].T);
        Assert.Equal(100, pts[1].D);
    }

    [Fact]
    public void 未知固定档被拒绝()
    {
        var (e, _) = MakeCfg();
        var r = e.Update(fixedMode: "turbo");
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("未知模式", r["error"]!.GetValue<string>());
    }

    [Fact]
    public void 指定档位改曲线时不动全局曲线()
    {
        var (e, fan) = MakeCfg();
        var before = e.Curve[0].D;          // 40

        var r = e.Update(curve: [new CurvePoint(50, 66)], mode: "game");
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(66, e.ModeCurves()["game"][0].D);
        Assert.Equal(before, e.Curve[0].D); // 全局没被改
    }

    [Fact]
    public void 编辑当前生效那一档时全局曲线跟着变()
    {
        var (e, fan) = MakeCfg();
        // 「固定曲线」模式下 ActiveMode = fixed_mode，所以必须先锁一档；
        // 只有 bind_mode / fixed_mode 都给不出档位时 ActiveMode 才是 null
        // （旧版 `_active_mode()` 同语义，别"优化"成回退到 _perfKey）。
        fan["fixed_mode"] = "beast";
        e.ObservePerfMode("beast");

        e.Update(curve: [new CurvePoint(50, 44)], mode: "beast");
        Assert.Equal(44, e.Curve[0].D);      // active == beast ⇒ 全局跟着写
    }

    [Fact]
    public void 没有生效档位时改某一档不动全局曲线()
    {
        var (e, _) = MakeCfg();              // bind_mode=false 且没 fixed_mode
        e.ObservePerfMode("beast");

        e.Update(curve: [new CurvePoint(50, 44)], mode: "beast");
        Assert.Equal(40, e.Curve[0].D);      // 全局保持原样（只写了 beast 槽）
        Assert.Equal(44, e.ModeCurves()["beast"][0].D);
    }

    [Fact]
    public void 开绑定的瞬间就对齐当前模式()
    {
        var (e, _) = MakeCfg();
        e.ObservePerfMode("beast");
        Assert.Equal(40, e.Curve[0].D);      // 还是全局曲线

        var r = e.SetBind(true);
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.True(r["bind_mode"]!.GetValue<bool>());
        Assert.Equal(41, e.Curve[0].D);      // 立刻换成 beast 档的曲线
    }

    [Fact]
    public void 关绑定会把固定档锁在当前生效的那一档()
    {
        var (e, fan) = MakeCfg();
        e.ObservePerfMode("game");
        e.SetBind(true);
        e.SetBind(false);

        Assert.Equal("game", fan["fixed_mode"]!.GetValue<string>());
        Assert.False(fan["bind_mode"]!.GetValue<bool>());
    }

    [Fact]
    public void 只切固定档不带曲线时立刻换全局并解除节流()
    {
        var (e, fan) = MakeCfg();
        var r = e.Update(fixedMode: "office");
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(30, e.Curve[0].D);      // office 档首点 30
    }

    [Fact]
    public void 取某档曲线给界面编辑()
    {
        var (e, _) = MakeCfg();
        var ok = e.EditCurve("game");
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal(2, (ok["curve"] as JsonArray)!.Count);

        var bad = e.EditCurve("turbo");
        Assert.False(bad["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void SYS档位曲线只写自己的档且未配的档返回空()
    {
        var (e, _) = MakeCfg();
        var r = e.Update(modeCurvesSys: new Dictionary<string, IReadOnlyList<CurvePoint>>
        {
            ["game"] = [new CurvePoint(50, 40), new CurvePoint(90, 80)],
        });
        Assert.True(r["ok"]!.GetValue<bool>());

        var game = e.EditCurve("game");
        Assert.Equal(2, (game["curve_sys"] as JsonArray)!.Count);
        var office = e.EditCurve("office");
        Assert.Empty((office["curve_sys"] as JsonArray)!);   // 没配 = 这一档不控制 SYS
    }
}
