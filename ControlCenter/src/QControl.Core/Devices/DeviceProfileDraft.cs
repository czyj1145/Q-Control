using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QControl.Core.Devices;

/// <summary>
/// 把「一次采集」变成「一份能用的机型档案」—— 纯逻辑，不碰磁盘也不碰注册表。
///
/// 为什么要有这一步：适配流程（docs/DEVICES.md）里最贵的不是采集，是**人**. 用户拿着一堆
/// 32 字节十六进制，作者再手工照抄成 json，中间任何一次错位都会变成"风扇读数反了"
/// 这种没人能看出原因的 bug。这里把能自动填的全填上，人只做复核。
///
/// 两条底线，**写死在代码里，不许由调用方改**：
///   · `verified` 与 `writes_allowed` 恒为 false —— 采集只能证明"读得出来"，
///     证明不了"写进去是对的"。后者要作者看原厂抓包，不是一键能决定的；
///   · 认不准的字段**不填**。宁可档案里少一项（界面上那块就不显示），
///     也不填一个"大概是"的偏移 —— 那是拿别人的散热读数试错。
/// </summary>
public static class DeviceProfileDraft
{
    /// <summary>
    /// 生成档案草案。
    /// </summary>
    /// <param name="identity">身份指纹（`MachineIdent.Collect(deep:true)` 的结果）。</param>
    /// <param name="candidates">候选项目号（`MachineIdent.ScanVendorCandidates()`）。</param>
    /// <param name="generic">兜底档案原文（`devices/quanta-gmwmi-generic.json`）—— 命令号、组号照抄它，
    /// 因为能跑出这份草案就说明这台机器已经在用 generic 档案读 EC 了。</param>
    /// <param name="advice">自动标定结果。</param>
    /// <param name="appVersion">程序版本（写进档案，方便作者对批次）。</param>
    /// <param name="samples">这段采样采了几帧（作者判断证据强度用）。</param>
    /// <param name="refs">用了哪些原生参照（"没认出温度"时能分清是没负载还是没参照）。</param>
    /// <param name="selfCheck">采集过程中的异常，原样转述，不吞。</param>
    public static JsonObject Build(
        JsonObject identity,
        JsonObject candidates,
        JsonObject generic,
        LayoutAdvice advice,
        string appVersion,
        int samples,
        JsonObject refs,
        IReadOnlyList<string> selfCheck)
    {
        var (project, projectWhy) = DetectProject(candidates);
        var product = Str(identity["system_product"]);
        var vendor = Str(identity["system_vendor"]);

        var id = project is not null
            ? "quanta-" + project.ToLowerInvariant()
            : "quanta-" + Slug(product is { Length: > 0 } p ? p : "unknown");

        var match = BuildMatch(project, product, vendor);

        // ec 段照抄 generic：能采到帧就说明这台机器本来就在用 generic 的命令表。
        var ec = generic["ec"]?.DeepClone() as JsonObject ?? new JsonObject();
        var layoutJson = new JsonObject();
        foreach (var (field, (off, len)) in advice.Layout.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            layoutJson[field] = new JsonArray(off, len);
        ec["layout"] = layoutJson;
        ec["layout_verified"] = advice.LayoutVerified && layoutJson.Count > 0;

        var profile = new JsonObject
        {
            ["id"] = id,
            ["name"] = NameOf(vendor, product),
            ["vendor"] = vendor is { Length: > 0 } ? vendor : "（未登记）",
            ["mold"] = project is not null ? "Quanta " + project : "Quanta / RW_GMWMI 家族（项目号未识别）",
            // ⚠️ 这两行是本文件的立论：一键采集只给"读"，"写"必须人复核
            ["verified"] = false,
            ["writes_allowed"] = false,
            ["priority"] = DeviceProfile.UserPriorityFloor,
            ["match"] = match,
            ["ec"] = ec,
            ["caps"] = generic["caps"]?.DeepClone() ?? new JsonObject(),
            ["notes"] = Notes(project, projectWhy, product, vendor, advice),
            ["adopt"] = new JsonObject
            {
                ["created"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                ["app"] = appVersion,
                ["samples"] = samples,
                ["refs"] = refs.DeepClone(),
                ["layout_verified"] = advice.LayoutVerified,
                ["missing"] = ToArray(advice.Missing),
                ["guesses"] = new JsonArray(advice.Guesses.Select(g => (JsonNode?)new JsonObject
                {
                    ["field"] = g.Field,
                    ["cmd"] = g.Cmd,
                    ["offset"] = g.Offset,
                    ["confidence"] = g.Confidence,
                    ["why"] = g.Why,
                }).ToArray()),
                ["identity"] = identity.DeepClone(),
                ["self_check"] = ToArray(selfCheck),
                // 统计表：作者复核"为什么没认出来"时唯一有信息量的东西
                // （哪个偏移在动、跟谁相关多少）。只留变化最大的 12 条，不然整页都是 0。
                ["stats"] = new JsonArray(advice.Stats
                    .OrderByDescending(s => s.Delta)
                    .Take(12)
                    .Select(s => (JsonNode?)new JsonObject
                    {
                        ["cmd"] = s.Cmd,
                        ["offset"] = s.Offset,
                        ["min"] = Math.Round(s.Min, 1),
                        ["max"] = Math.Round(s.Max, 1),
                        ["distinct"] = s.Distinct,
                        ["corr_cpu_power"] = NumOrNull(s.CorrPower),
                        ["corr_cpu_temp"] = NumOrNull(s.CorrCpu),
                        ["corr_gpu_temp"] = NumOrNull(s.CorrGpu),
                    }).ToArray()),
                ["how_to_use"] =
                    "这份文件放在 %ProgramData%\\Q-Control\\devices\\ 下，重启控制中心即可按它识别本机。"
                    + "要并进正式版本：把 id/name/mold 改好，确认 adopt.guesses 里每一条，"
                    + "再把 verified 置 true（写入权限只能由作者在复核原厂行为后打开）。",
            },
        };

        return profile;
    }

    /// <summary>
    /// 从盲扫结果里挑模具项目号。
    ///
    /// 判据（按可靠度）：子键里有 `ControlCenter`（原厂控制中心必然落的那个）> 有任何子键 > 值名里带 EC/BIOS。
    /// 只认短键名（≤8 个字符、全大写字母数字）—— `Microsoft` / `Intel` 这种长名字不是项目号。
    /// </summary>
    private static (string? Code, string Why) DetectProject(JsonObject candidates)
    {
        string? loose = null;
        var looseWhy = "";

        foreach (var (code, node) in candidates)
        {
            if (node is not JsonObject o) continue;
            var subs = new List<string>();
            if (o["sub"] is JsonArray sa)
                foreach (var s in sa) if (Str(s) is { Length: > 0 } v) subs.Add(v);

            var hasCc = subs.Any(s => s.Contains("controlcenter", StringComparison.OrdinalIgnoreCase)
                                   || s.Contains("control center", StringComparison.OrdinalIgnoreCase));
            if (hasCc) return (code, "子键里有 ControlCenter —— 原厂控制中心落的那个键，最可靠的一条");

            if (loose is null && IsProjectCode(code) && subs.Count > 0)
                (loose, looseWhy) = (code, $"短键名 + 有子键（{string.Join("/", subs.Take(3))}），但没有 ControlCenter 子键，需复核");
        }

        return loose is not null ? (loose, looseWhy) : (null, "没有扫到像模具项目号的注册表键");
    }

    /// <summary>项目号长什么样：本机是 `NL5`，一般 2~6 位大写字母+数字。太长的键名一律不认。</summary>
    private static bool IsProjectCode(string code)
        => code.Length is >= 2 and <= 8 && code.All(c => char.IsAsciiLetterOrDigit(c)) && code.Any(char.IsAsciiLetter);

    private static JsonObject BuildMatch(string? project, string product, string vendor)
    {
        var any = new JsonArray();

        if (project is not null)
            any.Add(new JsonObject { ["vendor_registry"] = new JsonArray("^" + Esc(project) + "$") });

        if (product.Length > 0)
        {
            var block = new JsonObject { ["system_product"] = new JsonArray("^" + Esc(product) + "$") };
            if (vendor.Length > 0)
                block["system_vendor"] = new JsonArray("^" + Esc(vendor) + "$");
            any.Add(block);
        }

        // 一条证据都没有时退回"有 RW_GMWMI 就算"——与 generic 同口径，至少不会认不出来
        if (any.Count == 0)
            any.Add(new JsonObject { ["wmi_classes"] = new JsonArray("^RW_GMWMI$") });

        return new JsonObject { ["any"] = any };
    }

    private static JsonArray Notes(string? project, string projectWhy, string product, string vendor, LayoutAdvice advice)
    {
        var arr = new JsonArray
        {
            "这份档案由「一键机型档案采集」本机生成：写入恒关（verified / writes_allowed 都是 false），"
            + "只用于让这台机器显示出温度与转速。",
            project is not null
                ? $"模具项目号取自注册表：{project}（{projectWhy}）。"
                : "没能从注册表里认出模具项目号，match 退回到机型名 —— 这是最不可靠的一条（OEM 常把"
                  + " system_product 整片填成同一个营销名），收到这份档案时请先复核。",
            "match 里按机型名那条是给「原厂软件被卸载后注册表键没了」准备的退路。",
        };

        if (advice.LayoutVerified)
        {
            // 别把"认出来了"一律说成"高置信"：置信度是**逐项**的，写进 `adopt.guesses`，
            // 报告里那句话必须跟着它走。2026-11-23 12:21 真机就是这样打脸的：
            // `cpu_temp` 在 adopt.guesses 里是 medium（16 帧全是 55，EC 那一路一次没变，
            // 只证明数值对得上），档案备注却写着"已高置信认出：CPU 温度 + 显卡温度 + 背光"。
            (string Field, string Label)[] fields =
                [("cpu_temp", "CPU 温度"), ("gpu_temp", "显卡温度"), ("backlight_level", "背光")];

            string? Conf(string field)
                => advice.Guesses.FirstOrDefault(g => g.Field == field)?.Confidence;

            var high = fields.Where(f => advice.Layout.ContainsKey(f.Field) && Conf(f.Field) == "high")
                             .Select(f => f.Label).ToList();
            var weak = fields.Where(f => advice.Layout.ContainsKey(f.Field) && Conf(f.Field) != "high")
                             .Select(f => f.Label).ToList();

            var text = high.Count > 0 ? "已高置信认出：" + string.Join(" + ", high) : "";
            if (weak.Count > 0)
                text += (text.Length > 0 ? "；" : "已认出（中等信心，采样里变化不足、只对上了数值）：")
                        + string.Join(" + ", weak);

            arr.Add((text.Length > 0 ? text : "已认出部分字段") + " ⇒ layout_verified=true，"
                    + "档案加载后这些读数就能显示。没认出的项一个都没写进去。");
        }
        else
            arr.Add("温度两路没齐 ⇒ layout_verified=false：程序只回原始字节、不显示温度。"
                    + "请在有负载时再采一次，或由作者按 adopt.guesses 人工确认。");

        foreach (var n in advice.Notes) arr.Add(n);

        if (!vendor.Contains("QUANTA", StringComparison.OrdinalIgnoreCase) && vendor.Length > 0
            && product.Length > 0)
            arr.Add($"SMBIOS 报的是「{vendor} {product}」—— 广达模具常被 OEM 改标，"
                    + "真正同模具的机器可能挂着别的牌子，所以 match 优先用项目号而不是品牌名。");

        return arr;
    }

    private static string NameOf(string vendor, string product)
    {
        var name = (vendor + " " + product).Trim();
        return name.Length > 0 ? name + "（本机采集）" : "未命名机型（本机采集）";
    }

    /// <summary>正则字面量化。**不用 Regex.Escape**：它会把空格转成 `\ `，.NET 的正则解析器不认这个转义。</summary>
    private static string Esc(string s)
        => string.Concat(s.Select(c => "\\^$.|?*+()[]{}".Contains(c) ? "\\" + c : c.ToString()));

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    /// <summary>
    /// 相关系数写进 json。**NaN 必须变 null** —— `JsonValue.Create(double.NaN)` 会直接抛
    /// （"JSON 不支持 NaN"），一份认不出任何东西的采集会因此整个失败。
    /// </summary>
    private static JsonNode? NumOrNull(double v)
        => double.IsNaN(v) ? null : JsonValue.Create(Math.Round(v, 2));

    private static string Slug(string raw)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw)
        {
            if (sb.Length >= 20) break;
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString().ToLowerInvariant() : "unknown";
    }

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
