using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Ec;
using QControl.Core.Model;
using QControl.Hal.Sys;
using QControl.Hal.Wmi;

namespace QControl.Hal.Ec;

/// <summary>写操作被守卫拒绝（权限 / 总闸 / 未授权的高危命令）。</summary>
public sealed class WriteDeniedException(string message) : Exception(message);

/// <summary>
/// EC 读写与守卫：读 / 写 / arm / 钳位。
///
/// **安全规矩（三条，缺一条就出过事故）**：
/// <list type="number">
/// <item>判定"这是不是写"看**命令方向**，不看子命令号。0x0203 在读组里只读状态、
///   在写组里才真改显示输出 —— 早期按子命令号判，导致"切换显卡模式"点了既不报错
///   也毫无效果（假成功）。**2026-10-23 起方向直接长在命令对象上**
///   （<see cref="EcCommand.Read"/> / <see cref="EcCommand.Write"/>），
///   连"组号等不等于读组"都不用猜了。</item>
/// <item>所有数值先过 <see cref="Bounds"/> 钳制。风扇 duty 的下限是 30 ——
///   写 0 **不是**"交回自动"，实测是**停转**，EC 并不接管。</item>
/// <item>高危命令（0x0300 / 0x0206 / 0x0203）要先 <see cref="Arm"/>，
///   且授权**命中即消耗**（TTL 内只放行一次）。</item>
/// </list>
///
/// **参数一律按槽名给**（<see cref="EcArgs"/>），不再有 `(sub, d0..d3, a..d)` 裸位置参数：
/// 位置参数下把 UFA2 写进 `d1` 会被 `a` 覆盖（右风扇停转那次事故），编译期看不出来。
/// </summary>
public sealed class EcHal : IDisposable
{
    private readonly GmWmi _io;
    private readonly EcSpec _ec;
    private readonly Func<Device> _device;
    private readonly ModelStore? _model;

    private readonly object _armLock = new();
    /// <summary>
    /// 高危命令的授权配额：一个子命令对应一组「过期时刻」，**一次 arm = 一个配额**。
    /// 为什么不再是单槽：FN+3（热键线程）与拔插电自动切档（采样线程）可能在同一
    /// TTL 窗口里各自 arm 0x0300，单槽被后一次覆盖，先写的一方消费掉授权后
    /// 另一方就 403 了。多槽仍严格保持「一次确认只允许写一次」。
    /// </summary>
    private readonly Dictionary<ushort, List<double>> _armed = new();

    private bool _writesEnabled = true;
    private bool? _adminCache;
    private int?[] _duty = [null, null, null];
    private JsonObject? _lastStatus;
    private double _lastStatusTs;
    /// <summary>
    /// 最后一次**真从 0x0207 读到** SYS 读数的时刻（不是"这一帧 ReadStatus 成功了"）。
    /// 串帧 / 全 0 / 异常时 CarryOverSys 会沿用上次值，但绝不能刷新这个时间戳 ——
    /// 否则 EC 真挂了会被每拍续命，旧转速永远显示下去。
    /// </summary>
    private double _lastRealSysTs;
    private readonly EcWriteLog _writeLog = new();

    /// <summary>型号 SKU 只在进程内探一次（见 <see cref="ProbeSkuOnce"/>）。</summary>
    private readonly object _skuLock = new();
    private bool _skuProbed;
    private uint? _sku;

    /// <summary>
    /// 「这台到底有没有第三路风扇」的运行时探测器（见 <see cref="ThirdFanProbe"/>）。
    /// 档案判了 `fans &lt; 3` 也照样读 `0x0207`：读回可信的 SYS 转速就升级成三路，
    /// 让型号名/SKU 表没覆盖到的三风扇机也能自动长出 SYS 行。
    /// </summary>
    private readonly ThirdFanProbe _sysProbe = new();

    /// <summary>
    /// 本次进程是否已经写过 `0x0206 = 1`（把 EC 的风扇控制权标成"软件接管"）。
    /// 写一次就够：这条不是每拍都要发的命令，重复发只会多一次 WMI 往返。
    /// 见 <see cref="EnsureFanControl"/>。
    /// </summary>
    private readonly object _fanCtlLock = new();
    private bool _fanControlTaken;

    /// <summary>
    /// 当前是否处于「未开自定义风扇、但用户点了最大转速」的**临时满转**态
    /// （见 <see cref="SetOemFanMax"/>）。
    ///
    /// 只有内存状态：EC 读不回 UFA，重启 / 睡眠唤醒后 EC 本来就回原生自动，
    /// 这个标志随进程一起归零正是对的。它是"这颗按钮该显示最大转速还是恢复自动"
    /// 的唯一真源（`/api/status` 的 `fan_oem_max`）。
    ///
    /// ⚠️ 任何一次正常写 UFA（<see cref="SetFans"/>）或交回（<see cref="ReleaseToEc"/>）
    /// 都要把它清掉 —— 否则曲线一接管、风扇早已不是满转，按钮却还写着"恢复自动"。
    /// </summary>
    public bool OemFanMax { get; private set; }

    /// <summary>
    /// 「采集探针」的临时写授权（见 <see cref="OpenWriteProbe"/>）：
    /// 到期时刻 + 放行的子命令白名单。**只绕过机型/总闸那一道门**，管理员与 arm 照判。
    /// </summary>
    private readonly object _probeLock = new();
    private double _probeUntil;
    private ushort[] _probeSubs = [];

    public EcHal(GmWmi io, Func<Device> device, ModelStore? model = null)
    {
        _io = io;
        _ec = io.Spec;
        _device = device;
        _model = model;
        // 节流默认值跟 presets.ec.gap 走（早先实现也是）—— 那是**写**间隔，
        // 读间隔由机型档案 read_gap 决定（见 GmWmi.ReadGap）。
        if (model?.Get("ec", "gap") is JsonNode g && Bounds.TryToDouble(g, out var gap) && gap > 0)
            _io.DefaultGap = gap;
        _io.ReadGap = _ec.ReadGap;
    }

    public EcSpec Spec => _ec;
    public GmWmi Transport => _io;
    public string? LastError => _io.LastError;

    /// <summary>
    /// 最近 40 次**写**尝试（含守门拒绝）。只给「一键采集」抄进采集包用 ——
    /// 采集自己做的写验证、以及用户之前手动试写的，都从这里出去。
    /// 读操作不留痕（每秒几十条，记了也没人看）。
    /// </summary>
    public EcWriteLog WriteLog => _writeLog;

    // ================================================================ 权限

    /// <summary>
    /// 机型层 + 用户「EC 控制」开关的最终写许可。
    /// ⚠️ 与 <see cref="WritesEnabled"/> 是两件事：那是进程内部状态（历史上常开），
    /// 这才是用户在设置页能看见的那颗开关。
    /// </summary>
    public bool WritesAllowed => _device().WritesAllowed;

    /// <summary>机型档案声明的风扇路数（0 = 未声明）。给"第三路要不要下发"这类判断用。</summary>
    public int Fans => _ec.Fans;

    public bool WritesEnabled => _writesEnabled;

    /// <summary>
    /// 最后一次写进 EC 的转速（三路里第一个非 null）。
    /// 非 null = **EC 里现在钉着一个我们写的值**，固件不会接管；
    /// 实测 7.5 小时纹丝不动，只有 <see cref="ReleaseToEc"/>（`0x0206=0` + UFA=`0xFF`）
    /// 或重启/睡眠唤醒能消掉。
    /// </summary>
    public int? DutyApplied
    {
        get
        {
            foreach (var d in _duty)
                if (d is > 0) return d;
            return null;
        }
    }

    /// <summary>
    /// 第 i 路（0=CPU / 1=GPU / 2=SYS）我们上一次**真写下去**的目标值；没写过 = null。
    ///
    /// 给 <c>POST /api/ec/fan</c> 用：只给一路时必须知道另外几路"我们记不记得"——
    /// 不记得就写不下去（`SetFans` 的兜底是 min_duty，那不是"不动它"，是"钉住它"）。
    /// </summary>
    public int? AppliedDutyAt(int index) => index is >= 0 and < 3 ? _duty[index] : null;

    public bool EnableWrites(bool on)
    {
        _writesEnabled = on;
        if (!on) lock (_armLock) _armed.Clear();
        return _writesEnabled;
    }

    /// <summary>
    /// 打开一个**范围最小化**的写入窗口，给「一键采集」的写验证用。
    ///
    /// 为什么需要它（而不是让用户先去设置页打开「EC 控制」再回来采集）：
    /// 那条路要三跳（开开关 → 自己试写 → 再采集），绝大多数用户走到第二跳就散了，
    /// 于是采集包里永远没有"写"的证据，那台机器就永远适配不了。用户在这里点的是
    /// **一次明确的确认**（界面写明要写哪两条、写什么值、采完恢复），比让他去扳一颗
    /// 全局开关更具体。
    ///
    /// 边界（写死，别放宽）：
    /// <list type="bullet">
    ///   <item>只绕过「机型/总闸」那一道门 —— **管理员照样要判**（非管理员写 WMI 不报错也不生效，
    ///     放过去就变成了最常见的假成功）；arm 也照样要判，所以调用方每次高危写前自己 arm；</item>
    ///   <item>白名单只含调用方列出的子命令（采集目前只有 0x0300 / 0x0100，**不含风扇**）；</item>
    ///   <item>有 TTL，且 <c>Dispose</c> 立刻收回 —— 采集结束、异常、被打断都会走到 finally。</item>
    /// </list>
    /// ⚠️ 不要把它做成 HTTP 端点，也不要让界面拿它包一层"手动写"。它只服务采集这一条路。
    /// </summary>
    public IDisposable OpenWriteProbe(TimeSpan ttl, params ushort[] subs)
    {
        lock (_probeLock)
        {
            _probeUntil = Now() + Math.Max(1, ttl.TotalSeconds);
            _probeSubs = subs;
        }
        return new ProbeScope(this);
    }

    private bool ProbeAllows(ushort sub)
    {
        lock (_probeLock)
        {
            if (Now() >= _probeUntil) return false;
            return Array.IndexOf(_probeSubs, sub) >= 0;
        }
    }

    private void CloseWriteProbe()
    {
        lock (_probeLock)
        {
            // 连带把这几条命令的 arm 授权也收掉：探针里可能刚 arm 完就抛了，
            // 留一条"已授权"的 0x0300 在 TTL 里没有任何理由。
            lock (_armLock)
                foreach (var s in _probeSubs) _armed.Remove(s);

            _probeUntil = 0;
            _probeSubs = [];
        }
    }

    private sealed class ProbeScope(EcHal owner) : IDisposable
    {
        public void Dispose() => owner.CloseWriteProbe();
    }

    /// <summary>高危命令的一次性授权。UI 二次确认后调它，<paramref name="ttl"/> 秒内允许发一次。</summary>
    public JsonObject Arm(ushort cmd, double ttl = 30)
    {
        lock (_armLock)
        {
            if (!_armed.TryGetValue(cmd, out var slots)) _armed[cmd] = slots = new List<double>();
            var now = Now();
            slots.RemoveAll(e => now >= e);          // 顺手清掉过期配额
            slots.Add(now + ttl);                    // 每次确认只加一个配额，不覆盖别人的
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["armed"] = CmdTable.Hex(cmd),
            ["ttl"] = ttl,
        };
    }

    /// <summary>
    /// 一次性授权检查：**命中即消耗**。
    /// ⚠️ 2026-10-14 修过的坑：旧实现命中不消耗，等于 TTL 窗口内无限次通过 ——
    /// docstring 承诺"允许发一次"名不副实，用户确认一次后窗口内连发会反复写 APMD。
    /// </summary>
    public bool Armed(ushort cmd)
    {
        lock (_armLock)
        {
            if (!_armed.TryGetValue(cmd, out var slots)) return false;
            var now = Now();
            slots.RemoveAll(e => now >= e);
            if (slots.Count == 0) { _armed.Remove(cmd); return false; }

            slots.RemoveAt(0);                       // 命中即消耗**一个**配额
            if (slots.Count == 0) _armed.Remove(cmd);
            return true;
        }
    }

    /// <summary>
    /// 非管理员时**写守卫直接拒绝**的理由。
    ///
    /// ⚠️ 这不是"保守起见"：实测非管理员下 WMI 的 `Put()` **不报错、也确实不生效**，
    ///    旧实现照样返回 `ok:true`，界面于是显示「已生效」而风扇一动没动 ——
    ///    正是本项目最忌讳的"假成功"。宁可给一条明确的 403。
    /// </summary>
    public const string AdminWriteDeniedHint =
        "EC 写入需要管理员权限。当前进程不是管理员：写命令会被 WMI 接受但静默不生效，"
        + "所以这里直接拒绝，不再回「已生效」。重启控制中心并同意 UAC 即可（程序会自己"
        + "请求提权），或双击 run-as-admin.cmd / 右键 exe 选「以管理员身份运行」。";

    /// <summary>
    /// 当前进程是否管理员。
    /// <para>
    /// 结果**缓存**：一个进程的令牌在生命周期内不会变（提权只能换新进程），
    /// 而它在每次写守卫上都要判一次，没必要每次去查令牌。
    /// </para>
    /// <para>
    /// 判定本身转调 <see cref="Elevation.IsElevated"/> —— 全项目只留一个判定口，
    /// 免得"自启说你是管理员、EC 说你不是"这种自相矛盾。
    /// </para>
    /// </summary>
    public bool IsAdmin()
    {
        if (_adminCache is bool cached) return cached;
        var isAdmin = Elevation.IsElevated();
        _adminCache = isAdmin;
        return isAdmin;
    }

    public JsonObject EnvInfo()
    {
        var admin = IsAdmin();
        var dev = _device();
        return new JsonObject
        {
            ["mode"] = "wmi",
            ["admin"] = admin,
            ["write_ok"] = admin && dev.WritesAllowed,
            ["hint"] = admin
                ? (dev.WritesAllowed ? null : dev.DenyHint())
                : AdminWriteDeniedHint,
        };
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ================================================================ 守卫

    /// <summary>
    /// 写命令的四道门。拒绝原因必须分开说，别都糊成"未验证"。
    ///
    /// 顺序：**机型/开关 → 管理员 → 内部总闸 → arm**。
    /// 管理员放在开关之后，是因为"用户自己把 EC 控制关了"比"没提权"更具体 ——
    /// 前者是他自己的选择（要给他出口），后者才是他需要去解决的事。
    ///
    /// 唯一的例外是**采集探针**（<see cref="OpenWriteProbe"/>）：它只替第一道门开一条缝
    /// （白名单子命令 + TTL），其余三道一个字都不放宽。
    /// </summary>
    private void RequireWrite(EcCommand cmd)
    {
        // ★ 判定依据是**命令方向**，不是子命令号（见类注释第 1 条）
        if (!cmd.IsWrite) return;

        var dev = _device();
        if (!dev.WritesAllowed && !ProbeAllows(cmd.Sub))
            throw new WriteDeniedException(dev.DenyHint());

        // 非管理员：WMI 会接受但不生效 ⇒ 拒绝（见 AdminWriteDeniedHint 的实测注释）
        if (!IsAdmin())
            throw new WriteDeniedException(AdminWriteDeniedHint);

        if (!_writesEnabled)
            throw new WriteDeniedException(
                $"{CmdTable.Hex(cmd.Sub)} 是写命令，EC 写入未开启。"
                + $"（写入总闸设计上常开，这里是内部状态异常）风险：{CmdTable.Risk(cmd.Sub)}");

        if (CmdTable.NeedsConfirm(cmd.Sub) && !Armed(cmd.Sub))
            throw new WriteDeniedException(
                $"{CmdTable.Hex(cmd.Sub)} 属高危命令（{CmdTable.Risk(cmd.Sub)}），"
                + "需要先二次确认授权（arm）；若刚刚确认过，可能是另一个操作同时用掉了那份授权，请重试。");
    }

    // ================================================================ Win 键锁

    /// <summary>
    /// 锁定 / 解锁 Win 键 —— **走 EC**（`0x0200` 写组，a6 = 1 禁用 / 0 启用）。
    ///
    /// <para>
    /// 依据（2026-11-22 反编译本机 <c>ControlCenter.exe</c> 的 <c>SetWinKey</c>）：
    /// <c>a0=0xFB00, a1=0x0200, a6 = IsEnabled ? 0 : 1</c>，写成功后原厂把同一个值
    /// 记进注册表 <c>WinKey</c>（见 <see cref="OemWinKey"/>）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **没有读回**：`0x0200` 读侧的 a6 是键盘亮度档（本机实测恒 0..2），不是 Win 键状态。
    /// 所以这里"写成功"只代表 SMI 发出去且返回成功，EC 接不接受无从校验 ——
    /// 调用方要用注册表 + 内存记住状态（见 `AppState.SetWinKeyLock`），别声称"已验证生效"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 子命令取 `ec.cmd.win_key`，档案没声明就退回 `ec.cmd.status` —— 两者都是 `0x0200`，
    /// 这样不用为一条命令去改 6 份档案。**不走 `/api/ec/raw`**：`0x0200` 在档案里是
    /// `read_only`（调试口不许写它），这条是派生自用户开关的专用写，与 `SetFanControl` 同理。
    /// </para>
    /// </summary>
    /// <param name="locked">true = 屏蔽 Win 键；false = 恢复。</param>
    /// <param name="persist">是否同步原厂注册表（退出流程里要传 false 之前先想清楚）。</param>
    public JsonObject SetWinKeyLock(bool locked, bool persist = true)
    {
        var sub = _ec.Sub("win_key") >= 0 ? Sub("win_key") : Sub("status");
        var a6 = locked ? 1u : 0u;

        Exchange(EcCommand.Write(sub, EcArgs.Empty.With(EcSlot.D4, a6)));

        var persisted = persist && OemWinKey.Sync(locked);
        Console.WriteLine($"[winkey] EC 0x{sub:X4} 写 a6={a6}（{(locked ? "锁定" : "解锁")}）"
                          + (persist ? $"，注册表{(persisted ? "已同步" : "未同步")}" : "，未落盘"));
        return new JsonObject
        {
            ["ok"] = true,
            ["locked"] = locked,
            ["cmd"] = $"0x{sub:X4}",
            ["a6"] = a6,
            ["persisted"] = persisted,
        };
    }

    /// <summary>
    /// 原厂注册表里记的 Win 键状态（true = 禁用）。null = 读不到 / 没装原厂。
    /// 只在启动恢复时用一次：决定要不要把 EC 的锁重放一遍。
    /// </summary>
    public static bool? StoredWinKeyLock => OemWinKey.Read();

    /// <summary>
    /// 机型档案里声明的子命令号。**没声明就抛**。
    /// ⚠️ 旧写法 `(ushort)_ec.Sub("xxx")` 在档案缺项时会把 `-1` 转成 **0xFFFF** 发给 EC ——
    /// 一条谁也没标定过的命令。宁可报错让上层"整行隐藏"，也不猜。
    /// </summary>
    private ushort Sub(string name)
        => _ec.Sub(name) is var v && v is >= 0 and <= 0xFFFF
            ? (ushort)v
            : throw new InvalidOperationException(
                $"机型档案没有声明 ec.cmd.{name} 的子命令号，拒绝下发（旧写法会把它当 0xFFFF 发出去）");

    // ================================================================ 底层往返

    /// <summary>
    /// 一条命令的完整往返（含守卫）。
    /// <para>
    /// 读路径**严格**：长度不符或回显被抢一律抛，让上层走"读不到 → 整行隐藏"。
    /// 写路径只对「守卫拒绝」和「传输失败」如实报错；写进去之后 EC 接没接受，
    /// 另有回读校验（SelfTestWrite / 采集探针），不在这里猜。
    /// ⚠️ 传输失败**绝不能吞成全零帧**：那会让 SetFans 照样回 ok:true、还把 _duty
    ///    缓存更新成没写进去的值（界面显示 55%，EC 里其实还是上一次的转速）。
    /// </para>
    /// </summary>
    private EcFrame Exchange(EcCommand cmd, double? gap = null)
    {
        try
        {
            RequireWrite(cmd);
        }
        catch (WriteDeniedException e)
        {
            // 「点了但硬件没动」最常见的原因就是被守卫拦下，而界面上只留一句提示、关掉就没了。
            // 记一条 denied：采集包里能看出"用户试过，卡在哪一道门"，不用他复述。
            if (cmd.IsWrite) Record(cmd, EcWriteOutcome.Denied, "", e.Message);
            throw;
        }

        var group = cmd.GroupFor(_ec);
        var req = EcFrame.Build(group, cmd);

        // 回显校验只对**档案标定过会回显**的命令开（本机只有 0x0200）。
        // 写命令一律不校验：Put 期间缓冲区是我们独占的，写完就落盘了，
        // 之后的回读只是附带信息。
        var verifyEcho = !cmd.IsWrite && _ec.EchoVerified.Contains(cmd.Sub);

        try
        {
            var frame = new EcFrame(_io.Exchange(req.ToArray(), group, cmd.Sub,
                verifyEcho: verifyEcho, gap: gap, isWrite: cmd.IsWrite));
            if (cmd.IsWrite) Record(cmd, EcWriteOutcome.Ok, frame.Hex, null);
            return frame;
        }
        catch (EcTransportException e) when (cmd.IsWrite)
        {
            // 传输失败 = 这条写命令根本没落到 EC。如实往上抛，不谎报成功：
            // 各写方法的调用方都有兜底 —— 曲线循环 catch 后记 err、采集探针记 error、
            // HTTP 的 Guarded 转成 {ok:false, error}。写失败时 _duty / arm 也不该被更新。
            Record(cmd, EcWriteOutcome.Error, "", e.Message);   // LastError 已在 transport 里记下
            throw;
        }
    }

    private void Record(EcCommand cmd, EcWriteOutcome outcome, string reply, string? error)
        => _writeLog.Add(new EcWriteAttempt(DateTimeOffset.Now, cmd.Sub, cmd.Args, outcome, reply, error));

    // ================================================================ 读

    /// <summary>
    /// 固件型号 SKU（`ec.cmd.sku`，本机家族 = `0x0201` 的 DAT2）。null = 没探到 / 档案没声明。
    /// </summary>
    public uint? Sku => _sku;

    /// <summary>
    /// 探一次型号 SKU，并据此定风扇路数（档案 `ec.fans_by_sku`）。进程内只探一次。
    ///
    /// <para>
    /// **为什么要这条**：三风扇机型（广达 NLY，SKU `0x33`/`0x34`）和两风扇机型共用
    /// 一模一样的命令表，唯一区别是"有没有第三路"。原厂控制台是读 SKU 判的；
    /// 我们原来只认档案里手写的 `ec.fans` —— 换台机器就得手改 json，改错了还会把
    /// SYS 槽写进一个不存在的位置（那一路的风扇就再也不受控）。
    /// </para>
    /// <para>
    /// 只读，**失败不抛**：探不到就退回档案里的 `fans`，行为与今天完全一致。
    /// 第一次失败就置位不再重试 —— 采样是 500ms 一次，固件不认这条命令时
    /// 每分钟多打 120 次往返没有任何意义。
    /// </para>
    /// </summary>
    public uint? ProbeSkuOnce()
    {
        // 锁而不是单纯 bool：采样线程和"一键采集"会同时进来，先置位再读会让后来的那个
        // 拿到 null（采集包里就少了 SKU 这一条证据）。这里只探一次，锁一次的代价可以忽略。
        lock (_skuLock)
        {
            if (_skuProbed) return _sku;
            _skuProbed = true;
            if (_ec.Sub("sku") < 0) return null;    // 档案没声明 → 不探（别的模具一点不受影响）

            try
            {
                var sku = Exchange(EcCommand.Read(Sub("sku"))).U32(EcFrame.Dat2);
                _sku = sku;
                _ec.ApplySku(sku);
            }
            catch
            {
                _sku = null;
            }
            return _sku;
        }
    }

    /// <summary>
    /// EC 状态快照（温度 / 转速 / 背光档）。
    /// <para>
    /// ⚠️ 偏移来自机型档案 `ec.layout`。`LayoutVerified=false` 的机型**只回原始字节**，
    /// 不回 cpu_temp/fan*_rpm —— 偏移猜错等于把某个寄存器当温度显示，
    /// 那比"不显示"危险得多（用户定的规矩：宁慢也不显示错的）。
    /// </para>
    /// </summary>
    public JsonObject ReadStatus()
    {
        ProbeSkuOnce();       // 顺带定风扇路数（只读、只探一次；档案没声明就什么都不做）

        var r = Exchange(EcCommand.Read(Sub("status")));
        var st = new JsonObject { ["raw"] = r.Hex };

        if (_ec.LayoutVerified)
        {
            foreach (var (key, slot) in _ec.Layout)
                st[key] = ValueOf(r, slot);
        }
        else
        {
            st["layout_unverified"] = true;
        }

        // 三风扇机型的 SYS 路：读数**不在这一帧里**，在档案声明的另一条子命令
        // （`ec.cmd.sys_info` = 0x0207 的 a2=温度 / a3=转速）。
        //
        // 依据是 FanControlPortable v3 的 `FanApi.ReadSysInfo`（2026-11-18 反编译核对）：
        //   a0=0xFA00 / a1=519(0x0207) → `sysTemp = (byte)data.a2; sysRpm = (ushort)data.a3;`
        // 它只在 SKU 51/52 时才读这条；我们除了 SKU/档案判定，还多一层**实测**：
        // 档案判两风扇但这条命令读回了可信的 SYS 转速 ⇒ 就是三风扇（见 ThirdFanProbe）。
        //
        // ⚠️ 这一段**故意不放在 `LayoutVerified` 里**：SYS 是独立的一张表，
        //    通用档案（layout 未标定）的三风扇机也应该能显示 SYS 温度和转速。
        //    主布局没标定只影响 CPU/GPU 读数，不影响这条只读命令的安全性。
        var probeSys = _ec.SysProbe && _ec.Fans < 3 && _sysProbe.ShouldSample(Now());
        if ((_ec.Fans >= 3 || probeSys) && _ec.SysSub >= 0 && _ec.SysLayout.Count > 0)
        {
            try
            {
                var sf = Exchange(EcCommand.Read((ushort)_ec.SysSub));

                // 串帧保护（2026-11-18 实测发现）：`read_gap` 提速到 0 之后，连续读两条
                // 不同子命令时固件可能把**上一条**（0x0200）的响应原样返回 ——
                // 现场形态是整条 SYS 帧变成 CPU 温度 / GPU 温度 / fan1 / fan2 转速，
                // 于是 `sys_temp` 变成 CPU 温度、`fan3_rpm` 变成 fan1 转速，全都"看起来合理"。
                // 判据：载荷（偏移 4 起 16 字节）与刚读的 status 帧完全相同且非全零。
                // 发现就隔一个写间隔重读一次；两次都串就这一拍**不给** SYS 读数，
                // 留 `sys_replay` 给排查（宁可少一路，也不显示串来的值）。
                // 这一拍是"出货"（已经三路）还是"探测"（档案判两路、拿读数碰运气）。
                // 探测拍**不往快照里写诊断键** —— 两风扇机上天天带一个 sys_replay 只会误导排查。
                var delivering = _ec.Fans >= 3;
                var reuse = false;

                if (SamePayload(sf, r))
                {
                    var again = Exchange(EcCommand.Read((ushort)_ec.SysSub), _ec.MinGap);
                    if (SamePayload(again, r))
                    {
                        reuse = true;
                        if (delivering) st["sys_replay"] = true;
                    }
                    else sf = again;
                }

                // 全 0 帧是"EC 未就绪 / 跨进程单槽缓冲被顶掉"的形态（见 EcFrame.IsAllZero），
                // 不是真的 SYS 停转：真停转时 a2 的温度仍在，整帧不会全零。
                // 0x0207 实测不回显（见 EcEcho 注释），没法靠回显挡，只能这样认。
                if (sf.IsAllZero)
                {
                    reuse = true;
                    if (delivering) st["sys_zero_frame"] = true;
                }

                if (!reuse)
                {
                    // ---- 运行时三风扇判定（档案判两风扇时才有意义）----
                    // 只看固件自己报的 a3（SYS 转速）：连续两次 300..8000 才算数。
                    // 两风扇机上这个字段实测恒 0 ⇒ 这条永远不成立，界面不会多画一个圈。
                    if (_ec.Fans < 3 && _ec.SysProbe && _sysProbe.Pending)
                    {
                        var tSlot = _ec.SysLayout.TryGetValue("sys_temp", out var ts)
                            ? ts : (EcFrame.Dat2, 1);
                        var rSlot = _ec.SysLayout.TryGetValue("fan3_rpm", out var rs)
                            ? rs : (EcFrame.Dat3, 2);
                        var pTemp = ValueOf(sf, tSlot);
                        var pRpm = ValueOf(sf, rSlot);
                        // GPU 温度只用于**负向证据**（两风扇机上 a2 是它的镜像）：
                        // 布局没标定的机型拿不到，传 null 就只按 30 s / 5 min 慢慢探。
                        long? pGpu = _ec.Layout.TryGetValue("gpu_temp", out var gSlot)
                            ? (long)ValueOf(r, gSlot) : null;

                        // 探测拍留痕：三风扇机用户报"还是没有 SYS"时，这两行就是唯一证据
                        // （读到的 a3 到底是多少）。第 1 次与之后每 20 次各打一行，不刷日志。
                        if (_sysProbe.Samples == 0 || _sysProbe.Samples % 20 == 0)
                            Console.WriteLine($"[ec] 0x{_ec.SysSub:X4} 探测第三路：a2={pTemp} ℃ / a3={pRpm} RPM"
                                              + $"{(_ec.Layout.ContainsKey("gpu_temp") ? $"/ GPU={pGpu} ℃" : "")}"
                                              + $"（判据 {ThirdFanProbe.MinRpm}~{ThirdFanProbe.MaxRpm} RPM 连续 "
                                              + $"{ThirdFanProbe.ConfirmHits} 次；第 {_sysProbe.Samples + 1} 次采样）");

                        var wasQuiet = _sysProbe.Quiet;
                        var verdict = _sysProbe.Observe(Now(), pTemp, pRpm, pGpu);

                        // 降速：两风扇机上 a2 逐拍等于 GPU 温度且 a3=0，基本就是"这条命令没 SYS 语义"。
                        // 只是降速不是判决 —— 读到转速或温度偏离 GPU 会自动恢复密集采样。
                        if (!wasQuiet && _sysProbe.Quiet)
                            Console.WriteLine($"[ec] 0x{_ec.SysSub:X4} a2 连续 {ThirdFanProbe.QuietAfterMirrors} 次等于 "
                                              + $"GPU 温度（{pTemp} ℃）且 a3=0 ⇒ 本机这条命令没有 SYS 语义，"
                                              + $"探测放慢到每 {ThirdFanProbe.QuietIntervalSeconds / 60} 分钟一次");

                        if (verdict == ThirdFanVerdict.HasThirdFan && _ec.RaiseFans(3, "sys_probe"))
                        {
                            st["fans_source"] = "sys_probe";
                            Console.WriteLine($"[ec] 0x{_ec.SysSub:X4} 连续 {ThirdFanProbe.ConfirmHits} 次读到 "
                                              + $"SYS 转速（{pRpm} RPM / {pTemp} ℃）⇒ 判定三风扇机型，"
                                              + "界面与曲线按三路走（档案/SKU 表没覆盖到也认）");
                        }
                    }

                    // 升级到三路之后，**同一帧**就是这一拍的 SYS 读数，不用再读一次。
                    if (_ec.Fans >= 3)
                    {
                        foreach (var (key, slot) in _ec.SysLayout)
                        {
                            var v = ValueOf(sf, slot);

                            // 物理量程兜底（不是"信不信这条偏移"，是"这个数不可能是它"）：
                            //   SYS 温度 0~125 ℃、转速 0~12000 RPM。
                            // 越界就整条丢掉，让界面画 "--" —— 项目铁律：宁可少一路，不显示错的。
                            // 注意：**不要**因为"它和 GPU 温度一样"就丢（两风扇机上 a2 会镜像 GPU，
                            // 但那条路我们根本不读；三风扇机上 SYS 贴着 GPU 是正常的）。
                            if (key is "sys_temp" && v > 125) continue;
                            if (key.Contains("rpm", StringComparison.Ordinal) && v > 12000) continue;

                            st[key] = v;
                        }
                        _lastRealSysTs = Now();
                    }
                }
            }
            catch (Exception e)
            {
                // SYS 读不到不能把整份快照搞掉：CPU/GPU 两路是好的，曲线还要靠它们。
                st["sys_error"] = e.Message;
            }

            // 这一拍没拿到真读数时沿用上次值，别让第三个转速圈闪一下归零。
            CarryOverSys(st);
        }

        _lastStatus = (JsonObject)st.DeepClone();
        _lastStatusTs = Now();
        return st;
    }

    /// <summary>
    /// 按 layout 里的长度取一个值（1 字节 / 2 字节会**掩码**，4 字节走 U32）。
    ///
    /// 为什么不能一律 U32：`0x0207` 的 SYS 温度只占 a2 的低 1 字节、转速只占 a3 的低
    /// 2 字节，高位是固件留的脏数据。按 4 字节整读，脏位会被算进温度/转速里。
    /// 本机 `layout` 里所有项都是 4 字节，走 `_ =>` 那一支，读法与改动前逐字节一致。
    /// </summary>
    private static uint ValueOf(EcFrame f, (int Offset, int Length) slot) => slot.Length switch
    {
        1 => f[slot.Offset],
        2 => (uint)(f[slot.Offset] | (f[slot.Offset + 1] << 8)),
        3 => f.U32(slot.Offset) & 0xFFFFFFu,
        _ => f.U32(slot.Offset),
    };

    /// <summary>
    /// 两张响应帧的**载荷**是否一模一样（偏移 4 起 16 字节 = a2..a5），且不是全零。
    ///
    /// <para>
    /// 用来识别"固件把上一条命令的响应又返回来一次"（串帧）。为什么要排除全零：
    /// 两风扇机型上 SYS 帧本来就是一片 0，全零相等判断不出任何东西，会误判成串帧
    /// 反而把正常的 0 值丢掉。前 4 字节（组号 + 子命令回显）不参与比较 —— 它们本来
    /// 就该不同（0xFA00 00 02 vs 0xFA00 00 07）。
    /// </para>
    /// </summary>
    private static bool SamePayload(EcFrame a, EcFrame b)
    {
        var nonZero = false;
        for (var i = 4; i < 20; i++)
        {
            if (a[i] != b[i]) return false;
            if (a[i] != 0) nonZero = true;
        }
        return nonZero;
    }

    /// <summary>
    /// SYS 路这一拍没拿到真读数（串帧 / 全 0 帧 / 异常）时沿用上次真读数（2026-11-19）。
    /// </summary>
    private void CarryOverSys(JsonObject st)
    {
        // st 里没有 fan3_rpm ⇒ UI 的 SetRpm(null) 把第三个圈归零、数字变 "--"，
        // 下一拍读到真值又扫回来 —— 用户看到的就是"圈突然变 0 又恢复"。
        // 年龄从**最后一次真读到**算起，不能用 _lastStatusTs：那个每拍都会刷新，
        // EC 真挂了会被无限续命，旧转速永远显示下去（宁可 "--"，不显示错的）。
        if (_lastStatus is null || _lastRealSysTs <= 0) return;
        if (Now() - _lastRealSysTs > 5.0) return;

        foreach (var key in new[] { "sys_temp", "fan3_rpm" })
        {
            if (st[key] is not null) continue;
            if (_lastStatus[key] is { } old) st[key] = old.DeepClone();
        }
    }

    /// <summary>
    /// 上一次 <see cref="ReadStatus"/> 的结果（快照，不发 WMI）。
    /// <para>
    /// ⚠️ <paramref name="maxAge"/> 必须由**控制类**调用方传：EC 读连续失败时
    /// <c>_lastStatus</c> 永远不会被覆盖，旧签名于是把"十分钟前的温度"当真值返回，
    /// 风扇曲线按冻结温度算占空比（45 °C 的旧值 ⇒ 实际 95 °C 还在 30%）。
    /// 超时返回 null，由调用方降级。
    /// </para>
    /// </summary>
    public JsonObject? CachedStatus(TimeSpan? maxAge = null)
    {
        if (_lastStatus is null) return _lastStatus;
        if (maxAge is { } limit && _lastStatusTs > 0 && Now() - _lastStatusTs > limit.TotalSeconds)
            return null;
        return _lastStatus;
    }

    /// <summary>
    /// 按机型档案的 layout 读状态里的单个字段。
    /// ⚠️ 不要写死偏移 —— 换机型就错，而且是"读错寄存器当温度"那种错。
    /// 档案没标定就直接抛，让上层走"读不到 → 整行隐藏"。
    /// </summary>
    private uint ReadStatusField(string key)
    {
        if (!_ec.Layout.TryGetValue(key, out var slot))
            throw new InvalidOperationException($"机型档案没有定义 {key} 的读数偏移");
        return Exchange(EcCommand.Read(Sub("status"))).U32(slot.Offset);
    }

    /// <summary>读 EC 的 APMD（当前性能模式值）。读组无副作用。</summary>
    /// <param name="gap">
    /// 显式间隔（秒）。只给"写后读回"用：那时上一条是写，通道按写间隔等，
    /// 传档案 <c>min_gap</c> 就能把"等多久"钉在实测落地时间上。
    /// </param>
    public uint ReadPerfApmd(double? gap = null)
        => Exchange(EcCommand.Read(Sub("perf")), gap).U32(EcFrame.Dat2);

    /// <summary>把当前 APMD 翻成 presets.perf.modes 的键名（认不出返回 null）。</summary>
    public (string? Key, uint Apmd) PerfModeKey(uint apmd)
    {
        // ⚠️ 必须持模型锁枚举：`JsonObject` 不是线程安全的，而这条读数每 500ms 跑一次，
        //    同时 HTTP 线程可能在 `perf.modes` 上增删键（首次调「自定义」灯效会
        //    `EnsureMode` 加一个 custom 档、背光写入会 Remove/Add `light`）。
        //    不锁就会 `Collection was modified` ⇒ 整帧快照被换成 {"error":…}（界面当帧断线）。
        var store = _model;
        if (store is null) return (null, apmd);
        lock (store.SyncRoot)
        {
            if (store.Presets["perf"]?["modes"] is not JsonObject modes) return (null, apmd);
            foreach (var (k, v) in modes)
            {
                if (v is JsonObject o && o["apmd"] is JsonNode n
                    && Bounds.TryToDouble(n, out var val) && (uint)val == apmd)
                    return (k, apmd);
            }
        }
        return (null, apmd);
    }

    public JsonObject ReadGfxMode()
        => new() { ["gfx_mode"] = Exchange(EcCommand.Read(Sub("gfx"))).U32(EcFrame.Dat2) };

    /// <summary>
    /// 显卡模式的**读探针**：比 <see cref="ReadGfxMode"/> 多留原始帧与 DAT4。
    /// 纯读（读组 0xFA00），不要 arm、不看总闸、不碰写入。
    ///
    /// 用途只有一个：机型适配时把"这台 EC 说不说得出当前模式"当成证据写进采集包
    /// （见 `App/GfxProbe.cs`）。⚠️ 它读的是**上次开机生效**的模式，而且 DSDT 里
    /// 这个值是从 `SAPD` 映射来的 —— 读到 1/2/3 不等于"这台能切"，别拿它开写权限。
    /// </summary>
    public JsonObject ReadGfxProbe()
    {
        var f = Exchange(EcCommand.Read(Sub("gfx")));
        return new JsonObject
        {
            ["raw"] = f.Hex,
            ["gfx_mode"] = f.U32(EcFrame.Dat2),
            ["dat4"] = f.U32(EcFrame.Dat4),
        };
    }

    /// <summary>读 ENTP / AWMI（读组 0x0202）。纯读，无副作用。</summary>
    public JsonObject ReadPerfEntp()
    {
        var r = Exchange(EcCommand.Read(Sub("read_perf")));
        return new JsonObject
        {
            ["entp"] = r.U32(EcFrame.Dat2),
            ["awmi"] = r.U32(EcFrame.Dat3),
        };
    }

    /// <summary>读 VGAT / RPM5（读组 0x0207）。纯读。</summary>
    public JsonObject ReadGpuRpm()
    {
        var r = Exchange(EcCommand.Read(Sub("gpu_info")));
        return new JsonObject
        {
            ["vgat"] = r.U32(EcFrame.Dat2),
            ["rpm5"] = r.U32(EcFrame.Dat3),
        };
    }

    /// <summary>
    /// 读固件的**风扇控制权标志**（`0x0206` 的 a2）。纯读，只用于排查。
    ///
    /// <para>
    /// 语义来自 FanControlPortable v3（`ReadFanControlStatus`）：`1` = 软件接管（UFA 生效），
    /// `0` = 交回固件。我们自己发的那一半在 <see cref="SetFanControl"/>（接管前写 1）。
    /// </para>
    /// <para>
    /// ⚠️ 本机实测（两风扇 NL5，原厂 daemon 在跑）：读回**恒为 1**，写 0 之后也立刻变回 1 ——
    /// daemon 每轮都会把它保持成 1。所以这个值只能当"当前是谁在管"的参考，
    /// 不能拿它判断"我们的 UFA 有没有生效"。
    /// </para>
    /// </summary>
    public JsonObject ReadFanControlFlag()
    {
        if (_ec.Sub("omcc") < 0)
            throw new InvalidOperationException("机型档案没有声明 ec.cmd.omcc，无法读风扇控制权");

        var f = Exchange(EcCommand.Read(Sub("omcc")));
        return new JsonObject
        {
            ["flag"] = ValueOf(f, (EcFrame.Dat2, 1)),   // 低 1 字节；高位是固件留的脏数据
            ["raw"] = f.Hex,
        };
    }

    // ================================================================ 写

    /// <summary>
    /// 把 EC 的风扇控制权标成「软件接管」（`0x0206` 的 a2 = 1）。
    ///
    /// <para>
    /// 依据是 FanControlPortable v3（2026-11-18 反编译核对）：`TakeoverFan()` 在写第一条
    /// UFA **之前**先 `WriteFanControlStatus(1)`；`RestoreAuto()` 则写 `0` 再下发
    /// `255,255,255`。也就是说 `0x0205` 的 UFA 只有在控制权被标成 1 时才稳定生效，
    /// 0 是"交回固件"的那一半。
    /// </para>
    /// <para>
    /// ⚠️ 本机实测（两风扇 NL5）：**原厂 daemon 在跑时** `0x0206` 读回 a2 恒为 1 ——
    /// daemon 每 5 秒会把标志和 UFA 一起刷回去，那时这条写只是**幂等的确认**。
    /// 把 daemon 停掉之后它就是真的开关：写 0 之后紧跟 UFA=0xFF 才会真的交回
    /// （见 <see cref="ReleaseToEc"/>，2026-11-18 实测）。
    /// </para>
    /// <para>
    /// ⚠️ 这里内部调 <see cref="Arm"/> 是刻意的：`0x0206` 在档案里是 confirm_required，
    /// 但这一个 arm 由"用户已经打开了自定义风扇 / 手动转速"这个动作派生，不是裸调试写。
    /// 一次 arm 只够一次写（见 <see cref="Armed"/>），不会留下长期授权。
    /// </para>
    /// </summary>
    public JsonObject SetFanControl(bool takeOver)
    {
        var sub = Sub("omcc");
        Arm(sub, ttl: 5);
        var f = Exchange(EcCommand.Write(sub, EcArgs.FromD0(takeOver ? 1u : 0u)), _ec.MinGap);
        lock (_fanCtlLock) _fanControlTaken = takeOver;

        return new JsonObject
        {
            ["ok"] = true,
            ["flag"] = takeOver ? 1 : 0,
            ["resp"] = f.Hex,
        };
    }

    /// <summary>
    /// 第一次下发 UFA 之前，把控制权标成 1（见 <see cref="SetFanControl"/>）。**幂等、失败不致命**。
    ///
    /// <para>
    /// 为什么要"第一次"而不是每拍：这条命令改的是控制权状态，不是转速；重复发只是多一次
    /// WMI 往返 + 多一次 EC 写。曲线每 5 秒一发，绑在里面会让每拍多花 ~0.5 秒。
    /// </para>
    /// <para>
    /// 失败时**只放过标志**，照样去写 UFA：老机器/固件可能不认 0x0206，
    /// 这时 UFA 依然是唯一可用的控制手段（本机历史上就是这么工作的）。
    /// 但"写入被拒（总闸/权限）"要原样抛出去 —— 那是确定性失败，上层必须自停。
    /// </para>
    /// </summary>
    private void EnsureFanControl()
    {
        lock (_fanCtlLock)
        {
            if (_fanControlTaken) return;
        }

        if (_ec.Sub("omcc") < 0) return;        // 档案没这条命令 ⇒ 照旧直接写 UFA

        try
        {
            SetFanControl(true);
        }
        catch (WriteDeniedException)
        {
            throw;
        }
        catch
        {
            // 固件不认这条命令：不是致命错误，UFA 继续写。
            lock (_fanCtlLock) _fanControlTaken = true;   // 别每拍都重试这条
        }
    }

    /// <summary>
    /// **把风扇交回 EC 固件**（`POST /api/fancurve/enabled {enabled:false}` 的落点）。
    ///
    /// <para>
    /// 两步，**顺序不能反、也不能只做一半**（2026-11-18 本机实证，原厂 daemon 已停、
    /// 曲线引擎停写，逐条对比）：
    /// <list type="number">
    /// <item>`0x0206 = 0` —— 清掉"软件接管"标志；</item>
    /// <item>`0x0205` 三路 UFA = **`0xFF`** —— 清掉目标转速。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 实测三组对照（钉 30% ≈ 1500 RPM，CPU 72~78 ℃）：
    /// <code>
    /// 只写 0x0206=0          → 1500 RPM 纹丝不动    （不清 UFA = 没交回）
    /// 只写 UFA=0xFF          → 5000 RPM 满转        （手动态下 0xFF 是 100%，不是"忽略"）
    /// 0x0206=0 然后 UFA=FF   → 8 秒内 1497 → 3802   （回到 EC 自己的温度曲线）✅
    /// </code>
    /// 第三组就是 CCLite（control-center-lite）`FanService.Enable(false)` 的做法，
    /// 也是 docs/PROTOCOL.md 里「`0xFF` = 忽略、保持原值」那条的**反例** ——
    /// 那条是在"没清标志"的手动态下测的，看到的是 100% 钳位的另一面。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 失败语义：`0x0206` 写不进去（总闸/权限/固件不认）时**绝不写 `0xFF`** ——
    /// 那一刻 `0xFF` 等于把风扇拉到 100%，正是"交回"的反面。宁可停在"我们不再写"，
    /// 由调用方如实报"已停写，但 EC 里还留着上一次的转速"。
    /// </para>
    /// </summary>
    public JsonObject ReleaseToEc()
    {
        var fanSub = Sub("fan");
        if (fanSub < 0)
            throw new InvalidOperationException("机型档案没有声明 ec.cmd.fan，无法交回风扇");

        // ① 先清接管标志。这一步失败就整件事失败（理由见上面的失败语义）。
        SetFanControl(false);           // 内部 arm；被拒时抛 WriteDeniedException

        // ② 三路 UFA 全写 0xFF（= 交回值）。按槽名写，与 SetFans 同一个映射。
        var args = EcArgs.Empty;
        foreach (var slot in _ec.FanSlots.Take(3)) args = args.With(slot, 0xFFu);
        var f = Exchange(EcCommand.Write(fanSub, args), _ec.MinGap);

        // 交回之后 EC 里不再有"我们钉的值"，对外别再报 DutyApplied
        _duty = [null, null, null];
        OemFanMax = false;

        return new JsonObject
        {
            ["ok"] = true,
            ["released"] = true,
            ["duty"] = 0xFF,
            ["resp"] = f.Hex,
        };
    }

    /// <summary>
    /// 手动风扇百分比。
    /// <para>
    /// ⚠️ **写 0 是停转，不是交回自动**（2026-10-13 二次实证：3500 RPM → 0，EC 不接管）。
    /// 所以所有 0 / null 一律钳到 `presets.fan.min_duty`（默认 30）。
    /// 但钳到 30 也只是"不停转"，EC 拿到非零 UFA 后会一直照这个值转
    /// （实测 84 °C 下 30% ≈ 1500 RPM 锁死数小时）。
    /// 「恢复自动」有两条：切到软件曲线（曲线接管），或 **<see cref="ReleaseToEc"/>**
    /// （`0x0206=0` + 三路 UFA=`0xFF`，几秒内真回到固件自己的温度曲线 —— 2026-11-18 实测）。
    /// </para>
    /// </summary>
    public JsonObject SetFans(int? cpu = null, int? gpu = null, int? sys = null)
    {
        var lo = Bounds.Clamp("fan_duty",
            _model?.Get("fan", "min_duty") is JsonNode n && Bounds.TryToDouble(n, out var v) ? v : 30);

        int Pick(int? x, int i)
        {
            if (x is null)
            {
                var prev = _duty[i];
                return prev is > 0 ? prev.Value : lo;
            }
            var rounded = (int)Math.Round((double)x.Value, MidpointRounding.ToEven);
            return Math.Max(lo, Math.Min(100, rounded));
        }

        var d0 = Pick(cpu, 0);
        var d1 = Pick(gpu, 1);
        var d2 = Pick(sys, 2);

        // SYS 槽（UFA3）的两种"跟随"规则，都照原厂来：
        //
        // ① 三风扇机型：软件曲线只算 CPU / GPU 两路，SYS 不能被动挨写 ——
        //    传 null 时 `Pick` 会取"上一次的值，没有就 min_duty(30%)"，等于把系统风扇
        //    钉在 30%。给它一条明确的规则：**跟最热的那一路**。
        // ② 两风扇机型：原厂对这一路写 **255**（= 忽略）。实证在
        //    `ControlCenterDaemon.MonitorAndControlFan` 的非三风扇分支：
        //    `SetFanSpeedWMI(CPU, GPU, 255)`；DSDT 的 0x0205 分支里 UFA3 还套了一层
        //    风扇存在性判断（GGIV 0x080E000E）。我们以前写的是 min_duty(30) ——
        //    固件多半会因为"这台风扇不存在"而忽略，但"没这台风扇还给它一个目标转速"
        //    本来就不该有，照原厂写 255。
        if (sys is null)
            d2 = _ec.Fans >= 3 ? Math.Max(d0, d1) : 255;

        // ⚠️ 三个 UFA 落在哪个参数槽由机型档案 `ec.fan_slots` 决定
        //    （本机 = ["d0","a","d2"]，换机型只改 json，不动代码）。
        //    按槽名写入，而不是按位置传参 —— 位置参数下 d1 会被 a 覆盖，
        //    表现为"左风扇满转、右风扇不动"，编译期发现不了。
        var slots = _ec.FanSlots.Take(3).ToList();
        var duties = new[] { d0, d1, d2 };
        var args = EcArgs.Empty;
        for (var i = 0; i < slots.Count && i < duties.Length; i++)
            args = args.With(slots[i], (uint)duties[i]);

        // 先标控制权再写 UFA（参考实现 TakeoverFan 的顺序：0x0206=1 → 0x0205）。
        // 这一步只在本次进程**第一次**下发时真发一次，之后是纯内存判断。
        EnsureFanControl();

        Exchange(EcCommand.Write(Sub("fan"), args));

        // ⚠️ 只有**写成功之后**才更新 _duty：先记后写会让 WMI 瞬时故障时的
        //    DutyApplied 对外撒谎（界面显示"风扇停在 55%"，EC 里其实是上一次的值）。
        _duty = [d0, d1, d2];
        OemFanMax = false;          // 正常手动/曲线接管了，临时满转态结束
        return new JsonObject { ["ok"] = true, ["fan1"] = d0, ["fan2"] = d1, ["fan3"] = d2 };
    }

    /// <summary>
    /// 睡眠唤醒后**重置进程内的风扇状态**，**不写 EC**。
    ///
    /// <para>
    /// S3 醒来 EC 会重新初始化，`0x0205` 钉的转速不会被保留 —— 所以那些"我们以为 EC 里还钉着"
    /// 的内存状态必须作废：
    /// <list type="number">
    /// <item><b>临时满转</b>：留着 `OemFanMax` / `_duty=100`，界面会继续写「恢复自动」
    /// 「最大转速（临时）」，而风扇早在按固件曲线跑了 —— 显示假话（用户对这条零容忍）。</item>
    /// <item><b>接管标志</b>：`0x0206` 也可能跟着被清成 0（PROTOCOL 只验证过"原厂 daemon 在场时
    /// 恒为 1"，唤醒后的真实值没有实测）。这里**无条件**让它下次写 UFA 前重标一次 1 ——
    /// 幂等，代价只是唤醒后曲线第一笔多一条命令（约 0.5 s 往返）；不重标的后果是
    /// "曲线显示运行中、风扇却不跟"（实测 OMCC=0 时写 100 只维持约 8 秒）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// ⚠️ 别拿它替代 <see cref="ReleaseToEc"/>：主动交回必须真的发 `0x0206=0` + UFA=0xFF。
    /// ⚠️ `_duty` 只在临时满转时清：曲线还在跑时那笔值仍代表 EC 里的现状（下一拍就会刷新），
    /// 提前清掉会让 `Stop(release:true)` 误判"没写过、不用交回"。
    /// </para>
    /// </summary>
    public void OnSystemResume()
    {
        if (OemFanMax)
        {
            OemFanMax = false;
            _duty = [null, null, null];
        }
        lock (_fanCtlLock) _fanControlTaken = false;
    }

    /// <summary>
    /// 「最大转速」在**未开启自定义风扇**时的那一半：临时接管 + 三路满转。
    ///
    /// <para>
    /// 2026-11-25 本机实测（固件模式、原厂 daemon 未跑），"原厂 max"不能只写 UFA：
    /// <code>
    /// 只写 0x0205 = 0xFF          → 3348 RPM 纹丝不动，随后随温度走（EC 当成"保持/忽略"）
    /// 只写 0x0205 = 100           → 冲到 5002，约 8 秒后自己回落到 3648（固件拿回去了）
    /// 0x0206=1 + 0x0205 = 100     → 持久 5002 / 4845 满转 ✅
    /// </code>
    /// 结论：不接管就拉不到满转 —— EC 只在 `0x0206=1` 时认 UFA。所以这里跟
    /// <see cref="SetFans"/> 走同一条接管前置（<see cref="EnsureFanControl"/>），
    /// 区别只在**不动曲线开关、不动 `presets.fan.mode`、不动滑块**：
    /// 用户关着自定义风扇时点一下 = 借一下控制权拉满，再点一下交回。
    /// </para>
    /// <para>
    /// ⚠️ `_duty` 必须记成 100：`FanCurveEngine.Stop(release:true)` 只在
    /// `DutyApplied is not null` 时交回，否则退出程序后风扇会一直满转。
    /// </para>
    /// <para>
    /// ⚠️ 两风扇机型的第三槽照 <see cref="SetFans"/> 写 255（原厂语义 = 忽略该路）；
    /// 三风扇机型才是真正的三路满转。
    /// </para>
    /// </summary>
    public JsonObject SetOemFanMax()
    {
        var fanSub = Sub("fan");
        if (fanSub < 0)
            throw new InvalidOperationException("机型档案没有声明 ec.cmd.fan，无法下发最大转速");

        var slots = _ec.FanSlots.Take(3).ToList();
        int[] duties = _ec.Fans >= 3 ? [100, 100, 100] : [100, 100, 255];

        var args = EcArgs.Empty;
        for (var i = 0; i < slots.Count && i < duties.Length; i++)
            args = args.With(slots[i], (uint)duties[i]);

        // ⚠️ 顺序与参考实现一致：先标接管，再写 UFA（见 EnsureFanControl）。
        EnsureFanControl();
        var f = Exchange(EcCommand.Write(fanSub, args));

        // 只有写成功之后才记状态（同 SetFans 的理由）。
        _duty = [duties[0], duties[1], duties[2]];
        OemFanMax = true;

        return new JsonObject
        {
            ["ok"] = true,
            ["mode"] = "oem-max",
            ["fan1"] = duties[0],
            ["fan2"] = duties[1],
            ["fan3"] = duties[2],
            ["resp"] = f.Hex,
        };
    }

    /// <summary>写 APMD 后等 EC 落地再读回（与 MachineProbe 的 SettleMs 同口径）。</summary>
    private const int PerfSettleMs = 450;
    /// <summary>
    /// 性能模式（索引 1/2/3）。
    /// <para>
    /// ⚠️ 下发的是 **APMD 值**，不是索引 —— BIOS 把 GWD0 原样透传到 APMD 寄存器。
    /// 早期发索引导致 APMD=2/3（不是有效档位），表现为"游戏模式功耗比办公还低"。
    /// 取值来自 `presets.perf.modes[*].apmd`，兜底回 DEFAULT_PRESETS，
    /// **绝不另写字面量**（两处各写一份必然对不上，已经错过一次）。
    /// </para>
    /// <para>
    /// 2026-11-18：写 EC 只是半件事。原厂 ControlCenter 把当前系统模式存在
    /// `HKLM\...\NL5\ControlCenter\ModeBeforeDC`，启动时按它重放 `0x0300` ——
    /// 我们不同步，用户一打开原厂就被旧档覆盖（CCLite 之所以没事就是它写了这个键）。
    /// 所有用户主动切档路径（界面 / `/api/ec/perf` / FN 热键）都过这里，
    /// 所以镜像放在**读回校验通过**之后，一处覆盖三条路径。
    /// `mirrorOem:false` 只给「拔电自动切办公」用：那个键同时是原厂插电恢复的基准，
    /// 原厂自己在 DC 模式下也不写它，详见 <see cref="OemModeRegistry"/>。
    /// </para>
    /// </summary>
    /// <param name="value">界面索引 1=office / 2=game / 3=beast。</param>
    /// <param name="mirrorOem">是否把新 APMD 同步进原厂 ModeBeforeDC；自动切档传 false。</param>
    public JsonObject SetPerfMode(int value, bool mirrorOem = true)
    {
        var key = value switch { 1 => "office", 2 => "game", 3 => "beast", _ => null };
        int? apmd = null;

        if (key is not null && _model?.Get("perf", "modes", key, "apmd") is JsonNode n
            && Bounds.TryToDouble(n, out var v))
            apmd = (int)v;

        if (apmd is null && key is not null
            && PresetDefaults.Defaults["perf"]?["modes"]?[key]?["apmd"] is JsonNode fn
            && Bounds.TryToDouble(fn, out var fv))
            apmd = (int)fv;

        if (apmd is null)
            return new JsonObject { ["ok"] = false, ["error"] = $"无效的性能模式索引：{value}" };

        var written = SetPerfApmd((uint)apmd.Value);
        written["mode"] = value;
        if (written["ok"]?.GetValue<bool>() != true) return written;

        // ⚠️ WMI 的 Put 被接受 ≠ APMD 真的变了。界面 / FN+3 只看 ok 就会落
        //    last_mode、切电源计划、排功耗墙；自动切档路径本来自己读回，手动路径
        //    却没有 —— 统一在这里做，三条路径一个口径。读不回来说 ok:false，绝不假成功。
        //
        // 2026-11-15：不再"闭眼睡满 450 ms 再读一发"。EC 落地通常在 200 ms 上下，
        // 固定睡满等于每次切档都多等半秒（叠加写间隔，原来一次切档 ≈ 0.9 s）。
        // 改成**命中即返回**：先等档案 min_gap（实测落地的最短间隔）读一发，对了立刻走；
        // 没落地才继续每 120 ms 补读，**总上限仍是 PerfSettleMs**，行为的下界没变。
        var settleStep = (int)Math.Round(Math.Max(0.05, _ec.MinGap) * 1000);
        var waited = 0;
        uint got;
        try
        {
            while (true)
            {
                var step = waited == 0 ? settleStep : Math.Min(120, PerfSettleMs - waited);
                if (step > 0) Thread.Sleep(step);
                waited += step;
                got = ReadPerfApmd(_ec.MinGap);
                if (got == (uint)apmd.Value || waited >= PerfSettleMs) break;
            }
        }
        catch (Exception e)
        {
            return new JsonObject
            {
                ["ok"] = false, ["mode"] = value,
                ["error"] = "写后读回 APMD 失败：" + e.Message,
            };
        }
        if (got == (uint)apmd.Value)
        {
            // 只有 EC 真的落地了才动原厂注册表：写失败却把 ModeBeforeDC 改掉，
            // 下次打开原厂会按一个我们根本没切成的档位重放 EC。
            if (mirrorOem) OemModeRegistry.Sync((uint)apmd.Value);
            return written;
        }

        return new JsonObject
        {
            ["ok"] = false, ["mode"] = value,
            ["wrote"] = apmd.Value, ["readback"] = got, ["waited_ms"] = waited,
            ["error"] = $"写后读回 APMD 是 {got}，不是要写的 {apmd.Value} —— EC 没接受这条命令",
        };
    }

    /// <summary>
    /// 直接下发 **APMD 值**（0x0300 的 d0），不经过"档位索引 → apmd"的映射。
    ///
    /// 只有「一键采集」的写验证用它：那边要证明的是"写什么值、EC 就读回什么值"，
    /// 走 <see cref="SetPerfMode"/> 会被配置里的映射绕一圈，读回对上了也说不清
    /// 是映射对还是写对。**界面/接口一律继续走 <see cref="SetPerfMode"/>**
    /// （它负责把索引翻成 APMD，那是 2026-10-13 踩过坑的地方）。
    /// </summary>
    public JsonObject SetPerfApmd(uint apmd)
    {
        var sub = Sub("perf");
        Exchange(EcCommand.Write(sub, EcArgs.FromD0(apmd)));
        // 授权已由 RequireWrite → Armed() 命中即消耗；这里不能再整键清空，
        // 否则会把同时在跑的另一方（FN+3 / 拔插电自动切档）的配额一起顶掉。
        return new JsonObject { ["ok"] = true, ["apmd"] = apmd };
    }

    /// <summary>
    /// 显卡模式（GFXT）。
    /// <para>
    /// ⚠️ 这条命令是「双面」的，**必须显式走写组**：读组只 `Store(SAPD,_T_3)` 把状态
    /// 映射进 DAT2，不写任何东西。合法取值只有 1/2/3（0 不匹配任何分支 = 空操作）。
    /// 1=混合输出 2=独显直连 3=集显输出；写入后**必须重启**才由固件应用。
    /// </para>
    /// </summary>
    public JsonObject SetGfxMode(int mode)
    {
        // ⚠️ 枚举量要**拒绝**，不能像 duty 那样钳制（2026-11-23 审查发现）：
        //    `Clamp` 会把 0 夹成 1（混合）、9 夹成 3（集显），命令真的写进写组，
        //    用户只是发了个错参数，**下次开机**才发现显示输出变了。
        if (mode is < 1 or > 3)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"无效的显卡模式：{mode}（只接受 1=混合 / 2=独显直连 / 3=集显）",
            };

        var v = mode;
        var sub = Sub("gfx");
        Exchange(EcCommand.Write(sub, EcArgs.FromD0((uint)v)));
        // 同 SetPerfApmd：授权由 Armed() 消费，不清掉别的调用方那一份
        return new JsonObject { ["ok"] = true, ["mode"] = v };
    }

    /// <summary>
    /// 键盘背光（0x0100）。
    /// <para>
    /// 字段含义（两次原厂抓包 + DSDT 实证）：
    /// <c>d0 = LED 地址（分区）</c>、<c>d(offset 11) = (effect&lt;&lt;4) | level</c>、
    /// <c>a/b/c = B/G/R</c>。⚠️ 地址不要写 0（会让灯灭），255 也不是有效分区。
    /// </para>
    /// </summary>
    public JsonObject SetBacklight(int level, int r = 255, int g = 255, int b = 255,
                                   int? addr = null, int? effect = null, double? gap = null)
    {
        var lvl = Bounds.Clamp("backlight_level", level);
        var ledAddr = Bounds.Clamp("backlight_alpha",
            addr ?? (_model?.Get("backlight", "addr") is JsonNode an && Bounds.TryToDouble(an, out var av) ? av : 6));
        var eff = ResolveEffect(effect);
        var dt3d = (byte)((eff << 4) | (lvl & 0x0F));

        // ⚠️ 返回的 rgb 必须是**钳制后**的值：server 会把它原样落进
        //    presets.backlight.rgb，返回未钳制的输入会让越界值持久化到配置里。
        var rb = Bounds.Clamp("backlight_rgb", r);
        var gb = Bounds.Clamp("backlight_rgb", g);
        var bb = Bounds.Clamp("backlight_rgb", b);

        // 槽位按 DSDT：d0 = LED 地址，a/b/c = B/G/R，d = (effect<<4)|level
        var args = EcArgs.Empty
            .With(EcSlot.D0, (uint)ledAddr)
            .With(EcSlot.A, (uint)bb)
            .With(EcSlot.B, (uint)gb)
            .With(EcSlot.C, (uint)rb)
            .With(EcSlot.D, dt3d);

        Exchange(EcCommand.Write(Sub("backlight"), args), gap);

        // 记下"EC 里现在这个地址是什么颜色"—— 下一次批量下发靠它跳过没变的区（见 SetBacklightZones）
        RememberZone(ledAddr, rb, gb, bb, lvl, eff);

        return new JsonObject
        {
            ["ok"] = true,
            ["level"] = lvl,
            ["rgb"] = new JsonArray(rb, gb, bb),
            ["addr"] = ledAddr,
            ["effect"] = eff,
            ["dt3d"] = $"0x{dt3d:X2}",
        };
    }

    /// <summary>灯效码：显式参数 &gt; `presets.backlight.effect` &gt; 1，钳到 [0,15]。</summary>
    private int ResolveEffect(int? effect)
        => Math.Clamp(
            effect ?? (_model?.Get("backlight", "effect") is JsonNode en && Bounds.TryToDouble(en, out var ev)
                ? (int)ev : 1), 0, 15);

    /// <summary>
    /// 「整条灯带」地址：**配置优先于档案**。
    ///
    /// 档案里那个 `ec.kb_trunk_addr` 是出包时写死的（本机 6），而别的机型要到手才试得出来；
    /// 试出来的值写在 `presets.backlight.trunk_addr` 里，本机立刻生效，不用等新档案。
    /// 配置里没有就退回档案 —— 没标定过的机器仍然是"逐区写"，行为一个字都不变。
    ///
    /// 为什么单拎一个口：这个值决定"一条命令还是三条"，写错会让灯亮错分区，
    /// 所以校验只在这里做一次（0 与 &gt;0xFF 都当没标定，和档案那边的口径一致）。
    /// </summary>
    private int? TrunkAddr
    {
        get
        {
            if (_model?.Get("backlight", "trunk_addr") is JsonNode n
                && Bounds.TryToDouble(n, out var v) && v is > 0 and <= 0xFF)
                return (int)v;
            return _ec.KbTrunkAddr;
        }
    }

    /// <summary>
    /// 开一个「写批」：块内的多条**写**按 <paramref name="gap"/> 连发，绕过 <c>min_gap</c> 下限。
    ///
    /// 业务代码用不到它 —— <see cref="SetBacklightZones"/> 自己会按档案 <c>burst_gap</c> 开。
    /// 暴露出来只为**采集期标定**：`burst_gap` 这个键此前只能作者手工试（逐档写三条看耗时），
    /// 而它决定神光同步是"三个区同时变"还是"一个一个爬"。
    ///
    /// ⚠️ 只对写有效：写完之后的第一条**读**仍按完整写间隔等（`_lastWasWrite` 不动），
    ///    「写后别立刻读」这条规矩没有被放开。
    /// </summary>
    public IDisposable Burst(double gap) => _io.WriteBurst(gap);

    /// <summary>
    /// 「EC 里现在是什么颜色」的镜像 —— **只由成功的写更新**，用来决定下一次能不能跳过某个区。
    ///
    /// 为什么需要它：联动关着时用户只调一个区，另外两个区的颜色一个字都没变。
    /// 以前三条一起重发，用户看到的是"灯闪三下"（每条 `0x0100` 都会让 EC 重刷 LED 引擎），
    /// 而原厂只发一条 ⇒ 直接变。有了这份镜像，没变的区就不发命令。
    ///
    /// ⚠️ 它会失真：EC 被**别的进程**改过色（原厂软件）、或 EC 掉电丢了颜色，镜像还是旧的。
    ///    失真时表现为"某个区不跟着变"，点一次「全部同色」/「恢复默认」（`force`）就能刷回来 ——
    ///    这比"每次都闪三下"划算。`WriteBacklightLevel`（自动熄灯恢复）也走无条件全写。
    /// </summary>
    private readonly Dictionary<int, int> _ecZoneColor = new();
    private int _ecZoneLevel = -1;
    private int _ecZoneEffect = -1;

    private void RememberZone(int addr, int r, int g, int b, int level, int effect)
    {
        lock (_ecZoneColor)
        {
            _ecZoneColor[addr] = (r << 16) | (g << 8) | b;
            _ecZoneLevel = level;
            _ecZoneEffect = effect;
        }
    }

    /// <summary>分区颜色补齐成三元组（协议里缺色按白色，与 SetBacklight 的兜底一致）。</summary>
    private static int[] Normalize(int[] rgb) => rgb.Length >= 3 ? rgb : [255, 255, 255];

    /// <summary>RGB 压成一个 int —— 只用于"颜色变没变"的比较，不进 EC。</summary>
    private static int Pack(int[] c) => (c[0] << 16) | (c[1] << 8) | c[2];

    /// <summary>
    /// 忘掉镜像（下一次批量下发无条件全写）。传输失败 / 写后读回不符时调用 ——
    /// 这时候"EC 里是什么颜色"已经不可知了，凭旧镜像跳过某个区就是拿假状态骗用户。
    /// </summary>
    private void ForgetZones()
    {
        lock (_ecZoneColor)
        {
            _ecZoneColor.Clear();
            _ecZoneLevel = -1;
            _ecZoneEffect = -1;
        }
    }

    /// <summary>
    /// 多分区染色。
    ///
    /// 三种走法，按优先级：
    /// <list type="number">
    /// <item><b>整条地址</b>（档案标了 `kb_trunk_addr`，且**所有分区要同一个颜色**）——
    ///   发**一条**命令。2026-11-15 真机标定：`addr=6` 一条就让三个区同时变，
    ///   这正是原厂「神光同步」能做到"同时"的机制（不是批量写）。</item>
    /// <item><b>只写变了的区</b>（<paramref name="skipUnchanged"/>）—— 每条 `0x0100` 都会让
    ///   EC 重刷一次 LED 引擎，没变的区一起重发，肉眼看到的是"闪几下"。</item>
    /// <item><b>逐条连发</b>（写批）—— 分区异色时必须三条，间隔按 `burst_gap`（本机 20ms）。</item>
    /// </list>
    ///
    /// ⚠️ 写批只解决"三条之间的间隔"，整条地址才是"完全同时"。两者不冲突：
    /// 分区异色时没有整条地址可用，只能三条背靠背。
    ///
    /// <paramref name="gap"/> 显式给了就尊重它（诊断/探针要精确控制时用），不再套写批。
    /// </summary>
    public JsonObject SetBacklightZones(int level, IEnumerable<(int Addr, int[] Rgb)> zones,
                                        int? effect = null, double? gap = null,
                                        bool skipUnchanged = false)
    {
        var list = zones.ToList();
        if (list.Count == 0)
            return new JsonObject { ["ok"] = false, ["error"] = "没有分区数据" };

        var lvl = Bounds.Clamp("backlight_level", level);
        var eff = ResolveEffect(effect);

        // 全部分区同一个颜色 + 标定了整条地址 ⇒ 一条命令。
        var uniformColor = list.All(z => Pack(Normalize(z.Rgb)) == Pack(Normalize(list[0].Rgb)))
            ? Normalize(list[0].Rgb)
            : null;
        var trunk = list.Count > 1 && uniformColor is not null ? TrunkAddr : null;

        var work = new List<(int Addr, int[] Rgb)>();
        if (trunk is { } trunkAddr)
        {
            // 整条：要不要发，看三个地址的镜像是不是都已经等于这个颜色
            var packed = Pack(uniformColor!);
            var already = false;
            if (skipUnchanged)
            {
                lock (_ecZoneColor)
                {
                    already = lvl == _ecZoneLevel && eff == _ecZoneEffect
                              && list.All(z => _ecZoneColor.TryGetValue(z.Addr, out var p) && p == packed);
                }
            }
            if (!already) work.Add((trunkAddr, uniformColor!));
        }
        else if (!skipUnchanged)
        {
            work = list;
        }
        else
        {
            // 档位或灯效变了 ⇒ 每条都得重写（EC 按最后一组参数重刷整条灯）；
            // 只有"同一档位/灯效、只是某个区换了颜色"才允许按区跳过。
            lock (_ecZoneColor)
            {
                var envelopeSame = lvl == _ecZoneLevel && eff == _ecZoneEffect;
                foreach (var (addr, rgb) in list)
                {
                    var c = Normalize(rgb);
                    if (envelopeSame && _ecZoneColor.TryGetValue(addr, out var prev) && prev == Pack(c))
                        continue;
                    work.Add((addr, c));
                }
            }
        }

        var results = new JsonArray();

        // 一次用户操作 = 一个写批，**哪怕只有一条**：单条命令如果照旧等写间隔（0.45s），
        // 用户点完颜色要愣半秒才看见灯动 —— 那半秒正是"慢"的体感来源，跟 EC 无关。
        // 档案没标定 burst_gap 的机型仍老老实实逐条写（老行为）。
        IDisposable? burst = work.Count > 0 && gap is null && _io.BurstGap > 0
            ? _io.WriteBurst(_io.BurstGap)
            : null;
        try
        {
            foreach (var (addr, rgb) in work)
                results.Add(SetBacklight(level, rgb[0], rgb[1], rgb[2], addr, effect, gap));
        }
        catch
        {
            // 批量写中途失败 ⇒ EC 里的颜色已经不可知，镜像作废（下一次全写）
            ForgetZones();
            throw;
        }
        finally
        {
            burst?.Dispose();
        }

        // 用整条地址写成功 ⇒ **每个分区**都变成这个颜色了，镜像要跟着更新，
        // 否则下一次调色会以为三个区还是旧色（多发一条整条命令，多闪一下）。
        if (trunk is not null && work.Count > 0)
        {
            var c = uniformColor!;
            foreach (var (addr, _) in list) RememberZone(addr, c[0], c[1], c[2], lvl, eff);
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["level"] = level,
            ["effect"] = eff,
            ["via"] = trunk is null ? "zones" : "trunk",
            ["wrote"] = work.Count,
            ["skipped"] = list.Count - work.Count,
            ["zones"] = results,
        };
    }

    // ================================================================ 诊断

    /// <summary>
    /// 直接下发任意参数的命令（逆向调试用）。走与正式写入完全相同的守卫。
    /// <para>
    /// 命令仍是**强类型对象**（子号 + 方向 + 槽位参数），只是槽位来自 HTTP 的
    /// args 数组而不是业务常量 —— 调试口不例外，因为它同样会把字节发给 EC。
    /// </para>
    /// </summary>
    public JsonObject Raw(EcCommand cmd, double? gap = null)
    {
        var outBuf = Exchange(cmd, gap);

        // ⚠️ 全 0 buffer 不能当数据用：逆向调试拿着 DAT2@4 = 0 去下协议结论，
        //    2026-10-13 就这么误判过"APMD 变成了 0"。如实附注。
        var note = outBuf.IsAllZero
            ? "EC 返回全 0 buffer（长度异常或 EC 未就绪），resp/u32 不可信，勿据此下结论"
            : null;

        var req = new JsonArray();
        foreach (var v in cmd.Args.ToSequence()) req.Add(v);

        return new JsonObject
        {
            ["ok"] = true,
            ["sub"] = CmdTable.Hex(cmd.Sub),
            ["group"] = CmdTable.Hex(cmd.GroupFor(_ec)),
            ["req"] = req,
            ["resp"] = outBuf.Hex,
            ["u32"] = new JsonObject
            {
                ["DAT2@4"] = outBuf.U32(EcFrame.Dat2),
                ["DAT3@8"] = outBuf.U32(EcFrame.Dat3),
                ["DAT4@12"] = outBuf.U32(EcFrame.Dat4),
                ["DAT5@16"] = outBuf.U32(EcFrame.Dat5),
                ["DAT6@20"] = outBuf.U32(EcFrame.Dat6),
                // 数据区最后两个 u32（@24/@28）：本机用不到，别的模具可能把温度/转速放这里。
                // 加上它们只是让采集能看到整段数据区，不改任何既有字段。
                ["DAT7@24"] = outBuf.U32(EcFrame.Dat7),
                ["DAT8@28"] = outBuf.U32(EcFrame.Dat8),
            },
            ["note"] = note,
        };
    }

    /// <summary>
    /// 写一次背光档位并立刻读回，判断写命令是否真的落到 EC。
    /// 这是把「点了没反应」这种静默失败变成明确结论的唯一办法。
    /// </summary>
    public JsonObject SelfTestWrite(bool restore = true)
    {
        var res = new JsonObject
        {
            ["before"] = null, ["wrote"] = null, ["readback"] = null, ["effective"] = false,
        };

        int before;
        try
        {
            before = (int)ReadStatusField("backlight_level");
            res["before"] = before;
        }
        catch (Exception e)
        {
            res["error"] = $"读状态失败：{e.Message}";
            return res;
        }

        var target = before != 2 ? 2 : 1;        // 挑一个不同的合法档位
        var was = _writesEnabled;
        EnableWrites(true);
        try
        {
            res["wrote"] = target;
            SetBacklight(target);
            Thread.Sleep(800);                   // 给 EC 一点时间落地

            var readback = (int)ReadStatusField("backlight_level");
            res["readback"] = readback;
            res["effective"] = readback == target;
        }
        catch (Exception e)
        {
            res["error"] = e.Message;
        }
        finally
        {
            // 恢复必须在 finally：写测试档 / 读回任何一步抛异常时，原档位也要还回去，
            // 否则用户点一次「写入自检」键盘灯就永远停在测试档位上了。
            // ⚠️ 要在 EnableWrites(was) 之前做 —— 恢复本身也是一次 EC 写。
            if (restore)
            {
                try { SetBacklight(before); }
                catch (Exception e)
                {
                    res["restore_error"] = e.Message;
                    res["effective"] = false;
                }
            }
            EnableWrites(was);
        }

        if (!res["effective"]!.GetValue<bool>() && !res.ContainsKey("error"))
        {
            res["note"] = IsAdmin()
                ? "命令已下发但 EC 未接受。0x0100 是含 APMD/PERM/LKB* 的大分支，"
                  + "需再确认 DT3D 高半字节选择子的取值。"
                : "需要管理员权限才能写 EC。请用 run-tray.cmd 启动（会请求 UAC）。";
        }
        return res;
    }

    public void Dispose() => _io.Dispose();
}
