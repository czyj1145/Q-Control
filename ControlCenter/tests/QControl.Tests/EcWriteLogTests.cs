using QControl.Core.Ec;

namespace QControl.Tests;

/// <summary>
/// 「最近写入记录」—— 只为「采集 → 发回作者」这条路存在。
///
/// 要钉死的三件事：
///   · 有上限、不无限长（它是内存里的，跑一天不能变成几千条）；
///   · 成功 / 被拦 / 往返失败**分开数**（同一句"没反应"三种根因，糊在一起就没用了）；
///   · 空的时候也要写字（"没试过"和"试了被拦"是两种结论，静默等于让人猜）。
/// </summary>
public class EcWriteLogTests
{
    private static EcWriteAttempt Attempt(ushort sub, EcWriteOutcome outcome, string reply = "", string? error = null,
                                          EcArgs args = default)
        => new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(8)), sub, args, outcome, reply, error);

    [Fact]
    public void 超过容量时丢掉最旧的并记账()
    {
        var log = new EcWriteLog();
        for (var i = 0; i < EcWriteLog.Capacity + 5; i++)
            log.Add(Attempt(0x0300, EcWriteOutcome.Ok, args: EcArgs.FromD0((uint)i)));

        var items = log.Snapshot();

        Assert.Equal(EcWriteLog.Capacity, items.Count);
        Assert.Equal(5, log.Dropped);
        // 留下来的必须是最新的那批：最后一条的 d0 是最后一次写进去的值
        Assert.Equal((uint)(EcWriteLog.Capacity + 4), items[^1].Args.D0);
        Assert.Contains("滚出记录", log.ToText());
    }

    [Fact]
    public void 三种结局分开计数()
    {
        var log = new EcWriteLog();
        log.Add(Attempt(0x0300, EcWriteOutcome.Ok, reply: "00 FB 00 03 00 00 00 00"));
        log.Add(Attempt(0x0300, EcWriteOutcome.Ok, reply: "00 FB 00 03 00 00 00 00"));
        log.Add(Attempt(0x0100, EcWriteOutcome.Denied, error: "需要先二次确认授权（arm）"));
        log.Add(Attempt(0x0205, EcWriteOutcome.Error, error: "EC 往返失败（试了 3 次）"));

        var json = log.ToJson();

        Assert.Equal(4, json["kept"]!.GetValue<int>());
        Assert.Equal(2, json["ok"]!.GetValue<int>());
        Assert.Equal(1, json["denied"]!.GetValue<int>());
        Assert.Equal(1, json["error"]!.GetValue<int>());
        Assert.Equal(4, json["items"]!.AsArray().Count);

        var text = log.ToText();
        Assert.Contains("成功 2 / 被拦 1 / 失败 1", text);
        Assert.Contains("DENY", text);
        Assert.Contains("需要先二次确认授权", text);
    }

    [Fact]
    public void 一条都没有时给的是怎么试而不是空白()
    {
        var text = new EcWriteLog().ToText();

        Assert.Contains("没有写入记录", text);
        Assert.Contains("EC 控制", text);
    }

    [Fact]
    public void 风扇曲线自己的写入可以被折叠但不隐瞒条数()
    {
        // 风扇曲线每隔几秒写一次转速，40 条的环很快会被它占满 ——
        // 报告里要折叠它，否则真正要看的那几条（性能档 / 背光）根本找不着。
        var log = new EcWriteLog();
        log.Add(Attempt(0x0205, EcWriteOutcome.Ok, args: EcArgs.Empty.With(EcSlot.D0, 80)));
        log.Add(Attempt(0x0300, EcWriteOutcome.Ok, args: EcArgs.FromD0(1)));
        log.Add(Attempt(0x0205, EcWriteOutcome.Ok, args: EcArgs.Empty.With(EcSlot.D0, 82)));

        var text = log.ToText(hideSub: 0x0205);

        Assert.Contains("0x0300", text);
        Assert.DoesNotContain("0x0205", text);
        Assert.Contains("另有 2 条风扇转速写入未逐条列出", text);
        // 逐条折叠，但计数照实（1 条非风扇写入）。
        // ⚠️ 措辞是"现有 1 条"不是"共 1 次"：后者会被读成"总共只写了 1 次"，
        //    而队列满 40 条后老记录会被挤出去（2026-11-23 真机就因此自相矛盾）。
        Assert.Contains("这份记录里现有 1 条", text);
    }

    [Fact]
    public void 累计次数不受容量上限影响()
    {
        // writes_added 靠 Total 前后相减：队列满 40 条之后 Count 不再增长，
        // 用它做差值会把"这次写了 10 次"算成 0（2026-11-23 真机：
        // 完整采集明明写了 10 条，包里 writes_added=0）。
        var log = new EcWriteLog();
        for (var i = 0; i < EcWriteLog.Capacity; i++)
            log.Add(Attempt(0x0300, EcWriteOutcome.Ok));

        var totalBefore = log.Total;
        var keptBefore = log.Snapshot().Count;

        for (var i = 0; i < 7; i++)
            log.Add(Attempt(0x0300, EcWriteOutcome.Ok));

        Assert.Equal(totalBefore + 7, log.Total);
        // 这就是旧算法会报 0 的原因：队列已满，条数一点没变
        Assert.Equal(keptBefore, log.Snapshot().Count);
    }

    [Fact]
    public void 记录条数不能被说成总写入次数()
    {
        var log = new EcWriteLog();
        for (var i = 0; i < EcWriteLog.Capacity + 11; i++)
            log.Add(Attempt(0x0300, EcWriteOutcome.Ok));

        var text = log.ToText();

        // "共 51 次（更早 11 条已滚出）"读起来像写了 62 次；现在是"现有 40 条 + 滚出 11 条"
        Assert.DoesNotContain("共 51 次", text);
        Assert.Contains("这份记录里现有 40 条", text);
        Assert.Contains("更早的 11 条已滚出记录", text);
        // 明细里必须说清这批记录不全是本次采集发的，否则只读采集的用户
        // 会以为"没写"是假话
        Assert.Contains("不全是本次采集发的", text);
    }

    [Fact]
    public void 只有风扇写入时也要说明白()
    {
        var log = new EcWriteLog();
        log.Add(Attempt(0x0205, EcWriteOutcome.Ok, args: EcArgs.Empty.With(EcSlot.D0, 80)));

        var text = log.ToText(hideSub: 0x0205);

        Assert.Contains("没有非风扇的写入记录", text);
        Assert.Contains("另有 1 条风扇转速写入", text);
    }

    [Fact]
    public void 参数只印非零槽()
    {
        // 全零不能印成"没带参数"：0 是合法值（APMD=0 就是狂暴档），
        // 印「—」会让复核的人把一次真实的档位写入看成空写
        Assert.Equal("d0=0", EcWriteAttempt.Describe(EcArgs.Empty));
        Assert.Equal("d0=3", EcWriteAttempt.Describe(EcArgs.FromD0(3)));
        Assert.Equal("d0=0", EcWriteAttempt.Describe(EcArgs.FromD0(0)));

        var fan = EcArgs.Empty.With(EcSlot.D0, 55).With(EcSlot.D2, 55);
        Assert.Equal("d0=55 d2=55", EcWriteAttempt.Describe(fan));
    }

    [Fact]
    public void 单条记录的json带原始参数与回帧()
    {
        var a = Attempt(0x0300, EcWriteOutcome.Ok, reply: "00 FB 00 03", args: EcArgs.FromD0(3));
        var json = a.ToJson();

        Assert.Equal("0x0300", json["cmd"]!.GetValue<string>());
        Assert.Equal("d0=3", json["args"]!.GetValue<string>());
        Assert.Equal("ok", json["result"]!.GetValue<string>());
        Assert.Equal(3u, json["args_raw"]![0]!.GetValue<uint>());
    }
}
