using System.Text.Json.Nodes;

namespace QControl.Core.Devices;

/// <summary>引导步骤要读哪一条写命令。</summary>
public enum GuideStepKind
{
    PerfOffice,
    PerfGame,
    PerfBeast,
    Keyboard,
    Fan,
}

/// <summary>
/// 一步引导：界面照着念，推断层照着读。
///
/// <see cref="Hint"/> 是**给用户的操作说明**，必须写得能照做（"在原厂里切到最省电那档"），
/// 不能写成"切换性能模式"这种他自己也得猜的话 —— 这一步的全部价值就是"用户做对了"。
/// </summary>
public sealed record GuideStep(string Id, string Title, string Hint, GuideStepKind Kind, string? ModeKey);

/// <summary>用户点的一次"这一步做完了"（<see cref="Skipped"/> = 这台机器没这项）。</summary>
public sealed record GuideMark(string Step, DateTimeOffset At, bool Skipped = false);

/// <summary>一步的结论：抓到了几条、读出了什么、为什么没成。</summary>
public sealed record GuideFinding(
    string Step,
    string Title,
    bool Marked,
    bool Skipped,
    int Frames,
    string? Value,
    string Why);

/// <summary>
/// 引导结论：<see cref="Caps"/> 进档案的 caps，<see cref="Presets"/> 进 model.json
/// （APMD 映射与键盘分区地址都住在配置里，不在档案里）。
/// </summary>
public sealed record GuideResult(
    JsonObject Caps,
    JsonObject Presets,
    JsonArray Findings,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Missing,
    JsonObject Evidence)
{
    /// <summary>
    /// 这次引导到底确认了东西没有（界面据此决定要不要摆「启用本机」）。
    ///
    /// ⚠️ 显卡那条**不算**：它恒开（2026-11-22 起默认认为能切，程序验不了），
    ///    拿它当"确认到了能力"，会让一次什么都没做成的采集也摆出「启用本机」。
    /// </summary>
    public bool AnyConfirmed => Findings.Any(f => f is JsonObject o
                                                  && o["value"] is not null
                                                  && !(o["step"] is JsonValue sv
                                                       && sv.TryGetValue<string>(out var s)
                                                       && s == AdoptGuide.GfxStep));
}

/// <summary>
/// 显卡模式的**读侧证据**（纯读，不写 EC、不重启）。
///
/// 为什么要它：原厂切显卡模式必须重启，观察会话里抓不到 `0x0203` 写字，
/// 而"这台有没有显卡切换"程序本来也验不了（2026-11-22 起不再问用户，按默认开启处理）；
/// 读数是**佐证** —— 独显直连时固件会把核显整个藏起来，读到 2 且系统里没有核显，
/// 说明那个值确实在生效。
/// ⚠️ 反过来不成立：读到 1（混合）既可能是"混合输出"，也可能是"这台根本没有 MUX"。
/// 所以它只进报告和 notes，不参与 `caps.gfx_switch` 的判定。
/// </summary>
/// <param name="Mode">`0x0203` 读到的当前值（DAT2）；null = 没读到。</param>
/// <param name="ReadOk">这次读有没有成功（EC 应答了）。</param>
/// <param name="Stable">两次读数一致（不一致 = 缓冲被抢 / 正被原厂改）。</param>
/// <param name="HasIgpu">系统里看得见核显。</param>
/// <param name="HasDgpu">系统里看得见独显。</param>
public readonly record struct GfxRead(
    int? Mode,
    bool ReadOk,
    bool Stable,
    bool HasIgpu,
    bool HasDgpu);

/// <summary>
/// 「引导适配」：把原厂写命令按**用户点的那几下**切成段，每段贴一个语义标签。
///
/// 为什么要有这一步：观察原厂本来就能抓到 0x0300 / 0x0100 / 0x0205 / 0x0203，
/// 但抓到的只是"值"—— 知道这台机器会写 APMD=1，不知道 1 是游戏还是狂暴。
/// 一旦用户是**照着界面的步骤**做的（"现在切到办公"→"好了"），这段时间窗里的值
/// 就自带标签，语义不用猜。这是"一键采集"和"完全适配"之间唯一缺的那块。
///
/// 三条纪律，与采集侧同源：
///   · **只认定证据**：没抓到的项一个都不填，写进 <see cref="Missing"/> 说清缺什么；
///   · **不拿默认值凑数**：三档性能没凑齐就不开性能模式 —— 少一档就不知道另一档的
///     APMD，界面那三个按钮点下去会写错值（"游戏模式功耗比办公还低"那类 bug）；
///   · **风扇最保守**：只有观察到的手动转速槽位和通用档案一致（d0/a/d2）才开手动风扇，
///     槽位不一样时宁可不开 —— 写错槽位是"左风扇满转、右风扇不动"，肉眼很难归因。
///
/// **显卡为什么不排一步**：原厂切显卡模式要重启，观察会话到不了那一步。
/// 这一项程序验不了，2026-11-22 起**不再问用户，按"能切"默认开启**（写档案时注明未逐档验证）；
/// 观察里真抓到 `0x0203` 写字仍算佐证，读数与显示拓扑只写进 notes。
/// </summary>
public static class AdoptGuide
{
    /// <summary>
    /// 引导步骤。顺序 = 弹窗顺序 = 标记顺序，推断层按这个顺序切时间窗。
    ///
    /// 性能三档故意拆成三步（而不是"切一圈"）：只有一步一问，才能把"哪个值是哪一档"
    /// 绑到标签上；合成一步就只能拿到一个无序的值集合，等于白抓。
    ///
    /// 文案一律短到能一眼照做：用户这时正盯着**原厂**的窗口，弹窗是余光看的。
    /// 风扇按真实模具写：很多广达机型压根没有手动拉条，只有 Auto / Max 快捷键。
    /// </summary>
    public static readonly IReadOnlyList<GuideStep> Steps =
    [
        new("perf_office", "办公 / 静音档",
            "在原厂里切到最省电那档",
            GuideStepKind.PerfOffice, "office"),
        new("perf_game", "游戏 / 性能档",
            "切到中间那档",
            GuideStepKind.PerfGame, "game"),
        new("perf_beast", "狂暴 / 最强档",
            "切到最强那档",
            GuideStepKind.PerfBeast, "beast"),
        new("keyboard", "键盘背光",
            "换个颜色；分区键盘把 A/B/C 各改一次",
            GuideStepKind.Keyboard, null),
        new("fan", "风扇",
            "有手动拉条就拉到 40% 停五六秒再回自动；只有快捷键就切 Max，等风扇明显响起来（约 5 秒）再切回 Auto",
            GuideStepKind.Fan, null),
    ];

    /// <summary>显卡那条 finding 的 step 标识（它没有引导步骤，但结论要单独一行摆出来）。</summary>
    public const string GfxStep = "gfx";

    /// <summary>通用档案的风扇槽位（与 `quanta-gmwmi-generic.json` 的 `ec.fan_slots` 一致）。</summary>
    private static readonly string[] GenericFanSlots = ["d0", "a", "d2"];

    /// <summary>
    /// 背光最多认到几区。超过就不再开背光页：界面 tab 挤不下，而且更可能是把
    /// logo 灯 / 灯条这类"非键盘分区"的地址也算进来了。
    ///
    /// ⚠️ 现实里**没有五分区键盘**（2026-11-17 用户：「没有五分区的电脑呀，四分区和二分区也很少」）：
    ///    常见是 3 区，整块 1 区；2 / 4 区极罕见。所以上限是 4 ——
    ///    以前写 6 是照调色板的颜色数排的，结果探针往 7/8 试两下就能画出"五分区"，
    ///    那不是能力、是笑话。数字与 <see cref="BacklightZones.MaxSupported"/> 同一个源。
    /// </summary>
    public const int MaxZones = BacklightZones.MaxSupported;

    /// <summary>
    /// 分区默认配色（红 / 绿 / 蓝 / 黄）。只在第一次写进 presets 时用，
    /// 之后以用户在灯效页调的颜色为准 —— 它不是"原厂默认色"，别拿它当证据。
    /// 只配到 <see cref="MaxZones"/> 那么多：多出来的颜色等于给不存在的分区留位置。
    /// </summary>
    private static readonly int[][] ZonePalette =
    [
        [255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 200, 0],
    ];

    /// <summary>
    /// 标记之后多久之内的帧还算这一步的。
    ///
    /// 原厂有的实现是"点完设置过几百毫秒才下发"，用户看到界面变了才点"好了"，
    /// 帧可能刚好落在标记后面。给 1.5 秒宽限，而且**只在这段一条都没抓到时才用** ——
    /// 否则会把下一步的帧抢过来，反而把语义绑反。
    /// </summary>
    private static readonly TimeSpan MarkGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>字节槽位名（偏移 8..11 = a/b/c/d，见 <c>EcFrame</c> 的布局注释）。</summary>
    private static readonly (int Off, string Name)[] ByteSlots =
        [(8, "a"), (9, "b"), (10, "c"), (11, "d")];

    // ================================================================ 分析

    /// <summary>
    /// 按引导标记切窗、逐项认定。
    /// </summary>
    /// <param name="writes">本次观察抓到的写命令（带时间戳，见 <see cref="ObservedWrite.At"/>）。</param>
    /// <param name="marks">用户点的标记（顺序即步骤顺序）。</param>
    /// <param name="start">观察开始时间 —— 第一个标记之前抓到的帧算第一步的。</param>
    /// <param name="gfxRead">读数与显示拓扑（佐证，见 <see cref="GfxRead"/>）；null = 没读到。</param>
    /// <param name="knownZoneAddrs">
    /// 这台机器**已经确认过**的键盘分区地址（配置里的 `presets.backlight.zones`）。
    ///
    /// 为什么需要它（2026-11-16 真机）：原厂改键盘颜色时会把 logo 灯 / 灯条一起写一条
    /// `0x0100`（本机 NL5 的 7/8 就是），引导只能数"帧里出现了哪些地址"，分不出谁是键盘 ——
    /// 于是三区键盘被算成五区，配置里多出两个 tab。判据只在这几个地址**这次全都出现**时生效：
    /// 那说明观察确实覆盖了全部分区，多出来的就不是键盘；反过来（用户只点了一个区）
    /// 一个都不裁，免得把四区键盘永远锁在三区。
    /// </param>
    public static GuideResult Analyze(
        IEnumerable<ObservedWrite> writes,
        IReadOnlyList<GuideMark> marks,
        DateTimeOffset start,
        GfxRead? gfxRead = null,
        IReadOnlyList<int>? knownZoneAddrs = null)
    {
        var all = writes.Where(w => w.Gwf0 == 0xFB00).OrderBy(w => w.At).ToList();
        var ordered = marks.OrderBy(m => m.At).ToList();
        var claimed = new HashSet<ObservedWrite>();

        var findings = new List<GuideFinding>();
        var notes = new List<string>();
        var missing = new List<string>();
        // 逐帧原始证据：结论（APMD/地址/槽位）之外，把每步那几条 32 字节报文原样留下。
        // 作者要补的东西（0x0300 除了 d0 还带不带别的字节、背光的灯效位、风扇的完整槽位向量）
        // 全在这几帧里 —— 只给结论等于让作者再猜一遍。
        var evidence = new JsonObject();

        var perf = new Dictionary<string, int>(StringComparer.Ordinal);
        var kbAddrs = new SortedSet<int>();
        var kbIgnoredAddrs = new SortedSet<int>();
        var fanSlots = new SortedSet<string>(StringComparer.Ordinal);
        var sawFan = false;
        var sawOmcc = all.Any(w => w.Gwf1 == OemObservation.SubOmcc);

        // 显卡模式不排引导步骤（原厂切换要重启，会话到不了那一步）。整轮观察里只看
        // 有没有 0x0203、值落在 1~3 的收进来当佐证（能力本身默认开，见下面那一段）。
        var gfxValues = new SortedSet<int>();
        foreach (var w in all.Where(w => w.Gwf1 == OemObservation.SubGfx))
        {
            var v = ByteOf(w, 4);
            if (v is >= 1 and <= 3) gfxValues.Add(v);
        }

        // ⚠️ 一个标记都没有（引导被跳过 / 用户没点）时**一步都不推断**：那会把五步全写成
        //    "没走到"、caps 里全部显式 false，等于用"没观察"覆盖 OemObservation 已经认到的东西。
        //    显卡那一条不受影响（它在循环外、恒开），不会被写成"没确认"。
        for (var i = 0; marks.Count > 0 && i < Steps.Count; i++)
        {
            var step = Steps[i];
            var mark = ordered.FirstOrDefault(m => m.Step == step.Id);
            if (mark is null)
            {
                findings.Add(new GuideFinding(step.Id, step.Title, false, false, 0, null,
                    "观察里没走到这一步（提前结束了）"));
                continue;
            }

            // 用户标了「这台没有这项」：这一段里的帧一条都不用 —— 那多半是他随手点到的
            // 残帧，拿来当证据就是往档案里塞假数据。
            if (mark.Skipped)
            {
                findings.Add(new GuideFinding(step.Id, step.Title, true, true, 0, null,
                    "你标了「这台没有这项」"));
                notes.Add($"{step.Title}：标了「这台没有这项」——已按没有处理。");
                continue;
            }

            // 窗口 = 上一步的标记 ~ 这一步的标记。第一个窗口从观察开始算起。
            var prev = start;
            var idx = ordered.IndexOf(mark);
            if (idx > 0) prev = ordered[idx - 1].At;

            var frames = all.Where(w => w.At > prev && w.At <= mark.At && !claimed.Contains(w)).ToList();
            if (!frames.Any(w => KindOf(step.Kind) == w.Gwf1))
            {
                // 宽限：原厂延迟下发时，帧会落在标记之后（但不能越过下一个标记）
                var next = idx + 1 < ordered.Count ? ordered[idx + 1].At : DateTimeOffset.MaxValue;
                var until = Min(mark.At + MarkGrace, next);
                var late = all.Where(w => w.At > mark.At && w.At <= until
                                          && !claimed.Contains(w)
                                          && KindOf(step.Kind) == w.Gwf1).ToList();
                if (late.Count > 0)
                {
                    // ⚠️ **全都要认领**，不能只认第一条（2026-11-23 审查）：
                    //    剩下那条会落进下一步的窗口，下一步就把它当成"自己这一步抓到的"——
                    //    典型形态是上一步迟发两条 0x0300、下一步用户没切档却点了完成，
                    //    于是 perf[下一步的档] 记成上一步的 APMD（写下去就是错的功耗映射）。
                    //    宁可下一步判"没抓到"（缺档），也不能认错。
                    foreach (var f in late) claimed.Add(f);
                    frames.AddRange(late);
                    notes.Add($"{step.Title}：写帧出现在点了「完成」之后 {Math.Max(0, (late[0].At - mark.At).TotalMilliseconds):0} 毫秒，"
                              + "按宽限算进这一步（原厂下发有延迟）。");
                }
            }

            var (value, why, count) = Read(step, frames, perf, kbAddrs, kbIgnoredAddrs, fanSlots, ref sawFan);
            CollectEvidence(step, frames, evidence);
            findings.Add(new GuideFinding(step.Id, step.Title, true, false, count, value, why));
        }

        // ---------------- caps ----------------
        var caps = new JsonObject();

        // 性能三档：必须齐。少一档就不知道另一档的 APMD 是哪个值，
        // 界面上那三个按钮点下去会写错 —— 宁可不给。
        var perfOk = perf.Count == 3;
        var perfModes = new JsonArray();
        if (perfOk)
        {
            perfModes.Add("office");
            perfModes.Add("game");
            perfModes.Add("beast");
        }
        caps["perf_modes"] = perfModes;
        if (!perfOk)
        {
            var have = string.Join("、", perf.Keys.Select(LabelOf));
            missing.Add(perf.Count == 0
                ? "性能档一档都没抓到（原厂没在观察里切档，或这台没有性能模式）⇒ 性能页保持关闭"
                : $"性能档只确认了 {have}，三档没凑齐 ⇒ 性能页保持关闭（凑齐三档才敢写 APMD）");
        }

        // 键盘：分区数 = 抓到的地址个数（1~MaxZones）。
        // 2026-11-14 起界面按 `caps.kb_zones` 画 N 个 tab，单区 / 双区 / 四区键盘也能开背光页 ——
        // 以前死要三个，于是那些机型这一页永远是关的，包里写再多地址也没用。
        // 超过上限的不开：tab 挤不下，且更像把 logo 灯 / 灯条也算成了键盘分区。
        //
        // ⚠️ 观察里确实会混进 logo 灯 / 灯条：原厂改键盘颜色时把它们一起写（本机 NL5 的 7/8）。
        //    配置里已有确认过的地址、且这些地址这次**全都出现**时，只认那几个（见 knownZoneAddrs）。
        var zoneAddrs = new SortedSet<int>(kbAddrs);
        var extraAddrs = new List<int>();
        // 只确认了一部分已知分区（用户这次只点了一个区）：分区数按已知的保留，
        // 但**不改 zoneAddrs** —— 完整表与配色由调用方跟配置合并（颜色以用户当前配置为准）。
        // 这里只负责别把话说错：不能一边说"1 区键盘"、一边档案里躺着 3 区。
        var partialKnown = false;
        if (knownZoneAddrs is { Count: > 1 } known && kbAddrs.Count > 0)
        {
            var outside = kbAddrs.Where(a => !known.Contains(a)).ToList();
            var hit = kbAddrs.Count(known.Contains);

            if (known.All(kbAddrs.Contains))
            {
                extraAddrs = outside;
                if (extraAddrs.Count > 0)
                {
                    zoneAddrs = new SortedSet<int>(known);
                    notes.Add($"观察里还有地址 {string.Join("/", extraAddrs)}（它们跟着键盘一起变色），"
                              + "但配置里已确认的分区是 " + string.Join("/", known) + " 且这次全都出现了 ⇒ "
                              + "那几个按 logo 灯 / 灯条处理，没算进键盘分区"
                              + "（要改判就手工把它们加进 presets.backlight.zones）。");
                }
            }
            else if (hit >= 2 && outside.Count > 0)
            {
                // 已知分区里这次确认到两个以上，但还混进表外地址 —— 原厂改键盘颜色时
                // 会把 logo 灯 / 灯条一起写（本机 NL5 的 7/8），而用户这次偏偏没点 A 区。
                // 判据：**能对上一半以上的已知分区**，就说明这台的分区表就是已知那套，
                // 表外地址按灯珠处理。2026-11-23 12:28 真机：抓到 3/4/7/8 ⇒ 被判成 4 区，
                // 4 个 tab 里两个是 logo 灯，还写进了用户配置。
                zoneAddrs = new SortedSet<int>(known);
                notes.Add($"观察里还有地址 {string.Join("/", outside)}（它们跟着键盘一起变色），"
                          + "配置里已确认的分区是 " + string.Join("/", known)
                          + $"，这次确认到其中 {hit} 个 ⇒ 那几个按 logo 灯 / 灯条处理，没算进键盘分区"
                          + "（要改判就手工把它们加进 presets.backlight.zones）。");
            }
            else if (kbAddrs.IsSubsetOf(known))
            {
                // 说明由调用方补（MachineAdopt 3.57：它同时负责把 caps.kb_zones 抬回已知数），
                // 这里只负责**别写错话**：不能一边说"1 区键盘"、一边档案里躺着 3 区。
                partialKnown = true;
            }
        }

        var kbOk = zoneAddrs.Count is >= 1 and <= MaxZones;
        caps["keyboard_rgb"] = kbOk;
        caps["kb_zones"] = kbOk ? zoneAddrs.Count : 0;
        if (zoneAddrs.Count > MaxZones)
            missing.Add($"键盘背光抓到 {zoneAddrs.Count} 个分区地址（{string.Join("/", zoneAddrs)}），"
                        + $"超过界面能画的上限 {MaxZones} ⇒ 先不开背光页"
                        + "（多半把 logo 灯 / 灯条也算成了键盘分区，需要作者核对）");
        else if (zoneAddrs.Count == 0)
            missing.Add("键盘背光没抓到分区地址 ⇒ 背光页保持关闭");
        // ⚠️ 只确认了一部分时别写"N 区键盘"这句：那次数是**这次的观察**，不是最终分区数，
        //    写出来会和 caps.kb_zones / presets 里的 tab 数打架（2026-11-23 00:04 真机）。
        else if (zoneAddrs.Count != 3 && !partialKnown)
            notes.Add($"{zoneAddrs.Count} 区键盘：背光页会画 {zoneAddrs.Count} 个 tab（A/B/C… 按地址降序）。"
                      + "示意图里的分区边界按色彩数均分，是**示意**、不是实测位置 —— "
                      + "原厂只下发了地址，物理分区边界得看实机亮起来的样子。");

        // findings 里那行「[已确认] 键盘背光」的 value 是**原始观察地址**，而 zoneAddrs
        // 可能已经按已知表裁过（跟键盘一起变色的 7/8 是 logo 灯）。2026-11-23 21:37 真机：
        // 正文印 "分区地址 3/5/7/8"，同一屏又写 "本次确认的能力：键盘背光 3 区" ——
        // 读者只能问"到底几个"。值必须跟结论同一个口径，原始地址留在 notes / evidence 里。
        RewriteKeyboardFinding(findings, kbAddrs, zoneAddrs, partialKnown, knownZoneAddrs);

        if (kbIgnoredAddrs.Count > 0)
            notes.Add($"另有 {kbIgnoredAddrs.Count} 个 0x0100 地址（{string.Join("/", kbIgnoredAddrs)}）"
                      + "只在熄灯/初始化帧里出现（亮度 0、三通道同值），按非键盘分区跳过；"
                      + "完整地址仍留在 observation.summary.kb_zone_addrs 里给作者复核。");

        // 风扇：观察到的槽位必须**落在**通用档案的槽位表里（d0/a/d2）才开。
        //
        // ⚠️ 比"子集"，不是比"相等"：观察只统计真写了转速的槽位（1..100），原厂在
        // "这一路不用"时写 0xFF —— 那几路根本不会出现在 fanSlots 里。两风扇机上
        // 原厂 Max 帧就是 `d0=100 a=100 d2=255`，抓出来只有 [a/d0]；按相等判，
        // 这台机器每跑一次带观察的采集都会被判成"槽位不一致"，把手动风扇/曲线从开
        // 改成关（2026-11-22 23:52 真机）。
        //
        // 反过来，"槽位里出现了档案没有的字节"才是真不一致（那才是写错位置的风险）。
        var fanOk = sawFan && fanSlots.Count >= 2 && fanSlots.IsSubsetOf(GenericFanSlots);
        caps["fan_manual"] = fanOk;
        caps["fan_curve"] = fanOk;
        if (sawFan && !fanOk)
            missing.Add($"风扇手动档的槽位是 [{SlotsText(fanSlots)}]，和通用档案 "
                        + $"[{string.Join("/", GenericFanSlots)}] 对不上 ⇒ 手动风扇与曲线都先不开"
                        + "（写错槽位会变成「一个风扇满转、另一个不动」，现场很难归因）");
        else if (!sawFan)
            missing.Add("风扇没抓到手动转速命令 ⇒ 手动风扇与曲线保持关闭");
        if (sawOmcc)
            notes.Add("抓到原厂写 0x0206（OMCC / 原厂曲线标志位）：这台有风扇曲线能力。"
                      + (fanOk ? "" : "但槽位没对上，曲线仍然没开。"));
        if (sawFan && !HasIntermediateDuty(all))
            notes.Add("风扇这轮只看到 Auto / Max 这类预设（转速是 0 / 100 / 0xFF，没有中间值）："
                      + "这类原厂没有手动拉条，属正常；程序仍不自己试写风扇。");
        if (HasMaxPreset(all))
            notes.Add("抓到 Max 预设帧（多路同时写 100%）：这台原厂快捷键确实走 EC 0x0205。");
        var duties = all.Where(w => w.Gwf1 == OemObservation.SubFan)
                        .SelectMany(FanDuties)
                        .Where(v => v is >= 1 and <= 99)
                        .Distinct().OrderBy(v => v).ToList();
        if (duties.Count >= 3)
            notes.Add($"另有 {duties.Count} 个不同中间转速（{duties[0]}~{duties[^1]}%）随时间变化："
                      + "这更像原厂自动曲线在周期性下发，不是用户在界面上手动拉条。"
                      + "手动风扇/曲线是按「槽位与通用档案一致」推出来的，不是按手动操作确认的。");

        // ---------------- 显卡模式（默认开；2026-11-22 起不再问用户） ----------------
        // 这一项程序验不了：切换要重启 ⇒ 观察抓不到 0x0203 写字，读寄存器也只能证明
        // "有个值可读"，证明不了"能切"。以前靠采集页问一句（"原厂里有没有显卡模式"），
        // 等于拿用户的回答当证据 —— 2026-11-22 用户定：**问不出来就别问，按能切换处理**。
        // 所以现在恒开，写进 findings 的 value 里注明"未逐档验证"；观察里抓到 0x0203 只当佐证。
        // 真正的防线不在这里：界面上写显卡模式仍要二次确认、重启才由固件应用
        // （`EcHal.SetGfxMode` 那边还按命令表判有没有 0x0203）。
        // 读数与显示拓扑只进 notes：独显直连时固件会把核显藏起来，"读到 1"分不出
        // 混合输出和"没有 MUX"，它改不了结论。
        var gfxReadNote = DescribeGfxRead(gfxRead);
        var gfxValue = gfxValues.Count > 0
            ? $"默认开启（未逐档验证）；观察到原厂写 0x0203，值 {string.Join("/", gfxValues)}"
            : "默认开启（未逐档验证）—— 切换要重启，观察里抓不到 0x0203";
        if (gfxReadNote.Length > 0)
        {
            notes.Add("显卡模式：" + gfxReadNote + "。");
            gfxValue += "；" + gfxReadNote;
        }

        // 显卡也进 findings 清单：它没有引导步骤，没有这一条用户会以为程序漏了这一项。
        findings.Add(new GuideFinding(GfxStep, "显卡模式", true, false, 0, gfxValue,
                                      "程序验不了切档，按默认开启处理"));
        caps["gfx_switch"] = true;

        // ---------------- presets（进 model.json） ----------------
        var presets = new JsonObject();

        // 确认到几档就记几档（少一档也记）：这既是给作者的证据，也避免"重新采一次
        // 从零开始"。**但 caps.perf_modes 只有三档齐了才给** —— 界面那三个按钮
        // 点下去就会按这里的映射写 EC，缺一档就可能写到别的档位上。
        if (perf.Count > 0)
        {
            var modes = new JsonObject();
            foreach (var key in (string[])["office", "game", "beast"])
                if (perf.TryGetValue(key, out var v))
                    modes[key] = new JsonObject { ["apmd"] = v };
            presets["perf"] = new JsonObject { ["modes"] = modes };

            notes.Add(perfOk
                ? "三档 APMD 已按你切的顺序认定："
                  + string.Join(" / ", perf.Select(kv => $"{LabelOf(kv.Key)}={kv.Value}"))
                : "已确认的档位 APMD 已记下（"
                  + string.Join(" / ", perf.Select(kv => $"{LabelOf(kv.Key)}={kv.Value}"))
                  + "），但三档没凑齐 ⇒ 性能页不开。");
        }

        if (kbOk)
        {
            // 地址按降序排 A/B/C… —— 与本机 NL5 的 5/4/3 一致；顺序错了最多亮错区，不会写坏。
            var zones = new JsonArray();
            var n = 0;
            foreach (var addr in zoneAddrs.OrderByDescending(a => a))
            {
                var rgb = ZonePalette[n % ZonePalette.Length];
                zones.Add(new JsonObject
                {
                    ["addr"] = addr,
                    ["name"] = ((char)('A' + n)).ToString(),
                    ["rgb"] = new JsonArray(rgb.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                });
                n++;
            }
            presets["backlight"] = new JsonObject
            {
                ["zones"] = zones,
                // 这些地址是从原厂写帧里读出来的，不是抄的默认值
                ["zone_addrs_confirmed"] = true,
            };
            notes.Add(partialKnown
                // 这一轮只抓到一部分地址：别写成"已认定 4（单区）"，那会和最终 3 区打架
                ? $"这次确认的键盘分区地址：{string.Join("/", kbAddrs)}（其余沿用已知表）。"
                : $"键盘分区地址已按原厂写帧认定：{string.Join("/", zoneAddrs)}"
                  + $"（{(zoneAddrs.Count == 1 ? "单区" : "A/B/C…")} 按地址降序）。");
        }

        if (gfxValues.Count > 0)
            notes.Add("显卡模式：观察到原厂写 0x0203 —— 有显卡切换能力；"
                      + "本轮没按界面逐档确认，值域顺序用之前要对一眼。");

        // 风扇槽位是"写 UFA 时该填哪几个字节"的唯一证据 —— 原样留在包里，
        // 槽位与通用档案不一致时它更是作者改档案的唯一依据（不写进 caps 就等于白抓）。
        if (fanSlots.Count > 0)
            evidence["fan_slots"] = StrArray(fanSlots);

        AddPerfByteNotes(evidence, notes);

        return new GuideResult(caps, presets, ToJson(findings), notes, missing, evidence);
    }

    // ================================================================ 逐帧证据

    /// <summary>
    /// 把这一步窗口里的原始报文收进 <c>evidence</c>。
    ///
    /// 只收**这一步真正关心的那条子命令**，且按内容去重（原厂自动风扇曲线每几秒就刷一帧，
    /// 不去重会让包里出现几十条一模一样的 0x0205）。结论（APMD / 地址 / 槽位）已经有了，
    /// 这里补的是"作者要照着写时所需的原文"：0x0300 除了 d0 还带了什么、背光的灯效位、
    /// 风扇一帧里同时写了哪几路。
    /// </summary>
    private static void CollectEvidence(GuideStep step, List<ObservedWrite> frames, JsonObject evidence)
    {
        var sub = KindOf(step.Kind);
        if (sub < 0) return;

        var hits = frames.Where(w => w.Gwf1 == sub).ToList();
        if (hits.Count == 0) return;

        switch (step.Kind)
        {
            case GuideStepKind.PerfOffice:
            case GuideStepKind.PerfGame:
            case GuideStepKind.PerfBeast:
            {
                var hit = hits[^1];                      // 同一段里写两次取最后一次（用户改主意以最后为准）
                var perf = evidence["perf"] as JsonObject ?? new JsonObject();
                evidence["perf"] = perf;
                perf[step.ModeKey!] = new JsonObject
                {
                    ["apmd"] = ByteOf(hit, 4),
                    ["raw"] = Hex(hit.Raw),
                    ["nonzero"] = NonZero(hit.Raw),
                };
                break;
            }

            case GuideStepKind.Keyboard:
            {
                var arr = evidence["keyboard"] as JsonArray ?? new JsonArray();
                evidence["keyboard"] = arr;
                foreach (var w in hits)
                {
                    var item = new JsonObject
                    {
                        ["addr"] = ByteOf(w, 4),
                        ["b"] = ByteOf(w, 8),
                        ["g"] = ByteOf(w, 9),
                        ["r"] = ByteOf(w, 10),
                        ["effect"] = ByteOf(w, 11) >> 4,
                        ["level"] = ByteOf(w, 11) & 0x0F,
                    };
                    if (!arr.Any(x => x is JsonObject o && Same(o, item))) arr.Add(item);
                }
                break;
            }

            case GuideStepKind.Fan:
            {
                var arr = evidence["fan"] as JsonArray ?? new JsonArray();
                evidence["fan"] = arr;
                foreach (var w in hits)
                {
                    // 一帧里同时写了哪几路：d0 在 4、a/b/c/d 在 8..11、d2 在 12。
                    // 0xFF = 原厂用来表示"这一路不用"，照实记，别当成 255% 的转速。
                    var item = new JsonObject
                    {
                        ["d0"] = ByteOf(w, 4),
                        ["a"] = ByteOf(w, 8),
                        ["b"] = ByteOf(w, 9),
                        ["c"] = ByteOf(w, 10),
                        ["d"] = ByteOf(w, 11),
                        ["d2"] = ByteOf(w, 12),
                        ["raw"] = Hex(w.Raw),
                    };
                    if (!arr.Any(x => x is JsonObject o
                                      && o["raw"]?.ToJsonString() == item["raw"]?.ToJsonString())) arr.Add(item);
                }
                break;
            }
        }
    }

    /// <summary>
    /// 三档的 0x0300 帧除了 d0（APMD）之外还有没有别的字节在跟着变。
    ///
    /// 这一条是给"完全适配"留的路标：我们写性能档只写 d0，如果原厂每档还带一个
    /// 第二参数（配置里那个 `dat2` 就是干这个的），照 d0 一条写下去在有些模具上
    /// 会变成"档位换了但功耗墙没换"。发现这种字节就写进 notes，由作者判定要不要带。
    /// </summary>
    private static void AddPerfByteNotes(JsonObject evidence, List<string> notes)
    {
        if (evidence["perf"] is not JsonObject perf || perf.Count < 2) return;

        var modes = new List<(string Key, JsonObject Frame)>();
        foreach (var (k, v) in perf)
            if (v is JsonObject fo) modes.Add((k, fo));
        if (modes.Count < 2) return;

        var diffs = new List<string>();
        // ⚠️ 从偏移 5 开始：偏移 4 就是 d0 的低字节，也就是 APMD 本身
        //    （`Read` 取的就是 `ByteOf(hit, 4)`）。从 4 开始扫会把它自己报成
        //    "除 d0 之外还有字节随档位变化" —— 2026-11-22 23:52 的报告里就出现了
        //    这条自相矛盾的话（"偏移 4（办公=2/游戏=1/狂暴=0）"），作者会被带偏。
        for (var off = 5; off < 32; off++)
        {
            var vals = new List<int>();
            foreach (var (_, frame) in modes)
                vals.Add(frame["raw"] is JsonValue rv && rv.TryGetValue<string>(out var hex) ? HexByte(hex, off) : 0);

            if (vals.Distinct().Count() <= 1) continue;
            diffs.Add($"偏移 {off}（{string.Join("/", modes.Select((m, i) => $"{LabelOf(m.Key)}={vals[i]}"))}）");
        }

        if (diffs.Count > 0)
            notes.Add("原厂 0x0300 帧里，除 d0（APMD）之外还有字节随档位变化：" + string.Join("；", diffs)
                      + "。程序写性能档只写 d0 —— 若这台机器切档后功耗墙没跟着变，"
                      + "就是这个第二参数没带上（配置里 `dat2` 那一位），需要作者按原厂行为补。");
    }

    /// <summary>从 `"01 02 03 …"` 这种报文串里取第 <paramref name="offset"/> 个字节。</summary>
    private static int HexByte(string hex, int offset)
    {
        var parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return offset >= 0 && offset < parts.Length
               && int.TryParse(parts[offset], System.Globalization.NumberStyles.HexNumber, null, out var b)
            ? b : 0;
    }

    private static string Hex(byte[] raw) => string.Join(' ', raw.Select(b => b.ToString("X2")));

    /// <summary>非零字节 → `{偏移: 值}`。32 字节全摆出来没人看得下去，非零的才是信息。</summary>
    private static JsonObject NonZero(byte[] raw)
    {
        var o = new JsonObject();
        for (var i = 0; i < raw.Length; i++)
            if (raw[i] != 0) o[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = raw[i];
        return o;
    }

    private static bool Same(JsonObject a, JsonObject b)
    {
        foreach (var (k, v) in a)
            if (b[k]?.ToJsonString() != v?.ToJsonString()) return false;
        return true;
    }

    private static JsonArray StrArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    // ================================================================ 显卡读数

    /// <summary>
    /// 把读数与显示拓扑翻成一句**能核对**的话（空串 = 没有读数，一个字都不编）。
    ///
    /// 判据只有一条："固件在独显直连 / 集显输出时会把另一块卡藏起来"。
    /// 所以读到 2 却还看得见核显、读到 3 却还看得见独显，都说明这个值**没在生效**
    /// （刚写完没重启 / 这台没有 MUX / 缓冲残留），只写进备注，不影响 caps。
    /// </summary>
    private static string DescribeGfxRead(GfxRead? read)
    {
        if (read is not { } r) return "";
        if (!r.ReadOk || r.Mode is not { } mode) return "EC 没应答 0x0203（读不到当前模式）";

        var stable = r.Stable ? "" : "（两次读数不一致）";
        var cards = $"核显{(r.HasIgpu ? "在" : "不在")}、独显{(r.HasDgpu ? "在" : "不在")}";

        return mode switch
        {
            < 1 or > 3 => $"读到 {mode}，不在 1~3 里（0x0203 未标定或缓冲残留）{stable}",
            2 when !r.HasIgpu => $"读到 2 = 独显直连，且系统里看不到核显 ⇒ 与固件行为一致{stable}",
            3 when !r.HasDgpu => $"读到 3 = 集显输出，且系统里看不到独显 ⇒ 与固件行为一致{stable}",
            1 when r.HasIgpu && r.HasDgpu => $"读到 1 = 混合输出（{cards}）{stable}"
                                             + "；没有 MUX 的机型读到 1 也一样，不能据此认定能切",
            _ => $"读到 {mode}，但{cards}，与当前模式对不上（可能刚写过还没重启，或这台没有 MUX）{stable}",
        };
    }

    // ================================================================ 落进档案

    /// <summary>
    /// 把引导结论并进档案草案：caps 覆盖（**包括把没确认的项显式写成 false**）、
    /// presets 存进 <c>presets_overrides</c>（配置那块由调用方另写 model.json）、
    /// 逐项证据存进 <c>adopt.guide</c>。
    ///
    /// 为什么要显式写 false：`Device.Caps()` 在档案没写某个键时会**按命令表推导**
    /// （有 fan 命令 ⇒ fan_manual=true）。不写 false 的话，用户一开 EC 总闸，
    /// 没确认过的风扇/显卡就会自己冒出来 —— 那是这条流水线上最不该有的"默认开"。
    /// </summary>
    public static void Apply(JsonObject profile, GuideResult r)
    {
        if (profile["caps"] is JsonObject caps)
            foreach (var (k, v) in r.Caps)
                caps[k] = v?.DeepClone();

        if (r.Presets.Count > 0)
            profile["presets_overrides"] = r.Presets.DeepClone();

        // 原厂实际写的风扇槽位落进 ec.fan_slots：槽位与通用档案不一致时 caps 那两项会关掉，
        // 但**档案里必须留下抓到的真实槽位** —— 否则作者收到包只知道"对不上"，
        // 不知道对不上的到底是哪几个字节，还得再让对方采一次。
        //
        // ⚠️ 但"抓到的槽位"是**观察口径**：只含真写了转速的那几路，原厂在"不用这一路"时
        // 写 0xFF、不会进来。所以只有出现档案表里**没有**的槽位时才改表；否则档案的完整表
        // 才是对的（它表达的是"该写哪几个字节、不用的写 255"）。
        // 2026-11-22 23:52 真机：两风扇 NL5 抓到 [a/d0]，把档案的 [d0/a/d2] 覆盖成两个槽位，
        // 顺序还是字典序（a 在前）—— 导入成正式档案后按它写 UFA 会填错字节。
        if (r.Evidence["fan_slots"] is JsonArray slots && slots.Count > 0 && profile["ec"] is JsonObject ec)
        {
            var observed = slots.Select(s => s?.ToString() ?? "").Where(s => s.Length > 0).ToList();
            var declared = ec["fan_slots"] is JsonArray da
                ? da.Select(s => s?.ToString() ?? "").Where(s => s.Length > 0).ToList()
                : [];
            var outside = observed.Where(s => !declared.Contains(s)).ToList();

            if (profile["notes"] is JsonArray n0)
            {
                if (outside.Count > 0)
                {
                    ec["fan_slots"] = slots.DeepClone();
                    n0.Add("ec.fan_slots 已按原厂写帧改为 [" + string.Join("/", slots.Select(s => s?.ToString()))
                           + $"]（其中的 {string.Join("/", outside)} 不在通用档案的表里；"
                           + "写 UFA 时按这几个字节填，能力仍需作者复核）。");
                }
                else
                {
                    n0.Add("原厂写帧里的风扇槽位是 [" + string.Join("/", observed)
                           + "]，都落在档案表 [" + string.Join("/", declared) + "] 里"
                           + "（原厂对不用的那一路写 255，所以抓不到）⇒ 档案表保持不变。");
                }
            }
        }

        var adopt = profile["adopt"] as JsonObject ?? new JsonObject();
        if (profile["adopt"] is null) profile["adopt"] = adopt;

        adopt["guide"] = new JsonObject
        {
            ["findings"] = r.Findings.DeepClone(),
            ["evidence"] = $"按弹窗 {Steps.Count} 步引导操作，取每个标记时间窗内的原厂写帧"
                           + "（显卡模式需重启，不排步骤，按整轮观察认定）",
            // 原始报文（去重后）：APMD 之外还带了什么、背光灯效位怎么放、风扇一帧写了哪几路，
            // 全在这里。作者照着写的时候不用再猜，也不用再让对方补采一次。
            ["frames"] = r.Evidence.DeepClone(),
        };

        if (profile["notes"] is JsonArray notes)
            foreach (var n in r.Notes) notes.Add(n);

        if (adopt["missing"] is JsonArray miss)
            foreach (var m in r.Missing) miss.Add(m);
    }


    // ================================================================ 逐项读

    /// <summary>
    /// 读一步窗口里的帧。返回（可读的值 / 没读出来的原因 / 抓到几条）。
    ///
    /// 值只写成**人能对得上原厂界面**的形式（"APMD 2" / "地址 5/4/3"），
    /// 不在这里编任何解释 —— 解释在 notes 里，出问题时两样都能追溯。
    /// </summary>
    private static (string? Value, string Why, int Count) Read(
        GuideStep step,
        List<ObservedWrite> frames,
        Dictionary<string, int> perf,
        SortedSet<int> kbAddrs,
        SortedSet<int> kbIgnoredAddrs,
        SortedSet<string> fanSlots,
        ref bool sawFan)
    {
        switch (step.Kind)
        {
            case GuideStepKind.PerfOffice:
            case GuideStepKind.PerfGame:
            case GuideStepKind.PerfBeast:
            {
                var hit = frames.LastOrDefault(w => w.Gwf1 == OemObservation.SubPerf);
                if (hit is null)
                    return (null, "这一段没抓到 0x0300（原厂没切档，或原厂不走 EC 切）", 0);

                var apmd = ByteOf(hit, 4);
                perf[step.ModeKey!] = apmd;
                return ($"APMD {apmd}", "", frames.Count(w => w.Gwf1 == OemObservation.SubPerf));
            }

            case GuideStepKind.Keyboard:
            {
                var kb = frames.Where(w => w.Gwf1 == OemObservation.SubBacklight).ToList();
                if (kb.Count == 0)
                    return (null, "这一段没抓到 0x0100（原厂没改灯，或灯不走 EC）", 0);

                foreach (var w in kb)
                {
                    var addr = ByteOf(w, 4);
                    if (addr == 0) continue;                    // 0 = 整键盘/灯灭
                    if (IsBacklightInit(w))
                    {
                        // 原厂打开灯效页时会先把所有 LED 组熄灭一遍：这些地址里有
                        // logo 灯、灯条等，不是键盘分区。丢掉但不瞒着 —— 交给 notes。
                        kbIgnoredAddrs.Add(addr);
                        continue;
                    }
                    kbAddrs.Add(addr);
                }

                return kbAddrs.Count > 0
                    ? ($"分区地址 {string.Join("/", kbAddrs)}", "", kb.Count)
                    // ⚠️ 只剩初始化/熄灯帧 ⇒ 什么也没确认到，value 必须是 null。
                    //    原来这里造了个"整键盘（地址 0）"的 value，报告就印成
                    //    `[已确认] 键盘背光：整键盘（地址 0）`—— 可这一窗口里根本没有 addr=0
                    //    的帧（上面 `addr == 0` 早被 continue 掉了），而同页 caps 又是 kb_zones=0
                    //    （2026-11-23 审查）。
                    : (null, "这一段只有初始化/熄灯帧，没有分区染色命令：分区数推不出来（也不等于「整键盘」）",
                       kb.Count);
            }

            case GuideStepKind.Fan:
            {
                var fan = frames.Where(w => w.Gwf1 == OemObservation.SubFan).ToList();
                if (fan.Count == 0)
                    return (null, "这一段没抓到 0x0205（快捷键可能不走 EC，或没真的切到 Max）", 0);

                sawFan = true;
                foreach (var w in fan)
                {
                    // d0 在偏移 4（u32 低字节），a/b/c/d 覆盖 d1 的低 4 字节（8..11），d2 在 12。
                    // 只认 1..100 的转速：原厂在"不用这一路"时写 0xFF，把它当槽位会多数一路。
                    if (DutyAt(w, 4)) fanSlots.Add("d0");
                    foreach (var (off, name) in ByteSlots)
                        if (DutyAt(w, off)) fanSlots.Add(name);
                    if (DutyAt(w, 12)) fanSlots.Add("d2");
                }

                // 只写"槽位 a/d0"会被读成"风扇表就这两个字节"：观察只统计真写了转速的
                // 那几路，原厂对不用的那一路写 255，所以抓不到。档案表要表达的是
                // "该写哪几个字节、不用的写 255"，两者必须分开说（2026-11-23 真机）。
                return fanSlots.Count > 0
                    ? ($"槽位 {string.Join("/", fanSlots)}"
                       + (fanSlots.IsSubsetOf(GenericFanSlots) && fanSlots.Count < GenericFanSlots.Length
                           ? $"（档案表仍按 {string.Join("/", GenericFanSlots)}，没写到的按 255 处理）"
                           : ""), "", fan.Count)
                    : (null, "抓到了 0x0205，但所有槽位都是 0/0xFF（Auto / Max 预设可能不走 EC）", fan.Count);
            }

            default:
                return (null, "未知步骤", 0);
        }
    }

    /// <summary>
    /// 把「[已确认] 键盘背光」那行的 value 改成**最终口径**（见调用处的注释）：
    /// 观察到的原始地址是证据，进 notes / evidence；这一行说的是"档案里到底几个分区"。
    /// 两者一致时原样保留。
    /// </summary>
    private static void RewriteKeyboardFinding(List<GuideFinding> findings, SortedSet<int> seen,
                                               SortedSet<int> final, bool partialKnown,
                                               IReadOnlyList<int>? known)
    {
        for (var i = 0; i < findings.Count; i++)
        {
            if (findings[i].Step != "keyboard" || findings[i].Value is null) continue;

            string value;
            if (!final.SetEquals(seen))
            {
                var extra = seen.Where(a => !final.Contains(a)).OrderBy(a => a);
                value = $"分区地址 {string.Join("/", final)}（这次点到 {string.Join("/", seen)}；"
                        + $"{string.Join("/", extra)} 跟着变色，按 logo 灯 / 灯条处理）";
            }
            else if (partialKnown && known is { Count: > 1 })
            {
                // 只点到部分已知分区：说清"这是这次点到的地址"，别让它读成最终分区数
                value = $"这次点到的地址 {string.Join("/", seen)}"
                        + $"（档案分区表仍按 {string.Join("/", known.OrderByDescending(a => a))}）";
            }
            else
            {
                continue;
            }

            findings[i] = findings[i] with { Value = value };
        }
    }

    /// <summary>
    /// 这条 0x0100 是不是「熄灯 / 初始化」帧：亮度 0，且 B/G/R 三通道是同一个哨兵值
    /// （0 / 1 / 255）。原厂一打开灯效页就会给所有 LED 组（键盘三区 + logo 灯 + 灯条）
    /// 各发一条这样的帧，地址里那些 7、8 不是键盘分区；真正按区染色时三通道不会全同、
    /// 亮度也不为 0。这里只排除"同时满足"的两条，避免把用户设的纯白/纯灰误丢。
    /// </summary>
    private static bool IsBacklightInit(ObservedWrite w)
    {
        var level = ByteOf(w, 11) & 0x0F;              // d = (effect << 4) | level
        if (level != 0) return false;
        var b = ByteOf(w, 8);
        var g = ByteOf(w, 9);
        var r = ByteOf(w, 10);
        return b == g && g == r && (r <= 1 || r >= 0xFE);
    }

    /// <summary>
    /// 观察里有没有"中间转速"（1~99）。只有 0 / 100 / 0xFF 时说明原厂这轮用的是
    /// Auto / Max 预设，不是手动拉条 —— 只写进备注，不改变能力判定（槽位规则仍是真凭据）。
    /// </summary>
    private static bool HasIntermediateDuty(IEnumerable<ObservedWrite> all)
        => all.Where(w => w.Gwf1 == OemObservation.SubFan)
              .SelectMany(FanDuties)
              .Any(v => v is >= 1 and <= 99);

    private static IEnumerable<int> FanDuties(ObservedWrite w)
        => new[] { 4, 8, 9, 10, 11, 12 }.Select(off => ByteOf(w, off));

    /// <summary>Max 快捷键的特征：同一帧里多路一起写 100%（原厂不会只拉一路到满）。</summary>
    private static bool HasMaxPreset(IEnumerable<ObservedWrite> all)
        => all.Any(w => w.Gwf1 == OemObservation.SubFan && FanDuties(w).Count(v => v == 100) >= 2);

    /// <summary>
    /// 第 <paramref name="index"/> 步关心哪条 EC 子命令（-1 = 没有这一步）。
    /// 界面计数与推断切窗用的是**同一个**判定，避免"弹窗说抓到了、报告说没抓到"。
    /// </summary>
    public static int StepSubCommand(int index)
        => index >= 0 && index < Steps.Count ? KindOf(Steps[index].Kind) : -1;

    /// <summary>
    /// 这条写帧算不算当前这一步的证据。原厂的风扇自动曲线（0x0205）在整轮里
    /// 会周期性下发：如果所有帧都计数，切性能档那几步会显示"抓到 1 条"，
    /// 用户以为切档成功就直接点「完成这步」，收尾才发现这一档是空的。
    /// </summary>
    public static bool CountsForStep(int index, int gwf1)
    {
        var sub = StepSubCommand(index);
        return sub >= 0 && sub == gwf1;
    }

    /// <summary>这个步骤关心哪条子命令。</summary>
    private static int KindOf(GuideStepKind kind) => kind switch
    {
        GuideStepKind.PerfOffice or GuideStepKind.PerfGame or GuideStepKind.PerfBeast
            => OemObservation.SubPerf,
        GuideStepKind.Keyboard => OemObservation.SubBacklight,
        GuideStepKind.Fan => OemObservation.SubFan,
        _ => -1,
    };

    private static bool DutyAt(ObservedWrite w, int off)
        => ByteOf(w, off) is >= 1 and <= 100;

    private static int ByteOf(ObservedWrite w, int off)
        => off >= 0 && off < w.Raw.Length ? w.Raw[off] : 0;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static string LabelOf(string modeKey) => modeKey switch
    {
        "office" => "办公",
        "game" => "游戏",
        "beast" => "狂暴",
        _ => modeKey,
    };

    /// <summary>
    /// 槽位按通用档案的顺序写（d0/a/d2），不是字典序 —— 报错信息里那一串得能直接和
    /// `ec.fan_slots` 对着看，否则"槽位不一致"这句话等于没说。
    /// </summary>
    private static string SlotsText(IEnumerable<string> slots)
        => string.Join("/", slots.OrderBy(s =>
        {
            var i = Array.IndexOf(GenericFanSlots, s);
            return i >= 0 ? i : 99;
        }));

    private static JsonArray ToJson(IEnumerable<GuideFinding> findings)
    {
        var arr = new JsonArray();
        foreach (var f in findings)
        {
            arr.Add(new JsonObject
            {
                ["step"] = f.Step,
                ["title"] = f.Title,
                ["marked"] = f.Marked,
                ["skipped"] = f.Skipped,
                ["frames"] = f.Frames,
                ["value"] = f.Value,
                ["why"] = f.Why,
            });
        }
        return arr;
    }
}
