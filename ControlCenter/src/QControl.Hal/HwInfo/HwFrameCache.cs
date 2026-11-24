namespace QControl.Hal.HwInfo;

/// <summary>
/// 共享内存帧的 **TTL 缓存**。
///
/// 为什么需要：一次 <see cref="SharedMemoryFrame.Read"/> = 映射整段 + 解析全部
/// 传感器/读数（真机约 200 组 / 1500 条读数，每条带 label/unit 字符串）。
/// 而 <see cref="HwInfoReader"/> 的每个 <c>FindXxx()</c> 都会独立调一次帧源 ——
/// 采样一帧里就有 6 处（CPU 电压 / 显存结温 / GPU 电压 / 电池功率 / 虚拟内存 /
/// 硬盘 SMART），加上看门狗，**每秒约 6 次全量解析**，纯是垃圾回收压力。
///
/// HWiNFO 自己 2 秒才轮询一次，所以 1 秒 TTL 不会让任何读数变更旧
/// （最坏情况只比直接读多 1 秒，而源数据本身就有最多 2 秒的年龄）。
/// 帧是不可变快照（解析那一刻的值），过期直接整帧换掉，没有"半新半旧"问题。
///
/// ⚠️ 只缓存**这一处**：诊断口（<c>requireFresh:false</c> 看冻结段）不走它；
///    看门狗的新鲜度判定也**不走它**了（2026-11-03 起走 `SharedMemoryFrame.ProbeFresh`
///    只读段头 —— 那一问每 2 秒一次，为拿一个布尔去解析整帧是纯浪费）。
/// </summary>
public sealed class HwFrameCache
{
    private readonly Func<HwFrame?> _source;
    private readonly double _ttlSec;
    private readonly object _lock = new();

    private HwFrame? _frame;
    private double _at = double.NegativeInfinity;

    /// <param name="source">真正的帧源（一般是 <c>() =&gt; SharedMemoryFrame.Read()</c>）。</param>
    /// <param name="ttlSec">
    /// 缓存有效期（秒）。默认 **2 秒**（2026-11-02 从 1 秒调上来）。
    ///
    /// 依据是测出来的，不是拍的：解析一整帧要**逐条**把几百条读数的 label/unit/兜底名
    /// 解成字符串，实测 **175 KB／帧**（`/api/debug/mem` 的 `hwinfo_frame.parse_alloc_mb`
    /// ÷ `parse_calls`），而采样是 1.4 秒一拍 ⇒ 1 秒的 TTL 等于每秒解析一次、220 KB/s。
    /// 而 **HWiNFO 自己就是 2 秒才轮询一次**：2 秒以内的第二次解析，拿到的必然是同一帧
    /// 内容，纯属白烧。TTL 对齐到它的轮询周期，解析立刻降到 0.5 次/秒。
    /// ⚠️ 代价：HWiNFO 那几项（PCH / CPU 功耗电压 / 显存结温 / SMART）最多再旧 1 秒 ——
    ///    它们本来就 2 秒才更新一次，界面上看不出来。EC 温度/风扇不走这条，完全不受影响。
    /// ⚠️ 别为了省内存把它调到 5 秒以上：新鲜度上限是 45 秒，但"卡住不动"的观感会更早出现。
    /// </param>
    public HwFrameCache(Func<HwFrame?> source, double ttlSec = 2.0)
    {
        _source = source;
        _ttlSec = ttlSec > 0 ? ttlSec : 2.0;
    }

    /// <summary>
    /// 取一帧。缓存里够新就直接给（**不复制**：帧是不可变快照，多人同时读安全），
    /// 否则当场读一次并换缓存。段不存在时 <c>source</c> 返回 null，null 也同样缓存
    /// （避免每秒 6 次白跑 OpenFileMapping）。
    /// </summary>
    public HwFrame? Get()
    {
        // ⚠️ 用**单调时钟**（开机毫秒）：以前用墙钟 `UtcNow`，系统时间被 NTP 往回调之后
        //    `now - _at` 变成大负数 ⇒ `_ttlSec` 永远不满足 ⇒ 缓存**再也不刷新**，
        //    HWiNFO 那几项读数会冻结到系统时间追上来为止（2026-11-12 审查）。
        var now = Environment.TickCount64 / 1000.0;
        lock (_lock)
        {
            // ⚠️ 判据只看时间戳，**不能**再带 `_frame is not null`：段不存在时 source 返回 null，
            //    那个 null 也是要缓存的结果 —— 否则 HWiNFO 没在跑的机器每帧白跑 6 次
            //    OpenFileMapping + MapViewOfFile + CloseHandle（正是这个缓存要消掉的开销）。
            if (now - _at < _ttlSec) return _frame;
            _frame = _source();
            _at = now;
            return _frame;
        }
    }

    /// <summary>丢掉缓存（下次一定重读）。用于"刚重启完 HWiNFO"这种时刻。</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _frame = null;
            _at = double.NegativeInfinity;
        }
    }
}
