using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Ec;
using QControl.Core.Logs;
using QControl.Core.Model;

namespace QControl.App;

/// <summary>观察会话的产出：给采集包/档案看的 <c>Json</c>，加上去重后的 <c>Writes</c> 供推断。</summary>
internal sealed record ObservationResult(JsonObject Json, IReadOnlyList<ObservedWrite> Writes,
                                         IReadOnlyList<LayoutSample> FanSamples);

/// <summary>
/// 「观察原厂」—— 抓原厂控制中心真实下发的 EC 写命令。
///
/// 原理：<c>RW_GMWMI.BufferBytes</c> 是跨进程共享的单槽缓冲区，原厂 ControlCenter.exe
/// 写 EC 走的是同一个 WMI 实例，它写→Put 之后那 32 字节报文会残留在缓冲区里，
/// 直到下一次被覆盖（我们自己的读组帧 0xFA00 也会覆盖它，所以这里只认 0xFB00）。
/// 于是**高频纯读**（<c>GmWmi.PeekBuffer</c>，一个字节都不写）就能把原厂的写命令
/// 一条条抓下来 —— 不需要 hook、不需要注入、不需要驱动。
///
/// 一条前置条件，别忽略：观察期间必须**暂停我们自己的风扇曲线**。曲线每秒往 EC 写
/// 0xFB00/0x0205（UFA），那帧和原厂写的手动风扇**字节一模一样**，不暂停就分不清谁写的。
/// 观察结束立刻恢复接管，窗口只有几十秒，风扇停在暂停前的转速上，安全。
///
/// ⚠️ 抓到的只进**证据**（observation.write_cmds），不据此开写权限 ——
/// 语义（分区/路数/档位映射）由 <see cref="QControl.Core.Devices.OemObservation"/> 推断，
/// 作者复核之后才算数。
/// </summary>
internal static class MachineObserve
{
    /// <summary>默认观察时长（秒）。够用户在原厂里把三档性能/键盘灯/显卡模式各切一遍。</summary>
    public const int DefaultSeconds = 60;

    /// <summary>
    /// 走引导时的默认观察时长。几步都要在原厂里翻一下，60 秒常常不够；
    /// 但步骤做完会自动收尾（<see cref="GuideSession.Mark"/> 返回 true 时界面请求结束），
    /// 所以放宽到 150 秒不会让用户干等 —— 只有他中途放弃才会走满。
    /// </summary>
    public const int GuidedSeconds = 150;

    /// <summary>两次 PeekBuffer 之间的休息（毫秒）。**0 = 不休息**。
    ///
    /// 原厂写帧在 BufferBytes 里的存活窗口只有几毫秒（2026-11-10 实测：60 秒抓到 21 条，
    /// 采样 29792 次）。`PeekBuffer` 本身要约 2.5ms，再加 sleep 就成 3 倍间隔，
    /// 命中率会掉一半 —— 观察窗口只有几十秒，宁可短时间吃满一个核也不能漏。</summary>
    private const int PeekSleepMs = 0;

    /// <summary>
    /// 风扇步骤里两帧 status 之间的间隔。
    ///
    /// ⚠️ 别按"理论上 200ms 节流"来定这个数：2026-11-14 真机实测**一帧 status 往返约 0.9 秒**
    ///    （WMI Put + 重取实例 + 节流），所以 250ms 的门槛根本不生效，实测 13 秒只拿到 14 帧。
    ///    这里留 400ms 是为了给 PeekBuffer 留一点时间（读的时候抓不到原厂写帧），
    ///    实际密度约 1.3 秒一帧 —— 引导提示因此要求用户把风扇保持 5~6 秒。
    /// </summary>
    private const int FanSampleMs = 400;

    /// <summary>风扇采样上限（250ms × 400 ≈ 100 秒）。远超任何一段引导窗口，纯粹防跑飞。</summary>
    private const int FanSampleCap = 400;

    /// <summary>
    /// 跑一轮观察会话，返回 <c>observation</c> 段（抓到的写命令 + 会话元信息）。
    /// </summary>
    /// <param name="seconds">观察时长（10~300 秒，缺省 <see cref="DefaultSeconds"/>）。</param>
    /// <param name="stage">进度回调（界面靠它显示「抓到第几条」）。</param>
    /// <param name="stop">提前结束信号（界面「完成」按钮）。返回 true 就收尾。</param>
    /// <param name="guide">
    /// 引导会话：给了就按"当前第几步"计数，并在结束时由调用方取走标记做语义切窗
    /// （见 <see cref="QControl.Core.Devices.AdoptGuide"/>）。不给 = 只在报告里留原始帧。
    /// </param>
    public static ObservationResult Observe(AppState st, int seconds, Action<string> stage,
                                            Func<bool>? stop = null, GuideSession? guide = null)
    {
        stop ??= static () => false;
        var span = Math.Clamp(seconds <= 0 ? DefaultSeconds : seconds, 10, 300);
        // 时间戳一律走本地时间：采集包其余字段（created / write_test.log.items[].at）都是本地，
        // 这里若用 UtcNow 会整体差 8 小时，作者对照包里的顺序会读错。
        var started = DateTimeOffset.Now;
        var fanWasRunning = st.Fan.Running;

        st.ResetObserveStop();
        // 引导会话从**观察这一刻**起算：第一个标记之前抓到的帧算第一步的窗口
        // （用户还没动原厂时若有残留写帧，归到办公档那一步也合理 —— 那通常就是当前档位）。
        // ⚠️ 在"原厂没跑"的提前返回之前就 Start：否则上一轮的标记会留在会话里，
        //    下一次采集拿到的是**上一次**的步骤时间，切出来的窗口全是错的。
        guide?.Start(started);

        // 原厂全没跑 ⇒ 一条写命令都抓不到（daemon 空闲时不写 EC）。
        // 别让用户守着 60 秒的白进度条，直接跳过并留下原因。
        if (!st.VendorAlive)
        {
            guide?.End();
            stage("原厂控制中心没在运行，跳过观察（抓不到写命令）…");
            var at = Stamp(started);
            return new ObservationResult(new JsonObject
            {
                ["skipped"] = "原厂控制中心没在运行（ControlCenter / daemon 都没找到）",
                ["started"] = at,
                ["ended"] = at,
                ["duration_s"] = 0,
                ["peek_count"] = 0,
                ["write_cmds"] = new JsonArray(),
                ["fan_paused"] = false,
            }, [], []);
        }

        st.SetObserving(true);

        stage("暂停风扇曲线，准备抓原厂命令…");
        if (fanWasRunning)
        {
            // release:false —— 一个字节都不写（写「当前温度对应转速」会污染黑板）。
            // Stop 后 LastError 保持 null，HealFanCurve 不会在观察期间重新接管。
            st.Fan.Stop(release: false);
        }

        // At 用 DateTimeOffset（不是字符串）：引导切窗要拿它和用户标记做时间比较，
        // 字符串比较在跨秒/跨分时不可靠。字符串只留给报告里的 t/last 显示。
        var writes = new List<(byte[] Raw, DateTimeOffset At, string First, string Last, int Seen)>();
        var peek = 0;
        byte[] last = [];

        // 「风扇」那一步期间顺带采 status 帧 —— 转速只能靠"变速的形状"认，
        // 而用户正好在那一刻把风扇拉起来/切 Max（见 FanLayoutAdvisor）。
        // 这段采样**只读**，不写任何东西；每 250ms 一次，其余时间照常盯缓冲区。
        var fanSamples = new List<LayoutSample>();
        var fanSampleAt = DateTimeOffset.MinValue;

        try
        {
            // 原厂主界面开着 ⇒ 让用户去点；只剩后台 daemon ⇒ 只能靠 FN 热键（没有可点的界面）。
            stage(guide is not null
                ? "照小窗里的步骤在原厂里做，每步点「完成这步」…"
                : st.VendorHasUi
                    ? "到原厂里切换：性能三档 / 键盘灯 / 风扇模式…"
                    : "原厂界面没开 —— 按 FN+3 切一次性能档，或打开原厂后再切…");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(span);
            // 把**实际**截止时刻交给引导会话：小窗靠它显示"还剩 N 秒"。延长时会再交一次。
            guide?.SetDeadline(deadline);

            while (DateTimeOffset.UtcNow < deadline && !stop())
            {
                // 「再等 60 秒」（2026-11-14）：观察是固定窗口，到点就按"没走到这一步"
                // 收尾。用户正在原厂里翻那个开关 / 临时被叫走时，一次明确的延长
                // 比让他整轮重采两分半便宜。请求由界面置位，这里取一次清零。
                var extend = st.TakeObserveExtend();
                if (extend > 0)
                {
                    deadline = deadline.AddSeconds(extend);
                    guide?.SetDeadline(deadline);
                    stage($"已延长 {extend} 秒 —— 接着做，每步点「完成这步」…");
                }

                // 风扇步骤：先补一帧 status（约 2.5ms 往返），再继续盯缓冲区。
                // 放在 peek 之前 —— 采样写进缓冲区的读组帧本来就会被下面的 0xFB00 过滤掉。
                if (guide?.CurrentKind == GuideStepKind.Fan)
                    SampleFan(st, fanSamples, ref fanSampleAt);

                byte[] raw;
                try
                {
                    raw = st.Ec.Transport.PeekBuffer();
                }
                catch (Exception e)
                {
                    // 非管理员 / WMI 异常：读不到黑板。别在 60 秒里疯狂重试刷日志。
                    Thread.Sleep(200);
                    _ = e;
                    continue;
                }
                peek++;

                if (raw.Length != EcFrame.Size)
                {
                    last = raw;
                    Thread.Sleep(PeekSleepMs);
                    continue;
                }

                var gwf0 = (ushort)(raw[0] | (raw[1] << 8));
                var isZero = raw.AsSpan().IndexOfAnyExcept((byte)0) < 0;

                // 只认写组：读组 0xFA00 是我们自己读 status 留下的，全 0 是原厂写完清空/未就绪。
                if (gwf0 != 0xFB00 || isZero)
                {
                    last = raw;
                    Thread.Sleep(PeekSleepMs);
                    continue;
                }

                // 去重：连续读到同一帧 = 原厂那条命令还在缓冲里，合并 seen。
                if (last.SequenceEqual(raw) && writes.Count > 0)
                {
                    var (r, at, f, _, s) = writes[^1];
                    writes[^1] = (r, at, f, Now(), s + 1);
                }
                else
                {
                    var gwf1 = (ushort)(raw[2] | (raw[3] << 8));
                    writes.Add((raw, DateTimeOffset.Now, Now(), Now(), 1));
                    // 引导会话按"当前第几步"计数：只数新帧、且只数这一步关心的子命令
                    // （原厂自动风扇 0x0205 会周期性刷屏，不能拿它冒充"切档成功"）。
                    guide?.Note(gwf1);
                    stage($"抓到写命令 0x{gwf1:X4}（第 {writes.Count} 条）…");
                }
                last = raw;
                Thread.Sleep(PeekSleepMs);
            }
        }
        finally
        {
            if (fanWasRunning)
            {
                // 恢复接管：观察结束，风扇交回软件曲线。
                try { st.Fan.Start(); } catch { /* 恢复失败留在日志，别让观察白跑 */ }
            }
            st.SetObserving(false);
            guide?.End();
        }

        var ended = DateTimeOffset.Now;
        var cmds = new JsonArray();
        var observed = new List<ObservedWrite>();
        foreach (var (raw, at, first, lastT, seen) in writes)
        {
            var gwf0 = (ushort)(raw[0] | (raw[1] << 8));
            var gwf1 = (ushort)(raw[2] | (raw[3] << 8));
            cmds.Add(new JsonObject
            {
                ["t"] = first,
                ["last"] = lastT,
                ["gwf0"] = CmdTable.Hex(gwf0),
                ["gwf1"] = CmdTable.Hex(gwf1),
                ["raw"] = string.Join(" ", raw.Select(x => x.ToString("X2"))),
                ["seen"] = seen,
            });
            observed.Add(new ObservedWrite(gwf0, gwf1, raw, at));
        }

        var json = new JsonObject
        {
            ["started"] = Stamp(started),
            ["ended"] = Stamp(ended),
            ["duration_s"] = Math.Round((ended - started).TotalSeconds, 1),
            ["peek_count"] = peek,
            ["write_cmds"] = cmds,
            ["fan_paused"] = fanWasRunning,
            ["fan_samples"] = fanSamples.Count,
        };
        return new ObservationResult(json, observed, fanSamples);
    }

    /// <summary>风扇步骤里每 250ms 补一帧 status。读失败就当这一帧没有 —— 采样断几帧不影响形状判定。</summary>
    private static void SampleFan(AppState st, List<LayoutSample> into, ref DateTimeOffset lastAt)
    {
        if (into.Count >= FanSampleCap) return;
        var now = DateTimeOffset.Now;
        if ((now - lastAt).TotalMilliseconds < FanSampleMs) return;
        lastAt = now;

        var frame = ReadStatusFrame(st);
        if (frame is not null) into.Add(new LayoutSample(double.NaN, double.NaN, double.NaN, frame));
    }

    /// <summary>
    /// 读一帧 status（只读）。取法与 <see cref="EcSampling.StatusFrame"/> 共用一份实现 ——
    /// 引导里的风那段、加负载那段、写探针那段必须同一口径，否则同一台机器会算出两套偏移。
    /// </summary>
    private static Dictionary<string, uint[]>? ReadStatusFrame(AppState st) => EcSampling.StatusFrame(st);

    /// <summary>采集包里的时间戳口径：本地时间 + 到毫秒，与 <c>write_test.log.items[].at</c> 对齐。</summary>
    private static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fff");

    private static string Now() => Stamp(DateTimeOffset.Now);
}
