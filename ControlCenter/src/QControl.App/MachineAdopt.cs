using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.Core.Data;
using QControl.Core.Devices;
using QControl.Core.Ec;

namespace QControl.App;

/// <summary>
/// 「一键机型档案采集」—— 把 <see cref="MachineReport"/> 那份**给人看**的报告，
/// 升级成**程序自己能用**的机型档案。
///
/// 为什么要在报告之外再造一份：报告是"证据"，档案是"结论"。以前流程是用户发报告、
/// 作者照抄成 `devices/*.json`、发版、用户升级 —— 中间任何一次手抄错位都会变成
/// "风扇读数反了"这种没人查得出的 bug。现在点一次按钮：采一段时间的帧、用原生读数
/// 当参照自动判偏移、**再把性能档与背光写进去读回来**（见 <see cref="MachineProbe"/>）、
/// 直接写一份**本机生效**的档案（写入仍关），作者只做复核。
///
/// 关键的一条：**读和写在同一次采集里验完**（2026-11-08）。以前要读走一遍、
/// 用户自己去设置页开总闸试写、再回来采一遍 —— 三跳，绝大多数人走到第二跳就散了，
/// 于是采集包里永远只有"读"的证据。现在一次点击产出**一份能给作者做到完全适配**的包：
/// 身份 / EC 命令表 / 逐帧采样 / 偏移结论 / 写命令的读回验证，全在同一个文件里。
///
/// 三条纪律，与报告同源：
///   · **能恢复才写**：写验证只碰性能档与键盘背光，且写前必读原值、写后必恢复；
///     风扇一个字都不写（写进去 EC 就一直按那个值转，只有重启能交回自动）；
///   · **不猜**：认不准的偏移不写进档案（<see cref="EcLayoutAdvisor"/> 负责把"认不准"说清楚）；
///   · **不给写权限**：草案恒为 verified=false / writes_allowed=false，
///     写 EC 的授权只能由作者复核原厂行为后打开。
/// </summary>
internal static class MachineAdopt
{
    /// <summary>
    /// 采样窗口（秒）。一轮 `status` 往返实测约 1 秒，20 秒才凑够 20 帧 ——
    /// 低于 6 帧算不出相关性，再长用户会觉得程序卡住（界面一直显示"正在采集"）。
    /// </summary>
    public const int DefaultSeconds = 20;

    /// <summary>
    /// 采集分几段。界面上的进度条就按这个数画格子 —— 段与段之间是**真边界**
    /// （见下方每次 <c>step(n)</c> 的位置），不是把总时长平均切开凑出来的。
    /// </summary>
    public static readonly string[] StepNames = ["读偏移", "引导原厂", "验证写入", "风扇负载", "生成档案"];

    public static JsonObject Collect(AppState st, string version, int seconds, bool probe,
                                     int observeSeconds = 0, Action<string>? stage = null,
                                     Action<int>? step = null)
    {
        var span = Math.Clamp(seconds <= 0 ? DefaultSeconds : seconds, 6, 30);
        stage ??= _ => { };
        step ??= _ => { };

        // 1) 先采基础报告：身份 / 候选项目号 / EC 命令表 / 硬件档案。
        //    它自己也会读一遍帧（1~2 秒），那几帧留着当证据，采样从下一轮重开。
        step(1);
        stage("读机型信息…");
        var report = MachineReport.Collect(st, version);

        // 2) 多帧采样（唯一"慢"的部分，调用方必须放后台线程）
        var (samples, refs, sampleNotes) = Sample(st, span, stage);

        // 3) 推断 + 草案
        var advice = EcLayoutAdvisor.Advise(samples);

        // 3.4) 显卡模式的读侧证据：纯读（0x0203 + 显示拓扑），不写、不重启。
        //      切换要重启 ⇒ 观察会话抓不到写字，这一项程序验不了（2026-11-22 起按默认
        //      能切处理，不再问用户）；这里读到的值只当佐证写进包（见 GfxProbe）。
        stage("读显卡模式…");
        var gfxProbe = GfxProbe.Read(st);

        // 3.41) 观察原厂 + 引导步骤（可选）：抓原厂真实下发的写命令，并按用户点的标记切段。
        //      放在写验证**之前**——写验证自己会往黑板写 0xFB00/0x0300、0x0100，
        //      夹在观察里会被误判成"原厂切了性能档/背光"。
        JsonObject? observation = null;
        IReadOnlyList<ObservedWrite>? observedWrites = null;
        IReadOnlyList<LayoutSample> fanSamples = [];
        GuideResult? guide = null;
        // 观察没跑时 st.Guide 里可能还留着上一轮的标记 ⇒ 只有真观察过才算数。
        // ⚠️ **必须在 Observe 之后取**（2026-11-14 修）：标记是用户在观察期间一条条点出来的，
        //    这行原先写在 Observe 之前，取到的永远是空表 —— 不管用户点得多认真，收尾时
        //    都按"一步没做"处理（包里有写帧、没有 marks，报告里连 guide 段都不出现）。
        IReadOnlyList<GuideMark> guideMarks = [];
        if (observeSeconds > 0)
        {
            step(2);
            var obs = MachineObserve.Observe(st, observeSeconds, stage, () => st.ObserveStopRequested, st.Guide);
            observation = obs.Json;
            observedWrites = obs.Writes;
            fanSamples = obs.FanSamples;
            guideMarks = st.Guide.Marks;

            // 引导标记是"用户照面板做的"这一事实的唯一记录。没有标记（观察跳过 / 用户没点）
            // 就不做语义推断 —— 拿一堆无标签的帧硬猜，比不猜更危险。
            if (guideMarks.Count > 0)
            {
                var markJson = new JsonArray();
                foreach (var m in guideMarks)
                    markJson.Add(new JsonObject
                    {
                        ["step"] = m.Step,
                        ["at"] = m.At.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        ["skipped"] = m.Skipped,
                    });
                observation["marks"] = markJson;
            }
        }

        // 3.42) 引导结论。
        //       · 有标记 ⇒ 逐步切窗推断（性能 / 背光 / 风扇），显卡那条恒开、一并写进 findings；
        //       · 没标记 ⇒ 不跑推断：那会把五步全写成"没走到"、caps 全部显式 false，
        //         等于用"没观察"覆盖 OemObservation 已经认到的能力。
        if (guideMarks.Count > 0)
        {
            guide = AdoptGuide.Analyze(observedWrites ?? [], guideMarks, st.Guide.Started,
                                       GfxProbe.ToEvidence(gfxProbe), KnownZoneAddrs(st));
            if (observation is not null)
            {
                observation["guide"] = guide.Findings.DeepClone();
                // 每步的原始报文（0x0300 除 d0 还带什么、背光灯效位、风扇一帧写哪几路）：
                // 作者要照着写的时候，结论不够用，原文才够。
                observation["guide_evidence"] = guide.Evidence.DeepClone();
                observation["gfx_probe"] = gfxProbe.DeepClone();
            }
        }

        // 3.45) 风扇转速：只有引导走到「风扇」那一步、用户真把风扇拉起来时才有数据。
        //       靠"变速的形状"认（不是相关性，也不是值域瞎猜）。认出来就补 fan1/fan2，
        //       连带把路数定下来 —— 这一步不写 EC，纯读。
        var fanAdvice = FanLayoutAdvisor.Advise(fanSamples, TakenSlots(advice), TempCmd(advice));
        if (fanAdvice.Layout.Count > 0) advice = EcLayoutAdvisor.WithFans(advice, fanAdvice);

        // 3.5) 写入验证 —— 放在采样之后：切档会改变功耗与温度，
        //      夹在采样里会污染"哪个偏移跟 CPU 功耗一起动"这件事。
        step(3);
        var probeOut = MachineProbe.Run(st, probe, stage);

        // 写探针如果**证实**了背光档位在哪个偏移（写进去、读回来），它比
        // "值域 0~2 且采样期间刚好变过"那条硬得多，并进标定结论。
        if (probeOut.BacklightOffset is { } blOff)
            advice = EcLayoutAdvisor.WithBacklightProbe(advice, blOff, 4,
                "写入 0/2 档后在同一偏移读回一致（写探针，不是值域猜的）");

        // 3.52) 加一段 CPU 负载：唯一能给出"哪一路风扇对 CPU"的**因果**证据。
        //       放在写验证之后 —— 它要独占负载，而探针自己会改档位、闪键盘灯。
        //       判不出归属时（两路一起升 / 负载没拉起来）只留 notes，不动布局。
        step(4);
        var loadOut = CpuLoadProbe.Run(st, stage);

        // 负载那一段的帧也参与温度推断：稳态采样的 20 秒常常是空载，温度不动 ⇒ 相关性算不出来。
        // 只补缺：已有结论来自更早的一整段采样，冲突时不被覆盖。
        if (loadOut.Ran)
        {
            var combined = new List<LayoutSample>(samples);
            combined.AddRange(loadOut.Baseline);
            combined.AddRange(loadOut.Loaded);
            combined.AddRange(loadOut.Recovery);
            advice = EcLayoutAdvisor.WithMissingTemps(advice, EcLayoutAdvisor.Advise(combined),
                                                      loadOut.Loaded.Count);
        }

        var roles = FanRoleAdvisor.Advise(loadOut.Baseline, loadOut.Loaded, loadOut.Recovery,
                                          advice.Layout, TakenSlots(advice));
        advice = EcLayoutAdvisor.WithFanRoles(advice, roles);

        var generic = EmbeddedData.ReadObject("devices/quanta-gmwmi-generic.json");

        // 3.53) 时序与回显自检（纯读）：测出 `read_gap` 建议值与哪些读命令回显可校验。
        //       必须放在**最后** —— 前面几段会写 EC 或造负载，那时测出来的间隔不作数。
        //       这两项以前只能靠作者手工标（连发 48 次 / 逐条比对回帧），而它们只要求读。
        step(5);
        var timing = EcTimingProbe.Run(st, Str(probeOut.WriteTest["mode"]) == "probe", stage);
        if (report["self_check"] is JsonArray scArr)
            foreach (var n in timing.Notes) scArr.Add(n);

        var selfCheck = new List<string>();
        if (report["self_check"] is JsonArray notes)
            foreach (var n in notes) selfCheck.Add(Str(n));
        selfCheck.AddRange(sampleNotes);
        selfCheck.AddRange(probeOut.Notes);
        // 认出转速时它的 notes 已经并进 advice.Notes（会出现在报告的"还差什么/说明"里）；
        // 没认出来时那些话只能走自检，否则"为什么没有转速"在包里一个字都看不到。
        // 只有真跑了引导（observeSeconds > 0）才谈得上"风扇那一步的样本不够"；
        // 观察整个没开时加这条只会让人以为采坏了。
        if (fanAdvice.Layout.Count == 0 && observeSeconds > 0) selfCheck.AddRange(fanAdvice.Notes);
        if (loadOut.Skip is { Length: > 0 } loadWhy) selfCheck.Add("加负载看风扇：没做成 —— " + loadWhy);

        var identity = report["identity"] as JsonObject ?? new JsonObject();
        var candidates = report["vendor_candidates"] as JsonObject ?? new JsonObject();

        stage("生成档案…");
        var profile = DeviceProfileDraft.Build(
            identity, candidates, generic, advice, version, samples.Count, refs, selfCheck);

        // 3.50) 时序与回显的实测结论写进草案。两个键都只影响"读得多快/信不信这一帧"，
        //       写错了最坏是慢或重试，不会动硬件 —— 但仍然只进草案（verified 恒 false）。
        if (profile["ec"] is JsonObject ecTiming)
        {
            if (timing.ReadGap is { } rg) ecTiming["read_gap"] = rg;
            if (timing.BurstGap is { } bg) ecTiming["burst_gap"] = bg;
            if (timing.EchoVerified.Count > 0)
            {
                var echoArr = new JsonArray();
                foreach (var c in timing.EchoVerified) echoArr.Add(c);
                ecTiming["echo_verified"] = echoArr;
            }
        }
        report["timing"] = timing.Json.DeepClone();

        // 3.51) 风扇路数与转速偏移（用**最终**布局：引导那一步 + 加负载重排之后的）。
        //       路数只在**确实认到两路**时才改：只认出一路时保持原值 —— "单风扇机型"和
        //       "另一个风扇当时没转（0dB）"在数据上分不开，改小了会把真风扇从界面上藏掉。
        //
        //       ⚠️ 2026-11-18：**只许升、不许降**（与下面 kb_zones 同一条规矩）。
        //       转速采样只读 `status`（0x0200）那一帧，而 SYS 第三路在 0x0207 里 ——
        //       三风扇机上这里永远只能数到 2，拿它覆盖就等于把三路写成两路
        //       （猎刃16 跑一次一键采集就会中招，草案导入后 SYS 圈环和曲线全没了）。
        var declaredFans = st.Ec.Spec.Fans;
        var fanCount = FanCountOf(advice);
        if (fanCount >= 2 && fanCount > declaredFans)
        {
            if (profile["ec"] is JsonObject ecFan) ecFan["fans"] = fanCount;
            if (profile["caps"] is JsonObject capsFan) capsFan["fan_count"] = fanCount;
        }
        else if (fanCount >= 2 && fanCount < declaredFans)
        {
            AddProfileNote(profile,
                $"这台按 SKU 判出来是 {declaredFans} 路风扇，但转速采样只数到 {fanCount} 路 —— "
                + "SYS 那一路不在 0x0200 帧里（在 0x0207），采样看不到。"
                + $"草案保留 {declaredFans} 路，不要按 {fanCount} 路理解。");
        }
        if (profile["adopt"] is JsonObject adoptFan)
        {
            var fanNotes = new List<string>(fanAdvice.Notes);
            // 引导「风扇」那一步没采到样本（samples=0）、但 layout 里确实有转速偏移时，
            // 别让档案自己打架：上面 notes 写着"这一段没标定"，下面 layout 却列着 fan1/fan2。
            // 口径写清：偏移来自加负载那一段的归属判定，不是引导那一步标出来的。
            var fansInLayout = advice.Layout.Keys.Count(k => k is "fan1_rpm" or "fan2_rpm" or "fan3_rpm");
            if (fanSamples.Count < FanLayoutAdvisor.MinSamples && fansInLayout > 0)
                fanNotes.Add("转速偏移不是引导「风扇」那一步标出来的（那一步只拿到 "
                    + fanSamples.Count + " 个样本）：它来自加负载那一段的归属判定，见 roles。");

            adoptFan["fan_rpm"] = new JsonObject
            {
                ["count"] = fanCount,
                ["declared"] = declaredFans,
                ["samples"] = fanSamples.Count,
                ["layout"] = FanLayoutJson(advice.Layout),
                ["notes"] = ToArray(fanNotes),
                // 归属证据：加 CPU 负载后哪一路起来了（decided=false 时只有原因）
                ["roles"] = loadOut.Json.DeepClone(),
                ["roles_decided"] = roles.Decided,
            };
        }

        // 3.55) 把观察到的原厂写命令翻译成 caps 增量，并进档案。
        //       caps 照抄 generic 时 fan_count/kb_zones 都是空/0，观察正好补齐这些
        //       "采集读不出来、只能看原厂行为"的语义（仍不置 verified）。
        //       ⚠️ 合并逻辑在 Core（OemObservation.Apply）—— 那里每一步都 DeepClone，
        //          同一个 JsonNode 挂两处会抛「node already has a parent」，整次采集失败。
        if (observation is not null && observedWrites is not null)
            OemObservation.Apply(profile, observation, observedWrites);

        // 3.56) 引导结论**最后**覆盖：上面那一步是"不分段的归纳"（看到 0x0100 就当有背光），
        //       这里才是"有标签的证据"（知道地址是 5/4/3 还是整键盘）。两者冲突时以引导为准，
        //       没确认的项在这里被显式写成 false —— 否则总闸一开，界面上会冒出没验过的能力。
        if (guide is not null) AdoptGuide.Apply(profile, guide);

        // 3.57) 键盘分区数**只许升、不许降**。
        //
        // `AdoptGuide` 按"这次观察抓到几个 0x0100 地址"定 kb_zones，可观察只能看到
        // **用户在原厂里点过的那一个分区**。三区机型上用户只点了 A 区 ⇒ 草案写 1 区
        // ⇒ 作者导入后同模具所有机器的背光页只剩一个 tab（2026-11-14 真机实测：本机
        // model.json 的 5/4/3 被同一条思路写成只剩 4，背光页从 3 个 tab 变 1 个）。
        if (guide is not null && profile["caps"] is JsonObject capsZone)
        {
            var seenZones = (int)(Num(capsZone["kb_zones"]) ?? 0);
            var knownZones = KnownKbZones(st, report);
            if (seenZones > 0 && knownZones > seenZones)
            {
                capsZone["kb_zones"] = knownZones;
                AddProfileNote(profile,
                    $"这次观察只抓到 {seenZones} 个键盘分区地址，按这台机器已知的 {knownZones} 区保留 —— "
                    + "观察只能看到你在原厂里点过的那一个分区，不代表机器只有这么多。");
            }
        }

        // 3.58) 草案里带回去的分区表同样不许比已知的少：导入端会照它 seed 配置，
        //       一份"只有 A 区"的草案导进去，等于把三区配置砍成一区。
        if (profile["presets_overrides"] is JsonObject poZone && poZone["backlight"] is JsonObject pblZone
            && pblZone["zones"] is JsonArray draftZones
            && st.Model.Get("backlight", "zones") is JsonArray curZones
            && curZones.Count > draftZones.Count)
        {
            var (mergedZones, addedZones) = BacklightZones.Merge(curZones, draftZones);
            if (addedZones > 0 || mergedZones.Count > draftZones.Count)
                pblZone["zones"] = mergedZones;
        }

        // 3.6) 结论与证据一起装进报告 —— **一份文件就是交付包**。
        //
        // 以前"结论"（档案）在 %ProgramData%\Q-Control\devices\，"证据"（报告）在 logs\，
        // 用户要"发给我们"的时候经常发错那个：发档案过来 → 里面没有采样、没有相关系数，
        // 复核不了；发 txt 过来 → 没有原始帧。现在 adopt-*.json 里同时有
        // profile_draft（结论）、samples（每一帧 + 同刻的原生参照）、
        // write_test（写命令的读回验证），作者一条命令就能转成正式档案。
        report["write_test"] = probeOut.WriteTest;
        // 显卡读数单独一块：它是唯一"不写 EC 也能拿到的显卡模式证据"，
        // 也是解释"为什么这台读到的模式和系统里的显卡对得上/对不上"的原始材料。
        report["gfx_probe"] = gfxProbe.DeepClone();
        if (observation is not null) report["observation"] = observation;

        // 风扇转速单独一块：它是唯一靠"变速的形状"认出来的东西（不是相关性），
        // 与 samples 那套判据不是一回事，复核时要能分开看。
        report["fan_rpm"] = new JsonObject
        {
            // count = **采样能看到的**路数（只读 0x0200 帧）；declared = 档案/SKU 判出来的路数。
            // 三风扇机上两者会不相等（SYS 在 0x0207），草案按 declared 保留，见 3.51。
            ["count"] = fanCount,
            ["declared"] = declaredFans,
            ["samples"] = fanSamples.Count,
            ["layout"] = FanLayoutJson(advice.Layout),
            ["notes"] = ToArray(fanAdvice.Notes),
            // 认出来的那两路，把整段序列原样贴上：形状判定（而不是相关性）最容易出的错
            // 是"把温度×100 这类平滑字段当成转速"，作者扫一眼数字量级就能否掉。
            ["series"] = FanSeriesJson(fanSamples, fanAdvice),
        };

        // 「加 CPU 负载看哪路起来」单独一块：它是因果证据，和"形状/相关性"都不是一回事。
        // 判不出来时这里照样写清原因（两路一起升 / 负载没拉起来 / 连不上 EC），别留空对象。
        report["fan_roles"] = new JsonObject
        {
            ["decided"] = roles.Decided,
            ["layout"] = FanLayoutJson(advice.Layout),
            ["load"] = loadOut.Json.DeepClone(),
            ["notes"] = ToArray(roles.Notes),
            ["missing"] = ToArray(roles.Missing),
        };

        // `profile_draft` 故意不在这里挂：能力表可能稍后从上一份草案继承（3.85），
        // 挂早了包里就是继承前的 0/空。真正的挂点在落盘决策之前。
        report["samples"] = SamplesJson(samples);

        // 加负载那一段的逐帧数据也进包。
        // 报告文本会写"33 帧、负载期间 CPU 功耗明显抬高""用那一段补上了 gpu_temp"，
        // 而包里原先只有三个均值 —— 作者看到"温度没认出来"时无法复核到底发生了什么的帧。
        // 这一段同样是"证据"，和稳态采样分开存（段名对应 CpuLoadProbe 的三段）。
        if (loadOut.Ran)
        {
            report["load_samples"] = new JsonObject
            {
                ["baseline"] = SamplesJson(loadOut.Baseline),
                ["load"] = SamplesJson(loadOut.Loaded),
                ["recovery"] = SamplesJson(loadOut.Recovery),
            };
        }
        report["how_to_use"] =
            "把这一份 .json 发给作者即可（控制中心 logs 目录里的 adopt-*.json）："
            + "里面有采集结论（profile_draft）、每一帧采样（samples）、"
            + "加负载那一段的逐帧采样（load_samples，用来核对温度/风扇归属）、"
            + "以及这次采集做的写入验证（write_test：性能档与键盘背光的写入值与 EC 读回值）。"
            + (observation is not null
                ? "还带一段观察原厂写命令的记录（observation：抓到原厂切档时真实下发的 EC 报文）。"
                : "")
            + "作者用 `QControl.exe --import-pack <文件>` 就能转成正式机型档案，不用再手抄偏移、"
            + "也不用再让你补采第二次。";

        // 4) 落盘：设备档案写进用户档案目录（与 model.json 同级），报告仍写 logs
        var outObj = new JsonObject { ["ok"] = true };
        var dir = UserProfileDir(st);
        var id = Str(profile["id"]);
        var file = Path.Combine(dir, id + ".json");

        // 清理路径一直拒绝动源码仓库里的 devices\，写入路径原先却直接覆盖同名文件 ——
        // 调试实例的 model.json 常在工作区，于是仓库里手改的 quanta-nl5.json 会被
        // verified=false 的草案盖掉（2026-11-07 踩过）。写之前先问一次能不能写。
        var (canWrite, denyWhy) = UserProfileStore.CanWriteProfile(dir, file);

        // 报告对象在采集**开始**时就建好了，而"生成时间"是这个文件的名字与头部第一行。
        // 必须等最后一步（写探针 / 时序自检 / 加负载）都做完再取一次，否则头部时间会早于
        // 正文里的事件 —— 2026-11-22 真机：头部写 22:53:02，正文最后一条写入是 22:53:58，
        // 作者按时间顺序读包时会以为"生成之后又有人写了 EC"。
        report["created"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz",
                                                       CultureInfo.InvariantCulture);

        // ⚠️ 先定下文件名再写正文：txt 里要写出"发哪一个文件"（见 AdoptText），
        //    而两份文件必须同名 —— 否则用户手里会出现两个时间戳不同的 adopt-*。
        var stem = MachineReport.MakeStem(report);

        var written = false;
        string? profileNote = null;

        // 只读采集产出的是"证据包"，**不许**覆盖上一次完整采集写下的本机草案。
        //
        // 草案里 `caps.kb_zones` / `presets_overrides`（性能档的 APMD、键盘分区表）只能靠
        // 「观察原厂 + 引导」拿到，只读这一轮一个都产不出来（guide == null，profile 照抄
        // generic 的 0/空）。2026-11-15 实测：跑一次只读采集就把上一份 kb_zones=3、
        // 三区预设的草案写成了 kb_zones=0、预设为 null —— 用户本机档案被"降级"了，
        // 而他看到的只是"采集成功"。
        var didProbeWrites = Str(probeOut.WriteTest["mode"]) == "probe";
        var thinnerThanExisting = !didProbeWrites && guide is null && File.Exists(file);

        // 3.85) 能力表**只升不降**：上一次草案里确认过的项，这一次没看到也不许抹掉。
        //
        // 两条路径都读旧草案（以前只读 guide == null 那条）：
        //   · 本次没开原厂（guide == null）⇒ 旧档案里更强的项直接沿用（GuidedCaps.Inherit）；
        //   · 本次跑了引导但只走完几步（guide != null）⇒ 本次没确认到的项取强保留（GuidedCaps.Merge）。
        //
        // 为什么第二条也要：2026-11-22 真机连着采了三次 —— 19:04 确认了「性能三档 + 背光 3 区 +
        // 显卡」，19:06 又跑一次（只切了显卡），`AdoptGuide.Apply` 把 caps 整块重写成
        // `perf_modes=[]` / `kb_zones=0`，22:53 再"忠实地"继承这份残表 ⇒ 草案里只剩显卡模式，
        // 而同一份 txt 正文还写着"性能档写入 3 次读回一致"。档案和报告互相打脸。
        //
        // 只并 caps / presets_overrides / 本次缺的 layout 偏移 / ec.fan_slots；
        // timing / write_test / 本次认出来的偏移都用本次的。
        //
        // ⚠️ 文案别写"那次带引导的采集"：0.1.2 起 `Inherit` 不再要求旧档案带过引导，
        //    旧档案的能力可能来自观察原厂、也可能来自作者补录 —— 说成"带引导"会让人
        //    以为那一轮跑过引导（2026-11-22 23:41 的报告就这么误导了一次）。
        string? inheritedCaps = null;
        var oldProfile = TryReadProfile(file, out var oldProfileWhen);
        if (oldProfile is not null)
        {
            if (guide is null)
            {
                if (GuidedCaps.Inherit(profile, oldProfile))
                {
                    AddProfileNote(profile, "本次没开原厂（引导没跑），caps 与预设沿用 "
                        + oldProfileWhen + " 那份档案里更强的结论 —— 没观察不等于这台没有这些能力。");
                    inheritedCaps = "能力表沿用 " + oldProfileWhen + " 那份档案"
                                    + "（本次没开原厂，没有重新确认）";
                }
            }
            else
            {
                var kept = GuidedCaps.Merge(profile, oldProfile);
                if (kept.Count > 0)
                {
                    var what = string.Join(" / ", kept);
                    AddProfileNote(profile, "本次没确认到的项（" + what + "）沿用 " + oldProfileWhen
                        + " 那份档案里的结论 —— 这次没在原厂里走到那几步，不代表这台没有这些能力。");
                    inheritedCaps = "本次没确认到的项沿用 " + oldProfileWhen + " 那份档案：" + what;
                }
            }
        }

        // 3.86) 「这次没确认到」的说法要跟**最终的** caps 对齐。
        //       caps 刚才可能被旧档案抬回来了；若还照观察结果写"⇒ 页面保持关闭"，
        //       同一份报告里就会一边说"本次确认的能力：性能三档"、一边说"性能页保持关闭"
        //       （2026-11-23 真机：13:02 那份包的两段话互相打脸）。
        //       档案里的 adopt.missing 是同一批句子，一起换掉，别让作者看到两套说法。
        List<string>? guideMissing = null;
        if (guide is not null)
        {
            var rawMissing = guide.Missing.ToList();
            var caps = profile["caps"] as JsonObject;
            guideMissing = rawMissing.Select(m => GuidedCaps.ReconcileMissing(m, caps)).ToList();
            if (profile["adopt"]?["missing"] is JsonArray adoptMissing)
                for (var i = 0; i < adoptMissing.Count; i++)
                {
                    var idx = rawMissing.IndexOf(Str(adoptMissing[i]));
                    if (idx >= 0) adoptMissing[i] = guideMissing[idx];
                }
        }

        // ⚠️ 交付包里的草案必须在**能力继承之后**再深拷贝一次（2026-11-17 真机踩到）：
        //    继承会把 caps / presets_overrides 换成上一份草案的；若报告在继承之前就拷好，
        //    包里写的是 `kb_zones=0`、本机写的是 3 —— 作者拿包 `--import-pack` 会得到
        //    一份比本机更差的正式档案，txt 里也会说"没确认分区"，而实际是有的。
        report["profile_draft"] = profile.DeepClone();

        if (!canWrite)
        {
            // 交付包照旧生成（用户要发给我们的是它），只是本机档案一个字节都不动。
            // 绝不静默降级：回执与 txt 报告都要说清为什么、这份包该怎么用。
            profileNote = denyWhy;
            outObj["profile_error"] = denyWhy;
        }
        else if (thinnerThanExisting)
        {
            profileNote = "这次是只读采集（没观察原厂、也没做写入验证），而本机已经有一份更完整的草案（"
                          + file + "，写于 " + File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm",
                              CultureInfo.InvariantCulture) + "）。"
                          + "键盘分区数、性能档 APMD 这些只有「完整采集」的观察步骤才产得出来，"
                          + "只读这一轮覆盖它只会让本机档案变差，所以没动它。";
            outObj["profile_skipped"] = profileNote;
        }
        else
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, profile.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                written = true;
                outObj["profile"] = file;
            }
            catch (Exception e)
            {
                outObj["ok"] = false;
                outObj["error"] = "档案写不进 " + dir + "：" + e.Message;
                profileNote = "档案写不进 " + dir + "：" + e.Message;
            }
        }
        // 3.7) 配置落地：APMD 映射与键盘分区地址住在 model.json（不在档案里），
        //      档案不给它，性能档写下去的 APMD 就还是别的机器的默认值。
        //      只在"本机档案真的写成功了"时才动配置：档案都没落地就改用户配置，
        //      等于改了半套，下次启动仍是旧档案 + 新 APMD，更容易写错。
        string? seedNote = null;
        if (written && guide is not null && guide.Presets.Count > 0)
        {
            var (seeded, why) = SeedPresets(st, guide.Presets);
            outObj["presets_seeded"] = seeded;
            if (!seeded) seedNote = why;
        }

        outObj["profile_dir"] = dir;
        outObj["profile_written"] = written;
        if (inheritedCaps is not null) outObj["profile_inherited_caps"] = inheritedCaps;

        // 引导结论也进交付包：作者复核时能看到"这台机器是照着哪几步认出来的"，
        // 也能看到哪几项没确认 —— 比只看 caps 多了"凭什么"。
        if (guide is not null)
        {
            report["guide"] = new JsonObject
            {
                ["findings"] = guide.Findings.DeepClone(),
                ["missing"] = ToArray(guideMissing ?? guide.Missing),
                ["notes"] = ToArray(guide.Notes),
            };
            if (seedNote is not null) report["seed_error"] = seedNote;
        }

        // 文案必须按**实际落盘结果**写：以前在落盘之前就把"已生成、重启后生效"
        // 写进 txt，一旦写入失败或按上面拒绝，那份报告就是在说假话。
        var text = AdoptText(report, advice, profile, dir, st.LogDirectory, stem,
            st.Ec.WriteLog.ToText(FanSubOf(st)), written, profileNote, inheritedCaps);

        try
        {
            var files = MachineReport.Save(st.LogDirectory, report, text, stem);
            outObj["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
            outObj["file"] = Path.GetFileName(files[0]);
            // 交付包（= 报告 json）。界面采完要定位到**这一个**：用户要发给我们的是它，
            // 不是 devices 目录里那份本机档案（档案没有证据，收到也没法复核）。
            outObj["pack"] = files[0];
        }
        catch (Exception e)
        {
            // 报告写不下去不影响档案本身，但要说出来（不是静默降级）
            outObj["report_error"] = "报告没写进日志目录：" + e.Message;
        }

        outObj["id"] = id;
        outObj["name"] = Str(profile["name"]);
        outObj["samples"] = samples.Count;
        outObj["layout_verified"] = advice.LayoutVerified;
        outObj["recognized"] = new JsonArray(advice.Guesses
            .Where(g => g.Confidence is "high" or "medium")
            .Select(g => (JsonNode?)new JsonObject
            {
                ["field"] = g.Field,
                ["cmd"] = g.Cmd,
                ["offset"] = g.Offset,
                ["confidence"] = g.Confidence,
            }).ToArray());
        outObj["missing"] = ToArray(advice.Missing);
        outObj["notes"] = ToArray(advice.Notes);
        outObj["stats"] = profile["adopt"]?["stats"]?.DeepClone();

        // 界面据此说人话：这次到底验没验写、验成了几条
        var wt = probeOut.WriteTest;
        outObj["probe"] = Str(wt["mode"]);
        outObj["probe_skip"] = wt["skip"]?.DeepClone();
        outObj["writes_verified"] = new JsonObject
        {
            ["perf"] = wt["perf"]?["effective"]?.DeepClone() ?? false,
            ["backlight"] = wt["backlight"]?["effective"]?.DeepClone() ?? false,
        };

        // 界面上要能说一句"风扇转速也认出来了" —— 没有这句，用户会以为包里还是只有温度。
        outObj["fan_rpm"] = new JsonObject
        {
            ["count"] = fanCount,
            ["fields"] = new JsonArray(advice.Layout.Keys
                .Where(k => k is "fan1_rpm" or "fan2_rpm" or "fan3_rpm")
                .Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
            // 归属是不是靠"加负载"定下来的：界面据此说"CPU 风扇认在 xx@12"
            ["roles_decided"] = roles.Decided,
        };

        // 观察原厂的摘要回给界面：整段 write_cmds 太大（可能几十条），只回条数 + caps 结论。
        if (observation is not null)
        {
            outObj["observation"] = new JsonObject
            {
                ["write_cmds"] = (observation["write_cmds"] as JsonArray)?.Count ?? 0,
                ["duration_s"] = observation["duration_s"]?.DeepClone(),
                ["skipped"] = observation["skipped"]?.DeepClone(),
                ["conclusion"] = (observation["conclusion"] as JsonObject)?.DeepClone(),
            };
        }

        // 引导结论：界面据此说"这台现在能做什么"、缺什么、以及要不要摆「启用本机」。
        if (guide is not null)
        {
            outObj["guide"] = new JsonObject
            {
                // 显卡那条恒开、不算"确认到能力"（见 GuideResult.AnyConfirmed）：
                // 否则一次什么都没做成的采集也会摆出「启用本机」。
                ["confirmed"] = guide.Findings.Count(f => f is JsonObject o && o["value"] is not null
                                                          && o["step"]?.GetValue<string>() != AdoptGuide.GfxStep),
                ["total"] = guide.Findings.Count,
                ["steps"] = guide.Findings.DeepClone(),
                ["missing"] = ToArray(guideMissing ?? guide.Missing),
                ["presets_seeded"] = written && guide.Presets.Count > 0 && seedNote is null,
            };
        }

        outObj["needs_restart"] = true;
        outObj["text"] = text;
        return outObj;
    }

    /// <summary>用户档案目录 = model.json 同级 `devices\`（安装版即 %ProgramData%\Q-Control\devices）。</summary>
    public static string UserProfileDir(AppState st)
        => Path.Combine(Path.GetDirectoryName(st.Model.Path) ?? AppContext.BaseDirectory, "devices");

    /// <summary>
    /// 把引导认出来的 APMD 映射与键盘分区地址写进 model.json。
    ///
    /// 为什么是一条条路径写、不整节替换：`ModelStore.Set` 的一级路径是**浅合并**
    /// （`Set(["perf"], …)` 会把 `modes` 整个换掉），那会把每档的 `plan` / `dat2` 一起抹掉。
    /// 走 `["perf","modes","office","apmd"]` 这种深路径才是"只改这一片叶子"。
    /// </summary>
    private static (bool Ok, string? Why) SeedPresets(AppState st, JsonObject presets)
    {
        var fails = new List<string>();

        if (presets["perf"] is JsonObject perf && perf["modes"] is JsonObject modes)
        {
            foreach (var (key, node) in modes)
            {
                if (node is not JsonObject m || m["apmd"] is not JsonValue av
                    || !av.TryGetValue<int>(out var apmd)) continue;

                var r = st.Model.Set(["perf", "modes", key, "apmd"], JsonValue.Create(apmd));
                if (!r.Ok) fails.Add($"perf.modes.{key}.apmd：{r.Error}");
            }
        }

        if (presets["backlight"] is JsonObject bl && bl["zones"] is JsonArray zones && zones.Count > 0)
        {
            // **只补缺，绝不覆盖**。
            //
            // 引导里能看到几个分区，取决于用户在观察期间点过几个分区 —— 他只点了 A 区，
            // 这里就只有一个地址。早先直接 `Set(["backlight","zones"], zones)` 是整表替换，
            // 等于把三区机型砍成一区：2026-11-14 真机实测，本机 `5/4/3` 被写成只剩 `4`，
            // 背光页从 3 个 tab 变成 1 个（用户看不出是采集干的）。
            //
            // ⚠️ 双保险：配置里已经有**确认过**的分区表时，草稿里"表外地址"一律不写进来 ——
            //    那些多半是 logo 灯 / 灯条（原厂换色时一起写）。判错的代价是用户配置里多出
            //    两个假分区 tab，而配置是用户最不能被改坏的东西（2026-11-23 12:28 踩过：
            //    7/8 被当成 4、5 区写进 model.json）。
            var keep = zones;
            if (KnownZoneAddrs(st) is { Count: > 0 } knownZones)
            {
                keep = new JsonArray();
                foreach (var z in zones)
                    if (z?["addr"] is JsonValue av && av.TryGetValue<int>(out var addr)
                        && knownZones.Contains(addr))
                        keep.Add(z!.DeepClone());
            }

            if (keep.Count > 0)
            {
                var (merged, added) = BacklightZones.Merge(
                    st.Model.Get("backlight", "zones") as JsonArray, keep);
                if (added > 0)
                {
                    var r = st.Model.Set(["backlight", "zones"], merged);
                    if (!r.Ok) fails.Add("backlight.zones：" + r.Error);
                }
            }

            // 地址是从原厂写帧读出来的，不是抄的默认值 —— 标记成"已实测标定"，
            // 灯效页和适配报告都会照这个说。
            var c = st.Model.Set(["backlight", "zone_addrs_confirmed"], JsonValue.Create(true));
            if (!c.Ok) fails.Add("backlight.zone_addrs_confirmed：" + c.Error);
        }

        return fails.Count == 0
            ? (true, null)
            : (false, "配置没写全：" + string.Join("；", fails));
    }

    /// <summary>
    /// 配置里**已确认过**的分区地址（`zone_addrs_confirmed` 为 true 时才算）。
    ///
    /// 只给引导判"哪些地址是键盘、哪些是 logo 灯 / 灯条"用：原厂改键盘颜色会把它们一起写，
    /// 光看写帧分不出来（本机 NL5 的 7/8 就是这么被算成第 4、5 个分区，配置里多出两个 tab）。
    ///
    /// ⚠️ 没确认过的配置**不能**当白名单：默认值里就有本机 NL5 的 5/4/3，
    ///    拿它去裁另一台机器，等于把五区键盘锁成三区。
    /// </summary>
    private static IReadOnlyList<int>? KnownZoneAddrs(AppState st)
    {
        if (st.Model.Get("backlight", "zone_addrs_confirmed") is not JsonValue cv
            || !cv.TryGetValue<bool>(out var ok) || !ok) return null;
        if (st.Model.Get("backlight", "zones") is not JsonArray arr || arr.Count == 0) return null;

        var list = new List<int>(arr.Count);
        foreach (var z in arr)
            if (z is JsonObject zo && Num(zo["addr"]) is { } a && a is > 0 and <= 0xFF) list.Add((int)a);
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 这台机器已知的键盘分区数：内置档案声明的、或配置里已经有的，取大的那个。
    /// 用途只有一个 —— 挡住"这次只观察到一个分区"把已知的多区配置降下来。
    /// </summary>
    private static int KnownKbZones(AppState st, JsonObject report)
    {
        var fromDevice = Num(report["device"]?["caps"]?["kb_zones"]) ?? 0;
        var fromModel = st.Model.Get("backlight", "zones") is JsonArray z ? z.Count : 0;
        return (int)Math.Max(fromDevice, fromModel);
    }

    /// <summary>往草案的 notes 里追一条（草案的 notes 是给作者看的，必须能解释每个改动的由来）。</summary>
    private static void AddProfileNote(JsonObject profile, string text)
    {
        if (profile["notes"] is JsonArray n) n.Add(text);
    }

    /// <summary>
    /// 读上一次写下的本机草案（读不到/读坏了都当"没有"）。
    ///
    /// 读坏了不该让整次采集失败：拿不到旧草案最多是这次少继承几项，
    /// 而让异常冒出去等于用户白等一轮采集。`when` 给报告文案用（"沿用 X 那次"）。
    /// </summary>
    private static JsonObject? TryReadProfile(string file, out string when)
    {
        when = "";
        try
        {
            if (!File.Exists(file)) return null;
            when = File.GetLastWriteTime(file).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>风扇转速命令号（没有就 0 = 不折叠）。见 <see cref="EcWriteLog.ToText"/> 的 hideSub。</summary>
    private static ushort FanSubOf(AppState st)
        => st.Ec.Spec.Sub("fan") is var s and > 0 and <= 0xFFFF ? (ushort)s : (ushort)0;

    // ================================================================ 采样

    /// <summary>
    /// 每轮把"档案声明可读"的命令全读一遍，同时抓一帧原生参照。
    ///
    /// 参照为什么是这三个：
    ///   · CPU 功耗（PDH RAPL）—— 没有 HWiNFO 也有，是最普适的"负载"代理；
    ///   · 显卡温度（NVML）—— 原生直读，比任何 EC 字段都可信；
    ///   · CPU 温度（EC）—— 只有 layout 已经标定过的机器才有，属于加分项。
    /// 三个都没有时不硬凑：<see cref="EcLayoutAdvisor"/> 会说"没参照，判不了"。
    /// </summary>
    private static (List<LayoutSample> Samples, JsonObject Refs, List<string> Notes) Sample(
        AppState st, int seconds, Action<string> stage)
    {
        var notes = new List<string>();
        var samples = new List<LayoutSample>();
        var refs = new JsonObject { ["cpu_power"] = false, ["cpu_temp"] = false, ["gpu_temp"] = false };

        var reads = MachineReport.ReadCommands(st.Ec.Spec).ToList();
        if (reads.Count == 0)
        {
            notes.Add("档案里没有可读的子命令，没法采样。");
            return (samples, refs, notes);
        }

        var (ecOk, ecWhy) = st.Ec.Transport.Available();
        if (!ecOk)
        {
            notes.Add("连不上 EC（" + (ecWhy ?? "原因未知") + "），没有采样。");
            return (samples, refs, notes);
        }

        // 每轮只读 `status`（温度/转速/背光都在这一帧里 —— 档案的 `layout` 也**只从它读**，
        // 见 `EcHal.ReadStatus`），其它读命令每 5 轮才走一遍。
        //
        // ⚠️ 2026-11-16 试过"主命令每轮 + 其余命令轮转"，想让别的命令也拿到 ≥6 个样本，
        //    结果在 NL5 上直接把 gpu_temp 标成了 `gpu_info@4`（那帧的 DAT2 确实是显卡温度，
        //    但 layout 没有"来源命令"这一维，读的时候永远读 status ⇒ 显示成 CPU 温度）。
        //    真机采集包 `adopt-20261116-183221` 就是活证据：cpu_temp 与 gpu_temp 都写成 @4。
        //    ⇒ 在 layout 有来源命令之前，**别的命令的样本不许参与标定**：
        //      这里不轮转，`EcLayoutAdvisor` 也只认 `status` 那一帧（双保险）。
        //
        // 为什么不一轮全读：实测一轮全读 ≈ 3.5 秒（4 条命令 × WMI 往返 + min_gap），
        // 14 秒才凑 4 帧，连 MinSamples 都到不了 —— 那这功能就等于"点了没用"。
        var statusName = reads.FirstOrDefault(r => r.Name == "status").Name;
        const int targetRounds = 16;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(seconds * 2, 35));
        var lastTs = double.NaN;
        var refsDone = false;           // 参照来源只提一次（见下面的注释）
        var refsWarned = false;         // "两个参照都没读到"也只提一次

        while (samples.Count < targetRounds && DateTime.UtcNow < deadline)
        {
            // 界面靠它显示真实进度（不是"转圈"）：采到第几帧是这段最慢的部分，
            // 用户得知道还要等多久，否则 20 秒的静默按钮会被当成卡死。
            stage($"采样 {samples.Count + 1}/{targetRounds}（跑点负载更好认温度）…");

            // ⚠️ 卡着快照的节拍走：参照值来自采样线程每秒刷新的那一帧，
            //    若我们每 400ms 采一次，就会有几个样本共用同一个参照值 —— 相关性里
            //    混进"阶梯状"的假象，实测把 CPU 温度判成了显卡温度（2026-10-31 真机踩到）。
            //    等 `ts` 变了再采，保证一帧样本配一帧独立的参照。
            var snap = WaitSnapshot(st, lastTs, 1500);
            if (snap is null) { Thread.Sleep(200); continue; }
            lastTs = Num(snap["ts"]) ?? lastTs;

            var frames = new Dictionary<string, uint[]>(StringComparer.Ordinal);
            var fullPass = samples.Count % 5 == 0;

            foreach (var (name, sub) in reads)
            {
                if (!fullPass && name != statusName) continue;
                try
                {
                    var raw = st.Ec.Raw(new EcCommand((ushort)sub, EcDirection.Read));
                    if (raw["u32"] is JsonObject u)
                        frames[name] =
                        [
                            (uint)Math.Max(0, Num(u["DAT2@4"]) ?? 0),
                            (uint)Math.Max(0, Num(u["DAT3@8"]) ?? 0),
                            (uint)Math.Max(0, Num(u["DAT4@12"]) ?? 0),
                            (uint)Math.Max(0, Num(u["DAT5@16"]) ?? 0),
                            (uint)Math.Max(0, Num(u["DAT6@20"]) ?? 0),
                            // 数据区尾部：别的模具可能把温度/转速放这里（标定只在标准槽没结论时才看）
                            (uint)Math.Max(0, Num(u["DAT7@24"]) ?? 0),
                            (uint)Math.Max(0, Num(u["DAT8@28"]) ?? 0),
                        ];
                }
                catch (Exception e)
                {
                    if (!notes.Contains("读 EC 失败：" + e.Message)) notes.Add("读 EC 失败：" + e.Message);
                }
            }
            if (frames.Count == 0) break;

            double? cpuW = null, cpuT = null, gpuT = null;
            bool layoutUnverified = false;
            try
            {
                cpuW = Num(snap["cpu"]?["power"]);
                gpuT = Num(snap["gpu"]?["temp"]);
                layoutUnverified = snap["ec"]?["layout_unverified"] is JsonValue lv
                                   && lv.TryGetValue<bool>(out var lu) && lu;
                // CPU 温度参照：未标定机型上 `ec.cpu_temp` 是 HWiNFO 兜底（外部读数，可以用）；
                // 已标定机型上它就是 EC 自己的偏移读数 —— 拿它当参照等于自己证自己，
                // 逐帧完全相等 ⇒ 方差 0 ⇒ 相关算不出 ⇒ 永远"认不出温度"。换成 HWiNFO。
                cpuT = layoutUnverified ? Num(snap["ec"]?["cpu_temp"]) : st.HwCpuTempC();
            }
            catch { /* 参照拿不到就留空，不猜 */ }

            // 参照的来源必须写进报告：复核的人要知道 CPU 温度是**外部**读数，
            // 而不是 EC 自己那个偏移 —— 后者会把"自证"包装成很强的相关性。
            if (!refsDone && cpuT is not null)
            {
                refsDone = true;
                notes.Add(layoutUnverified
                    ? "CPU 温度参照来自 HWiNFO（本机 EC 偏移未标定，读数由它兜底）："
                      + "相关性照样算得出，但请按「外部参照」看待，不要当成 EC 自己的读数。"
                    : "CPU 温度参照取自 HWiNFO 的 CPU Package（**不是** EC 自己那个偏移）—— "
                      + "已标定的机器上拿 EC 读数当参照是循环论证，算出来的相关会是假的 1.00。");
            }
            if (cpuT is null && cpuW is null && !refsWarned)
            {
                refsWarned = true;
                notes.Add("CPU 温度与功耗两个参照都没读到（HWiNFO 共享内存没在更新？）："
                          + "温度偏移只剩「值域像不像℃」这一条弱证据，这次不据此下结论。");
            }

            refs["cpu_power"] = (bool)refs["cpu_power"]! || cpuW is not null;
            refs["cpu_temp"] = (bool)refs["cpu_temp"]! || cpuT is not null;
            refs["gpu_temp"] = (bool)refs["gpu_temp"]! || gpuT is not null;

            samples.Add(new LayoutSample(
                cpuW ?? double.NaN, cpuT ?? double.NaN, gpuT ?? double.NaN, frames));
        }

        if (samples.Count > 0 && samples.Count < EcLayoutAdvisor.MinSamples)
            notes.Add($"只采到 {samples.Count} 帧（要 {EcLayoutAdvisor.MinSamples} 帧才够算相关性）："
                      + "EC 每次往返比预期慢，或这轮采集被打断。再采一次即可。");

        if (!RefsAny(refs))
            notes.Add("这段采样没有任何原生参照（CPU 功耗 / CPU 温度 / 显卡温度都没读到）—— "
                      + "偏移只能靠值域猜，所以一个字都没写进档案。");

        return (samples, refs, notes);
    }

    private static bool RefsAny(JsonObject refs)
        => (bool)refs["cpu_power"]! || (bool)refs["cpu_temp"]! || (bool)refs["gpu_temp"]!;

    /// <summary>
    /// 把采样序列写成 json：每一帧 = 各读命令 7 个偏移的原始值 + 同刻的三个原生参照。
    ///
    /// 为什么要把逐帧原始数据交给作者：`adopt.stats` 只留了变化最大的 12 条、
    /// `adopt.guesses` 只有结论。有了逐帧数据，作者能自己重算相关系数、
    /// 验证"凭什么把 cpu_temp 判在 4 号偏移" —— 否则一键采集对接收端就是个黑盒，
    /// 判错时没人查得出来（2026-10-31 那次把信息帧认成 CPU 温度就是靠手工比对才发现的）。
    /// </summary>
    private static JsonArray SamplesJson(IReadOnlyList<LayoutSample> samples)
    {
        var arr = new JsonArray();
        foreach (var s in samples)
        {
            var frames = new JsonObject();
            foreach (var (name, vals) in s.Frames.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                frames[name] = new JsonArray(vals.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

            arr.Add(new JsonObject
            {
                ["cpu_power"] = NanOrNull(s.CpuPowerW),
                ["cpu_temp"] = NanOrNull(s.CpuTempC),
                ["gpu_temp"] = NanOrNull(s.GpuTempC),
                ["frames"] = frames,
            });
        }
        return arr;
    }

    /// <summary>NaN 必须写 null：`JsonValue.Create(double.NaN)` 会直接抛（JSON 不支持 NaN）。</summary>
    private static JsonNode? NanOrNull(double v) => double.IsNaN(v) ? null : JsonValue.Create(v);

    /// <summary>
    /// 等一个**新的**状态快照（`ts` 与上次不同），返回它。等到超时就返回 null。
    ///
    /// 为什么不直接 `CachedStatus()`：那是采样线程每秒刷一次的同一份对象，
    /// 采集快于 1 Hz 时会把同一帧参照配给好几个 EC 样本，相关性里就混进"阶梯"——
    /// 实测症状很隐蔽：CPU 温度被判成显卡温度，而且看起来相关系数还挺高。
    /// </summary>
    private static JsonObject? WaitSnapshot(AppState st, double lastTs, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            try
            {
                var snap = st.CachedStatus();
                if ((Num(snap["ts"]) ?? 0) != lastTs) return snap;
            }
            catch { /* 快照读不到就当没更新，继续等 */ }
            Thread.Sleep(120);
        }
        return null;
    }

    // ================================================================ 文本

    /// <summary>
    /// 在人读报告后面追加"本机档案"这一节：保存到哪、认出了什么、还差什么、
    /// **要发给作者的是哪一个文件**。
    /// 这一段是给用户看的（他能照着判断"要不要再采一次"），也是给作者看的（复核依据）。
    /// </summary>
    private static string AdoptText(JsonObject report, LayoutAdvice advice, JsonObject profile,
                                    string dir, string logsDir, string stem, string writeLogText,
                                    bool profileWritten, string? profileNote,
                                    string? inheritedCaps = null)
    {
        var sb = new StringBuilder(MachineReport.ToText(report));
        const int W = 74;
        var rule = new string('-', W);
        var id = Str(profile["id"]);

        sb.AppendLine();
        sb.AppendLine(rule);
        sb.AppendLine("本机机型档案（一键采集）");
        sb.AppendLine(rule);
        sb.AppendLine();
        sb.AppendLine("  档案 id  : " + id);
        sb.AppendLine("  档案名   : " + Str(profile["name"]));
        if (profileWritten)
        {
            sb.AppendLine("  本机档案 : " + Path.Combine(dir, id + ".json"));
            // 能力表可能有一部分是从上一份草案继承的 —— 必须让用户看见，
            // 否则会以为"这次没观察也把能力重新确认了一遍"。文案由调用方按路径拼好
            // （没开原厂 / 引导只走完几步，两句话不一样，别在这里统一加后缀）。
            if (inheritedCaps is not null)
                sb.AppendLine("  能力来源 : " + inheritedCaps);
            sb.AppendLine("  生效条件 : 重启控制中心后按这份档案识别本机");
        }
        else
        {
            sb.AppendLine("  本机档案 : 这次没写，本机那份保持原样。原因：");
            foreach (var line in (profileNote ?? "原因未知").Split('\n'))
                sb.AppendLine("    " + line.TrimEnd());
            sb.AppendLine("  生效条件 : 交付包里仍带着 profile_draft，作者用 --import-pack 导入即可；"
                          + "本机不需要重启，也没有东西会在重启后生效");
        }
        sb.AppendLine("  写入权限 : 关闭（verified=false）—— 采集能证明「写命令真的落到 EC 了」，"
                      + "证明不了语义对（哪一档对应哪种功耗要按原厂行为确认）");
        if (report["device"]?["verified"] is JsonValue bv && bv.TryGetValue<bool>(out var builtin) && builtin)
            sb.AppendLine("  注意     : 上面这行只说本次生成的本机草案。本机已命中内置已验证档案 "
                          + Str(report["device"]?["id"]) + "（priority 100 > 草案 50），"
                          + "重启后仍按内置档案识别，功能不会退化。");
        sb.AppendLine();

        var wt = report["write_test"] as JsonObject;
        var didProbe = Str(wt?["mode"]) == "probe";
        sb.AppendLine(didProbe
            ? "  写入验证（这次采集自己做的，采完已恢复原值）"
            : "  写入验证（这次没做）");
        sb.AppendLine();
        foreach (var line in (Str(wt?["verdict"]).Split('\n')))
            sb.AppendLine("    " + line.TrimEnd());
        sb.AppendLine();

        // 只读这一档最容易被误读成"白采了"。把**它到底换来了什么**摆出来：
        // 读的适配它是完整的（认机型 + 读数偏移），缺的只有写权限那半张纸。
        //
        // ⚠️ 但"只读 + 引导"是另一种情况：引导那一段是**看原厂发命令**，性能档 APMD、
        //    键盘分区、风扇槽位都能认出来（2026-11-23 13:23 真机，六项全确认）。
        //    这时还照抄"风扇 / 性能档 / 背光都要写权限"，就跟下面"本次确认的能力：
        //    性能三档…"同屏打架 —— 同一份报告里一句说缺、一句说已确认。
        if (!didProbe)
        {
            sb.AppendLine("  只读采集能做到哪一步：");
            sb.AppendLine("    · 认机型 —— 按注册表项目号出 match 规则，同模具的机器共用这一份；");
            sb.AppendLine("    · 读数回来 —— 认出温度/转速在 EC 里的位置，重启后就是 EC 原生读数，");
            sb.AppendLine("      不再依赖 HWiNFO 兜底（历史曲线也不会因为没开 HWiNFO 而断）；");
            if (GuideConfirmedAny(report))
            {
                sb.AppendLine("    · 已经看到的 —— 下面引导里认出来的档位 / 分区 / 槽位是「看原厂怎么发命令」"
                              + "得来的，不需要自己写；缺的只有「自己写一次再读回」这半张纸，"
                              + "完整采集会替你补。");
            }
            else
            {
                sb.AppendLine("    · 还缺什么 —— 风扇 / 性能档 / 背光都要写权限。这个不用每台机器都试：");
                sb.AppendLine("      同模具只要有一台点「一键采集」（写验证十几秒），补齐一次就对整批生效。");
            }
            sb.AppendLine();
        }
        // 采集**自己**发了几条 vs 这段时间 EC 上还有别的写入 —— 两个数必须分开说。
        // 以前只写一句"采集之前已有 15 次（多半是你自己试的）"，而下面的明细按队列现状
        // 只列 11 条：同一页两个数打架，还把这些写入全推给用户。实际上那 15 条里有一半
        // 是上一次采集自己的写验证、剩下的是切档时重放的灯效（2026-11-23 真机）。
        // 只读包的读者第一反应就是"只读是不是偷偷写了"，所以这段先把自己撇清。
        var addedWrites = Num(wt?["writes_added"]) ?? 0;
        if (!didProbe)
        {
            // 只读这一档采集路径里根本不会调写方法 ⇒ 这段时间里的写一定不是采集发的，
            // 归属可以写死（不用猜）。
            sb.AppendLine("    本次采集自己没发写命令（只读）：包里 write_test 的 perf / backlight 都是空的。");
            if (addedWrites > 0)
                sb.AppendLine($"    但这段观察时间（几十秒）里 EC 上仍有 {addedWrites:0} 次写入 —— "
                              + "全部来自切档时控制中心重放的灯效、风扇曲线自己的调度，"
                              + "跟本次采集无关；逐条在下面的明细里。");
        }
        else
        {
            sb.AppendLine($"    这段时间 EC 上一共 {addedWrites:0} 次写入（含本次采集的写验证、"
                          + "切档时重放的灯效、风扇曲线），逐条在下面的明细里。");
        }
        if (Num(wt?["manual_writes_before"]) is { } manual && manual > 0)
            sb.AppendLine($"    采集开始前，这份记录里已有 {manual:0} 条非风扇写入"
                          + "（上一次采集的写验证、切档重放的灯效都可能在里面）。"
                          + "这个数与下面明细的条数不一定相等：那是采集开始时的快照，期间可能有老条目被挤出。");
        sb.AppendLine();

        // 逐条写尝试（含采集自己写的那几次）。聊天窗口贴不了文件时，用户贴的就是这一段，
        // 所以 ok / denied / error 与 EC 回帧必须在这里露出来，不能只躺在 json 里。
        sb.AppendLine("  写尝试明细（最近的在下，最多 40 条）");
        sb.AppendLine();
        sb.Append(writeLogText);
        sb.AppendLine();

        // 引导这一节是"这台现在能干什么"的直接答案：每一步都写清确认没确认、
        // 没确认的原因是什么 —— 用户据此知道要不要重采，作者据此知道该补哪一块。
        if (report["guide"] is JsonObject guideJson && guideJson["findings"] is JsonArray guideFindings)
        {
            sb.AppendLine("  适配结论（引导步骤 + 显卡问答）");
            sb.AppendLine();
            foreach (var item in guideFindings)
            {
                if (item is not JsonObject o) continue;
                var ok = o["value"] is not null;
                sb.AppendLine($"    [{(ok ? "已确认" : "未确认")}] {Str(o["title"])}："
                              + (ok ? Str(o["value"]) : Str(o["why"])));
            }
            sb.AppendLine();

            var enabled = new List<string>();
            if (profile["caps"]?["perf_modes"] is JsonArray pm && pm.Count > 0) enabled.Add("性能三档");
            if (profile["caps"]?["keyboard_rgb"] is JsonValue kb && kb.GetValue<bool>())
                enabled.Add("键盘背光 " + (profile["caps"]?["kb_zones"] is JsonValue zv
                    && zv.TryGetValue<int>(out var zones) ? zones : 0) + " 区");
            if (profile["caps"]?["fan_manual"] is JsonValue fm && fm.GetValue<bool>()) enabled.Add("手动风扇与曲线");
            if (profile["caps"]?["gfx_switch"] is JsonValue gx && gx.GetValue<bool>()) enabled.Add("显卡模式");

            sb.AppendLine(enabled.Count > 0
                ? "    本次确认的能力：" + string.Join(" / ", enabled)
                : "    本次没有确认到任何可启用的能力（包里的 observation 仍可作为作者的证据）");
            sb.AppendLine(enabled.Count > 0
                ? "    下一步：点关于页「启用本机」打开 EC 写入并重启（会再确认一次）；"
                  + "也可在设置页「EC 控制」手动打开。"
                : "");
            sb.AppendLine();

            if (guideJson["missing"] is JsonArray miss && miss.Count > 0)
            {
                sb.AppendLine("    这台还没确认的：");
                foreach (var m in miss) sb.AppendLine("      · " + Str(m));
                // 能力是取强合并出来的：这次没确认到 ≠ 页面会关。不点破这一句，
                // 用户会以为上面"本次确认的能力：性能三档"和这里在互相打脸（2026-11-23 真机）。
                if (miss.Any(m => Str(m).Contains("照常开", StringComparison.Ordinal)))
                    sb.AppendLine("      上面这几项在档案里本来就是开的（沿用之前确认过的结论），页面不会关；"
                                  + "缺的只是这次在原厂里的一次复核。");
                sb.AppendLine();
            }

            if (report["seed_error"] is JsonValue se)
            {
                sb.AppendLine("    ⚠ " + Str(se) + "（APMD/分区地址没落进配置，性能档与背光先别用）");
                sb.AppendLine();
            }
        }

        // 显卡模式单独一段：它是唯一**没有引导步骤**的能力（切换要重启），
        // 用户和作者都要能看到这次到底按什么判的 —— 用户回答 / 观察到写帧 / 没确认。
        if (report["gfx_probe"] is JsonObject gfxP)
        {
            var gfxOn = profile["caps"]?["gfx_switch"] is JsonValue gv && gv.GetValue<bool>();
            sb.AppendLine("  显卡模式：" + (gfxOn ? "本次开启（未逐档切换验证，写入后需重启才生效）"
                                                 : "本次保持关闭"));
            sb.AppendLine("    读数核对：" + Str(gfxP["verdict"]));
            sb.AppendLine();
        }

        // 时序与回显自检：这是唯一一段"只读、但能替作者省掉手工标定"的结论，
        // 用户看报告时最想知道的是"我的机器读得快不快、会不会读到脏帧"。
        if (report["timing"] is JsonObject tj && tj["attempted"]?.ToJsonString() != "false")
        {
            sb.AppendLine("  时序与回显自检（纯读，一个字节都没写）");
            sb.AppendLine();

            if (tj["read_gap"] is JsonObject rg)
            {
                var rows = rg["rows"] as JsonArray ?? new JsonArray();
                var desc = string.Join("  ",
                    rows.OfType<JsonObject>().Select(r =>
                        $"{Num(r["gap_ms"]) ?? 0}ms→{Num(r["fail"]) ?? 0} 失败"));
                sb.AppendLine("    读间隔（连发 " + (Num(rg["burst"]) ?? 0) + " 次）：" + desc);
                sb.AppendLine(Num(rg["suggest_ms"]) is { } sm
                    ? $"    建议 read_gap = {sm:0} ms（已写进本机草案；作者复核后才进正式档案）"
                    : "    没测出安全值 —— 档案里 read_gap 保持原样");
            }

            if (tj["burst_gap"] is JsonObject bgj)
            {
                var bRows = bgj["rows"] as JsonArray ?? new JsonArray();
                var bDesc = string.Join("  ",
                    bRows.OfType<JsonObject>().Select(r =>
                        $"{Num(r["gap_ms"]) ?? 0}ms→{(r["ok"]?.ToJsonString() == "true" ? "OK" : "失败")}"));
                sb.AppendLine("    背光连发（单条 " + (Num(bgj["single_ms"]) ?? 0) + " ms）：" + bDesc);
                sb.AppendLine(Num(bgj["suggest_ms"]) is { } bm
                    ? $"    建议 burst_gap = {bm:0} ms（三个区一起变，而不是一个一个爬）"
                    : "    没测出安全值 —— 这台机器的背光只能逐条发");
            }

            if (tj["echo"] is JsonArray eh)
            {
                var okCmds = eh.OfType<JsonObject>()
                    .Where(o => o["echo"]?.ToJsonString() == "true")
                    .Select(o => Str(o["cmd"])).ToList();
                sb.AppendLine("    回显可校验：" + (okCmds.Count > 0 ? string.Join(" / ", okCmds) : "（一条都没有）"));
                sb.AppendLine(okCmds.Count > 0
                    ? "      ⇒ 这些读会校验回帧命令号：被原厂控制中心抢占时重试，而不是把脏帧当读数"
                    : "      ⇒ 这类读只能靠间隔防抢占，speed 上的结论以包里的 timing 段为准");
            }
            sb.AppendLine();
        }

        sb.AppendLine("  要发给作者的文件");
        sb.AppendLine("    " + Path.Combine(logsDir, stem + ".json"));
        if (didProbe)
            sb.AppendLine("    这一个就够了：采集结论 + 每一帧采样 + 写入验证的读回值都在里面，");
        else if (GuideConfirmedAny(report))
            sb.AppendLine("    这一个就够了：采集结论 + 每一帧采样 + 引导里抓到的原厂写帧都在里面"
                          + "（没做写入验证，但档位 / 分区 / 槽位是从原厂真实报文里认的），");
        else
            sb.AppendLine("    这一个就够了：采集结论 + 每一帧采样都在里面（这次没验写入，作者只能先做只读适配），");
        sb.AppendLine("    作者一条命令就能转成正式机型档案。聊天窗口贴不了文件的话，贴同名 .txt 全文。");
        sb.AppendLine();

        sb.AppendLine("  自动识别出的偏移：");
        if (advice.Guesses.Count == 0)
        {
            sb.AppendLine("    （没有）");
        }
        else
        {
            foreach (var g in advice.Guesses)
                sb.AppendLine($"    {g.Field,-16} {g.Cmd}@{g.Offset,-3} [{g.Confidence}]  {g.Why}");
        }
        sb.AppendLine();

        if (advice.Missing.Count > 0)
        {
            sb.AppendLine("  还差什么：");
            foreach (var m in advice.Missing) sb.AppendLine("    · " + m);
            sb.AppendLine();
        }

        foreach (var n in advice.Notes) sb.AppendLine("  " + n);
        sb.AppendLine();

        return sb.ToString();
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>风扇结论里"认在哪几个偏移"那一段 —— 档案、采集包、界面回执共用同一个口径。</summary>
    private static JsonObject FanLayoutJson(IReadOnlyDictionary<string, (int Offset, int Len)> layout)
    {
        var o = new JsonObject();
        foreach (var (field, (off, len)) in layout)
            if (field is "fan1_rpm" or "fan2_rpm" or "fan3_rpm") o[field] = new JsonArray(off, len);
        return o;
    }

    /// <summary>布局里认出了几路风扇（0/1/2）。</summary>
    private static int FanCountOf(LayoutAdvice advice)
        => advice.Layout.Keys.Count(k => k is "fan1_rpm" or "fan2_rpm" or "fan3_rpm");

    /// <summary>
    /// 认出来的那几路，整段序列原样贴进包里。
    /// 形状判定最怕的错是"把温度×100 这类平滑字段当成转速"—— 作者看一眼量级就能否掉，
    /// 只给一个偏移数字则看不出来。
    /// </summary>
    private static JsonObject FanSeriesJson(IReadOnlyList<LayoutSample> samples, FanAdvice a)
    {
        var o = new JsonObject();
        foreach (var g in a.Guesses)
        {
            if (g.Field is not ("fan1_rpm" or "fan2_rpm" or "fan3_rpm")) continue;
            // 偏移 → 帧数组下标：DAT2..DAT8 = 4/8/12/16/20/24/28 → 0..6。
            // 别写死 5 项（2026-11-16）：数据区尾巴上那两路也可以是转速。
            var idx = g.Offset is >= 4 and <= 28 && g.Offset % 4 == 0 ? g.Offset / 4 - 1 : -1;
            if (idx < 0) continue;

            var arr = new JsonArray();
            foreach (var s in samples)
                if (s.Frames.TryGetValue(g.Cmd, out var vals) && idx < vals.Length)
                    arr.Add((int)vals[idx]);
            o[g.Field] = arr;
        }
        return o;
    }

    /// <summary>
    /// 已经认给别的字段的 (命令, 偏移)。
    /// 少了这一步，温度那个偏移会被风扇判定再认一遍 —— 界面上就会有一个"转速"显示成 58。
    /// </summary>
    private static IEnumerable<(string Cmd, int Offset)> TakenSlots(LayoutAdvice advice)
        => advice.Guesses
            .Where(g => g.Confidence is "high" or "medium"
                        && g.Field is not ("fan1_rpm" or "fan2_rpm" or "fan3_rpm"))
            .Select(g => (g.Cmd, g.Offset));

    /// <summary>温度认在哪条读命令里 —— 转速与温度通常在同一帧（`status`），优先从那一条里找。</summary>
    private static string? TempCmd(LayoutAdvice advice)
        => advice.Guesses.FirstOrDefault(g =>
            (g.Field is "cpu_temp" or "gpu_temp") && (g.Confidence is "high" or "medium"))?.Cmd;

    private static double? Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<uint>(out var u)) return u;
            if (v.TryGetValue<int>(out var i)) return i;
        }
        return null;
    }

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }

    /// <summary>
    /// 引导里有没有真的确认到本事（显卡那条恒开、不算 —— 与界面「启用本机」同一口径）。
    /// 只读采集靠"看原厂发命令"就能拿到档位 / 分区 / 槽位，这时不能再笼统说"都要写权限"。
    /// </summary>
    private static bool GuideConfirmedAny(JsonObject report)
        => report["guide"]?["findings"] is JsonArray findings
           && findings.OfType<JsonObject>().Any(o =>
                  o["value"] is not null && Str(o["step"]) != AdoptGuide.GfxStep);
}
