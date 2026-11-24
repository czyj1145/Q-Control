using QControl.Hal.Sys;
// ⚠️ 必须起别名：System.Threading.Tasks.TaskScheduler 与 Hal 的同名类冲突
//    （implicit usings 已经把 System.Threading.Tasks 引进来了）
using Scheduler = QControl.Hal.Sys.TaskScheduler;

namespace QControl.App;

/// <summary>
/// 开机自启的真机自检（`--autostart-selftest`）。
///
/// **不碰真实的自启入口**：用临时任务名 `QControl.Selftest` 和临时
/// Run 值 `QControlSelftest` 走一遍"注册 → 回读 → 删除 → 回读为空"，
/// 跑完就清掉。真实任务名是 `QControl`，验证时误删它
/// 会直接改变用户的开机行为 —— 这种事不能拿"我觉得没影响"来赌。
///
/// 这个自检能证明的事：
///   ① 计划任务 XML 是 Windows 认的（`RegisterTask` 不报错）；
///   ② 注册后**回读得到**（COM 回 S_OK 但任务不存在的情况出现过）；
///   ③ 删除干净（不留垃圾任务）；
///   ④ 注册表 Run 项读写正常；
///   ⑤ 自启命令到底指向哪个 exe（静默启动器在不在）。
/// </summary>
internal static class AutostartCheck
{
    private const string TempTask = "QControl.Selftest";
    private const string TempRun = "QControlSelftest";

    public static int Run(int port)
    {
        var failed = 0;
        var cmd = Autostart.TrayCommand(port);

        Console.WriteLine(new string('=', 66));
        Console.WriteLine(" 开机自启自检（临时名字，不碰真实自启入口）");
        Console.WriteLine(new string('=', 66));

        // ---- [1] 现状 ----
        Console.WriteLine();
        Console.WriteLine("[1] 当前状态");
        var task = Scheduler.Query(@"\", Autostart.TaskName);
        Console.WriteLine($"  管理员           : {(Autostart.IsAdmin() ? "是" : "否（注册最高权限任务会失败，这是预期）")}");
        Console.WriteLine($"  真实任务存在     : {(task.Exists ? "是" : "否")}"
                          + (task.Exists ? $"（启用：{task.Enabled}）" : ""));
        Console.WriteLine($"  Run 项           : {Autostart.RunValue() ?? "（空）"}");
        Console.WriteLine($"  IsOn()           : {(Autostart.IsOn() ? "开" : "关")}");
        // 任务"存在"和"起得来"是两件事：升级后指向的目标 exe 可能已经不在了
        var (taskExe, taskArgs) = Scheduler.ActionCommand(@"\", Autostart.TaskName);
        Console.WriteLine($"  任务实际指向     : {(taskExe ?? "（读不到）")}"
                          + (taskArgs is null ? "" : " " + taskArgs));
        if (taskExe is not null && !File.Exists(taskExe))
            Console.WriteLine("  [WARN] 上面这个路径不存在 —— 开机不会起任何东西"
                              + "（下次以管理员启动时会自动改指回主程序）");
        Console.WriteLine();
        Console.WriteLine("  自启将拉起：");
        Console.WriteLine($"    exe            : {cmd.Exe}");
        Console.WriteLine($"    args           : {cmd.Args}");
        Console.WriteLine($"    cwd            : {cmd.WorkingDir}");
        Console.WriteLine($"    入口可用        : {(Autostart.SelfEntryPresent(cmd) ? "是（主程序自己，无控制台）" : "**否** — 主程序不在：" + cmd.Exe)}");
        if (!Autostart.SelfEntryPresent(cmd)) failed++;

        // ---- [2] 计划任务往返 ----
        Console.WriteLine();
        Console.WriteLine("[2] 计划任务注册 / 回读 / 删除（临时任务名）");

        // 非管理员注册不了 HighestAvailable，但「XML 能不能被 Windows 解析」
        // 这件事必须验证 —— 退回成 LeastPrivilege 的临时任务只验通路，
        // 生产路径永远是 HighestAvailable（登录时免 UAC + 能写 EC）。
        var admin = Autostart.IsAdmin();
        var runLevel = admin ? "HighestAvailable" : "LeastPrivilege";
        if (!admin)
            Console.WriteLine("  非管理员：临时任务改用 LeastPrivilege 注册，只验 XML/COM 通路；"
                              + "\n           真实注册需要管理员（开 UAC 后重跑本自检即可全绿）");

        var xml = Autostart.BuildTaskXml(cmd, runLevel);
        var who = $"{Environment.UserDomainName}\\{Environment.UserName}";

        if (Scheduler.Register(@"\", TempTask, xml, who, out var err))
        {
            Console.WriteLine($"  [ OK ] RegisterTask 成功（{runLevel}）");
            if (Scheduler.Query(@"\", TempTask).Exists)
            {
                Console.WriteLine("  [ OK ] 回读：任务存在");

                // XML 里加了 <RestartOnFailure>（2026-11-02）——「能被注册」不等于「真生效」：
                // 元素名拼错、放到 schema 不认的位置，Windows 都是**静默忽略**，任务照样建得出来。
                // 所以必须回读设置本身，这是本项目对 COM 的一贯口径（别只看 S_OK）。
                var (rc, ri) = Scheduler.RestartPolicy(@"\", TempTask);
                if (rc == Autostart.TaskRestartCount)
                    Console.WriteLine($"  [ OK ] 失败重启：{rc} 次 / 间隔 {ri ?? "（读不到）"}");
                else
                {
                    Console.WriteLine($"  [FAIL] 失败重启没写进任务设置（读到 {(rc is null ? "读不到" : rc.ToString())}，"
                                      + $"应为 {Autostart.TaskRestartCount}）");
                    failed++;
                }
            }
            else
            {
                Console.WriteLine("  [FAIL] 回读：任务不存在（COM 说成功但系统里没有）");
                failed++;
            }
        }
        else
        {
            Console.WriteLine("  [FAIL] RegisterTask 失败：" + err);
            if (!admin) Console.WriteLine("         权限导致的失败在管理员下会消失，"
                                          + "但这里算失败，别把「没验到」当成「验过了」");
            failed++;
        }

        if (Scheduler.Query(@"\", TempTask).Exists)
        {
            if (Scheduler.Delete(@"\", TempTask, out var delErr)
                && !Scheduler.Query(@"\", TempTask).Exists)
                Console.WriteLine("  [ OK ] 已删除，回读确认不存在");
            else
            {
                Console.WriteLine("  [FAIL] 删除失败：" + delErr);
                failed++;
            }
        }

        // ---- [3] 注册表 Run 往返 ----
        Console.WriteLine();
        Console.WriteLine("[3] 注册表 Run 项往返（临时值名）");
        if (Autostart.SetRun(cmd, TempRun))
        {
            var back = Autostart.ReadRun(TempRun);
            Console.WriteLine($"  [ OK ] 写入并回读：{back}");
            if (back != cmd.CommandLine)
            {
                Console.WriteLine($"  [FAIL] 回读值与预期不同：{cmd.CommandLine}");
                failed++;
            }
        }
        else
        {
            Console.WriteLine("  [FAIL] 写入 HKCU Run 项失败");
            failed++;
        }
        if (Autostart.DeleteRun(TempRun) && Autostart.ReadRun(TempRun) is null)
            Console.WriteLine("  [ OK ] 已删除，回读为空");
        else
        {
            Console.WriteLine("  [FAIL] 删除临时 Run 项失败");
            failed++;
        }

        // ---- 结论 ----
        Console.WriteLine();
        Console.WriteLine(new string('=', 66));
        Console.WriteLine(failed == 0
            ? " => 通过：自启链路可用（真实自启入口未改动）"
            : $" => 不通过：{failed} 项失败");
        Console.WriteLine(new string('=', 66));
        return failed == 0 ? 0 : 1;
    }
}
