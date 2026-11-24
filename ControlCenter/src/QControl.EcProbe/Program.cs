using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Ec;
using QControl.Core.Model;
using QControl.Hal.Ec;
using QControl.Hal.Wmi;

namespace QControl.EcProbe;

/// <summary>
/// P3 的验收工具：**默认只读**地跑一遍 C# 的 EC 读写层，并可与运行中的实例逐字段对账。
///
/// 安全边界：
/// <list type="bullet">
/// <item>默认只发**读组 0xFA00** 上的 0x0200 / 0x0202 / 0x0203 / 0x0207 —— 全是只读命令；</item>
/// <item>写路径只有 `--selftest-write` 会触发，且只写一次背光档位、立刻读回、再还原。
///   **必须你本人执行**。</item>
/// </list>
/// 退出码 0 = 通过。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] argv)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        var doSelfTestWrite = argv.Contains("--selftest-write");
        var samples = int.TryParse(ArgValue(argv, "--samples"), out var n) ? Math.Clamp(n, 1, 20) : 5;
        var setFansArg = ArgValue(argv, "--set-fans");
        var setBacklightArg = ArgValue(argv, "--set-backlight");
        var testFans = argv.Contains("--test-fans");
        var trunkProbe = argv.Contains("--trunk-probe");

        // `write` 是启动器（p3-probe.cmd）用的"这是写操作"标记 ——
        // cmd 里剥离参数很麻烦（for 会按逗号拆分 100,100），所以干脆交给这里：
        //   `write` 单独出现      ⇒ 跑背光自检（最安全的写验证）
        //   `write --set-fans …`  ⇒ 只跑指定的那个，不再叠加自检
        if (argv.Contains("write", StringComparer.OrdinalIgnoreCase)
            && !doSelfTestWrite && setFansArg is null && setBacklightArg is null && !testFans)
        {
            doSelfTestWrite = true;
        }

        Line();
        Say("P3 · EC 读写层验证（默认只读）");
        Line();

        // ---------------------------------------------------------- 身份与档案
        Section("[1] 机型识别");
        var ident = MachineIdent.Collect();
        var dev = DeviceResolver.Resolve(ident);
        Say($"  识别结果   : {dev.Id}  ({dev.Name})");
        Say($"  已适配     : {(dev.Verified ? "是" : "否")}");
        Say($"  命中字段   : {(dev.MatchedBy.Count > 0 ? string.Join("、", dev.MatchedBy) : "无")}");
        Say($"  写许可     : {(dev.WritesAllowed ? "允许" : "禁止")}   ← 唯一的判定口");
        if (!dev.WritesAllowed) Say($"  拒绝原因   : {dev.DenyHint()}");
        Say($"  EC 命令表  : {(dev.Ec.Has ? $"{dev.Ec.Cmd.Count} 条命令" : "无")}");
        Say($"  读数布局   : {(dev.Ec.LayoutVerified ? "已标定" : "未标定（只回 raw）")}");
        Say($"  节流       : min_gap={dev.Ec.MinGap}s, 默认 gap={0.45}s");
        Say($"  背光整条   : " + (dev.Ec.KbTrunkAddr is { } trunkAddr
            ? $"addr={trunkAddr}（分区同色一条命令）"
            : "未标定（分区同色只能逐条写）"));

        if (!dev.Ec.Has)
        {
            Fail("档案里没有 EC 命令表 —— 这台机器不走 RW_GMWMI，后续跳过");
            return Summary(false);
        }

        // ---------------------------------------------------------- 连接
        Section("[2] 连接 RW_GMWMI");
        EcHal ec;
        string? modelPath = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var io = new GmWmi(dev.Ec);
            // 配置要真读：性能模式索引 → APMD 的映射就在 presets.perf.modes 里，
            // 没有它 SetPerfMode 只能回落到出厂默认（也能用，但热改的值看不到）。
            modelPath = FindModel();
            var model = modelPath is null ? null : new ModelStore(modelPath);
            ec = new EcHal(io, () => dev, model);
            sw.Stop();
            var (ok, detail) = io.Available();
            if (!ok)
            {
                Fail($"实例取不到：{detail}");
                return Summary(false);
            }
            Ok($"已连接（{sw.ElapsedMilliseconds} ms）");
            Say($"  配置文件   : {modelPath ?? "（没找到 model.json，用出厂默认）"}"
                + (model?.LoadError is { } le ? $"  ⚠️ 解析失败：{le}" : ""));
            var env = ec.EnvInfo();
            Say($"  管理员     : {(env["admin"]!.GetValue<bool>() ? "是" : "否")}");
            Say($"  可写 EC    : {(env["write_ok"]!.GetValue<bool>() ? "是" : "否")}");
        }
        catch (Exception ex)
        {
            Fail($"连接失败：{ex.GetType().Name}: {ex.Message}");
            return Summary(false);
        }

        using (ec)
        {
            // ------------------------------------------------------ 读状态
            Section($"[3] 读状态（读组 0x{dev.Ec.ReadGroup:X4} / 子命令 0x{dev.Ec.Sub("status"):X4}）");
            JsonObject? good = null;
            var okCount = 0;
            var failed = 0;
            Say("  （耗时 = 真实 WMI 往返，**不含**人为节流；含节流见右列）");
            for (var i = 1; i <= samples; i++)
            {
                var before = ec.Transport.Stats;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var st = ec.ReadStatus();
                    sw.Stop();
                    var after = ec.Transport.Stats;
                    var realMs = (after.Seconds - before.Seconds) * 1000.0 / Math.Max(1, after.Count - before.Count);

                    var raw = st["raw"]!.GetValue<string>();
                    if (!raw.StartsWith("00 FA", StringComparison.Ordinal))
                    {
                        Warn($"第 {i} 次：回显不符（{raw[..Math.Min(11, raw.Length)]}）→ 丢弃");
                        continue;
                    }
                    okCount++;
                    good = st;
                    Ok($"第 {i} 次 往返 {realMs,5:F1} ms  含节流 {sw.ElapsedMilliseconds,4} ms   {raw}");
                }
                catch (Exception ex)
                {
                    failed++;
                    sw.Stop();
                    Fail($"第 {i} 次：{ex.GetType().Name}: {ex.Message}");
                }
            }
            Say("");
            Say($"  有效 {okCount}/{samples}，失败 {failed}");

            if (good is null)
            {
                Fail("一次都没读到有效数据");
                return Summary(false);
            }

            Say("");
            foreach (var (k, _) in dev.Ec.Layout)
                if (good[k] is JsonNode v) Say($"  {k,-16} = {v.GetValue<uint>()}");

            // SYS 路（三风扇机型）：读数在另一条子命令的帧里，只有真的读到了才打印
            if (dev.Ec.SysSub >= 0 && dev.Ec.SysLayout.Count > 0)
            {
                Say($"  --- SYS 路（0x{dev.Ec.SysSub:X4} 帧，fans={dev.Ec.Fans}）---");
                foreach (var (k, _) in dev.Ec.SysLayout)
                    Say($"  {k,-16} = " + (good[k] is JsonNode sv
                        ? sv.GetValue<uint>().ToString()
                        : "（没读到：fans<3 或该命令超时，见 sys_error）"));
            }

            // ------------------------------------------------------ 其他只读命令
            Section("[4] 其他只读命令");
            TryRead("性能模式 APMD", () =>
            {
                var apmd = ec.ReadPerfApmd();
                var (key, v) = ec.PerfModeKey(apmd);
                return $"APMD={v}  模式={key ?? "（认不出）"}";
            });
            TryRead("显卡模式 GFXT", () =>
            {
                // 走读探针：与机型适配用的是同一条路（多留 DAT4 与原始帧，见 EcHal.ReadGfxProbe）
                var r = ec.ReadGfxProbe();
                return $"gfx_mode={r["gfx_mode"]!.GetValue<uint>()}  dat4={r["dat4"]!.GetValue<uint>()}  "
                       + "（1=混合 2=独显直连 3=集显）\n"
                       + $"      raw: {r["raw"]}";
            });
            TryRead("ENTP/AWMI", () =>
            {
                var r = ec.ReadPerfEntp();
                return $"entp={r["entp"]}  awmi={r["awmi"]}";
            });
            TryRead("VGAT/RPM5", () =>
            {
                var r = ec.ReadGpuRpm();
                return $"vgat={r["vgat"]}  rpm5={r["rpm5"]}";
            });
            TryRead("型号 SKU（0x0201）", () =>
            {
                var sku = ec.ProbeSkuOnce();
                if (sku is null) return "读不到（档案没声明 ec.cmd.sku，或固件不认这条命令）";
                var map = dev.Ec.FansBySku.Count == 0
                    ? "（档案没写 fans_by_sku，不自动判定路数）"
                    : $"（SKU 表已命中规则：本机判为 {dev.Ec.Fans} 路）";
                return $"sku=0x{sku.Value:X2}（{sku.Value}）{map}";
            });
            TryRead("风扇控制权（0x0206 a2）", () =>
            {
                var r = ec.ReadFanControlFlag();
                return $"flag={r["flag"]}（0=BIOS 自控 / 1=已被软件接管）\n"
                       + $"      raw: {r["raw"]}";
            });

            // 0x0202：硬件能力位（高刷 / 相机 / 独显直连）。**只打原始帧，不解释。**
            //
            // 为什么不解释：外部独立实现（control-center-lite，同 NL5 模具的另一台机器）判
            // `a4==2` = 高刷、`a6==2` = 独显直连；本机（SKU 56）读回来 a4=1 / a5=2 / a6=1，
            // 而 0x0203 明确是 2（正在独显直连）。同一份逆向结论在两台机器上对不上 ⇒
            // 这三位不是全模具共用的，谁的映射都不算证据。**能力判断继续只信 0x0203。**
            JsonObject? cap0202 = null;
            TryRead("能力位 0x0202", () =>
            {
                var f = new EcFrame(RawRead(ec, dev.Ec.ReadGroup, 0x0202));
                cap0202 = new JsonObject
                {
                    ["raw"] = f.Hex,
                    ["a2"] = f.U32(EcFrame.Dat2),
                    ["a3"] = f.U32(EcFrame.Dat3),
                    ["a4"] = f.U32(EcFrame.Dat4),
                    ["a5"] = f.U32(EcFrame.Dat5),
                    ["a6"] = f.U32(EcFrame.Dat6),
                };
                return $"a2={f.U32(EcFrame.Dat2)} a3={f.U32(EcFrame.Dat3)} a4={f.U32(EcFrame.Dat4)} "
                       + $"a5={f.U32(EcFrame.Dat5)} a6={f.U32(EcFrame.Dat6)}（未解释，见代码注释）\n"
                       + $"      raw: {f.Hex}";
            });

            // ------------------------------------------------------ 模具指纹
            //
            // 目的只有一个：**换一台机器跑同一条命令，两行 diff 就是"模具是否同一套读写"的答案。**
            // 不装第二台机器也能先用本机留档（已知：本机 SKU 56 / 两风扇 / a3=0）。
            //
            // 三项刻意**绕过机型档案直接读**，因为它们是"这个模具有没有这一路"的物理证据：
            //   · 0x0207 的 a3（SYS 转速）—— 档案只在 `fans>=3` 时才读它，两风扇机永远看不到；
            //     直读才能回答"SKU 命中 fans_by_sku 的机器到底有没有第三个扇"。
            //   · 0x0202 / 0x0203 —— 能力位与显示模式，跨模具最可能不同的两条。
            Section("[4b] 模具指纹（换机器跑同一条命令，diff 两行即可）");
            JsonObject? fp0207 = null;
            if (dev.Ec.Sub("sys_info") >= 0)
            {
                try
                {
                    var f = new EcFrame(RawRead(ec, dev.Ec.ReadGroup, (ushort)dev.Ec.Sub("sys_info")));
                    fp0207 = new JsonObject
                    {
                        ["raw"] = f.Hex,
                        ["a2"] = f.U32(EcFrame.Dat2),
                        ["a3"] = f.U32(EcFrame.Dat3),
                    };
                }
                catch (Exception e)
                {
                    Warn($"0x0207 直读失败（{e.GetType().Name}: {Trim(e.Message)}）");
                }
            }
            JsonObject? fp0203 = null;
            try
            {
                var f = new EcFrame(RawRead(ec, dev.Ec.ReadGroup, (ushort)dev.Ec.Sub("gfx")));
                fp0203 = new JsonObject { ["raw"] = f.Hex, ["a2"] = f.U32(EcFrame.Dat2), ["a3"] = f.U32(EcFrame.Dat3) };
            }
            catch (Exception e)
            {
                Warn($"0x0203 直读失败（{e.GetType().Name}: {Trim(e.Message)}）");
            }

            var layoutObj = new JsonObject();
            foreach (var (k, slot) in dev.Ec.Layout)
                layoutObj[k] = $"{slot.Offset},{slot.Length}";
            var skuObj = new JsonObject();
            foreach (var (k, v) in dev.Ec.FansBySku.OrderBy(x => x.Key))
                skuObj[$"0x{k:X2}"] = v;
            var cmdObj = new JsonObject();
            foreach (var (k, v) in dev.Ec.Cmd.OrderBy(x => x.Key))
                cmdObj[k] = $"0x{v:X4}";

            var fp = new JsonObject
            {
                ["device"] = dev.Id,
                ["sku"] = ec.Sku is { } skuVal ? $"0x{skuVal:X2}" : null,
                ["fans"] = dev.Ec.Fans,
                ["fans_by_sku"] = skuObj,
                ["layout"] = layoutObj,
                ["cmd"] = cmdObj,
                ["status"] = good["raw"]!.GetValue<string>(),
                ["sys0207"] = fp0207,
                ["cap0202"] = cap0202,
                ["gfx0203"] = fp0203,
            };
            Say("  " + fp.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

            // ------------------------------------------------------ 与运行中实例对账
            Section("[5] 与运行中的控制中心对账（EC 块）");
            JsonObject? live = await FetchLiveEc();
            var matched = live is not null;
            if (live is null)
            {
                Warn("控制中心没在跑（8765），跳过对账 —— 不算失败");
            }
            else
            {
                Say($"  实例 raw : {live["raw"]}");
                Say("");
                var mismatched = 0;
                foreach (var (k, _) in dev.Ec.Layout)
                {
                    if (!live.TryGetPropertyValue(k, out var pv) || pv is null) continue;
                    var mine = good[k]!.GetValue<uint>();
                    var theirs = pv.GetValue<uint>();
                    var tol = k.Contains("rpm", StringComparison.Ordinal) ? 400L : 4L;
                    var same = Math.Abs((long)mine - (long)theirs) <= tol;
                    if (!same) mismatched++;
                    Say($"  {k,-16} 本进程 {mine,-7} 实例 {theirs,-7} "
                        + (same ? "一致" : $"差 {mine - theirs}"));
                }
                matched = mismatched == 0;
                if (matched) Ok("两个独立读路径给出的值在容差内一致");
                else Warn($"{mismatched} 项超出容差（温度 ±4 / 转速 ±400）—— 见上表");
            }

            // ------------------------------------------------------ 守卫（不写）
            Section("[6] 写守卫（只验证拦截，不发写命令）");
            var denials = 0;
            void ExpectDeny(string label, Func<JsonObject> action)
            {
                try
                {
                    action();
                    Warn($"{label}：未被拦截 ← 守卫有问题");
                }
                catch (WriteDeniedException e)
                {
                    denials++;
                    Ok($"{label}：已拦截 — {Trim(e.Message)}");
                }
                catch (Exception e)
                {
                    Warn($"{label}：抛了别的异常 {e.GetType().Name} — {Trim(e.Message)}");
                }
            }

            // 高危命令在没 arm 时必须被拒（这条不会真写到 EC：守卫在任何 WMI 之前就抛）
            ExpectDeny("set_perf_mode(1) 未 arm", () => ec.SetPerfMode(1));
            ExpectDeny("set_gfx_mode(2) 未 arm", () => ec.SetGfxMode(2));

            // 阀门关掉后，普通写也必须被拒
            var wasWrites = ec.WritesEnabled;
            ec.EnableWrites(false);
            ExpectDeny("set_fans 阀门关闭", () => ec.SetFans(40, 40, 40));
            ec.EnableWrites(wasWrites);

            // ------------------------------------------------------ 可选：写自检
            if (testFans)
            {
                Section("[7] 手动风扇写入验证（--test-fans 双向）");
                RunFanRoundtrip(ec);
            }
            else if (setFansArg is not null)
            {
                Section($"[7] 手动风扇设定（--set-fans {setFansArg}）");
                RunSetFans(ec, setFansArg);
            }
            else if (trunkProbe)
            {
                RunTrunkProbe(ec, modelPath, ArgValue(argv, "--trunk-addrs"));
            }
            else if (setBacklightArg is not null)
            {
                Section($"[7] 背光写入验证（--set-backlight {setBacklightArg}）");
                RunSetBacklight(ec, setBacklightArg);
            }
            else if (doSelfTestWrite)
            {
                Section("[7] 写入自检（--selftest-write，会真的动一次背光）");
                try
                {
                    var r = ec.SelfTestWrite(restore: true);
                    Say("  " + r.ToJsonString(new System.Text.Json.JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }));
                }
                catch (Exception e)
                {
                    Fail($"{e.GetType().Name}: {e.Message}");
                }
            }
            else
            {
                Section("[7] 写入验证");
                Say("  跳过（未传 --selftest-write / --set-fans / --set-backlight）。");
                Say("  EC 写入验证必须由你本人执行 —— 见 src/README.md 的确认清单。");
            }

            // ------------------------------------------------------ HWiNFO 共享内存
            var hwinfoOk = true;
            if (argv.Contains("--hwinfo"))
            {
                Section("[9] HWiNFO 共享内存（只有它能给的那几项）");
                hwinfoOk = CheckHwInfo();
            }

            // ------------------------------------------------------ 传感器对账
            var sensorsOk = true;
            if (argv.Contains("--sensors"))
            {
                Section("[8] 传感器与运行中实例对账");
                sensorsOk = await CompareSensors();
            }

            // ------------------------------------------------------ 结论
            var pass = okCount > 0 && denials >= 3 && (live is null || matched) && sensorsOk && hwinfoOk;
            return Summary(pass, okCount, samples, denials);
        }
    }

    // ================================================================ 工具

    /// <summary>
    /// 绕过机型档案直接读一条子命令，拿原始 32 字节应答。
    ///
    /// <paramref name="verifyEcho"/> 故意为 false：`0x0202` 本机实测**不回显** `0xFA00/子命令`
    /// 头（回的是 `14 01 02 02 …`），开回显校验会直接抛。指纹只用来跨机器对拍，
    /// 不需要那层「这条回包是不是我的」的保护（对拍时看到头不一样本身就是信息）。
    /// </summary>
    private static byte[] RawRead(EcHal ec, ushort group, ushort sub)
    {
        var req = EcFrame.Build(group, EcCommand.Read(sub)).ToArray();
        return ec.Transport.Exchange(req, group, sub, verifyEcho: false);
    }

    private static void TryRead(string label, Func<string> action)
    {
        try
        {
            Ok($"{label,-14} {action()}");
        }
        catch (Exception e)
        {
            Warn($"{label,-14} {e.GetType().Name}: {Trim(e.Message)}");
        }
    }

    private static async Task<JsonObject?> FetchLiveEc()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var text = await http.GetStringAsync("http://127.0.0.1:8765/api/status");
            return JsonNode.Parse(text)?["ec"] as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string Trim(string s) => s.Length <= 90 ? s : s[..90] + "…";

    /// <summary>
    /// 读 HWiNFO 共享内存。这一段是 P5 的验收：段能不能打开、布局挑对没有、
    /// 数据新不新鲜、三项只有 HWiNFO 能给的读数取到没有。
    /// </summary>
    private static bool CheckHwInfo()
    {
        var exists = QControl.Hal.HwInfo.SharedMemoryFrame.SegmentExists();
        Say($"  段存在     : {(exists ? "是" : "否（HWiNFO 没起，或没开共享内存导出）")}");
        if (!exists) return false;

        var frame = QControl.Hal.HwInfo.SharedMemoryFrame.Read(requireFresh: false);
        if (frame is null)
        {
            Fail("段打得开但解析失败 —— 布局对不上");
            return false;
        }

        var age = QControl.Hal.HwInfo.SharedMemoryFrame.AgeSeconds(frame);
        var fresh = QControl.Hal.HwInfo.SharedMemoryFrame.IsFresh(frame);

        Say($"  布局       : {frame.Layout}（声明 sensor {frame.SensorElemSize}B / reading {frame.ReadingElemSize}B）");
        Say($"  版本       : v{frame.Version} rev {frame.Revision}");
        Say($"  元素       : {frame.Sensors.Count} 个传感器组 / {frame.Readings.Count} 条读数");
        Say($"  poll 年龄  : {age:F1} s   {(fresh ? "新鲜" : "⚠️ 冻结的残留段")}");

        if (!fresh)
        {
            Warn("数据不新鲜 —— 下面是冻结快照，不能当真数据用");
        }

        var reader = new QControl.Hal.HwInfo.HwInfoReader(
            () => QControl.Hal.HwInfo.SharedMemoryFrame.Read(requireFresh: false));

        Say("");
        void Show(string label, (double? Value, string? Source) r, string unit)
        {
            if (r.Value is null) Warn($"{label,-14} 取不到");
            else Ok($"{label,-14} {r.Value.Value,8:F2} {unit,-3}  ← {r.Source}");
        }

        Show("PCH 温度", reader.FindPch(), "°C");
        Show("CPU 电压", reader.FindCpuVoltage(), "V");
        Show("CPU 功耗", reader.FindCpuPower(), "W");

        var (limits, lsrc) = reader.FindCpuPowerLimits();
        if (limits is null) Warn("PL1/PL2        取不到");
        else Ok($"PL1/PL2        pl1={limits.GetValueOrDefault("pl1"):F0}W pl2={limits.GetValueOrDefault("pl2"):F0}W  ← {lsrc}");

        Show("显存结温", reader.FindGpuMemTemp(), "°C");
        Show("GPU 电压", reader.FindGpuVoltage(), "V");
        Show("电池功率", reader.FindBattRate(), "W");

        var (vmem, vsrc) = reader.FindVmem();
        if (vmem is null) Warn("虚拟内存        取不到");
        else Ok($"虚拟内存        {vmem["percent"]}% / {vmem["used_gb"]} GB  ← {vsrc}");

        var smart = reader.FindDriveSmart();
        if (smart.Count == 0) Warn("硬盘 SMART      取不到");
        else foreach (var rec in smart)
            Ok($"SMART {rec.Model}{(rec.Sid is { } sid ? " (" + sid + ")" : "")}: "
               + $"temp={rec.Temp} life={rec.Life} spare={rec.Spare} "
               + $"writes={rec.WritesGb}GB reads={rec.ReadsGb}GB "
               + $"vols=[{string.Join(",", rec.Volumes ?? [])}]");

        Say("");
        Say("  前 8 条温度：");
        foreach (var (g, l, v) in reader.ListTemps(8))
            Say($"    {v,7:F1}°C  {g} / {l}");

        return fresh;
    }

    /// <summary>
    /// 传感器逐字段与运行中的实例对账。
    ///
    /// 两个必须做的处理：
    /// 1. **第一次读必然是 0 的字段**要预热：`cpu.usage` 与 `network.rate` 都是
    ///    "两次调用之间的差分"，没有前一次就只能是 0；RAPL 还要攒到第三次才有值。
    /// 2. **配对采样**：实例的 `/api/status` 和我这边不是同一瞬间读的。
    ///    GPU 频率在 500~2000 MHz 之间跳变，差 1 秒就能差 4 倍。
    ///    所以跑 3 轮「先取实例、立刻读本地」，每个字段取**最小差值** ——
    ///    最小差都超出容差，才是真的不一致。
    /// </summary>
    private static async Task<bool> CompareSensors()
    {
        using var snap = new QControl.Hal.Sensors.StatusSnapshot();

        // 预热：第一次读铺底，等 1.1 秒让 CPU 占用率 / 网络速率有差分
        _ = snap.Read();
        await Task.Delay(1100);
        _ = snap.Read();          // RAPL 第三次才有有效值
        await Task.Delay(150);

        Say($"  CPU 功耗来源：{snap.CpuPowerSource ?? "（RAPL 不可用：" + (snap.CpuPowerWhy ?? "?") + "）"}");

        var fields = new (string Path, double Tol, string Unit)[]
        {
            ("cpu.usage",        10, "%"),
            ("cpu.power",        12, "W"),
            // 频率本身在 1466/2200 两档间跳，两个进程不可能同时刻读到同一个值；
            // 容差按"相邻两档"给，否则每次都是假告警
            ("cpu.freq_mhz",    800, "MHz"),
            ("cpu.cores",         0, ""),
            ("cpu.threads",       0, ""),
            ("memory.percent",    3, "%"),
            ("battery.percent",   0, "%"),
            ("battery.voltage_v", 0.3, "V"),
            ("battery.remain_wh", 1.0, "Wh"),
            ("battery.full_wh",   0.5, "Wh"),
            ("system.uptime_s",   8, "s"),
            ("gpu.temp",          6, "°C"),
            ("gpu.usage",        15, "%"),
            ("gpu.power",        35, "W"),
            ("gpu.clock",       900, "MHz"),
            ("gpu.mem_used",    300, "MB"),
        };

        var best = new Dictionary<string, (double Diff, double Mine, double Live)>(StringComparer.Ordinal);
        var rounds = 0;

        for (var r = 0; r < 3; r++)
        {
            var live = await FetchLiveStatus();
            if (live is null)
            {
                Warn("控制中心没在跑（8765），跳过对账");
                return true;
            }
            var mine = snap.Read();
            rounds++;

            foreach (var (path, _, _) in fields)
            {
                var a = Dig(mine, path);
                var b = Dig(live, path);
                if (a is null || b is null) continue;
                var diff = Math.Abs(a.Value - b.Value);
                if (!best.TryGetValue(path, out var cur) || diff < cur.Diff)
                    best[path] = (diff, a.Value, b.Value);
            }
            await Task.Delay(250);
        }

        Say($"  （{rounds} 轮配对采样，每个字段取最小差值）");
        Say("");
        Say($"  {"字段",-22}{"本进程",-12}{"实例",-12}判定");

        var failures = 0;
        foreach (var (path, tol, unit) in fields)
        {
            if (!best.TryGetValue(path, out var b))
            {
                Warn($"{path,-22}两边都取不到");
                continue;
            }
            var ok = b.Diff <= tol;
            if (!ok) failures++;
            Say($"  {path,-22}{b.Mine,-12:F1}{b.Live,-12:F1}"
                + (ok ? "✓" : $"✗ 最小差 {b.Diff:F1}{unit}（容差 {tol:F1}）"));
        }

        // 字符串 / 布尔单独比
        var liveLast = await FetchLiveStatus();
        if (liveLast is not null)
        {
            // 局部函数会捕获这个变量，编译器不会把外层的 null 检查带进去 ——
            // 先落一个非空别名，省掉 CS8602 噪音
            var liveObj = liveLast!;
            var mineLast = snap.Read();
            void CmpText(string path)
            {
                var a = DigText(mineLast, path);
                var b = DigText(liveObj, path);
                var ok = a == b;
                if (!ok) failures++;
                Say($"  {path,-22}{Short(a),-24}{Short(b),-24}" + (ok ? "✓" : "✗"));
            }
            CmpText("cpu.name");
            CmpText("gpu.name");
            CmpText("battery.present");
            CmpText("battery.plugged");
            CmpText("display.ok");
            CmpText("display.igpu");
            CmpText("display.has_dgpu");
            CmpText("display.dgpu");

            failures += CompareDisks(mineLast, liveObj);
        }

        Say("");
        if (failures == 0) Ok("全部字段在容差内一致");
        else Warn($"{failures} 项超出容差 —— 见上表");
        return failures == 0;
    }

    /// <summary>按点分路径取数值（只支持数字节点）。</summary>
    private static double? Dig(JsonObject root, string path)
    {
        JsonNode? cur = root;
        foreach (var k in path.Split('.'))
        {
            if (cur is not JsonObject o || !o.TryGetPropertyValue(k, out var next)) return null;
            cur = next;
        }
        if (cur is null) return null;
        return double.TryParse(cur.ToJsonString(), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? d : null;
    }

    /// <summary>显示用截断：型号名动辄 40 字符，会把表格撑歪。</summary>
    private static string Short(string? s)
        => s is null ? "null" : (s.Length <= 22 ? s : s[..22] + "…");

    /// <summary>
    /// 硬盘档案对账。只比**身份字段**：`temp/life/spare/writes_gb/reads_gb` 来自 HWiNFO，
    /// C# 侧要到 P5 才有，这里显式说明而不是假装一致。
    /// </summary>
    private static int CompareDisks(JsonObject mine, JsonObject py)
    {
        var a = mine["disk_info"] as JsonArray ?? [];
        var b = py["disk_info"] as JsonArray ?? [];

        Say("");
        Say($"  {"disk_info",-22}{$"{a.Count} 块",-12}{$"{b.Count} 块",-12}"
            + (a.Count == b.Count ? "✓" : "✗ 数量不同"));

        var failures = a.Count == b.Count ? 0 : 1;
        var smartOnly = new[] { "temp", "life", "spare", "writes_gb", "reads_gb" };

        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            if (a[i] is not JsonObject x || b[i] is not JsonObject y) continue;

            foreach (var key in (string[])["index", "label", "model", "bus", "media",
                                           "size_gb", "health", "firmware", "serial"])
            {
                var va = x[key]?.ToJsonString() ?? "null";
                var vb = y[key]?.ToJsonString() ?? "null";
                if (va == vb) continue;
                failures++;
                Say($"  disk[{i}].{key,-12}{Short(va),-24}{Short(vb),-24}✗");
            }

            // 卷列表按集合比（顺序不该有意义）
            var sa = x["volumes"] is JsonArray va1
                ? string.Join(",", va1.Select(v => v?.GetValue<string>() ?? "").OrderBy(x => x, StringComparer.Ordinal))
                : "";
            var sb = y["volumes"] is JsonArray vb1
                ? string.Join(",", vb1.Select(v => v?.GetValue<string>() ?? "").OrderBy(x => x, StringComparer.Ordinal))
                : "";
            if (sa != sb)
            {
                failures++;
                Say($"  disk[{i}].volumes      {Short(sa),-24}{Short(sb),-24}✗");
            }

            var mineSmart = smartOnly.Where(k => x[k] is not null).ToList();
            var pySmart = smartOnly.Where(k => y[k] is not null).ToList();
            if (mineSmart.Count == 0 && pySmart.Count > 0)
                Say($"  disk[{i}] SMART        C# 缺 {string.Join("/", pySmart)}（P5 接 HWiNFO 才有）");
        }
        return failures;
    }

    private static string? DigText(JsonObject root, string path)
    {
        JsonNode? cur = root;
        foreach (var k in path.Split('.'))
        {
            if (cur is not JsonObject o || !o.TryGetPropertyValue(k, out var next)) return null;
            cur = next;
        }
        if (cur is null) return null;
        return cur.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? cur.GetValue<string>()
            : cur.ToJsonString();
    }

    private static async Task<JsonObject?> FetchLiveStatus()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var text = await http.GetStringAsync("http://127.0.0.1:8765/api/status");
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 写风扇并观察转速变化。**只由用户执行**（需要管理员）。
    ///
    /// 判定不是"命令没报错"，而是"转速真的变了"：
    /// 写之前记一次 RPM，写完之后等 3 秒再读一次，看有没有朝目标方向动。
    /// </summary>
    private static void RunSetFans(EcHal ec, string arg)
    {
        var parts = arg.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Length > 3)
        {
            Fail("格式：--set-fans cpu,gpu[,sys]，例如 --set-fans 45,45,45");
            return;
        }

        var duty = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var v))
            {
                Fail($"第 {i + 1} 个值不是整数：{parts[i]}");
                return;
            }
            duty[i] = v;
        }

        try
        {
            var before = ec.ReadStatus();
            Say($"  写前：fan1={before["fan1_rpm"]}  fan2={before["fan2_rpm"]}");

            var r = ec.SetFans(duty.Length > 0 ? duty[0] : null,
                               duty.Length > 1 ? duty[1] : null,
                               duty.Length > 2 ? duty[2] : null);
            Ok($"已下发：fan1={r["fan1"]} fan2={r["fan2"]} fan3={r["fan3"]}");

            Say("  等 3 秒让 EC 生效…");
            Thread.Sleep(3000);

            var after = ec.ReadStatus();
            Say($"  写后：fan1={after["fan1_rpm"]}  fan2={after["fan2_rpm"]}");

            var d1 = (long)after["fan1_rpm"]!.GetValue<uint>() - before["fan1_rpm"]!.GetValue<uint>();
            var d2 = (long)after["fan2_rpm"]!.GetValue<uint>() - before["fan2_rpm"]!.GetValue<uint>();
            Ok($"变化：fan1 {d1:+#;-#;0} RPM，fan2 {d2:+#;-#;0} RPM");

            var target = (uint)r["fan1"]!.GetValue<int>();
            if (target >= 90 && d1 < 50 && d2 < 50)
                Warn("转速几乎没动 —— 目标可能是当前值附近，也可能写入未生效。换个明显不同的值再测。");
            else
                Ok("转速确有变化 ⇒ 写入生效");

            Say("");
            Say("  ⚠️ EC 拿到非零 UFA 后会一直照这个值转，不会自动接管。");
            Say("     恢复自动：重启，或让风扇曲线接管（fancurve.set_mode(\"auto\")）。");
        }
        catch (WriteDeniedException e)
        {
            Fail($"被守卫拦下：{e.Message}");
        }
        catch (Exception e)
        {
            Fail($"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 风扇写入的**双向**验证：先写低（30%）、再写高（100%），最后写回中间值。
    ///
    /// 为什么必须双向：单写 100% 是测不出来的 —— 如果风扇本来就在满转
    /// （比如上一次 `--set-fans 100` 留下的，EC 不会自动接管），目标值等于当前值，
    /// 转速纹丝不动，看起来像"写入失败"，其实写成功了但无事发生。
    /// 2026-10-19 实测就是这么误判的（写前 5002 → 写后 4979）。
    /// </summary>
    private static void RunFanRoundtrip(EcHal ec)
    {
        static int Rpm(JsonObject st)
            => (int)(st["fan1_rpm"]?.GetValue<uint>() ?? 0) + (int)(st["fan2_rpm"]?.GetValue<uint>() ?? 0);

        try
        {
            var s0 = ec.ReadStatus();
            var r0 = Rpm(s0);
            Say($"  初始：fan1={s0["fan1_rpm"]} fan2={s0["fan2_rpm"]}  合计 {r0} RPM");

            // ① 写低：期望转速明显下降
            var w1 = ec.SetFans(30, 30, 30);
            Say($"  写低：fan1={w1["fan1"]} fan2={w1["fan2"]}  → 等 4 秒…");
            Thread.Sleep(4000);
            var s1 = ec.ReadStatus();
            var r1 = Rpm(s1);
            Say($"        fan1={s1["fan1_rpm"]} fan2={s1["fan2_rpm"]}  合计 {r1} RPM  变化 {r1 - r0:+#;-#;0}");

            // ② 写高：期望转速明显上升
            var w2 = ec.SetFans(100, 100, 100);
            Say($"  写高：fan1={w2["fan1"]} fan2={w2["fan2"]}  → 等 4 秒…");
            Thread.Sleep(4000);
            var s2 = ec.ReadStatus();
            var r2 = Rpm(s2);
            Say($"        fan1={s2["fan1_rpm"]} fan2={s2["fan2_rpm"]}  合计 {r2} RPM  变化 {r2 - r1:+#;-#;0}");

            // ③ 写回中间值，别把风扇留在满转上
            ec.SetFans(45, 45, 45);
            Say("  已写回 45%（不留在满转）。注意：EC 会一直按这个值转，");
            Say("  要交回自动曲线请运行 ..\\..\\run-as-admin.cmd。");

            var down = r0 - r1;      // 写低带来的下降
            var up = r2 - r1;        // 写高带来的上升
            Say("");
            if (down > 200 || up > 200)
            {
                Ok($"写入生效：写低降了 {down} RPM，写高升了 {up} RPM");
            }
            else
            {
                Fail($"两个方向都没动（降 {down} / 升 {up} RPM）—— 写入未生效，"
                     + "可能是管理员权限不足，或 EC 拒绝了这条命令");
            }
        }
        catch (WriteDeniedException e)
        {
            Fail($"被守卫拦下：{e.Message}");
        }
        catch (Exception e)
        {
            Fail($"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 整条灯带地址探测（`--trunk-probe`）：逐个候选地址点亮白色，**由人看键盘**回答。
    ///
    /// 为什么必须人看：EC 只接受写入、不回报"哪个分区现在是什么颜色"，
    /// 所以"写 addr=6 之后三个区是不是同时变白"这件事没有任何读回手段能证实。
    /// 2026-11-15 作者就是这么手工标的（跑 `recon/led_all_probe.py 6,7,8,0x38 5` 看灯）；
    /// 这个子命令只是把它变成"不用装 Python 也能重来一遍"。
    ///
    /// 标定值写进 `presets.backlight.trunk_addr`（配置优先于档案，见 EcHal.TrunkAddr）：
    /// 同一台机器立刻从"三条连发"切成"一条命令"，不用等新档案。
    /// </summary>
    private static void RunTrunkProbe(EcHal ec, string? modelPath, string? addrsArg)
    {
        var addrs = (addrsArg ?? "6,7,8,56")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x.Trim(), out var v) ? v : -1)
            .Where(v => v is > 0 and <= 0xFF)
            .ToList();
        if (addrs.Count == 0) addrs = [6, 7, 8, 56];

        Section("[trunk] 整条灯带地址探测");
        if (!ec.EnvInfo()["write_ok"]!.GetValue<bool>())
        {
            Fail("当前不允许写 EC（未适配机型先在设置页打开「EC 控制」）。这一项必须写真字节，没有只读版。");
            return;
        }

        int level;
        try { level = (int)(ec.ReadStatus()["backlight_level"]?.GetValue<uint>() ?? 2u); }
        catch { level = 2; }

        Say($"  当前档位 : {level}");
        Say("  候选地址 : " + string.Join(", ", addrs));
        Say("  每个地址会点亮白色，**请看键盘**再回答；回车 = 不是它，q = 退出。");
        Say("  ⚠️ 这一轮会改掉键盘当前颜色，结束后请在界面里点一次预设恢复。");

        int? found = null;
        foreach (var addr in addrs)
        {
            try
            {
                ec.SetBacklight(level, 255, 255, 255, addr);
            }
            catch (Exception e)
            {
                Warn($"addr={addr} 写入失败：{e.Message}");
                continue;
            }

            Say("");
            Say($"  已向 addr={addr} 写白色。三个分区是否**同时**变白？");
            Console.Write("  [y/N/q] ");
            var ans = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (ans == "q") break;
            if (ans is "y" or "yes")
            {
                found = addr;
                break;
            }
        }

        if (found is null)
        {
            Warn("没有确认到整条地址 —— 这台机器的背光只能逐区写（行为与标定前一致，不会错）。");
            return;
        }

        Ok($"整条地址 = {found}");
        try
        {
            ec.SetBacklightZones(level, [(found.Value, new[] { 255, 255, 255 })]);
        }
        catch { /* 恢复失败不影响结论 */ }

        if (modelPath is null)
        {
            Say("  没找到 model.json，请手工把下面这行加进 presets.backlight：");
            Say($"    \"trunk_addr\": {found}");
            return;
        }

        var model = new ModelStore(modelPath);
        var r = model.Set(["backlight", "trunk_addr"], JsonValue.Create(found.Value));
        if (!r.Ok) { Fail("写不进配置：" + r.Error); return; }
        model.Save();
        Ok($"已写入 {modelPath} 的 presets.backlight.trunk_addr = {found}"
           + " —— 同一颜色的三区从下一次下发起就是**一条命令**。");
    }

    /// <summary>写背光并读回验证。</summary>
    private static void RunSetBacklight(EcHal ec, string arg)
    {
        if (!int.TryParse(arg, out var level))
        {
            Fail("格式：--set-backlight 0|1|2");
            return;
        }
        try
        {
            var before = ec.ReadStatus();
            Say($"  写前：backlight_level={before["backlight_level"]}");

            var r = ec.SetBacklight(level);
            Ok($"已下发：level={r["level"]} addr={r["addr"]} dt3d={r["dt3d"]}");

            Thread.Sleep(800);
            var after = ec.ReadStatus();
            Say($"  写后：backlight_level={after["backlight_level"]}");

            var wrote = r["level"]!.GetValue<int>();
            var readback = (int)after["backlight_level"]!.GetValue<uint>();
            if (readback == wrote) Ok("读回一致 ⇒ 写入生效");
            else Warn($"读回 {readback} ≠ 写入 {wrote} —— 命令已下发但 EC 未接受");
        }
        catch (WriteDeniedException e)
        {
            Fail($"被守卫拦下：{e.Message}");
        }
        catch (Exception e)
        {
            Fail($"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>向上找 ControlCenter/model.json（或当前目录的 model.json）。找不到就返回 null。</summary>
    private static string? FindModel()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "model.json");
                if (File.Exists(candidate)) return candidate;
                var nested = Path.Combine(dir.FullName, "ControlCenter", "model.json");
                if (File.Exists(nested)) return nested;
            }
        }
        return null;
    }

    private static int Summary(bool pass, int okCount = 0, int samples = 0, int denials = 0)
    {
        Line();
        if (pass)
        {
            Say("结论");
            Line();
            Say($"  读通路     : 有效 {okCount}/{samples} 次");
            Say($"  写守卫     : {denials}/3 道拦截生效");
            Say("  => 通过：C# 的 EC 读写层可用");
            Line();
            Say("  ⚠️ 写操作（风扇/背光/性能/显卡模式）尚未真机验证 —— 见 README 的确认清单。");
            Line();
            return 0;
        }

        Say("结论");
        Line();
        Say("  => 不通过：见上文的 [FAIL] / [WARN]");
        Line();
        return 1;
    }

    private static string? ArgValue(string[] argv, string key)
    {
        var i = Array.IndexOf(argv, key);
        return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
    }

    private static void Line() => Console.WriteLine(new string('=', 66));
    private static void Section(string t) { Console.WriteLine(); Say(t); }
    private static void Say(string t) => Console.WriteLine(t);
    private static void Ok(string t) => Console.WriteLine("  [ OK ] " + t);
    private static void Warn(string t) => Console.WriteLine("  [WARN] " + t);
    private static void Fail(string t) => Console.WriteLine("  [FAIL] " + t);
}
