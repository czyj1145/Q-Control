using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QControl.Boot.Ui;

/// <summary>
/// 缺 .NET 10 桌面运行时时的那个小窗口：讲清楚缺什么 → 一键下载安装 / 去官网。
///
/// 三个状态共用同一个窗口（提示 / 进度 / 失败），只切面板不切窗口 ——
/// 换窗口会让用户觉得"点了一下又冒出个新东西"，而且失败后再回到提示态也简单。
///
/// ⚠️ 下载与安装全部在后台线程（Task.Run + IProgress 回主线程）。安装那一步会在
///    等待 UAC 时阻塞几分钟，放 UI 线程上窗口就直接"未响应"了。
/// </summary>
internal partial class BootWindow : Window
{
    private readonly AppLayout _layout;
    private readonly IReadOnlyList<string> _args;
    private readonly bool _simulateMissing;

    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>0 = 主程序已经拉起来了（或用户自己关窗），2 = 用户主动放弃。</summary>
    public int ExitCode { get; private set; }

    public BootWindow(AppLayout layout, IReadOnlyList<string> args, string? installedVersion, bool simulateMissing)
    {
        InitializeComponent();

        _layout = layout;
        _args = args;
        _simulateMissing = simulateMissing;

        SubTitle.Text = installedVersion is null
            ? Program.ProductName + " · 绿色版"
            : Program.ProductName + " · 绿色版 · 本机已有 " + installedVersion;

        ShowPrompt();
    }

    // ================================================================ 三个状态

    private void ShowPrompt()
    {
        HeadTitle.Text = "缺 .NET 10 桌面运行时";
        SetPanel(PromptPanel, PromptButtons);
        FootHint.Text = "";   // 底栏左边留空：三个按钮已经占满，再塞提示只会被截成半句话
    }

    private void ShowProgress(string status, double percent = 0, string detail = "")
    {
        HeadTitle.Text = "正在补齐运行环境";
        SetPanel(ProgressPanel, ProgressButtons);
        StatusText.Text = status;
        Bar.Value = percent;
        DetailText.Text = detail;
        FootHint.Text = "请勿关闭窗口";
    }

    private void ShowFail(string message)
    {
        HeadTitle.Text = "没能自动装好";
        SetPanel(FailPanel, FailButtons);
        ErrorText.Text = message;
        FootHint.Text = "";
    }

    private void SetPanel(UIElement panel, UIElement buttons)
    {
        foreach (var p in new UIElement[] { PromptPanel, ProgressPanel, FailPanel })
            p.Visibility = ReferenceEquals(p, panel) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var b in new UIElement[] { PromptButtons, ProgressButtons, FailButtons })
            b.Visibility = ReferenceEquals(b, buttons) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================ 按钮

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => TryClose();

    private void OnExitClick(object sender, RoutedEventArgs e) => TryClose();

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // 下载途中可以取消；安装阶段（RunInstaller 阻塞）点了不会立刻生效，
        // 但也不会出错 —— 装完照样复检、照样启动
        _cts?.Cancel();
        Log.Info("用户点了取消");
    }

    private void OnWebClick(object sender, RoutedEventArgs e) => OpenDownloadPage();

    private void TryClose()
    {
        if (_busy) return;          // 装到一半关窗，用户会以为"没装成功"
        ExitCode = 2;               // 用户主动放弃：不是错误
        Close();
    }

    private static void OpenDownloadPage()
    {
        try
        {
            // UseShellExecute=true 才会交给默认浏览器打开（false 是"执行这个文件"）
            Process.Start(new ProcessStartInfo(RuntimeInstaller.DownloadPageUrl) { UseShellExecute = true });
            Log.Info("已打开官网下载页：" + RuntimeInstaller.DownloadPageUrl);
        }
        catch (Exception ex)
        {
            Log.Error("打开浏览器失败", ex);
            MessageBox.Show("没打开浏览器。请手动访问：\n" + RuntimeInstaller.DownloadPageUrl,
                Program.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ================================================================ 安装

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        _cts = new CancellationTokenSource();

        ShowProgress("正在检查运行环境…");

        // Progress<T> 在主线程构造 ⇒ 回调自动回到 UI 线程，不需要自己 Dispatcher.Invoke
        var progress = new Progress<InstallerProgress>(p =>
        {
            StatusText.Text = p.Text;
            Bar.Value = p.Percent;
            DetailText.Text = p.Percent >= 85
                ? "正在静默安装到 C:\\Program Files\\dotnet（若弹出授权窗口请点「是」）"
                : "下载完成后会自动开始安装，中途只需在授权窗口点一次「是」";
        });

        try
        {
            await Task.Run(() => RuntimeInstaller.EnsureInstalled(progress, _cts.Token), _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Info("下载已取消");
            _busy = false;
            ShowPrompt();
            return;
        }
        catch (BootException ex)
        {
            Log.Error("安装运行时失败：" + ex.Message);
            _busy = false;
            ShowFail(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            Log.Error("安装运行时出错", ex);
            _busy = false;
            ShowFail("安装时出错：" + ex.Message + "\n\n日志：" + Log.LogPath);
            return;
        }

        _busy = false;

        // 运行时齐了：把控制权交给主程序。这一步失败也要说清楚 ——
        // 否则又是"装了半天，双击还是没反应"。
        if (!Launcher.TryLaunch(_layout, _args, out var error))
        {
            ShowFail(error);
            return;
        }

        ExitCode = 0;
        Close();
    }

    // ================================================================ 截图（开发用）

    /// <summary>
    /// 把三种状态各渲一张 PNG（`--boot-ui-shot=&lt;前缀&gt;`）。
    /// 为什么要有：这个窗口只有在"没装运行时"的机器上才出现，开发机上永远看不到，
    /// 版式/换行/对比度只能靠看图发现问题 —— 与安装器的 `/ui-shot` 同一个理由。
    /// </summary>
    public static void RenderShots(AppLayout layout, IReadOnlyList<string> args, string prefix)
    {
        void Render(string suffix, Action<BootWindow> prepare)
        {
            var win = new BootWindow(layout, args, null, true)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            win.Show();
            prepare(win);
            win.UpdateLayout();
            win.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(win.ActualWidth), (int)Math.Ceiling(win.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(win);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = prefix + suffix + ".png";
            using (var fs = File.Create(path)) encoder.Save(fs);

            Log.Info("截图：" + path);
            win.Close();
        }

        Render("-prompt", _ => { });
        Render("-progress", w => w.ShowProgress("正在下载 .NET 10 桌面运行时… 12.4 / 57.3 MB（22%）", 22,
            "下载完成后会自动开始安装，中途只需在授权窗口点一次「是」"));
        Render("-installing", w => w.ShowProgress("正在安装 .NET 10 桌面运行时…", 88,
            "正在静默安装到 C:\\Program Files\\dotnet（若弹出授权窗口请点「是」）"));
        Render("-fail", w => w.ShowFail(
            "无法自动下载 .NET 10 桌面运行时（约 57 MB）。\n\n" +
            "试过的 3 个下载地址都没成功：\n" +
            "· aka.ms — 连接超时\n" +
            "· dotnetcli.azureedge.net — 连接超时\n" +
            "· dotnetcli.blob.core.windows.net — 域名解析失败（DNS）\n\n" +
            "办法：连上网后重试；或手动下载安装，再重新运行本程序。\n" +
            "（也可以把下载到的 exe 放到绿色版目录里，再试一次。）"));
    }
}
