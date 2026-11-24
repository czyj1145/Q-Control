using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using QControl.Core.Model;

namespace QControl.Core.Ec;

/// <summary>一次写尝试的结局。三档必须分开记：被守卫拦下 ≠ 发出去失败 ≠ 成功。</summary>
public enum EcWriteOutcome
{
    /// <summary>发出去且 EC 回了帧（写成功后 EC 的应答内容不作校验，见 EcHal.Exchange）。</summary>
    Ok,

    /// <summary>被守卫拒绝（没管理员 / 总闸关 / 机型未验证 / 高危没 arm）—— 一条字节都没发出去。</summary>
    Denied,

    /// <summary>发出去了但往返失败（WMI 异常 / 长度不对 / 超时）。</summary>
    Error,
}

/// <summary>
/// 一次写尝试的记录。<see cref="Reply"/> 是 EC 回的 32 字节（失败时为空），
/// 复核的人靠它判断"EC 到底有没有接受这条命令"。
/// </summary>
public sealed record EcWriteAttempt(
    DateTimeOffset At,
    ushort Sub,
    EcArgs Args,
    EcWriteOutcome Outcome,
    string Reply,
    string? Error)
{
    public string Cmd => CmdTable.Hex(Sub);

    /// <summary>参数里**非零的那些槽**（`d0=3`、`d0=55 d2=55`）。</summary>
    public string ArgsText => Describe(Args);

    /// <summary>
    /// 只印非零槽 —— 除了"全零"这一种情况：那说明写进去的参数就是 0，
    /// 而 **0 是合法参数值**（APMD=0 就是狂暴档）。早先印「—」，读日志的人会以为
    /// "这条命令没带参数"，于是把一次真实的档位写入当成空写（2026-11-08 采集包实测踩到）。
    /// </summary>
    public static string Describe(EcArgs args)
    {
        var sb = new StringBuilder();
        for (var i = 0; i <= (int)EcSlot.D; i++)
        {
            var slot = (EcSlot)i;
            var v = args.Get(slot);
            if (v == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(EcArgs.SlotName(slot)).Append('=').Append(v);
        }
        return sb.Length == 0 ? "d0=0" : sb.ToString();
    }

    /// <summary>给人看的 JSON 里每条记录的形状（`at` 用本地时间，复核时和日志对得上）。</summary>
    public JsonObject ToJson() => new()
    {
        ["at"] = At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        ["cmd"] = Cmd,
        ["args"] = ArgsText,
        ["args_raw"] = new JsonArray(Args.ToSequence().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
        ["result"] = Outcome switch
        {
            EcWriteOutcome.Ok => "ok",
            EcWriteOutcome.Denied => "denied",
            _ => "error",
        },
        ["reply"] = Reply,
        ["error"] = Error,
    };
}

/// <summary>
/// 最近若干次**写**尝试的环形记录 —— 只为「采集 → 发回作者」这条路存在。
///
/// 为什么要记在 HAL：写入的成功/失败是**最容易被假象骗过去**的一件事 ——
///   ① 非管理员时 WMI 的 Put 不报错也不生效（旧实现回 ok:true，风扇一动没动）；
///   ② 写路径对传输异常是**宽容**的（见 <c>EcHal.Exchange</c>），失败会变成"静默无效果"；
///   ③ 守卫拒绝根本到不了 EC，用户只看到一句提示。
/// 所以采集包里不能只有"读了什么"，还得有"**写了什么、EC 怎么回的**"。
/// 作者靠这份记录判断这台机器的命令表是不是同一套 —— 比用户口述"好像能用"可靠得多。
/// 2026-11-08 起「一键采集」自己会写两条（性能档 / 键盘背光）并读回，这份记录就是那条结论的底账。
///
/// 只存内存（不落盘、不进历史）：它属于"这次会话发生了什么"，重启后清零是合理的；
/// 采集时把当时的内容整段抄进采集包，那才是要交付的东西。
/// </summary>
public sealed class EcWriteLog
{
    /// <summary>最多留多少条。40 条足够覆盖"打开开关 → 切几档 → 调风扇/背光"一轮操作。</summary>
    public const int Capacity = 40;

    private readonly Queue<EcWriteAttempt> _items = new();
    private readonly object _lock = new();
    private long _dropped;
    private long _total;

    public void Add(EcWriteAttempt attempt)
    {
        lock (_lock)
        {
            _total++;
            _items.Enqueue(attempt);
            while (_items.Count > Capacity)
            {
                _items.Dequeue();
                _dropped++;
            }
        }
    }

    /// <summary>按时间先后（最旧在前）。返回快照，调用方拿去遍历是安全的。</summary>
    public IReadOnlyList<EcWriteAttempt> Snapshot()
    {
        lock (_lock) return _items.ToArray();
    }

    /// <summary>因为超出容量被丢掉的条数 —— 采集包里要说明"这不是全部"，不能假装完整。</summary>
    public long Dropped
    {
        get { lock (_lock) return _dropped; }
    }

    /// <summary>
    /// 这个进程里**一共**发生过多少次写尝试（含被挤出队列的）。
    ///
    /// 为什么还要一个累计数：`Snapshot().Count` 在队列满 40 条以后就不再增长，
    /// 采集期间新写的条数会被同期风扇曲线的写入抵消掉 —— 2026-11-23 真机：
    /// 完整采集明明写了 10 条，包里 `writes_added` 却是 0。
    /// 用 <c>Total</c> 前后相减才是"这段区间里到底写了几次"。
    /// </summary>
    public long Total
    {
        get { lock (_lock) return _total; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _dropped = 0;
            _total = 0;
        }
    }

    public JsonObject ToJson()
    {
        var items = Snapshot();
        var ok = 0;
        var denied = 0;
        var error = 0;
        foreach (var i in items)
        {
            switch (i.Outcome)
            {
                case EcWriteOutcome.Ok: ok++; break;
                case EcWriteOutcome.Denied: denied++; break;
                default: error++; break;
            }
        }

        return new JsonObject
        {
            ["kept"] = items.Count,
            ["dropped"] = Dropped,
            ["ok"] = ok,
            ["denied"] = denied,
            ["error"] = error,
            ["items"] = new JsonArray(items.Select(i => (JsonNode?)i.ToJson()).ToArray()),
        };
    }

    /// <summary>
    /// 报告 txt 里的那一段。**空的时候也要写字**：用户"想试但没写"和
    /// "写了但被拦"是两种完全不同的结论，静默什么都不印就等于让作者去猜。
    ///
    /// <paramref name="hideSub"/> = 要折叠掉的子命令（采集传风扇 0x0205）：
    /// 风扇曲线每隔几秒就写一次转速，40 条的容量很快会被它占满，
    /// 真正要看的那几条（性能档 / 背光）反而被挤没了。折叠不是隐瞒 —— 条数照报。
    /// </summary>
    public string ToText(ushort? hideSub = null)
    {
        var all = Snapshot();
        var shown = hideSub is { } hide ? all.Where(i => i.Sub != hide).ToList() : all.ToList();
        var hidden = all.Count - shown.Count;

        var sb = new StringBuilder();
        var ok = shown.Count(i => i.Outcome == EcWriteOutcome.Ok);
        var denied = shown.Count(i => i.Outcome == EcWriteOutcome.Denied);
        var error = shown.Count(i => i.Outcome == EcWriteOutcome.Error);

        if (shown.Count == 0)
        {
            sb.AppendLine(hidden > 0
                ? $"  这次没有非风扇的写入记录（另有 {hidden} 条风扇转速写入，是曲线自己写的）。"
                : "  这次没有写入记录（一条写命令都没发出去）。");
            sb.AppendLine("  只读采集就是这个样子 —— 要让作者复核写入，点「一键采集」（完整）再采一次即可，");
            sb.AppendLine("  它会自己写两条命令（性能档 / 键盘背光）并读回，不需要你先去开「EC 控制」。");
            return sb.ToString();
        }

        // 这里**不能**写"共 N 次"：N 是当前队列里的条数，队列满 40 条后会随着新写入
        // 把老记录挤出去 —— 一个"共 11 次（更早 111 条已滚出）"读起来像
        // "总共写了 122 次"，和正文里另一处"采集之前已有 15 次"必然打架
        // （2026-11-23 真机：只读采集的报告里就这么自相矛盾过）。
        sb.Append("  这份记录里现有 ").Append(shown.Count).Append(" 条：成功 ").Append(ok)
          .Append(" / 被拦 ").Append(denied).Append(" / 失败 ").Append(error);
        var extra = new List<string>();
        if (hidden > 0) extra.Add($"另有 {hidden} 条风扇转速写入未逐条列出");
        if (Dropped > 0) extra.Add($"更早的 {Dropped} 条已滚出记录");
        if (extra.Count > 0) sb.Append("（").Append(string.Join("；", extra)).Append("）");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("  注：这是进程内最近 " + Capacity + " 条写入记录，含采集期间其它模块"
                      + "（切档时重放的灯效、风扇曲线）发的写入，不全是本次采集发的。");
        sb.AppendLine();

        foreach (var i in shown)
        {
            var result = i.Outcome switch
            {
                EcWriteOutcome.Ok => "OK   ",
                EcWriteOutcome.Denied => "DENY ",
                _ => "ERR  ",
            };
            sb.Append("  ").Append(i.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
              .Append("  ").Append(result)
              .Append(i.Cmd).Append("  ")
              .Append(i.ArgsText);

            if (i.Outcome == EcWriteOutcome.Ok)
            {
                // 应答只印前 8 字节：整帧 32 字节挤进文本就没人看了，需要全帧的看 json
                sb.Append("  ← ").Append(Head(i.Reply, 8));
            }
            else if (!string.IsNullOrWhiteSpace(i.Error))
            {
                sb.Append("  ").Append(Short(i.Error!, 120));
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Head(string hex, int bytes)
    {
        if (hex.Length == 0) return "（空帧）";
        var parts = hex.Split(' ');
        return parts.Length <= bytes ? hex : string.Join(' ', parts.Take(bytes)) + " …";
    }

    private static string Short(string text, int max)
    {
        var one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}
