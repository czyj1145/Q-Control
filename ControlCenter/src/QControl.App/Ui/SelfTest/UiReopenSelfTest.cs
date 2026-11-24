using System.Windows;
using System.Windows.Threading;

namespace QControl.App.Ui.SelfTest;

/// <summary>
/// 「界面关了再开」的自检 —— <c>--ui-reopen-selftest</c>。
///
/// 为什么要单独一条：窗口是**反复开关**的（关窗口只释放界面内存，后端留在托盘里），
/// 而问题全都只在"第二次"露头，编译期和单窗口截图都看不出来：
///   · 切主题闪退（全局事件没退订，漏给下一个窗口）；
///   · 第二次开窗必崩（共享调色板字典把上一会话的 Window 登记成 owner，
///     新线程写键时触发跨线程访问 —— 见 <see cref="UiTheme.ResetPalette"/>）。
///
/// 做法：连开连关 N 个会话，每个会话都走**真的构造 + Show + Close**（不 mock），
/// 每次都在新的 STA 线程上 —— 与 <see cref="UiHost"/> 的生产路径同构。
/// 任何一个会话抛异常就当场判失败，把全量堆栈打出来。
///
/// ⚠️ 只读：不建 AppState（不碰 EC、不占端口、不提权），所以可以和日常实例共存。
/// </summary>
internal static class UiReopenSelfTest
{
    private const int Sessions = 3;

    /// <summary>每次开窗后等多久再关：Show 之后立刻关会在首帧前就关掉，验不出渲染期的坑。</summary>
    private const int SettleMs = 400;

    public static int Run()
    {
        for (var i = 1; i <= Sessions; i++)
        {
            if (!OneSession(out var error))
            {
                Console.Error.WriteLine($"[reopen] FAIL 第 {i} 次会话（开窗→关窗）：{error}");
                return 2;
            }
            Console.WriteLine($"[reopen] PASS 第 {i} 次会话（开窗→关窗）");
        }

        Console.WriteLine($"[reopen] 全部通过（{Sessions} 次开/关，主题切换各一次）");
        return 0;
    }

    private static bool OneSession(out string? error)
    {
        string? err = null;
        var ok = false;

        var thread = new Thread(() =>
        {
            try
            {
                // 与 UiHost.Run 同一条规矩：会话开始先换一份干净的调色板字典。
                // ⚠️ 这行不是"顺手清一下"——去掉它，第二次会话会在这里的路上抛
                //    InvalidOperationException（调用线程无法访问此对象），本自检立刻 FAIL。
                UiTheme.ResetPalette();

                var window = new MainWindow(state: null)
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,          // 摆屏外：不闪、不抢焦点
                    Top = -20000,
                };

                // 每个会话都切一次主题：换主题会重灌调色板，是跨会话脏数据的另一条触发路径。
                // ⚠️ 放在 Show 之后：与用户真的点那颗按钮同一个时刻（窗口已在渲染）。
                window.Show();
                UiTheme.SetMode(UiTheme.Palette == UiPalette.Light ? ThemeMode.Dark : ThemeMode.Light);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SettleMs) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    window.Close();     // Closed 里会 InvokeShutdown ⇒ Dispatcher.Run 返回 ⇒ 线程结束
                };
                timer.Start();

                window.Closed += (_, _) => Dispatcher.CurrentDispatcher.InvokeShutdown();
                Dispatcher.Run();

                ok = true;
            }
            catch (Exception ex)
            {
                err = ex.ToString();
            }
        })
        {
            IsBackground = true,
            Name = "cc-ui-reopen",
        };
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        thread.Join();

        error = err;
        return ok;
    }
}
