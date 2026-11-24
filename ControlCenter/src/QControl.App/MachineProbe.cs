using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Ec;
using QControl.Core.Model;

namespace QControl.App;

/// <summary>
/// 「一键采集」里的**写入验证**那一半 —— 真正下发命令、读回、恢复原值。
///
/// 为什么采集要写：只读采样只能证明"读得出来"。一台机器要能真正适配（性能档能切、
/// 键盘灯能调、caps 能定），还必须回答"**写进去的东西到底落到 EC 了没有**"。
/// 这件事以前要靠用户自己走三跳（设置页开总闸 → 自己试写 → 再回来采集），
/// 绝大多数人走到第二跳就散了。现在它属于同一次采集：点一次，读和写一起验完。
///
/// 只验两条命令，**而且顺序就是危险度顺序**：
/// <list type="number">
///   <item><b>0x0300 性能档</b>：写错了最坏是功耗墙不对，切回来就没事；</item>
///   <item><b>0x0100 键盘背光</b>：写错了最多亮错分区，不伤硬件；</item>
///   <item><b>0x0205 手动风扇：不做。</b> EC 拿到非零 UFA 后会一直按那个值转
///     （写 0 = 停转），只有重启/睡眠唤醒能交回原生自动 —— 采集不能把用户的机器
///     留在那个状态里。这条路按原厂行为人工确认，包里如实写"未验证"。</item>
/// </list>
///
/// 三条硬约束：
///   · **能恢复才写**：读不到当前 APMD 就整段跳过（没有原值 = 没法写回）；
///   · **写完必读回**：`ok:true` 只说明 WMI 收下了，不说明 EC 接受了（本项目最忌讳的假成功）；
///   · **只放行这两条**：写入窗口由 <see cref="QControl.Hal.Ec.EcHal.OpenWriteProbe"/> 开，
///     白名单 + TTL，管理员与 arm 照判，采完立刻收回。
/// </summary>
internal static class MachineProbe
{
    /// <summary>写完等多久再读回：EC 落地是毫秒级，但 WMI 往返本身有 0.2~0.5 秒节流。</summary>
    private const int SettleMs = 450;

    /// <summary>写入窗口的 TTL。够跑完两条探针，又短到"万一没走到 finally"也不会一直开着。</summary>
    private const int GateTtlSec = 90;

    /// <summary>用来比对的两个背光档位。取 0 与 2（两端），中间档在有的固件上会被合并。</summary>
    private const int LevelLow = 0;
    private const int LevelHigh = 2;

    /// <summary>背光探针的结论。<c>Offset</c> 有值 = 档位字段定位成功（可以写进 layout）。</summary>
    public sealed record BacklightResult(JsonObject Json, int? Offset);

    public sealed record Outcome(JsonObject WriteTest, int? BacklightOffset, IReadOnlyList<string> Notes);

    /// <summary>
    /// 跑一遍写入验证，返回采集包里的 <c>write_test</c> 段。
    /// </summary>
    /// <param name="requested">用户在界面上选的是"完整采集"（true）还是"只读采集"（false）。</param>
    public static Outcome Run(AppState st, bool requested, Action<string> stage)
    {
        var notes = new List<string>();
        var dev = st.Current;
        var spec = st.Ec.Spec;

        // 采集前**已有**的写尝试：要排除风扇曲线自己每秒写的那条 0x0205，
        // 否则一台正在跑曲线的机器会把"你自己试过 0 次"说成"试过 5 次"。
        var fanSub = spec.Sub("fan");
        var pre = st.Ec.WriteLog.Snapshot();
        var before = pre.Count;
        // 累计写入次数（含已被挤出队列的）：区间新增只能靠它前后相减，
        // 用 Snapshot().Count 差值会在队列满 40 条后被同期风扇写入抵消（2026-11-23 真机）。
        var totalBefore = st.Ec.WriteLog.Total;
        var manualBefore = fanSub <= 0 ? before : pre.Count(a => a.Sub != (ushort)fanSub);

        var w = new JsonObject
        {
            ["mode"] = requested ? "probe" : "readonly",
            ["requested"] = requested,
            ["admin"] = st.Ec.IsAdmin(),
            ["ec_writes_allowed"] = dev.WritesAllowed,
            ["profile_writes"] = dev.ProfileWrites,
            ["user_override"] = dev.EcOverride,
        };

        var perfSub = spec.Sub("perf");
        var backlightSub = spec.Sub("backlight");
        var statusSub = spec.Sub("status");

        var skip = SkipReason(st, requested, perfSub, backlightSub);
        int? backlightOffset = null;

        if (skip is not null)
        {
            w["skip"] = skip;
            w["verdict"] = "这次没有验证写入：" + skip;
            if (requested) notes.Add("没有验证写入：" + skip);
        }
        else
        {
            var subs = new List<ushort>();
            if (perfSub > 0) subs.Add((ushort)perfSub);
            if (backlightSub > 0) subs.Add((ushort)backlightSub);

            w["gate"] = new JsonObject
            {
                ["scoped"] = true,
                ["ttl_sec"] = GateTtlSec,
                ["subs"] = new JsonArray(subs.Select(s => (JsonNode?)JsonValue.Create(CmdTable.Hex(s))).ToArray()),
                ["note"] = "采集期间临时放行的写命令（不含风扇）。采集结束立刻收回；"
                           + "管理员与高危 arm 照判，机型/总闸那一道门只对这两条开缝。",
            };

            using var gate = st.Ec.OpenWriteProbe(TimeSpan.FromSeconds(GateTtlSec), subs.ToArray());

            stage("验证性能档…");
            var perf = perfSub > 0 ? PerfProbe(st, (ushort)perfSub, notes) : NotInProfile("perf", perfSub);
            w["perf"] = perf;

            stage("验证键盘背光…");
            var bl = backlightSub > 0 && statusSub > 0
                ? BacklightProbe(st, (ushort)backlightSub, (ushort)statusSub, notes)
                : new BacklightResult(NotInProfile("backlight", backlightSub), null);
            w["backlight"] = bl.Json;
            backlightOffset = bl.Offset;

            // 风扇：**故意不做**。写清楚原因，别让复核的人以为漏了
            w["fan"] = new JsonObject
            {
                ["attempted"] = false,
                ["cmd"] = CmdTable.Hex(0x0205),
                ["why"] = "风扇写入会让 EC 一直按写进去的值转（写 0 是停转），交回原生自动要显式发"
                          + " 0x0206=0 + UFA=0xFF 两条命令（见 EcHal.ReleaseToEc）—— 采集只是只读，"
                          + "不碰风扇；真要验请在界面上开一次「自定义风扇」再关掉，风扇会自己回到固件曲线。",
            };

            w["verdict"] = Verdict(perf, bl.Json);
        }

        // ---- 原有字段照留（导入端与旧包都按这几个键读）----
        try
        {
            var apmd = st.Ec.ReadPerfApmd();
            w["apmd_now"] = apmd;
            w["perf_key"] = st.Ec.PerfModeKey(apmd).Key;
        }
        catch (Exception e)
        {
            w["apmd_error"] = e.Message;           // 读不到就空着，别编一个 0（0 本身是合法档位）
        }

        var after = st.Ec.WriteLog.ToJson();
        w["log"] = after;
        // 采集期间**新增**的写尝试次数 = 累计值前后相减（含风扇曲线自己写的 ——
        // 所以别当成"探针写了这么多次"）。以前用 Snapshot().Count 差值，
        // 队列满 40 条后被同期风扇写入抵消，完整采集明明写了 10 条却报 0（2026-11-23 真机）。
        w["writes_added"] = st.Ec.WriteLog.Total - totalBefore;
        // 采集开始时队列里能看到的非风扇写入条数（上一次采集的写验证也在里面，
        // 不能当成"用户自己试的"）。只读包据此说明"采集之前发生了什么"。
        w["manual_writes_before"] = manualBefore;

        return new Outcome(w, backlightOffset, notes);
    }

    /// <summary>为什么不做写入验证（null = 可以跑）。每一种都必须是**具体**原因，别糊成"不可用"。</summary>
    private static string? SkipReason(AppState st, bool requested, int perfSub, int backlightSub)
    {
        // 只读这一档要说清"能做到哪一步"：它**足够**做读的适配（认机型 + 读数偏移），
        // 缺的只有写权限那半张纸 —— 而写是**模具级**的事，同模具只要一台肯试写就够。
        // 不写这句，用户会以为"没验写 = 白采了"。
        if (!requested)
            return "只读采集：没有向 EC 写任何东西（认机型 + 认出温度/转速的偏移；"
                   + "如果开了原厂走引导，档位 / 分区 / 槽位也能从原厂写帧里认出来）。"
                   + "「自己写一次再读回」要另采一次完整采集 —— 同模具任意一台愿意试写就够，不必每台都试。";
        if (!st.Ec.Spec.Has) return "机型档案里没有 EC 命令表，没有可写的命令。";
        if (perfSub <= 0 && backlightSub <= 0) return "机型档案没声明性能档/背光的子命令号。";

        var (ok, why) = st.Ec.Transport.Available();
        if (!ok) return "连不上 EC（" + (why ?? "原因未知") + "）。";

        // ⚠️ 非管理员下 WMI 的写**不报错也不生效**：放过去就是一次假成功，宁可不试
        if (!st.Ec.IsAdmin())
            return "控制中心不是管理员：非管理员下写命令会被 WMI 接受但不生效，"
                   + "按管理员身份重启控制中心后再采一次才有写证据。";

        return null;
    }

    private static JsonObject NotInProfile(string name, int sub)
        => new()
        {
            ["attempted"] = false,
            ["skip"] = sub > 0 ? $"{name} 没有可用的配合命令（需要 status 帧才能读回）" : $"机型档案没有声明 {name} 的命令号",
        };

    // ================================================================ 性能档

    /// <summary>
    /// 把配置里在用的那几个 APMD 值逐个写下去，再读回来比对，最后恢复原档。
    ///
    /// 这一步顺手把"三档 APMD 到底是哪三个值"这件事变成实测数据 ——
    /// 以前它只能靠作者看原厂抓包确认，是导入端唯一"绝不自动"的一条。
    /// 读回一致 ⇒ 这条写命令真生效且 APMD 不重映射；不一致 ⇒ 要么写不进去、
    /// 要么这台机器把 GWD0 又映射了一层（正是最需要知道的那种差异）。
    /// </summary>
    private static JsonObject PerfProbe(AppState st, ushort sub, List<string> notes)
    {
        var o = new JsonObject { ["attempted"] = true, ["cmd"] = CmdTable.Hex(sub) };

        uint original;
        try
        {
            original = st.Ec.ReadPerfApmd();
        }
        catch (Exception e)
        {
            // 没有原值 = 写完恢复不回去 ⇒ 一个字节都不写（宁可没证据，也不能把用户留在别的档位）
            o["attempted"] = false;
            o["skip"] = "读不到当前 APMD（" + e.Message + "）：没有原值就没法保证采完能恢复，所以没写。";
            notes.Add("性能档没验：当前 APMD 读不到，写了恢复不回去。");
            return o;
        }

        o["before"] = original;
        o["perf_key_before"] = st.Ec.PerfModeKey(original).Key;

        var candidates = EcWriteProbe.ApmdCandidates(ConfiguredApmd(st));
        o["candidates"] = new JsonArray(candidates.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());

        var steps = new JsonArray();
        var allOk = true;

        foreach (var c in candidates)
        {
            try
            {
                st.Ec.Arm(sub, ttl: 20);                 // 0x0300 属高危：每次写前单独授权
                st.Ec.SetPerfApmd((uint)c);
                Thread.Sleep(SettleMs);
                var got = st.Ec.ReadPerfApmd();
                var effective = got == (uint)c;
                allOk &= effective;
                steps.Add(new JsonObject
                {
                    ["wrote"] = c,
                    ["readback"] = got,
                    ["effective"] = effective,
                });
            }
            catch (Exception e)
            {
                allOk = false;
                steps.Add(new JsonObject { ["wrote"] = c, ["error"] = e.Message });
            }
        }

        o["steps"] = steps;
        o["effective"] = allOk && steps.Count > 0;

        // ---- 恢复原档（无论前面成没成，只要读过原值就一定写回）----
        try
        {
            st.Ec.Arm(sub, ttl: 20);
            st.Ec.SetPerfApmd(original);
            Thread.Sleep(SettleMs);
            var back = st.Ec.ReadPerfApmd();
            o["restored"] = back == original;
            o["readback_after_restore"] = back;
            o["perf_key_after"] = st.Ec.PerfModeKey(back).Key;
            if (back != original)
                notes.Add($"性能档没恢复成功：写回 {original}、读回 {back} —— 请手动切一次档位或重启控制中心。");
        }
        catch (Exception e)
        {
            o["restored"] = false;
            o["restore_error"] = e.Message;
            notes.Add("性能档恢复失败：" + e.Message + " —— 请手动切一次档位。");
        }

        return o;
    }

    /// <summary>配置里在用的 APMD 值（`presets.perf.modes[*].apmd`）。数字一律走 ToJsonString 解析。</summary>
    private static IEnumerable<int?> ConfiguredApmd(AppState st)
    {
        if (st.Model.Get("perf", "modes") is not JsonObject modes) yield break;
        foreach (var (_, v) in modes)
        {
            if (v is JsonObject o && o["apmd"] is JsonNode n && Num(n) is { } d) yield return (int)d;
        }
    }

    // ================================================================ 键盘背光

    /// <summary>
    /// 写两档背光（0 → 2），每档之后读一帧 status，用"低字节 == 写进去的档位"
    /// 在数据区的 u32 偏移里定位档位字段（先标准槽 @4~@20，没对上再看尾部 @24/@28），
    /// 最后把原档位写回去。
    ///
    /// 先写 0 再写 2 是**故意的**：万一中途崩了，灯停在最亮档而不是黑着
    /// （用户会以为键盘灯坏了）。原档位从**写之前那一帧**里取，不用问用户。
    /// </summary>
    private static BacklightResult BacklightProbe(AppState st, ushort backlightSub, ushort statusSub,
                                                  List<string> notes)
    {
        var o = new JsonObject
        {
            ["attempted"] = true,
            ["cmd"] = CmdTable.Hex(backlightSub),
            ["wrote"] = new JsonArray(LevelLow, LevelHigh),
        };

        var before = FrameMap(st, statusSub);
        if (before is null)
        {
            o["attempted"] = false;
            o["skip"] = "读不到 status 帧，没法读回背光档位，所以没写。";
            notes.Add("背光没验：status 帧读不到。");
            return new BacklightResult(o, null);
        }

        // 用配置里的**第一个分区地址**写，不用 `backlight.addr`：
        // 那个全局地址是界面留下的"最后调过分区"，可能是任何值（本机 2026-11-15 是 63，
        // 一个不在 3/4/5/6 里的数）。写验证只关心"档位字段在哪"，但写到一个真实分区上，
        // 结论才经得起复核 —— 否则报告里那句「0x0100 的写入落到 EC 了」说的可能是无效地址。
        var probeAddr = ProbeAddr(st);
        o["addr"] = probeAddr is { } pa ? pa : null;

        var afterLow = WriteLevel(st, backlightSub, LevelLow, statusSub, notes, probeAddr);
        var afterHigh = WriteLevel(st, backlightSub, LevelHigh, statusSub, notes, probeAddr);

        if (afterLow is null || afterHigh is null)
        {
            o["attempted"] = false;
            o["skip"] = "写入后读不到 status 帧，没法比对，所以没有结论。";
            notes.Add("背光没验：写完读不回 status。");
            RestoreLevel(st, backlightSub, statusSub, LevelHigh, null, notes, probeAddr);
            return new BacklightResult(o, null);
        }

        var scan = EcWriteProbe.ScanLevel(before, afterLow, LevelLow, afterHigh, LevelHigh);
        o["found_offset"] = scan.Offset is { } fo ? fo : null;
        o["found_cmd"] = scan.Offset is not null ? "status" : null;
        o["effective"] = scan.Offset is not null;
        o["why"] = scan.Why;
        if (scan.Matched.Count > 1)
            o["matched"] = new JsonArray(scan.Matched.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());

        // ⚠️ 逐帧原始数据留着：判错时（比如固件把档位塞在别的 u32 里）作者能自己重算，
        //    而不是只能相信我们那句 "effective"
        o["frames"] = new JsonObject
        {
            ["before"] = MapJson(before),
            ["level_low"] = MapJson(afterLow),
            ["level_high"] = MapJson(afterHigh),
        };

        // ---- 恢复 ----
        var original = scan.Offset is { } off && before.TryGetValue(off, out var bv)
                       && EcWriteProbe.LevelOf(bv) is { } lv
            ? lv
            : LevelHigh;                 // 认不出原档位就恢复成最亮：既不黑着，也肉眼可查

        var restored = RestoreLevel(st, backlightSub, statusSub, original, scan.Offset, notes, probeAddr);
        o["original"] = scan.Offset is not null ? original : null;
        o["restored"] = restored;
        o["restored_level"] = original;
        if (scan.Offset is null)
            o["restore_note"] = "没认出档位字段，已把背光恢复成最亮档（" + LevelHigh + "）；"
                                + "如果它原本是关着的，请手动调回来。";

        return new BacklightResult(o, scan.Offset);
    }

    /// <summary>
    /// 背光探针要写的 LED 地址 = 配置里的**第一个分区**。
    ///
    /// 为什么不用 `presets.backlight.addr`：那是"界面最后调过哪个分区"的落点，
    /// 可能是用户随手选的，也可能是某个请求留下的脏值 —— 2026-11-15 本机读到 **63**
    /// （不在 3/4/5/6 里）。写验证要证明的是"档位字段在哪个偏移"，写到一个**真实分区**上，
    /// 报告里那句「0x0100 的写入落到 EC 了」才站得住。
    /// </summary>
    private static int? ProbeAddr(AppState st)
    {
        if (st.Model.Get("backlight", "zones") is JsonArray zones)
        {
            foreach (var n in zones)
            {
                if (n is JsonObject o && EcSampling.Num(o["addr"]) is { } a && a is > 0 and <= 0xFF)
                    return (int)a;
            }
        }
        return null;      // 配置里没有分区 ⇒ 交给 EcHal 的默认值，行为与以前一致
    }

    private static Dictionary<int, uint>? WriteLevel(AppState st, ushort backlightSub, int level,
                                                     ushort statusSub, List<string> notes, int? addr = null)
    {
        try
        {
            st.Ec.SetBacklight(level, addr: addr);
        }
        catch (Exception e)
        {
            // 守卫/传输失败必须单独说：这时候"没读到差异"和"写根本没发出去"是两件事
            notes.Add($"背光写到 {level} 档失败：{e.Message}");
            return null;
        }
        Thread.Sleep(SettleMs);
        return FrameMap(st, statusSub);
    }

    /// <summary>把档位写回去，并读回确认（读回不了也算失败，但要分开说）。</summary>
    private static bool RestoreLevel(AppState st, ushort backlightSub, ushort statusSub,
                                     int level, int? offset, List<string> notes, int? addr = null)
    {
        try
        {
            st.Ec.SetBacklight(level, addr: addr);
            Thread.Sleep(SettleMs);
            if (offset is not { } off) return true;      // 没有偏移就没法读回，只能算"尽力恢复"
            var map = FrameMap(st, statusSub);
            if (map is null) return true;
            return map.TryGetValue(off, out var v) && EcWriteProbe.LooksLikeLevel(v, level);
        }
        catch (Exception e)
        {
            notes.Add("背光恢复失败：" + e.Message + " —— 请手动调一下键盘背光。");
            return false;
        }
    }

    /// <summary>status 帧 → 偏移:u32。读不到就返回 null（不编 0 —— 0 是合法读数）。</summary>
    private static Dictionary<int, uint>? FrameMap(AppState st, ushort statusSub)
    {
        if (statusSub == 0) return null;
        try
        {
            var raw = st.Ec.Raw(new EcCommand(statusSub, EcDirection.Read));
            if (raw["u32"] is not JsonObject u) return null;

            var map = new Dictionary<int, uint>();
            // 数据区全部 7 个 u32（@4..@28）：标准槽 + 尾部槽都要进来，
            // 否则 ScanLevel 的兜底一遍（别的模具把档位放在 @24/@28）没有数据可比。
            foreach (var off in EcWriteProbe.Slots.Concat(EcWriteProbe.TailSlots))
            {
                // DAT2@4 / DAT3@8 … DAT8@28：n = off/4 + 1
                var key = $"DAT{off / 4 + 1}@{off}";
                if (Num(u[key]) is { } d) map[off] = (uint)Math.Max(0, d);
            }
            return map.Count > 0 ? map : null;
        }
        catch
        {
            return null;
        }
    }

    private static JsonObject MapJson(Dictionary<int, uint> map)
    {
        var o = new JsonObject();
        foreach (var (off, v) in map.OrderBy(kv => kv.Key))
            o[off.ToString(CultureInfo.InvariantCulture)] = v;
        return o;
    }

    // ================================================================ 结论文本

    /// <summary>
    /// 一段**只描述现象**的结论。三处共用（包文本 / 界面提示 / 导入复核），
    /// 免得同一件事三种说法。不做"这台机器能不能适配"的判决 —— 那是作者的事。
    /// </summary>
    private static string Verdict(JsonObject perf, JsonObject backlight)
    {
        var sb = new StringBuilder();

        sb.Append("性能档：");
        if (!Bool(perf["attempted"])) sb.Append(Str(perf["skip"]) is { Length: > 0 } ps ? ps : "没验。");
        else
        {
            var steps = perf["steps"] as JsonArray ?? new JsonArray();
            var ok = steps.Count(s => Bool(s?["effective"]));
            var detail = string.Join(" / ", steps
                .Select(s => $"写 {Num(s?["wrote"])}→读 {Num(s?["readback"])?.ToString(CultureInfo.InvariantCulture) ?? "失败"}")
                .Take(6));
            sb.Append($"写入 {steps.Count} 次、读回一致 {ok} 次（{detail}）；");
            sb.Append(Bool(perf["restored"]) ? "已恢复原档。" : "**原档没恢复成功**，请看采集说明。");
        }

        sb.AppendLine();
        sb.Append("键盘背光：");
        if (!Bool(backlight["attempted"]))
            sb.Append(Str(backlight["skip"]) is { Length: > 0 } bs ? bs : "没验。");
        else if (Bool(backlight["effective"]))
        {
            var addr = Num(backlight["addr"]);
            sb.Append($"写入 {LevelLow}/{LevelHigh} 档"
                      + (addr is { } ad ? $"（分区 {ad:0}）" : "")
                      + $"后，status@{Num(backlight["found_offset"])} 读回一致 ⇒ ")
              .Append("0x0100 的写入落到 EC 了，档位就是这么存的；")
              .Append(Bool(backlight["restored"]) ? "已恢复原档。" : "**原档没恢复成功**。");
        }
        else
            sb.Append("写入有回帧，但没在 status 帧里对上档位 —— 可能写的是别的分区/选择子，也可能档位不在 status 里。");

        sb.AppendLine();
        sb.Append("风扇：未验证（采集只读，不写风扇；写入后交回固件要发 0x0206=0 + UFA=0xFF，"
                  + "见 EcHal.ReleaseToEc —— 所以采集不做这件事）。");

        return sb.ToString();
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>数字一律走 ToJsonString 再解析：`Bounds.TryToDouble` 对强类型节点会抛（见 AppState.Num）。</summary>
    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (n.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static bool Bool(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
