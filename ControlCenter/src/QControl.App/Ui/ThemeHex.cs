namespace QControl.App.Ui;

/// <summary>
/// **两套主题的色号真源**：这里只放"不止一个绘制面要用"的那几个令牌。
///
/// 为什么值得单开一个文件：同一个颜色现在有三个使用者 ——
/// ① WPF 界面（<see cref="UiPalette"/> 把它们翻成 Brush）；
/// ② 托盘右键菜单（<c>Tray/TrayPalette</c>，纯 GDI 自绘，碰不了 WPF 的 Color）；
/// ③ OSD 提示条（<c>Osd/OsdPalette</c>，同样是自绘位图）。
/// 各抄一份十六进制，改色时必然漂移。
/// 于是把色号钉在一处，谁用谁解析：WPF 侧 <c>ColorConverter</c>，GDI 侧自己拆 RGB。
///
/// ⚠️ 本文件**不许**写 using System.Windows.* —— 托盘线程会读它，一碰 WPF 就白省了内存。
/// ⚠️ 键名照 CSS 变量起（Panel ↔ --panel、CardBottom ↔ 卡片渐变末端），**本文件即唯一真源**。
/// </summary>
internal static class ThemeHex
{
    // 暗色
    public const string DarkPanel = "#141821";
    public const string DarkCardBottom = "#11151d";
    public const string DarkLine = "#252c3a";
    public const string DarkTxt = "#e6ebf5";
    public const string DarkAcc = "#4dd6ff";

    // 浅色
    public const string LightPanel = "#ffffff";
    public const string LightCardBottom = "#f4f7fc";
    public const string LightLine = "#ccd5e4";
    public const string LightTxt = "#1b2230";
    public const string LightAcc = "#0f86cc";
}

/// <summary>
/// 裸 RGB（0..255）。**故意不用 <c>System.Windows.Media.Color</c>**：那一下就把 WPF 拉进进程了。
///
/// 放在 ThemeHex 旁边是因为它俩是同一类东西 —— "不带 WPF 的配色管道"，托盘菜单和 OSD
/// 都靠它取色（<c>Rgb.Hex(ThemeHex.DarkPanel)</c>）。
/// </summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>从 <c>#rrggbb</c> 解出来（自己拆，不走 ColorConverter）。</summary>
    public static Rgb Hex(string hex)
    {
        if (hex.Length < 7) return default;
        return new Rgb(Convert.ToByte(hex.Substring(1, 2), 16),
                       Convert.ToByte(hex.Substring(3, 2), 16),
                       Convert.ToByte(hex.Substring(5, 2), 16));
    }
}
