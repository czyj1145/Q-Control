namespace QControl.Hal.Sys;

/// <summary>
/// HWiNFO 读失败的自救计数器：连续失败计数 / 成功清零 / 攒到阈值就重启。
///
/// **为什么要有**：HWiNFO 会在两种情况下不出数，而界面只会表现为「温度/PCH 那一行
/// 突然没了」：① 进程被关 / 崩了；② 跑满 12 小时（免费版）后**静默停止轮询** ——
/// 进程还在，共享内存却冻住。两者的处理不同：① 重新拉起，② 只有重启进程才能恢复。
///
/// ⚠️ **只在整个共享内存读不到时计数**。单个指标天然可能
///    没有（电池满电时充放电功率恒 0、集显模式下没有 NVIDIA 组），拿它当
///    「HWiNFO 挂了」会没完没了地重启进程。
/// ⚠️ 阈值是**阶梯**而不是「一次就重启」：刚关掉 / 刚崩溃时十几秒内就会回来，
///    立刻抢着重启反而会和用户的操作打架。成功读到数据时计数清零，所以这些点
///    只在**持续失败**时命中。
/// </summary>
public static class HwInfoHealth
{
    /// <summary>
    /// 连续失败到第几次时在后台尝试拉起 / 自救。
    ///
    /// ⚠️ **计量单位是看门狗轮次，不是秒**：看门狗每 2 秒跑一次 ⇒
    ///    3 / 10 / 60 / 300 对应的真实时间是 6 秒 / 20 秒 / 2 分钟 / 10 分钟。
    ///    这个节奏不需要更激进 —— 数据没了是「少一行」，不是「算错数」。
    /// </summary>
    private static readonly int[] SpawnAt = [3, 10, 60, 300];

    /// <summary>连续失败超过这么久、进程却还活着 ⇒ 疑似 12 小时到期，重启进程。</summary>
    private const double HealAfterSeconds = 600;

    private static readonly object Gate = new();
    private static int _streak;
    private static DateTime _since;
    private static bool _healed;

    /// <summary>当前状态（诊断口 `stats` 用）。</summary>
    public static (int Streak, bool Healed, double FailedForSeconds) State
    {
        get
        {
            lock (Gate)
            {
                var dur = _streak == 0 ? 0 : (DateTime.UtcNow - _since).TotalSeconds;
                return (_streak, _healed, Math.Round(dur, 1));
            }
        }
    }

    /// <summary>读到数据：清零。必须在**每次成功**时调（否则失败计数会跨小时累积）。</summary>
    public static void NoteOk()
    {
        lock (Gate)
        {
            _streak = 0;
            _healed = false;
        }
    }

    /// <summary>
    /// 读不到：+1，返回累计次数。调用方按 <see cref="ShouldSpawn"/> /
    /// <see cref="ShouldHeal"/> 决定是否真去动进程（这里**不做**任何阻塞动作）。
    /// </summary>
    public static int NoteFail()
    {
        lock (Gate)
        {
            if (_streak == 0) _since = DateTime.UtcNow;
            return ++_streak;
        }
    }

    /// <summary>
    /// 主动停下来时清零（2026-10-21：按需模式下我们会自己把 HWiNFO 收掉）。
    ///
    /// ⚠️ 为什么必须清：失败计数是"共享内存读不到"的连续次数，而我们主动停掉进程
    ///    之后**本来就该读不到**。不清零的话，下一次切回"需要"时会带着旧的 streak
    ///    直接进入阶梯点，甚至因为 `failedFor` 已经很大而触发 12 小时自愈重启 ——
    ///    表现就是「关掉界面几分钟，再打开，HWiNFO 被反复杀掉重启」。
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _streak = 0;
            _healed = false;
            _since = default;
        }
    }

    /// <summary>这个失败次数是不是「该试着拉起/重启」的阶梯点。</summary>
    public static bool ShouldSpawn(int streak) => SpawnAt.Contains(streak);

    /// <summary>
    /// 该不该做「重活」（重启还活着的进程）。只允许发生一次 ——
    /// 12 小时到期是**周期性**事件，但同一段时间里反复重启没有意义。
    /// </summary>
    public static bool ShouldHeal(int streak)
    {
        lock (Gate)
        {
            if (_healed) return false;
            if (streak <= SpawnAt[^1]) return false;
            if ((DateTime.UtcNow - _since).TotalSeconds < HealAfterSeconds) return false;
            _healed = true;
            return true;
        }
    }
}
