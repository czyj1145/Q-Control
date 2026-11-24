using System.Diagnostics;
using System.Text.Json.Nodes;
using QControl.Core.Ec;
using QControl.Core.Model;

namespace QControl.App;

/// <summary>
/// 「时序与回显自检」—— 一键采集最后一段，纯读。
///
/// 为什么需要它：机型档案里有两个键决定"读得多快"和"读回来的东西能不能信"，
/// 以前都只能靠作者手工标：
/// <list type="bullet">
///   <item><c>ec.echo_verified</c>：哪些读命令的回帧前 4 字节会回带 group+sub。
///     带这个标记的读才做回显校验 —— 校验开着，缓冲区被原厂控制中心抢占时
///     会重试，而不是把别人的帧当成温度显示（2026-10-31 把信息帧认成 CPU 温度就是这一类）；</item>
///   <item><c>ec.read_gap</c>：两条读之间要等多久。空着就退回 <c>min_gap</c>（本机 0.2 s），
///     每帧白等，打开窗口首帧慢一倍。当年是手工连发 48 次测出来的。</item>
/// </list>
/// 两件事都**只需要读**，所以可以放在采集里自动做：连读若干次，看回帧对不对得上。
///
/// 边界（写进报告，不装懂）：
///   · 只测档案声明 `read_only` 的命令，`0x0400` 一律不碰（铁律）；
///   · 只给**建议值**：写进草案（verified 仍 false），作者复核后才进正式档案；
///   · 12 次零失败不等于长期零失败，所以建议值会在"第一个零失败档"上再加一档余量。
/// </summary>
internal static class EcTimingProbe
{
    /// <summary>
    /// 每一档间隔连发几次。20 ≈ 1 秒（无间隔时），而 12 次容易"碰巧全过"——
    /// 每帧都要读的那条命令，把间隔判松了的代价是偶发脏帧，不是慢一点。
    /// </summary>
    private const int BurstCount = 20;

    /// <summary>探测档位（秒）：从"完全不等"到"档案里的保守值"。</summary>
    private static readonly double[] Gaps = [0, 0.005, 0.01, 0.02, 0.05];

    /// <summary>建议值在"第一个零失败档"上加的余量 —— 样本只有 12 次，不留余量太赌。</summary>
    private const double Margin = 0.01;

    /// <summary>建议值的上限：超过它就不必再"优化"了，本来也就这个量级。</summary>
    private const double MaxSuggest = 0.2;

    public sealed record Result(
        JsonObject Json,
        double? ReadGap,
        double? BurstGap,
        IReadOnlyList<string> EchoVerified,
        IReadOnlyList<string> Notes);

    /// <summary>写批间隔的候选档（秒）。取"能用的最小档 +10 ms"，上限 50 ms。</summary>
    private static readonly double[] BurstGaps = [0.002, 0.01, 0.02, 0.05];

    public static Result Run(AppState st, bool allowWrites, Action<string> stage)
    {
        stage ??= _ => { };

        var notes = new List<string>();
        var json = new JsonObject { ["attempted"] = true };

        var reads = MachineReport.ReadCommands(st.Ec.Spec).ToList();
        if (reads.Count == 0)
        {
            notes.Add("时序自检没做成：档案里没有可读的子命令。");
            json["attempted"] = false;
            json["skip"] = "档案里没有可读的子命令";
            return new Result(json, null, null, [], notes);
        }

        // ---------------------------------------------------------------- 回显
        //
        // 每条读命令连读一次就够判断 —— 回帧前 4 字节要么是 group+sub，要么不是。
        // 但"一次对上"可能是碰巧，所以读两次、两次都对才算。
        stage("测读命令回显…");
        var echoCmds = new List<string>();
        var echoJson = new JsonArray();

        foreach (var (name, sub) in reads)
        {
            var s = (ushort)sub;
            var (ok1, resp1, err1) = OneRead(st, s, 0);
            var (ok2, resp2, err2) = OneRead(st, s, 0);
            var echo = ok1 && ok2 && EchoMatches(resp1, s) && EchoMatches(resp2, s);

            if (echo) echoCmds.Add(CmdTable.Hex(s));
            echoJson.Add(new JsonObject
            {
                ["cmd"] = CmdTable.Hex(s),
                ["name"] = name,
                ["echo"] = echo,
                ["resp"] = ok1 ? resp1 : null,
                ["error"] = err1 ?? err2,
            });
        }
        json["echo"] = echoJson;

        if (echoCmds.Count == 0)
            notes.Add("所有读命令的回帧都不带命令号（回显不可校验）⇒ 这类读**不能**靠间隔防抢占，"
                      + "档案里也就不会加 echo_verified。");
        else
            notes.Add("回显可校验的读命令：" + string.Join(" / ", echoCmds)
                      + "（回帧前 4 字节 = group+sub，能挡住「缓冲区被原厂抢占」这种脏帧）。");

        // ---------------------------------------------------------------- 写批间隔
        //
        // 放在读之前：这一段会写 EC，写完再测读会先吃一次"写后读"的等待（热身那行就是给它留的）。
        // 只有用户已经同意写（采集做了写验证）才跑 —— 只读采集一个字节都不写。
        // 写权限由 ProbeBurst 自己开一个"只放行背光"的短窗口（见那里的注释）。
        var burstGap = allowWrites
            ? ProbeBurst(st, stage, notes, json)
            : SkipBurst(notes);

        // ---------------------------------------------------------------- 读间隔
        //
        // 只拿**回显可校验**的命令测：没有回显就分不清"读快了出脏帧"和"这命令本来就不回显"。
        // 优先用 status（每帧都要读的那条，最有代表性）。
        var probeName = reads.FirstOrDefault(r => r.Name == "status").Name;
        var probeSub = echoCmds.Count == 0
            ? (ushort?)null
            : (ushort)reads.FirstOrDefault(r => r.Name == (probeName ?? r.Name)).Sub;

        if (probeSub is null)
        {
            notes.Add("测不出读间隔建议值（没有回显可校验的读命令）—— 档案里 read_gap 保持原样。");
            json["read_gap"] = null;
            return new Result(json, null, burstGap, echoCmds, notes);
        }

        stage("测读间隔…");
        _ = OneRead(st, probeSub.Value, 0);        // 热身：先把"写后第一条读"的等待消耗掉

        var rows = new JsonArray();
        var data = new List<(double Gap, int Fail)>();
        foreach (var gap in Gaps)
        {
            var fail = 0;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < BurstCount; i++)
            {
                var (ok, resp, _) = OneRead(st, probeSub.Value, gap);
                if (!ok || !EchoMatches(resp, probeSub.Value)) fail++;
            }
            sw.Stop();

            data.Add((gap, fail));
            rows.Add(new JsonObject
            {
                ["gap_ms"] = (int)Math.Round(gap * 1000),
                ["fail"] = fail,
                ["total"] = BurstCount,
                ["elapsed_ms"] = (int)Math.Round(sw.Elapsed.TotalMilliseconds),
            });

            if (fail == 0) break;                  // 更宽的档位不必再测
        }
        var safe = data.Where(d => d.Fail == 0).Select(d => (double?)d.Gap).DefaultIfEmpty(null).Min();

        json["read_gap"] = new JsonObject
        {
            ["probe_cmd"] = CmdTable.Hex(probeSub.Value),
            ["burst"] = BurstCount,
            ["rows"] = rows,
            ["safe_gap_ms"] = safe is null ? null : (int)Math.Round(safe.Value * 1000),
        };

        // 建议值统一由 Core 决定（第一个零失败档 + 余量），这里只负责把数据喂进去。
        var suggest = EcEcho.PickReadGap(data, Margin, MaxSuggest);
        if (suggest is null)
        {
            notes.Add($"连发 {BurstCount} 次，最慢那一档（{Gaps[^1] * 1000:0} ms）仍有失败 ⇒ "
                      + "这条读命令的间隔保持档案里的值，不做提速建议。");
            return new Result(json, null, burstGap, echoCmds, notes);
        }

        json["read_gap"]!["suggest_ms"] = (int)Math.Round(suggest.Value * 1000);
        notes.Add($"读间隔实测：第一条零失败的档是 {(int)Math.Round(safe!.Value * 1000)} ms，"
                  + $"建议写 {suggest.Value * 1000:0} ms（样本 {BurstCount} 次，留 {Margin * 1000:0} ms 余量）。"
                  + (safe.Value == 0
                      ? "**这台机器的读与读之间不需要等待** —— 比档案里的 min_gap 快一个数量级。"
                      : ""));

        return new Result(json, suggest, burstGap, echoCmds, notes);
    }

    // ---------------------------------------------------------------- 写批间隔

    /// <summary>
    /// 写批间隔（`ec.burst_gap`）：神光同步"三个区一起变"还是"一个一个爬"就取决于它。
    ///
    /// 测法：对已有的键盘分区地址连发三条**同档位、同颜色**的 `0x0100`（外观不变），
    /// 从最小间隔往上找"三条都成功、且总耗时接近单条往返×3"的第一档。
    /// 总耗时不达标说明固件在忙等 —— 那一档虽然没报错，也不算真的连发成功。
    ///
    /// 为什么可以写：这一段只在用户点了「完整采集」（已经同意写验证）后才跑，
    /// 而且写的是**当前档位 + 探针刚写过的白色**，不会把灯留在别的状态。
    /// </summary>
    private static double? ProbeBurst(AppState st, Action<string> stage, List<string> notes, JsonObject json)
    {
        var zones = ZoneList(st);
        if (zones.Count < 2)
        {
            notes.Add("写批间隔没测：配置里只有 " + zones.Count + " 个键盘分区地址"
                      + "（连发至少要两个区才有意义）—— 档案里 burst_gap 保持原样。");
            return null;
        }

        // ⚠️ **本段自己开临时写窗口**（2026-11-23 审查 P1-4）。
        //    采集写验证用的那个窗口（`MachineProbe.Run` 里的 `using var gate`）在这一段
        //    之前就 Dispose 了，而未适配机型（`writes_allowed=false`）的写只认这个窗口 ⇒
        //    试探写必然被 WriteDeniedException 拦下，报告于是写"这台机器的写入路径本身有问题"
        //    —— 把"守卫拒绝"当成了硬件结论。
        //    这里只放行背光子命令、30 秒、本段结束即收回；管理员 / EC 总闸 / 机型许可照判。
        var blSub = st.Ec.Spec.Sub("backlight");
        using var gate = blSub > 0
            ? st.Ec.OpenWriteProbe(TimeSpan.FromSeconds(30), (ushort)blSub)
            : null;

        int level;
        try
        {
            level = (int)(EcSampling.Num(st.Ec.ReadStatus()["backlight_level"]) ?? 2);
        }
        catch (Exception e)
        {
            notes.Add("写批间隔没测：读不到当前背光档位（" + e.Message + "）。");
            return null;
        }

        stage("测背光连发间隔…");

        // 基准：单条写入的纯往返。用 gap=0 的写批包一条，把节流完全摘掉再计时。
        double single;
        try
        {
            var sw0 = Stopwatch.StartNew();
            using (st.Ec.Burst(0))
                st.Ec.SetBacklight(level, 255, 255, 255, zones[0].Addr);
            sw0.Stop();
            single = sw0.Elapsed.TotalMilliseconds;
        }
        catch (QControl.Hal.Ec.WriteDeniedException e)
        {
            // 本段已经开过"只放行背光"的临时写窗口，还被拒 ⇒ 拦它的是**真门**：
            // EC 写入总闸关着 / 不是管理员 / 这台机型禁写。不是硬件问题，别下那种结论。
            notes.Add("写批间隔没测：试探写被守卫拒绝（EC 写入总闸关着、不是管理员，或这台机型禁写）"
                      + "—— 不是硬件问题；连发间隔要在能写的机器上测。"
                      + "（" + e.Message + "）");
            json["burst_gap"] = new JsonObject { ["skipped"] = "写权限未开" };
            return null;
        }
        catch (Exception e)
        {
            notes.Add("写批间隔没测：单条背光写入就失败了（" + e.Message + "）—— 这台机器的写入路径本身有问题。");
            return null;
        }

        var rows = new JsonArray();
        double? pick = null;
        double pickMs = 0;

        foreach (var gap in BurstGaps)
        {
            Thread.Sleep(250);              // 让 EC 从上一轮里空下来，否则测到的是排队时间
            var sw = Stopwatch.StartNew();
            string? err = null;
            try
            {
                using (st.Ec.Burst(gap))
                    foreach (var z in zones.Take(3))
                        st.Ec.SetBacklight(level, 255, 255, 255, z.Addr, z.Effect);
            }
            catch (Exception e)
            {
                err = e.Message;
            }
            sw.Stop();

            var ms = sw.Elapsed.TotalMilliseconds;

            // 判据只认两件事：三条都没抛，且写完 EC 还能正常读回同一个档位。
            // **不看耗时** —— 0x0100 每条都要等固件刷新 LED 引擎（实测 27 ms → 170 ms/条），
            // 那个开销和间隔无关，拿它当"没连发成功"会把每一档都判掉（2026-11-15 第一版就这样）。
            var readback = true;
            if (err is null)
            {
                try
                {
                    readback = (int)(EcSampling.Num(st.Ec.ReadStatus()["backlight_level"]) ?? level) == level;
                }
                catch { readback = false; }
            }
            var ok = err is null && readback;

            rows.Add(new JsonObject
            {
                ["gap_ms"] = (int)Math.Round(gap * 1000),
                ["ok"] = ok,
                ["elapsed_ms"] = (int)Math.Round(ms),
                ["readback"] = readback,
                ["error"] = err,
            });

            if (ok) { pick = gap; pickMs = ms; break; }
        }

        json["burst_gap"] = new JsonObject
        {
            ["single_ms"] = (int)Math.Round(single),
            ["addrs"] = new JsonArray(zones.Take(3).Select(z => (JsonNode?)JsonValue.Create(z.Addr)).ToArray()),
            ["rows"] = rows,
            ["suggest_ms"] = pick is null ? null : (int)Math.Round(Math.Min(0.05, pick.Value + 0.01) * 1000),
        };

        // 不管测出什么，都把灯恢复成配置里的颜色（探针刚把它写成过白色）。
        try { st.Ec.SetBacklightZones(level, zones.Select(z => (z.Addr, z.Rgb)).ToList(), null); }
        catch { /* 恢复失败不影响结论，报告里不装懂 */ }

        if (pick is null)
        {
            notes.Add($"写批间隔没测出安全值（单条 {(int)single} ms，三条连发在最慢档也超预算）"
                      + "—— 这台机器的背光写入只能逐条发，神光同步会比原厂慢，但不会错。");
            return null;
        }

        var suggest = Math.Round(Math.Min(0.05, pick.Value + 0.01), 3);
        notes.Add($"背光连发实测：单条 {(int)single} ms；间隔 {(int)Math.Round(pick.Value * 1000)} ms 时"
                  + $"三条 {(int)Math.Round(pickMs)} ms 发完且档位读回一致，建议 burst_gap = {suggest * 1000:0} ms。"
                  + "⚠️ 三条连发的大部分时间花在固件刷新 LED 引擎上（每条都要等上一落地），"
                  + "间隔压到最小也只省几十毫秒；**要真正「同时变」得靠整条地址**（`kb_trunk_addr`）。");
        return suggest;
    }

    /// <summary>
    /// 只读采集时跳过写批间隔，但要说清楚"为什么没测"，不然包里这一项像是漏了。
    /// </summary>
    private static double? SkipBurst(List<string> notes)
    {
        notes.Add("写批间隔没测：这轮是只读采集（没有写 EC）—— 档案里 burst_gap 保持原样，"
                  + "神光同步会按逐条写的节奏，慢一点但不会错。");
        return null;
    }

    private sealed record Zone(int Addr, int[] Rgb, int Effect);

    /// <summary>配置里的键盘分区（地址 + 颜色）。读不出来就返回空 —— 不猜地址。</summary>
    private static List<Zone> ZoneList(AppState st)
    {
        var list = new List<Zone>();
        if (st.Model.Get("backlight", "zones") is not JsonArray zarr) return list;

        var effect = (int)(EcSampling.Num(st.Model.Get("backlight", "effect")) ?? 1);
        foreach (var node in zarr)
        {
            if (node is not JsonObject zo) continue;
            if (EcSampling.Num(zo["addr"]) is not { } a) continue;

            var rgb = new[] { 255, 255, 255 };
            if (zo["rgb"] is JsonArray c && c.Count == 3)
            {
                for (var i = 0; i < 3; i++)
                    rgb[i] = (int)(EcSampling.Num(c[i]) ?? 255);
            }
            list.Add(new Zone((int)a, rgb, effect));
        }
        return list;
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>读一条命令。任何异常都折成 (false, "", 原因)，不让探测本身把采集带崩。</summary>
    private static (bool Ok, string Resp, string? Error) OneRead(AppState st, ushort sub, double gap)
    {
        try
        {
            var r = st.Ec.Raw(new EcCommand(sub, EcDirection.Read), gap);
            var resp = r["resp"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            return (resp.Length > 0, resp, null);
        }
        catch (Exception e)
        {
            return (false, "", e.Message);
        }
    }

    /// <summary>
    /// 回帧前 4 字节是不是 `group(LE) + sub(LE)`。判定规则在 Core（可单测），这里只转一层。
    /// 读组固定 0xFA00 ⇒ 帧头形如 `00 FA 00 02`（status）。
    /// </summary>
    internal static bool EchoMatches(string resp, ushort sub) => EcEcho.Matches(resp, 0xFA00, sub);
}
