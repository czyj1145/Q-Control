using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.FanCurve;
using QControl.App.HotKey;
using QControl.App.Mem;
using QControl.App.Osd;
using QControl.Core.Model;
using QControl.Hal.Ec;
using QControl.Hal.HwInfo;
using QControl.Hal.Nvml;
using QControl.Hal.Sensors;
using QControl.Hal.Sys;
using QControl.Hal.Wmi;

namespace QControl.App;

/// <summary>
/// AppState 分片：**硬件档案、机器报表、重启**。
/// `/api/hardware`、`/api/device/report`、`/api/identity`、导出机器报告、延时重启，以及 Dispose。
/// </summary>
public sealed partial class AppState
{
    /// <summary>`/api/model` 的响应体。</summary>
    public JsonObject ModelView() => _model.View(Current.Summary());

    /// <summary>
    /// `GET /api/hardware`。采集要跑十几条 WMI 查询（八组并行后实测 ~120 ms），同步做会让
    /// 这个 GET 卡住；而且它开机后基本不变，采一次就够。
    ///
    /// 所以：**没采过就起后台线程采，这一帧先回 busy**，界面显示「读取中…」并轮询；
    /// 点「刷新」走 `POST /api/hardware/refresh`。
    ///
    /// ⚠️ 正常情况下这条路径**不会被用户看到 busy**：首个快照就绪后采样循环会先预采一次
    ///    （见 <see cref="WarmupHardware"/>），用户点开窗口时缓存已经在手上。
    /// </summary>
    public JsonObject HardwareInfo()
    {
        JsonObject? snap;
        lock (_hwLock) snap = _hwProfile;

        // ⚠️ 非采集期间也要带 `busy:false`：早先实现那边这个键一直存在，
        //    缺了界面拿不到"采集结束"的信号（P7 A/B 对账时抓到的字段缺口）。
        if (snap is not null && !_hwBusy)
        {
            var copy = (JsonObject)snap.DeepClone();
            copy["busy"] = false;
            return copy;
        }
        if (!_hwBusy) RefreshHardware(force: true);

        return new JsonObject
        {
            ["ok"] = true,
            ["busy"] = true,
            ["at"] = snap?["at"]?.DeepClone(),
            // ⚠️ 必须 DeepClone：这些节点已经挂在 snap 上了，
            //    直接挂进新对象会抛 "The node already has a parent"。
            ["groups"] = snap?["groups"]?.DeepClone() ?? new JsonArray(),
        };
    }

    /// <summary>
    /// 预热硬件档案：**首个快照就绪后**后台静默采一次，用户第一次点开硬件页时缓存已在手上。
    ///
    /// 程序是托盘常驻 / 开机自启，从启动到用户点开窗口通常有几十秒 —— 把这一百多毫秒
    /// 花在开机那会儿，页面就能 0 毫秒出内容。
    ///
    /// ⚠️ 必须等快照就绪再采：<see cref="HardwareProfile.Collect"/> 要读 status 里的
    ///    `power_plan` / `gpu_info` / `perf` 才能补出「电源计划 / PCIe 当前 / EC 当前状态」
    ///    三行。拿 null 采下来的档案会被缓存到进程结束 —— 采集是一次性的，不会自愈，
    ///    这三行就永远缺着。
    /// ⚠️ 幂等：已有结果 / 正在采 / 还没有快照，三种情况都直接返回。
    /// </summary>
    public void WarmupHardware()
    {
        if (_hwProfile is not null || _hwBusy || !HasCachedStatus) return;
        RefreshHardware();
    }

    /// <summary>后台重采一次硬件档案。已在采 / 已有结果且没 force 就返回 false。</summary>
    public bool RefreshHardware(bool force = false)
    {
        lock (_hwLock)
        {
            if (_hwBusy) return false;
            if (_hwProfile is not null && !force) return false;
            _hwBusy = true;
        }

        // ⚠️ 要**全量**那一帧：硬件档案会拿快照里的字段做兜底，精简档缺段会被当成"读不到"
        //    记进档案（而档案是开机采一次就缓存的东西，错了会一直错）。
        //    还没有任何帧时照旧传 null，别在这里为了采档案去读 EC。
        var status = HasCachedStatus ? CachedFullStatus() : null;
        var t = new Thread(() =>
        {
            try
            {
                var snap = HardwareProfile.Collect(_model, status);
                lock (_hwLock) _hwProfile = snap;
            }
            catch { /* 采集失败：保留旧结果，下轮再试 */ }
            finally { lock (_hwLock) _hwBusy = false; }
        })
        { IsBackground = true, Name = "hwprofile" };
        t.Start();
        return true;
    }

    /// <summary>
    /// `GET /api/device`：完整机型报告。
    /// 身份只采一次就缓存 —— `identify()` 在早先实现也是进程级缓存，
    /// 而且它读注册表（十来次 RegOpenKey）没必要每次请求都跑。
    /// </summary>
    public JsonObject DeviceReport()
    {
        var dev = Current;
        var ident = Identity();

        // identity 那一段不是把 ident 原样丢给界面：`vendor_registry` 内部是
        // {项目号: bool}、`wmi_classes` 也是，界面要的是"命中了哪些"的列表。
        var reg = new JsonArray();
        if (ident["vendor_registry"] is JsonObject vr)
            foreach (var (k, v) in vr)
                if (v?.GetValue<bool>() == true) reg.Add(k);

        var wmi = new JsonObject();
        if (ident["wmi_classes"] is JsonObject wc)
            foreach (var (k, v) in wc)
                wmi[k] = v?.GetValue<bool>() == true;

        var identity = new JsonObject
        {
            ["system_vendor"] = ident["system_vendor"]?.DeepClone(),
            ["system_product"] = ident["system_product"]?.DeepClone(),
            ["system_family"] = ident["system_family"]?.DeepClone(),
            ["board_vendor"] = ident["board_vendor"]?.DeepClone(),
            ["board_product"] = ident["board_product"]?.DeepClone(),
            ["bios_vendor"] = ident["bios_vendor"]?.DeepClone(),
            ["bios_version"] = ident["bios_version"]?.DeepClone(),
            ["acpi_pnp0c14"] = ident["acpi_pnp0c14"]?.DeepClone(),
            ["wmi_classes"] = wmi,
            ["vendor_registry"] = reg,
        };

        var profiles = new JsonArray();
        foreach (var p in DeviceResolver.AllProfiles)
            // DeviceProfile 没有强类型 Name/Verified —— 从 Raw 里取。
            // （它只强类型化了匹配要用的字段，其它保持原样透传。）
            profiles.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Raw["name"]?.GetValue<string>() ?? p.Id,
                ["verified"] = p.Raw["verified"]?.GetValue<bool>() ?? false,
                ["priority"] = p.Priority,
            });

        var outObj = new JsonObject
        {
            ["identity"] = identity,
            ["device"] = dev.View(),
            ["profiles"] = profiles,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };

        // 写不了的时候**必须说明为什么**，否则界面只能干瞪眼。
        if (!dev.WritesAllowed)
        {
            outObj["warning"] = !dev.EcCapable
                ? "本机没有可用的 EC 命令表 —— 控制中心不会向 EC 写入任何东西。"
                : dev.ProfileWrites
                    ? "EC 写入已在设置页「EC 控制」里关闭 —— 只做只读展示。"
                    : $"本机未通过机型验证（识别为：{dev.Name}）—— EC 写入默认关闭，" +
                      "界面只做只读展示；确认风险后可在设置页「EC 控制」里手动打开。";
        }

        return outObj;
    }

    // ================================================================ 机型适配报告

    /// <summary>程序版本（「关于」页与适配报告同一来源；SourceLink 的 "+abc1234" 后缀去掉）。</summary>
    public static string AppVersion { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(AppState).Assembly;
        var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString()
                  ?? "未标注";
        var plus = ver.IndexOf('+');
        return plus > 0 ? ver[..plus] : ver;
    }

    /// <summary>
    /// 身份指纹（注册表 + SMBIOS + 可选 `root\WMI` 类探测）。
    ///
    /// 进程内缓存：读十来次注册表，没必要每次请求都跑。`deep: true` 表示"连 WMI 兼容类也探"
    /// （100~300 ms）—— 只有适配报告需要它，别在启动路径上用。
    ///
    /// ⚠️ **出口必须 `DeepClone()`**（2026-11-08 真机踩到）：缓存里的 `JsonObject` 只要被
    ///    调用方塞进另一个 `JsonObject`（报告就是 `report["identity"] = …`），它就**有父亲了**；
    ///    下一次再往别的对象里塞，会直接抛
    ///    `InvalidOperationException: The node already has a parent`。
    ///    症状极具误导性：**第二次**导出机型报告必然是 500，第一次好好的。
    ///    所以规矩是「缓存的节点永远不许出缓存」，不是在这儿补一次克隆。
    /// </summary>
    public JsonObject Identity(bool deep = false)
    {
        if (_ident is { } cached && (!deep || (cached["wmi_classes"] as JsonObject)?.Count > 0))
            return (JsonObject)cached.DeepClone();

        // ⚠️ 新采出来的这份**同样**要克隆再交出去。旧写法 `return _ident = Collect()`
        //    让缓存与调用方拿着同一个节点：调用方一把它塞进 report，缓存里的 _ident
        //    就跟着有了父亲，之后被改、或被挂到别处都会污染缓存。规矩是
        //    「缓存的节点永远不许出缓存」，命中分支和新建分支一个都不能漏。
        _ident = MachineIdent.Collect(deep);
        return (JsonObject)_ident.DeepClone();
    }

    /// <summary>
    /// `POST /api/app/machine-report`：采一份「机型适配报告」（见 <see cref="MachineReport"/>）。
    ///
    /// <paramref name="save"/> = true 落盘成 `logs/adopt-*.json` + `.txt`；false 只在响应里
    /// 带回全文（界面「复制到剪贴板」用，不产生垃圾文件）。
    ///
    /// ⚠️ **慢**（注册表盲扫 + 几条 EC 往返 + 硬件档案 ≈ 1~2 s），调用方必须放后台线程。
    /// ⚠️ 采集全程只读：不写 EC、不改配置。哪段采不到会写进 `text` 的「采集说明」，不编数据。
    /// </summary>
    public JsonObject ExportMachineReport(bool save)
    {
        var report = MachineReport.Collect(this, AppVersion);
        var text = MachineReport.ToText(report);

        var outObj = new JsonObject { ["ok"] = true, ["text"] = text };

        if (save)
        {
            try
            {
                var files = MachineReport.Save(LogDirectory, report, text);
                outObj["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
                outObj["file"] = Path.GetFileName(files[0]);
                outObj["dir"] = LogDirectory;
            }
            catch (Exception e)
            {
                outObj["ok"] = false;
                outObj["error"] = "报告写不进 logs 目录：" + e.Message;
            }
        }
        return outObj;
    }

    // ================================================================ 分段

    /// <summary>
    /// `POST /api/app/machine-report` 的 `adopt:true`：**一键机型档案采集**
    /// （见 <see cref="MachineAdopt"/> 与 <see cref="MachineReport"/>）。
    ///
    /// 一次点击做完四件事：多帧采样（约 20 秒）、自动标定 layout 偏移、
    /// **验证性能档与键盘背光的写入**（约 5 秒，写前读原值、写完恢复）、
    /// 把 `devices/&lt;id&gt;.json` 写进本机用户档案目录（与 model.json 同级）。
    ///
    /// <paramref name="probe"/> = false 就是"只读采集"：一个字节都不往 EC 写，
    /// 包里的写入证据那段如实写"没验"（用户只想要读数时用这个）。
    ///
    /// ⚠️ **更慢**（30 秒上下），必须放后台线程。
    /// ⚠️ 写完**不会立刻生效**：Device 已经跟着 EcHal / 风扇引擎一路造下去了，
    ///    换档案要重建整条链。所以结果里 `needs_restart=true`，界面必须如实说"重启后生效"。
    /// </summary>
    public JsonObject ExportMachineProfile(int seconds, bool probe = false, int observeSeconds = 0)
    {
        // 引导小窗**从这一刻**就叫起来（2026-11-14）：点下按钮后还有 30 多秒在读机型 /
        // 采样 16 帧 / 读显卡模式，这段旧实现里什么都没有 —— 用户以为程序还没开始，
        // 去原厂里翻别的页面，等小窗冒出来时人已经不在状态了（真机复现：他把"打开原厂
        // 控制中心"当成唯一要做的动作，剩下 150 秒一步没点，到点按没做到收尾）。
        // Warmup 段只显示进度、不接受标记，观察真开始时由 Guide.Start 接过去。
        var withGuide = observeSeconds > 0;
        if (withGuide) Guide.BeginWarmup();

        try
        {
            return MachineAdopt.Collect(this, AppVersion, seconds, probe, observeSeconds,
                                        stage =>
                                        {
                                            _adoptStage = stage;
                                            Guide.NoteStage(stage);
                                        },
                                        step => _adoptStep = step);
        }
        finally
        {
            _adoptStage = null;         // 采完/失败都清空：界面靠"null = 没在采"收尾
            _adoptStep = 0;
            Guide.End();                // 失败在半路时别把小窗钉在屏幕上（End 幂等）
        }
    }

    /// <summary>
    /// 采集进行到**第几段**（1..<see cref="MachineAdopt.StepNames"/>.Length，0 = 没在采）。
    /// 界面据此画分段进度 —— 段是后端真实的段边界，不是把总时长平均切开凑的百分比。
    /// </summary>
    public int AdoptStep => _adoptStep;

    private volatile int _adoptStep;

    /// <summary>
    /// 采集进行到哪一步（界面直接读，不走 HTTP —— 本来就在同一个进程里）。
    /// null = 没在采集。**只写阶段名，不写百分比**：那些数字编不出来，编出来就是骗人。
    /// </summary>
    public string? AdoptStage => _adoptStage;

    private volatile string? _adoptStage;

    /// <summary>
    /// 一键采集是不是正在跑。**整条链是单例状态**（Guide 会话、写窗口、风扇曲线起停、
    /// 负载线程都是进程级的），两轮重叠会互相踩：先结束的那轮 `Guide.End()` 会把还在观察的
    /// 引导窗关掉、`_adoptStage` 被清成"没在采"，两轮写验证还会互相读改写 APMD。
    ///
    /// 入口用 <see cref="BeginAdopt"/> 抢（原子），风扇卡死自救也读它 —— 采集自己会
    /// `Stop(release:false)` 把转速钉住，那段时间判"卡死"必然是误报（2026-11-23 审查）。
    /// </summary>
    public bool Adopting => Volatile.Read(ref _adoptBusy) != 0;

    /// <summary>抢采集权；已经有人在采就返回 false。</summary>
    public bool BeginAdopt() => Interlocked.CompareExchange(ref _adoptBusy, 1, 0) == 0;

    public void EndAdopt() => Interlocked.Exchange(ref _adoptBusy, 0);

    private int _adoptBusy;

    /// <summary>
    /// 「观察原厂」阶段的提前结束信号（界面「完成」按钮置位）。
    /// 只在观察窗口里被轮询，观察结束由 <see cref="MachineObserve"/> 自动复位。
    /// </summary>
    public bool ObserveStopRequested => _observeStopRequested;

    private volatile bool _observeStopRequested;

    /// <summary>请求提前结束当前观察窗口（幂等，下次采集开始时由 <see cref="MachineObserve"/> 复位）。</summary>
    public void RequestObserveStop() => _observeStopRequested = true;

    /// <summary>
    /// 「再等 60 秒」：把观察窗口的截止时刻往后推（引导小窗上那颗按钮）。
    ///
    /// 为什么要有它：观察是**固定窗口**，到点就按"没走到这一步"收尾（150 秒是
    /// `observe` 参数，不是"等你做完"）。用户正在原厂里找那个开关、或者临时接了个电话，
    /// 到点被收掉只能整轮重采两分半 —— 给一次明确的延长比让他重来便宜得多。
    /// 只在观察循环里被取用（取一次清零），采集结束后残留的请求不会漏到下一轮。
    /// </summary>
    public void RequestObserveExtend(int seconds) => _observeExtend = Math.Clamp(seconds, 10, 300);

    /// <summary>观察循环每轮来取一次延长请求；没请求返回 0（取完即清零，不会重复延长）。</summary>
    public int TakeObserveExtend() => Interlocked.Exchange(ref _observeExtend, 0);

    private int _observeExtend;

    /// <summary>采集开始时复位观察结束信号（供 <see cref="MachineObserve"/> 调用）。</summary>
    public void ResetObserveStop()
    {
        _observeStopRequested = false;
        // 延长请求也一起清：上一轮尾声点的「再等 60 秒」不该把下一轮的窗口撑长。
        Interlocked.Exchange(ref _observeExtend, 0);
    }

    /// <summary>「观察原厂」是否正在进行（界面据此显示「完成观察」按钮）。</summary>
    public bool Observing => _observing;

    private volatile bool _observing;

    /// <summary>置位/清位观察进行标志（供 <see cref="MachineObserve"/> 调用）。</summary>
    public void SetObserving(bool value) => _observing = value;

    /// <summary>
    /// 「引导适配」的会话（步骤清单 + 用户标记）。界面渲染、观察线程计数、采集收尾取标记，
    /// 三个方向都读同一个对象 —— 状态只有一份，不存在"界面停在第 2 步、后端按第 3 步切窗"。
    /// </summary>
    internal GuideSession Guide { get; } = new();

    // ================================================================ 重启计划
    //
    // 为什么走系统 `shutdown.exe` 而不是自己调 ExitWindowsEx：
    //   · 计划交给系统后，本进程被关掉/崩了都不影响倒计时（关了界面照样重启）；
    //   · `shutdown /a` 是现成的取消通道，不用自己维护定时器线程；
    //   · 有程序阻止关机（未保存的文档）时系统会自己中止，比我们强杀安全得多。
    // ⚠️ **不写 /f**（强制）：强制关掉 Word/编辑器会丢未保存内容，得不偿失。

    private readonly object _rebootLock = new();
    private (double At, int Seconds, string Reason, string? Mode)? _reboot;

    private static string ShutdownExe()
    {
        var root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C://Windows";
        return Path.Combine(root, "System32", "shutdown.exe");
    }

    /// <summary>当前是否有计划中的重启 + 剩余秒数（到点后自动清掉本地记录）。</summary>
    public JsonObject RebootState()
    {
        lock (_rebootLock)
        {
            if (_reboot is not { } r)
                return new JsonObject { ["ok"] = true, ["planned"] = false, ["remain"] = 0 };

            var remain = (int)Math.Round(r.At + r.Seconds - UnixNow());
            if (remain <= 0)
            {
                _reboot = null;
                return new JsonObject { ["ok"] = true, ["planned"] = false, ["remain"] = 0 };
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["planned"] = true,
                ["remain"] = remain,
                ["seconds"] = r.Seconds,
                ["reason"] = r.Reason,
                ["mode"] = r.Mode,
            };
        }
    }

    private static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// `shutdown.exe` 的输出只取第一行非空文本。
    ///
    /// 它的原始输出末尾跟着换行和控制字节，按系统代码页解码会在尾巴上粘出一个
    /// 乱码汉字（实测 `...（1116）\n浰`）。note / error 是给人看的，
    /// 留一行干净的话就够，剩下的控制字符一律不要。
    /// </summary>
    private static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Replace("\r", "").Trim('\0', ' ', '\t');
            if (line.Length > 0) return line;
        }
        return "";
    }

    /// <summary>
    /// `seconds = 0` 表示**立刻**重启：`shutdown /r /t 0` 不弹任何系统对话框。
    /// ⚠️ `seconds &gt; 0` 时系统自己会弹一个倒计时对话框（样式不归我们管、只能点"关闭"），
    ///    所以界面切显卡模式只用 0 或干脆不计划（2026-11-07 用户报障）。上限 24 小时。
    /// </summary>
    public JsonObject PlanReboot(int seconds, string reason, string? mode)
    {
        // 越界一律拒绝，**不再 clamp**：`Clamp` 会把"传错了"悄悄变成另一个合法值
        // （1e10 → int.MinValue → 0 → 当场重启，2026-11-23 审查发现）。
        // 入口（PostHandlers.Reboot）已经拦过一次，这里是给其它调用方的第二道。
        if (seconds is < 0 or > 86400)
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"seconds 越界（{seconds}）：只接受 0~86400 的秒数，0 = 立刻重启",
            };

        var s = seconds;
        var args = $"/r /t {s}";

        if (!string.IsNullOrWhiteSpace(reason))
        {
            // /c 的注释上限 512 字符。去掉内层引号：这段要嵌进命令行，
            // 引号没处理会让后面的参数被当成注释的一部分。
            var safe = reason.Replace("\"", "").Trim();
            if (safe.Length > 200) safe = safe[..200];
            args += $" /c \"{safe}\"";
        }

        var (code, stdout, stderr) = Shell.Run(ShutdownExe(), args, 10_000);
        if (code is null)
            return new JsonObject { ["ok"] = false, ["error"] = "调用 shutdown 失败：" + FirstLine(stderr) };
        if (code != 0)
        {
            var msg = FirstLine(stderr + stdout);
            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"shutdown 返回 {code}：{(msg.Length > 0 ? msg : "未知原因")}",
            };
        }

        lock (_rebootLock)
            _reboot = (UnixNow(), s, reason ?? "", mode);

        var r = RebootState();
        // s = 0 是"立刻重启"：系统那边没有倒计时可取消，别谎报 planned
        if (s > 0) r["planned"] = true;
        return r;
    }

    /// <summary>
    /// 取消计划重启。
    ///
    /// ⚠️ **1116 = 「没有正在进行的关机操作」，这不是错误** —— 本地记录照样清掉，
    ///    只把系统原话放进 note 供排查。
    /// ⚠️ 其它非 0（5 拒绝访问 / 1190 已有其它关机进程 / null = shutdown.exe 没跑起来）
    ///    说明**系统那边可能还在倒计时**：必须返回失败并**保留**记录，
    ///    否则界面弹「已取消重启」、倒计时条消失，机器到点照样重启（2026-10-17 修）。
    /// </summary>
    public JsonObject CancelReboot()
    {
        var (code, stdout, stderr) = Shell.Run(ShutdownExe(), "/a", 10_000);
        var msg = FirstLine(stderr + stdout);

        if (code is 0 or 1116)
        {
            lock (_rebootLock) _reboot = null;
            var o = new JsonObject { ["ok"] = true, ["planned"] = false, ["remain"] = 0 };
            if (code == 1116)
                o["note"] = msg.Length > 0 ? msg : "没有正在进行的关机";
            return o;
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["planned"] = true,
            ["remain"] = RebootState()["remain"]?.DeepClone(),
            ["error"] = $"取消失败（shutdown /a 返回 {code?.ToString() ?? "null"}）："
                        + (msg.Length > 0 ? msg : "未知原因"),
        };
    }

    /// <summary>
    /// 原厂软件进程名列表（空 = 没跑）。只检测告知，**不自动杀**。
    ///
    /// ⚠️ **不再用 `SELECT Name FROM Win32_Process`**（2026-11-02 换掉）：那条 WQL
    ///    每跑一次就要把系统里**全部**进程（本机 300+ 个）各封一个 COM 对象出来，
    ///    而它每 10 拍（≈10~19 秒）就跑一次 ⇒ 每秒 20~30 个原生对象挂在终结器队列上，
    ///    托盘常驻时工作集从 30 MB 爬回 60 MB、句柄在 745~1050 之间锯齿，主因就是它。
    ///    现在只按**已知的 5 个名字**查（名单本来就在 <see cref="VendorProcs"/> 里），
    ///    走系统进程快照，一个 COM 对象都不建，语义完全一样。
    /// </summary>
    /// <summary>
    /// 原厂控制中心是否在运行（任一进程）。「观察原厂」开始前用它判断值不值得等 60 秒：
    /// 原厂全没跑就一条写命令都抓不到，让用户干等是最差体验。
    /// </summary>
    public bool VendorAlive => VendorRunning().Count > 0;

    /// <summary>
    /// 原厂**主界面**在不在（ControlCenter.exe）。只剩后台 daemon 时用户没有可点的界面，
    /// 提示要改成「按 FN+3」而不是「去原厂里点」——那是两个完全不同的动作。
    /// </summary>
    public bool VendorHasUi
        => VendorRunning().Any(n => n is JsonValue v
                                    && v.TryGetValue<string>(out var s)
                                    && s.Equals("ControlCenter.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 原厂此刻**有没有可点的界面窗口** —— 「一键采集」开始前的门槛用它（见 `Ui.VendorPrompt`）。
    ///
    /// 为什么要比 <see cref="VendorAlive"/> 更细：进程在跑、窗口关到托盘里，是采集最常见的
    /// 翻车场景 —— 用户以为"原厂开着"，引导小窗却让他去原厂里切档，他找不到界面，
    /// 150 秒过去一条写命令都没抓到。三种状态要三套话，绝不能用一句"请打开原厂"糊过去。
    ///
    /// ⚠️ 只看 ControlCenter.exe 的顶层主窗口：daemon / OSD 服务那几只没有界面，
    ///    它们的窗口（消息窗 / 托盘提示）会被误当成"界面开着"，那就等于又骗了用户一次。
    /// </summary>
    public VendorUiState VendorUi()
    {
        var any = false;
        foreach (var exe in VendorProcs)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            }
            catch
            {
                continue;      // 单个名字查不到（进程刚退 / 权限）：跳过，别阻塞门槛判断
            }

            try
            {
                if (procs.Length == 0) continue;
                any = true;
                if (!exe.Equals("controlcenter.exe", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var p in procs)
                    if (MainWindowUsable(p)) return VendorUiState.WindowReady;
            }
            finally
            {
                foreach (var p in procs) p.Dispose();   // ⚠️ 每只都要放手：它自带进程句柄
            }
        }
        return any ? VendorUiState.NoWindow : VendorUiState.NotRunning;
    }

    /// <summary>
    /// 这个进程有没有**摆在屏幕上的**主窗口：可见 + 没最小化。
    /// 最小化也算"没打开界面"——用户看不见它就没法在里面切档，而 `IsWindowVisible`
    /// 对最小化的窗口照样回 true，所以必须再问一次 `IsIconic`。
    ///
    /// ⚠️ 先问 `MainWindowHandle`，回 0 再按 PID 枚举一遍顶层窗口：那个属性只认
    ///    "没有 owner、带标题的顶层窗口"，原厂某些版本主窗口被挂住 / 标题为空时会回 0。
    ///    只信它的话，用户明明开着界面却被告知"请打开界面"——这种误判最招骂。
    /// </summary>
    private static bool MainWindowUsable(Process p)
    {
        try
        {
            var hwnd = p.MainWindowHandle;
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsIconic(hwnd)) return true;

            var pid = (uint)p.Id;
            var found = false;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var owner);
                if (owner != pid) return true;
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                // 工具窗口（托盘提示 / 消息窗）不算界面 —— 它们也"可见"，但不是能点的那块
                if ((GetWindowLong(h, GwlExStyle) & WsExToolWindow) != 0) return true;
                found = true;
                return false;      // 找到一个就够了，别再往下枚举
            }, IntPtr.Zero);
            return found;
        }
        catch
        {
            return false;
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    private static JsonArray VendorRunning()
    {
        var arr = new JsonArray();
        foreach (var exe in VendorProcs)
        {
            try
            {
                var procs = System.Diagnostics.Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(exe));
                if (procs.Length == 0) continue;
                // 报**真实大小写**的名字（界面直接显示它），名单里那份是全小写的
                var name = procs[0].ProcessName + ".exe";
                foreach (var p in procs) p.Dispose();      // ⚠️ 每只都要放手：它自带进程句柄
                arr.Add(name);
            }
            catch
            {
                // 单个名字查不到（进程刚退 / 权限）：跳过，不阻塞快照
            }
        }
        return arr;
    }

    /// <summary>
    /// 「那六项 HWiNFO 专属读数**这一拍有没有**」—— 没有就给界面一句话；有就 null。
    ///
    /// 判据是**读数本身**，不是"进程在不在 / 段能不能打开"（2026-11-22 改）：
    /// 早先那两条都会漏报，而漏报的表现就是用户说的「检测到 HWiNFO，但没读数，又不提示」：
    ///   · 只判"进程在跑" ⇒ 进程压根没起来（拉起被退避挡住、按需模式还没拉、用户自己那份
    ///     被杀掉）时永远不吭声，界面上只是那六行**静悄悄空着**；
    ///   · 只判"段能读到新鲜帧" ⇒ 帧在、标签却一个都对不上（布局变了 / 另一份实例没开
    ///     导出）时也是静悄悄。
    /// 所以改成：六项里**任意一项**读到值就算它干活了；一项都没有才准备报。
    ///
    /// `kind` 是给界面分文案用的：
    ///   · `Notfound`   —— 连 `HWiNFO64.exe` 都没有（下载 / 重新检测那条）；
    ///   · `Nodata`     —— 进程在跑，但一项都没读到（多半是没开「共享内存导出」/ 12 小时到期）；
    ///   · `NotRunning` —— 进程没在跑，而 exe 是在的（所以界面仍要说，不能说"未找到"）。
    /// </summary>
    private JsonObject? HwinfoBlock(bool hwAny)
    {
        // 整帧断档（窗口关着 / 没人看）⇒「这一轮多久没读到数」重新计时。
        // 不重置的话：关窗五分钟再打开，会立刻弹一行"没有数据"（其实它刚被拉起来，还没到）。
        var now = Environment.TickCount64;
        if (now - _hwFullAt > 5000) _hwNoDataSince = 0;
        _hwFullAt = now;

        if (hwAny)                                    // 有读数 ⇒ 它在干活，什么都不用说
        {
            _hwNoDataSince = 0;
            return NotBlocked();
        }
        // 用户关掉了这个数据源 ⇒ 别贴告示，但也别声称"恢复"
        if (HwinfoMode() == "off")
        {
            _hwNoDataSince = 0;
            return NotBlocked(quiet: true);
        }

        // 连 exe 都没有 ⇒ 只有「未找到」那条说得清（下载 / 重新检测）。这里必须照实回一个
        // kind，**不能返回 null**（2026-11-22 19:38 修）：界面那头是拿"没有读数"当门去看
        // 有没有 exe 的，这里一沉默，那行「HWiNFO 未找到」就永远不出现 ——
        // 表现是"程序连有没有 HWiNFO 都测不出来了"。这一态不用等（exe 不在，等多久都不出数）。
        if (!HwDirFound())
        {
            _hwNoDataSince = 0;
            return Blocked("notfound", false,
                "没找到 HWiNFO64.exe（程序目录下的 HWiNFO 文件夹里没有它）", null, null);
        }

        // ⚠️ 静默期只用**一条单调的时钟**：「这一轮连续多久没读到数」（2026-11-22 22:52 重做）。
        //    以前是 `InLaunchWarmup()` 的 25 秒静默 —— 而**每次退避重试都会重新武装它**，
        //    于是"拉起失败 → 25 秒静默 → 6 秒计时 → 界面 3 拍确认"这条链永远攒不满：
        //    探针每 40~54 秒重试一次，界面刚数到第 2 拍又被静默清零。实测（22:45）就是
        //    后端 22:45:22 已经判出"没找到"，用户 22:45:25 打开设置页却什么都看不到，
        //    一直等到 22:46:39 它自己拉起来为止。
        //    重试**不该**把"没数"这段时间洗掉 —— 它恰恰是问题本身。
        //    12 秒盖得住一次正常拉起（实测 3.6~7.4 秒），也短到用户会注意到。
        if (_hwNoDataSince == 0) _hwNoDataSince = now;
        if (now - _hwNoDataSince < 12000) return NotBlocked(quiet: true);

        if (HwInfoProcess.IsRunning())
        {
            var reason = "HWiNFO 在运行，但六项读数一项都没读到"
                         + "（多半是另一份实例没开「共享内存导出」，或免费版 12 小时到期）";
            return Blocked("nodata", true, reason, null, null);
        }

        // 拉起被退避挡住时，光说"没在跑"没用 —— 用户需要知道"为什么"和"什么时候会自己再试"
        var (left, why) = HwInfoProcess.LaunchBlock;
        var wait = left > 0
            ? $"上次没起来（{why}），约 {(left < 60 ? $"{left:0} 秒" : $"{left / 60:0} 分钟")}后才会自己再试"
            : null;
        var why2 = why is null
            ? "HWiNFO 没在运行（exe 找到了，进程没起来）"
            : $"HWiNFO 没在运行：{wait}";
        return Blocked("notrunning", false, why2, left, why);
    }

    private JsonObject Blocked(string kind, bool running, string reason, double? left, string? why)
    {
        // 进出这一态各留一行日志（设置页那行提示只在"真的没数"时才出现，
        // 事后凭日志能对上时间线：什么时候开始的、为什么、有没有自己恢复）。
        NoteBlockLogged(kind, reason);
        return new JsonObject
        {
            ["kind"] = kind,
            ["running"] = running,
            ["reason"] = reason,
            ["blocked_for"] = left is > 0 ? Math.Round(left.Value) : null,
            ["block_reason"] = why,
            // 「这一轮已经多久没读到数了」—— 用户对时间线时最常问的就是这个（秒）
            ["no_data_for"] = _hwNoDataSince == 0
                ? null
                : Math.Round((Environment.TickCount64 - _hwNoDataSince) / 1000.0),
        };
    }

    /// <summary>
    /// 不报这条提示。<paramref name="quiet"/> = 只是"这一拍先别说话"（还没满 12 秒静默期 /
    /// 数据源关了），**不算恢复** —— 不能打"读数已恢复"，那是在撒谎（2026-11-22 22:40 改：
    /// 20:33 那条"恢复"其实是静默期挡掉的，下一拍又报了"没找到"）。
    /// ⚠️ 这里**绝不碰 `_hwNoDataSince`**：静默期不能因为"又重试了一次"就重新计时，
    ///    那正是 22:45 那版看不到提示的原因（见 HwinfoBlock 里那段说明）。
    /// </summary>
    private JsonObject? NotBlocked(bool quiet = false)
    {
        if (_hwBlockLogged is not null)
        {
            if (!quiet) Console.WriteLine("[hwinfo] 读数已恢复（之前那条提示消失）");
            _hwBlockLogged = null;
        }
        return null;
    }

    private string? _hwBlockLogged;
    /// <summary>「这一轮连续多久没读到那六项」的起点（TickCount64，0 = 还没开始计）。</summary>
    private long _hwNoDataSince;
    /// <summary>上一次整帧的时刻 —— 断档超过 5 秒就说明刚才没人在看，计时重来。</summary>
    private long _hwFullAt;

    private void NoteBlockLogged(string kind, string reason)
    {
        if (_hwBlockLogged == kind) return;
        _hwBlockLogged = kind;
        Console.WriteLine(kind == "notrunning"
            ? "[hwinfo] 提示：没在运行 —— " + reason
            : "[hwinfo] 提示：" + reason);
    }

    /// <summary>
    /// 本机有没有 `HWiNFO64.exe`（`HwInfoProcess.Dir()`，**带 5 秒缓存**）。
    ///
    /// 为什么要缓存：探测要从 exe 目录往上翻最多 10 层、每层试两个路径，快照每秒一次，
    /// 而这件事一分钟内不会变 —— 每次都扫盘纯属浪费。只在"没在跑"的时候才会问。
    /// </summary>
    private bool HwDirFound()
    {
        var now = Environment.TickCount64;
        if (_hwDirFound is null || now - _hwDirAt > 5000)
        {
            try { _hwDirFound = HwInfoProcess.Dir() is not null; }
            catch { _hwDirFound = false; }
            _hwDirAt = now;
        }
        return _hwDirFound == true;
    }

    private bool? _hwDirFound;
    private long _hwDirAt;

    /// <summary>
    /// 「重新检测」时把上面那份目录缓存作废 —— 否则用户刚把 exe 放好、点了按钮，
    /// 界面还得再等最多 5 秒才认（toast 说"已找到"而这一行还写着"未找到"）。
    /// </summary>
    internal void InvalidateHwinfoDirCache()
    {
        _hwDirFound = null;
        _hwDirAt = 0;
    }

    public void Dispose()
    {
        _running = false;
        try { _sampler?.Interrupt(); } catch { /* 已退出 */ }
        try { _sampler?.Join(1500); } catch { /* 不阻塞退出 */ }
        // 先收热键/OSD：监听线程会读 EC，必须在 EC 之前停
        try { _hotkey.Dispose(); } catch { /* 退出路径不抛 */ }
        // 卸键盘钩子（纯 Win32，不碰 EC）：留着它退出后进程一小段时间里 Win 键还是死的
        try { _winLock.Dispose(); } catch { /* 退出路径不抛 */ }
        // ⚠️ EC 方案的锁**不会**随进程消失（那正是它与钩子的区别）⇒ 退出时主动解锁。
        //    顺序必须在 `_ec.Dispose()` 之前，否则这条 SMI 发不出去。
        //    理由：这是个会吃掉键盘按键的功能，关掉控制中心还留一个没人知道的死 Win 键，
        //    比它带来的便利糟得多。
        if (_winEcLocked)
        {
            try { _ec.SetWinKeyLock(locked: false); }
            catch (Exception e) { Console.Error.WriteLine($"[winkey] 退出时解锁失败：{e.Message}"); }
        }
        _fan.Dispose();
        _sensors.Dispose();
        _ec.Dispose();
        // 收掉**我们自己拉起的**那个 HWiNFO（用户手动开的那份不碰）。
        // 口径是只停我们自己拉起的那个：
        // 否则点完「修复」再退控制中心，那个实例会一直留在后台占着共享内存。
        try { HwInfoProcess.StopOurs(); } catch { /* 退出路径不抛 */ }
        // 历史档收句柄：最后一行已经 flush 过，这里只是把文件关干净（下次启动追加）
        try { HistoryStore.Close(); } catch { /* 退出路径不抛 */ }
    }
}

/// <summary>
/// 原厂软件此刻的界面状态（「一键采集」门槛用，见 <see cref="AppState.VendorUi"/>）。
///
/// 三态而不是一个 bool：进程在跑 ≠ 界面开着 —— 采集的引导那一段要求用户**在原厂界面里**
/// 点，只跑后台 daemon 时得让用户去打开界面，一句话说不完。
/// </summary>
public enum VendorUiState
{
    /// <summary>没拿到程序状态（例如 --ui-shot 那条无后端的路径）⇒ 不拦，照原样采集。</summary>
    Unknown = 0,

    /// <summary>一个原厂进程都没有：引导抓不到任何写命令。</summary>
    NotRunning,

    /// <summary>进程在跑（多半是 ControlCenterDaemon），但找不到摆在屏幕上的主窗口。</summary>
    NoWindow,

    /// <summary>主界面窗口开着（可见、没最小化）：可以照引导在原厂里操作。</summary>
    WindowReady,
}
