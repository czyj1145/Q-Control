using QControl.Core.FanCurve;

namespace QControl.Tests;

/// <summary>
/// 风扇卡死自救的判据回归（纯逻辑，不碰硬件）。
///
/// 每条用例对应的都是真机形态：
///   · 「84 °C 下 1500 RPM 锁死数小时」= 高温不动；
///   · 「关掉自定义风扇后风扇停在 1500，温度都 85 度了」= 温度变了转速不跟；
///   · 「写 30/90/100 回显都对、转速一点不动」= 下发目标后不跟。
/// 反向用例同样重要：**待机 60 °C 转速恒定不是卡住**，误判会平白发一次交回命令。
/// </summary>
public sealed class FanStuckGuardTests
{
    /// <summary>按固定间隔喂 N 拍；返回最后一次判定 + 这段里有没有触发过自救。</summary>
    private static (FanGuardVerdict Last, bool Rescued, double? RescueAt)
        Run(FanStuckGuard g, double start, double step, int ticks,
            Func<int, int?> rpm, Func<int, double?> temp, int? duty, bool canAct = true)
    {
        var v = FanGuardVerdict.None;
        var rescued = false;
        double? at = null;
        for (var i = 0; i < ticks; i++)
        {
            v = g.Update(start + i * step, [rpm(i), rpm(i), null], temp(i), duty, canAct);
            if (v.RescueNow) { rescued = true; at ??= start + i * step; }
        }
        return (v, rescued, at);
    }

    private static FanStuckGuard Guard() => new()
    {
        HotTemp = 78,
        HotRpm = 2400,
        HotFrozenSeconds = 90,
        FrozenSeconds = 150,
        RpmTolerance = 25,
        TempSwing = 10,
        ResponseSeconds = 20,
        DutyStep = 10,
        ResponseTolerance = 60,
        CooldownSeconds = 180,
        MaxAttempts = 3,
        RecoverSeconds = 30,
        // 风险模型：低温不判、越热越急、升温再加急
        RiskTemp = 65,
        DangerTemp = 92,
        DangerFrozenSeconds = 20,
        MinFrozenSeconds = 15,
        RiseRateWarn = 2.0,
    };

    // ================================================================ ① 高温不动（时长按风险折算）

    [Fact]
    public void 八十四度转速不动六十秒就判卡住并给自救()
    {
        var g = Guard();
        // 84 °C 需要等 90 + (20-90)*((84-78)/(92-78)) = 60 秒，不是固定的 90 秒
        var (v, rescued, _) = Run(g, 1000, 5, 20, _ => 1500, _ => 84.0, duty: null);

        Assert.True(v.Stuck);
        Assert.Equal(FanStuckGuard.ReasonHot, v.Reason);
        Assert.Equal(1, v.Fan);
        Assert.True(rescued);
        Assert.Equal(1, g.Attempts);
        Assert.Equal(60, v.NeedSeconds, 0);
    }

    [Fact]
    public void 温度没过门限就不许动手()
    {
        var g = Guard();
        // 76 °C 转速恒定 1200 转：不到高温门限、也没风险 —— 那是固件的静音策略，别去打扰
        var (v, rescued, _) = Run(g, 1000, 5, 40, _ => 1200, _ => 76.0, duty: null);

        Assert.False(v.Stuck);
        Assert.False(rescued);
        Assert.Equal(0, g.Attempts);
        Assert.Equal("low", v.Risk);      // 有风险等级但没到高温判据
    }

    [Fact]
    public void 危险温度只需二十秒()
    {
        var g = Guard();
        // 92 °C 还 1500 转：再等一分钟就是白烤一分钟
        var v = Run(g, 1000, 5, 10, _ => 1500, _ => 92.0, duty: null).Last;

        Assert.True(v.Stuck);
        Assert.Equal(20, v.NeedSeconds, 0);
        Assert.Equal("high", v.Risk);
    }

    [Fact]
    public void 没有转速读数时不把风险报成高()
    {
        // 未判到卡住时 Detect 返回 rpm=null。以前 `RiskOf(temp, rpmAt ?? 0)` 把 null 当 0 转，
        // 于是"未卡住 + 80℃"也报 high（风扇明明 4000 转），诊断面板常驻"风险高 / 需持续 85 秒"。
        var g = Guard();
        var v = g.Update(1000, [4000, 4000, null], 80.0, 50, canAct: true);

        Assert.False(v.Stuck);
        Assert.NotEqual("high", v.Risk);
        Assert.Equal("medium", v.Risk);          // 高温但转速未知：按温度给 medium，不硬报高
    }

    [Fact]
    public void 升温快时比同温度的稳态更早介入()
    {
        // 稳态 84 °C：需要 60 秒
        var steady = Guard();
        var vSteady = Run(steady, 1000, 5, 20, _ => 1500, _ => 84.0, duty: null).Last;

        // 从 78 快速爬到 84（约 3 °C/min）：风险更高，等待时间要更短
        var rising = Guard();
        var vRising = Run(rising, 1000, 5, 20, _ => 1500, i => 78.0 + i * 0.25, duty: null).Last;

        Assert.True(vSteady.Stuck);
        Assert.True(vRising.NeedSeconds < vSteady.NeedSeconds,
                    $"升温时应更急：升温 {vRising.NeedSeconds:0.0}s vs 稳态 {vSteady.NeedSeconds:0.0}s");
    }

    [Fact]
    public void 高温但转速正常跟随不判卡()
    {
        var g = Guard();
        var v = Run(g, 1000, 5, 60, i => 2000 + (i / 4) * 300, i => 80.0 + i * 0.25, duty: null).Last;

        Assert.False(v.Stuck);
        Assert.Equal(0, g.Attempts);
    }

    // ================================================================ ② 温度变了转速不跟

    [Fact]
    public void 温度摆动超过十度转速一级没跳判卡住()
    {
        var g = Guard();
        // 60 → 72 °C（12 度），转速恒 2000，未到高温门限
        var v = Run(g, 1000, 5, 40, _ => 2000, i => 60.0 + i * 0.3, duty: null).Last;

        Assert.True(v.Stuck);
        Assert.Equal(FanStuckGuard.ReasonSwing, v.Reason);
        Assert.True(v.FrozenSeconds >= 150);
    }

    [Fact]
    public void 待机温度稳定转速恒定是正常的不许误判()
    {
        var g = Guard();
        var v = Run(g, 1000, 5, 200, _ => 1500, _ => 58.0, duty: null).Last;

        Assert.False(v.Stuck);
        Assert.Equal(0, g.Attempts);
        Assert.Null(v.Reason);
    }

    // ================================================================ ③ 下发目标后不跟

    [Fact]
    public void 目标变了二十秒转速不动判卡住()
    {
        var g = Guard();
        var rescued = false;
        var v = FanGuardVerdict.None;
        // 前 15 秒目标 40%（转速稳定在 1500），之后曲线要求 60% —— 转速一点没跟
        for (var i = 0; i < 12; i++)
        {
            var duty = i < 3 ? 40 : 60;
            v = g.Update(1000 + i * 5, [1500, 1500, null], 70.0, duty, true);
            rescued |= v.RescueNow;
        }

        Assert.True(v.Stuck);
        Assert.Equal(FanStuckGuard.ReasonDuty, v.Reason);
        Assert.True(rescued);
    }

    [Fact]
    public void 目标恒定转速恒定的稳态不算卡住()
    {
        var g = Guard();
        // ⚠️ 这条是 2026-11-18 修掉的误判形态：探针以前"没有就无条件起一个"，
        //    于是待机这种目标恒定 + 转速恒定，20 秒后必然被判成"下发了不跟"，
        //    一次误判就是白交回一次风扇（转速当着用户的面抖一下）。
        var (v, rescued, _) = Run(g, 1000, 5, 120, _ => 1500, _ => 60.0, duty: 45);

        Assert.False(v.Stuck);
        Assert.False(rescued);
        Assert.Equal(0, g.Attempts);
    }

    [Fact]
    public void 下发了新目标转速跟上就不判()
    {
        var g = Guard();
        var v = FanGuardVerdict.None;
        // 前 20 秒 1500 RPM，之后跳到 2400（跟着 40→60% 的目标走）
        for (var i = 0; i < 20; i++)
        {
            var now = 1000 + i * 5;
            var rpm = now < 1040 ? 1500 : 2400;
            v = g.Update(now, [rpm, rpm, null], 70.0, i < 4 ? 40 : 60, true);
        }

        Assert.False(v.Stuck);
    }

    [Fact]
    public void 目标已饱和时不做响应判定()
    {
        var g = Guard();
        // 92→100 这种本来就看不出来，转速不动是正常的
        var v = FanGuardVerdict.None;
        for (var i = 0; i < 20; i++)
            v = g.Update(1000 + i * 5, [4900, 4900, null], 92.0, i < 4 ? 92 : 100, true);

        Assert.False(v.Stuck);
    }

    // ================================================================ 转速 0

    [Fact]
    public void 交回固件无效时下一次自救提前到三十秒()
    {
        var g = Guard();
        Run(g, 1000, 5, 20, _ => 1500, _ => 84.0, duty: null);
        Assert.Equal(1, g.Attempts);
        Assert.True(g.RetryIn(1095) > 100);        // 常规冷却还得等 ~175 秒

        // 交回命令发出去了：允许 30 秒后就再来一次（届时由软件接管）
        g.Accelerate(1095);
        Assert.Equal(30, g.RetryIn(1095), 0);

        var rescued = false;
        for (var i = 1; i <= 6; i++)               // 1100 … 1125，每拍 5 秒，不触发观测中断
            rescued |= g.Update(1095 + i * 5, [1500, 1500, null], 84.0, null, true).RescueNow;

        Assert.True(rescued);
        Assert.Equal(2, g.Attempts);
    }

    [Fact]
    public void 睡眠唤醒后不把没观测的那段时间算成卡住()
    {
        var g = Guard();
        g.Update(1000, [1500, 1500, null], 84.0, null, true);

        // 六小时后醒来：这六小时里一帧都没观测过，不能判成"卡了六小时"
        var v = g.Update(1000 + 6 * 3600, [1500, 1500, null], 84.0, null, true);

        Assert.False(v.Stuck);
        Assert.Equal(0, g.Attempts);
    }

    [Fact]
    public void 转速为零不判卡住也不自救()
    {
        var g = Guard();
        var v = Run(g, 1000, 5, 200, _ => 0, _ => 90.0, duty: null).Last;

        Assert.False(v.Stuck);
        Assert.Equal(0, g.Attempts);
    }

    // ================================================================ 闸门与配额

    [Fact]
    public void 不允许写时照样判卡但不发自救()
    {
        var g = Guard();
        var v = Run(g, 1000, 5, 40, _ => 1500, _ => 84.0, duty: null, canAct: false).Last;

        Assert.True(v.Stuck);
        Assert.False(v.RescueNow);
        Assert.Equal(0, g.Attempts);       // 没有消耗配额，条件恢复后仍能救

        var again = g.Update(1200, [1500, 1500, null], 84.0, null, canAct: true);
        Assert.True(again.RescueNow);
    }

    [Fact]
    public void 一段卡死最多自救三次且间隔递增()
    {
        var g = Guard();
        var rescueAt = new List<double>();

        for (var i = 0; i < 400; i++)            // 2000 秒，一直卡在 1500 RPM / 84 °C
        {
            var now = 1000 + i * 5;
            var v = g.Update(now, [1500, 1500, null], 84.0, null, true);
            if (v.RescueNow) rescueAt.Add(now);
        }

        Assert.Equal(3, rescueAt.Count);
        Assert.Equal(60, rescueAt[0] - 1000, 0);       // 84 °C 按风险折算只要 60 秒
        Assert.Equal(180, rescueAt[1] - rescueAt[0], 0);   // 冷却 180 s × 1
        Assert.Equal(360, rescueAt[2] - rescueAt[1], 0);   // 冷却 180 s × 2
        Assert.Equal(3, g.Attempts);
    }

    [Fact]
    public void 转速恢复后计数归零并报一次恢复()
    {
        var g = Guard();
        // 先卡住并自救一次
        for (var i = 0; i < 20; i++)
            g.Update(1000 + i * 5, [1500, 1500, null], 84.0, null, true);
        Assert.Equal(1, g.Attempts);

        // 转速开始动（每拍都不一样），30 秒后算恢复
        var recovered = false;
        for (var i = 0; i < 10; i++)
        {
            var v = g.Update(1200 + i * 5, [2000 + i * 100, 2000 + i * 100, null], 70.0 - i, null, true);
            Assert.False(v.Stuck);
            recovered |= v.Recovered;
        }

        Assert.True(recovered);
        Assert.Equal(0, g.Attempts);
    }

    [Fact]
    public void 重置后从头开始计时()
    {
        var g = Guard();
        Run(g, 1000, 5, 20, _ => 1500, _ => 84.0, duty: null);
        Assert.True(g.Stuck);

        g.Reset();
        Assert.False(g.Stuck);
        Assert.Equal(0, g.Attempts);

        var v = g.Update(2000, [1500, 1500, null], 84.0, null, true);
        Assert.False(v.Stuck);                   // 刚起算，还不到 90 秒
    }
}
