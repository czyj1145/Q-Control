using QControl.Hal.Sys;

namespace QControl.App.Ui;

/// <summary>
/// 主窗口的「自我提权」部分（partial，单独一个文件是为了不和逐页迁移的改动抢同一个文件）。
///
/// 只在**非管理员**实例上可达：徽章 `_warnAdmin` 平时 `Collapsed`，
/// 只有 `/api/status` 的 `env.admin == false` 时才显示（见 MainWindow.UpdateChips）。
/// </summary>
public partial class MainWindow
{
    /// <summary>最后打开过的页（`ShowPage` 写）。提权重启后按它回到原页。</summary>
    private string? _lastPage;

    /// <summary>
    /// 顶栏「⚠ 需管理员」被点：确认 → 提权重启。
    ///
    /// 为什么要确认框：这个动作会**关掉当前窗口再换一个进程**，必须让用户知道
    /// "UAC 之后窗口会自己回来"；静默重启会像崩溃。
    ///
    /// 三种结局分别说话：
    ///   · Started   = 新进程已在起，本进程接下来会被它请退（不自己抢着退）；
    ///   · Cancelled = 用户在 UAC 上点了「否」，本进程照旧只读运行；
    ///   · Failed    = 说清原因（比如不是从 exe 启动的），别让用户以为点坏了。
    /// </summary>
    private async void AskElevate()
    {
        var ok = await UiDialog.ConfirmAsync(
            this,
            "以管理员身份重启？",
            "EC 写入必须要管理员权限。同意 UAC 后，控制中心会换一个管理员进程重新起来，"
            + "窗口会自动重开；当前这个只读实例会随即退出。",
            okText: "重启并提权");
        if (!ok) return;

        var result = UiElevate.RestartElevated(_lastPage);
        switch (result.Status)
        {
            case ElevateStatus.Started:
                Toast("已请求管理员权限：同意 UAC 后窗口会自动重开");
                break;

            case ElevateStatus.Cancelled:
                Toast("已取消提权（UAC 上点了「否」）", ok: false);
                break;

            default:
                Toast("提权失败：" + (result.Detail ?? "原因未知"), ok: false);
                break;
        }
    }
}
