using System.Text.Json.Nodes;

namespace QControl.App.Ui.SelfTest;

/// <summary>
/// WPF 窄写接口（<see cref="UiActions"/>）的自检 —— <c>--ui-actions-selftest</c>。
///
/// 为什么要单独一条：这条链路是"造一个 ApiRequest 再去调 PostHandlers"，
/// 中间任何一处接错（序列化、状态码映射、ok 判定）界面上的表现都是**静默失败**，
/// 编译期完全看不出来。三条用例覆盖三种回包：
///   ① 200 + ok:true —— 只读端点（取某档曲线），不碰硬件；
///   ② 403 —— 非管理员时发一条 EC 写入，验证"没被允许"能如实报出来（不是假成功）；
///      管理员下**跳过这一条**：那是真的会写硬件的请求，不能在自检里跑；
///   ③ 400 —— 故意漏参，验证"业务拒绝"不会被当成成功。
/// 全程**不写硬件**：非管理员的写入在 EcHal 里就被拦住（抛 WriteDenied），到不了 EC。
/// </summary>
internal static class UiActionsCheck
{
    public static int Run(AppState? state)
    {
        if (state is null)
        {
            Console.Error.WriteLine("[uiact] 没有 AppState（没找到 model.json），无法自检");
            return 1;
        }

        var failed = 0;

        // ① 只读端点：应当 ok
        var curve = UiActions.Post(state, "/api/fancurve/mode-curve", new JsonObject { ["mode"] = "office" });
        Report("只读端点 mode-curve", curve.Ok, curve.Error);
        if (!curve.Ok) failed++;

        // ② 非管理员发一条 EC 写入：必须是 403 → ok:false 且带原因。
        //    ⚠️ arm 本身不判权限（它只是给下一条写开一次性授权，见 EcHal.Arm），
        //       所以这里测的是**真正的写**：非管理员的写入会在 EcHal 里被拦下、
        //       连 EC 通道都进不去，所以这条用例是安全的。
        if (state.Ec.IsAdmin())
        {
            Console.WriteLine("[uiact] SKIP 403 映射（当前是管理员：这条用例会真的写风扇）");
        }
        else
        {
            var arm = UiActions.Post(state, "/api/ec/arm", new JsonObject { ["cmd"] = 0x0300 });
            var fan = UiActions.Post(state, "/api/ec/fan", new JsonObject { ["cpu"] = 100, ["gpu"] = 100 });
            // 两种合法拒绝都要算过：
            //   ① 软件曲线没开（2026-11-18 起的出厂默认）—— 手动写入本来就该被挡；
            //   ② 曲线开着但非管理员 —— 权限边界。
            // 只认「管理员」会把默认配置下的自检判成失败，那反倒说明自检写死了实现细节。
            var reason = fan.Error ?? "";
            var rejected = !fan.Ok
                           && (reason.Contains("管理员", StringComparison.Ordinal)
                               || reason.Contains("软件曲线", StringComparison.Ordinal)
                               // 散热页总开关已从「软件曲线」改叫「自定义风扇」；文案跟着换了，
                               // 自检也必须认新词，否则默认配置（总开关关着）会被判成失败。
                               || reason.Contains("自定义风扇", StringComparison.Ordinal));
            Report("非管理员写入 → 403", rejected, fan.Error);
            if (!rejected) failed++;
            // arm 在非管理员下也返回 ok（它只记一条一次性授权），界面据此继续走写入、
            // 由写入那一步报出真正的拒绝 —— 这里不重复断言。
            _ = arm;
        }

        // ③ 坏请求：400 必须落成 ok:false 且带原因（绝不能被当成成功）
        var bad = UiActions.Post(state, "/api/ec/raw", new JsonObject());
        Report("非法参数 ec/raw（期望失败并带原因）",
            !bad.Ok && !string.IsNullOrWhiteSpace(bad.Error), bad.Error);
        if (bad.Ok || string.IsNullOrWhiteSpace(bad.Error)) failed++;

        Console.WriteLine(failed == 0 ? "[uiact] 全部通过" : $"[uiact] 失败 {failed} 条");
        return failed == 0 ? 0 : 2;
    }

    private static void Report(string name, bool pass, string? error)
    {
        var tail = string.IsNullOrWhiteSpace(error) ? "" : " · " + error;
        Console.WriteLine($"[uiact] {(pass ? "PASS" : "FAIL")} {name}{tail}");
    }
}
