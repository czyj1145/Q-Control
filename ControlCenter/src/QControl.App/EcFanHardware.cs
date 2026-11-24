using System.Text.Json.Nodes;
using QControl.Core.FanCurve;
using QControl.Hal.Ec;

namespace QControl.App;

/// <summary>
/// 把 <see cref="EcHal"/> 接到曲线引擎的 <see cref="IFanHardware"/> 上。
///
/// 这一层只做三件事，别的都不做：
///   ① 把 Hal 的 `WriteDeniedException` 翻成 Core 的 `FanWriteDeniedException`
///      —— Core 不认识 Hal 的类型，而"确定性失败必须自停"这条语义得跨过边界；
///   ② 把 `JsonObject` 快照里的温度取成 `double?`（Hal 不暴露强类型读数的原因见 EcHal）；
///   ③ 缓存 `EnvInfo()`（里面要查令牌，不必每次 Start 都跑一遍）。
/// </summary>
internal sealed class EcFanHardware(EcHal ec) : IFanHardware
{
    private JsonObject? _env;

    public bool WritesEnabled => ec.WritesEnabled;
    public bool WritesAllowed => ec.WritesAllowed;

    public bool AdminOk
    {
        get
        {
            _env ??= ec.EnvInfo();
            return _env["write_ok"]?.GetValue<bool>() ?? false;
        }
    }

    public string? AdminHint
    {
        get
        {
            _env ??= ec.EnvInfo();
            return _env["hint"] is System.Text.Json.Nodes.JsonValue v
                   && v.TryGetValue<string>(out var s) ? s : null;
        }
    }

    public int? DutyApplied => ec.DutyApplied;

    /// <summary>临时满转态（未开自定义风扇时点了「最大转速」，见 EcHal.SetOemFanMax）。</summary>
    public bool OemFanMax => ec.OemFanMax;

    public void SetFans(int? cpu, int? gpu, int? sys = null)
    {
        try
        {
            // ⚠️ 两风扇机上 SYS 槽**不归我们管**（2026-11-26 审查）：
            //    `presets.fan.debug_sys_curve` + `curve_sys` 只是让这条曲线能编辑、能保存
            //    （见 FanCurveEngine.State 的说明），不值得也不该把 UFA3 真的写下去。
            //    两风扇机 0x0207 的 a2 恰好镜像 GPU 温度，很容易被判成"本机有 SYS 温度"，
            //    于是调试曲线会真的给一个不存在的风扇下发目标转速。
            //    传 null 由 EcHal 按原厂语义写 255（= 忽略这一路）。
            if (ec.Fans < 3) sys = null;

            // 三路分别对应 UFA1/UFA2/UFA3（DSDT：UFA1=GWD0、UFA2=GWD2、UFA3=GWD3，
            // 机型档案 fan_slots=["d0","a","d2"] 与之一一对应）。
            ec.SetFans(cpu, gpu, sys);
        }
        catch (WriteDeniedException e)
        {
            throw new FanWriteDeniedException(e.Message);
        }
    }

    /// <summary>
    /// 交回 EC 固件：`0x0206=0` + 三路 UFA=0xFF（顺序由 <c>EcHal.ReleaseToEc</c> 保证）。
    /// 被拒时翻成 Core 的异常类型，曲线那边才知道这是"确定性失败"、不该重试。
    /// </summary>
    public void ReleaseToEc()
    {
        try
        {
            ec.ReleaseToEc();
        }
        catch (WriteDeniedException e)
        {
            throw new FanWriteDeniedException(e.Message);
        }
    }

    public (double? Cpu, double? Gpu) Temps(TimeSpan maxAge)
    {
        var st = ec.CachedStatus(maxAge);
        if (st is null) return (null, null);
        return (Pick(st, "cpu_temp"), Pick(st, "gpu_temp"));
    }

    /// <summary>
    /// 第三路（SYS）温度：读快照里的 `sys_temp`（来自 `0x0207` 的 a2，见 EcHal.ReadStatus）。
    ///
    /// <para>
    /// 偏移依据是 FanControlPortable v3 的 `ReadSysInfo`（2026-11-18 反编译核对）：
    /// `0x0207` 的 **a2 = 温度（1 字节）/ a3 = 转速（2 字节）**。它只对三风扇机型读这条，
    /// 我们同样只在 `ec.fans >= 3` 时才让 EcHal 去读。
    /// </para>
    /// <para>
    /// ⚠️ 2026-11-18 在本机（两风扇 NL5）用 raw 探针看到 a2 恒等于 GPU 温度 ——
    /// 那是**两风扇机上这条命令没有 SYS 语义**的表现（参考实现也从不在这类机器上读它），
    /// 不能据此推断三风扇机上的 a2 是假的。所以这里不做"像不像 GPU"的猜测，
    /// 只信任量程校验（EcHal 里 0~125 ℃）；两风扇机型拿不到这个键，自然返回 null。
    /// </para>
    /// </summary>
    public double? SysTemp(TimeSpan maxAge)
    {
        var st = ec.CachedStatus(maxAge);
        return st is null ? null : Pick(st, "sys_temp");
    }

    private static double? Pick(JsonObject st, string key)
    {
        if (st[key] is not System.Text.Json.Nodes.JsonValue v) return null;
        if (v.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        // ⚠️ ToJsonString + parse：TryGetValue<double> 不跨 Int32/Double 转换
        return double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
