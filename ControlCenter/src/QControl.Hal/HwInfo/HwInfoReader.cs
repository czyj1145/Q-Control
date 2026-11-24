using System.Text.RegularExpressions;

namespace QControl.Hal.HwInfo;

/// <summary>
/// 从共享内存里挑出那几项**只有 HWiNFO 能提供**的读数。
///
/// 为什么必须靠 HWiNFO（2026-10-16 逐条查过，不是偷懒）：
/// <list type="bullet">
/// <item>**CPU 电压**：EC 读组里没有电压字段；`Win32_Processor.CurrentVoltage` 是静态
///   SMBIOS 值、恒为 15，不是实时电压。</item>
/// <item>**PCH 温度**：EC 不吐、Windows 没有对应计数器。</item>
/// <item>**PL1/PL2**：只在 MSR，Windows 无接口。</item>
/// </list>
///
/// ⚠️ 每条都要求**单位**当硬条件：HWiNFO 里同名 label 会出现在多个组
/// （"Average Power" 在 CPU 和 GPU 下都有），只按 label 关键字收会串味。
/// </summary>
public sealed class HwInfoReader
{
    /// <summary>HWiNFO `dwReadingType`：1 = 温度。</summary>
    public const int TypeTemp = 1;

    /// <summary>PCH 读数的合理量程（°C）。超出即视为"不是温度"或读错。</summary>
    private static readonly (double Lo, double Hi) PchRange = (10, 125);

    private static readonly Regex TempUnit = new(@"(°|deg)?\s*C\b", RegexOptions.IgnoreCase);

    private readonly Func<HwFrame?> _frame;

    /// <summary>
    /// <paramref name="frameProvider"/> 每次调用取一帧（带新鲜度判断）。
    /// 传函数而不是帧，是为了让上层可以按秒复用同一帧（HWiNFO 2 秒才轮询一次）。
    /// </summary>
    public HwInfoReader(Func<HwFrame?> frameProvider) => _frame = frameProvider;

    // ================================================================ 通用判定

    private static string UnitOf(HwReading r) => (r.Unit ?? "").Trim();

    private static bool InUnit(HwReading r, string want)
        => UnitOf(r).Equals(want, StringComparison.OrdinalIgnoreCase);

    /// <summary>是不是温度读数 —— **以 HWiNFO 自己的类型标记为准**。</summary>
    private static bool IsTempReading(HwReading r)
    {
        if (r.Type == TypeTemp) return true;
        if (r.Type != 0) return false;                 // 明确标了非温度，就采信
        return TempUnit.IsMatch(UnitOf(r)) || UnitOf(r) == "°C";
    }

    /// <summary>
    /// "距 TjMax 还差多少度"这类**反向**读数 —— 单位与类型都像温度（夹具里
    /// `unit="°C"`、`type=1`），值也落在 10~125，但语义是"离上限还有几度"：
    /// 43 代表很热，不是 43 °C。当温度用会得到方向相反的结论。
    /// HWiNFO 在每个核心上都给这么一条（本机 24 条），必须按标签排掉。
    /// </summary>
    private static bool IsDistanceReading(string labelUpper)
        => labelUpper.Contains("DISTANCE", StringComparison.Ordinal)
           || labelUpper.Contains("TJMAX", StringComparison.Ordinal)
           || labelUpper.Contains("TJ MAX", StringComparison.Ordinal);

    // ================================================================ 找 PCH

    /// <summary>
    /// PCH 温度。判据按优先级：
    /// <list type="number">
    /// <item>传感器**组名**含 PCH（HWiNFO 常把 PCH 单独列一组）；</item>
    /// <item>读数 label 含 PCH；</item>
    /// <item>label 含 CHIPSET / SOUTHBRIDGE。</item>
    /// </list>
    /// 都要求是温度读数且落在量程内。
    /// </summary>
    public (double? Value, string? Source) FindPch()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? best = null;
        var bestPri = int.MaxValue;
        string? src = null;

        foreach (var (group, r) in d.Rows())
        {
            if (!IsTempReading(r)) continue;
            var v = r.Value;
            if (v < PchRange.Lo || v > PchRange.Hi) continue;

            var upGroup = d.SensorUpper(r.SensorIndex);
            var upLabel = r.LabelUpper;

            int pri;
            if (upGroup.Contains("PCH", StringComparison.Ordinal)
                || upLabel.Contains("PCH", StringComparison.Ordinal)) pri = 0;
            else if (upLabel.Contains("CHIPSET", StringComparison.Ordinal)
                     || upLabel.Contains("SOUTHBRIDGE", StringComparison.Ordinal)
                     || upLabel.Contains("SOUTH BRIDGE", StringComparison.Ordinal)) pri = 1;
            else continue;

            if (pri >= bestPri) continue;
            bestPri = pri;
            best = v;
            src = $"hwinfo:{group} / {r.Label}";
        }
        return (best, src);
    }

    // ================================================================ CPU 功耗（兜底）

    /// <summary>
    /// CPU 封装功耗 —— **兜底路径**。首选是系统 PDH RAPL（`RaplPower`），
    /// 只有那条路没数时才走到这里。
    ///
    /// 优先级：label 含 Package &gt; 其它含 Power 的项。
    /// 明确排除 Limit / TDP / Allow / PL1 / PL2 / Max —— 那些是**上限值**不是实测功耗。
    /// </summary>
    public (double? Value, string? Source) FindCpuPower()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? bestVal = null;
        var bestPri = int.MaxValue;
        string? bestLabel = null;

        foreach (var (group, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("CPU", StringComparison.Ordinal)) continue;
            if (!InUnit(r, "W")) continue;

            var lab = r.LabelUpper;
            if (!lab.Contains("POWER", StringComparison.Ordinal)) continue;
            if (new[] { "LIMIT", "TDP", "ALLOW", "PL1", "PL2", "MAX" }.Any(k => lab.Contains(k, StringComparison.Ordinal)))
                continue;

            var v = r.Value;
            if (!(v > 0 && v < 500)) continue;      // 0 = 没读到

            var pri = lab.Contains("PACKAGE", StringComparison.Ordinal) ? 0 : 1;
            if (pri >= bestPri) continue;
            bestPri = pri;
            bestVal = v;
            bestLabel = r.Label;
        }
        return (bestVal, bestVal is null ? null : $"hwinfo:{bestLabel}");
    }

    // ================================================================ CPU 电压

    /// <summary>
    /// CPU 核心电压。**本项没有原生替代**（EC 读组无电压字段、Windows 无电压接口）。
    ///
    /// ⚠️ HWiNFO 在 Intel 平台上**没有**"封装级核心电压"这一条 —— 只有每个核自己的
    /// `P-core N VID` / `E-core N VID`。实测 P-core 0 会明显高于其余核（1.31 vs 1.19，
    /// 单核睿频所致），所以**取各核 VID 的均值**，比抓某一个核稳定得多；
    /// 一个核都没有时才退到 Uncore/SA（那是环总线/系统代理，不是核心电压）。
    /// </summary>
    public (double? Value, string? Source) FindCpuVoltage()
    {
        var d = _frame();
        if (d is null) return (null, null);

        var cores = new List<double>();
        (string Label, double Value)? other = null;
        foreach (var (group, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("CPU", StringComparison.Ordinal)) continue;

            var u = r.UnitUpper;
            if (u is not ("V" or "MV")) continue;

            var lab = r.LabelUpper;
            var v = r.Value;
            if (u == "MV") v /= 1000.0;
            if (!(v > 0.2 && v < 2.0)) continue;

            if (Regex.IsMatch(lab, @"^[PE]-CORE \d+ VID$"))
            {
                cores.Add(v);
            }
            else if (other is null && new[] { "VID", "VCORE", "CORE VOLTAGE", "CPU VOLTAGE", "VDDCR" }
                     .Any(k => lab.Contains(k, StringComparison.Ordinal)))
            {
                other = (r.Label ?? "", v);
            }
        }

        if (cores.Count > 0)
            return (cores.Average(), $"hwinfo:核心 VID 均值（{cores.Count} 核）");
        if (other is { } o)
            return (o.Value, $"hwinfo:{o.Label}");
        return (null, null);
    }

    // ================================================================ CPU 温度（兜底）

    /// <summary>
    /// CPU 温度（°C）—— **兜底路径**：只有 EC 给不出 `cpu_temp` 时才走这里
    /// （未标定 layout 的机型、或 EC 临时读失败）。
    ///
    /// 判据按优先级：
    /// <list type="number">
    /// <item>`CPU Package` / `Tctl` / `Tdie` —— HWiNFO 自己给的核心温度，最接近"CPU 多少度"；</item>
    /// <item>`Core Max` / `Core Average` / `CPU Die`；</item>
    /// <item>本组里其它带 CPU / CORE 的温度。</item>
    /// </list>
    /// ⚠️ 必须限定在**组名含 CPU** 的传感器里，且量程 10~125 °C —— CPU 组里还有
    ///    VRM / 距离类读数（`Distance to TjMax` 之类），不卡量程会把 3 °C 当温度。
    /// </summary>
    public (double? Value, string? Source) FindCpuTemp()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? best = null;
        var bestPri = int.MaxValue;
        string? label = null;

        foreach (var (_, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("CPU", StringComparison.Ordinal)) continue;
            if (!IsTempReading(r)) continue;

            var v = r.Value;
            if (!(v >= 10 && v <= 125)) continue;

            var lab = r.LabelUpper;
            // ⚠️ `Distance to TjMAX` 单位/类型都像温度（夹具里 unit=°C、type=1），数值也落在
            //    10~125，但它是"离上限还差几度"的**反向**读数：43 表示很热。主路径有
            //    `CPU Package` 兜着看不出来，可一旦某个平台没有 Package/Tctl/Tdie，
            //    这里就会把"还差 43 度"当成"CPU 43 °C"，方向与数值全错。
            if (IsDistanceReading(lab)) continue;

            int pri;
            if (lab.Contains("PACKAGE", StringComparison.Ordinal)
                || lab.Contains("TCTL", StringComparison.Ordinal)
                || lab.Contains("TDIE", StringComparison.Ordinal)) pri = 0;
            else if (lab.Contains("CORE MAX", StringComparison.Ordinal)
                     || lab.Contains("CORE AVERAGE", StringComparison.Ordinal)
                     || lab.Contains("CPU DIE", StringComparison.Ordinal)) pri = 1;
            else if (lab.Contains("CPU", StringComparison.Ordinal)
                     || lab.Contains("CORE", StringComparison.Ordinal)) pri = 2;
            else continue;

            if (pri >= bestPri) continue;
            bestPri = pri;
            best = v;
            label = r.Label;
        }
        return (best, best is null ? null : $"hwinfo:{label}");
    }

    // ================================================================ 显卡温度（兜底）

    /// <summary>
    /// 显卡温度（°C）—— 兜底路径：`nvidia-smi` 拿不到温度时（AMD 独显 / 驱动没装 /
    /// 集显输出模式下 NVIDIA 组不出现）才走这里。原生能读就不用它。
    ///
    /// ⚠️ 组里"像温度"的标签很多：`GPU Memory Junction`（显存结温，另有 FindGpuMemTemp）、
    ///    `GPU Hot Spot`（热点，通常比核心高十几度）。这两个必须排除，
    ///    否则显示出来的"显卡温度"会比用户看到的实际核心温度高一截。
    /// </summary>
    public (double? Value, string? Source) FindGpuTemp()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? best = null;
        var bestPri = int.MaxValue;
        string? label = null;

        foreach (var (_, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("GPU", StringComparison.Ordinal)) continue;
            if (!IsTempReading(r)) continue;

            var lab = r.LabelUpper;
            if (lab.Contains("HOT SPOT", StringComparison.Ordinal)
                || lab.Contains("HOTSPOT", StringComparison.Ordinal)
                || lab.Contains("MEMORY", StringComparison.Ordinal)
                || lab.Contains("VRAM", StringComparison.Ordinal)
                || lab.Contains("GDDR", StringComparison.Ordinal)) continue;
            if (IsDistanceReading(lab)) continue;

            var v = r.Value;
            if (!(v >= 10 && v <= 125)) continue;

            int pri;
            if (lab == "GPU TEMPERATURE") pri = 0;
            else if (lab.Contains("CORE", StringComparison.Ordinal)) pri = 1;
            else if (lab.Contains("TEMP", StringComparison.Ordinal)) pri = 2;
            else continue;

            if (pri >= bestPri) continue;
            bestPri = pri;
            best = v;
            label = r.Label;
        }
        return (best, best is null ? null : $"hwinfo:{label}");
    }

    // ================================================================ 核显（独显不可用时）

    /// <summary>
    /// 核显的核心频率（MHz）—— 只在**独显彻底读不到**（集显输出模式）时才用，
    /// 见 `GpuSensors.ReadIntegrated`。
    ///
    /// ⚠️ **温度故意不在这里给**（2026-11-07 用户质疑"任务管理器都没有温度"后核实）：
    ///    HWiNFO 的 `iGPU [#0] … GPU Core Temperature` 与
    ///    `CPU [#0] …: Enhanced … CPU GT Cores (Graphics)` **是同一个 DTS 点**，
    ///    同一下实测两边都是 56.0°C。数字不假，但它和 CPU 同 die 同散热、会一起涨落，
    ///    放在显卡卡片上会被当成独显那种独立温度传感器 ⇒ 不显示。
    ///
    /// 频率是真读数（`GPU Clock`，非 `GPU Memory Clock`），本机集显输出下 700MHz。
    /// </summary>
    public (double? Clock, string? Source) FindIntegratedGpuClock()
    {
        var d = _frame();
        if (d is null) return (null, null);

        foreach (var (_, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("IGPU", StringComparison.Ordinal)) continue;

            var lab = r.LabelUpper;

            // ⚠️ 排除 `GPU Memory Clock`：那是显存频率，不是核心频率
            if (r.UnitUpper != "MHZ") continue;
            if (!lab.Contains("CLOCK", StringComparison.Ordinal)) continue;
            if (lab.Contains("MEMORY", StringComparison.Ordinal)) continue;
            if (r.Value is not (> 50 and < 5000)) continue;

            return (r.Value, $"hwinfo:{r.Label}");
        }

        return (null, null);
    }

    // ================================================================ 风扇转速（兜底）

    /// <summary>
    /// 两路风扇转速（RPM）—— EC 读不到 `fan1_rpm` / `fan2_rpm` 时的兜底。
    ///
    /// 判据：**单位必须是 RPM** + 标签含 FAN + 0 &lt; 值 &lt; 12000。
    /// ⚠️ 只认 RPM：同一组里 `Fan Control` / `Fan Duty` 是百分比或 0~255 的原始值，
    ///    收进来就是"假转速"（界面上会显示成一条永远不动的曲线）。
    ///
    /// 归属：标签含 CPU → CPU 风扇；含 GPU → GPU 风扇；两者都没有（HWiNFO 常只给
    /// `Fan1` / `Fan2`）时按出现顺序补空位。**宁可只给一路，也不把同一路填两遍。**
    /// </summary>
    public (double? Cpu, double? Gpu, string? Source) FindFanRpm()
    {
        var d = _frame();
        if (d is null) return (null, null, null);

        var rows = new List<(string Label, double Value)>();
        foreach (var (_, r) in d.Rows())
        {
            if (r.UnitUpper != "RPM") continue;

            var lab = r.LabelUpper;
            if (!lab.Contains("FAN", StringComparison.Ordinal)) continue;

            var v = r.Value;
            if (!(v > 0 && v < 12000)) continue;
            rows.Add((r.Label ?? "", v));
        }
        if (rows.Count == 0) return (null, null, null);

        double? cpu = null, gpu = null;
        // ⚠️ 记下**已经被归属走**的下标（2026-11-12 修）：补空位那一轮以前是无条件
        //    从 rows[0] 开始喂，于是 `["GPU Fan"=2400, "Fan1"=2500]` 这种顺序会把
        //    GPU 那一条再算给 CPU —— 界面两路转速一模一样、真正的 CPU 风扇永远不出现，
        //    正好违反上面"宁可只给一路，也不把同一路填两遍"的承诺。
        var used = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            var up = rows[i].Label.ToUpperInvariant();
            if (cpu is null && up.Contains("CPU", StringComparison.Ordinal)) { cpu = rows[i].Value; used[i] = true; continue; }
            if (gpu is null && up.Contains("GPU", StringComparison.Ordinal)) { gpu = rows[i].Value; used[i] = true; continue; }
        }

        // 没写归属的（Fan1 / Fan2）按顺序补空位
        for (var i = 0; i < rows.Count && (cpu is null || gpu is null); i++)
        {
            if (used[i]) continue;
            var v = rows[i].Value;
            if (cpu is null) { cpu = v; used[i] = true; continue; }
            if (gpu is null && Math.Abs(v - cpu.Value) > 0.5) { gpu = v; break; }
        }

        var src = "hwinfo:" + string.Join(" / ", rows.Select(x => x.Label));
        return (cpu, gpu, src);
    }

    // ================================================================ PL1 / PL2

    /// <summary>
    /// CPU 功耗墙 PL1 / PL2（W）。
    ///
    /// ⚠️ **优先取 Dynamic**：EC 的性能模式（APMD）改写的正是动态值 ——
    /// office 35W / game 55W / beast 110W。Static 恒为 80W 不随模式变
    /// （疑似出厂 MSR 默认值），拿它显示会三个模式一模一样。
    /// 某个档只有 Static 时用 Static；都没有就整个键不出现（绝不用估计值占位）。
    /// </summary>
    public (Dictionary<string, double>? Limits, string? Source) FindCpuPowerLimits()
    {
        var d = _frame();
        if (d is null) return (null, null);

        var dyn = new Dictionary<string, double>(StringComparer.Ordinal);
        var sta = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (group, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("CPU", StringComparison.Ordinal)) continue;
            if (!InUnit(r, "W")) continue;

            var lab = r.LabelUpper;
            if (!lab.Contains("POWER LIMIT", StringComparison.Ordinal)) continue;

            string which;
            if (lab.Contains("PL1", StringComparison.Ordinal)) which = "pl1";
            else if (lab.Contains("PL2", StringComparison.Ordinal)) which = "pl2";
            else continue;

            var v = r.Value;
            if (!(v > 1 && v < 500)) continue;     // 0 = 没读到

            if (lab.Contains("DYNAMIC", StringComparison.Ordinal)) dyn[which] = v;
            else if (lab.Contains("STATIC", StringComparison.Ordinal)) sta[which] = v;
        }

        var outLimits = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var k in (string[])["pl1", "pl2"])
        {
            if (dyn.TryGetValue(k, out var dv)) outLimits[k] = dv;
            else if (sta.TryGetValue(k, out var sv)) outLimits[k] = sv;
        }

        if (outLimits.Count == 0) return (null, null);
        var useDyn = dyn.ContainsKey("pl1") || dyn.ContainsKey("pl2");
        return (outLimits, $"hwinfo:PL1/PL2 Power Limit（{(useDyn ? "动态值" : "静态值")}）");
    }

    /// <summary>
    /// GPU 功耗墙（W）→ `{"now": 当前生效, "rated": 额定, "max": 最高}`。
    ///
    /// **第一路是 NVML**（2026-11-09 改）：`nvmlDeviceGetEnforcedPowerLimit` 给的就是
    /// 当前生效墙，随性能模式变（办公 83.23 / 游戏 114.50 / 狂暴 140 W，驱动 621.01 实测）。
    /// 旧版本这里直接读 HWiNFO 的 `GPU Power Limit (rated)/(max)`，那两条是 **vBIOS 常量**
    /// （70 / 140），三档一模一样 —— 界面把常量摆在每档下面，用户读到就是"办公模式满功耗"，
    /// 2026-11-09 报障。所以**能读生效墙就一定读**，常量只留给 tooltip 当规格说明。
    ///
    /// **第二路才是 HWiNFO**（NVML 不可用时：核显输出模式、没装驱动、老驱动缺接口）：
    /// `dGPU [#0]: NVIDIA ...` 组下 `GPU Power Limit (rated)` / `(max)`，
    /// 实测与 `nvidia-smi power.default_limit / max_limit` 一致（70 / 140 W）。
    /// 这条路上**没有 `now`** —— 缺这个键，上层就不拿它当生效墙显示。
    /// NVML 可用时 HWiNFO 只用来**补 `max`**（NVML 那条 Constraints 接口在本机会崩，
    /// 见 `NvmlSensors.ReadPowerLimits` 的注释）。
    ///
    /// ⚠️ 集显输出模式下没有 NVIDIA 组 ⇒ 返回 `(null, null)`，绝不拿核显组的读数滥竽充数。
    /// </summary>
    public (Dictionary<string, double>? Limits, string? Source) FindGpuPowerLimits()
    {
        // ---- 第一路：NVML（当前生效墙 + 硬件默认墙）----
        var (now, nvRated) = QControl.Hal.Sensors.GpuSensors.ReadPowerLimits();
        var outLimits = new Dictionary<string, double>(StringComparer.Ordinal);
        if (now is { } n1) outLimits["now"] = n1;
        if (nvRated is { } n2) outLimits["rated"] = n2;

        // ---- 第二路：HWiNFO（补硬件最高墙；NVML 完全不可用时它就是唯一来源）----
        var d = _frame();
        if (d is null)
            return outLimits.Count == 0
                ? (null, null)
                : (outLimits, "nvml:Power Management Limit");

        int bestPri = int.MaxValue;
        string? bestGroup = null;
        var best = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (group, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("GPU", StringComparison.Ordinal)) continue;
            if (!InUnit(r, "W")) continue;

            var lab = r.LabelUpper;
            if (!lab.Contains("POWER LIMIT", StringComparison.Ordinal)) continue;
            if (lab.Contains("THERMAL", StringComparison.Ordinal)) continue;

            var v = r.Value;
            if (!(v > 1 && v < 1000)) continue;

            // 独显组（NVIDIA/AMD）优先于可能出现的核显组
            var ug = d.SensorUpper(r.SensorIndex);
            var pri = ug.Contains("NVIDIA", StringComparison.Ordinal)
                   || ug.Contains("AMD", StringComparison.Ordinal) ? 0 : 1;

            if (bestGroup is null || pri < bestPri)
            {
                bestPri = pri;
                bestGroup = group;
                best.Clear();
            }
            if (!string.Equals(bestGroup, group, StringComparison.Ordinal)) continue;

            if (lab.Contains("RATED", StringComparison.Ordinal)
                || lab.Contains("DEFAULT", StringComparison.Ordinal)) best["rated"] = v;
            else if (lab.Contains("MAX", StringComparison.Ordinal)) best["max"] = v;
        }

        // 只补缺的键：NVML 的「当前生效墙」永远优先，HWiNFO 不能覆盖它
        foreach (var (k, v) in best)
            if (!outLimits.ContainsKey(k)) outLimits[k] = v;

        if (outLimits.Count == 0) return (null, null);

        var src = now is not null
            ? "nvml:Power Management Limit" + (best.ContainsKey("max") ? " + hwinfo:最高墙" : "")
            : $"hwinfo:{bestGroup} / GPU Power Limit";
        return (outLimits, src);
    }

    // ================================================================ 显存结温

    /// <summary>
    /// 显存结温（°C）—— **只有 HWiNFO 有**：`nvidia-smi -q -d TEMPERATURE` 的
    /// `Memory Current Temp` 与 NVML 对应接口本机都返回 N/A（驱动不暴露）。
    /// </summary>
    public (double? Value, string? Source) FindGpuMemTemp()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? best = null;
        var bestPri = int.MaxValue;
        string? label = null;

        foreach (var (group, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("GPU", StringComparison.Ordinal)) continue;
            if (!IsTempReading(r)) continue;

            var lab = r.LabelUpper;
            var keys = new[] { "MEMORY JUNCTION", "MEM JUNCTION", "VRAM", "MEMORY TEMPERATURE", "MEMORY TEMP", "GDDR" };
            if (!keys.Any(k => lab.Contains(k, StringComparison.Ordinal))) continue;

            var v = r.Value;
            if (!(v >= 10 && v <= 125)) continue;

            var pri = lab.Contains("JUNCTION", StringComparison.Ordinal) ? 0 : 1;
            if (pri >= bestPri) continue;
            bestPri = pri;
            best = v;
            label = r.Label;
        }
        return (best, best is null ? null : $"hwinfo:{label}");
    }

    // ================================================================ GPU 电压

    /// <summary>
    /// GPU 核心电压。**没有原生替代**：NVML 的 `nvml.dll` 里根本没有
    /// `nvmlDeviceGetVoltage`，`nvidia-smi` 也没有任何电压字段。
    ///
    /// ⚠️ **标签必须精确匹配**：GPU 组里单位是 V 的读数有一大票都像 ——
    /// `GPU MSVDD Voltage`（显存供电）、`GPU FBVDD Input Voltage` 19.45V（输入轨）、
    /// `GPU 12VHPWR Voltage`（输入轨）。所以要两道闸：标签含 `CORE VOLTAGE`
    /// + 数值落在 0.2~2.0 V。别写成"GPU 组里第一个 V 单位的行"。
    /// </summary>
    public (double? Value, string? Source) FindGpuVoltage()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? best = null;
        var bestPri = int.MaxValue;
        string? label = null;

        foreach (var (group, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("GPU", StringComparison.Ordinal)) continue;
            if (r.UnitUpper != "V") continue;

            var lab = r.LabelUpper;
            var v = r.Value;
            if (!(v > 0.2 && v < 2.0)) continue;

            int pri;
            if (lab == "GPU CORE VOLTAGE") pri = 0;
            else if (lab.Contains("CORE VOLTAGE", StringComparison.Ordinal)) pri = 1;
            else continue;

            if (pri >= bestPri) continue;
            bestPri = pri;
            best = v;
            label = r.Label;
        }
        return (best, best is null ? null : $"hwinfo:{label}");
    }

    // ================================================================ 电池功率（兜底）

    /// <summary>
    /// 电池充放电功率（W，**充电为正 / 放电为负**）—— 兜底路径，
    /// 首选是 `root\wmi BatteryStatus` 的 ChargeRate/DischargeRate。
    ///
    /// ⚠️ 判 label 时要先剔掉 "DISCHARGE" 再看有没有 "CHARGE" ——
    /// 因为 **"DISCHARGE" 里本身含子串 "CHARGE"**。
    /// </summary>
    public (double? Value, string? Source) FindBattRate()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? charge = null, dis = null, both = null;

        foreach (var (group, r) in d.Rows())
        {
            if (!d.SensorUpper(r.SensorIndex).Contains("BATTERY", StringComparison.Ordinal)) continue;

            var u = r.UnitUpper;
            if (u is not ("W" or "MW")) continue;

            var lab = r.LabelUpper;
            if (!lab.Contains("RATE", StringComparison.Ordinal) && !lab.Contains("POWER", StringComparison.Ordinal))
                continue;

            var v = r.Value;
            if (u == "MW") v /= 1000.0;

            var hasDis = lab.Contains("DISCHARGE", StringComparison.Ordinal);
            var hasChg = lab.Replace("DISCHARGE", "").Contains("CHARGE", StringComparison.Ordinal);

            if (hasDis && hasChg) both = v;
            else if (hasDis) dis = v;
            else if (hasChg) charge = v;
        }

        if (both is { } b && Math.Abs(b) >= 0.05) return (b, "hwinfo:Charge/Discharge Rate");
        if (dis is { } dd && Math.Abs(dd) >= 0.05) return (-Math.Abs(dd), "hwinfo:Discharge Rate");
        if (charge is { } c) return (c, "hwinfo:Charge Rate");
        return (null, null);
    }

    // ================================================================ 虚拟内存

    /// <summary>
    /// 虚拟内存（提交量）→ `{percent, used_gb}`。
    /// ⚠️ 业务路径**不该用这个** —— 界面走的是 `psapi.GetPerformanceInfo`
    /// （实测两者逐位一致，因为 HWiNFO 也是拿同一对内核数字算的）。
    /// 这里只作对照/排查。
    /// </summary>
    public (Dictionary<string, double?>? Value, string? Source) FindVmem()
    {
        var d = _frame();
        if (d is null) return (null, null);

        double? pct = null, usedMb = null;

        foreach (var (_, r) in d.Rows())
        {
            var lab = r.LabelUpper;
            if (!lab.Contains("VIRTUAL MEMORY", StringComparison.Ordinal)) continue;

            var u = r.UnitUpper;
            if (lab.Contains("LOAD", StringComparison.Ordinal) && u == "%") pct = r.Value;
            else if (lab.Contains("COMMITTED", StringComparison.Ordinal) && u == "MB") usedMb = r.Value;
        }

        if (pct is null && usedMb is null) return (null, null);
        return (new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["percent"] = pct is null ? null : Math.Round(pct.Value, 1),
            ["used_gb"] = usedMb is null ? null : Math.Round(usedMb.Value / 1024.0, 1),
        }, "hwinfo:Virtual Memory");
    }

    // ================================================================ 硬盘 SMART

    /// <summary>组名形如 `S.M.A.R.T.: YMTC PC41Q-1TB-B (YMA61T0RA260341DDK) [C:, D:, E:]`。</summary>
    /// <remarks>
    /// ⚠️ 命名组语法是 **`(?&lt;name&gt;…)`**，不是早先实现的 `(?P&lt;name&gt;…)` ——
    /// .NET 不认后者，会在**静态构造时**抛 RegexParseException，
    /// 整个 HwInfoReader 类型初始化失败（症状：读 PCH 直接崩，跟正则毫无关系的样子）。
    /// </remarks>
    private static readonly Regex SmartGroup = new(
        @"^S\.M\.A\.R\.T\.:\s*(?<model>.+?)\s*\((?<sid>[^)]*)\)\s*(?:\[(?<vols>[^\]]*)\])?\s*$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// 组内 label → 输出键。**只认完全相等**：`Drive Temperature 2..8` 是同一块盘的
    /// 其它传感器（主控 / 各 NAND 区），包含匹配会把它们全收进来当盘温。
    /// </summary>
    private static readonly Dictionary<string, string> SmartLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DRIVE TEMPERATURE"] = "temp",
        ["DRIVE REMAINING LIFE"] = "life",
        ["DRIVE AVAILABLE SPARE"] = "spare",
        ["TOTAL HOST WRITES"] = "writes_gb",
        ["TOTAL HOST READS"] = "reads_gb",
    };

    private static readonly Dictionary<string, (double Lo, double Hi)> SmartRange = new(StringComparer.Ordinal)
    {
        ["temp"] = (5.0, 125.0),
        ["life"] = (0.0, 100.0),
        ["spare"] = (0.0, 100.0),
        ["writes_gb"] = (0.0, 1e7),
        ["reads_gb"] = (0.0, 1e7),
    };

    private static readonly Dictionary<string, double> ByteUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GB"] = 1.0, ["TB"] = 1024.0, ["MB"] = 1.0 / 1024.0,
    };

    /// <summary>一块盘的 SMART 读数。</summary>
    public sealed class SmartRecord
    {
        /// <summary>HWiNFO 组名里的型号（可能比 WMI 的多几个字符）。</summary>
        public string Model { get; set; } = "";
        public string? Sid { get; set; }
        public List<string>? Volumes { get; set; }
        public double? Temp { get; set; }
        public double? Life { get; set; }
        public double? Spare { get; set; }
        public double? WritesGb { get; set; }
        public double? ReadsGb { get; set; }
    }

    /// <summary>
    /// 物理硬盘的 SMART 读数；HWiNFO 没出数时返回空列表。
    ///
    /// ⚠️ **每块盘一条，主键是「型号 + 组名里的 SID」**（2026-11-12 修）。以前主键只有型号：
    ///    两块同型号盘（双 NVMe 的笔记本、内置盘 + 同型号硬盘盒）会并成一条 ——
    ///    后一组的 temp/life/writes 覆盖前一组，`Sid`/`Volumes` 又只保留第一组的，
    ///    界面上两块盘显示一模一样的读数，其中一块是错的。
    /// ⚠️ **这几项原生确实拿不到**：`MSFT_StorageReliabilityCounter` 在本机是空的，
    /// NVMe 协议查询 / 温度属性 / 透传命令在本机存储栈上全被拒（2026-11-02 实测四条路，
    /// 详见 DETAILS「磁盘 SMART 为什么只能走 HWiNFO」）。HWiNFO 能读是靠它的**内核**驱动。
    /// ⚠️ **单位别硬比字符串**：HWiNFO 给温度的单位不是 `°C`，解析出来是 `'¡æ'`
    /// （它用了非 UTF-8 的单字节度符号）。判"是不是温度"一律走类型标记 `dwReadingType=1`。
    /// </summary>
    public List<SmartRecord> FindDriveSmart()
    {
        var outList = new List<SmartRecord>();
        var byKey = new Dictionary<string, SmartRecord>(StringComparer.OrdinalIgnoreCase);
        var d = _frame();
        if (d is null) return outList;

        foreach (var (group, r) in d.Rows())
        {
            var m = SmartGroup.Match(group ?? "");
            if (!m.Success) continue;

            var model = m.Groups["model"].Value.Trim();
            var sid = m.Groups["sid"].Value.Trim();
            if (model.Length == 0 && sid.Length == 0) continue;

            var key = model + "|" + sid;
            if (!byKey.TryGetValue(key, out var rec))
            {
                byKey[key] = rec = new SmartRecord { Model = model, Sid = sid.Length > 0 ? sid : null };
                outList.Add(rec);
            }

            if (rec.Volumes is null && m.Groups["vols"].Value.Length > 0)
                rec.Volumes = m.Groups["vols"].Value
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToList();

            var lab = r.LabelUpperTrim;
            if (!SmartLabels.TryGetValue(lab, out var k)) continue;

            var v = r.Value;
            switch (k)
            {
                case "temp":
                    if (!IsTempReading(r)) continue;
                    break;
                case "life":
                case "spare":
                    if (!InUnit(r, "%")) continue;
                    break;
                default:
                    if (!ByteUnits.TryGetValue(UnitOf(r), out var scale)) continue;
                    v *= scale;
                    break;
            }

            var (lo, hi) = SmartRange[k];
            if (!(v >= lo && v <= hi)) continue;

            var rounded = Math.Round(v, 1);
            switch (k)
            {
                case "temp": rec.Temp = rounded; break;
                case "life": rec.Life = rounded; break;
                case "spare": rec.Spare = rounded; break;
                case "writes_gb": rec.WritesGb = rounded; break;
                case "reads_gb": rec.ReadsGb = rounded; break;
            }
        }
        return outList;
    }

    // ================================================================ 全部温度

    /// <summary>全部温度读数（调试/界面兜底用）。</summary>
    public List<(string Group, string Label, double Value)> ListTemps(int limit = 200)
    {
        var list = new List<(string, string, double)>();
        var d = _frame();
        if (d is null) return list;

        foreach (var (group, r) in d.Rows())
        {
            // 与 find_pch 用同一个判据（类型标记优先，类型缺失才退回单位），
            // 否则 "type=Other 但单位写着 °C" 的字段（距离/偏移类）会被误收
            if (!IsTempReading(r)) continue;
            if (IsDistanceReading(r.LabelUpper)) continue;   // 距 TjMax 的余量不是温度
            list.Add((group, r.Label, r.Value));
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>
    /// 与 <see cref="ListTemps"/> 同一判据，但把 `unit` / `type` 一起带出来 ——
    /// 诊断口要看这两个原始字段（判「为什么这条没被认成温度」时就靠它们）。
    /// </summary>
    public List<(string Group, string Label, double Value, string Unit, int Type)>
        ListTempsDetailed(int limit = 200)
    {
        var list = new List<(string, string, double, string, int)>();
        var d = _frame();
        if (d is null) return list;

        foreach (var (group, r) in d.Rows())
        {
            if (!IsTempReading(r)) continue;
            if (IsDistanceReading(r.LabelUpper)) continue;   // 与 ListTemps 同一个口径
            list.Add((group, r.Label, r.Value, UnitOf(r), r.Type));
            if (list.Count >= limit) break;
        }
        return list;
    }
}
