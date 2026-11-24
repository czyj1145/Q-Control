using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace QControl.App.Http;

/// <summary>
/// 极简 HTTP/1.1 服务器（短连接）。
///
/// 为什么不用 `HttpListener`：
///   Windows 上监听端口需要 URL ACL，非管理员会话直接 `Access is denied`，
///   开发和自检都跑不起来（还得 netsh http add urlacl，多一步部署负担）。
///   `TcpListener` 绑 127.0.0.1 没这个问题。
///
/// 为什么短连接就够：
///   每个请求一个连接就够：调用方只有脚本和自检工具，界面走进程内直读、
///   不产生 HTTP 流量。不必为了 keep-alive 引入半成品状态机，
///   开销可忽略。
///
/// 请求只解析到够用的程度：请求行 + Content-Length + body。
/// 不实现 chunked / 分块上传 / 100-continue —— 本机自用，调用方都是自家脚本。
/// </summary>
internal sealed class MiniHttpServer
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpListener _listener;
    private readonly Func<ApiRequest, ApiResponse> _handler;

    public int Port { get; }

    public MiniHttpServer(int port, Func<ApiRequest, ApiResponse> handler)
    {
        Port = port;
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { return; }          // 正常停机
            catch (SocketException) { continue; }                // 瞬时错误，继续接

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    /// <summary>
    /// 请求形状不合法（头超长 / 体过大 / 头不完整）。与 <see cref="IOException"/> 分开：
    /// 前者要回 400，后者才是"客户端真的断了、什么都不用回"。
    /// </summary>
    private sealed class BadRequestException(string message) : Exception(message);

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream;
            try { stream = client.GetStream(); }
            catch { return; }                                  // 连接已经废了

            client.ReceiveTimeout = (int)IoTimeout.TotalMilliseconds;
            client.SendTimeout = (int)IoTimeout.TotalMilliseconds;

            // ---- 读请求：只有这一段的 IOException 才是「客户端断了」 ----
            ApiRequest? req;
            try
            {
                // ⚠️ ReceiveTimeout 只管同步 Socket.Receive，ReadAsync 不认它：
                //    半截请求头 / 声明了 Content-Length 却不发完 body 的连接会永久挂着。
                req = await ReadRequestAsync(stream).WaitAsync(IoTimeout).ConfigureAwait(false);
            }
            catch (BadRequestException ex)
            {
                // 这几处是**我们自己**校验失败（头太长 / 体太大 / 头不完整），
                // 不属于"客户端断线"：必须回 400，不能让连接裸关。
                // 裸关的症状是 curl `(52) Empty reply from server`、界面判"后端未响应"。
                try
                {
                    await WriteResponseAsync(stream, ApiResponse.BadRequest(ex.Message), "GET").ConfigureAwait(false);
                }
                catch { /* 连 400 都写不出去：对方已经走了 */ }
                return;
            }
            catch (IOException) { return; }
            catch (SocketException) { return; }
            catch (TimeoutException) { return; }   // 半开连接：15 秒没读完就放掉
            catch (Exception ex) { await TryWrite500(stream, ex); return; }
            if (req is null) return;                           // 空连接 / 坏请求

            // ---- 执行 handler：它自己抛的 IOException **绝不等于**客户端断线 ----
            // 早先把读请求、handler、写响应全包在一个 try 里，catch(IOException) 直接
            // 吞掉：于是历史文件被占、配置写失败、JsonNode 复用这类**后端**异常
            // 都变成空 body，界面只说「后端未响应」，日志里一条线索都没有
            //（2026-11-08 之前只给 /api/app/machine-report 单独打过补丁，根因没动）。
            ApiResponse res;
            try
            {
                res = _handler(req);
            }
            catch (Exception ex)
            {
                res = InternalError(ex);
            }

            // ---- 写响应：只有这里的 IOException 才算客户端关页面 ----
            try
            {
                await WriteResponseAsync(stream, res, req.Method).WaitAsync(IoTimeout).ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (TimeoutException) { }           // 对方不收了：别再占着连接
            catch (Exception ex) { await TryWrite500(stream, ex); }
        }
    }

    private static async Task TryWrite500(NetworkStream stream, Exception ex)
    {
        // 内部错误也要回 500，绝不让连接裸死 —— 否则界面 fetch reject，
        // 轮询连失两次就弹「后端未响应」，而后端其实活着。
        try { await WriteResponseAsync(stream, InternalError(ex), "GET").ConfigureAwait(false); }
        catch { /* 连 500 都写不出去，只能放弃 */ }
    }

    private static ApiResponse InternalError(Exception ex)
        // ⚠️ 用序列化器拼，别手插字符串：ex.Message 里的引号 / 反斜杠 / 换行
        //    会把 JSON 拼坏，客户端拿到的是解析失败而不是一条能读的错误。
        => ApiResponse.Json(
            JsonSerializer.Serialize(
                new { ok = false, error = "内部错误：" + ex.GetType().Name + ": " + ex.Message },
                ApiResponse.JsonOptions),
            500);

    // ---------------------------------------------------------------- 读请求

    private static async Task<ApiRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var headBuf = new MemoryStream();
        var one = new byte[1];
        var matched = 0;
        // 逐字节找 \r\n\r\n。慢，但只在请求头阶段（<1KB），换来零状态机的简单。
        while (headBuf.Length < MaxHeaderBytes)
        {
            var n = await stream.ReadAsync(one).ConfigureAwait(false);
            if (n == 0) return headBuf.Length == 0 ? null : throw new BadRequestException("请求头不完整");
            headBuf.WriteByte(one[0]);
            matched = one[0] switch
            {
                (byte)'\r' when matched is 0 or 2 => matched + 1,
                (byte)'\n' when matched is 1 => 2,
                (byte)'\n' when matched is 3 => 4,
                _ => 0,
            };
            if (matched == 4) break;
        }
        if (matched != 4) throw new BadRequestException("请求头超长或格式错误");

        var head = Encoding.Latin1.GetString(headBuf.GetBuffer(), 0, (int)headBuf.Length);
        var lines = head.Split("\r\n", StringSplitOptions.None);
        var parts = lines[0].Split(' ');
        // 请求行不合法（连"方法 路径"两段都没有）也要回 400：以前这里 `return null`，
        // 调用方直接关 socket，客户端拿到的是 `curl: (52) Empty reply` —— 而这个文件
        // 92-93 行的注释明确说了"必须回 400，不能让连接裸关"（2026-11-23 审查）。
        if (parts.Length < 2) throw new BadRequestException("请求行不合法");

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? "" : target[(q + 1)..];

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var c = line.IndexOf(':');
            if (c <= 0) continue;
            if (!line[..c].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            int.TryParse(line[(c + 1)..].Trim(), out contentLength);
        }

        var body = "";
        if (contentLength > 0)
        {
            if (contentLength > MaxBodyBytes) throw new BadRequestException("请求体过大（上限 4 MiB）");
            var buf = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                var n = await stream.ReadAsync(buf.AsMemory(read)).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
            }
            body = Encoding.UTF8.GetString(buf, 0, read);
        }

        return new ApiRequest(method, path, query, body);
    }

    // ---------------------------------------------------------------- 写响应

    private static async Task WriteResponseAsync(NetworkStream stream, ApiResponse res, string method)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(Reason(res.Status)).Append("\r\n");
        head.Append("Content-Type: ").Append(res.ContentType).Append("\r\n");
        head.Append("Content-Length: ").Append(res.Body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Connection: close\r\n");
        // 让「这是桩」一眼可辨：curl -I 或浏览器 Network 面板都能看到
        head.Append("X-CC-Server: QControl/0.1 (P1)\r\n");
        // 只有真正回桩样本时才说"这是桩"：无条件写 1 会让真机模式下的真数据
        // 也被标注成桩（`/api/gpu/info` 以前正是这样，消费者没法分辨新旧）。
        head.Append(res.IsStub ? "X-CC-Stub: 1\r\n" : "X-CC-Stub: 0\r\n");
        head.Append("\r\n");

        var headBytes = Encoding.Latin1.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes).ConfigureAwait(false);
        if (method != "HEAD" && res.Body.Length > 0)
            await stream.WriteAsync(res.Body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        _ => "Status",
    };

    public void Stop() => _listener.Stop();
}
