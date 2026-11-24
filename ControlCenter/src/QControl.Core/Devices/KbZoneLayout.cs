namespace QControl.Core.Devices;

/// <summary>
/// 键盘示意图上"某个横坐标属于哪一区"的切法 —— 纯几何规则，不碰 EC、不碰界面。
///
/// 为什么从 <c>KeyboardView</c> 里搬出来：这是**原厂实拍的分界**，不是画法。
/// 界面那边随时会为了好看改坐标/比例，而这两条线改一次就意味着"灯亮错一格"，
/// 所以它得有单测看着（2026-11-17 用户逐键核对："F5 是一区的，截图键是二区的"）。
///
/// 坐标单位是 u：主键区从左边缘起 15u，小键盘再占 4u，整键盘 19u。
/// </summary>
public static class KbZoneLayout
{
    /// <summary>主键区宽度（单位 u）。小键盘/方向键不算在内 —— 它们固定是最后一区。</summary>
    public const double MainWidthU = 15;

    /// <summary>
    /// 绿色区（A）的右边界 / 红色区（C）的左边界，单位 u，适用于**除 F 行以外**的各行。
    ///
    /// 来源：原厂灯光参考图（2026-11-17 用户提供，绿/蓝/红三色实拍分界）。
    ///   · 绿→蓝 落在第 6 列键的**中间**：F 键（中心 5.25u）整颗还在绿里，
    ///     5 / T / G / V（中心 5.5~6.25u）已经变蓝 ⇒ 边界 5.35，且必须用 `<` 判断
    ///     （写成 `<= 5.5` 会把 5 / T 一起划进绿灯）。
    ///   · 蓝→红 在 13u 附近：`]`（中心 13.0u）仍是蓝，`\` / Backspace / Enter / 右 Shift
    ///     （中心 ≥13.8u）全红，13.5 正好把两类分开。
    /// </summary>
    public const double ZoneAMax = 5.35, ZoneCMin = 13.5;

    /// <summary>
    /// F 行（`Rows[0]`）自己那两条分界 —— 与下面几行**不同**，所以单列。
    ///
    /// 用户对着原厂图逐键核对：「F5 是一区的，截图键是二区的」：
    /// 绿色一直包到 F5（F5 中心 5.5u），蓝色从 F6 起、一直包到截图键 PrtSc（中心 13.5u），
    /// 红色从 Del 起（14u 之后）。下面几行同一列（5 / T）是蓝的，所以这里各宽一格。
    /// </summary>
    public const double ZoneAFMax = 6, ZoneCFMin = 14;

    /// <summary>
    /// 主键区里某个横坐标（中心，单位 u）属于哪一区（0 = A 绿 / 1 = B 蓝 / 2 = C 红）。
    ///
    /// 三区按原厂实拍的切法（F 行另有 <see cref="ZoneAFMax"/>）；
    /// 其它分区数（只有 1 / 2 / 4 真实存在）按 0~15u 等分 —— 原厂只给了地址、没给物理位置，
    /// 等分是示意图上唯一不撒谎的画法（tab 提示里会写明"位置按色彩数均分示意"）。
    /// </summary>
    /// <param name="centerU">键帽中心的横坐标（u）。</param>
    /// <param name="zones">分区数。</param>
    /// <param name="topRow">这一行是不是 F 行（它比下面几行各宽一格）。</param>
    public static int ZoneOf(double centerU, int zones, bool topRow = false)
    {
        if (zones <= 1) return 0;

        if (zones == 3)
        {
            var aMax = topRow ? ZoneAFMax : ZoneAMax;
            var cMin = topRow ? ZoneCFMin : ZoneCMin;
            return centerU < aMax ? 0 : centerU >= cMin ? 2 : 1;
        }

        var band = (int)Math.Floor(centerU / MainWidthU * zones);
        return Math.Clamp(band, 0, zones - 1);
    }
}
