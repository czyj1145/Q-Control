using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using QControl.Hal.Nvml;
using QControl.Hal.Pdh;

namespace QControl.Hal.Sensors;

/// <summary>
/// GPU 读数。**三条路，按顺序降级**（2026-11-03 起用 NVML，11-07 补第三级）：
/// <list type="number">
/// <item><see cref="NvmlSensors"/> —— 进程内 NVML，微秒级，常驻后不再起进程；</item>
/// <item>`nvidia-smi` —— 兜底（没装驱动 / 老驱动缺函数 / Optimus 把 dGPU 关掉时）。
///   它是**每秒一个子进程**、实测 53 ms 墙钟，所以只当后备，不再当主路。</item>
/// <item>**核显** —— 前两级都确认"这台机器现在没有可用的 NVIDIA 显卡"时（集显输出模式），
///   改报核显的型号 + 占用率。只有这两项有原生源，见 <see cref="ReadIntegrated"/>。</item>
/// </list>
///
/// 前两条路给出的 `/api/status` 形状**逐字一致**：键名、单位（°C / % / W / MHz / MiB）、
/// "取不到给 null 不给 0" 的规矩都一样 —— 上层与界面完全不需要知道走的是哪条。
/// 第三条路只有 `name` / `usage` 两个键，其余整键不出现（界面按行隐藏）。
///
/// ⚠️ **smi 的输出要先验形，再当数据**（2026-11-07 用户报障"切集显后显卡卡报错"）：
///    切到集显输出后驱动与 dGPU 不再通信，`nvidia-smi` 会把
///    `NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver…`
///    打在 **stdout** 上。旧实现只判"这行是不是空"，于是整句英文被当作显卡名送进
///    `/api/status`，总览页名称标签把这段报错原样显示出来 —— 用户看到的就是"显卡卡报错"。
///    现在两重校验：先认失败字样，再要求一行必须是 name + 6 项读数（7 段）。
/// ⚠️ 明确"没有 NVIDIA 显卡"之后要**冷却**：否则集显模式下每一拍都起一次子进程
///    （最坏时 `nvidia-smi` 在等驱动超时要挂 6 秒），采样线程会被拖死。
/// </summary>
public sealed class GpuSensors : IDisposable
{
    private const string Query =
        "name,temperature.gpu,utilization.gpu,power.draw,clocks.current.graphics,memory.used,memory.total";

    /// <summary>一行 csv 的段数：`name` + 6 项读数。结构校验的唯一真源。</summary>
    private const int CsvFields = 7;

    /// <summary>明确"这台机器没有可用的 NVIDIA 显卡"（切集显输出 / 驱动不在）后，多久再问一次。</summary>
    private const double NoGpuRetrySeconds = 30.0;

    /// <summary>这一拍只是没读到（子进程超时 / 空输出），下次再试的间隔。</summary>
    private const double EmptyRetrySeconds = 5.0;

    private const string UnknownName = "Unknown GPU";

    /// <summary>冷却到期时间；在此之前不再起 `nvidia-smi`。UTC。</summary>
    private DateTime _smiRetryAt = DateTime.MinValue;

    /// <summary>上一次记过的不可用原因（同一原因不重复刷日志）。</summary>
    private string? _lastWhy;

    /// <summary>核显占用率（PDH）。只有 NVIDIA 彻底读不到时才会打开，打开前零成本。</summary>
    private readonly GpuEngineUsage _igpuUsage = new();

    /// <summary>核显型号（null = 还没探到 / 确实没有）。</summary>
    private string? _igpuName;
    private DateTime _igpuRetryAt = DateTime.MinValue;

    /// <summary>
    /// HWiNFO 帧源 —— 只给核显那条路补温度/频率用（Windows 没有核显的原生接口）。
    /// 为 null（没装 HWiNFO / 独立构造）时这两项就是 null，界面整行隐藏。
    /// </summary>
    private readonly Func<HwInfo.HwFrame?>? _hwFrame;

    /// <summary>型号（构造时取一次，两条路都没数时给 Unknown）。</summary>
    public string Name { get; }

    public GpuSensors(Func<HwInfo.HwFrame?>? hwFrame = null)
    {
        _hwFrame = hwFrame;

        // 型号优先问 NVML：它已经初始化过了，再为一行字符串起一个 nvidia-smi 不值
        var nv = NvmlSensors.Read();
        if (nv is not null)
        {
            Name = nv["name"]?.GetValue<string>() ?? UnknownName;
            return;
        }

        // NVML 不可用时走**同一条** smi 解析（含失败字样与结构校验）——
        // 别在这里另写一遍"取第一行"：那正是把错误句当成型号的老路。
        var probe = ReadBySmi();
        if (probe["available"]?.GetValue<bool>() == true)
        {
            Name = probe["name"]?.GetValue<string>() ?? UnknownName;
            return;
        }

        // 连 NVIDIA 都没有（集显输出模式 / 别的品牌机器）：报核显，至少不是 Unknown GPU
        Name = IgpuName() ?? UnknownName;
    }

    /// <summary>
    /// 显卡功耗墙（W）：**当前生效** / 硬件默认。
    ///
    /// 给「三档功耗墙学习」用（`AppState.PowerWall`）—— 那里要的是"这个模式下显卡到底给多少"，
    /// 只有 `Now` 会随性能模式变（办公 83.23 / 游戏 114.50 / 狂暴 140，2026-11-09 实测）。
    ///
    /// ⚠️ `Now` 是**驱动当前生效值**，不是实时功耗（实时功耗在 <see cref="Read"/> 的 `power`）。
    /// ⚠️ 硬件**最高**墙这里不给：`nvmlDeviceGetPowerManagementLimitConstraints` 在本机
    ///    `0xC0000005` 崩进程（不可捕获），改由 HWiNFO 那一路补（见 `HwInfoReader`）。
    /// ⚠️ 只走 NVML，不回落 `nvidia-smi`：缺值时上层整行隐藏，别为一条墙再起子进程。
    /// </summary>
    public static (double? Now, double? Rated) ReadPowerLimits()
        => NvmlSensors.ReadPowerLimits();

    /// <summary>
    /// GPU 静态信息（驱动版本 / PCIe 代数与宽度上限）—— 就是 `/api/gpu/info` 的形状。
    /// NVML 不可用（Optimus 关掉 dGPU）时返回 null，调用方自己决定回落什么。
    /// </summary>
    public static JsonObject? StaticInfo() => NvmlSensors.StaticInfo();

    public JsonObject Read()
    {
        // 快路：一次 NVML 读 = 几条驱动侧查询，微秒级，无进程
        var nv = NvmlSensors.Read();
        if (nv is not null) return nv;

        var smi = ReadBySmi();
        if (smi["available"]?.GetValue<bool>() == true) return smi;

        // 第三级：独显彻底读不到时改报核显（2026-11-07）。
        // 集显输出模式下系统里只剩核显这块适配器，总览页至少还能显示是谁、忙多少，
        // 而不是一句"不可用"。
        return ReadIntegrated();
    }

    /// <summary>
    /// 核显那一帧：名称 + 占用率，有 HWiNFO 时再补核心频率。
    ///
    /// ⚠️ **温度不给**（2026-11-07 核实后去掉）：HWiNFO 的 `iGPU … GPU Core Temperature`
    ///    与 `CPU … CPU GT Cores (Graphics)` 是同一个 DTS 点（实测同值），
    ///    和 CPU 同 die 同散热，摆在显卡卡上会被误读成独显那种独立传感器。
    /// ⚠️ 功耗 / 显存也不给：
    ///    · HWiNFO 的 `IGPU Power` 本机读到 67.8W（比整包还高，是包装值，别信）；
    ///    · 显存只有 `GPU D3D Memory Dynamic`（动态占用），没有"总容量"，
    ///      WMI 的 `AdapterRAM` 对核显是 32 位老字段、4GB 封顶，不可信。
    /// 按项目规矩：拿不到就不放键，界面整行隐藏，绝不用 CPU 温度之类的东西顶上去冒充。
    /// </summary>
    private JsonObject ReadIntegrated()
    {
        var name = IgpuName();
        if (name is null) return Unavailable();

        var usage = _igpuUsage.Value();

        double? clock = null;
        if (_hwFrame is not null)
            clock = new HwInfo.HwInfoReader(_hwFrame).FindIntegratedGpuClock().Clock;

        var o = new JsonObject
        {
            ["available"] = true,
            ["name"] = name,
            ["usage"] = usage is null ? null : Math.Round(usage.Value, 1),
        };
        if (clock is { } c) o["clock"] = Math.Round(c, 0);
        return o;
    }

    /// <summary>核显型号。探到一次就记住；没探到时 10 秒后再问（WMI 打嗝时别每拍都去查）。</summary>
    private string? IgpuName()
    {
        if (_igpuName is not null) return _igpuName;

        var now = DateTime.UtcNow;
        if (now < _igpuRetryAt) return null;

        _igpuName = DisplaySensors.IntegratedName();
        if (_igpuName is null) _igpuRetryAt = now.AddSeconds(10);
        return _igpuName;
    }

    /// <summary>
    /// `nvidia-smi` 兜底。返回 `available:false`（只有这一个键）= 这台机器现在没有可用的
    /// NVIDIA 显卡，与"读到了但某项是 `[N/A]`"是两回事。
    /// </summary>
    private JsonObject ReadBySmi()
    {
        if (DateTime.UtcNow < _smiRetryAt) return Unavailable();

        var (stdout, stderr) = Run($"--query-gpu={Query} --format=csv,noheader,nounits");

        // 第一关：失败说明。`nvidia-smi` 不区分 stdout/stderr，两个流都看。
        var why = FirstFailure(stdout) ?? FirstFailure(stderr);
        if (why is not null) return NoGpu(why);

        var line = stdout.Split('\n')
                         .Select(x => x.Trim())
                         .FirstOrDefault(x => x.Length > 0);
        if (line is null) return NoGpu(null);        // 空输出 / 超时：不写日志，短冷却

        var v = line.Split(',').Select(x => x.Trim()).ToArray();

        // 第二关：结构。这一行必须是 name + 6 项读数。
        // 段数不够 ⇒ 它不是数据（警告文本、老驱动的其它输出），当"没显卡"，
        // **绝不**把半截英文塞进 name。
        if (v.Length < CsvFields) return NoGpu(line);

        // 显卡名理论上不含逗号，但真带了也不能把后面的读数挤错位 ⇒ 读数固定取**最后 6 段**。
        var numStart = v.Length - (CsvFields - 1);
        var name = string.Join(",", v[..numStart]).Trim();

        // ⚠️ 解析失败一律给 **null**，不给 0.0：
        //    nvidia-smi 对不支持的字段输出 `[N/A]`，给 0 会让界面显示
        //    "0 W / 0 MHz" 这种假数据。null 走界面既有的"整行隐藏"约定。
        double? F(int i)
        {
            var s = v[numStart + i];
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : null;
        }

        return new JsonObject
        {
            ["available"] = true,
            ["name"] = name.Length > 0 ? name : Name,
            ["temp"] = F(0),
            ["usage"] = F(1),
            ["power"] = F(2),
            ["clock"] = F(3),
            ["mem_used"] = F(4),
            ["mem_total"] = F(5),
        };
    }

    private static JsonObject Unavailable() => new() { ["available"] = false };

    /// <summary>
    /// 记一次"没有可用的 NVIDIA 显卡"并设冷却。<paramref name="detail"/> 为 null = 这一拍
    /// 只是没读到（空输出 / 超时），那不写日志，冷却也短。
    /// </summary>
    private JsonObject NoGpu(string? detail)
    {
        _smiRetryAt = DateTime.UtcNow.AddSeconds(detail is null ? EmptyRetrySeconds : NoGpuRetrySeconds);

        if (detail is not null && !string.Equals(detail, _lastWhy, StringComparison.Ordinal))
        {
            _lastWhy = detail;
            // 一行就够：完整报文往往带换行和提示语，日志里会糊成一片
            var oneLine = string.Join(" ", detail.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                                .Trim();
            Console.Error.WriteLine("[gpu] 没有可用的 NVIDIA 显卡：" + oneLine);
        }

        return Unavailable();
    }

    /// <summary>
    /// `nvidia-smi` 的已知失败报文（切集显输出 / 驱动没装 / 设备句柄取不到）。
    /// 命中任意一句就说明**这段文本不是数据**，别拿去解析。
    /// </summary>
    private static string? FirstFailure(string text)
    {
        if (text.Length == 0) return null;

        string[] markers =
        [
            "NVIDIA-SMI has failed",                    // 驱动与显卡不通信
            "couldn't communicate with the NVIDIA",     // 同上的长句后半
            "Unable to determine the device handle",    // 设备掉线
            "No devices were found",                    // Optimus 把 dGPU 关掉
            "No NVIDIA devices",
        ];

        foreach (var m in markers)
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase)) return text;

        return null;
    }

    /// <summary>
    /// 跑 nvidia-smi，**两个流一起异步收**（只收 stdout 会把失败说明漏成"空输出"，
    /// 而 stderr 不读干净则可能在缓冲区满时把子进程卡住）。
    /// `CreateNoWindow` 是必须的 —— 否则在无窗口/托盘模式下会弹黑框。
    /// <para>
    /// ⚠️ 2026-11-12 修：以前 stdout 走的是**同步** `ReadToEnd()`，它会一直阻塞到子进程
    /// 关掉管道为止 —— 后面那句 `WaitForExit(6000)` 根本轮不到执行。驱动半死 / 切集显输出时
    /// `nvidia-smi` 挂在 `nvmlInit` 上不退出，于是整个采样线程（以及 /api/status 的每一帧）
    /// 被永久吃掉，进程还在、数据全停。现在两个流都异步读，超时才真的能生效、才杀得掉。
    /// 写法与 <c>Sys/Shell.cs</c> 一致，那边早就是对的。
    /// </para>
    /// </summary>
    private static (string Out, string Err) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return ("", "");

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(6000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                // 被杀掉的进程不会再把管道读完：给读取任务一点时间收尾，别把异常带出去。
                try { Task.WaitAll([outTask, errTask], 500); } catch { }
                return ("", "");                    // 超时当"没读到"，由调用方的短冷却接手
            }

            // 兜底：进程都退出了这两个任务基本已经完成；万一没完成或拿异常，
            // 也**不能连已经拿到的 stdout 一起丢掉**（那会把"读到了"降级成"没读到"）
            return (SafeResult(outTask), SafeResult(errTask));
        }
        catch
        {
            return ("", "");      // 没有独显 / 没装驱动 / nvidia-smi 不在 PATH：正常情况，不是错误
        }
    }

    /// <summary>取异步读取结果；任务没完成或抛异常时给空串，绝不把异常漏出去。</summary>
    private static string SafeResult(Task<string> t)
    {
        try { return t.Wait(500) ? t.Result : ""; } catch { return ""; }
    }

    public void Dispose() => _igpuUsage.Dispose();
}
