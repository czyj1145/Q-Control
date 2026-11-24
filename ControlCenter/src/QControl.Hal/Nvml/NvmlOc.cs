using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace QControl.Hal.Nvml;

/// <summary>NVML 返回码异常。`Code` 是原始 <c>nvmlReturn_t</c>，用来区分"没权限"和"不支持"。</summary>
public sealed class NvmlException(int code, string what)
    : Exception($"{what}：{NvmlOc.ErrorText(code)}")
{
    public int Code { get; } = code;
}

/// <summary>
/// GPU 超频 —— 直连 `nvml.dll` 的**时钟偏移**接口（`nvmlDeviceGet/SetClockOffsets`）。
///
/// 为什么只有这一条路（2026-10-11 逐条排除过，结论不变）：
///   · `nvidia-smi -pl`（功耗墙）：本机硬件不支持；
///   · `-lgc / -lmc`（锁频）：GeForce 不支持；
///   · NVML 的 Applications Clocks：本机全部 Not Supported（只对 Tesla/Quadro 开放）；
///   · 老的 VF 接口（`SetGpcClkVfOffset` 等）：本机 Not Supported，且 NVIDIA 已宣布弃用。
/// 唯一的 `nvmlDeviceGetClockOffsets` / `SetClockOffsets` 从 r555 起公开，
/// 与原厂 `QNVOC.dll` 的 `QNVSetGPUClockDelta` / `QNVSetMEMClockDelta` 语义一一对应。
///
/// ⚠️ **显存 offset 是双倍单位**：要显存有效频率 +500 MHz，得写 1000。
///    本模块内部一律用驱动原值（raw），只在读给界面时除 2。
/// ⚠️ 写需要管理员：非管理员返回 `Insufficient Permissions`（4），**不是** Not Supported（3）——
///    两者必须分开报，否则用户会以为"显卡不支持"。
/// </summary>
public sealed class NvmlOc
{
    // nvmlClockType_t
    private const int ClockGraphics = 0;
    private const int ClockMem = 2;
    // nvmlPstates_t：0 = 最高性能档（超频偏移只对它生效）
    private const uint Pstate0 = 0;
    // nvmlTemperatureSensors_t：0 = 核心温度
    private const uint TempGpu = 0;

    /// <summary>`nvmlClockOffset_v1` 的版本号：(1&lt;&lt;24) | sizeof(结构) = 0x1000018。</summary>
    private const uint OffsetStructV1 = 0x1000018;

    /// <summary>显存 raw → 有效 MHz 的除数。</summary>
    public const int MemEffDivisor = 2;

    private const double CapTtlSeconds = 5.0;
    private const double RetryAfterSeconds = 30.0;

    private static readonly Lazy<NvmlOc> Lazy = new(() => new NvmlOc());
    public static NvmlOc Instance => Lazy.Value;

    private readonly object _lock = new();
    private IntPtr _dev;
    private bool _opened;
    private string? _error;
    private DateTime _failAt = DateTime.MinValue;
    private string _name = "";
    private string _driver = "";
    private JsonObject? _cap;
    private DateTime _capAt = DateTime.MinValue;

    // ---------------------------------------------------------------- 打开

    /// <summary>加载 nvml.dll 并取到 GPU0 句柄。失败后 30 秒内直接复用错误（别每次调用都重试）。</summary>
    private void Open()
    {
        if (_opened) return;
        if (_error is not null && (DateTime.UtcNow - _failAt).TotalSeconds < RetryAfterSeconds)
            throw new InvalidOperationException(_error);

        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var dll = Path.Combine(win, "System32", "nvml.dll");
            if (!File.Exists(dll))
                throw new FileNotFoundException("未找到 nvml.dll（没装 NVIDIA 驱动？）：" + dll, dll);

            // 预加载绝对路径：之后 DllImport("nvml.dll") 命中的就是这个模块。
            // 直接靠 DllImport 搜索也行，但那样出错时拿不到"文件不存在"这种明确原因。
            NativeLibrary.Load(dll);

            Check(nvmlInit_v2(), "nvmlInit");
            Check(nvmlDeviceGetHandleByIndex_v2(0, out _dev), "获取 GPU0 句柄");

            _name = ReadName((byte[] b, uint n) => nvmlDeviceGetName(_dev, b, n), 128);
            _driver = ReadName(nvmlSystemGetDriverVersion, 96);

            _opened = true;
            _error = null;
        }
        catch (Exception e)
        {
            _error = e.GetType().Name + ": " + e.Message;
            _failAt = DateTime.UtcNow;
            throw new InvalidOperationException(_error, e);
        }
    }

    private static string ReadName(Func<byte[], uint, int> call, int size)
    {
        try
        {
            var buf = new byte[size];
            if (call(buf, (uint)size) != 0) return "";
            var end = Array.IndexOf(buf, (byte)0);
            return Encoding.UTF8.GetString(buf, 0, end < 0 ? buf.Length : end);
        }
        catch
        {
            return "";
        }
    }

    internal static string ErrorText(int code)
    {
        try
        {
            var p = nvmlErrorString(code);
            return p == IntPtr.Zero ? "code " + code : Marshal.PtrToStringUTF8(p) ?? ("code " + code);
        }
        catch
        {
            return "code " + code;
        }
    }

    private static void Check(int code, string what)
    {
        if (code != 0) throw new NvmlException(code, what);
    }

    // ---------------------------------------------------------------- 读 / 写

    /// <summary>
    /// 读/写某个时钟域的 offset。<c>value = null</c> 只读；给了值则写入后**再读回校验**。
    /// 返回 `(raw, min, max)`；读回失败三项都是 null。
    /// </summary>
    private (int? Raw, int? Min, int? Max) ReadWrite(int clockType, int? value)
    {
        var off = new ClockOffset
        {
            Version = OffsetStructV1,
            Type = (uint)clockType,
            Pstate = Pstate0,
            ClockOffsetMHz = value ?? 0,
        };

        var rc = value is null
            ? nvmlDeviceGetClockOffsets(_dev, ref off)
            : nvmlDeviceSetClockOffsets(_dev, ref off);
        Check(rc, value is null ? "读时钟偏移" : "写时钟偏移");

        // 「写→读回验证」是本项目所有写操作的硬性规矩，不接受"发出去就算成功"
        var back = new ClockOffset { Version = OffsetStructV1, Type = (uint)clockType, Pstate = Pstate0 };
        var rc2 = nvmlDeviceGetClockOffsets(_dev, ref back);
        return rc2 == 0
            ? (back.ClockOffsetMHz, back.MinClockOffsetMHz, back.MaxClockOffsetMHz)
            : (null, null, null);
    }

    /// <summary>UI 的「有效 MHz」→ 驱动原值（显存 ×2）。</summary>
    public static int? EffToRaw(string kind, int? eff)
        => eff is null ? null : eff.Value * (kind == "mem" ? MemEffDivisor : 1);

    // ---------------------------------------------------------------- 对外

    /// <summary>加载是否成功（失败给原因）。</summary>
    public (bool Ok, string? Error) Available()
    {
        try
        {
            lock (_lock) Open();
            return (true, null);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>
    /// 取设备句柄（给**读数**用 —— <see cref="NvmlSensors"/> 复用这一份 init 与句柄）。
    /// 失败给 false + 原因，**不抛**：读数是采样路径上的东西，不能因为显卡睡了就丢异常。
    /// </summary>
    internal bool TryDevice(out IntPtr device, out string? error)
    {
        lock (_lock)
        {
            try
            {
                Open();
                device = _dev;
                error = null;
                return true;
            }
            catch (Exception e)
            {
                device = IntPtr.Zero;
                error = e.Message;
                return false;
            }
        }
    }

    /// <summary>设备名（`nvmlDeviceGetName` 的原样字符串，取不到时是空串）。</summary>
    internal string DeviceName { get { lock (_lock) return _name; } }

    /// <summary>驱动版本（`nvmlSystemGetDriverVersion`，取不到时是空串）。</summary>
    internal string DriverVersion { get { lock (_lock) return _driver; } }

    /// <summary>
    /// 能力：核心/显存的 offset 范围与当前值。**缓存 5 秒**（NVML 查询不便宜，别每帧打）。
    /// 写操作后会把缓存打掉，下一次进来立刻刷新。
    /// </summary>
    public JsonObject Capability(bool refresh = false)
    {
        lock (_lock)
        {
            if (!refresh && _cap is not null
                && (DateTime.UtcNow - _capAt).TotalSeconds < CapTtlSeconds)
                return (JsonObject)_cap.DeepClone();

            try
            {
                Open();
                var outObj = new JsonObject
                {
                    ["ok"] = true,
                    ["name"] = _name,
                    ["driver"] = _driver,
                    ["error"] = null,
                };

                AddDomain(outObj, "core", ClockGraphics, 1);
                AddDomain(outObj, "mem", ClockMem, MemEffDivisor);

                _cap = outObj;
                _capAt = DateTime.UtcNow;
                return (JsonObject)outObj.DeepClone();
            }
            catch (Exception e)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["error"] = e.Message,
                    ["core"] = null,
                    ["mem"] = null,
                    ["name"] = _name,
                    ["driver"] = _driver,
                };
            }
        }
    }

    private void AddDomain(JsonObject outObj, string key, int clockType, int divisor)
    {
        var (raw, min, max) = ReadWrite(clockType, null);
        outObj[key] = new JsonObject
        {
            ["raw"] = raw,
            ["min"] = min,
            ["max"] = max,
            ["eff"] = Div(raw, divisor),
            ["eff_min"] = Div(min, divisor),
            ["eff_max"] = Div(max, divisor),
            ["divisor"] = divisor,
        };
    }

    /// <summary>
    /// 偏移量按早先实现的 `//` 取整（向下取整）。C# 的 `/` 是向零截断，
    /// 负数上差 1（-3//2=-2 vs -3/2=-1），界面会看到差一档的数。
    /// </summary>
    private static int? Div(int? value, int divisor)
        => value is null ? null : (int)Math.Floor(value.Value / (double)divisor);

    /// <summary>
    /// 实时状态（**不缓存**，每次现读）：当前/标称最大频率、P-State、温度、功耗。
    /// 读不到的项**不放这个键**（不补 0），界面按"没有就整行隐藏"处理。
    /// </summary>
    public JsonObject Status()
    {
        lock (_lock)
        {
            try { Open(); }
            catch (Exception e) { return new JsonObject { ["ok"] = false, ["error"] = e.Message }; }

            var outObj = new JsonObject { ["ok"] = true };

            if (TryCall(() => { Check(nvmlDeviceGetClockInfo(_dev, (uint)ClockGraphics, out var v), "读核心频率"); return (int)v; }, out var core))
                outObj["core_clock"] = core;
            if (TryCall(() => { Check(nvmlDeviceGetMaxClockInfo(_dev, (uint)ClockGraphics, out var v), "读最大核心频率"); return (int)v; }, out var coreMax))
                outObj["core_clock_max"] = coreMax;
            if (TryCall(() => { Check(nvmlDeviceGetClockInfo(_dev, (uint)ClockMem, out var v), "读显存频率"); return (int)v; }, out var mem))
                outObj["mem_clock"] = mem;
            if (TryCall(() => { Check(nvmlDeviceGetMaxClockInfo(_dev, (uint)ClockMem, out var v), "读最大显存频率"); return (int)v; }, out var memMax))
                outObj["mem_clock_max"] = memMax;
            // ⚠️ P0 就是 0 —— 判据必须是"拿到了值"，不是"值非零"，否则丢掉最关键的那条
            if (TryCall(() => { Check(nvmlDeviceGetPerformanceState(_dev, out var v), "读 P-State"); return v; }, out var pstate))
                outObj["pstate"] = pstate;
            if (TryCall(() => { Check(nvmlDeviceGetTemperature(_dev, TempGpu, out var v), "读温度"); return (int)v; }, out var temp))
                outObj["temp"] = temp;
            if (TryCall(() => { Check(nvmlDeviceGetPowerUsage(_dev, out var v), "读功耗"); return (int)v; }, out var mw))
                outObj["power"] = Math.Round(mw / 1000.0, 1);   // mW → W

            return outObj;
        }
    }

    /// <summary>
    /// 调一个**可选**函数：函数不存在（老驱动）或返回非 0 → 返回 false，这一项就不出现。
    /// 绝不因为少一个可选项让整个状态读取失败。
    /// </summary>
    private static bool TryCall<T>(Func<T> call, out T value)
    {
        try
        {
            value = call();
            return true;
        }
        catch (Exception e) when (e is NvmlException or EntryPointNotFoundException or DllNotFoundException)
        {
            value = default!;
            return false;
        }
    }

    /// <summary>
    /// 写入 offset（`null` = 不动这一项）。两项**互相独立**：核心成功、显存失败时
    /// `ok=false` 但明细分开返回 —— 绝不允许"发了一半还报成功"。
    /// </summary>
    public JsonObject Apply(int? coreRaw, int? memRaw)
    {
        if (coreRaw is null && memRaw is null)
            return new JsonObject { ["ok"] = false, ["error"] = "没有要写入的值" };

        lock (_lock)
        {
            try { Open(); }
            catch (Exception e) { return new JsonObject { ["ok"] = false, ["error"] = e.Message }; }

            var res = new JsonObject();
            var errors = new List<string>();
            var ok = true;

            foreach (var (key, clockType, value) in new[]
                     {
                         ("core", ClockGraphics, coreRaw),
                         ("mem", ClockMem, memRaw),
                     })
            {
                if (value is null) continue;
                var want = value.Value;
                try
                {
                    var (raw, _, _) = ReadWrite(clockType, want);
                    var applied = raw == want;
                    res[key] = new JsonObject
                    {
                        ["raw"] = raw,
                        ["requested"] = want,
                        ["applied"] = applied,
                        ["error"] = applied ? null : $"驱动接受但未生效（写 {want} 读回 {raw}）",
                    };
                    if (!applied) ok = false;
                }
                catch (Exception e)
                {
                    var msg = Humanize(e);
                    res[key] = new JsonObject
                    {
                        ["raw"] = null,
                        ["requested"] = want,
                        ["applied"] = false,
                        ["error"] = msg,
                    };
                    errors.Add($"{key}: {msg}");
                    ok = false;
                }
            }

            if (errors.Count > 0) res["error"] = string.Join(" / ", errors);
            res["ok"] = ok;

            _capAt = DateTime.MinValue;     // 下次 capability 立刻刷新
            return res;
        }
    }

    /// <summary>两个时钟域都归零 —— 恢复出厂频率。</summary>
    public JsonObject Reset() => Apply(0, 0);

    /// <summary>把 NVML 的英文错误换成用户看得懂的中文（只改最常见两条）。</summary>
    private static string Humanize(Exception e)
    {
        if (e is NvmlException ne)
        {
            if (ne.Code == 4)          // NVML_ERROR_NO_PERMISSION
                return ne.Message + " —— 需要管理员权限运行控制中心"
                                    + "（重启一次并同意 UAC 即可；run-tray.cmd 也会提权）";
            if (ne.Code == 3)          // NVML_ERROR_NOT_SUPPORTED
                return ne.Message + " —— 当前驱动/显卡不支持该功能";
        }
        return e.Message;
    }

    // ---------------------------------------------------------------- P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct ClockOffset
    {
        public uint Version;
        public uint Type;
        public uint Pstate;
        public int ClockOffsetMHz;
        public int MinClockOffsetMHz;
        public int MaxClockOffsetMHz;
    }

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr nvmlErrorString(int code);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlSystemGetDriverVersion(byte[] version, uint length);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetClockOffsets(IntPtr device, ref ClockOffset offsets);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceSetClockOffsets(IntPtr device, ref ClockOffset offsets);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetClockInfo(IntPtr device, uint type, out uint clock);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, uint type, out uint clock);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int pstate);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensor, out uint temp);

    [DllImport("nvml.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
}
