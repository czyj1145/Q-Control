using System.Management;

namespace QControl.Hal.Sensors;

/// <summary>
/// 「本次开机 / 唤醒至今」的起点 —— 开机时长**唯一**的判定口。
///
/// <para>
/// ⚠️ 为什么不能直接用 `Environment.TickCount64`（2026-10-23 之前 `SystemSensors` 的做法）：
/// 那个计数只在**内核冷启动**时归零。Windows 的快速启动（`HiberbootEnabled=1`）是把内核会话
/// 休眠进 hiberfile、下次开机再原样恢复 —— 内核压根没重启，计数一路累加；睡眠唤醒同理。
/// 于是「刚开机」能显示出「2 天」。本机实测（2026-10-23 19:25）：冷启动停在 10-20 22:59:35
/// （`LastBootUpTime` / 事件 6005 / `Kernel-General` 12 三个源一致），之后的每一次开机都是
/// 快速启动（`Kernel-Boot` id=27「引导类型为 0x1」= 从休眠恢复），计数从来没归零过。
/// 顺带一提：任务管理器「正常运行时间」也是这个口径，所以它同样会显示 2 天。
/// </para>
///
/// <para>
/// 真正的「这次机器开始可用」取下面两者的**较晚者**：
/// · `Win32_OperatingSystem.LastBootUpTime` —— 上次**冷启动**（快速启动不更新它）；
/// · `Win32_LogonSession`（`LogonType=2`）里最新的 `StartTime` —— 冷启动、快速启动、
///   睡眠唤醒**都会**新建交互式会话。本机实测这条时间戳序列（19:19 / 12:33 / 20:59 / 11:48…）
///   与 `Power-Troubleshooter` 的唤醒记录逐条对得上，是「这次开机」最贴边的信号。
/// </para>
///
/// <para>
/// 探测只在很可能已经变了的时刻做：平时连一次 WMI 都不发（见 <see cref="SleepSlackSec"/>）。
/// 之所以能自己发现「睡过」—— 睡眠 / 休眠期间墙钟照走，而 `TickCount64` 不走，
/// 两者的差值就是睡过去的那段时间。
/// </para>
/// </summary>
public static class BootClock
{
    /// <summary>一次探测的结果。</summary>
    /// <param name="Start">本次开机 / 唤醒的时刻（本地时间）。</param>
    /// <param name="ColdBoot">
    /// true = 起点就是上次冷启动（之后没睡过、也没重新登录过）；
    /// false = 起点来自一次登录 / 唤醒。
    /// </param>
    public readonly record struct BootStamp(DateTime Start, bool ColdBoot);

    /// <summary>
    /// 墙钟比 `TickCount64` 多走多少秒才算「中间睡过」。
    /// 放宽到 5 秒是躲开 NTP 校时的小跳变 —— 误判的代价只是一次多余的探测。
    /// </summary>
    private const double SleepSlackSec = 5.0;

    /// <summary>探测失败后的冷却秒数。免得每次刷新都去打 WMI。</summary>
    private const double RetryAfterSec = 30.0;

    private static readonly object _lock = new();
    private static BootStamp? _stamp;
    private static DateTime _probeAt;
    private static long _probeTick;

    /// <summary>
    /// 本次开机 / 唤醒至今的秒数。
    /// 取不到起点时退回 `TickCount64`（口径是老的那套，但总比这一行空掉强）。
    /// </summary>
    public static double UptimeSeconds()
    {
        var s = Current();
        return s is { } st
            ? Math.Max(0, (DateTime.Now - st.Start).TotalSeconds)
            : Environment.TickCount64 / 1000.0;
    }

    /// <summary>本次起点。两个源都取不到时返回 null（调用方整行隐藏）。</summary>
    public static BootStamp? Current()
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            var tick = Environment.TickCount64;

            var stale = _stamp is null
                     || _stamp.Value.Start > now   // 系统时间被往回调过，旧起点已经站不住
                     || (now - _probeAt).TotalSeconds - (tick - _probeTick) / 1000.0 > SleepSlackSec;

            if (!stale) return _stamp;

            // 上次探测刚失败过 → 冷却期内不要再打 WMI
            if (_stamp is null && (now - _probeAt).TotalSeconds < RetryAfterSec) return null;

            var got = Probe(now);
            _probeAt = now;
            _probeTick = tick;
            if (got is { } g) _stamp = g;
            return _stamp;
        }
    }

    // ---------------------------------------------------------------- 探测

    /// <summary>取「这次机器开始可用」的时刻。任一源挂掉都不连坐另一个。</summary>
    private static BootStamp? Probe(DateTime now)
    {
        var boot = MaxDate(LastBootUpTime(), now);
        var logon = MaxDate(LatestInteractiveLogon(), now);

        // 有比冷启动更新的登录 / 唤醒 ⇒ 那才是这次开机的时刻
        if (logon is { } lg && (boot is null || lg > boot.Value))
            return new BootStamp(lg, ColdBoot: false);

        return boot is { } bt ? new BootStamp(bt, ColdBoot: true) : null;
    }

    /// <summary>上次**冷启动**时刻。快速启动不会更新它（这正是我们要的）。</summary>
    private static DateTime? LastBootUpTime()
    {
        DateTime? best = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT LastBootUpTime FROM Win32_OperatingSystem"));

            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
            {
                using (mo) { Pick(Dmtf(mo["LastBootUpTime"] as string), ref best); }
            }
        }
        catch { /* 取不到就不参与比较 */ }
        return best;
    }

    /// <summary>
    /// 最新的交互式登录会话起点。`LogonType=2` 才是真人在本机登录
    /// （排除 `LogonType=4/5` 这类批处理和服务的会话，它们不随开机刷新）。
    /// </summary>
    private static DateTime? LatestInteractiveLogon()
    {
        DateTime? best = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT StartTime FROM Win32_LogonSession WHERE LogonType = 2"));

            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
            {
                using (mo) { Pick(Dmtf(mo["StartTime"] as string), ref best); }
            }
        }
        catch { /* 取不到就不参与比较 */ }
        return best;
    }

    /// <summary>留最大的那个，且**不接受未来的时间**（脏数据会让时长算成负数）。</summary>
    private static void Pick(DateTime? v, ref DateTime? best)
    {
        if (v is null) return;
        if (best is null || v.Value > best.Value) best = v;
    }

    private static DateTime? MaxDate(DateTime? v, DateTime now)
        => v is { } t && t <= now ? t : null;

    /// <summary>DMTF 时间字符串（带 UTC 偏移）→ 本地时间。`Win32_LogonSession` 用这个格式。</summary>
    private static DateTime? Dmtf(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }
}
