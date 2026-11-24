using QControl.App.Ui;

namespace QControl.App.Tray;

/// <summary>
/// 托盘右键菜单的两套配色。色号全部来自 <see cref="ThemeHex"/>（与界面同一份真源），
/// 这里只负责"暗/亮各取哪几个 + 阴影多重"。
///
/// 为什么菜单要自绘而不是给系统菜单加暗色：原生弹出菜单是 DWM/主题说了算，
/// 只有"跟随 Windows"一条路（undocumented SetPreferredAppMode），既跟不了本软件的
/// 三态设置，浅色/深色来回切也不可靠。既然界面全是自绘的，菜单也自绘，
/// 两套色号就完全一致 —— 这跟 OSD 卡片、托盘图标同一套做法。
/// </summary>
internal sealed record TrayPalette(
    Rgb BgTop, Rgb BgBottom, Rgb Line, Rgb Txt, Rgb Acc, byte ShadowAlpha)
{
    /// <summary>暗色：卡片底 Panel → CardBottom，描边 Line。</summary>
    public static readonly TrayPalette Dark = new(
        BgTop: Rgb.Hex(ThemeHex.DarkPanel),
        BgBottom: Rgb.Hex(ThemeHex.DarkCardBottom),
        Line: Rgb.Hex(ThemeHex.DarkLine),
        Txt: Rgb.Hex(ThemeHex.DarkTxt),
        Acc: Rgb.Hex(ThemeHex.DarkAcc),
        ShadowAlpha: 5);         // 暗底上阴影要很轻，重了就是一圈脏

    /// <summary>浅色：纯白卡片 → #f4f7fc，描边 #ccd5e4。</summary>
    public static readonly TrayPalette Light = new(
        BgTop: Rgb.Hex(ThemeHex.LightPanel),
        BgBottom: Rgb.Hex(ThemeHex.LightCardBottom),
        Line: Rgb.Hex(ThemeHex.LightLine),
        Txt: Rgb.Hex(ThemeHex.LightTxt),
        Acc: Rgb.Hex(ThemeHex.LightAcc),
        ShadowAlpha: 6);

    /// <summary>
    /// token（model.json 的 <c>presets.ui.theme</c>）→ 此刻该用的那套色。
    /// auto 在这里就按注册表算成暗/亮，**不缓存** —— 菜单每次弹出都现算，
    /// 所以"切完主题立刻右键"就是新色，系统主题在 auto 下变了也不用谁去推送。
    /// </summary>
    public static TrayPalette Resolve(string? token) => ThemeIntent.IsLight(token) ? Light : Dark;
}
