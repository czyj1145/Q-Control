using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QControl.Core.Devices;

namespace QControl.App.Ui.SelfTest;

/// <summary>
/// 给自动化验收用的"截图即冒烟"：起窗口 → 等几拍数据 → 把窗口内容渲成 PNG → 退出。
///
/// 为什么要有它：WPF 没有 DOM 可查，
/// 光靠"编译过了"证明不了没画错（空白窗、字体丢失、资源键写错全都是编译期看不出来的）。
/// 这里用 <see cref="RenderTargetBitmap"/> 直接渲染**正在显示的窗口内容**，
/// 不需要截屏权限、不怕被别的窗口挡、也不依赖桌面分辨率。
///
/// ⚠️ 窗口要真的 <c>Show()</c> 一次：没显示过的 Window 不跑布局管线，ActualWidth 是 0，
///    渲染出来是一张全透明的图（0 字节）。为了不闪一下，把它摆到屏幕外 (-20000,-20000)。
/// ⚠️ 等待时间是**必需的**：状态每秒一拍，立刻渲染只会拍到"读取中…"。
/// </summary>
internal static class UiShot
{
    public static int Run(AppState? state, string outPath, string? page, string? theme,
                          int width, int height, int waitMs, string? dialog = null, bool flip = false)
    {
        var exit = 1;

        // 转场动效已全部删除（2026-10-28），截图不再需要"先关动画"——
        // 翻页是瞬时到位的，拍到的一定是稳定帧。

        // 版式演练：`--update-source` 指了调试源时，先**同步**查一次再建窗口。
        // 否则关于页永远是 idle（正常路径是后台查 + 轮询，不阻塞 UI），
        // latest / newer / empty / error 四种排版就永远截不到。
        if (Updates.UpdateChecker.SourceOverride is { Length: > 0 })
            Updates.UpdateChecker.Instance.CheckAndWait();

        var thread = new Thread(() =>
        {
            try
            {
                // 截图也是一次独立的 UI 会话（自己的 STA 线程）：调色板字典必须是新的，
                // 理由同 UiTheme.ResetPalette（--ui-shot 与常驻实例同进程共存时尤其重要）。
                UiTheme.ResetPalette();

                var window = new MainWindow(state, page, theme)
                {
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                };
                if (width > 0) window.Width = width;
                if (height > 0) window.Height = height;

                window.Show();

                // 对话框的视觉回归：把模态框当成一页拍下来（见 --ui-dialog）。
                // "guide" 特殊：引导弹窗是独立 Window，拍它自己而不是主窗口。
                // "guide-warmup"：采集前段那一屏（按钮全灰、只报阶段）—— 新加的状态也得有人拍。
                Window shotTarget = window;
                if (dialog is "guide" or "guide-warmup")
                {
                    var guide = new GuideWindow(GuideDemoView(warmup: dialog == "guide-warmup"))
                    {
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = -20000,
                    };
                    guide.Show();
                    shotTarget = guide;
                }
                else _ = OpenDialog(window, dialog);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(300, waitMs)) };
                var pendingFlip = flip;
                timer.Tick += (_, _) =>
                {
                    // --flip：**在窗口活着的时候**切一次主题再拍。
                    // 为什么单列一条路：`--theme light` 是"新窗口按浅色起"，那是另一回事；
                    // 「切主题后不跟着变」的 bug（静态画笔实例、DynamicResource 被本地值盖掉）
                    // 只有走 SetMode 才会露出来，直接拍浅色永远是好的。
                    if (pendingFlip)
                    {
                        pendingFlip = false;
                        UiTheme.SetMode(UiTheme.Palette == UiPalette.Light
                            ? ThemeMode.Dark
                            : ThemeMode.Light);
                        return;      // 下一个 tick 再拍，给重排/重画留一拍
                    }
                    timer.Stop();
                    try
                    {
                        Save(shotTarget, outPath);
                        Console.WriteLine($"[shot] 已写入 {outPath}");
                        exit = 0;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[shot] 渲染失败：{ex.GetType().Name}: {ex.Message}");
                        exit = 2;
                    }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };
                timer.Start();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[shot] 启动失败：" + ex);
                exit = 2;
            }
        })
        {
            IsBackground = true,
            Name = "cc-ui-shot",
        };
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        thread.Join();
        return exit;
    }

    /// <summary>
    /// 引导弹窗的示例快照：取**最长的一句提示**（风扇那步的 Auto / Max），
    /// 并让前四步都已完成 —— 步骤条最长、按钮全可用，排版问题才藏不住。
    /// </summary>
    private static JsonObject GuideDemoView(bool warmup = false)
    {
        // 采集前段那一屏：只报阶段、按钮全灰。文案取实际最长的阶段行（采样帧数那条）。
        if (warmup)
            return new JsonObject
            {
                ["active"] = true,
                ["phase"] = "warmup",
                ["stage"] = "采样 12/16（跑点负载更好认温度）…",
                ["remaining"] = -1,
                ["index"] = 0,
                ["total"] = AdoptGuide.Steps.Count,
                ["finished"] = false,
                ["frames"] = 0,
                ["steps"] = new JsonArray(),
            };

        var steps = new JsonArray();
        for (var i = 0; i < AdoptGuide.Steps.Count; i++)
        {
            steps.Add(new JsonObject
            {
                ["id"] = AdoptGuide.Steps[i].Id,
                ["title"] = AdoptGuide.Steps[i].Title,
                ["frames"] = i < 4 ? 2 : 0,
                ["done"] = i < 4,
                ["skipped"] = false,
                ["current"] = i == 4,
            });
        }
        var last = AdoptGuide.Steps[^1];
        return new JsonObject
        {
            ["active"] = true,
            ["phase"] = "observe",
            ["remaining"] = 42,
            ["index"] = AdoptGuide.Steps.Count - 1,
            ["total"] = AdoptGuide.Steps.Count,
            ["finished"] = false,
            ["title"] = last.Title,
            ["hint"] = last.Hint,
            ["frames"] = 2,
            ["steps"] = steps,
        };
    }

    /// <summary>
    /// 弹一个示例对话框给截图用。文案刻意用**真实场景**里最长的那几条
    /// （显卡模式的三选一最长），排版问题才藏不住。
    /// ⚠️ 这里只发不管结果：截图完成后进程直接退出，不需要有人点它。
    /// </summary>
    private static Task? OpenDialog(MainWindow window, string? kind) => kind switch
    {
        "confirm" => UiDialog.ConfirmAsync(window, "切换系统模式",
            "即将切换到「狂暴模式」\n\n"
            + "这会向 EC 的 APMD 寄存器下发 0x00，并同步切换 Windows 电源计划「高性能」。",
            "切换", "取消"),
        // 显卡模式**写入前**的确认框：正文最长的一版（三段"写入/生效/当下" + 一段警告），
        // 换行挤不挤、警告段跟正文有没有分开，只能看图判断。
        "gfx-confirm" => UiDialog.ConfirmAsync(window, "切换显卡模式",
            "即将把显卡模式切换到「集显输出」\n当前：独显直连\n\n"
            + "· 写入：向 EC 的 0xFB00（GFXT）写组写入该模式\n"
            + "· 生效：需重启电脑，由固件下次开机时应用（与原厂一致）\n"
            + "· 当下：显示输出暂不变化，属正常现象\n\n"
            + "⚠ 集显输出：独显不参与，游戏 / 渲染性能大幅下降，\n"
            + "   本界面之后也读不到 NVIDIA 数据。",
            "写入 EC", "取消"),
        "choose" => UiDialog.ChooseAsync(window, "显卡模式已写入 EC",
            "已写入 EC：模式 2 · 独显直连\n"
            + "显示输出将在下次开机时由固件切换（与原厂一致），\n"
            + "重启前不会发生变化。\n\n"
            + "· 立即重启：立即重启电脑\n"
            + "· 稍后重启：本次不执行任何操作，稍后自行重启\n\n"
            + "已写入的值不会丢失，重启后生效。",
            [
                ("now", "立即重启", true, false),
                ("later", "稍后重启", false, false),
            ]),
        // 关于页「清理」的三选一：正文里有采集包 + 档案两份文件清单，是三选一里**最高**的一版
        // （多一段目录清单就顶出窗口的排法只能靠看图发现）。
        "clear-profiles" => UiDialog.ChooseAsync(window, "清理日志，还是连采集包/档案一起清？",
            "日志：21 个文件，合计 1.2 MB（其中区间导出 14 个）。\n"
            + "会删掉 logs 目录里的三类文件：当天与历史日志（cc-*.log）、轮转旧档（cc-*.old.log）、"
            + "区间记录导出（capture-*.log）。正在写的那份当天日志也会被清掉，随后立刻新建一份空的继续写。\n\n"
            + "采集包：6 个，合计 336 KB（logs\\adopt-*.json 与同名 .txt —— 「一键采集」生成、要发给作者的那份）\n"
            + "  · adopt-G16-Pro-20261108-140101.json\n  · adopt-G16-Pro-20261108-140101.txt\n"
            + "  · adopt-G16-Pro-20261108-150117.json\n  · adopt-G16-Pro-20261108-150117.txt\n"
            + "  · 另有 2 个\n\n"
            + "机型档案：2 个，合计 12.4 KB（一键采集给本机存的温度/转速偏移）\n"
            + "  · quanta-nl5.json\n  · my-machine.json\n"
            + "删掉后要重启控制中心才会回落到内置档案；重启之前仍在用内存里那份。\n\n"
            + "删除后都不可恢复。",
            [
                ("logs", "只清日志", true, false),
                ("all", "全部清掉", false, true),
                ("cancel", "取消", false, false),
            ]),
        // 「一键采集」开始前的原厂门槛：三态各一版（正文是三~四段 + 三颗按钮，
        // 第一段就是长句 —— 换行挤不挤只能看图）。文案直接取 VendorPrompt 那份，
        // 别在这里抄第二遍。
        "vendor-notrunning" => VendorPrompt.ShowAsync(window, VendorUiState.NotRunning, writes: true),
        "vendor-nowindow" => VendorPrompt.ShowAsync(window, VendorUiState.NoWindow, writes: true),
        "vendor-ready" => VendorPrompt.ShowAsync(window, VendorUiState.WindowReady, writes: true),
        "vendor-readonly" => VendorPrompt.ShowAsync(window, VendorUiState.WindowReady, writes: false),
        _ => null,
    };

    /// <summary>渲染窗口的**内容根**（无边框窗口里 = 整个界面）。</summary>
    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement content)
            throw new InvalidOperationException("窗口没有内容根");

        var w = (int)Math.Ceiling(content.ActualWidth);
        var h = (int)Math.Ceiling(content.ActualHeight);
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"内容尺寸异常 {w}x{h}");

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
