using System.Windows;
using System.Windows.Media;

namespace QControl.App.Ui;

/// <summary>
/// 主题解析：**auto 不落盘**（存的是"跟随系统"这个意图），生效色在这里现算。
/// 真源是 model.json 的 <c>presets.ui.theme</c>，WPF 没有刷新闪白这回事，
/// 只读它一份就够。
///
/// ⚠️ token 解析与"跟随系统"的判定在 <see cref="ThemeIntent"/> 里（那边不含 WPF 类型，
///    托盘菜单要用）。这里只管把意图翻成 <see cref="UiPalette"/> 并重灌共享字典。
/// ⚠️ 系统切换时没有事件回调（不想为这点小事挂 SystemEvents 那条 STA 线程），
///    改成主窗口每秒 tick 里按需读一次注册表（只有 auto 才读，读一次 ~微秒）。
/// </summary>
internal static class UiTheme
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    /// <summary>当前**生效**的配色（auto 已解析成暗/亮）。</summary>
    public static UiPalette Palette { get; private set; } = UiPalette.Dark;

    /// <summary>生效配色变了（切模式或系统主题变了）。</summary>
    public static event Action? Changed;

    /// <summary>
    /// 进程级**共享**调色板字典：窗口和**每个页面**都把它合并进自己的 Resources。
    ///
    /// 为什么不能只灌窗口：页面里的控件多半是 cs 现 <c>new TextBlock</c> 建的，
    /// 这时候元素还没挂进窗口的逻辑树（ContentControl.Content 那一下的时机问题），
    /// <c>FindResource("TxtBrush")</c> 会一路查到空 —— 抛
    /// ResourceReferenceKeyNotFoundException，紧跟着再把占位对象（NamedObject）
    /// 强转成 Brush 又抛一次 InvalidCastException。挂在页面**自己**的字典上就不依赖
    /// 树了：构造完立刻能查。
    ///
    /// 换主题 = 在 <see cref="Resolve"/> 里重灌这一份字典（键被覆盖，绑了
    /// DynamicResource 的控件自己跟着变），不需要谁去挨个通知页面。
    ///
    /// ⚠️ 它有**线程亲和**：字典会把自己登记进合并过它的每个 Window / 元素的 owner
    /// 列表里，注销时机只看对象本身，不看 UI 线程死活。所以这个字段是**按 UI 会话**
    /// 活的，不是进程级单例 —— 新会话必须调 <see cref="ResetPalette"/> 换一份新的，
    /// 见那里的注释。
    /// </summary>
    public static ResourceDictionary PaletteRes => _paletteRes ??= new ResourceDictionary();

    private static ResourceDictionary? _paletteRes;

    /// <summary>
    /// 丢掉旧的调色板字典 —— **UI 会话开始时（造窗口之前）和结束时各调一次**。
    ///
    /// 为什么必须换：窗口把这份字典合并进自己的 Resources，字典就把它登记成 owner。
    /// 用户关掉窗口后，Window 对象与那条 STA 线程都结束了，可**登记还在字典里**
    /// （字典是静态字段，比窗口活得久）。下一次开窗是**另一条 STA 线程**，构造函数里
    /// 的 Load → Apply 一写键就触发 ResourceDictionary.NotifyOwners，顺着 owner 链去动
    /// 上一会话的窗口 → ThemeManager.SyncWindowThemeMode 撞 VerifyAccess →
    /// InvalidOperationException「调用线程无法访问此对象」，窗口再也开不出来
    /// （关一次再点托盘必现）。
    ///
    /// 顺带解决泄漏：旧的字典实例连带上一个窗口的可视树，会一直被这个静态字段钉住。
    ///
    /// ⚠️ 只能在 UI 线程上调（会话开始/结束都在那条 cc-ui 线程上）。
    /// </summary>
    public static void ResetPalette() => _paletteRes = null;

    /// <summary>
    /// 从共享调色板字典里取画笔，**不依赖可视树、绝不抛异常**。
    ///
    /// 为什么需要它：页面里用 <c>new Run(...)</c> 现建的 inline / 图形，在渲染期用
    /// <c>(Brush)FindResource("TxtBrush")</c> 静态查，元素还没进逻辑树（或资源未生效）时
    /// 会连抛 ResourceReferenceKeyNotFoundException + InvalidCastException（NamedObject → Brush），
    /// 颜色被迫回退。共享字典 <see cref="PaletteRes"/> 是调色板的真源、启动即灌好，
    /// 直接查它即可，与树无关，换主题后下次渲染自然拿到新色（页面 Render 会重建这些元素）。
    /// </summary>
    public static Brush? PaletteBrush(string key)
        => PaletteRes.Contains(key) ? PaletteRes[key] as Brush : null;

    /// <summary>把共享调色板合并进某个元素自己的资源字典（幂等，可以反复调）。</summary>
    public static void AttachPalette(FrameworkElement element)
    {
        var merged = element.Resources.MergedDictionaries;
        foreach (var d in merged)
            if (ReferenceEquals(d, PaletteRes)) return;
        merged.Add(PaletteRes);
    }

    public static void Load(string? saved)
    {
        Mode = ThemeIntent.Parse(saved);
        Resolve(notify: false);
    }

    public static ThemeMode Parse(string? token) => ThemeIntent.Parse(token);

    public static string Token(ThemeMode mode) => ThemeIntent.Token(mode);

    /// <summary>切模式。返回 true = 需要落盘（调用方负责写回 presets.ui.theme）。</summary>
    public static bool SetMode(ThemeMode mode)
    {
        if (Mode == mode) return false;
        Mode = mode;
        Resolve(notify: true);
        return true;
    }

    /// <summary>手动切（侧栏那颗按钮）：暗 ↔ 亮。auto 状态下点它 = 脱离跟随。</summary>
    public static ThemeMode ManualOpposite()
        => Palette == UiPalette.Dark ? ThemeMode.Light : ThemeMode.Dark;

    /// <summary>auto 模式下由主窗口每秒调一次：系统主题变了就重刷。</summary>
    public static void PollSystem()
    {
        if (Mode != ThemeMode.Auto) return;
        Resolve(notify: true);
    }

    public static bool SystemIsLight() => ThemeIntent.SystemIsLight();

    private static void Resolve(bool notify)
    {
        var next = ThemeIntent.IsLight(Mode) ? UiPalette.Light : UiPalette.Dark;
        if (ReferenceEquals(next, Palette) && notify) return;
        Palette = next;
        next.Apply(PaletteRes);          // 重灌共享字典 —— 所有引用者一起变
        if (notify) Changed?.Invoke();
    }
}
