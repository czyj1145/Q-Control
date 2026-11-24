using System.Diagnostics;
using System.Text.Json.Nodes;

namespace QControl.Hal.Sensors;

/// <summary>
/// 组装 `/api/status` 里**非 EC** 的那几段：`cpu` / `gpu` / `memory` / `battery` / `network` / `system`。
///
/// 为什么单独一个类：这些读数各自有缓存与差分状态（CPU 占用率、网络速率、满充容量），
/// 必须是**长生命周期的同一个实例**才有意义 —— 每次新建一个，占用率永远返回 0。
/// </summary>
public sealed class StatusSnapshot : IDisposable
{
    private readonly CpuSensors _cpu = new();
    private readonly GpuSensors _gpu;
    private readonly SystemSensors _sys = new();
    private readonly DiskSensors _disk = new();
    /// <summary>
    /// HWiNFO 帧源。默认每次现读；上层（<see cref="QControl.App.AppState"/>）
    /// 会传共享的 <see cref="HwFrameCache"/>，让采样一帧里的 6 处读取只解析一次段。
    /// </summary>
    private readonly Func<HwInfo.HwFrame?> _hwFrame;
    /// <summary>已拿到的 HWiNFO 读取器（**只有段真的存在时才会缓存**，见 <see cref="Hwinfo"/>）。</summary>
    private HwInfo.HwInfoReader? _hwinfo;

    public StatusSnapshot() : this(null) { }

    public StatusSnapshot(HwInfo.HwFrameCache? sharedFrameCache)
    {
        // 帧源也交给 GpuSensors：核显回退那条路只有 HWiNFO 能给温度/频率
        _gpu = sharedFrameCache is null ? new GpuSensors() : new GpuSensors(sharedFrameCache.Get);
        _hwFrame = sharedFrameCache is null
            ? () => HwInfo.SharedMemoryFrame.Read()
            : sharedFrameCache.Get;
    }

    /// <summary>
    /// 拿 HWiNFO 读取器；段不存在就返回 null（SMART 五项整键不出现，界面整行隐藏）。
    ///
    /// ⚠️ **只有拿到时才缓存**。这里原来用 `Lazy<>`，第一次求值发生在启动后的
    /// 第一帧 —— 那时 HWiNFO 往往还没起来（控制中心刚启动，`EnsureHwinfo`
    /// 还在等共享内存），Lazy 会把 null **永久缓存**：之后 HWiNFO 起来了，
    /// SMART 也一辈子不出现。P7 A/B 实测到的「C# 少了 temp/life/spare/writes/reads
    /// 五项」就是它。
    /// </summary>
    private HwInfo.HwInfoReader? Hwinfo()
    {
        if (_hwinfo is not null) return _hwinfo;
        if (!HwInfo.SharedMemoryFrame.SegmentExists()) return null;
        return _hwinfo = new HwInfo.HwInfoReader(_hwFrame);
    }

    /// <summary>一次性读全。字段名与早先实现 `/api/status` 逐字一致。</summary>
    public JsonObject Read() => Read(true);

    /// <summary>
    /// 按需读：<paramref name="full"/> = false 时只取**没人看时还必须有**的那三段。
    ///
    /// 为什么要有这一档（2026-11-02）：托盘常驻时这一帧是唯一的常驻定时活动，
    /// 而 `memory` / `network` / `system` / `display` / `disk_info` 这些**只有界面会用**。
    /// 精简档保留的三段是"没有就出事"的：
    ///   · `cpu` / `gpu` —— 150 秒历史曲线的温度和功耗就是它们；缺了曲线会断线，
    ///     用户开窗时看到的正好是断的（比"晚一秒出数"难看得多）；
    ///   · `battery` —— 拔电自动切办公（`WatchPowerSource`）判的就是 `plugged`。
    /// ⚠️ 调用方要按 `demand` 决定，别在本类里猜 —— 见 `AppState` 的 `_fullUntil`。
    /// </summary>
    public JsonObject Read(bool full)
    {
        if (!full)
        {
            var t0 = Stopwatch.GetTimestamp();
            var cpu = _cpu.Read();
            var t1 = Stopwatch.GetTimestamp();
            var gpu = _gpu.Read();
            var t2 = Stopwatch.GetTimestamp();
            var batt = _sys.Battery();
            var t3 = Stopwatch.GetTimestamp();

            UsCpu = Elapsed(t0, t1);
            UsGpu = Elapsed(t1, t2);
            UsBatt = Elapsed(t2, t3);
            UsMem = UsNet = UsSys = UsDisp = UsDisk = 0;

            return new JsonObject { ["cpu"] = cpu, ["gpu"] = gpu, ["battery"] = batt };
        }

        var a0 = Stopwatch.GetTimestamp();
        var cpuFull = _cpu.Read();
        var a1 = Stopwatch.GetTimestamp();
        var gpuFull = _gpu.Read();
        var a2 = Stopwatch.GetTimestamp();
        var mem = _sys.Memory();
        var a3 = Stopwatch.GetTimestamp();
        var bat = _sys.Battery();
        var a4 = Stopwatch.GetTimestamp();
        var net = _sys.Network();
        var a5 = Stopwatch.GetTimestamp();
        var sys = _sys.System();
        var a6 = Stopwatch.GetTimestamp();
        var disp = DisplaySensors.Read();
        var a7 = Stopwatch.GetTimestamp();
        var disk = DiskInfo();
        var a8 = Stopwatch.GetTimestamp();

        UsCpu = Elapsed(a0, a1);
        UsGpu = Elapsed(a1, a2);
        UsMem = Elapsed(a2, a3);
        UsBatt = Elapsed(a3, a4);
        UsNet = Elapsed(a4, a5);
        UsSys = Elapsed(a5, a6);
        UsDisp = Elapsed(a6, a7);
        UsDisk = Elapsed(a7, a8);

        return new JsonObject
        {
            ["cpu"] = cpuFull,
            ["gpu"] = gpuFull,
            ["memory"] = mem,
            ["battery"] = bat,
            ["network"] = net,
            ["system"] = sys,
            ["display"] = disp,
            ["disk_info"] = disk,
        };
    }

    // ---------------------------------------------------------------- 拆账（/api/debug/mem 用）

    /// <summary>最近一帧里各子块的自耗时（微秒）。只为回答"26 ms/秒到底花在哪"，不参与逻辑。</summary>
    public long UsCpu, UsGpu, UsMem, UsBatt, UsNet, UsSys, UsDisp, UsDisk;

    private static long Elapsed(long from, long to)
        => (long)((to - from) * 1_000_000.0 / Stopwatch.Frequency);

    /// <summary>
    /// 硬盘档案。静态部分走 TTL 缓存（`Win32_DiskDrive` 一次 90~165 ms）；
    /// SMART 五项每次现读（HWiNFO 2 秒一轮，够新）。
    /// </summary>
    public JsonArray DiskInfo()
    {
        var arr = new JsonArray();
        foreach (var d in _disk.Info(Hwinfo())) arr.Add(d);
        return arr;
    }

    /// <summary>诊断：CPU 功耗计数器到底连上没有（`null` = RAPL 不可用）。</summary>
    public string? CpuPowerSource => _cpu.PowerSource;
    public string? CpuPowerWhy => _cpu.PowerWhy;

    public void Dispose()
    {
        _cpu.Dispose();
        _gpu.Dispose();      // 核显那条 PDH 查询（只在独显读不到时才会打开）
    }
}
