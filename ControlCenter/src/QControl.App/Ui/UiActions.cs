using System.Text;
using System.Text.Json.Nodes;
using QControl.App.Http;

namespace QControl.App.Ui;

/// <summary>写操作的结果。**ok:false 是正常返回值**（守卫拒绝 / 硬件报错），不是异常。</summary>
internal readonly record struct ActResult(bool Ok, string? Error, JsonObject? Raw)
{
    public static ActResult Fail(string error) => new(false, error, null);

    /// <summary>给用户看的失败原因：后端给的就用它，没有才用兜底文案。</summary>
    public string Why(string fallback) => string.IsNullOrWhiteSpace(Error) ? fallback : Error!;
}

/// <summary>
/// WPF 的**窄写接口** —— 唯一的写路径。
///
/// 为什么是"造一个假的 ApiRequest 去调 PostHandlers"，而不是在 UI 里直接
/// <c>st.Ec.SetPerfMode()</c>：
///   ① 守卫只有一份。管理员判定 / EC 总闸 / verified 硬拦 / 高危 arm / 边界钳制
///      全在 EcHal 与 PostHandlers 里，UI 绕过它们的那天就是"界面说成功、硬件没动"；
///   ② 副作用只有一份。切模式要顺手记 <c>presets.perf.last_mode</c>、背光要落
///      <c>presets.backlight</c> —— 这些都在 PostHandlers 里，抄一遍必然漂移；
///   ③ 错误语义只有一份。403（没被允许）与 200+ok:false（业务拒绝）的区别是
///      界面文案的依据，抄一遍就会出现"一处说需管理员、另一处说参数错"。
/// 代价只是序列化一次 body（几十字节），且 UI 线程上不做任何 EC 现算。
///
/// ⚠️ 只允许传**写**端点（POST）。GET 走 <see cref="LocalApi"/> 的进程内快照，
///    别在这里顺手加读接口 —— 读接口在 UI 线程上现算会卡界面。
/// ⚠️ 绝对不要从这里发 <c>/api/app/quit</c>：那会把用户的后端关掉。
/// </summary>
internal static class UiActions
{
    public static ActResult Post(AppState? state, string path, JsonObject? body = null)
    {
        if (state is null) return ActResult.Fail("后端没有初始化（没找到 model.json 或启动失败），写操作不可用");

        ApiResponse? response;
        try
        {
            var json = body?.ToJsonString(ApiResponse.JsonOptions) ?? "";
            response = PostHandlers.TryHandle(new ApiRequest("POST", path, "", json), state, () => { });
        }
        catch (Exception e)
        {
            return ActResult.Fail("调用失败：" + e.Message);
        }

        // null = 路由表里有这条、但 PostHandlers 没接 —— 与 HTTP 侧的 501 同义
        if (response is null) return ActResult.Fail($"{path} 还没有实现");

        JsonObject? node = null;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(response.Body)) as JsonObject;
        }
        catch
        {
            // 回包不是 JSON：状态码仍然可信，下面按状态码报错
        }

        var error = Str(node?["error"]);

        if (response.Status == 403)
            return new ActResult(false, error ?? "写入被拒绝（需要管理员权限，或这台机型没有 verified 标定）", node);

        if (response.Status != 200)
            return new ActResult(false, error ?? $"HTTP {response.Status}", node);

        var ok = node?["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        if (!ok)
            // 报障要根因：界面上那条"保存失败"toast 关掉就没了，日志里必须留一份
            // 带原因的记录，否则事后只能靠猜。
            Console.Error.WriteLine($"[ui] POST {path} 失败：{error ?? "（后端没给原因）"}");
        return new ActResult(ok, ok ? null : error ?? "失败", node);
    }

    private static string? Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
