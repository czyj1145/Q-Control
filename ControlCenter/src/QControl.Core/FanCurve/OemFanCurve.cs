using System.Globalization;

namespace QControl.Core.FanCurve;

/// <summary>原厂 UserFan 解析结果：CPU / GPU / SYS 各自的 8 点占空比（温度固定见 <see cref="OemFanCurve.Temperatures"/>）。</summary>
public sealed record OemCurveDuties(int[] Cpu, int[] Gpu, int[]? Sys);

/// <summary>
/// 原厂 UserFan 字符串的双向算术（纯函数，不碰注册表）。
///
/// 格式来自原厂 daemon 实证（docs/PROTOCOL.md §七·补⑥）：
///   · `UserFan1/2/3` = "c0,c1,…,c7;g0,g1,…,g7"（分号前 CPU、后 GPU）
///   · `UserFan1_SYS/2_SYS/3_SYS` = "s0,s1,…,s7"
///   · 8 个档位的温度是固件固定的 <see cref="Temperatures"/> = 30/40/…/100 °C。
///
/// 写方向（<see cref="Sample"/>）把我们的曲线采样成原厂 8 点；
/// 读方向（<see cref="TryParseUserFan"/> / <see cref="TryParseDuties"/> / <see cref="ToCurve"/>）
/// 把原厂 8 点还原成我们的 {t,d} 曲线，供「读原厂曲线到三档」用。
/// </summary>
public static class OemFanCurve
{
    /// <summary>原厂 `Default_Temperature[8]`（从 daemon 的 FieldRVA 数据解出，不是猜的）。</summary>
    public static readonly int[] Temperatures = [30, 40, 50, 60, 70, 80, 90, 100];

    /// <summary>按原厂 8 个温度采样一条曲线。空曲线 = 每档都给 minDuty（安全下限）。</summary>
    public static int[] Sample(IReadOnlyList<CurvePoint>? curve, int minDuty, int maxDuty)
    {
        var result = new int[Temperatures.Length];
        for (var i = 0; i < Temperatures.Length; i++)
            result[i] = CurveInterpolator.Interp(curve, Temperatures[i], minDuty, maxDuty);
        return result;
    }

    /// <summary>SYS 没配独立曲线时跟最热的一路（逐温度取 max），与 EcHal 的兜底规则一致。</summary>
    public static int[] FollowHotter(int[] cpu, int[] gpu)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(gpu);
        if (cpu.Length != Temperatures.Length || gpu.Length != Temperatures.Length)
            throw new ArgumentException($"CPU / GPU 采样数必须都是 {Temperatures.Length}");

        var result = new int[Temperatures.Length];
        for (var i = 0; i < result.Length; i++) result[i] = Math.Max(cpu[i], gpu[i]);
        return result;
    }

    /// <summary>原厂 UserFanN 的合并串。</summary>
    public static string UserFanString(int[] cpu, int[] gpu)
        => string.Join(",", cpu) + ";" + string.Join(",", gpu);

    /// <summary>原厂 UserFanN_SYS 的单段串。</summary>
    public static string SysFanString(int[] sys)
        => string.Join(",", sys);

    // ================================================================ 读方向

    /// <summary>
    /// 解析 "c0,…,c7;g0,…,g7"。严格：必须两段、各 8 个 0~100 整数，否则整体失败。
    /// 原厂可能写 0 表示"这一档不转"（= 没配曲线），调用方据此跳过该档，别当有效曲线导入。
    /// </summary>
    public static bool TryParseUserFan(string? text, out int[] cpu, out int[] gpu, out string? error)
    {
        cpu = [];
        gpu = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "原厂 UserFan 串为空";
            return false;
        }

        var parts = text.Split(';');
        if (parts.Length != 2)
        {
            error = $"原厂 UserFan 段数应为 2（CPU;GPU），实际 {parts.Length}";
            return false;
        }
        if (!TryParseDuties(parts[0], out cpu, out var e1))
        {
            error = e1;
            return false;
        }
        if (!TryParseDuties(parts[1], out gpu, out var e2))
        {
            error = e2;
            return false;
        }
        return true;
    }

    /// <summary>解析单段 8 个 0~100 整数（SYS 串 / UserFan 的其中一段）。</summary>
    public static bool TryParseDuties(string? text, out int[] duty, out string? error)
    {
        duty = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "原厂占空比串为空";
            return false;
        }

        var parts = text.Split(',');
        if (parts.Length != Temperatures.Length)
        {
            error = $"原厂占空比应有 {Temperatures.Length} 档，实际 {parts.Length}";
            return false;
        }

        var values = new int[Temperatures.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var s = parts[i].Trim();
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                || v is < 0 or > 100)
            {
                error = $"原厂占空比第 {i + 1} 档非法：{s}";
                return false;
            }
            values[i] = v;
        }
        duty = values;
        return true;
    }

    /// <summary>把原厂 8 点占空比还原成我们的 {t,d} 曲线（温度取固定的 30/40/…/100）。</summary>
    public static List<CurvePoint> ToCurve(IReadOnlyList<int> duty)
    {
        if (duty.Count != Temperatures.Length)
            throw new ArgumentException($"占空比点数必须是 {Temperatures.Length}");
        var pts = new List<CurvePoint>(Temperatures.Length);
        for (var i = 0; i < Temperatures.Length; i++)
            pts.Add(new CurvePoint(Temperatures[i], duty[i]));
        return CurveInterpolator.Normalize(pts);
    }

    /// <summary>整条曲线全 0 = 原厂没配（不是"真的要 0% 转"）—— 导入时跳过这一档。</summary>
    public static bool AllZero(IReadOnlyList<int> duty) => duty.Count > 0 && duty.All(v => v == 0);
}
