using System.Diagnostics;
using System.Text.Json.Nodes;

namespace QControl.Hal.Sys;

/// <summary>
/// 退出原厂软件（原厂控制中心 + 独立的 Hotkey OSD）—— `server.App.stop_vendor` 的 C# 版。
///
/// ⚠️ 只有用户点按钮（或打开「启动时自动退出」开关）才会走到这里 ——
///    杀进程不可逆，绝不要无条件自动调用。
///
/// ⚠️ **顺序很关键（2026-10-15 实测）**：先停服务、再收进程。
///    `hkosdservice`（Auto 启动）的宿主进程就是 `HkeyOSDSvis.exe`：
///    先 terminate 进程的话，SCM 会立刻把它拉回来（还带失败恢复），
///    用户看到的就是"点了一会儿又弹"。
///
/// ⚠️ 不动计划任务 `\ControlCenter\ControlCenter`（原厂 daemon 的登录自启）。
///    改别人的计划任务属于系统级改动，交给用户自己决定；这里只保证
///    "现在这一刻它们都不在跑"。要连自启一起关掉走设置页那个开关。
/// </summary>
public static class VendorControl
{
    /// <summary>
    /// 原厂软件进程名（小写比较）。关掉界面后守护进程仍驻留，会和我们抢 FN 键。
    ///
    /// 后两个来自**另一个安装包**「Hotkey OSD」（`C:\Program Files\Hotkey OSD\`）：
    /// FN+1 / FN+3 那张原厂 OSD 图就是 `HkeyUserPS.exe` 画的，由服务常驻拉起。
    /// 旧列表只认 ControlCenter* 三个名字 ⇒ 检测不到、也就关不掉，
    /// 警告一消失用户以为原厂全关了，实际 OSD 照旧（2026-10-15 用户报障）。
    /// </summary>
    private static readonly string[] VendorProcs =
    [
        "controlcenter.exe", "controlcenterdaemon.exe", "cdsosrv.exe",
        "hkeyosdsvis.exe", "hkeyuserps.exe",
    ];

    /// <summary>拉起上面后两个进程的服务名。必须**先**停它，否则 SCM 立刻拉回来。</summary>
    private static readonly string[] VendorServices = ["hkosdservice"];

    /// <summary>
    /// 停服务 + 收残留进程。返回 `{ok, killed[], services[], errors[]}`
    /// （与早先实现逐字段同构，界面直接把 killed / services 拼进提示文案）。
    ///
    /// `ok` 只在**一条错误都没有**时为 true —— 部分成功要如实说，
    /// 否则用户看到"已退出原厂软件"而 FN 键还在弹原厂 OSD。
    /// </summary>
    public static JsonObject Stop()
    {
        var killed = new JsonArray();
        var services = new JsonArray();
        var errors = new JsonArray();

        // ---- 1. 先停服务（正常路径下它的宿主进程会跟着退出）----
        foreach (var name in VendorServices)
        {
            var (code, stdout, stderr) = Shell.Run(ScExe(), "stop " + name, 8_000);
            var msg = (stdout + " " + stderr).Trim();

            // 1062 = 服务未启动（本来就没跑）/ 1060 = 服务不存在（没装 Hotkey OSD
            // 的机器）—— 这两种都算"它现在不碍事"，不是失败。
            if (code == 0 || msg.Contains("1062", StringComparison.Ordinal)
                          || msg.Contains("1060", StringComparison.Ordinal))
            {
                services.Add(name);
            }
            else
            {
                errors.Add($"停服务 {name} 失败：{(msg.Length > 0 ? msg : $"rc={code?.ToString() ?? "null"}")}");
            }
        }

        // 给 SCM 时间把宿主进程收掉，再去看残留
        if (services.Count > 0) Thread.Sleep(1_000);

        // ---- 2. 再收残留进程（原厂控制中心三个 + OSD 两个）----
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception e)
        {
            errors.Add("枚举进程失败：" + e.Message);
            return Result(killed, services, errors);
        }

        foreach (var p in all)
        {
            try
            {
                string name;
                try { name = p.ProcessName + ".exe"; }
                catch { continue; }                    // 进程已退出（读名字会抛）

                if (!VendorProcs.Contains(name.ToLowerInvariant())) continue;

                try
                {
                    // psutil.terminate() 等价 TerminateProcess（不经窗口消息）；
                    // 原厂有窗口，但我们要的是"立刻不在"，不是"问它愿不愿意关"。
                    p.Kill();
                    killed.Add(name);
                }
                catch (Exception e)
                {
                    errors.Add($"{name}: {e.Message}");
                }
            }
            finally
            {
                p.Dispose();
            }
        }

        return Result(killed, services, errors);
    }

    private static JsonObject Result(JsonArray killed, JsonArray services, JsonArray errors)
        => new()
        {
            ["ok"] = errors.Count == 0,
            ["killed"] = killed,
            ["services"] = services,
            ["errors"] = errors,
        };

    /// <summary>
    /// `sc.exe` 全路径。
    /// ⚠️ 读**注册表**判状态才可靠（`sc` 的输出随系统语言变），但停服务本身只能调它；
    ///    输出统一由 <see cref="Shell"/> 按系统代码页解码，中文才不会变乱码。
    /// </summary>
    private static string ScExe()
        => Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                        "System32", "sc.exe");
}
