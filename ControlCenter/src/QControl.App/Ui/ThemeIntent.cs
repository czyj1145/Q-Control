using Microsoft.Win32;

namespace QControl.App.Ui;

/// <summary>主题三态 —— 与 <c>presets.ui.theme</c> 的三个值逐字对应（dark / light / auto）。</summary>
internal enum ThemeMode
{
    Dark,
    Light,
    Auto,
}

/// <summary>
/// "用户要的是暗还是亮"这件事的**唯一判定口**：token 解析 + auto 跟随系统。
///
/// ⚠️ 这个文件里**不许出现任何 WPF 类型**（Color / Brush / ResourceDictionary）。
///    托盘常驻态（没开窗口）实测 77 MB，靠的就是那些程序集根本没进进程；托盘右键菜单
///    也要按主题上色，如果为了读一句 theme 去调 <c>UiTheme</c>，光取它的静态字段就会
///    把 UiPalette → System.Windows.Media 一路加载进来（工作集直接翻倍）。
///    所以"意图解析"和"生效配色"拆开：这里只认字符串和注册表，
///    <see cref="UiTheme"/> 那边才有 WPF 画笔。
///
/// 跟随系统读的是注册表 <c>AppsUseLightTheme</c>（0=暗 1=亮），不是
/// SystemParameters —— 后者在 Win10/11 上对"应用主题"和"系统主题"是混的。
/// </summary>
internal static class ThemeIntent
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static ThemeMode Parse(string? token) => token switch
    {
        "light" => ThemeMode.Light,
        "auto" => ThemeMode.Auto,
        _ => ThemeMode.Dark,     // 读不到 / 非法值一律暗色（CSS 也是"不挂 data-theme = 暗"）
    };

    public static string Token(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Auto => "auto",
        _ => "dark",
    };

    /// <summary>auto 到底算哪一边（auto 以外不看注册表）。</summary>
    public static bool IsLight(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => true,
        ThemeMode.Auto => SystemIsLight(),
        _ => false,
    };

    /// <summary>一句 token（model.json 里那个字符串）→ 此刻该用浅色吗。任意线程可调。</summary>
    public static bool IsLight(string? token) => IsLight(Parse(token));

    public static bool SystemIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;        // 读不到就当暗色（不能因为读注册表失败把界面刷成白的）
        }
    }
}
