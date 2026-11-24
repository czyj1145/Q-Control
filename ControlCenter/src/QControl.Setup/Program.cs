using System.Windows;

namespace QControl.Setup;

/// <summary>
/// 入口。五个模式：
///
///   （无参）        向导安装
///   /uninstall      向导卸载
///   /silent         静默（配合 /uninstall 做静默卸载、配合 /dir= 做批量部署）
///   /extract=&lt;dir&gt; 只解包（便携版/排障）
///   /selftest       自检：在临时沙箱里把整套流程跑一遍，不碰真实系统
///
/// ⚠️ 安装/卸载必须管理员（清单里已写 requireAdministrator）。没提权时这里只报错
///    退出，绝不"尽量试试" —— 装一半比装失败更糟。
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitNotAdmin = 5;

    [STAThread]
    private static int Main(string[] argv)
    {
        var options = SetupOptions.Parse(argv);
        Log.Init(options.Mode == SetupMode.Install && !options.Silent ? "" : options.Mode.ToString().ToLowerInvariant());
        Log.Info($"{SetupOptions.ProductName} {SetupOptions.Version} · 模式={options.Mode} · 管理员={Admin.IsElevated()}");

        // 这两行是"卸载删错目录 / 升级装出两套"的第一手证据：
        // 用户报障时先看它，就知道程序认为该动的是哪个目录、凭什么这么认为。
        if (options.Mode is SetupMode.Install or SetupMode.Uninstall)
            Log.Info($"安装目录：{options.InstallDir}（{options.InstallDirSource}）");

        try
        {
            switch (options.Mode)
            {
                case SetupMode.SelfTest: return SelfTest.Run(options);
                case SetupMode.Extract: return Extract(options);
                case SetupMode.Shot: return Shot(options);
            }

            if (!Admin.IsElevated())
            {
                Log.Error("没有管理员权限，拒绝继续");
                if (!options.Silent) Complain("安装和卸载都需要管理员权限。\n请右键安装包，选择「以管理员身份运行」。");
                return ExitNotAdmin;
            }

            if (options.Silent) return RunSilent(options);

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            return app.Run(new Ui.SetupWindow(options));
        }
        catch (SetupException ex)
        {
            Log.Error(ex.Message);
            if (!options.Silent) Complain(ex.Message);
            return ExitFailed;
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (!options.Silent) Complain("安装程序出错：" + ex.Message + "\n\n详细信息见：" + Log.Path);
            return ExitFailed;
        }
    }

    // ---------------------------------------------------------------- 静默

    private static int RunSilent(SetupOptions options)
    {
        if (options.Mode == SetupMode.Uninstall)
        {
            // 静默卸载**默认保留**配置与日志：删用户数据必须是显式选择（向导里那个勾选框）。
            Installer.Uninstall(options, purgeData: false, progress: null, CancellationToken.None);
        }
        else
        {
            Installer.Install(options, desktopShortcut: true, progress: null, CancellationToken.None);
        }
        return ExitOk;
    }

    // ---------------------------------------------------------------- 只解包

    private static int Extract(SetupOptions options)
    {
        var dir = options.ExtractDir;
        if (string.IsNullOrWhiteSpace(dir))
            throw new SetupException($"用法：{ExeName()} /extract=<目标目录>");

        var full = Path.GetFullPath(dir);
        Log.Info("解包到 " + full);
        Payload.ExtractTo(full, null, 0, 100, CancellationToken.None);

        // 便携版要 exe 旁边就有一份 model.json，否则主程序会退回"纯桩模式"
        if (Payload.ReadModelBytes() is { } model)
            File.WriteAllText(Path.Combine(full, "model.json"), model);

        Log.Info("解包完成：" + full);
        return ExitOk;
    }

    private static void Complain(string message)
        => System.Windows.Forms.MessageBox.Show(message, SetupOptions.ProductName,
                                                System.Windows.Forms.MessageBoxButtons.OK,
                                                System.Windows.Forms.MessageBoxIcon.Warning);

    /// <summary>当前 exe 的文件名 —— 提示里写实际名字，而不是写死"QControl.Setup.exe"（副本叫 Uninstall.exe）。</summary>
    private static string ExeName()
    {
        try { return Path.GetFileName(Environment.GetCommandLineArgs()[0]); }
        catch { return "QControl.Setup.exe"; }
    }

    // ---------------------------------------------------------------- 截图（开发用）

    /// <summary>
    /// `/ui-shot=&lt;路径前缀&gt;`：把安装向导和卸载向导各渲一张 PNG 后退出。
    ///
    /// 为什么要有：安装器的版式（圆角、间距、中文换行、暗色对比度）只能靠看图发现问题 ——
    /// 与主程序的 `--ui-shot` 是同一个思路。**不需要管理员**：只渲染窗口，不点任何按钮。
    /// </summary>
    private static int Shot(SetupOptions options)
    {
        var prefix = options.ShotPrefix ?? Path.Combine(Path.GetTempPath(), "qcontrol-setup");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        void Render(string suffix, SetupOptions o, Action<Ui.SetupWindow> prepare)
        {
            var win = new Ui.SetupWindow(o) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            win.Show();
            prepare(win);
            win.UpdateLayout();

            // 等一次渲染完成，否则抓到的是空白
            win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

            var dpi = 96d;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(win.ActualWidth * dpi / 96d),
                (int)Math.Ceiling(win.ActualHeight * dpi / 96d),
                dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(win);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var path = prefix + suffix + ".png";
            using (var fs = File.Create(path)) encoder.Save(fs);

            Log.Info("截图：" + path);
            win.Close();
        }

        var install = options;
        var uninstall = SetupOptions.Parse(["/uninstall"]);

        Render("-install", install, _ => { });
        Render("-progress", install, w => { w.ShowProgress(); });
        Render("-done", install, w => { w.ShowProgress(); w.ShowDone(); });
        Render("-uninstall", uninstall, _ => { });

        app.Shutdown();
        return ExitOk;
    }
}
