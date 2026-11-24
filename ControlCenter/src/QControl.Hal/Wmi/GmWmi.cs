using System.Diagnostics;
using System.Management;
using QControl.Core.Devices;

namespace QControl.Hal.Wmi;

/// <summary>EC 传输失败（长度不符 / 回显被抢 / WMI 异常）。</summary>
public sealed class EcTransportException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>一次往返的统计（累计值，调用方取差分）。</summary>
public readonly record struct EcStats(long Count, double Seconds);

/// <summary>
/// 广达 `RW_GMWMI` 的传输层 —— **唯一与 EC 直接对话的地方**。
///
/// P0 已经证明的事实（`spike/README.md`）：
/// <list type="bullet">
/// <item>`WSAA` **不是 WMI 方法**。类上只有 `Active` / `BufferBytes` / `InstanceName`
///   三个属性、零方法。写 `BufferBytes` 再 `Put()`，驱动就去调 ACPI 的 `WSAA`。</item>
/// <item>读 EC **不需要管理员**（写才需要）。</item>
/// <item>`BufferBytes` 是**跨进程共享的单槽缓冲区**：写→Put→重取读回 三步非原子，
///   别的进程随时能把命令顶掉，于是回读到的是**别人的应答**。P0 实测撞到过
///   `00 FA 00 03` + 全 0 —— 只看长度就把它当成"0 °C"。</item>
/// </list>
///
/// 所以本类的核心不是"发命令"，而是**识别出这次读到的到底是不是我的应答**：
/// 校验长度 32 + 校验前 4 字节回显 == 我发的 group/sub，不符就丢弃重试。
/// </summary>
public sealed class GmWmi : IDisposable
{
    public const int BufferSize = 32;

    private readonly ManagementScope _scope;
    private readonly object _lock = new();
    private readonly Stopwatch _sinceLastCall = Stopwatch.StartNew();

    private ManagementObject? _instance;
    private ManagementClass? _class;
    private long _opsCount;
    private double _opsSeconds;

    public EcSpec Spec { get; }

    /// <summary>
    /// 两次写命令之间的最小间隔（秒）。低于机型档案的 <c>min_gap</c> 会被抬上去。
    /// 这是**写**那一侧的间隔 —— 与 <see cref="ReadGap"/> 分开，见 <see cref="Throttle"/>。
    /// </summary>
    public double DefaultGap { get; set; } = 0.45;

    /// <summary>
    /// 两条**读**命令之间的间隔（秒），由机型档案的 `read_gap` 给。
    /// 档案不写这个键时，<see cref="EcSpec.ReadGap"/> 会退回 `min_gap` —— 也就是
    /// **没实测过的机型行为一个字都不变**，别替它们把间隔压下来。
    ///
    /// 为什么读可以比写快一个数量级（2026-11-15 真机实测）：
    /// <list type="bullet">
    /// <item>真实往返只有 ~12 ms（`/api/debug/mem` 的 `snapshot.us.ec` 就是这么量的），
    ///   WSAA 是同步的 —— `Put()` 返回时 EC 已经处理完了，不需要"等它消化"；</item>
    /// <item>0.45 s 来自早先实现的保守值，实测每帧有两发读（status + perf），
    ///   于是每帧白等 0.45 s：`us.perf` = 461 828 µs，占整帧 515 ms 的 90%。
    ///   那 0.45 s **全额**落在"打开窗口 → 首帧出数"和"点开性能页 → 系统模式出数"上。</item>
    /// </list>
    ///
    /// ⚠️ 读间隔能压这么低的前提是**写完之后不许立刻读**：写命令（UFA/APMD/LKB*）
    ///    改的是 EC 的状态机，固件那侧还没落地时再灌一条命令，回帧就不可信了。
    ///    所以 <see cref="Throttle"/> 的规则是「上一次是写 ⇒ 这次仍按写间隔」，
    ///    写路径的时序**一个字都没变**，只把"读→读"这一段放开。
    /// ⚠️ 读间隔不参与 <see cref="Spec"/> 的 <c>min_gap</c> 下限判断 —— 那个下限是给
    ///    写/写之后用的（机型档案里的 0.2 s）。回显可校验的读由 <c>echo_verified</c>
    ///    兜底；回显不可校验的读（0x0202/0x0203/0x0207）本来就不该靠间隔去防抢占。
    /// </summary>
    public double ReadGap { get; set; } = 0.05;

    /// <summary>
    /// 「写批」内部两条写命令之间的间隔（秒），来自机型档案 `burst_gap`；
    /// **0 = 不支持连发**（<see cref="WriteBurst"/> 会拒绝，调用方照旧逐条按写间隔写）。
    /// 含义与放开依据见 <see cref="EcSpec.BurstGap"/>。
    /// </summary>
    public double BurstGap { get; set; }

    /// <summary>单次读失败后的最大尝试次数（含首次）。</summary>
    public int Attempts { get; set; } = 3;

    /// <summary>最近一次失败原因（诊断用；成功时清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 上一条发出去的命令是不是写（决定这一条要等哪个间隔）。
    /// </summary>
    private bool _lastWasWrite;

    /// <summary>
    /// 上一条命令的子命令号。只用来判"这是不是同一条命令的连续写" ——
    /// 用户拖亮度 / 连点亮度时发的都是 0x0100，那种重复写可以按 `burst_gap` 连发。
    /// </summary>
    private ushort _lastSub;

    /// <summary>
    /// 非 null = 正在一个写批里，所有**写**命令按这个间隔走（<see cref="WriteBurst"/>）。
    /// 只在 <see cref="_lock"/> 内读写。
    /// </summary>
    private double? _burstGap;

    /// <summary>
    /// 单次 WMI 操作的上限。⚠️ 只设 `ConnectionOptions.Timeout` / `Scope.Options` **不管
    /// `Put()` 和 `GetInstances()`**：它们是同步阻塞调用，返回前一直占着调用线程。
    /// 采样线程一旦卡在这里，`fancurve` 的 `_hwGate`、`AppState` 的 `Lock` 跟着全卡住，
    /// 界面就"整个死了但进程还在"。WMI 正常往返本机 &lt;15 ms，15 s 已经是"绝不可能是慢"。
    /// </summary>
    private static readonly TimeSpan WmiOpTimeout = TimeSpan.FromSeconds(15);

    public GmWmi(EcSpec spec)
    {
        Spec = spec;
        BurstGap = spec.BurstGap;
        var opts = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            EnablePrivileges = true,
            Timeout = TimeSpan.FromSeconds(8),
        };
        // ⚠️ 命名空间串必须是 winmgmts:\\.\root\WMI 形式（少一个反斜杠会静默返回空表）
        _scope = new ManagementScope(@"\\.\" + spec.Namespace.TrimStart('\\'), opts);
        _scope.Connect();
    }

    public EcStats Stats => new(Interlocked.Read(ref _opsCount), Volatile.Read(ref _opsSeconds));

    /// <summary>能不能拿到实例（拿不到 = 这台机器没有 RW_GMWMI，或 WMI 服务异常）。</summary>
    public (bool Ok, string? Detail) Available()
    {
        try
        {
            lock (_lock)
            {
                var mo = FindInstance();
                mo?.Dispose();
            }
            return (true, null);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    // ================================================================ 交换

    /// <summary>
    /// 下发一条命令并取回 32 字节应答。
    ///
    /// <paramref name="verifyEcho"/> = true 时要求回读的前 4 字节等于我发的
    /// group+sub（小端）。读命令必须开；写命令可以关（写只关心有没有发出去，
    /// 不应答内容用）。
    ///
    /// <paramref name="isWrite"/> = 这条命令走的是不是写组。**只用于定节流间隔**
    /// （写 → 写间隔，读 → 读间隔），不参与任何权限判断。
    /// </summary>
    /// <exception cref="EcTransportException">长度不符 / 回显始终被抢 / WMI 异常。</exception>
    public byte[] Exchange(byte[] request, ushort group, ushort sub,
                           bool verifyEcho, double? gap = null, int? attempts = null,
                           bool isWrite = false)
    {
        if (request.Length != BufferSize)
            throw new ArgumentException($"请求缓冲区必须是 {BufferSize} 字节", nameof(request));

        var tries = Math.Max(1, attempts ?? Attempts);
        var lastReason = "";

        lock (_lock)
        {
            for (var i = 0; i < tries; i++)
            {
                Throttle(gap, isWrite, sub);
                var sw = Stopwatch.StartNew();
                try
                {
                    var resp = ExchangeOnce(request);
                    sw.Stop();
                    Interlocked.Increment(ref _opsCount);
                    Interlocked.Exchange(ref _opsSeconds, Math.Round(
                        Volatile.Read(ref _opsSeconds) + sw.Elapsed.TotalSeconds, 6));

                    if (resp.Length != BufferSize)
                    {
                        lastReason = $"EC 返回长度 {resp.Length} 字节（应为 {BufferSize}）";
                        continue;
                    }
                    if (verifyEcho && !EchoMatches(resp, group, sub))
                    {
                        lastReason = $"回显不符（收到 {Hex(resp.AsSpan(0, 4))}，"
                                     + $"期望 {group:X4}/{sub:X4}）——缓冲区被其它进程抢占";
                        continue;
                    }

                    LastError = null;
                    return resp;
                }
                catch (Exception e)
                {
                    sw.Stop();
                    Interlocked.Increment(ref _opsCount);
                    Interlocked.Exchange(ref _opsSeconds, Math.Round(
                        Volatile.Read(ref _opsSeconds) + sw.Elapsed.TotalSeconds, 6));
                    lastReason = $"{e.GetType().Name}: {e.Message}";
                    // WMI 层异常（COM 已死等）重试也没意义，直接抛
                    if (e is ManagementException or InvalidOperationException)
                        throw new EcTransportException(lastReason, e);
                }
            }
        }

        LastError = lastReason;
        throw new EcTransportException($"EC 往返失败（试了 {tries} 次）：{lastReason}");
    }

    private static bool EchoMatches(byte[] resp, ushort group, ushort sub)
        => resp[0] == (byte)(group & 0xFF)
           && resp[1] == (byte)(group >> 8)
           && resp[2] == (byte)(sub & 0xFF)
           && resp[3] == (byte)(sub >> 8);

    private static string Hex(ReadOnlySpan<byte> b)
        => string.Join(" ", b.ToArray().Select(x => x.ToString("X2")));

    /// <summary>
    /// 开一个「写批」：块内的**写**命令用 <paramref name="gap"/> 当间隔（**不受 min_gap 下限约束**），
    /// 块一结束立刻恢复成普通写间隔。
    ///
    /// 用途只有一个 —— 一次用户操作要连发多条写的时候（三区同色 / 神光同步，
    /// 一条 `0x0100` 只能写一个地址）。以前那三条之间各等 0.45s，用户看到的是
    /// 「A 区变红 … 一秒后 B 区变红 … 再一秒 C 区」，而原厂是三条背靠背发出去 ⇒ 同时变。
    ///
    /// 为什么能这么压：`0x0100` 分支内部对 EC 有两次忙等（`While(ECRD(LKBE) ≠ …)`），
    /// 上一条没落地时后一条的 `Put()` 自己会阻塞 —— 节流在这里只是防"我们自己灌太快"，
    /// 不是硬件要求。写完之后的那条**读**仍按写间隔等（`_lastWasWrite` 不动），
    /// 所以「写后别立刻读」这条规矩没被放开。
    ///
    /// <paramref name="gap"/> 由调用方从 <see cref="BurstGap"/> 取；没标定过 `burst_gap` 的机型
    /// 根本没有这个值，<see cref="EcHal"/> 那边会走逐条路径。
    /// </summary>
    public IDisposable WriteBurst(double gap)
    {
        lock (_lock) _burstGap = Math.Clamp(gap, 0.001, 5.0);
        return new BurstScope(this);
    }

    /// <summary>写批的收尾：把批内间隔清掉（异常路径也不会漏，见 EcHal 的 using）。</summary>
    private sealed class BurstScope(GmWmi io) : IDisposable
    {
        public void Dispose()
        {
            lock (io._lock) io._burstGap = null;
        }
    }

    /// <summary>
    /// 严格串行 + 节流。节流是**人为间隔**，Stats 不计它。
    ///
    /// 等多久取决于**上一条**命令和这一条的方向：
    /// <code>
    ///   在写批里、这一条是写 ⇒ 批间隔（burst_gap，本机 20ms）  ← 一次操作的多条写
    ///   写 → 写（同一子命令）  ⇒ burst_gap                    ← 用户连着拖/连点同一条命令
    ///   写 → 写（不同子命令）  ⇒ 写间隔（max(DefaultGap, MinGap)）
    ///   读 → 写               ⇒ 0（不等）                      ← 2026-11-15 放开，见下
    ///   写 → 读               ⇒ 写间隔（固件要落地）
    ///   读 → 读               ⇒ ReadGap（本机 0，实测往返 ~10ms）
    /// </code>
    ///
    /// <para>
    /// **为什么「读 → 写」可以不等（2026-11-15）**：0.45 s 这条间隔原本是"发完一条命令
    /// 别急着灌下一条"，它保护的是两种情形 —— **写之后固件还在改状态机**（所以写→读、
    /// 写→写 必须等），以及"我们自己别灌太快"。但"读→写"里上一条只是一次读组查询，
    /// 它不改变任何状态机，`Put()` 返回时 EC 早就处理完了。
    /// </para>
    /// <para>
    /// 而它卡住的恰恰是**用户看得见的那一段**：点一下风扇/背光/亮度的 HTTP 请求会落到
    /// `Throttle`，上一条几乎总是采样线程刚发的那发读 —— 于是每一次单击都要先白等
    /// 0.43 s 才把命令发出去。实测 `/api/ec/fan` 的响应时间 ≈ 450 ms 就是这么来的
    /// （EC 真实往返只有 ~10 ms）。放开之后单击的延迟回到"一次往返"的量级，
    /// 而**写之后那两条规矩一个字都没动**：写完的下一发读照样等写间隔。
    /// </para>
    ///
    /// <para>
    /// **为什么「写 → 写（同一子命令）」可以按 burst_gap**：用户拖亮度滑块 / 连点亮度，
    /// 发的都是同一条 0x0100，写的是同一组寄存器，后一次覆盖前一次。档案里标了
    /// `burst_gap` 的机型（本机 20 ms，原厂连发三个区的实证）才有这个快通道；
    /// 没标定的机型仍然逐条按写间隔走 —— 未知模具的行为一个字都不改。
    /// </para>
    /// </summary>
    private void Throttle(double? gap, bool isWrite, ushort sub)
    {
        double g;
        if (isWrite && _burstGap is { } burst)
            g = burst;                                   // 显式写批：背靠背发
        else if (isWrite && _lastWasWrite)
            // 写→写：同一条命令的重复写走档案 burst_gap（没标定就退回写间隔）。
            // ⚠️ 调用方显式给了 gap 就不抢它的（诊断/探针要精确控制间隔时用）。
            g = gap is null && _lastSub == sub && Spec.BurstGap > 0
                ? Math.Clamp(Spec.BurstGap, 0.001, 5.0)
                : Math.Max(gap ?? DefaultGap, Spec.MinGap);
        else if (isWrite)
            g = gap ?? 0;                                // 读→写：不等人为间隔（显式 gap 照尊重）
        else if (_lastWasWrite)
            g = Math.Max(gap ?? DefaultGap, Spec.MinGap);  // 写→读：等固件落地
        else
            // 读→读：档案 read_gap 是**下限**，调用方显式给了更大的 gap 要照尊重 ——
            // 旧写法 `Math.Max(0, ReadGap)` 把显式 gap 直接丢掉，于是 0x0207 的
            // "串帧重读"（SamePayload 后按 min_gap 再读一次）实际只等了 read_gap(50ms)，
            // 又拿到同一帧 ⇒ sys_replay=true ⇒ 三风扇机型第三路读数这一拍照样被丢。
            g = Math.Max(Math.Max(0, ReadGap), gap ?? 0);

        var elapsed = _sinceLastCall.Elapsed.TotalSeconds;
        var wait = g - elapsed;
        if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
        _sinceLastCall.Restart();

        // 更新在**发出之前**：哪怕这一条抛了，下一条也该按"刚发过写"来等
        _lastWasWrite = isWrite;
        _lastSub = sub;
    }

    /// <summary>
    /// 一次真实的往返：写 BufferBytes → Put → **重取实例**读回。
    ///
    /// 三个细节都不能省：
    /// 1. 必须重取实例 —— Put 之后原对象的属性不保证刷新（所以每次都重新取，不复用旧对象）；
    /// 2. 不能复用上一次的缓冲区 —— 那正是"读到别人的应答"的来源；
    /// 3. 属性赋值要按实际声明类型试 —— 档案声明是 `UInt8[]`，但不同固件可能是别的。
    ///
    /// ⚠️ **每次往返只枚举一次**（2026-11-02）：读回用的那只实例直接留下来当下一轮
    ///    写入的对象，上一只当场 Dispose。此前是"读回一只 + 下一轮再枚举一只"，
    ///    每秒 2~3 次往返 ⇒ 每秒多出两条 COM 枚举器 + 一个 COM 对象，
    ///    全压在终结器队列上 —— 这正是托盘常驻时工作集从 30 MB 一路爬回 60 MB 的主因
    ///    （GC 一跑句柄就从 1050 掉回 745，见 /api/debug/mem 的 handles 锯齿）。
    /// </summary>
    private byte[] ExchangeOnce(byte[] request)
    {
        var mo = _instance ??= FindInstance()
            ?? throw new EcTransportException("未找到 RW_GMWMI 实例（需要管理员权限？）");

        if (!AssignBuffer(mo, request, out var assignDetail))
            throw new EcTransportException($"无法写入 BufferBytes：{assignDetail}");

        mo.Put(new PutOptions { Timeout = WmiOpTimeout });

        // Put 之后必须换一只新实例读回（原对象的属性不保证刷新）
        var fresh = FindInstance()
            ?? throw new EcTransportException("Put 之后重新枚举不到实例");

        byte[] result;
        try
        {
            var value = fresh["BufferBytes"];
            result = AsBytes(value)
                ?? throw new EcTransportException(
                    $"BufferBytes 类型未识别（{value?.GetType().Name ?? "null"}）");
        }
        catch
        {
            // 读失败就别把它留成下一轮的写入对象
            fresh.Dispose();
            throw;
        }

        // 这只刚读回来的一定是新的，留作下一轮写入用；上一只（已 Put 过）放手
        if (!ReferenceEquals(_instance, fresh))
        {
            _instance?.Dispose();
            _instance = fresh;
        }
        return result;
    }

    /// <summary>按声明类型依次尝试赋值。见 P0 spike 里的四种兜底。</summary>
    private static bool AssignBuffer(ManagementObject mo, byte[] buf, out string detail)
    {
        detail = "";
        try
        {
            mo["BufferBytes"] = buf;
            detail = "byte[]";
            return true;
        }
        catch (Exception e1)
        {
            try
            {
                mo["BufferBytes"] = buf.Select(b => (ushort)b).ToArray();
                detail = "ushort[]";
                return true;
            }
            catch
            {
                detail = $"byte[]/{e1.GetType().Name} 与 ushort[] 都失败";
                return false;
            }
        }
    }

    /// <summary>WMI 数组值 → byte[]。</summary>
    public static byte[]? AsBytes(object? v) => v switch
    {
        null => null,
        byte[] b => b,
        ushort[] u => u.Select(x => (byte)x).ToArray(),
        sbyte[] sb => sb.Select(x => (byte)x).ToArray(),
        int[] i => i.Select(x => (byte)x).ToArray(),
        object[] o => o.Select(Convert.ToByte).ToArray(),
        _ => null,
    };

    /// <summary>
    /// 取（并挑出）`RW_GMWMI` 的实例。
    ///
    /// ⚠️ **两处都必须 Dispose**（2026-11-02 修）：
    ///    · `ManagementClass` 缓存成一份 —— 它只是一条通往 `IWbemServices` 的 RCW，
    ///      内容不会变，每来一次往返新建一份纯属白烧；
    ///    · `GetInstances()` 返回的集合**自己就是 COM 枚举器**，不 Dispose 就只会等
    ///      终结器来收 —— 每秒 2~3 次往返 ⇒ 每秒几个原生对象挂在终结器队列上，
    ///      这就是托盘态工作集"慢慢爬回来"的真因（内存 30 → 60 MB、句柄 745~1050 锯齿）。
    /// </summary>
    private ManagementObject? FindInstance()
    {
        var mc = _class ??= new ManagementClass(_scope, new ManagementPath(Spec.Class), null);
        using var all = mc.GetInstances(new System.Management.EnumerationOptions { Timeout = WmiOpTimeout });
        ManagementObject? first = null;
        foreach (ManagementObject mo in all)
        {
            var name = mo["InstanceName"] as string;
            if (name == Spec.Instance)
            {
                first?.Dispose();      // 之前留下的"备胎"没人要了
                return mo;
            }
            if (first is null) first = mo;
            else mo.Dispose();
        }
        return first;
    }

    /// <summary>
    /// 纯读黑板：只重取实例读 <c>BufferBytes</c>，**不 Put、不触发 WSAA**。
    ///
    /// 观察会话用它高频抓原厂写命令：原厂写 BufferBytes → Put 之后，那 32 字节报文
    /// 残留在跨进程共享的单槽里，直到下一次被覆盖。覆盖来自**任何进程**的写（含我们自己
    /// 读组走的「写 BufferBytes 再 Put」，那一帧会以 0xFA00 开头 —— 调用方要把它过滤掉）。
    ///
    /// 与 <see cref="Exchange"/> 的区别：Exchange 是「写→Put→重取读回」的往返，
    /// 会真的往 EC 发一条命令；这里**一个字节都不写**，只读最后一次 Put 留下的值。
    /// </summary>
    /// <exception cref="EcTransportException">枚举不到实例 / BufferBytes 类型未识别。</exception>
    public byte[] PeekBuffer()
    {
        lock (_lock)
        {
            var mo = FindInstance()
                ?? throw new EcTransportException("未找到 RW_GMWMI 实例（需要管理员权限？）");
            try
            {
                var value = mo["BufferBytes"];
                return AsBytes(value)
                    ?? throw new EcTransportException(
                        $"BufferBytes 类型未识别（{value?.GetType().Name ?? "null"}）");
            }
            finally
            {
                mo.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = null;
            _class?.Dispose();
            _class = null;
        }
    }
}
