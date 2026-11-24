using System.Runtime.CompilerServices;

namespace QControl.App.Ui;

/// <summary>
/// **界面全部入口的隔离层**（2026-11-02 加）。
///
/// 为什么要这一层：`Program.Main` 里只要**出现**对 UI 类型的调用，JIT 编译 Main 时就会
/// 顺着被调方法的签名把 `MainWindow` / `UiTheme` 一路解析出来，于是
/// `PresentationFramework` + `PresentationCore` + `WindowsBase` + `System.Xaml`
/// **在进程启动那一刻就进了内存** —— 一次窗口都没开过也一样。
/// 实测（`/api/debug/mem` 的 `modules` 段）：托盘态启动 0.3 秒时这四份就已经在里面。
/// 也就是说 csproj 里"没开过窗口时对工作集几乎零成本"那句话一直不成立。
///
/// 现在 Main 只碰本类：每个方法都标 `NoInlining`，JIT 编译 Main 时最多解析到这里的
/// 签名（`AppState` / `string` / `int`，全是无 WPF 的类型），真正的 UI 方法要等
/// **第一次真的开窗/截图**才被 JIT，WPF 程序集也就等到那一刻才加载。
/// 托盘常驻一整晚都不开窗的用户，这四份程序集一辈子不会进进程。
///
/// ⚠️ 新增界面入口一律加在这里，**不要**在 Program 里直接调 `UiHost` / `UiShot`。
/// ⚠️ 本文件不许 `using System.Windows.*` —— 一写就前功尽弃（JIT 会把它们拉回来）。
/// </summary>
internal static class UiEntry
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Show(AppState? state, string? page) => UiHost.Show(state, page);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Close() => UiHost.Close();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool IsOpen() => UiHost.IsOpen;

    // ------------------------------------------------ 诊断/自检（同样是界面，同样按需加载）

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ReopenSelfTest() => SelfTest.UiReopenSelfTest.Run();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int RefreshSelfTest() => SelfTest.UiRefreshSelfTest.Run();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ActionsSelfTest(AppState? state) => SelfTest.UiActionsCheck.Run(state);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int FitSelfTest(AppState? state, int width, int height)
        => SelfTest.UiFitCheck.Run(state, width, height);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Shot(AppState? state, string path, string? page, string? theme,
                           int width, int height, int waitMs, string? dialog, bool flip)
        => SelfTest.UiShot.Run(state, path, page, theme, width, height, waitMs, dialog, flip);
}
