using System.Globalization;
using System.Text.Json.Nodes;

namespace QControl.Core.Devices;

/// <summary>
/// 复核意见。三档的分工：
///   · <see cref="Blockers"/> —— 有它就不能并入：条件是"这份档案会认错机器 / 根本加载不了"；
///   · <see cref="Warnings"/> —— 能并入，但作者必须知道的风险（读不全、caps 没验证、match 靠机型名）；
///   · <see cref="Notes"/> —— 这次采集认出了什么、按什么证据判的。
/// </summary>
public sealed record PackReview(
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes)
{
    public bool Ready => Blockers.Count == 0;
}

/// <summary>
/// 导入结果。<see cref="Profile"/> 只在 <see cref="Ok"/> 为真时非空 ——
/// 有阻塞项时**不产出档案**，宁可让人先处理证据，也不给一份"大概能用"的 json。
/// </summary>
public sealed record PackImportResult(
    bool Ok,
    string? Error,
    JsonObject? Profile,
    PackReview Review,
    string? MatchedBy)
{
    public static PackImportResult Fail(string error)
        => new(false, error, null, new PackReview([error], [], []), null);
}

/// <summary>
/// 把「用户发来的采集包」变成「能并进 `devices/*.json` 的正式档案」—— 纯逻辑，
/// 不碰磁盘也不碰注册表（读写文件在 <c>App/PackImportCli.cs</c>）。
///
/// 为什么要有这一步：一键采集已经能在**用户自己的机器上**把只读部分适配好
/// （见 <see cref="DeviceProfileDraft"/>），但那份草案是给"本机临时生效"用的 ——
/// priority 50、verified 恒 false、caps 照抄 generic。要让它变成**正式机型档案**
/// （进安装包、给同模具的所有人用），中间还差一次人的复核。这里把复核里
/// **能自动的部分全自动**（id / priority / match 自检 / 证据齐全度 / caps 缺口清单），
/// 人只看结论。
///
/// 三条底线，与采集侧同源、不许调用方绕过：
///   · `verified` 恒 false，除非调用方显式 <c>allowWrite</c>（那是作者复核原厂行为之后的动作）；
///   · 包里自称的 `verified` / `writes_allowed` **一律不信**（防止手改字段就放开写权限）；
///   · match 规则必须用包里的 identity 当场跑通，跑不通就拒绝 ——
///     "发出去认不出自己"的档案比没有档案更糟。
/// </summary>
public static class PackImport
{
    /// <summary>采集包的 schema 标识（与 <c>MachineReport.SchemaId</c> 同值 —— 那边是 App 层，Core 不能引用它）。</summary>
    public const string SchemaId = "qcontrol/machine-report";

    /// <summary>
    /// 正式档案（进 `devices/`）的优先级。与内置已验证机型（nl5=100）同档：
    /// 只有同模具的专用档案，才配和它并列；generic 是 10，一定在后面。
    /// </summary>
    public const double FormalPriority = 100;

    /// <summary>
    /// 导入一份采集包。
    /// </summary>
    /// <param name="pack">采集包本体（`logs/adopt-*.json` 的整个 JsonObject）。</param>
    /// <param name="allowWrite">
    /// 是否给写权限。**默认 false**，只有作者复核过原厂行为（写命令、APMD 映射、RGB 分区）
    /// 之后才手动打开 —— 采集本身只能证明"读得出来"。
    /// </param>
    /// <param name="now">导入时间戳（测试可固定）。</param>
    public static PackImportResult Import(JsonObject pack, bool allowWrite = false, DateTimeOffset? now = null)
    {
        try
        {
            return ImportCore(pack, allowWrite, now ?? DateTimeOffset.Now);
        }
        catch (Exception e)
        {
            // 结构怪异的包（数组当对象用、类型不对）不该让调用方崩在 IO 里
            return PackImportResult.Fail("解析采集包失败：" + e.Message);
        }
    }

    private static PackImportResult ImportCore(JsonObject pack, bool allowWrite, DateTimeOffset now)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        var notes = new List<string>();

        // ---------------------------------------------------------------- 1. 这是不是一份采集包
        var schema = Str(pack["schema"]);
        if (schema.Length > 0 && !string.Equals(schema, SchemaId, StringComparison.Ordinal))
            return PackImportResult.Fail(
                $"这不是机型采集包（schema = {schema}，应为 {SchemaId}）。"
                + "别把 devices\\ 目录里的档案当成包发过来 —— 档案没有证据，没法复核。");

        if (pack["identity"] is not JsonObject identity || identity.Count == 0)
            return PackImportResult.Fail(
                $"包里没有 identity：这不像是机型采集包（应含 schema = {SchemaId}）。"
                + "采集没跑完、文件被截断，或者把 devices\\ 里的档案当成包发过来了。");

        // ---------------------------------------------------------------- 2. 取草案（结论）
        //
        // profile_draft 是 2026-11-07 起才写进报告的。旧包只有 adopt.guesses，
        // 重建出来会丢 stats 和 caps 来源，不如让人重采一次（重采只要 30 秒）。
        if (pack["profile_draft"] is not JsonObject draft || draft.Count == 0)
            return PackImportResult.Fail(
                "这份文件里没有 profile_draft（旧采集工具产出的报告）：请让对方用新版控制中心"
                + "重新点一次「一键采集」，再把 logs\\adopt-*.json 发过来。");

        var profile = (JsonObject)draft.DeepClone();

        // ---------------------------------------------------------------- 3. 规范成正式档案
        var id = Str(profile["id"]);
        if (id.Length == 0)
            blockers.Add("草案里没有 id —— 采集时没能生出档案名，这份不能直接用。");
        else if (!IsValidId(id))
            blockers.Add($"档案 id `{id}` 不合规（只许小写字母/数字/短横线）—— 手改一下再导入。");

        profile["priority"] = FormalPriority;      // 草案的 50 是"本机临时档"的下限，正式档案要 100
        profile["verified"] = allowWrite;
        // 明确删掉：这个键存在时会**盖过** verified（见 Device.ProfileWrites），
        // 留着 false 会让"作者把 verified 改成 true"变成没反应。
        profile.Remove("writes_allowed");

        var adopt = profile["adopt"] as JsonObject ?? new JsonObject();
        profile["adopt"] = adopt;
        adopt["import"] = new JsonObject
        {
            ["at"] = now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            ["pack_created"] = Str(pack["created"]),
            ["pack_app"] = Str(pack["app"]?["version"]),
            ["pack_samples"] = (pack["samples"] as JsonArray)?.Count ?? 0,
            ["write_allowed_by"] = allowWrite ? "作者显式 --allow-write" : "未开启（这份档案只能读）",
        };
        adopt.Remove("how_to_use");
        adopt["how_to_use"] =
            "这是从用户采集包导入的正式档案。verified=false 时只做只读展示；"
            + "要开写入：按 docs/DEVICES.md 第三步确认 caps（风扇路数 / MUX / RGB 分区）与写命令，"
            + "再用 --allow-write 重新导入一次，或手改 verified。";

        // ---------------------------------------------------------------- 4. match 自检（关键）
        //
        // 用**采集机上真实的 identity** 跑一遍这份规则：认不出 ⇒ 这份档案发出去对谁都不生效，
        // 包括采集它的人。这是唯一一条能自动验证"适配到没到位"的证据，所以它是阻塞项。
        string? matchedBy = null;
        var loaded = DeviceProfile.TryLoad(profile);
        if (loaded is null)
        {
            blockers.Add("生成的档案加载不起来（没有 id）—— 见上面那条。");
        }
        else if (DeviceMatcher.Match(loaded, identity) is { } hit)
        {
            matchedBy = string.Join(", ", hit.Fields);
            notes.Add($"match 自检通过：用采集时的身份跑这份规则，命中的字段是 {matchedBy}。");
        }
        else
        {
            blockers.Add("match 自检**不通过**：用采集包里的 identity 跑这份规则认不出那台机器。"
                         + "别发版 —— 发出去连采集它的人自己都命中不了。");
        }

        // ---------------------------------------------------------------- 5. 会不会顶掉已有档案
        var clash = DeviceProfile.LoadAll().FirstOrDefault(p => p.Id == id);
        if (clash is not null && clash.Verified)
            blockers.Add($"id `{id}` 与内置已验证档案（{Str(clash.Raw["name"])}）同名："
                         + "导入会把它顶掉。换个 id，或确认这份确实要替换它（那就改 id 后再手工合并）。");

        // ---------------------------------------------------------------- 6. 证据齐全度
        var ec = profile["ec"] as JsonObject;
        var layout = ec?["layout"] as JsonObject;
        if (layout is null || layout.Count == 0)
            warnings.Add("layout 是空的：这份档案只能认机型，EC 温度/转速一个都读不出来"
                         + "（未标定机型上会由 HWiNFO 兜底显示，但那不是 EC 原生读数）。");
        else if (!Bool(ec?["layout_verified"]))
            warnings.Add("layout_verified = false：程序只回原始字节、不展示温度。"
                         + "值是对的也不要紧，但作者要清楚这份档案在界面上等于「认得出、读不出」。");

        if (!HasProjectCode(profile))
            warnings.Add("match 里没有模具项目号（vendor_registry），只靠机型名："
                         + "OEM 常把 SMBIOS 整片填成同一个营销名，可能误命中别的机器。"
                         + "有条件的话让对方卸掉原厂控制中心前先把 HKLM\\SOFTWARE\\WOW6432Node\\<项目号> 记下来。");

        // 风扇转速单独看：它也在 layout 里，但判据和温度完全不同（看变速的形状，不看相关性）。
        // 缺了它，机器上看得到温度却看不到转速、风扇曲线也没有反馈 —— 属"适配还差一截"，
        // 而补的办法很具体（引导第 5 步就是干这个的），要写出来。
        var rpmFields = (ec?["layout"] as JsonObject)?.Select(kv => kv.Key)
            .Where(k => k is "fan1_rpm" or "fan2_rpm" or "fan3_rpm").ToList() ?? new List<string>();
        if (rpmFields.Count > 0)
            notes.Add("风扇转速在 layout 里：" + string.Join("、", rpmFields)
                      + "（靠「风扇拉起那一段」的形状认出，逐项依据见包里 `fan_rpm` 段）。"
                      + (Bool(adopt["fan_rpm"]?["roles_decided"])
                          ? "哪一路对 CPU 已用 20 秒 CPU 负载判定：`fan1_rpm` 恒为 CPU 风扇。"
                          : "CPU / GPU 的归属**没判出来**（两路一起升，或负载没拉起来）——"
                            + "仍按偏移升序，复核时看包里 `fan_roles` 的原因。"));
        else
            warnings.Add("包里没有风扇转速（layout 里没有 fan1_rpm / fan2_rpm / fan3_rpm）：这台机器上看不到转速、"
                         + "风扇曲线也没有反馈。让对方按引导第 5 步把风扇拉到 Max 停两秒再切回自动、重采一次即可。");

        var samples = pack["samples"] as JsonArray;
        if (samples is null)
            warnings.Add("包里没有本轮采样帧：结论能看，但「为什么这么判」没法复算。");
        else if (samples.Count < 8)
            warnings.Add($"包里只有 {samples.Count} 帧采样（正常 16 帧）：相关性算得比较勉强，"
                         + "建议让对方在有负载时再采一次。");

        var refs = adopt["refs"] as JsonObject;
        if (!Bool(refs?["gpu_temp"]))
            warnings.Add("这段采样没有显卡温度参照：显卡那一路多半没认出来（采集时显卡在待机），"
                         + "不一定是坏；核对散热页有没有显卡温度即可。");

        // ---------------------------------------------------------------- 6.25 观察原厂
        //
        // 2026-11-09 起「一键适配」会顺带观察原厂：抓原厂控制中心真实下发的 EC 写命令，
        // 把能确定的 caps（键盘灯 / 显卡切换 / 手动风扇 / 曲线）填进去。抓包是客观证据，
        // 但「分区数 / 风扇路数 / 档位映射」仍是按报文推断，要作者复核 —— 不据此开写权限。
        var obs = adopt["observation"] as JsonObject;
        var obsCmds = (obs?["write_cmds"] as JsonArray)?.Count ?? 0;
        var obsKeys = new List<string>();
        if (obs?["conclusion"] is JsonObject concl)
            foreach (var (k, v) in concl)
                if (v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b) obsKeys.Add(k);

        if (obs is not null)
        {
            var obsSkip = Str(obs["skipped"]);
            if (obsSkip.Length > 0)
                warnings.Add("观察原厂没做成：" + obsSkip
                             + "。下次让对方先打开原厂控制中心、切一次性能档或键盘灯，再点「一键采集」。");
            else if (obsKeys.Count > 0)
                notes.Add($"观察原厂抓到 {obsCmds} 条写命令，据此在 caps 里认定：{string.Join("、", obsKeys)}。"
                          + "这些来自真实抓包、不是猜的，但「分区数 / 风扇路数 / 档位映射」仍建议按原厂界面复核。");
            else if (obsCmds > 0)
                notes.Add($"观察原厂抓到 {obsCmds} 条写命令，但没有能据此确定的 caps 项（可能只切了性能档）。");
            else
                warnings.Add("这次采集带了「观察原厂」，但一条写命令都没抓到：多半是没去原厂里切档。"
                             + "caps 只能维持通用档案的默认值。");
        }

        warnings.Add(obs is not null
            ? "caps 的写入项（键盘灯 / 显卡切换 / 风扇）部分来自「观察原厂」抓包，"
              + "未观察到的项仍按通用档案：开写之前仍按 docs/DEVICES.md 第三步逐项确认语义。"
            : "caps 是照抄通用档案的：风扇路数 / MUX / RGB 分区都没验证。"
              + "只读展示不受影响；开写之前必须按 docs/DEVICES.md 第三步逐项确认。");

        // ---------------------------------------------------------------- 6.5 写入证据
        //
        // 2026-11-08 起「一键采集」自己会把性能档与键盘背光**写进去再读回来**
        // （写前读原值、写完恢复），所以包里第一次有了"写这条路通不通"的实测数据，
        // 不再需要用户先去设置页开总闸自己试。
        //
        // ⚠️ 读回一致只证明"命令被接受且没被重映射"，**证明不了语义对**（哪一档对应哪种功耗、
        //    风扇槽位、RGB 分区）。所以这些结论只进 notes / adopt.import，**绝不据此开写权限**。
        var writeTest = pack["write_test"] as JsonObject;
        var writeLog = writeTest?["log"] as JsonObject;
        var mode = Str(writeTest?["mode"]);
        var kept = (int)(Num(writeLog?["kept"]) ?? 0);
        var okCount = (int)(Num(writeLog?["ok"]) ?? 0);
        var denied = (int)(Num(writeLog?["denied"]) ?? 0);
        var errs = (int)(Num(writeLog?["error"]) ?? 0);

        if (writeTest is null || writeLog is null)
        {
            warnings.Add("包里没有写入记录（旧版控制中心的采集）：命令表在这台机器上"
                         + "能不能写还是空白。要让对方用新版控制中心做一次完整采集。");
        }
        else
        {
            adopt["import"]!["write_test"] = new JsonObject
            {
                ["mode"] = mode.Length > 0 ? mode : "manual",
                ["kept"] = kept,
                ["ok"] = okCount,
                ["denied"] = denied,
                ["error"] = errs,
                ["apmd_now"] = Num(writeTest["apmd_now"]),
                ["perf_key"] = Str(writeTest["perf_key"]),
            };

            var perf = writeTest["perf"] as JsonObject;
            var light = writeTest["backlight"] as JsonObject;
            var perfEffective = Bool(perf?["effective"]);
            var lightEffective = Bool(light?["effective"]);
            var steps = (perf?["steps"] as JsonArray)?.Count ?? 0;
            var foundOffset = Num(light?["found_offset"]);

            adopt["import"]!["probe"] = new JsonObject
            {
                ["perf_effective"] = perfEffective,
                ["perf_steps"] = steps,
                ["perf_restored"] = perf?["restored"]?.DeepClone(),
                ["backlight_effective"] = lightEffective,
                ["backlight_offset"] = foundOffset is { } off ? JsonValue.Create((int)off) : null,
                ["backlight_restored"] = light?["restored"]?.DeepClone(),
                ["fan"] = "未验证（采集不做风扇写入：写进去 EC 会一直按那个值转）",
            };

            if (mode == "readonly")
            {
                warnings.Add("这份包是**只读采集**：写这条路一个字都没验（对方选了不写 EC）。"
                             + "它能并成「认得出机型 + 读数正确、但写入仍关」的正式档案 —— 读的适配到这就算完；"
                             + "要开写还差一次完整采集（写验证十几秒、采完恢复原档）。"
                             + "**不必每台都试**：写命令是模具级的，同模具任意一台肯试写，"
                             + "补齐一次对整批同项目号的机器都生效（match 按项目号命中同一份档案）。");
            }
            else if (perfEffective)
            {
                notes.Add($"写探针验过 0x0300：{steps} 个 APMD 值逐个写下去都读回一致，"
                          + (Bool(perf?["restored"]) ? "采完已恢复原档" : "**原档没恢复成功**（见包里的 notes）")
                          + " ⇒ 这条写命令在这台机器上确实生效，APMD 也没被再映射一层。"
                          + "但「哪一档对应哪种功耗」仍要按原厂行为确认。");
            }
            else if (perf is not null)
            {
                warnings.Add("写探针试了 0x0300（性能档）但读回对不上："
                             + (Str(perf["skip"]) is { Length: > 0 } ps ? ps
                                : "命令有回帧、APMD 却没变成写进去的值 —— 这台机器多半把 GWD0 又映射了一层，"
                                  + "或者这个子命令号对应的不是 APMD。开写之前必须先搞清楚。"));
            }

            if (lightEffective)
            {
                notes.Add("写探针验过 0x0100（键盘背光）：写入 0/2 档后 status@"
                          + (foundOffset?.ToString(CultureInfo.InvariantCulture) ?? "?")
                          + " 读回一致 ⇒ 背光档位就在这个偏移（档案里的 layout 已按它填）。"
                          + (Bool(light?["restored"]) ? "" : " ⚠️ 原档位没恢复成功。"));
            }
            else if (light is not null && Bool(light["attempted"]))
            {
                warnings.Add("写探针试了 0x0100（键盘背光）但没在 status 帧里对上档位："
                             + "命令可能被 SW 选择子/分区地址吃掉，也可能档位不在 status 里 —— "
                             + "背光页能不能用，要按原厂行为确认。");
            }

            if (okCount == 0 && kept > 0)
                warnings.Add($"采集期一共 {kept} 次写入，一次都没拿到 EC 回帧（被拦 {denied} / 失败 {errs}）："
                             + "先看包里的 error 是管理员 / 开关 / arm 哪一道，解决了再让对方重采。");
        }

        // 风扇那条**永远**没验过：EC 拿到非零 UFA 后会一直按那个值转（写 0 = 停转），
        // 只有重启 / 睡眠唤醒能交回原生自动 —— 采集不能把用户的机器留在那个状态里。
        warnings.Add("风扇写入仍未验证（采集故意不做：写进去 EC 就一直按那个值转）："
                     + "开写之前必须按原厂行为确认 UFA 槽位与档位映射。");

        // ---------------------------------------------------------------- 7. 这次认出了什么
        var guesses = adopt["guesses"] as JsonArray;
        if (guesses is not null)
        {
            var found = new List<string>();
            foreach (var g in guesses)
            {
                if (g is not JsonObject go) continue;
                var conf = Str(go["confidence"]);
                if (conf is not ("high" or "medium")) continue;
                found.Add($"{Str(go["field"])}←{Str(go["cmd"])}@{Num(go["offset"])}[{conf}]");
            }
            notes.Add(found.Count > 0
                ? "采集认出的字段：" + string.Join("、", found)
                : "采集一个字段都没认出来（档案里 layout 为空）。");

            if (pack["app"]?["version"] is { } ver)
                notes.Add($"采集用的是 {Str(pack["app"]?["version"])}（{Str(pack["created"])}）。");
        }

        // HWiNFO 兜底当参照这件事必须转述：不然复核的人会把它当成 EC 自己的读数做循环论证
        if (adopt["self_check"] is JsonArray selfCheck
            && selfCheck.Any(n => Str(n).Contains("来自 HWiNFO", StringComparison.Ordinal)))
            notes.Add("注意：这次采集的 CPU 温度参照来自 HWiNFO（不是 EC 自己的读数）——"
                      + "关系仍然成立，但它属于外部参照。");

        var review = new PackReview(blockers, warnings.Distinct().ToList(), notes);

        // 有阻塞项就不给档案：让人先处理证据，而不是拿一份可能认错机器的 json 去发版
        if (!review.Ready)
            return new PackImportResult(false, null, null, review, matchedBy);

        return new PackImportResult(true, null, profile, review, matchedBy);
    }

    /// <summary>
    /// 从包里取"这次采集出来的 layout"（字段 → (偏移, 长度)）。给诊断/预览用；
    /// 正式档案里已经有同样内容的 <c>ec.layout</c>，不要重复维护。
    /// </summary>
    public static IReadOnlyDictionary<string, (int Offset, int Len)> LayoutOf(JsonObject pack)
    {
        var outMap = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        if (pack["profile_draft"]?["ec"]?["layout"] is not JsonObject layout) return outMap;
        foreach (var (field, node) in layout)
        {
            if (node is JsonArray a && a.Count >= 2)
                outMap[field] = ((int)(Num(a[0]) ?? 0), (int)(Num(a[1]) ?? 4));
        }
        return outMap;
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>档案 id 的合法形状：小写字母/数字/短横线，2~40 字符（`quanta-nl5` 这种）。</summary>
    private static bool IsValidId(string id)
        => id.Length is >= 2 and <= 40
           && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>match 里有没有一条靠项目号的规则（最可靠的那条证据）。</summary>
    private static bool HasProjectCode(JsonObject profile)
    {
        if (profile["match"]?["any"] is not JsonArray blocks) return false;
        foreach (var b in blocks)
            if (b is JsonObject bo && bo.ContainsKey("vendor_registry"))
                return true;
        return false;
    }

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static bool Bool(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<uint>(out var u)) return u;
        return null;
    }
}
