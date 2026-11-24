using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace QControl.App.Http;

/// <summary>进来的一个请求（不含 header —— 路由与桩模式都用不上）。</summary>
internal sealed record ApiRequest(string Method, string Path, string Query, string Body);

/// <summary>要回出去的响应。Body 是已经编码好的字节，避免每处重复处理编码。</summary>
/// <param name="IsStub">
/// 这份 body 是不是 `tests/fixtures` 里的桩样本。只影响响应头 `X-CC-Stub` ——
/// ⚠️ 以前这个头**无条件写 1**，于是真机模式下拿到的真数据（`/api/status`、
/// `/api/gpu/info` …）也被标成"桩"，谁拿它判断数据真伪就会全信错。
/// </param>
internal sealed record ApiResponse(int Status, string ContentType, byte[] Body, bool IsStub = false)
{
    public const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>
    /// JSON 响应的序列化选项。
    ///
    /// ⚠️ `UnsafeRelaxedJsonEscaping` **必须**加：默认编码器会把中文转成 `\u4E2D`，
    ///    这里也是原样输出中文，不转义成 \uXXXX。
    ///    不加这行，A/B 逐字节对比会满屏差异，虽然界面能解析，但"契约一致"无从验证。
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static ApiResponse Json(string json, int status = 200)
        => new(status, JsonContentType, Encoding.UTF8.GetBytes(json));

    public static ApiResponse Json<T>(T value, int status = 200)
        => Json(JsonSerializer.Serialize(value, JsonOptions), status);

    /// <summary>
    /// 「这个端点存在，但还没接上实现」。
    ///
    /// 为什么用 501 而不是先回个假的 `{ok:true}`：
    ///   界面写操作一律判 `r.get("ok")`，回 true 等于告诉用户"已生效"，
    ///   而实际上什么都没做 —— 那正是本项目最忌讳的"显示错的东西"。
    ///   501 + 明确文案，谁看都不会误会。
    /// </summary>
    public static ApiResponse Unimplemented(string method, string path)
        => Json(JsonSerializer.Serialize(new
        {
            ok = false,
            error = $"{method} {path} 尚未实现（端点已冻结，但没有接上实现）",
        }, JsonOptions), 501);

    /// <summary>
    /// POST 未知接口的 404。
    ///
    /// ⚠️ GET 和 POST 的 404 **形状不同**，这是既有契约，不能统一：
    ///   POST 回 {"ok":false,"error":"未知接口"} + 404；
    ///   GET  回一张 **HTML** 错误页。
    ///   统一成 JSON 会让 GET 的 404 与契约不一致（P7 逐字节 diff 时会冒出来）。
    /// </summary>
    public static ApiResponse NotFoundJson()
        => Json("""{"ok":false,"error":"未知接口"}""", 404);

    /// <summary>GET 非 /api/ 路径的 404：HTML（契约要求，别改成 JSON）。</summary>
    public static ApiResponse NotFoundHtml()
        => new(404, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(NotFoundHtmlBody));

    private const string NotFoundHtmlBody =
        "<!DOCTYPE HTML><html><head><title>404 Not Found</title></head>"
        + "<body><h1>Not Found</h1>"
        + "<p>The requested URL was not found on this server.</p>"
        + "</body></html>";

    public static ApiResponse BadRequest(string error)
        => Json(JsonSerializer.Serialize(new { ok = false, error }, JsonOptions), 400);
}

/// <summary>一个被冻结的端点（用于自检与文档，不参与分发）。</summary>
internal sealed record EndpointSpec(string Method, string Path, string Notes);
