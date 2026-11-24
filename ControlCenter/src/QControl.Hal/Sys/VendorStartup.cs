using Microsoft.Win32;
using System.Text.Json.Nodes;

namespace QControl.Hal.Sys;

/// <summary>
/// 原厂软件的两个自启动入口（**只读**，设置页「原厂软件」卡用）。
///
/// 2026-10-18 本机实测确认的两条路：
///   ① 计划任务 `\ControlCenter\ControlCenter` → `ControlCenterDaemon.exe /1`
///      （登录触发、运行级别 Highest）—— 「关掉原厂界面后守护进程还会回来」的原因；
///   ② 服务 `hkosdservice`（启动类型 自动）→ 宿主 `HkeyOSDSvis.exe`，
///      FN+1 / FN+3 那张原厂图就是它画的。
///
/// 「抑制」= 任务 Enabled=False + 服务 Start=3(demand，**不是** 4/disabled)，
/// **不删任务、不卸载、不删文件**。用 demand 的理由见 `AppState.SetVendorBlock`：
/// disabled 会把"手动 sc start"一起禁掉，依赖原厂画提示条的模式就再也拉不起来（报 1058）。
/// 所以"自启被抑制"的判定必须把 demand 也算进去 —— 见 <see cref="StartBlocked"/>。
/// 2026-10-22 起这里**可写**了（设置页「抑制原厂开机自启」那个开关）：两个入口都能
/// 改回去，恢复时连服务一起 `sc start` 拉起 —— 只改标志位的话用户得重启才看得见原厂
/// 回来，那是半成品。写操作用 `sc.exe` / COM，**不直接写注册表**（`Reg.cs` 仍是只读，
/// 全项目唯一允许写注册表的地方还是 `Autostart.cs`）。
/// </summary>
public static class VendorStartup
{
    public const string TaskDir = @"\ControlCenter";
    public const string TaskName = "ControlCenter";
    public const string ServiceName = "hkosdservice";

    /// <summary>Windows 服务的 Start 值 → 名字。读注册表拿到的是**数字**，永远可靠。</summary>
    private static readonly Dictionary<int, string> StartName = new()
    {
        [0] = "boot", [1] = "system", [2] = "auto", [3] = "demand", [4] = "disabled",
    };

    /// <summary>`{"path","exists","enabled"}`。`enabled` 为 null = 任务不存在/读不到。</summary>
    public static JsonObject TaskState()
    {
        var (exists, enabled) = TaskScheduler.Query(TaskDir, TaskName);
        return new JsonObject
        {
            ["path"] = TaskDir + @"\" + TaskName,
            ["exists"] = exists,
            ["enabled"] = exists ? enabled : null,
        };
    }

    /// <summary>
    /// `{"name","exists","start","start_label","running"}`。
    ///
    /// `running` 由调用方给（本机就是"`HkeyOSDSvis.exe` 在不在"）——
    /// 再起一次 `sc query` 既慢又要把本地化文本解析一遍，不值得。
    /// </summary>
    public static JsonObject ServiceState(bool running)
    {
        const string sub = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
        var outObj = new JsonObject
        {
            ["name"] = ServiceName,
            ["exists"] = false,
            ["start"] = null,
            ["start_label"] = "",
            ["running"] = false,
        };

        var start = Reg.Int(Registry.LocalMachine, sub, "Start");
        if (start is null) return outObj;      // 没装原厂 → 服务不存在

        var label = StartName.TryGetValue(start.Value, out var n) ? n : start.Value.ToString();
        outObj["exists"] = true;
        outObj["start"] = start.Value;
        outObj["start_label"] = label;
        outObj["running"] = running;
        return outObj;
    }

    /// <summary>
    /// 「自启被抑制」的判据：任务禁用，或服务启动类型是 disabled / **demand**。
    ///
    /// <para>
    /// ⚠️ `demand` 必须算抑制（2026-11-26 审查）：抑制写的就是 demand（见
    /// <see cref="SetServiceStart"/> 与 `AppState.SetVendorBlock` 的说明）。以前
    /// `Program` 的开机复核和 `GET /api/vendor/state` 都只认 `disabled`，于是
    /// 每次开机都判「抑制态被改回来了」、白重跑一遍任务/服务配置，`blocked` 还恒为 false。
    /// </para>
    /// <para>
    /// 只管**存在**的入口：不存在的那条本来就不会自己起来，说"被我们抑制了"是假的。
    /// 开机复核要的是相反口径（不存在 = 不用管），所以那边用 <see cref="TaskSuppressed"/> /
    /// <see cref="ServiceSuppressed"/> 自己拼。
    /// </para>
    /// </summary>
    public static bool StartBlocked(JsonObject task, JsonObject svc)
        => TaskSuppressed(task) || ServiceSuppressed(svc);

    /// <summary>这条任务**存在且**被禁用。</summary>
    public static bool TaskSuppressed(JsonObject task)
        => task["exists"]?.GetValue<bool>() == true && task["enabled"]?.GetValue<bool>() == false;

    /// <summary>这个服务**存在且**启动类型是 disabled / demand（demand 才是抑制的实际写法）。</summary>
    public static bool ServiceSuppressed(JsonObject svc)
    {
        if (svc["exists"]?.GetValue<bool>() != true) return false;
        var label = svc["start_label"] is JsonValue lv && lv.TryGetValue<string>(out var s) ? s : null;
        return label is "disabled" or "demand";
    }

    // ================================================================ 写：抑制 / 恢复
    // 三种操作，全部**可逆**：不删任务、不卸载服务、不动原厂的任何文件。
    // 权限：改计划任务和服务启动类型都需要管理员（非管理员如实失败，别假装成功）。

    /// <summary>合法的启动类型（`sc config start=` 的取值，也是 `start_label` 的取值）。</summary>
    private static readonly HashSet<string> ValidStart =
        new(["boot", "system", "auto", "demand", "disabled"], StringComparer.Ordinal);

    /// <summary>
    /// 启用 / 禁用原厂计划任务。返回 (成功, 说明)。
    ///
    /// ⚠️ 用 COM 的 `IRegisteredTask::Enabled`，**绝不用 DeleteTask** —— 删掉就再也
    ///    恢复不了原样（只有原厂的修复/重装会重建），而本项目对原厂的原则一直是"抑制而非清除"。
    /// ⚠️ 写完必须**回读**：COM 回 S_OK 但值没变的情况在本项目踩过（`RegisterTask` 同款坑）。
    /// 失败才退回 `schtasks.exe`（与早先实现同款兜底；它需要 CREATE_NO_WINDOW，
    /// `Shell.Run` 已经带上了）。
    /// </summary>
    public static (bool Ok, string Detail) SetTaskEnabled(bool on)
    {
        var firstErr = "";
        object? com = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                firstErr = "Schedule.Service 不可用";
            }
            else
            {
                dynamic svc = Activator.CreateInstance(type)!;
                com = (object)svc;
                svc.Connect();
                dynamic task = svc.GetFolder(TaskDir).GetTask(TaskName);
                task.Enabled = on;
                if ((bool)task.Enabled == on)
                    return (true, "原厂计划任务已" + (on ? "启用" : "禁用"));
                firstErr = $"设置后回读仍为 {(bool)task.Enabled}";
            }
        }
        catch (Exception e)
        {
            firstErr = e.Message;
        }
        finally
        {
            if (com is not null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(com); }
                catch { /* 释放失败不影响结果 */ }
            }
        }

        var full = TaskDir + @"\" + TaskName;
        var (code, output, err) = Shell.Run(SchtasksExe(),
            $"/Change /TN \"{full}\" {(on ? "/ENABLE" : "/DISABLE")}", 8000);

        // 回读仍是唯一判据：schtasks 在中文系统上要么 0 要么 1，但输出是本地化的，
        // 解析它必然在别的语言上失效（见 TaskScheduler 顶部那段为什么不用 schtasks）。
        var (exists, enabled) = TaskScheduler.Query(TaskDir, TaskName);
        if (exists && enabled == on)
            return (true, "原厂计划任务已" + (on ? "启用" : "禁用") + "（schtasks 兜底）");

        var msg = string.IsNullOrWhiteSpace(output) ? err : output;
        return (false, $"改计划任务失败（rc={code?.ToString() ?? "-"}）：{firstErr}"
                       + (msg.Length > 0 ? "；schtasks：" + msg.Trim() : ""));
    }

    /// <summary>
    /// 改服务启动类型。`kind` 只接受 boot / system / auto / demand / disabled。
    ///
    /// ⚠️ `sc` 的语法是 `start= <值>`（等号后**必须有空格**）—— 少了这个空格它会把
    ///    `start=disabled` 当成一个未知参数，回一个完全看不懂的错误码。早先实现在这里
    ///    也留了同样的警告，别再踩。
    /// ⚠️ 判成功靠**回读注册表的 Start 值**（`ServiceState`），不靠 sc 的输出文字 ——
    ///    那是本地化的（"AUTO_START" / "自动"），换语言系统会静默失效。
    /// </summary>
    public static (bool Ok, string Detail) SetServiceStart(string kind)
    {
        if (!ValidStart.Contains(kind)) return (false, "不支持的启动类型：" + kind);

        var (code, output, err) = Shell.Run(ScExe(), $"config {ServiceName} start= {kind}", 8000);
        var st = ServiceState(running: false);
        if (st["exists"]?.GetValue<bool>() == true && Str(st["start_label"]) == kind)
            return (true, $"服务 {ServiceName} 启动类型 = {Label(kind)}");

        var msg = string.IsNullOrWhiteSpace(output) ? err : output;
        return (false, $"改服务启动类型失败（rc={code?.ToString() ?? "-"}）：{msg.Trim()}");
    }

    /// <summary>
    /// 启动服务 —— 恢复自启时用。
    /// 「恢复自启」如果只是把 Start 改回 auto、服务却还在停着，用户得重启电脑才看得见
    /// 原厂回来，那就是半成品（早先实现同一条结论）。
    /// 已在运行（SCM 报 1056）算成功：目的已经达到了。
    /// </summary>
    public static (bool Ok, string Detail) StartService()
    {
        var (code, output, err) = Shell.Run(ScExe(), $"start {ServiceName}", 12000);
        var msg = string.IsNullOrWhiteSpace(output) ? err : output;

        // 1056 = ERROR_SERVICE_ALREADY_RUNNING。有的 sc 版本把它放进输出而不是退出码。
        if (code == 0 || code == 1056 || msg.Contains("1056", StringComparison.Ordinal))
            return (true, $"服务 {ServiceName} 已在运行");

        return (false, $"服务没启动起来（rc={code?.ToString() ?? "-"}）：{msg.Trim()}");
    }

    /// <summary>
    /// 停服务 —— 「提示条全交给我们」时用（原厂 OSD 和我们的卡片二选一，不能同时画）。
    ///
    /// ⚠️ 只 `sc stop`，**不改启动类型**：下次开机它照样自动起来，那时由
    ///    `AppState.ApplyOsdMode` 再按当前模式停一次。要是顺手把 Start 改成 disabled，
    ///    用户切回「全原厂」时就得再改回来 —— 一次操作改两个东西，后患更多。
    /// </summary>
    public static (bool Ok, string Detail) StopService()
    {
        var (code, output, err) = Shell.Run(ScExe(), $"stop {ServiceName}", 12000);
        var msg = string.IsNullOrWhiteSpace(output) ? err : output;

        // 1062 = ERROR_SERVICE_NOT_ACTIVE：本来就没在跑，目的已经达到。
        if (code == 0 || code == 1062 || msg.Contains("1062", StringComparison.Ordinal))
            return (true, $"服务 {ServiceName} 已停止");

        return (false, $"服务没停下来（rc={code?.ToString() ?? "-"}）：{msg.Trim()}");
    }

    /// <summary>启动类型的显示名（界面与说明文案用，别在别处再抄一份）。</summary>
    public static string Label(string kind) => kind switch
    {
        "boot" => "引导",
        "system" => "系统",
        "auto" => "自动",
        "demand" => "手动",
        "disabled" => "已禁用",
        _ => kind,
    };

    private static string System32Exe(string name)
        => Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                        "System32", name);

    private static string ScExe() => System32Exe("sc.exe");
    private static string SchtasksExe() => System32Exe("schtasks.exe");

    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
