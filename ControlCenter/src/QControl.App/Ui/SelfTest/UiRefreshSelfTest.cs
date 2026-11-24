using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QControl.App.Ui.Controls;
using QControl.App.Ui.Views;

namespace QControl.App.Ui.SelfTest;

/// <summary>
/// 「重复点侧栏同一项 = 刷新这一页」的自检 —— <c>--ui-refresh-selftest</c>。
///
/// 为什么值得单独一条：这条路径**只有"第二次点同一项"才走得到**（<c>ShowPage</c> 里
/// <c>changed == false</c> 的分支），截图和一次性开窗都验不出来。而它要调的
/// <see cref="RefreshPage"/> 会挨页去碰各页自己的加载函数（读计划任务 COM、枚举进程、
/// 后台采集硬件档案…），任何一页在"页面已建好、控件已进树"的状态下出问题，用户看到
/// 的就是"点一下侧栏就闪退"。所以：八页各点两下，全过才算通过。
///
/// 同时钉住一条设计意图：重复点击是**原地刷新，不是重建页面**。若哪天有人在
/// <c>ShowPage</c> 里把它改成重建，页面实例会换掉、滚动位置和编辑态全丢，
/// 这里的 <see cref="object.ReferenceEquals"/> 会当场报出来。
///
/// ⚠️ 只读：不建 AppState（不碰 EC、不占端口、不提权），日常实例在跑时也能安全跑。
/// </summary>
internal static class UiRefreshSelfTest
{
    /// <summary>侧栏全部八页（顺序同侧栏）。</summary>
    private static readonly string[] Pages =
        ["dash", "perf", "cool", "light", "sys", "hw", "about", "set"];

    /// <summary>
    /// 点完之后等多久再关窗。Refresh 里的异步拉取（Offload 到线程池 / 后台采集硬件档案）
    /// 要时间跑回来 —— 等太短就等于没验到"结果落回界面"那一步。
    /// </summary>
    private const int SettleMs = 900;

    /// <summary>
    /// 「跟随系统外观」开关必须跟着 <see cref="UiTheme.Mode"/> 走 —— 而且要在**重复点击
    /// 侧栏「设置」**这条路径上就归位，不能等切页。
    /// 断的是同步结果：这条路径上若把主题同步塞进后台读（`Offload`），这里会当场失败。
    /// </summary>
    private static string? ThemeAutoFollowsMode(MainWindow window)
    {
        window.ShowPage("set");
        if (window.PageHost.Content is not SettingsView view) return "设置页没有挂到 PageHost 上";

        UiTheme.SetMode(ThemeMode.Auto);
        window.ShowPage("set");                     // 重复点当前项 ⇒ 刷新
        if (ReadThemeAuto(view) != true) return "开着「跟随系统外观」时，刷新后开关没亮起";

        UiTheme.SetMode(ThemeMode.Light);           // 等价于侧栏那颗按钮切到白天
        window.ShowPage("set");
        if (ReadThemeAuto(view) != false)
            return "切到白天（已退出跟随）后，重复点「设置」开关仍停在开启状态";

        UiTheme.SetMode(ThemeMode.Dark);
        window.ShowPage("set");
        return ReadThemeAuto(view) == false ? null : "切回深色（已退出跟随）后开关没有关闭";
    }

    /// <summary>提示条是否已经建出来（＝「已刷新」那条至少弹过一次）。</summary>
    private static bool ToastShown(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_toast",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(window) is Border;
    }

    /// <summary>
    /// 读那颗开关的 <c>IsOn</c>。走反射而不是给页面开口子 —— 自检专用属性会把
    /// 测试的关注点带进生产 API。
    /// </summary>
    private static bool? ReadThemeAuto(SettingsView view)
    {
        var field = typeof(SettingsView).GetField("_themeAutoSwitch",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(view) as SwitchButton)?.IsOn;
    }

    public static int Run()
    {
        string? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                string? fail = null;
                var passed = 0;

                // 与 UiHost.Run 同一条规矩：会话开始先换一份干净的调色板字典
                // （见 UiReopenSelfTest 里那段说明）。
                UiTheme.ResetPalette();

                var window = new MainWindow(state: null)
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,          // 摆屏外：不闪、不抢焦点
                    Top = -20000,
                };
                window.Show();

                foreach (var key in Pages)
                {
                    window.ShowPage(key);                      // 第一次：切过去（首次进入要建页）
                    var first = window.PageHost.Content;
                    window.ShowPage(key);                      // 第二次：重复点当前项 ⇒ 刷新
                    var again = window.PageHost.Content;

                    if (first is null)
                    {
                        fail = $"{key} 页切过去之后 PageHost 是空的";
                        break;
                    }
                    if (!ReferenceEquals(first, again))
                    {
                        fail = $"{key} 页在重复点击后被重建了（应为复用同一实例、原地刷新）";
                        break;
                    }
                    passed++;
                }

                // 主题开关那条真实故障的回归（2026-10-24 用户报）：
                // 开着「跟随系统外观」→ 用侧栏那颗按钮切到白天（= 退出跟随）→ 点「设置」，
                // 开关必须当场关掉。原实现里这几项都是后台读，等回来要几百毫秒，
                // 用户看到的就是"点了没反应"，只有切页（Loaded 重新触发）才归位。
                if (fail is null) fail = ThemeAutoFollowsMode(window);

                // 刷新必须有可见反馈：光重拉数据，屏幕上一秒一变的数字看不出任何差别
                // （2026-10-24 用户报「点了没反应、需要的是能刷新」）。
                // 脉动是动画，读不出结果；这里验提示条确实被弹出来过 —— 只有走
                // 「重复点击 ⇒ RefreshPage + 反馈」那条分支才可能创建它。
                if (fail is null && !ToastShown(window))
                    fail = "重复点击没有触发刷新反馈（「已刷新」提示条没被弹出来）";

                if (fail is null)
                {
                    // 让各页 Refresh 里那几次异步读真正回来、落到控件上（这一步出异常
                    // 会被 Dispatcher 的未处理异常路径打出来，也就等于验到了）。
                    window.Closed += (_, _) => Dispatcher.CurrentDispatcher.InvokeShutdown();

                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SettleMs) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        window.Close();
                    };
                    timer.Start();
                    Dispatcher.Run();
                }
                else
                {
                    window.Close();
                    error = fail;
                }

                if (fail is null)
                    Console.WriteLine($"[refresh] 全部通过（{passed} 页各点两下：切过去 + 原地刷新；"
                        + "设置页「跟随系统外观」随模式当场归位）");
            }
            catch (Exception ex)
            {
                error = ex.ToString();
            }
        })
        {
            IsBackground = true,
            Name = "cc-ui-refresh",
        };
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        // 超时就不等了：真卡在消息循环里时，无参 Join 会把整条自检（连 CI）挂死。
        if (!thread.Join(TimeSpan.FromSeconds(60)) && error is null)
            error = "自检线程 60s 内没有结束（消息循环没收尾）";

        if (error is null) return 0;

        Console.Error.WriteLine($"[refresh] FAIL {error}");
        return 2;
    }
}
