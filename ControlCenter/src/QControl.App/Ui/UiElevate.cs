using QControl.Core.Startup;
using QControl.Hal.Sys;

namespace QControl.App.Ui;

/// <summary>
/// 界面里「以管理员身份重启」的实现 —— 只负责"拿参数 + 起进程"。
///
/// 为什么不复用 Program 的启动参数：这个按钮点是**在只读实例自己身上**点的，参数得清洗：
///   · 去掉 `--tray` / `--no-open`：用户正看着窗口，重启后必须自己回来，否则看着像"没反应"；
///   · 去掉 `--no-elevate`：只读实例是带着它起的，照抄过去新进程又判成"别提权"；
///   · 按当前页重给 `--page`：换完进程跳回总览会让人以为刚才那页丢了；
///   · 加上 `--takeover`：新进程要先请**本进程**退出、等端口真空了再绑（端口在我们手里）；
///   · 加上 `--elevated`：标记"已经是提权后的进程"，防反复提权。
/// 这些规则都在 `Core/Startup/ElevationPlan.TakeoverArguments` 里，有单测；这里不复制。
///
/// ⚠️ 起完**不要**在这里关后端：新进程会 `POST /api/app/quit` 请我们退出。
///    自己抢着退，会把"新进程还没绑上端口"那段窗口期暴露成一次启动失败。
///
/// 两条路，优先零 UAC 的那条：
///   ① `Autostart.HandoverByTask` —— 请 HighestAvailable 自启任务代启。任务计划服务
///      用任务里存的完整令牌，**整个提权协商被跳过，一发 UAC 都不弹**。任务传不了
///      参数，所以要开哪一页写在 `Core.Startup.OpenRequest` 那张纸条里；
///   ② `runas` —— 任务不存在/被禁用的兜底（第一次装任务时走到，那一次之后就有了）。
/// </summary>
internal static class UiElevate
{
    public static ElevateResult RestartElevated(string? page)
    {
        // ⚠️ 必须声明"该拉起的是本程序这份"：任务若被另一份副本（安装版 × 绿色版）
        //    占着，代启起来的是那份，这里就会变成"提权了但换了个目录"，
        //    用户看着窗口回来了，实际 EC/配置全在一个不相干的副本上。
        if (Autostart.HandoverByTask(page, out var detail, Autostart.SelfCommand()))
        {
            Console.WriteLine("[elevate] " + detail);
            return ElevateResult.Started();
        }

        Console.WriteLine("[elevate] " + detail + " —— 退回 UAC 提权。");

        // [0] 是本进程的可执行文件，其余才是用户/启动链传来的参数
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        return Elevation.RestartSelf(ElevationPlan.TakeoverArguments(argv, page));
    }
}
