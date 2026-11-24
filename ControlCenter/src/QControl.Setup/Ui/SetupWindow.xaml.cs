using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace QControl.Setup.Ui;

/// <summary>
/// 安装/卸载向导。
///
/// 它**只管界面和状态机**：所有会动系统的事都在 <see cref="Installer"/> 里，
/// 这样 `/silent`、向导、自检三条路走的是同一套实现，不会出现"界面上能装、
/// 静默装就漏了某一步"。
///
/// 状态机：配置页 → 进度页 → 完成页；任何一步抛异常就停在进度页并显示原因
/// （主按钮变成「关闭」）。
/// </summary>
internal partial class SetupWindow : Window
{
    private readonly SetupOptions _options;
    private readonly bool _uninstall;

    /// <summary>实际生效的选项（用户在界面上改过安装目录时与 <see cref="_options"/> 不同）。</summary>
    private SetupOptions _active;

    private bool _busy;      // 正在装/卸：不许关窗、不许重复点
    private bool _finished;  // 已到完成页：主按钮 = 关闭

    public SetupWindow(SetupOptions options)
    {
        InitializeComponent();
        _options = options;
        _active = options;
        _uninstall = options.Mode == SetupMode.Uninstall;
        ApplyMode();
    }

    // ================================================================ 初始化

    private void ApplyMode()
    {
        SubTitle.Text = $"版本 {SetupOptions.Version} · MIT 许可 · 广达模具 EC 控制中心";
        // 内容量不同（卸载页少两个勾选框），固定高度会在中间留出一大块空白
        Height = _uninstall ? 430 : 462;
        DirBox.Text = _options.InstallDir;

        var payloadMb = Payload.UncompressedSize() / 1024d / 1024d;

        if (_uninstall)
        {
            Title = "卸载 Q-Control";
            IntroText.Text = "将从本机移除 Q-Control 控制中心：程序文件、快捷方式、开机自启任务都会被清理。";

            // ⚠️ 安装位置**不再锁死**（2026-10-30 用户报障"卸载不能修改路径"）。
            //    以前这里只显示"默认位置"，装到别的盘的人点卸载，删的是一个根本不存在的
            //    Program Files\Q-Control，真正的目录原封不动 —— 卸载项却已经从
            //    「应用和功能」里没了，从此再也卸不掉。现在默认值来自自动识别（见
            //    SetupOptions.Detect），识别不出来时用户可以自己指过去。
            DirLabel.Text = "安装位置（已自动识别，可修改）";
            DirBox.IsReadOnly = false;
            DirBox.IsEnabled = true;
            BrowseBtn.Visibility = Visibility.Visible;
            DesktopChk.Visibility = Visibility.Collapsed;
            PurgeChk.Visibility = Visibility.Visible;
            LaunchChk.Visibility = Visibility.Collapsed;

            UpdateUninstallHint();
            NoteText.Text =
                "配置与日志在 %ProgramData%\\Q-Control（风扇曲线、灯效、性能策略都在里面）。\n" +
                "默认保留它们，重装后设置直接还在；要一并清掉就勾选上面那一项。";
            ProgTitle.Text = "正在卸载";
            PrimaryBtn.Content = "卸载";
            FootHint.Text = "";
            DoneTitle.Text = "卸载完成";
        }
        else
        {
            IntroText.Text = "将 Q-Control 控制中心安装到本机。安装过程需要管理员权限（读写 EC 必须有），"
                           + "装好之后的日常使用不会再弹 UAC。";

            // 检测到既有安装 ⇒ 默认目录就是它，点一下「安装」等于就地升级：
            // 程序文件换新、旧版本的残留清掉、%ProgramData% 里的配置原样保留。
            if (_options.DetectedDir is not null)
                DirLabel.Text = $"安装位置（检测到已安装版本 · {_options.InstallDirSource}，将继续就地升级）";

            SpaceText.Text = payloadMb > 0 ? $"需要约 {payloadMb:F0} MB 磁盘空间" : "";
            NoteText.Text =
                "程序装在安装目录里，普通用户只读 —— 避免被替换后借开机自启任务提权。\n" +
                "配置与日志单独放在 %ProgramData%\\Q-Control，普通用户可写，所以改设置不需要管理员。\n" +
                DotnetRuntime.StatusLine() + "。";
            ProgTitle.Text = "正在安装";
            PrimaryBtn.Content = "安装";
            FootHint.Text = "装程序本体约 10~30 秒；需要下载 .NET 运行时时会久一些（1~3 分钟）";
            DoneTitle.Text = "安装完成";
        }
    }

    /// <summary>
    /// 卸载页底部那行提示：当前路径的占用大小，或"这个目录不像安装目录"的即时警告。
    /// 用户改路径时实时跟着走（见 <see cref="OnDirChanged"/>）—— 框里能打字却不给反应，
    /// 等于鼓励他填错。
    /// </summary>
    private void UpdateUninstallHint()
    {
        var dir = (DirBox.Text ?? "").Trim();

        if (!SetupOptions.LooksLikeInstallDir(dir))
        {
            SpaceText.Text = Directory.Exists(dir)
                ? "这个目录里没有找到 Q-Control 的程序文件 —— 请确认它是不是安装位置。"
                : "这个目录不存在。";
            return;
        }

        var usedMb = Installer.EstimateSizeKb(dir) / 1024d;
        var from = string.IsNullOrEmpty(_options.DetectedSource)
            ? ""
            : $"（自动识别自{_options.DetectedSource}）";
        SpaceText.Text = usedMb > 0 ? $"当前占用约 {usedMb:F0} MB{from}" : from;
    }

    // ================================================================ 交互

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => TryClose();

    private void OnCancelClick(object sender, RoutedEventArgs e) => TryClose();

    private void TryClose()
    {
        if (_busy) return;          // 装到一半关窗 = 半个程序躺在 Program Files 里
        Close();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            // 用 WinForms 的 FolderBrowserDialog：net48 里它是老式树形对话框（能用、不用手写 COM），
            // 省掉一段自己维护 IFileOpenDialog vtable 的风险面。
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择 Q-Control 的安装位置",
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(DirBox.Text) ? DirBox.Text : SetupOptions.DefaultInstallDir,
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && dlg.SelectedPath.Length > 0)
                DirBox.Text = dlg.SelectedPath;
        }
        catch (Exception ex)
        {
            Log.Warn("打开目录选择框失败：" + ex.Message);
        }
    }

    /// <summary>卸载页改路径时刷新底部提示（安装页那行是"需要多少空间"，与路径无关）。</summary>
    private void OnDirChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_uninstall) UpdateUninstallHint();
    }

    private async void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (_finished) { TryLaunchAndClose(); return; }
        if (_busy) return;

        var dir = (DirBox.Text ?? "").Trim();
        if (!IsSaneInstallDir(dir))
        {
            MessageBox.Show(this, "这个路径不能用作安装目录：\n需要一个完整的盘符路径，且不能直接是盘的根目录。",
                            "路径不可用", MessageBoxButton.OK, MessageBoxImage.Warning);
            DirBox.Focus();
            DirBox.SelectAll();
            return;
        }

        // 卸载：路径里找不到 Q-Control 的东西时先问一句。真正删目录发生在 Installer 里，
        // 只有 LooksLikeInstallDir 为真才会递归删 —— 这里问是为了让用户知道"填错了"
        // 和"填对了但只剩残渣"是两回事，别按下去才发现什么都没删。
        if (_uninstall && !SetupOptions.LooksLikeInstallDir(dir) && !ConfirmUnusualUninstallTarget(dir))
            return;

        var options = _options.WithInstallDir(dir);
        _active = options;

        _busy = true;
        ShowProgress();

        var progress = new Progress<SetupProgress>(p =>
        {
            ProgBar.Value = p.Percent;
            ProgText.Text = p.Text;
        });

        try
        {
            if (_uninstall)
            {
                var purge = PurgeChk.IsChecked == true;
                await Task.Run(() => Installer.Uninstall(options, purge, progress, CancellationToken.None));
            }
            else
            {
                var desktop = DesktopChk.IsChecked == true;
                await Task.Run(() => Installer.Install(options, desktop, progress, CancellationToken.None));
            }
            ShowDone();
        }
        catch (SetupException ex)
        {
            Log.Warn("安装/卸载中止：" + ex.Message);
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            ShowError("操作失败：" + ex.Message + "\n\n详细信息见日志：" + Log.Path);
        }
        finally
        {
            _busy = false;
        }
    }

    // ================================================================ 页面切换

    internal void ShowProgress()
    {
        P_Config.Visibility = Visibility.Collapsed;
        P_Done.Visibility = Visibility.Collapsed;
        P_Progress.Visibility = Visibility.Visible;

        ProgError.Visibility = Visibility.Collapsed;
        ProgBar.Value = 0;
        ProgText.Text = "准备中…";

        PrimaryBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        FootHint.Text = _uninstall ? "正在清理，请不要关闭窗口" : "正在复制文件，请不要关闭窗口";
    }

    internal void ShowDone()
    {
        P_Progress.Visibility = Visibility.Collapsed;
        P_Config.Visibility = Visibility.Collapsed;
        P_Done.Visibility = Visibility.Visible;

        PrimaryBtn.IsEnabled = true;
        PrimaryBtn.Content = "完成";
        CancelBtn.Visibility = Visibility.Collapsed;
        FootHint.Text = "";
        _finished = true;

        DoneText.Text = _uninstall
            ? (PurgeChk.IsChecked == true
                ? "程序文件、快捷方式与配置都已清理。"
                : "程序文件与快捷方式已删除；配置与日志保留在 %ProgramData%\\Q-Control。")
            : $"已安装到 {_active.InstallDir}\n从开始菜单（或桌面）打开「{SetupOptions.ProductName}」。";
    }

    private void ShowError(string message)
    {
        P_Progress.Visibility = Visibility.Visible;
        ProgText.Text = "已中止";
        ProgError.Text = message;
        ProgError.Visibility = Visibility.Visible;

        PrimaryBtn.IsEnabled = true;
        PrimaryBtn.Content = "关闭";
        CancelBtn.Visibility = Visibility.Collapsed;
        FootHint.Text = "";
        _finished = true;
    }

    // ================================================================ 收尾

    private void TryLaunchAndClose()
    {
        if (!_uninstall && LaunchChk.IsChecked == true)
        {
            try
            {
                // 本进程已经是管理员 ⇒ 子进程继承同一个令牌，直接就是提权实例，不弹 UAC。
                // 主程序是 WinExe，双击/被拉起都不会闪控制台。
                var exe = Path.Combine(_active.InstallDir, SetupOptions.MainExe);
                // ⚠️ 不早退：早退会跳过末尾的 Close()，向导会傻站在那儿不关
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo(exe)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = _active.InstallDir,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("启动失败：" + ex.Message);
            }
        }
        Close();
    }

    /// <summary>
    /// 卸载路径里没有 Q-Control 的程序文件时，让用户明确确认一次。
    /// 返回 true = 继续卸载（只是清注册表项 / 快捷方式 / 自启任务）。
    /// </summary>
    private bool ConfirmUnusualUninstallTarget(string dir)
    {
        var detail = Directory.Exists(dir)
            ? "这个目录里没有找到 Q-Control 的程序文件。\n继续卸载只会移除注册表里的卸载项、快捷方式与自启任务，不会改动这个目录里的内容。"
            : "这个目录不存在。\n继续卸载只会移除注册表里的卸载项、快捷方式与自启任务。";

        return MessageBox.Show(this, detail + "\n\n要继续吗？", "确认卸载位置",
                               MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private static bool IsSaneInstallDir(string path)
    {
        if (path.Length == 0) return false;
        if (!Compat.IsPathFullyQualified(path)) return false;
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root)
            && !string.Equals(Path.GetFullPath(path).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
