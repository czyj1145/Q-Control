using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace QControl.App.Ui.SelfTest;

/// <summary>
/// 「一屏装得下」自检 —— <c>--ui-fit-selftest</c>。
///
/// 为什么要有它：**页面不该在常态下需要滚动条**。
/// 这一条靠肉眼很难守：内容只超出 0.7px 时滚动条也会挂出来，而"多一行说明 / 多一块
/// 提示条就顶破一屏"这种回归，编译、截图、单测全都看不出来。所以把它变成一个能反复跑的断言。
///
/// 判据只有一个：**该页的滚动区 ScrollableHeight ≤ 0.5**（= 一条滚动条都不会出现）。
/// 窗口按生产路径自己摆（<see cref="MainWindow.FitToScreen"/>：工作区 62% × 84%），
/// 也可以用 <c>--size</c> 钉一个尺寸来复核别的机器/分辨率。
///
/// 不查「关于」页：那一页是日志尾巴，天生比一屏长，滚起来是对的。
/// 也不查"展开态"（硬件页的进阶信息）：展开后允许滚动，这跟用户的说法一致
/// ——「常态不展开的情况下」。
/// </summary>
internal static class UiFitCheck
{
    /// <summary>要复核的页（= 侧栏全部业务页 + 设置，**不含 about**）。</summary>
    private static readonly string[] Pages = ["dash", "perf", "cool", "light", "sys", "hw", "set"];

    public static int Run(AppState? state, int width = 0, int height = 0)
    {
        var failed = 0;
        var exit = 1;

        var thread = new Thread(() =>
        {
            try
            {
                // 与 --ui-shot 同一个理由：这是**独立的一次界面会话**，调色板字典必须重来，
                // 否则与常驻实例同进程跑时会互相覆盖画笔（见 UiTheme.ResetPalette）。
                UiTheme.ResetPalette();

                var window = new MainWindow(state, "dash")
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                };
                if (width > 0) window.Width = width;
                if (height > 0) window.Height = height;

                window.Show();
                Console.WriteLine($"[fit] 窗口 {window.ActualWidth:0}×{window.ActualHeight:0}"
                    + $"（页面视口 {window.ActualWidth - 84:0}×{window.ActualHeight - 56:0}）");

                foreach (var page in Pages)
                {
                    window.ShowPage(page);
                    // 等一拍数据：磁盘/电池/原厂进程这些行是每秒 tick 才建出来的，
                    // 立刻量会量到"读取中…"那几行（比真身矮），自检就白做了。
                    Pump(1300);

                    var (ok, hasScroll, scrollable, content, viewport) = Measure(window);
                    if (!hasScroll)
                        Console.WriteLine($"[fit] PASS {page,-6} 无滚动区（整页居中，不可能出滚动条）");
                    else if (ok)
                        Console.WriteLine($"[fit] PASS {page,-6} 滚动量 0（内容区 {content:0.#} / 视口 {viewport:0.#}）");
                    else
                        Console.WriteLine($"[fit] FAIL {page,-6} 滚动量 {scrollable:0.#}"
                            + $"（内容 {content:0.#} > 视口 {viewport:0.#}）—— 这一页会挂一条滚动条");

                    if (!ok) failed++;
                }

                window.Close();
                exit = failed == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[fit] 自检失败：" + ex);
                exit = 2;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "cc-ui-fit",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Console.WriteLine(failed == 0
            ? $"[fit] {Pages.Length} 页全部一屏装得下（不含关于页）"
            : $"[fit] 失败 {failed} 页：会需要滚动条");
        return exit;
    }

    /// <summary>跑一下消息泵：让布局、数据 tick、绑定回灌都走完（等价于"停顿 ms 毫秒"）。</summary>
    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>量当前页的滚动区。总览页没有 ScrollViewer（内容居中、永不滚动）⇒ HasScroll=false。</summary>
    private static (bool Ok, bool HasScroll, double Scrollable, double Content, double Viewport)
        Measure(MainWindow window)
    {
        if (window.PageHost.Content is not DependencyObject page) return (false, false, 0, 0, 0);

        var scroll = Find<ScrollViewer>(page, "Scroll");

        // 总览页整页居中、没有滚动区 —— 这一页"永远不会有滚动条"，算通过
        if (scroll is null) return (true, false, 0, 0, 0);

        var bad = scroll.ScrollableHeight > 0.5 || scroll.ScrollableWidth > 0.5;
        return (!bad, true, scroll.ScrollableHeight, scroll.ExtentHeight, scroll.ViewportHeight);
    }

    private static T? Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit && hit.Name == name) return hit;
            var deep = Find<T>(child, name);
            if (deep is not null) return deep;
        }
        return null;
    }
}
