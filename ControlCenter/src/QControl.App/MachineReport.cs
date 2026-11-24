using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QControl.Core.Devices;
using QControl.Core.Ec;
using QControl.Hal.Sys;
using QControl.Hal.Wmi;

namespace QControl.App;

/// <summary>
/// 「机型适配报告」—— 让**别的广达模具用户**把自己的机器信息交给我们，好补一份机型档案。
///
/// 项目目前只有 `quanta-nl5`（隐星 G16 Pro）真机标定过，别的机型一律「只读兼容」：
/// 认得出是广达 RW_GMWMI 家族、能读，但一个字节都不写。要让它可用，必须有人提供
/// 那台机器的身份指纹 + EC 原始帧（见 docs/DEVICES.md「新增一个机型」），而用户不会去跑命令行探针
/// —— 这个类就是把探针要做的事搬进界面：点一下，出一份双格式报告。
///
/// 三条纪律：
///   · **只读**。不写 EC（帧只读档案声明 read_only 的子命令，0x0400 按铁律排除）、
///     不改配置、不动自启，连 model.json 都不碰；
///   · **不猜**。哪一段采不到就在 `self_check` 里写明原因，绝不用占位值凑满；
///   · **不给隐私**。序列号 / UUID / MAC / IP / 计算机名 / 文件名一律剔除
///     （见 <see cref="PrivateMarkers"/>）—— 适配只需要型号和 EC 帧，那些一个都用不上。
/// </summary>
internal static partial class MachineReport
{
    public const string SchemaId = "qcontrol/machine-report";
    public const int SchemaVersion = 1;

    /// <summary>整组丢弃的硬件分组：网络那组全是 MAC / IP，对适配没用。</summary>
    private static readonly string[] SkipGroups = ["net"];

    /// <summary>行名里带这些词的整行剔除（序列号类信息对适配无用，对隐私有害）。</summary>
    private static readonly string[] PrivateMarkers =
        ["序列号", "UUID", "处理器 ID", "计算机名", "料号", "标识", "安装日期"];

    /// <summary>EC 铁律：0x0400 永不使用（连读都不读）。</summary>
    private const int ForbiddenCmd = 0x0400;

    // ================================================================ 采集

    /// <summary>
    /// 采一份完整报告。**慢**（注册表盲扫 + 几条 EC 往返 ≈ 1~2 s），调用方必须放后台线程。
    /// </summary>
    public static JsonObject Collect(AppState st, string version)
    {
        var notes = new JsonArray();
        var dev = st.Current;

        var report = new JsonObject
        {
            ["schema"] = SchemaId,
            ["schema_version"] = SchemaVersion,
            ["created"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            ["app"] = AppBlock(st, version),
            ["identity"] = Identity(st, notes),
            ["vendor_candidates"] = Candidates(notes),
            ["vendor_processes"] = Processes(st),
            ["device"] = dev.View(),
            ["ec"] = EcBlock(st, dev, notes),
            ["hardware"] = HardwareBlock(st, notes),
            ["hwinfo"] = HwinfoBlock(st, notes),
            ["self_check"] = notes,
        };

        // 头部给一句"为什么你要看这份东西"，省得开发者再解释一遍
        report["how_to_use"] =
            "把这份文件（或 .txt 全文）发给作者：他会按 identity + vendor_candidates 补一份 "
            + "devices/*.json 的匹配规则，再按 ec.frames 标定 layout 偏移。"
            + "更省事的做法是点「一键采集」—— 它会多采一段帧，把结论和逐帧数据一起写进 "
            + "logs\\adopt-*.json，作者用 --import-pack 一条命令就能转成正式档案。";
        return report;
    }

    /// <summary>
    /// HWiNFO 现状（共享内存到底出不出数）。
    ///
    /// 为什么要单开一节：**没有 EC 的机器（台式机）上，温度/转速/电压只有它一条路**，
    /// 而 HWiNFO 从 7.0 起默认**不导出共享内存**（免费版连续跑满 12 小时还会自动关）。
    /// 少了这一节，收到采集包的人只能从"功耗有数、温度没数"去反推 —— 2026-11-08 那台
    /// 台式机就是这么查的，绕了一大圈。
    /// 采不到也不许影响别的段：失败只留一条说明。
    /// </summary>
    private static JsonObject HwinfoBlock(AppState st, JsonArray notes)
    {
        try
        {
            var diag = st.HwinfoDiag();
            if (diag["ok"] is not JsonValue ok || !ok.TryGetValue<bool>(out var good) || !good)
                notes.Add("HWiNFO：" + Str(diag["note"]));
            return diag;
        }
        catch (Exception e)
        {
            notes.Add("HWiNFO 状态采集失败：" + e.Message);
            return new JsonObject();
        }
    }

    private static JsonObject AppBlock(AppState st, string version) => new()
    {
        ["version"] = version,
        ["runtime"] = $".NET {Environment.Version}",
        ["port"] = st.Port,
        ["os"] = Environment.OSVersion.VersionString,
        ["proc_arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        ["env"] = st.Ec.EnvInfo(),          // admin / write_ok / hint（EC 写入为什么被拦）
    };

    private static JsonObject Identity(AppState st, JsonArray notes)
    {
        try
        {
            // deep: true ⇒ 连 root\WMI 有哪些兼容类也探一遍（未适配机型最需要这一条）
            return st.Identity(deep: true);
        }
        catch (Exception e)
        {
            notes.Add("身份采集失败：" + e.Message);
            return new JsonObject();
        }
    }

    /// <summary>盲扫出来的候选项目号 —— 新机型能不能认出来，全看这一段。</summary>
    private static JsonObject Candidates(JsonArray notes)
    {
        try
        {
            return MachineIdent.ScanVendorCandidates();
        }
        catch (Exception e)
        {
            notes.Add("候选项目号扫描失败：" + e.Message);
            return new JsonObject();
        }
    }

    /// <summary>
    /// 正在跑的原厂进程名（只有进程名，**没有路径** —— 路径里常带用户名）。
    /// 原厂控制中心还在的话，能拿它抓写命令做对照（docs/DEVICES.md 第三步 `caps` 标定）。
    /// </summary>
    private static JsonArray Processes(AppState st)
    {
        try
        {
            // 复用后端那份名单（VendorProcs），别在这里再维护一份"什么算原厂"
            return st.VendorState()["running"]?.DeepClone() as JsonArray ?? new JsonArray();
        }
        catch
        {
            return new JsonArray();
        }
    }

    /// <summary>
    /// EC 段：能不能连上 + 档案命令表 + 一组**只读**原始帧。
    ///
    /// 原始帧是标定 layout 的唯一依据（docs/DEVICES.md 第二步：空载/负载各跑一次，
    /// 看哪个偏移在动）。这里给的是**采集那一瞬间**的快照，所以文本版会提示
    /// "想标定温度/转速，请在空载和烤机时各导一次"。
    /// </summary>
    private static JsonObject EcBlock(AppState st, Device dev, JsonArray notes)
    {
        var ec = new JsonObject();
        try
        {
            var spec = st.Ec.Spec;
            ec["wmi_class"] = spec.Class;
            ec["read_group"] = $"0x{spec.ReadGroup:X4}";
            ec["write_group"] = $"0x{spec.WriteGroup:X4}";
            ec["layout_verified"] = spec.LayoutVerified;
            ec["fans"] = spec.Fans;
            // SKU：三风扇判定（0x33/0x34）的唯一依据，采集包里带上，
            // 事后再问用户"你这台是不是三风扇"就没意义了。读不到就不写这个键。
            if (st.Ec.ProbeSkuOnce() is { } sku) ec["sku"] = sku;
            ec["cmd"] = spec.View()["cmd"]?.DeepClone() ?? new JsonObject();

            var (ok, detail) = st.Ec.Transport.Available();
            ec["available"] = ok;
            if (!ok)
            {
                if (!string.IsNullOrEmpty(detail)) ec["error"] = detail;
                ec["frames"] = new JsonArray();
                notes.Add("连不上 EC（" + (detail ?? "原因未知") + "）：这份报告里没有 EC 原始帧，"
                          + "别的机型适配主要就卡在这一段。");
                return ec;
            }

            var frames = new JsonArray();
            foreach (var (name, sub) in ReadCommands(spec))
            {
                try
                {
                    var one = st.Ec.Raw(new EcCommand((ushort)sub, EcDirection.Read));
                    one["name"] = name;
                    frames.Add(one);
                }
                catch (Exception e)
                {
                    notes.Add($"EC 读 0x{sub:X4}（{name}）失败：{e.Message}");
                }
            }
            ec["frames"] = frames;

            if (frames.Count == 0)
                notes.Add("档案里没有可读的子命令（`ec.read_only` 为空），所以没有采到原始帧。");

            try
            {
                ec["status"] = st.Ec.ReadStatus();
            }
            catch (Exception e)
            {
                notes.Add("读 EC 状态快照失败：" + e.Message);
            }
        }
        catch (Exception e)
        {
            notes.Add("EC 段采集失败：" + e.Message);
        }
        return ec;
    }

    /// <summary>
    /// 只采**档案声明为只读**的子命令，且排除 0x0400（项目铁律：那条连读都不读）。
    /// 未适配机型走 generic 档案，它的 read_only 里正好有 0x0200/0x0202/0x0203/0x0207。
    /// </summary>
    internal static IEnumerable<(string Name, int Sub)> ReadCommands(EcSpec spec)
    {
        foreach (var (name, sub) in spec.Cmd.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (sub is <= 0 or > 0xFFFF) continue;
            if (sub == ForbiddenCmd) continue;
            if (!spec.ReadOnly.Contains((ushort)sub)) continue;
            yield return (name, sub);
        }
    }

    private static JsonArray HardwareBlock(AppState st, JsonArray notes)
    {
        try
        {
            // 当场采一次（不用 HardwareInfo()：那个可能是 busy，报告不能等后台线程）
            // ⚠️ 要**全量**那一帧：报告里的硬件组会从快照取 CPU/GPU/硬盘等字段，
            //    精简档缺段会被写成"读不到"（2026-11-02 分档之后）。
            var status = st.HasCachedStatus ? st.CachedFullStatus() : null;
            return TrimGroups(HardwareProfile.Collect(st.Model, status));
        }
        catch (Exception e)
        {
            notes.Add("硬件信息采集失败：" + e.Message);
            return new JsonArray();
        }
    }

    /// <summary>
    /// 硬件分组瘦身：丢掉网络整组，丢掉序列号类行，去掉 `adv` 标记（报告里全展开）。
    /// 空组整组不出现 —— 与界面同一条规矩，绝不留空壳。
    /// </summary>
    private static JsonArray TrimGroups(JsonObject hw)
    {
        var outArr = new JsonArray();
        if (hw["groups"] is not JsonArray groups) return outArr;

        foreach (var g in groups)
        {
            if (g is not JsonObject go) continue;

            var id = Str(go["id"]);
            if (SkipGroups.Contains(id, StringComparer.Ordinal)) continue;

            var rows = new JsonArray();
            foreach (var r in go["rows"] as JsonArray ?? new JsonArray())
            {
                if (r is not JsonObject ro) continue;
                var k = Str(ro["k"]);
                if (PrivateMarkers.Any(m => k.Contains(m, StringComparison.Ordinal))) continue;

                var clone = ro.DeepClone() as JsonObject ?? new JsonObject();
                clone.Remove("adv");
                // PCI 设备 ID 尾巴上是**实例唯一后缀**（每台机器不同），只留 SUBSYS 那一节 ——
                // 适配要的是"这块卡在这个模具上是什么"，不是"这台机器是哪一台"。
                var dev = Str(clone["v"]);
                if (InstanceSuffix().IsMatch(dev)) clone["v"] = InstanceSuffix().Replace(dev, "$1");
                rows.Add(clone);
            }
            if (rows.Count == 0) continue;

            outArr.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = Str(go["name"]),
                ["rows"] = rows,
            });
        }
        return outArr;
    }

    /// <summary>
    /// PCI 设备 ID 的**实例后缀**：`PCI\VEN_..&DEV_..&SUBSYS_..&REV_A1\` + 实例段
    /// 最后那一段每台机器都不同（等于设备指纹），截掉。SUBSYS 要留着 —— 它才说明
    /// "这张卡是挂在哪个 OEM 模具上的"。
    /// </summary>
    [GeneratedRegex(@"^(PCI\\[^\\]+&REV_[0-9A-F]{2})\\.*$", RegexOptions.IgnoreCase)]
    private static partial Regex InstanceSuffix();

    // ================================================================ 落盘
    /// <summary>
    /// 写盘：`adopt-&lt;机型&gt;-&lt;时间&gt;.json` + 同名 `.txt`。
    ///
    /// 两份各有各的用处：json = **交付包**（作者用 `--import-pack` 直接转成正式档案），
    /// txt 给用户复制粘贴（聊天窗口贴不了文件，但能贴一段文字）。
    /// txt 带 BOM —— 早先实现记事本/部分中文工具没有 BOM 会把 UTF-8 显示成乱码。
    ///
    /// <paramref name="stem"/> 由调用方给定时（一键采集）**写盘前**就能在 txt 里写出
    /// "要发哪个文件" —— 交付物只有一个，不该让用户在两个文件之间猜。
    /// </summary>
    /// <returns>写成功的文件全路径（顺序：json, txt）。</returns>
    public static IReadOnlyList<string> Save(string dir, JsonObject report, string text, string? stem = null)
    {
        System.IO.Directory.CreateDirectory(dir);

        stem ??= MakeStem(report);

        var jsonPath = Path.Combine(dir, stem + ".json");
        var txtPath = Path.Combine(dir, stem + ".txt");

        var json = report.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(jsonPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(txtPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return [jsonPath, txtPath];
    }

    /// <summary>
    /// 报告文件名（不含扩展名）：`adopt-&lt;机型&gt;-&lt;时间&gt;`。
    /// 抽出来是为了让 txt 正文能提前引用同一个名字（见上面 <see cref="Save"/> 的注）。
    /// </summary>
    public static string MakeStem(JsonObject report)
    {
        var ident = report["identity"] as JsonObject;
        return "adopt-" + Slug(Str(ident?["system_product"])) + "-"
               + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    /// <summary>文件名安全化：只留字母数字，最多 20 字符（机型名里常有空格/斜杠）。</summary>
    private static string Slug(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw)
        {
            if (sb.Length >= 20) break;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 0 ? s : "unknown";
    }

    // ================================================================ 文本版

    /// <summary>
    /// 人读版。给用户直接粘到聊天窗口/私信里 —— 所以：短、分节、不含任何隐私字段。
    /// </summary>
    public static string ToText(JsonObject r)
    {
        const int W = 74;
        var sb = new StringBuilder();

        void Line(string s = "") => sb.AppendLine(s);
        void Rule() => Line(new string('-', W));
        void Head(string title)
        {
            Line();
            Rule();
            Line(title);
            Rule();
        }
        void Kv(string k, string? v) => Line("  " + Pad(k, 16) + ": " + (Blank(v) ? "（无）" : v));
        void Sub(string s) => Line("  " + s);

        // ---------------- 头部 ----------------
        Line("Q-Control 控制中心 · 机型适配报告");
        Line("生成时间 " + Str(r["created"]));
        Line("报告格式 " + Str(r["schema"]) + "/" + (r["schema_version"]?.ToJsonString() ?? "?"));
        Rule();
        Line();
        Line("这份东西是什么");
        Wrap(sb, "控制中心目前只对「七彩虹 隐星 G16 Pro」(Quanta NL5) 做过真机标定。"
                + "别的广达模具机型默认走「只读兼容」：能认出是广达家族、能读，但一个字节都不写。"
                + "把这份报告发给作者，他就能按你这台机器的证据补一份机型档案。");
        // 完整采集会临时写 EC，"全程只读"这句是只读采集时代留下的；照抄会让报告
        // 前半句说不写、后半节的「写尝试明细」又列了 7 次写，自己打自己。
        var probed = (r["write_test"] as JsonObject)?["requested"] is JsonValue rq
                     && rq.TryGetValue<bool>(out var want) && want;
        Wrap(sb, probed
            ? "完整采集会临时写 EC 验证性能档与键盘背光（写前读原值、写完读回再恢复），"
              + "风扇不碰；引导观察那一段只读不写，不动任何系统设置。只读采集全程一个字节都不写。"
            : "采集全程只读：不写 EC、不改配置、不动任何系统设置。");

        // ---------------- 程序 ----------------
        Head("程序");
        var app = r["app"] as JsonObject;
        Kv("版本", Str(app?["version"]));
        Kv("运行环境", Str(app?["runtime"]));
        Kv("端口", app?["port"]?.ToJsonString());
        Kv("系统", Str(app?["os"]));
        var env = app?["env"] as JsonObject;
        Kv("管理员", env?["admin"]?.ToJsonString() == "true" ? "是" : "否");
        Kv("EC 可写", env?["write_ok"]?.ToJsonString() == "true" ? "是" : "否");

        // ---------------- 机型识别 ----------------
        Head("机型识别");
        var ident = r["identity"] as JsonObject;
        Kv("整机厂商", Str(ident?["system_vendor"]));
        Kv("整机型号", Str(ident?["system_product"]));
        Kv("整机版本", Str(ident?["system_version"]));
        Kv("产品家族", Str(ident?["system_family"]));
        Kv("主板", Join(" / ", Str(ident?["board_vendor"]), Str(ident?["board_product"])));
        Kv("BIOS", Join(" ", Str(ident?["bios_vendor"]), Str(ident?["bios_version"])));
        Kv("ACPI PNP0C14", Join(", ", Flat(ident?["acpi_pnp0c14"], 3)));

        var dev = r["device"] as JsonObject;
        Kv("命中档案", Join(" ", Str(dev?["id"]), "（" + Str(dev?["name"]) + "）"));
        Kv("已验证", dev?["verified"]?.ToJsonString() == "true" ? "是" : "否");
        Kv("识别依据", Join(", ", Flat(dev?["matched_by"], 6)));

        var wmi = Flat(ident?["wmi_classes"], 6);
        if (wmi.Count > 0) Kv("WMI 兼容类", Join(", ", wmi));

        var reg = Flat(ident?["vendor_registry"], 8);
        if (reg.Count > 0) Kv("已登记项目号", Join(", ", reg));

        var dnotes = Flat(dev?["notes"], 4);
        if (dnotes.Count > 0)
        {
            Line();
            Sub("档案备注：");
            foreach (var n in dnotes) Wrap(sb, "· " + n, 4, 6);
        }

        // ---------------- 候选项目号 ----------------
        Head("候选项目号（HKLM\\SOFTWARE 盲扫）");
        Wrap(sb, "原厂控制中心一定会在注册表里落一个以模具项目号命名的键（本机是 NL5）。"
                + "下面是把 SOFTWARE 下所有「像项目号」的键都列出来的结果 —— 带 ControlCenter / EC "
                + "字样子键的那个，就是你这台机器的模具代号。");
        Line();

        var cand = r["vendor_candidates"] as JsonObject;
        var shown = 0;
        foreach (var (code, node) in cand ?? new JsonObject())
        {
            if (shown++ >= 40) { Line("  …（还有更多，完整清单见 .json）"); break; }
            if (node is not JsonObject co) continue;
            var bits = Str(co["bits"]);
            var sub = Flat(co["sub"], 6);
            var val = Flat(co["val"], 6);
            Line($"  {code,-8} [{bits}]" + (sub.Count > 0 ? "  子键: " + Join(", ", sub) : "")
                                     + (val.Count > 0 ? "  值: " + Join(", ", val) : ""));
        }
        if (shown == 0)
            Line("  （没扫到形如项目号的键 —— 也许原厂控制中心没装，或模具代号规则不同）");

        var vp = Flat(r["vendor_processes"], 6);
        if (vp.Count > 0)
        {
            Line();
            Kv("原厂进程在跑", Join(", ", vp));
        }

        // ---------------- EC ----------------
        Head("EC 原始帧");
        var ec = r["ec"] as JsonObject;
        Kv("传输类", Str(ec?["wmi_class"]));
        Kv("读 / 写组", Join("  /  ", Str(ec?["read_group"]), Str(ec?["write_group"])));
        Kv("连得上", ec?["available"]?.ToJsonString() == "true" ? "是" : "否");
        Kv("偏移已标定", ec?["layout_verified"]?.ToJsonString() == "true" ? "是" : "否（只回原始字节）");
        if (Str(ec?["error"]) is { Length: > 0 } ecErr) Kv("错误", ecErr);

        var frames = ec?["frames"] as JsonArray;
        if (frames is { Count: > 0 })
        {
            Line();
            Wrap(sb, "下面每条是 32 字节应答。DAT2..DAT8 是按 4 字节切出来的 u32（偏移 4/8/12/16/20/24/28）。"
                    + "标定温度/转速要在空载和烤机时各导一次，对比哪个偏移在动。");
            foreach (var f in frames)
            {
                if (f is not JsonObject fo) continue;
                Line();
                Line($"  {Str(fo["sub"])} {Str(fo["name"])}");
                foreach (var l in HexLines(Str(fo["resp"]))) Line("    " + l);
                var u = fo["u32"] as JsonObject;
                if (u is not null)
                    Line("    " + string.Join("  ", u.Select(kv => $"{kv.Key}={kv.Value?.ToJsonString()}")));
                if (Str(fo["note"]) is { Length: > 0 } fn) Line("    ⚠ " + fn);
            }
        }

        // ---------------- HWiNFO ----------------
        // 放在 EC 后面：没有 EC 的机器（台式机）上，温度/转速/电压全在这一节里交代清楚，
        // 否则收到的报告只剩"连不上 EC"，没人知道现场为什么一个传感器都没有。
        if (r["hwinfo"] is JsonObject hw)
        {
            Head("传感器读数来源（HWiNFO）");
            var found = hw["exe_found"]?.ToJsonString() == "true";
            Kv("程序目录里有它", found ? "是" : "否（见「采集说明」怎么放）");
            Kv("进程", hw["running"]?.ToJsonString() == "true"
                ? "在跑" + (hw["ours_running"]?.ToJsonString() == "true" ? "（控制中心拉起的）" : "（用户自己开的）")
                : "没在跑");
            Kv("共享内存", (hw["segment"]?.ToJsonString() == "true" ? "段在" : "段不在")
                + " / " + (hw["fresh"]?.ToJsonString() == "true"
                    ? "新鲜"
                    : "不新鲜（" + (hw["age_sec"] is { } ag ? ag.ToJsonString() + " 秒前" : "无时间戳") + "）"));
            if (Str(hw["layout"]) is { Length: > 0 } layout)
                Kv("布局", layout + "  v" + (hw["version"]?.ToJsonString() ?? "?")
                    + " r" + (hw["revision"]?.ToJsonString() ?? "?")
                    + " · " + (hw["sensor_elem"]?.ToJsonString() ?? "?")
                    + "/" + (hw["reading_elem"]?.ToJsonString() ?? "?")
                    + " · " + (hw["n_sensors"]?.ToJsonString() ?? "?") + " 组 / "
                    + (hw["n_readings"]?.ToJsonString() ?? "?") + " 条");
            Kv("结论", Str(hw["note"]));

            if (hw["reads"] is JsonObject reads)
            {
                Line();
                foreach (var (label, key) in new[]
                         {
                             ("CPU 温度", "cpu_temp"), ("显卡温度", "gpu_temp"), ("风扇转速", "fans_rpm"),
                             ("CPU 功耗", "cpu_power"), ("CPU 电压", "cpu_voltage"),
                             ("显存结温", "gpu_mem_temp"), ("PCH 温度", "pch"),
                         })
                {
                    var pair = reads[key] as JsonArray;
                    if (pair is null) continue;
                    var v = pair.Count > 0 ? pair[0] : null;
                    var src = pair.Count > 1 ? Str(pair[1]) : "";
                    // 转速那项是 `{cpu, gpu}`：两路都没有时别印 `{"cpu":null,"gpu":null}`
                    var cell = v is null ? "（无）" : v.ToJsonString();
                    if (v is JsonObject vo && vo.All(kv => kv.Value is null)) cell = "（无）";
                    Line("  " + Pad(label, 16) + ": " + cell + (src.Length > 0 ? "  ← " + src : ""));
                }
            }

            var temps = hw["temps"] as JsonArray;
            if (temps is { Count: > 0 })
            {
                Line();
                Sub($"段里认出来的温度读数（前 {Math.Min(temps.Count, 15)} 条，共 {temps.Count} 条）");
                foreach (var t in temps.Take(15))
                {
                    if (t is not JsonObject to) continue;
                    Line("    " + Pad(Str(to["group"]), 28) + " " + Str(to["label"]) + " = "
                         + to["value"]?.ToJsonString() + " " + Str(to["unit"])
                         + "  [type=" + to["type"]?.ToJsonString() + "]");
                }
            }
        }

        // ---------------- 硬件 ----------------
        Head("硬件档案");
        foreach (var g in r["hardware"] as JsonArray ?? new JsonArray())
        {
            if (g is not JsonObject go) continue;
            Line();
            Line("  " + Str(go["name"]));
            foreach (var row in go["rows"] as JsonArray ?? new JsonArray())
            {
                if (row is not JsonObject ro) continue;
                var k = Str(ro["k"]);
                var v = Str(ro["v"]);
                var sub2 = Str(ro["sub"]);
                Line("    " + Pad(k, 12) + " " + v + (Blank(sub2) ? "" : "（" + sub2 + "）"));
            }
        }

        // ---------------- 自检 ----------------
        Head("采集说明");
        var notes = r["self_check"] as JsonArray;
        if (notes is { Count: > 0 })
        {
            foreach (var n in notes) Wrap(sb, "· " + Str(n), 2, 4);
        }
        else
        {
            Line("  全部采集成功，没有异常。");
        }

        // ---------------- 隐私 ----------------
        Head("隐私说明");
        Wrap(sb, "本报告不含：机器序列号、整机 UUID、主板/硬盘序列号、网卡 MAC 与 IP、"
                + "计算机名、用户名、任何本地文件路径、Wi-Fi 名称。");
        Wrap(sb, "报告里只有：机型号 / BIOS / 主板型号 / 内存与显卡与硬盘的型号 / "
                + "注册表里像模具代号的键名 / EC 的 32 字节应答。");
        Line();

        return sb.ToString();
    }

    /// <summary>
    /// 按宽度折行。<paramref name="indent"/> = 首行缩进，<paramref name="hanging"/> = 续行缩进
    /// （列表项传更大的值，续行才会落在项目符号右边；不给就与首行对齐）。
    /// 中文按 2 列宽算，够用了。
    /// </summary>
    private static void Wrap(StringBuilder sb, string text, int indent = 2, int hanging = -1)
    {
        const int limit = 72;
        var first = new string(' ', indent);
        var rest = new string(' ', hanging >= 0 ? hanging : indent);
        var line = first;
        var used = indent;

        foreach (var ch in text)
        {
            var w = ch > 0x2E80 ? 2 : 1;      // 中日韩字符按双宽
            if (used + w > limit && ch != ' ')
            {
                sb.AppendLine(line);
                line = rest;
                used = rest.Length;
            }
            line += ch;
            used += w;
        }
        if (line.Trim().Length > 0) sb.AppendLine(line);
    }

    /// <summary>32 字节 → 每行 16 字节的两行（一行 95 字符塞进聊天窗口会被折得没法看）。</summary>
    private static IEnumerable<string> HexLines(string hex)
    {
        var parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { yield return "（空）"; yield break; }
        for (var i = 0; i < parts.Length; i += 16)
            yield return string.Join(' ', parts.Skip(i).Take(16));
    }

    // ---------------------------------------------------------------- 小工具

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>只取"值为 true 的键名"或数组元素 —— vendor_registry / wmi_classes 都是 {名: bool} 形状。</summary>
    private static List<string> Flat(JsonNode? n, int cap)
    {
        var list = new List<string>();
        switch (n)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    if (list.Count < cap && v is JsonValue vv && vv.TryGetValue<bool>(out var b) && b)
                        list.Add(k);
                break;
            case JsonArray a:
                foreach (var x in a)
                    if (list.Count < cap && Str(x) is { Length: > 0 } s) list.Add(s);
                break;
            default:
                if (Str(n) is { Length: > 0 } one && cap > 0) list.Add(one);
                break;
        }
        return list;
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static string Join(string sep, params string?[] parts)
        => string.Join(sep, parts.Where(p => !Blank(p)));

    private static string Join(string sep, IEnumerable<string> parts)
        => string.Join(sep, parts.Where(p => !Blank(p)));

    /// <summary>中文按双宽对齐（`PadRight` 按字符数算，会把"整机厂商"和"版本"对不齐）。</summary>
    private static string Pad(string s, int width)
    {
        var used = s.Sum(c => c > 0x2E80 ? 2 : 1);
        return used >= width ? s : s + new string(' ', width - used);
    }
}
