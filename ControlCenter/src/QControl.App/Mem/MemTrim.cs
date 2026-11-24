using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace QControl.App.Mem;

/// <summary>
/// 「把工作集还给系统」—— 唯一还能真正压低常驻内存的动作（2026-10-27 加）。
///
/// 为什么需要它：GC（工作站 + 非并发 + Conserve 9 + LOH 门槛 128K）只能把**托管堆**
/// 里没人要的对象还给 OS。它管不了两类页：
///   ① 已经 JIT 过的代码页、加载过的 dll 的私有脏页；
///   ② 托管堆里"还活着但暂时用不到"的那部分（例如刚关窗时整棵可视树的残留代）。
/// 这两类只有 `SetProcessWorkingSetSize(-1,-1)` 能把它们换到页面文件里 ——
/// 实测托盘态 71 MB → 20 MB 量级。代价是下次访问要缺页（NVMe 上几十毫秒）。
///
/// ⚠️ **这不是"改数字"**：被换出的是真实物理页，系统确实拿回了内存；
///    数据没有丢，仍在提交（Private Bytes）里，只是不在 RAM 里。所以
///    "任务管理器内存列变小了但提交没变"是预期现象，不是骗人。
/// ⚠️ **只在界面关闭时做**：窗口开着时 trim 会让下一次重建 UI 卡一下，
///    而用户正盯着界面 —— 那是"慢"换"数字"，不值。判据由
///    <see cref="NoteUiOpen"/> / <see cref="NoteUiClosed"/> 维护，
///    **故意不接受外部传参**：否则每次调用点都要自己判断窗口状态，
///    早晚有一处判断写反。
/// ⚠️ 调用方**不要同步等**：一次 trim 含两轮阻塞 GC（几十毫秒 STW），
///    采样线程上直接调会让那一拍明显拖长。一律走 <see cref="TrimAsync"/>。
/// </summary>
internal static class MemTrim
{
    private static readonly object Gate = new();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

    /// <summary>
    /// 兜底周期：距上次满这么久**无条件**收一次（哪怕工作集看着不高 ——
    /// 提交里那部分换出去了但页框还被钉着，用户看的是任务管理器那一列）。
    /// </summary>
    public static readonly TimeSpan IdlePeriod = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 两次自动 trim 的最小间隔（防止"刚收完就爬回去"导致反复缺页）。
    /// </summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 工作集到这条线以上才值得动手（2026-11-02 实测后定：**55 MB**）。
    ///
    /// 为什么不是更低：托盘态修完那几条泄漏（WMI/COM 枚举、HWiNFO 重复解析、WPF 不再进进程）
    /// 之后，**稳态就在 50~57 MB**，而且 trim 完 45 秒内必然回填到同一水平 ——
    /// 那是每秒采样真正在用的代码页/数据页，换出去马上又缺页拉回来。
    /// 阈值设在 42 会让进程每 5 分钟白换一次 50 MB 页（磁盘读 + 几十毫秒 STW），
    /// 平均下来只把数字从 53 拉到 49，不值。
    /// 真涨的情况（关了一次界面、长时间漂移）才该收 —— 关窗那一次由 UiHost 自己触发，
    /// 这里管的是"漏出来的那部分"。到 55 MB 就收，15 分钟兜底一定收一次。
    /// </summary>
    public const double HighMb = 55;

    private static bool _uiOpen;
    private static bool _running;
    private static long _lastTick;                 // Environment.TickCount64；0 = 从未跑过
    private static JsonObject _last = LastSkeleton();

    /// <summary>界面是否开着（由 <c>UiHost</c> 维护，本类不碰任何 WPF 类型）。</summary>
    public static bool UiOpen { get { lock (Gate) return _uiOpen; } }

    public static void NoteUiOpen() { lock (Gate) _uiOpen = true; }

    public static void NoteUiClosed() { lock (Gate) _uiOpen = false; }

    /// <summary>
    /// 现在轮到"空闲归还"了吗（采样循环每分钟问一次）。
    ///
    /// 三个条件缺一不可：界面关着、没有别的 trim 在跑、距上次够久。
    /// 为什么不在关窗时一次性做完就算：托盘常驻时每秒采样还在分配对象，
    /// 工作集会慢慢爬回来，隔一段收一次才能长期停在低位。
    ///
    /// 判据（2026-11-02）：
    ///   · 距上次满 <see cref="IdlePeriod"/>(15 分钟) ⇒ **无条件**收（兜底）；
    ///   · 否则距上次不足 <see cref="MinGap"/>(10 分钟) ⇒ 不收（防抖）；
    ///   · 否则只在工作集 ≥ <see cref="HighMb"/>(55 MB) 时收 —— 没到线就一动不动。
    /// </summary>
    public static bool IdleDue
    {
        get
        {
            lock (Gate)
            {
                if (_uiOpen || _running) return false;
                if (_lastTick == 0) return true;        // 从没跑过（首轮启动预热后）

                var since = Environment.TickCount64 - _lastTick;
                if (since >= IdlePeriod.TotalMilliseconds) return true;
                if (since < MinGap.TotalMilliseconds) return false;

                // ⚠️ 用 Environment.WorkingSet（不建 Process 对象）：这个判据每分钟问一次，
                //    为读一个数去 new 一个 Process 属于自己给自己添垃圾。
                var ws = Environment.WorkingSet / 1048576.0;
                return ws >= HighMb;
            }
        }
    }

    /// <summary>最近一次 trim 的结果（`GET /api/debug/mem` 里给出）。</summary>
    public static JsonObject Last { get { lock (Gate) return (JsonObject)_last.DeepClone(); } }

    /// <summary>丢到线程池去做，调用方立刻返回（关窗路径 / 采样循环都用它）。</summary>
    public static void TrimAsync(string why)
        => ThreadPool.QueueUserWorkItem(_ => Run(why));

    /// <summary>
    /// 真正干活：GC(2) ×2 + LOH 压缩 + 归还工作集。返回 (before_mb, after_mb)；
    /// 已经有一次在跑时返回 (-1,-1) —— 并发 trim 没有意义，只会互相把页换进换出。
    /// </summary>
    public static (double Before, double After) Run(string why)
    {
        lock (Gate)
        {
            if (_running) return (-1, -1);
            _running = true;
        }

        var before = -1.0;
        var after = -1.0;
        var ok = false;
        var sw = Stopwatch.StartNew();
        try
        {
            before = WorkingSetMb();

            // 和 UiHost 关窗时那套一致：先常规回收两遍（第一遍清 finalizable 残留），
            // 再压缩 LOH。LOH 是反复开关窗口时最容易碎住的地方。
            try
            {
                GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            }
            catch
            {
                // 回收失败不致命：大不了这轮堆不还给 OS。
            }

            try
            {
                // (-1,-1) = "把工作集交给系统自己去修剪"，这是 Win32 约定的写法。
                ok = SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
            }
            catch
            {
                // 极端环境（受保护的进程/ACL 异常）拿不到句柄：如实记 ok=false，不假装成功。
            }

            after = WorkingSetMb();
            return (before, after);
        }
        finally
        {
            sw.Stop();
            lock (Gate)
            {
                _running = false;
                _lastTick = Environment.TickCount64;
                _last = new JsonObject
                {
                    ["why"] = why,
                    ["ok"] = ok,
                    ["before_mb"] = before < 0 ? null : Math.Round(before, 1),
                    ["after_mb"] = after < 0 ? null : Math.Round(after, 1),
                    ["saved_mb"] = before < 0 || after < 0 ? null : Math.Round(before - after, 1),
                    ["ms"] = (int)sw.ElapsedMilliseconds,
                    ["at"] = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    ["at_text"] = DateTime.Now.ToString("HH:mm:ss"),
                };
            }

            // 只在真降下来时才写一行：托盘常驻期间日志本来就该是安静的。
            if (after > 0 && before > after)
                Console.WriteLine($"[mem] 归还工作集（{why}）：{before:0.0} → {after:0.0} MB，"
                                  + $"耗时 {sw.ElapsedMilliseconds} ms");
        }
    }

    private static double WorkingSetMb()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            return me.WorkingSet64 / 1048576.0;
        }
        catch
        {
            return -1;
        }
    }

    private static JsonObject LastSkeleton() => new()
    {
        ["why"] = null,
        ["ok"] = null,
        ["before_mb"] = null,
        ["after_mb"] = null,
        ["saved_mb"] = null,
        ["ms"] = null,
        ["at"] = null,
        ["at_text"] = null,
    };
}
