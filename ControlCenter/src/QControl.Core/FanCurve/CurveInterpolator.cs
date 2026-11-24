using QControl.Core.Model;

namespace QControl.Core.FanCurve;

/// <summary>曲线上的一个点。温度用 double，转速用 int（EC 只接受整数 duty）。</summary>
public readonly record struct CurvePoint(double T, int D);

/// <summary>
/// 风扇曲线的校验与插值 —— 纯函数，无状态。
///
/// 三件事：曲线归一化（排序去重加端点）、按温度插值、越界钳位。
/// 这段代码是散热安全的最后一道算术关卡，**一个字都不许"顺手优化"**：
/// 插值错了就是转速错了，转速错了就是过热或者噪音。
/// </summary>
public static class CurveInterpolator
{
    /// <summary>
    /// 曲线点温区的硬边界。超出即夹住 —— 不是拒绝，是夹住（与早先实现一致）。
    /// 曲线允许的温度范围比"读数是否可信"（1~115）窄，因为 20 以下/110 以上
    /// 没有调曲线的意义。
    /// </summary>
    public const double PointTempMin = 20;
    public const double PointTempMax = 110;

    /// <summary>
    /// 校验 + 规范化一条曲线：温度夹到 20~110、转速过 fan_duty 边界、按温度升序。
    /// </summary>
    /// <exception cref="ArgumentException">点不是 {t, d} 形状或数值非法。</exception>
    public static List<CurvePoint> Normalize(IEnumerable<CurvePoint>? points)
    {
        var outList = new List<CurvePoint>();
        if (points is null) return outList;

        foreach (var p in points)
        {
            if (double.IsNaN(p.T) || double.IsInfinity(p.T))
                throw new ArgumentException("曲线点必须是 {t, d} 对象");

            outList.Add(new CurvePoint(
                Math.Max(PointTempMin, Math.Min(PointTempMax, p.T)),
                // ⚠️ 这里用的是**全局 fan_duty 边界**（30~100），
                //    不是配置里的 min_duty/max_duty —— 两者不是一回事，别合并。
                Bounds.Clamp("fan_duty", p.D)));
        }

        outList.Sort((a, b) => a.T.CompareTo(b.T));
        return outList;
    }

    /// <summary>
    /// 线性插值求目标转速。
    ///
    /// 四个分支的顺序是有意义的，别重排：
    /// 空曲线 → 下限；温度在首点以下 → 首点转速；在末点以上 → 末点转速；
    /// 否则找到跨住 temp 的那一段做线性插值。
    /// </summary>
    public static int Interp(IReadOnlyList<CurvePoint>? curve, double temp, int minDuty, int maxDuty)
    {
        var pts = curve is null
            ? []
            : curve.OrderBy(p => p.T).ToList();

        if (pts.Count == 0)
            return ClampDuty(minDuty, minDuty, maxDuty);

        if (temp <= pts[0].T)
            return ClampDuty(pts[0].D, minDuty, maxDuty);
        if (temp >= pts[^1].T)
            return ClampDuty(pts[^1].D, minDuty, maxDuty);

        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (a.T <= temp && temp <= b.T)
            {
                // 跨距为 0（两点同温）时按 1e-6 兜底，避免除零。
                // 与早先实现 `max(1e-6, b["t"] - a["t"])` 一致。
                var span = Math.Max(1e-6, b.T - a.T);
                var r = (temp - a.T) / span;
                return ClampDuty(a.D + (b.D - a.D) * r, minDuty, maxDuty);
            }
        }
        return ClampDuty(pts[^1].D, minDuty, maxDuty);
    }

    /// <summary>
    /// 按**配置的** min/max 钳制 duty（不是全局 30~100）。
    /// 舍入用银行家舍入（.NET 的 `MidpointRounding.ToEven`）。
    /// </summary>
    public static int ClampDuty(double value, int minDuty, int maxDuty)
        => Math.Max(minDuty, Math.Min(maxDuty, (int)Math.Round(value, MidpointRounding.ToEven)));
}
