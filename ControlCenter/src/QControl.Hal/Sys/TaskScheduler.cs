namespace QControl.Hal.Sys;

/// <summary>
/// 计划任务查询（**只读**）。走 COM `Schedule.Service`。
///
/// 为什么不用 `schtasks /query`：
///   ① 它的输出是人读表格，列名随系统语言变（简中「任务名 / 状态 / 下次运行时间」），
///      解析必然在别的语言上静默失效；
///   ② 判断"任务被禁用了"要读 `Enabled` 字段，`schtasks` 的列表里只有状态文字
///      「已禁用」，又是一个本地化字符串；
///   ③ `schtasks /xml` 的输出编码还不一定是 ANSI（可能是 UTF-16），
///      解码错就会得到一串带 \0 的乱码，且只在中文任务名上暴露。
///   COM 的属性是强类型的 `bool`，跟语言无关。
///
/// ⚠️ "不存在"和"读不到"都返回 `exists=false`：这里只用于显示和"跳过"，
///    误判的代价只是少一条提示。**绝不能**用它来决定写操作（写操作必须自己确认）。
/// </summary>
public static class TaskScheduler
{
    /// <summary>
    /// 查任务。`folder` 传 `"\\"` 表示根目录。
    /// 返回 `(是否存在, 是否启用)`；任务不存在时 `Enabled` 为 null ——
    /// 界面靠这个区分「没有这个任务」和「任务被禁用了」。
    /// </summary>
    public static (bool Exists, bool? Enabled) Query(string folder, string name)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return (false, null);

            dynamic svc = Activator.CreateInstance(type)!;
            try
            {
                svc.Connect();
                dynamic dir = svc.GetFolder(folder);
                dynamic task = dir.GetTask(name);
                return (true, (bool)task.Enabled);
            }
            finally
            {
                // COM 对象没有 IDisposable；显式放掉引用让 RCW 尽快释放
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(svc);
            }
        }
        catch
        {
            // 目录不存在 / 任务不存在 / COM 不可用（非交互会话）—— 一律按"不存在"处理
            return (false, null);
        }
    }

    // ------------------------------------------------------------ 读动作（迁移用）

    /// <summary>
    /// 读任务的第一条动作：`(exe 路径, 参数)`。读不到就 `(null, null)`。
    ///
    /// 唯一用途是**判断自启入口还能不能用**（升级后目标 exe 被删 / 被换名，
    /// 见 `Autostart.RepairDeadEntry`）。只取第一条：本项目的任务永远只有一条 Exec。
    ///
    /// ⚠️ `Definition` 要读任务的安全描述符，普通权限**可能**拿到访问被拒 ——
    ///    那种情况按"读不到"处理，调用方会跳过修复而不是乱改。
    /// </summary>
    public static (string? Exe, string? Args) ActionCommand(string folder, string name)
    {
        object? com = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return (null, null);

            dynamic svc = Activator.CreateInstance(type)!;
            com = (object)svc;
            svc.Connect();
            dynamic task = svc.GetFolder(folder).GetTask(name);
            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count < 1) return (null, null);

            dynamic first = actions.Item(1);
            // 只认 Exec（TASK_ACTION_EXEC = 0）；其余类型本项目不产生，当作读不到
            if ((short)first.Type != 0) return (null, null);

            var exe = (string)first.Path;
            var args = (string)first.Arguments;
            return (string.IsNullOrWhiteSpace(exe) ? null : exe,
                    string.IsNullOrWhiteSpace(args) ? null : args);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (com is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com); }
                catch { /* 释放失败不影响结果 */ }
            }
        }
    }

    // ------------------------------------------------------------ 读设置（补策略用）

    /// <summary>
    /// 读任务的「失败后重启」设置：`(次数, 间隔)`。读不到返回 `(null, null)`。
    ///
    /// 唯一用途是**判断老任务要不要补上这条设置**（`Autostart.RepairRestartPolicy`）——
    /// 这次加的是 XML 里的 `<RestartOnFailure>`，而已有任务不会被重写。
    ///
    /// ⚠️ 和 <see cref="ActionCommand"/> 一样，`Definition` 可能要任务的安全描述符，
    ///    普通权限**可能**被拒 —— 那种情况必须回 `null`（= "不知道"），不能回 0，
    ///    否则调用方会把它当成"没设置"而去做需要管理员的整段重写。
    /// </summary>
    public static (int? Count, string? Interval) RestartPolicy(string folder, string name)
    {
        object? com = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return (null, null);

            dynamic svc = Activator.CreateInstance(type)!;
            com = (object)svc;
            svc.Connect();
            dynamic settings = svc.GetFolder(folder).GetTask(name).Definition.Settings;
            return ((int)settings.RestartCount, (string?)settings.RestartInterval);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (com is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com); }
                catch { /* 释放失败不影响结果 */ }
            }
        }
    }

    // ------------------------------------------------------------ 启动（零 UAC 的入口）

    /// <summary>`TASK_STATE_RUNNING`（`IRegisteredTask.State` 的枚举值）。</summary>
    private const int TaskStateRunning = 4;

    /// <summary>`SCHED_E_TASK_ATTEMPTED` 之类都不重要，只用这一个：任务正在运行。</summary>
    private const uint ErrTaskAlreadyRunning = 0x8004131E;

    /// <summary>
    /// **按需启动一个已注册的任务** —— 这是「装一次，之后所有启动都不弹 UAC」的关键。
    ///
    /// 任务计划服务用任务里存的 `<RunLevel>HighestAvailable</RunLevel>` 令牌启动它，
    /// 调用方是普通权限也照样给完整管理员令牌，而且**不经过 AppInfo ⇒ 不弹 UAC**。
    /// 前提有两条，缺一不可（本项目的 `Autostart.BuildTaskXml` 两个都满足）：
    ///   · Principal 是**当前用户**（不是 SYSTEM / 别的账号）；
    ///   · `<AllowStartOnDemand>true</AllowStartOnDemand>`，且执行时用户已登录
    ///     （`InteractiveToken` 要求会话在）。
    ///
    /// 返回三态分得开：`(Ok=false, AlreadyRunning=true)` 不是错误 —— 说明实例本来就
    /// 在跑（`MultipleInstancesPolicy=IgnoreNew` 下再 Run 会抛 0x8004131E），
    /// 调用方应当去"叫窗口"而不是报失败。
    /// </summary>
    public static (bool Ok, bool AlreadyRunning, string Error) Run(string folder, string name)
    {
        object? com = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return (false, false, "Schedule.Service 不可用");

            dynamic svc = Activator.CreateInstance(type)!;
            com = (object)svc;
            svc.Connect();
            dynamic dir = svc.GetFolder(folder);
            dynamic task = dir.GetTask(name);

            // 先看状态：正在运行就不用 Run（省一次异常，也避免把 AlreadyRunning 判丢）
            if ((int)task.State == TaskStateRunning) return (false, true, "任务已在运行");

            task.Run(null);
            return (true, false, "");
        }
        catch (Exception e)
        {
            var already = e is System.Runtime.InteropServices.COMException ce
                          && (uint)ce.HResult == ErrTaskAlreadyRunning;
            return (false, already, e.Message);
        }
        finally
        {
            if (com is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com); }
                catch { /* 释放失败不影响结果 */ }
            }
        }
    }

    // ------------------------------------------------------------ 写（注册 / 删除）

    /// <summary>`ITaskFolder::RegisterTask` 的 Flags：存在则覆盖（TASK_CREATE_OR_UPDATE）。</summary>
    private const int TaskCreateOrUpdate = 6;

    /// <summary>`TASK_LOGON_INTERACTIVE_TOKEN` —— 用当前登录令牌跑，不存密码。</summary>
    private const int TaskLogonInteractiveToken = 3;

    /// <summary>
    /// 注册（或覆盖）一个任务。**XML 由调用方给**（见 `Autostart.BuildTaskXml`）：
    /// 逐字段 COM 赋值踩过早绑定的坑（字段一多就漏），传 XML 只有一次调用。
    ///
    /// ⚠️ 只有管理员能注册 `RunLevel=HighestAvailable` 的任务；非管理员会在这里
    ///    拿到 COM 异常（通常是 0x80070005）。**这是预期结果**，调用方负责解释，
    ///    不要当成"未知错误"吞掉。
    ///
    /// ⚠️ 注册成功后必须**回读**（`Query`）确认。COM 返回 S_OK 但任务查不到的情况
    ///    真出现过 —— 那时如果直接报"已开启"，用户开机会发现根本没起来。
    /// </summary>
    public static bool Register(string folder, string name, string xml, string userId,
                                out string error)
    {
        error = "";
        object? com = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                error = "Schedule.Service 不可用（计划任务服务未启动？）";
                return false;
            }

            dynamic svc = Activator.CreateInstance(type)!;
            com = (object)svc;
            svc.Connect();
            dynamic dir = svc.GetFolder(folder);

            // 七个参数一个都不能省。Password / Sddl 必须发 **VT_EMPTY（null）**，
            // 不能发空字符串：空串会被当成"给了个空密码"去校验，注册直接回
            // 0x8007052E「用户名或密码不正确」（非管理员实测踩到，极难从错误码
            // 联想到是引号里多了两个字节）。InteractiveToken 本来就不用密码。
            dir.RegisterTask(name, xml, TaskCreateOrUpdate, userId, null,
                             TaskLogonInteractiveToken, null);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            if (com is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com); }
                catch { /* 释放失败不影响结果 */ }
            }
        }
    }

    /// <summary>删除任务。任务本来就不存在时返回 true（达成目的），与 `tray.delete_task` 同口径。</summary>
    public static bool Delete(string folder, string name, out string error)
    {
        error = "";
        object? com = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                error = "Schedule.Service 不可用";
                return false;
            }

            dynamic svc = Activator.CreateInstance(type)!;
            com = (object)svc;
            svc.Connect();
            dynamic dir = svc.GetFolder(folder);
            dir.DeleteTask(name, 0);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            // 任务不存在 = 已经是想要的状态，别把"删了个不存在的"报成失败
            return !Query(folder, name).Exists;
        }
        finally
        {
            if (com is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com); }
                catch { /* 忽略 */ }
            }
        }
    }
}
