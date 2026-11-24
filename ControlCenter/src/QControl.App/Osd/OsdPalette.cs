using QControl.App.Ui;

namespace QControl.App.Osd;

/// <summary>
/// OSD 提示条的两套配色。底板/文字/描边直接取 <see cref="ThemeHex"/>（与界面、托盘菜单
/// 同一份真源），只有"强调色"是 OSD 自己的一套 —— 卡片很小，界面的青色在上面显不出层次。
///
/// ⚠️ 2026-10-24 之前 OSD 只有一套写死的深色，浅色模式下屏幕左上角突然黑一块，
///    用户报「OSD 外观不会跟随白天黑夜模式」。现在 <see cref="Resolve"/> **每次弹出时现算**，
///    与 <c>Tray/TrayPalette</c> 同一套路子：切完主题立刻按 FN，出来的就是新色，
///    不需要谁去推送，auto 下系统主题变了也不用监听。
/// </summary>
internal sealed record OsdPalette(
    Rgb BgTop,      // 底板渐变起点
    Rgb BgBottom,   // 底板渐变终点
    Rgb Bar,        // 左侧竖条 + 图标（强调色）
    Rgb Fg,         // 标题
    Rgb Fg2,        // 副标题
    Rgb Edge,       // 1px 内描边
    byte EdgeAlpha,
    byte BadgeAlpha)  // 图标徽章底色（强调色叠加的透明度）
{
    /// <summary>暗色：沿用 OSD 原来的深色卡片（黑底白字 + 亮蓝强调）。</summary>
    public static readonly OsdPalette Dark = new(
        BgTop: Rgb.Hex("#202430"),
        BgBottom: Rgb.Hex("#11131a"),
        Bar: Rgb.Hex("#60a5fa"),
        Fg: Rgb.Hex("#ffffff"),
        Fg2: Rgb.Hex("#98a2ba"),
        Edge: Rgb.Hex("#ffffff"),
        EdgeAlpha: 26,
        BadgeAlpha: 46);

    /// <summary>
    /// 浅色：白卡片 → <see cref="ThemeHex.LightCardBottom"/>，文字用界面主字色，
    /// 描边直接用界面那条分割线。强调色在 #60a5fa → #2f7fd6 压深一档，
    /// 否则白底上（尤其徽章底色只有 38/255）几乎看不见。
    /// </summary>
    public static readonly OsdPalette Light = new(
        BgTop: Rgb.Hex(ThemeHex.LightPanel),
        BgBottom: Rgb.Hex(ThemeHex.LightCardBottom),
        Bar: Rgb.Hex("#2f7fd6"),
        Fg: Rgb.Hex(ThemeHex.LightTxt),
        Fg2: Rgb.Hex("#5d6979"),
        Edge: Rgb.Hex(ThemeHex.LightLine),
        EdgeAlpha: 255,
        BadgeAlpha: 38);

    /// <summary>
    /// token（<c>presets.ui.theme</c>）→ 此刻该用的那套色。auto 由 <see cref="ThemeIntent"/>
    /// 按注册表现算，**不缓存**。
    /// </summary>
    public static OsdPalette Resolve(string? token) => ThemeIntent.IsLight(token) ? Light : Dark;
}
