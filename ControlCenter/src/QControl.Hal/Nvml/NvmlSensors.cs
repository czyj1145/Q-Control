using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace QControl.Hal.Nvml;

/// <summary>
/// GPU 实时读数走**进程内 NVML**（温度 / 占用 / 功耗 / 核心频率 / 显存）。
///
/// 为什么不用 `nvidia-smi`（2026-11-03 实测后改）：`nvidia-smi` 每次调用要重新
/// `nvmlInit` + 枚举设备，实测 **53 ms 墙钟**，而采样是**每秒一拍** ⇒ 一小时 3600 个
/// 子进程、约 3 分钟墙钟、2~3% 单核 —— 比控制中心自己（0.3%）贵一个数量级，
/// 也是托盘常驻时唯一的"持续动作"。换成常驻进程内的 NVML 之后：
///   · 初始化只在**第一次读数**付一次（~50 ms，和一次 nvidia-smi 等价）；
///   · 之后每次读是 **微秒级**（同一块共享内存里的计数器，不走 IPC、不起进程）；
///   · 读数的语义与 `nvidia-smi --query-gpu=…` **逐项一致**（温度 °C / 占用 % /
///     功耗 W / 频率 MHz / 显存 MiB），`/api/status` 的形状一个字节都不用改。
///
/// ⚠️ **失败必须回落**：`nvml.dll` 的可用性随驱动安装方式变化，老驱动还可能缺
///    `nvmlDeviceGetUtilizationRates`。所以这里任何一项读不到就返回 null，
///    由 <see cref="Sensors.GpuSensors"/> 回到原来的 `nvidia-smi` 路径 ——
///    NVML 是"省 CPU 的快路"，不是"唯一的路"。
/// ⚠️ 与 <see cref="NvmlOc"/> **共用同一个 init 与设备句柄**：NVML 的 init 是引用计数的，
///    再 init 一次不会错，但同一个进程里没必要开两份驱动上下文（每份都要占工作集）。
///    超频那条路径本来就懒加载，谁先到谁初始化。
/// ⚠️ 读**不需要**管理员：只有写时钟偏移才需要（见 NvmlOc 的注释）。
/// </summary>
internal static class NvmlSensors
{
    // nvmlTemperatureSensors_t：0 = 核心温度
    private const uint TempGpu = 0;
    // nvmlClockType_t：0 = 图形核心
    private const int ClockGraphics = 0;

    /// <summary>
    /// 读一帧，形状与 `GpuSensors.Read()` 完全一致（键名 / 单位 / null 语义都一样）。
    /// 返回 null = NVML 不可用（没驱动 / 没 dll / 设备读不出来）⇒ 调用方回落 `nvidia-smi`。
    /// </summary>
    public static JsonObject? Read()
    {
        if (!NvmlOc.Instance.TryDevice(out var dev, out _)) return null;

        // 每项各自成败：显卡休眠 / 老驱动不支持某一项时**只少这一项**，
        // 不让整个读数失败（与 nvidia-smi 的 `[N/A]` 语义一致 —— 给 null 不给 0）。
        var temp = Try(() => { Check(nvmlDeviceGetTemperature(dev, TempGpu, out var v), "读温度"); return (double)v; });
        var usage = Try(() =>
        {
            var u = new Utilization();
            Check(nvmlDeviceGetUtilizationRates(dev, ref u), "读占用率");
            return (double)u.Gpu;
        });
        var power = Try(() =>
        {
            Check(nvmlDeviceGetPowerUsage(dev, out var mw), "读功耗");
            return Math.Round(mw / 1000.0, 2);          // mW → W，与 smi 的小数位对齐
        });
        var clock = Try(() =>
        {
            Check(nvmlDeviceGetClockInfo(dev, ClockGraphics, out var mhz), "读核心频率");
            return (double)mhz;
        });

        double? memUsed = null, memTotal = null;
        var mem = Try(() =>
        {
            var m = new Memory();
            Check(nvmlDeviceGetMemoryInfo(dev, ref m), "读显存");
            return (Used: Math.Round(m.Used / 1048576.0), Total: Math.Round(m.Total / 1048576.0));
        });
        if (mem is { } mm)
        {
            memUsed = mm.Used;
            memTotal = mm.Total;
        }

        // 四项核心读数**全失败** ⇒ 当作这套路径不可用（典型：Optimus 把 dGPU 关掉了）。
        // 这时必须回落 smi 去拿"没有显卡"的同一个结论，而不是回一帧 available:true 的空壳。
        if (temp is null && usage is null && power is null && clock is null) return null;

        var name = NvmlOc.Instance.DeviceName;
        return new JsonObject
        {
            ["available"] = true,
            ["name"] = string.IsNullOrWhiteSpace(name) ? "Unknown GPU" : name,
            ["temp"] = temp,
            ["usage"] = usage,
            ["power"] = power,
            ["clock"] = clock,
            ["mem_used"] = memUsed,
            ["mem_total"] = memTotal,
        };
    }

    /// <summary>
    /// 显卡功耗墙（W）→ `{now, rated, max}`，**原生优先，别再走 nvidia-smi**。
    ///
    /// <para>
    /// `now` = `nvmlDeviceGetEnforcedPowerLimit`，**当前生效的墙**（Dynamic Boost 折算后）。
    /// 实测（2026-11-09，驱动 621.01）三档各不相同：办公 **83.23 W** / 游戏 **114.50 W** /
    /// 狂暴 **140 W** —— 这正是界面「每档下面那行显卡功耗」该显示的数字。
    /// </para>
    ///
    /// ⚠️ **`nvmlDeviceGetPowerManagementLimit` 在本机返回 3（NOT_SUPPORTED）**，别用它 ——
    ///    即便 `nvidia-smi -q -d POWER` 的 `Current Power Limit` 有值，那条 query 走的是
    ///    enforced 这一支。两个名字很像，报错也像"驱动不暴露"，2026-11-09 各踩过一次。
    /// ⚠️ **旧结论已作废**：DETAILS 里"当前生效墙驱动不暴露（`nvidia-smi power.limit` 恒 N/A）"
    ///    是 2026-10 那版驱动的实测。`power.limit` 那个 *query 字段* 仍然是 N/A，
    ///    但 enforced / `Current Power Limit` 都读得到。
    /// ⚠️ **不要调 `nvmlDeviceGetPowerManagementLimitConstraints`**（2026-11-09 实测）：
    ///    它在这台机器上 `0xC0000005` 直接崩进程（`struct{uint,uint}` 与 `ref` 两种写法都试过），
    ///    而 AV 是**不可捕获**的 —— `Try` 兜不住，整个后端会静默消失（日志停在最后一拍）。
    ///    最高墙留给 <see cref="HwInfo.HwInfoReader"/> 的 HWiNFO 那一路补，别在这儿冒险。
    /// ⚠️ 读不要管理员权限（非提权进程实测也返回 70 / 140 W）。
    /// </summary>
    public static (double? Now, double? Rated) ReadPowerLimits()
    {
        if (!NvmlOc.Instance.TryDevice(out var dev, out _)) return (null, null);

        var now = Try(() =>
        {
            Check(nvmlDeviceGetEnforcedPowerLimit(dev, out var mw), "读当前生效功耗墙");
            return Math.Round(mw / 1000.0, 2);
        });

        var rated = Try(() =>
        {
            Check(nvmlDeviceGetPowerManagementDefaultLimit(dev, out var mw), "读默认功耗墙");
            return Math.Round(mw / 1000.0, 2);
        });

        return (now, rated);
    }

    /// <summary>NVML 不可用（没装 / 初始化失败）时的原因，给诊断口用；可用时为 null。</summary>
    public static string? Unavailable =>
        NvmlOc.Instance.TryDevice(out _, out var err) ? null : err;

    /// <summary>
    /// 显卡**静态信息**：型号 / 驱动 / VBIOS / 显存总量 / PCIe 上限。
    ///
    /// 硬件档案（`/api/hardware`）原来为这 5 个字段起一个 `nvidia-smi` 子进程 ——
    /// 实测 **58 ms**，占整轮采集约 17%，而这些都是**装机后就不会变**的字符串。
    /// 换成进程内 NVML 后是微秒级（与 <see cref="Read"/> 共用同一份 init 与设备句柄）。
    ///
    /// 返回 null = NVML 完全不可用（没驱动 / 没 dll / 显卡被 Optimus 关掉），
    /// 由调用方回落 `nvidia-smi` —— NVML 是省时间的快路，不是唯一的路。
    /// 单项取不到就是 null（老驱动可能缺 `nvmlDeviceGetVbiosVersion` 这类接口），不影响其它项。
    /// </summary>
    public static JsonObject? StaticInfo()
    {
        if (!NvmlOc.Instance.TryDevice(out var dev, out _)) return null;

        var memTotal = Try(() =>
        {
            var m = new Memory();
            Check(nvmlDeviceGetMemoryInfo(dev, ref m), "读显存总量");
            // 字节 → MiB（`nvidia-smi --query-gpu=memory.total --nounits` 就是这个单位）
            return Math.Round(m.Total / 1048576.0);
        });

        var gen = Try(() =>
        {
            Check(nvmlDeviceGetMaxPcieLinkGeneration(dev, out var v), "读 PCIe 代数上限");
            return (double)v;
        });

        var width = Try(() =>
        {
            Check(nvmlDeviceGetMaxPcieLinkWidth(dev, out var v), "读 PCIe 宽度上限");
            return (double)v;
        });

        var name = NvmlOc.Instance.DeviceName;
        var driver = NvmlOc.Instance.DriverVersion;
        var vbios = Text((byte[] b, uint n) => nvmlDeviceGetVbiosVersion(dev, b, n), 96);

        // 一条都拿不到 ⇒ 当作这套路径不可用（典型：Optimus 把 dGPU 关掉了），
        // 让调用方回落 smi 去拿"没有显卡"的同一个结论。
        if (memTotal is null && gen is null && width is null
            && vbios.Length == 0 && name.Length == 0) return null;

        return new JsonObject
        {
            ["name"] = name.Length > 0 ? name : null,
            ["driver"] = driver.Length > 0 ? driver : null,
            ["vbios"] = vbios.Length > 0 ? vbios : null,
            ["mem_total"] = memTotal,
            ["pcie_gen"] = gen,
            ["pcie_width"] = width,
        };
    }

    /// <summary>定长 UTF-8 缓冲 → 字符串（NVML 的 `Get*Version` 系列都是这个形状）。</summary>
    private static string Text(Func<byte[], uint, int> call, int size)
    {
        try
        {
            var buf = new byte[size];
            if (call(buf, (uint)size) != 0) return "";
            var end = Array.IndexOf(buf, (byte)0);
            return System.Text.Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end).Trim();
        }
        catch
        {
            return "";
        }
    }

    private static void Check(int code, string what)
    {
        if (code != 0) throw new NvmlException(code, what);
    }

    /// <summary>单项读取：任何 NVML 失败 / 老驱动缺函数都只让这一项变 null。</summary>
    private static T? Try<T>(Func<T> call) where T : struct
    {
        try
        {
            return call();
        }
        catch (Exception e) when (e is NvmlException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limit);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint limit);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temp);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, ref Utilization utilization);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, uint type, out uint clock);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, ref Memory memory);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetVbiosVersion(IntPtr device, byte[] version, uint length);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint maxLinkGen);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint maxLinkWidth);
}
