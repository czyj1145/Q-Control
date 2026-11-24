using System.Security.Principal;
using QControl.Core.Startup;

namespace QControl.Hal.Sys;

/// <summary>提权重启的三种结局。取消 ≠ 失败：前者是用户的选择，后者要修东西。</summary>
public enum ElevateStatus
{
    /// <summary>新进程已经起来了，调用方应当立刻退出。</summary>
    Started,

    /// <summary>用户在 UAC 上点了「否」（或安全软件静默拦下）。</summary>
    Cancelled,

    /// <summary>起不来。`Detail` 里是能给人看的原因。</summary>
    Failed,
}

/// <summary>见 <see cref="ElevateStatus"/>。</summary>
public readonly record struct ElevateResult(ElevateStatus Status, string? Detail)
{
    public bool Ok => Status == ElevateStatus.Started;
    public static ElevateResult Started() => new(ElevateStatus.Started, null);
    public static ElevateResult Cancelled(string? detail = null) => new(ElevateStatus.Cancelled, detail);
    public static ElevateResult Failed(string detail) => new(ElevateStatus.Failed, detail);
}

/// <summary>
/// **自我提权**：把自己以管理员身份重新拉一次。
///
/// 为什么需要（ROADMAP R4）：EC 写命令在非管理员下会被 WMI **接受却不生效**
/// （详见 <see cref="Ec.EcHal.AdminWriteDeniedHint"/>）。写守卫因此一律回 403，
/// 用户看到的是"点了没反应"。早先只能靠外部的 `run-as-admin.cmd → run-silent.wsf`
/// 提权，直接双击 exe 就永远是只读的。
///
/// 为什么用「重启自己」而不用 `requireAdministrator` 清单：
///   清单会让**每一次**干净启动都弹 UAC —— 包括 `--help`、`--selftest`、
///   `--ui-shot` 这些纯只读的调用，以及 `--show`（它只是把旧实例的窗口叫出来）。
///   进程内判断 + `runas` 重启才能挑场合，也能在"已经有实例在跑"时干脆不提权。
///
/// ⚠️ 提权**不可能**在同一个进程里完成（令牌在进程生命周期内不变）：所谓自我提权
///    一定是"起一个新进程 + 老进程退出"。所以调用方拿到 <see cref="ElevateStatus.Started"/>
///    之后必须马上返回，绝不能接着往下初始化 AppState（两个进程会抢 EC 单槽缓冲区）。
/// </summary>
public static class Elevation
{
    private static bool? _elevated;

    /// <summary>
    /// 当前进程是不是管理员。结果**缓存**：令牌在进程生命周期内不会变
    /// （提权只能换新进程），而写守卫每次都要问一次。
    ///
    /// 这是全项目**唯一**的管理员判定口 —— `Autostart.IsAdmin`、
    /// `EcHal.IsAdmin` 都转调这里，避免三处各查一遍令牌、某天口径不一致。
    /// </summary>
    public static bool IsElevated()
    {
        if (_elevated is bool cached) return cached;
        bool value;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            value = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            value = false;
        }
        _elevated = value;
        return value;
    }

    /// <summary>
    /// 以管理员身份重启当前程序（UAC 由系统弹），参数按 <see cref="ElevationPlan"/>
    /// 原样带过去。**成功返回后调用方必须退出**。
    ///
    /// 工作目录沿用当前目录：`--ui` / `--fixtures` 这类相对路径在新进程里才指得对。
    /// </summary>
    public static ElevateResult RestartSelf(IReadOnlyList<string> argv)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return ElevateResult.Failed("取不到当前进程的可执行文件路径（Environment.ProcessPath 为空）");

        // 由 `dotnet QControl.dll` 启动时 ProcessPath 是 dotnet.exe：
        // 拿它加 "runas" 重启只会再起一个空宿主。这里如实拒绝，别假装提权成功。
        var name = Path.GetFileName(exe);
        if (!name.Equals("QControl.exe", StringComparison.OrdinalIgnoreCase))
            return ElevateResult.Failed(
                $"当前宿主是 {name}（{exe}），不是本程序自己的 exe。"
                + "请直接运行 QControl.exe（或双击 run-as-admin.cmd）再试。");

        var args = ElevationPlan.Join(ElevationPlan.ElevatedArguments(argv));

        var result = ShellEx.Launch(exe, "runas", args, Environment.CurrentDirectory,
                                    ShellEx.MaskNoAsync | ShellEx.MaskNoUi,
                                    ShellEx.ShowNormal);

        if (result.Ok) return ElevateResult.Started();
        if (result.Cancelled) return ElevateResult.Cancelled(result.Error);
        return ElevateResult.Failed(result.Error ?? "ShellExecuteEx 失败（没有错误码）");
    }
}
