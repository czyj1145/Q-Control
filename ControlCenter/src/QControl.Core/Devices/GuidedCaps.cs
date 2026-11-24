using System.Text.Json.Nodes;

namespace QControl.Core.Devices;

/// <summary>
/// 「这次没观察原厂」时，把上一次草案里**靠观察才拿得到**的那半张纸搬过来。
///
/// 为什么需要（2026-11-17 真机）：采集分两次跑 —— 先没开原厂、再开原厂。
/// 没开原厂那次 `guide == null`，草案照抄 generic 的 caps（`kb_zones=0` / `perf_modes=[]` /
/// 风扇全关），**把上一次带引导采集确认的能力整套抹掉**。用户看到的是"采集成功"，
/// 本机档案却退化了 —— 与 2026-11-15 那次只读采集的降级是同一类问题。
///
/// <para>
/// ⚠️ 2026-11-22 改成**逐键取强**（以前是整块替换 caps）。整块替换有两个反例：
/// ① 上一次带引导的采集只确认了显卡（用户没在原厂里逐档切），那份 caps 里
///    `perf_modes=[]`、`kb_zones=0` —— 搬到这一次，等于拿一份残表覆盖一份好表；
/// ② 再下一次采集继续"沿用"这份残表，错误就一直传下去（本机 19:04 → 19:06 → 22:53 三次）。
/// 取强之后，"确认过"的东西只会被更强的结论覆盖，不会被"这次没看到"抹掉。
/// 这也和 `MachineAdopt` 里 `kb_zones` / 风扇路数「只许升、不许降」是同一条规矩。
/// </para>
///
/// 一律 `DeepClone`：JsonNode 单个父节点，直接搬会抛 "node already has a parent"。
/// </summary>
public static class GuidedCaps
{
    /// <summary>
    /// 「这次没开原厂」的继承：把旧草案里更强的东西搬过来。
    /// 返回 true = 真的搬了（调用方要把它写进回执/报告）。
    ///
    /// <para>
    /// ⚠️ 2026-11-22 23:35 去掉了"旧档案必须带过引导结论"这道门（以前是
    /// `adopt.guide.findings` 里有 value 才搬）。它挡住过两次真实的数据丢失：
    /// 本机 23:20 修补进草案的 `presets_overrides`（三档 APMD 映射）在 23:35 那次采集
    /// 里整块消失 —— 因为那份草案的 caps/presets 是 `OemObservation` 与手改出来的，
    /// 根本没有 `adopt.guide` 段。
    /// </para>
    /// <para>
    /// 门去掉是安全的：<see cref="Merge"/> 本身只动已知键、且**只升不降** ——
    /// 旧档案里没有更强的东西时它一个字节都不写，返回空表即等同"没搬"。
    /// 真正的防线在那份函数里，不在这道门上。
    /// </para>
    /// </summary>
    public static bool Inherit(JsonObject profile, JsonObject? old)
        => Merge(profile, old).Count > 0;

    /// <summary>
    /// 把旧草案里**比当前草案更强**的能力并进来，返回被保留下来的项（人话名，空表 = 没动）。
    ///
    /// <para>
    /// 与 <see cref="Inherit"/> 分开：这条路径不要求旧草案带过引导 —— 引导**跑过但没走完**时
    /// （只确认了显卡），本次结论会把没走到的项显式写成 false，那也需要拿上一次的结论兜住。
    /// 调用方：`guide == null` 走 <see cref="Inherit"/>，`guide != null` 直接走这里。
    /// </para>
    ///
    /// <para>
    /// 只认这几样，别的一概不搬：`caps`（这台能不能做这件事的契约表）、
    /// `presets_overrides`（三档 APMD 映射 / 背光分区表）、`ec.layout` 里本次缺的偏移、
    /// 旧档案声明过而本次没抓到的 `ec.fan_slots`。
    /// 其余键——尤其是 `verified` / `writes_allowed`——永远以本次为准。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Merge(JsonObject profile, JsonObject? old)
    {
        var kept = new List<string>();
        if (old is null) return kept;

        if (old["caps"] is JsonObject oldCaps) MergeCaps(profile, oldCaps, kept);
        if (old["presets_overrides"] is JsonObject oldPresets) MergePresets(profile, oldPresets, kept);
        MergeLayout(profile, old, kept);

        // 原厂实际写的风扇槽位也只在这条路上出现；没有它就退回 generic 的 d0/a/d2，
        // 而别的模具可能根本不是这几个字节。本次抓到了就以本次为准。
        if (old["ec"]?["fan_slots"] is JsonArray slots && profile["ec"] is JsonObject ec
            && ec["fan_slots"] is null)
        {
            ec["fan_slots"] = slots.DeepClone();
            // 搬了就要记进 kept：报告靠它说"本次没确认到的项沿用上次的"，
            // 漏记等于档案里静默带着上一轮的结论、回执一个字节都不提（2026-11-23 审查）。
            kept.Add("风扇槽位");
        }

        return kept;
    }

    /// <summary>caps 逐键取强：三档取并集、分区数/路数取大、开关取或。</summary>
    private static void MergeCaps(JsonObject profile, JsonObject oldCaps, List<string> kept)
    {
        if (profile["caps"] is not JsonObject caps)
            profile["caps"] = caps = new JsonObject();

        // 性能三档：并集。凑不齐三档时 `caps.perf_modes` 本来就不该给出（AdoptGuide 的规矩），
        // 但"上次三档齐、这次一档没抓到"必须保住上次那份 —— 界面三个按钮点下去写的是
        // `presets.perf.modes[*].apmd`，那份映射也在下面按块补回来。
        var curModes = Modes(caps["perf_modes"]);
        var oldModes = Modes(oldCaps["perf_modes"]);
        var union = Order(curModes.Concat(oldModes));
        if (!Same(curModes, union))
        {
            var arr = new JsonArray();
            foreach (var m in union) arr.Add(m);
            caps["perf_modes"] = arr;
            if (oldModes.Except(curModes).Any())
                kept.Add(union.Count >= 3 ? "性能三档" : "性能档 " + string.Join("/", oldModes.Except(curModes)));
        }

        var curZones = Int(caps["kb_zones"]);
        var oldZones = Int(oldCaps["kb_zones"]);
        var zonesKept = oldZones > curZones;
        if (zonesKept)
        {
            caps["kb_zones"] = oldZones;
            kept.Add($"键盘背光 {oldZones} 区");
        }

        var curFans = Int(caps["fan_count"]);
        var oldFans = Int(oldCaps["fan_count"]);
        if (oldFans > curFans)
        {
            caps["fan_count"] = oldFans;
            kept.Add($"{oldFans} 路风扇");
        }

        (string Key, string Label)[] switches =
        [
            ("keyboard_rgb", "键盘背光"),
            ("fan_manual", "手动风扇"),
            ("fan_curve", "风扇曲线"),
            ("gfx_switch", "显卡模式"),
        ];
        foreach (var (key, label) in switches)
        {
            if (Device.Truthy(caps[key])) continue;
            if (!Device.Truthy(oldCaps[key])) continue;
            caps[key] = true;
            // 键盘两区/三区已经由上面那条记过了，别在清单里重复出现
            if (key != "keyboard_rgb" || !zonesKept) kept.Add(label);
        }
    }

    /// <summary>
    /// 本次没认出来的布局偏移，用旧档案补齐（**只补缺失键**，本次认出的永不覆盖）。
    ///
    /// <para>
    /// 为什么敢补：同一台机器、同一固件，偏移不会变；旧档案里那一项本来就是它在用的值。
    /// 不补的后果是"越采越少" —— 2026-11-22 23:35 那次背光写验证没在 status 帧里对上档位，
    /// `layout` 里就少了 `backlight_level`，档案比上一份更差，而机器一个字节都没变。
    /// </para>
    /// <para>
    /// 补进来的值必须留痕：layout 是"实测标定"的证据表，混进旧值要能一眼查到来历。
    /// </para>
    /// </summary>
    private static void MergeLayout(JsonObject profile, JsonObject old, List<string> kept)
    {
        if (profile["ec"] is not JsonObject ec) return;

        KeepMissing("layout", "读数偏移");
        KeepMissing("sys_layout", "SYS 路偏移");
        return;

        void KeepMissing(string key, string label)
        {
            if (old["ec"]?[key] is not JsonObject oldMap || oldMap.Count == 0) return;

            var added = 0;
            if (ec[key] is not JsonObject cur)
            {
                ec[key] = oldMap.DeepClone();
                added = oldMap.Count;
            }
            else
            {
                foreach (var (k, v) in oldMap)
                {
                    if (cur[k] is not null) continue;
                    cur[k] = v?.DeepClone();
                    added++;
                }
            }
            if (added == 0) return;

            // 拿的是旧档案里"已标定"的表 ⇒ 标定状态跟着走，别让 layout 有值却报"未标定"
            if (Device.Truthy(old["ec"]?["layout_verified"])) ec["layout_verified"] = true;
            kept.Add(label);
            if (profile["notes"] is JsonArray notes)
                notes.Add($"{label}：本次没认出来的 {added} 项沿用上一份档案（同一台机器偏移不会变），"
                          + "出处是上一份草案，复核时对着它的采集包看。");
        }
    }

    /// <summary>
    /// 按**最终的** caps 校正「这次没确认到」的说法。
    ///
    /// <para>
    /// `AdoptGuide` 只看得见这一轮观察，而 caps 是跟旧档案取强合并出来的。引导五步
    /// 全被标成「这台没有这项」时，它会写"性能档一档都没抓到 ⇒ 性能页保持关闭"，
    /// 而同一份报告的能力表里三档是齐的、正文还写着"本次确认的能力：性能三档" ——
    /// 两句话在同一屏里打架（2026-11-23 真机）。
    /// </para>
    /// <para>
    /// 校正只改说法、不改证据：缺什么照写，只是把"页面会关"换成"档案里本来就是开的"。
    /// 真正该关的（caps 里也是 0/false）一个字都不动。
    /// </para>
    /// </summary>
    public static string ReconcileMissing(string m, JsonObject? caps)
    {
        if (caps is null) return m;

        if (m.StartsWith("性能档", StringComparison.Ordinal)
            && caps["perf_modes"] is JsonArray modes && modes.Count >= 3)
            return "性能档这次没在原厂里逐档切（引导里没走到那几步）；档案里三档 APMD 是齐的 "
                   + "⇒ 性能页照常开，只差一次逐档复核";

        // "超过界面能画的上限"那条是真问题（可能把 logo 灯算成键盘分区），不校正
        var zones = Int(caps["kb_zones"]);
        if (m.StartsWith("键盘背光", StringComparison.Ordinal)
            && !m.Contains("超过界面能画的上限", StringComparison.Ordinal)
            && zones > 0)
            return $"键盘背光这次没在原厂里点到（引导里没走到）；档案里是 {zones} 区 "
                   + "⇒ 背光页照常开，只差一次点色复核";

        if (m.StartsWith("风扇", StringComparison.Ordinal) && Device.Truthy(caps["fan_manual"]))
            return "风扇这次没在原厂里拉过（引导里没走到）；档案里手动风扇与曲线是开的 "
                   + "⇒ 照常开，只差一次现场复核";

        return m;
    }

    /// <summary>presets 块按块补齐：本次没确认到的整块（背光分区表）搬旧的；
    /// 性能档按档补 —— 这次只确认了一档、上次三档齐时，缺的两档从上次来。
    /// </summary>
    private static void MergePresets(JsonObject profile, JsonObject oldPresets, List<string> kept)
    {
        if (profile["presets_overrides"] is not JsonObject presets)
        {
            profile["presets_overrides"] = oldPresets.DeepClone();
            NotePresets(kept, oldPresets);
            return;
        }

        if (oldPresets["perf"]?["modes"] is JsonObject oldModes)
        {
            if (presets["perf"] is not JsonObject perf)
                presets["perf"] = perf = new JsonObject();
            if (perf["modes"] is not JsonObject modes)
                perf["modes"] = modes = new JsonObject();

            var added = false;
            foreach (var (k, v) in oldModes)
            {
                if (modes[k] is not null) continue;
                modes[k] = v?.DeepClone();
                added = true;
            }
            if (added && !kept.Contains("性能档 APMD 映射")) kept.Add("性能档 APMD 映射");
        }

        foreach (var (k, v) in oldPresets)
        {
            if (k == "perf" || presets[k] is not null) continue;
            presets[k] = v?.DeepClone();
            kept.Add(k == "backlight" ? "背光分区表" : k);
        }
    }

    private static void NotePresets(List<string> kept, JsonObject presets)
    {
        if (presets["perf"] is not null && !kept.Contains("性能档 APMD 映射")) kept.Add("性能档 APMD 映射");
        if (presets["backlight"] is not null) kept.Add("背光分区表");
    }

    /// <summary>`perf_modes` 取出有序模式名（丢掉空串/非字符串，别把脏值当档位）。</summary>
    private static List<string> Modes(JsonNode? node)
    {
        var list = new List<string>();
        if (node is not JsonArray arr) return list;
        foreach (var n in arr)
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 && !list.Contains(s))
                list.Add(s);
        return list;
    }

    /// <summary>按 office → game → beast 排序（与界面三按钮同序），未知模式排在后面。</summary>
    private static List<string> Order(IEnumerable<string> modes)
    {
        string[] known = ["office", "game", "beast"];
        return modes.Distinct()
                    .OrderBy(m => Array.IndexOf(known, m) is var i && i >= 0 ? i : 99)
                    .ToList();
    }

    private static bool Same(List<string> a, List<string> b)
        => a.Count == b.Count && a.SequenceEqual(b, StringComparer.Ordinal);

    private static int Int(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), out var p)) return p;
        }
        return 0;
    }
}
