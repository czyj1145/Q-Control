using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace QControl.Shared;

/// <summary>一个下载地址的失败记录（主机名 + 人话原因）。</summary>
internal sealed class DownloadAttempt
{
    public DownloadAttempt(string host, string reason, bool isMetadata = false)
    {
        Host = host;
        Reason = reason;
        IsMetadata = isMetadata;
    }

    public string Host { get; }

    public string Reason { get; }

    /// <summary>
    /// true = 这条失败来自"版本清单"而不是安装包本身。
    /// 界面上不列它 —— 用户关心的是"安装包下不下来"，清单拉不到只是我们改用兜底补丁号，
    /// 把它混进"试过的 N 个下载地址"只会让那段话更难读。
    /// </summary>
    public bool IsMetadata { get; }
}

/// <summary>所有下载地址都失败。消息本身已经是给用户看的那一段（不含"办法"部分）。</summary>
internal sealed class RuntimeDownloadException : Exception
{
    public RuntimeDownloadException(string message, IReadOnlyList<DownloadAttempt> attempts) : base(message)
    {
        Attempts = attempts;
    }

    public IReadOnlyList<DownloadAttempt> Attempts { get; }
}

/// <summary>
/// 下载 .NET 10 桌面运行时安装包 —— **安装器与绿色版引导器共用的唯一一份实现**。
///
/// 为什么要有多个下载地址（2026-10-30 用户报障「无法自动下载 .NET 10 桌面运行时」）：
///   现场日志是 `SocketException: 连接尝试失败。 2.23.246.9:443` —— `aka.ms` 被解析到一个
///   连不通的 Akamai 边缘节点，21 秒后超时。而当时**只有一个下载源**，没有第二条路可走，
///   界面只能把用户劝退到"自己上官网下"。
///
///   实测这台机器的可达性：`aka.ms` 超时、`builds.dotnet.microsoft.com` 502，
///   但 `dotnetcli.azureedge.net` 与 `dotnetcli.blob.core.windows.net` 满速可用（同一个文件，
///   60,035,296 字节）。所以正确做法不是换一个"更好的"写死地址 —— 网络环境千差万别，
///   今天好的明天可能不通 —— 而是**多源顺序回退**。
///
/// 地址顺序（先最省事的，再最可靠的）：
///   ① `aka.ms` 短链：永远指向当前最新 10.0.x，海外网络一次就命中；
///   ② 版本清单（`releases.json`）拿到具体补丁号，再拼 CDN 直链 ——
///      清单本身也有两个 Azure 域名互为备份；
///   ③ 清单也取不到时用内置的兜底版本号（发版时随 pack-release 更新）。
///
/// 为什么不像以前那样只写死一个补丁号：写死 10.0.14 半年后就是 404；而只认 aka.ms 又会在
/// 上面那种网络里彻底没路。两条腿都留着才稳。
/// </summary>
internal static class RuntimeDownload
{
    /// <summary>界面上怎么称呼它。</summary>
    public const string DisplayName = ".NET 10 桌面运行时";

    /// <summary>官网下载页（中文）：自动下载失败时的兜底出口。</summary>
    public const string DownloadPageUrl = "https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0";

    /// <summary>
    /// 官方"最新通道"短链。刻意不写死补丁号（写死了就等着半年后链接失效），
    /// 但它会 302 到 `builds.dotnet.microsoft.com`，在国内网络里未必通 ⇒ 必须留有后手。
    /// </summary>
    public const string LatestChannelUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";

    /// <summary>
    /// 版本清单。三个域名顺序试：前两个是 Azure CDN（国内通常可达），最后才是官方主站域名。
    /// 1.1 MB 的 JSON，只取里面第一个 `windowsdesktop-runtime-x.y.z-win-x64.exe`。
    /// </summary>
    private static readonly string[] MetadataUrls =
    {
        "https://dotnetcli.azureedge.net/dotnet/release-metadata/10.0/releases.json",
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json",
        "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json",
    };

    /// <summary>
    /// 直链模板。路径部分与官方 `builds.dotnet.microsoft.com` 完全一致 ——
    /// 只是换了承载域名，所以只要版本号对，同一个文件一定在。
    /// </summary>
    private static readonly string[] CdnTemplates =
    {
        "https://dotnetcli.azureedge.net/dotnet/WindowsDesktop/{0}/windowsdesktop-runtime-{0}-win-x64.exe",
        "https://dotnetcli.blob.core.windows.net/dotnet/WindowsDesktop/{0}/windowsdesktop-runtime-{0}-win-x64.exe",
    };

    /// <summary>
    /// 版本清单也取不到时的兜底补丁号。**发版时随 pack-release 更新**（见 ReleaseCheck 的告警）。
    /// 不追求永远最新：它是"连微软的清单都拉不下来"时的最后一条路，能装上一个 10.0.x 就够用。
    /// </summary>
    private const string FallbackVersion = "10.0.14";

    /// <summary>连上服务器（拿到响应头）的等待上限。以前是无限等，一次失败要 21 秒。</summary>
    private const int ConnectTimeoutSeconds = 10;

    /// <summary>版本清单是小文件，等这么久还不来就换下一个域名（慢网 1.1 MB 也要十几秒）。</summary>
    private const int MetadataTimeoutSeconds = 15;

    /// <summary>干净完整的安装包约 57 MB，小于这个数的一律当残片。</summary>
    private const long MinimumBytes = 20L * 1024 * 1024;

    /// <summary>
    /// 从清单里抠补丁号。**注意版号不在 `name` 里**（那边只有
    /// `windowsdesktop-runtime-win-x64.exe` 这种不带版号的文件名），只出现在 url 的路径段中：
    /// `.../dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe`。
    /// 2026-10-30 第一版就照着 name 猜，结果永远匹配不上、每次都退回兜底版本号（幸好数值一样没暴露）。
    /// </summary>
    private static readonly Regex VersionPattern = new(
        @"WindowsDesktop/(\d+\.\d+\.\d+)/windowsdesktop-runtime",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 把运行时安装包下到 <paramref name="target"/>。所有地址都失败时抛
    /// <see cref="RuntimeDownloadException"/>（消息可直接显示给用户）。
    /// </summary>
    /// <param name="report">
    /// 进度回调：(percent, 文案)。percent **小于 0 表示"不知道该走多少了"**（换源中），
    /// 调用方保持上一次的百分比不动即可。
    /// </param>
    /// <param name="userAgent">两个入口各自的 UA，留个身份方便对方看日志。</param>
    /// <param name="log">写日志（INFO）。</param>
    public static void Fetch(string target, Action<double, string>? report, string userAgent,
                             Action<string> log, CancellationToken ct)
    {
        // net48 默认协商协议在个别老系统上仍会挑 TLS 1.0，aka.ms 直接 400。
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { /* 老系统不支持就算了 */ }

        var attempts = new List<DownloadAttempt>();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        // 逐个地址试；成功即返回，失败记下原因继续
        bool TryOne(string url, bool bigFile)
        {
            if (!tried.Add(url)) return false;
            var host = HostOf(url);
            index++;

            report?.Invoke(-1, $"正在连接 {host}…（第 {index} 个地址）");
            log($"尝试下载：{url}");

            try
            {
                if (bigFile) DownloadOne(url, target, report, userAgent, ct);
                else GetSmallText(url, MetadataTimeoutSeconds, userAgent, ct);

                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = Explain(ex);
                attempts.Add(new DownloadAttempt(host, reason));
                log($"该地址不可用（{host}）：{reason}");
                TryDelete(target);
                return false;
            }
        }

        report?.Invoke(-1, "正在检查下载地址…");

        // ① 官方最新通道
        if (TryOne(LatestChannelUrl, bigFile: true)) return;

        // ② 版本清单 → CDN 直链
        var version = LatestVersion(report, userAgent, log, ct, attempts);
        var versions = new List<string>();
        if (version is not null) versions.Add(version);
        if (!versions.Contains(FallbackVersion)) versions.Add(FallbackVersion);

        foreach (var v in versions)
        {
            foreach (var template in CdnTemplates)
            {
                if (TryOne(string.Format(template, v), bigFile: true)) return;
            }
        }

        throw new RuntimeDownloadException(BuildMessage(attempts), attempts);
    }

    /// <summary>
    /// 从版本清单里取最新的补丁号；清单也拉不到就返回 null（调用方退回兜底版本号）。
    /// 清单的失败也记进 <paramref name="attempts"/>，这样界面能看出"连清单都没拿到"。
    /// </summary>
    private static string? LatestVersion(Action<double, string>? report, string userAgent, Action<string> log,
                                         CancellationToken ct, List<DownloadAttempt> attempts)
    {
        foreach (var url in MetadataUrls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var host = HostOf(url);
                report?.Invoke(-1, $"正在查询最新版本…（{host}）");
                log($"读取版本清单：{url}");

                var json = GetSmallText(url, MetadataTimeoutSeconds, userAgent, ct);
                var m = VersionPattern.Match(json);
                if (!m.Success)
                {
                    log("版本清单里没找到 windowsdesktop-runtime 的版本号，改用兜底版本 " + FallbackVersion);
                    return null;
                }

                log($"版本清单给出最新补丁号：{m.Groups[1].Value}");
                return m.Groups[1].Value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var host = HostOf(url);
                var reason = Explain(ex);
                attempts.Add(new DownloadAttempt(host + "（版本清单）", reason, isMetadata: true));
                log($"版本清单不可用（{host}）：{reason}");
            }
        }

        log("三个版本清单地址都不可用，改用兜底版本 " + FallbackVersion);
        return null;
    }

    // ================================================================ 单个地址

    private static void DownloadOne(string url, string target, Action<double, string>? report,
                                    string userAgent, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        // 连接阶段限时、下载阶段不限时：57 MB 在慢网下要几分钟，一刀切的超时只会误杀
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));

        HttpResponseMessage resp;
        try
        {
            resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, handshake.Token)
                       .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"连接 {ConnectTimeoutSeconds} 秒没有响应");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw new HttpStatusException((int)resp.StatusCode);

            // 响应头到了 ⇒ 服务器确实在答；后面的慢速传输交给 ReadWriteTimeout 兜
            handshake.CancelAfter(Timeout.Infinite);

            var total = resp.Content.Headers.ContentLength ?? 0;
            using var src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var dst = File.Create(target);

            var buffer = new byte[128 * 1024];
            long done = 0;
            var lastPercent = -1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = src.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                dst.Write(buffer, 0, read);
                done += read;

                // 每 1% 才刷一次界面：内容一多，逐块报告会把消息队列塞满
                var percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent == lastPercent) continue;
                lastPercent = percent;

                var text = total > 0
                    ? $"正在下载 {DisplayName}… {done / 1048576d:F1} / {total / 1048576d:F1} MB（{percent}%）"
                    : $"正在下载 {DisplayName}… {done / 1048576d:F1} MB";
                report?.Invoke(total > 0 ? percent : -1, text);
            }

            dst.Flush();

            // 长度对不上 = 传输中途断了（CDN 断流时 Read 会正常返回 0，不抛异常）。
            // 这种半截文件长度也可能过 20 MB、头两个字节也还是 MZ，光靠 Validate 抓不住；
            // 放过去的话用户会在"安装"那一步看到一个莫名其妙的退出码。
            if (total > 0 && done != total)
                throw new IOException($"下载不完整（{done} / {total} 字节）");
        }

        Validate(target);
    }

    /// <summary>
    /// 下完必须验一遍内容。**为什么不能只看长度**：代理/网关的拦截页、登录页、校园网提示页
    /// 也是 200、也可能几十 MB；而 57 MB 的 PE 文件头两个字节一定是 `MZ`。
    /// 校验不过就当这个源失败、换下一个 —— 宁可多花十秒，也不能把拦截页当安装包去执行。
    /// </summary>
    private static void Validate(string target)
    {
        var size = new FileInfo(target).Length;
        if (size < MinimumBytes) throw new IOException($"下到的文件只有 {size / 1048576d:F1} MB，不是完整安装包");

        using var fs = File.OpenRead(target);
        if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new IOException("下到的不是可执行文件（被重定向到网页了？）");
    }

    private static string GetSmallText(string url, int seconds, string userAgent, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        using var resp = http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                             .GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new HttpStatusException((int)resp.StatusCode);

        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    // ================================================================ 错误说明

    /// <summary>
    /// 把异常翻成一句人话。
    ///
    /// **这条是本次报障的另一半根因**：以前界面只显示最外层的
    /// `ex.Message`（"发送请求时出错。"），而真正有用的信息 —— 连哪个 IP、为什么连不上 ——
    /// 全被压在 `InnerException` 里，用户和我们都只能猜。现在一律取**最内层**。
    /// </summary>
    private static string Explain(Exception ex)
    {
        if (ex is HttpStatusException status) return status.Message;
        if (ex is TimeoutException) return ex.Message;

        var inner = Deepest(ex);

        if (inner is SocketException socket) return SocketReason(socket);

        if (inner is WebException web)
        {
            return web.Status switch
            {
                WebExceptionStatus.Timeout => "连接超时",
                WebExceptionStatus.NameResolutionFailure => "域名解析失败（DNS）",
                WebExceptionStatus.ProxyNameResolutionFailure => "代理域名解析失败",
                WebExceptionStatus.ConnectFailure => "无法连接到服务器",
                WebExceptionStatus.TrustFailure => "证书校验失败（可能被中间设备劫持）",
                _ => web.Status.ToString(),
            };
        }

        if (inner is OperationCanceledException) return "连接超时";

        return FirstLine(inner.Message);
    }

    private static string SocketReason(SocketException se) => se.SocketErrorCode switch
    {
        SocketError.TimedOut => "连接超时",
        SocketError.HostNotFound or SocketError.NoData => "域名解析失败（DNS）",
        SocketError.TryAgain => "DNS 无响应",
        SocketError.NetworkUnreachable => "网络不可达",
        SocketError.HostUnreachable => "主机不可达",
        SocketError.ConnectionRefused => "拒绝连接（端口不通）",
        SocketError.ConnectionReset => "连接被重置",
        SocketError.ConnectionAborted => "连接中断",
        _ => se.SocketErrorCode + "：" + FirstLine(se.Message),
    };

    private static Exception Deepest(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null) cur = cur.InnerException;
        return cur;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "未知错误";
        var line = text.Split('\n')[0].Trim();
        return line.Length > 80 ? line.Substring(0, 80) + "…" : line;
    }

    private static string BuildMessage(List<DownloadAttempt> attempts)
    {
        var text = $"无法自动下载 {DisplayName}（约 57 MB）。";

        // 只列安装包本身的地址；清单那几条只进日志（见 DownloadAttempt.IsMetadata）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var a in attempts)
        {
            if (a.IsMetadata || !seen.Add(a.Host)) continue;
            lines.Add($"· {a.Host} — {a.Reason}");
        }

        if (lines.Count > 0)
        {
            text += $"\n\n试过的 {lines.Count} 个下载地址都没成功：";
            foreach (var line in lines) text += "\n" + line;
        }

        return text;
    }

    /// <summary>
    /// 拼成最终给用户看的那段话。<paramref name="localHint"/> 由调用方给 ——
    /// 安装器说"放到安装包旁边"，绿色版说"放到绿色版目录里"，两者的逃生通道位置不同。
    /// </summary>
    public static string FailureText(Exception ex, string localHint)
    {
        var body = ex is RuntimeDownloadException
            ? ex.Message
            : $"无法自动下载 {DisplayName}（约 57 MB）。\n\n原因：{Explain(ex)}";

        return body + "\n\n" +
               "办法：连上网后重试；或手动下载安装，再重新运行本程序。\n" +
               $"下载地址：{DownloadPageUrl}\n" +
               $"（{localHint}）";
    }

    // ================================================================ 小工具

    private static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 临时目录会被清理 */ }
    }

    /// <summary>HTTP 状态码不是 2xx（404 说明这个版本号在 CDN 上不存在，换一个）。</summary>
    private sealed class HttpStatusException : Exception
    {
        public HttpStatusException(int code)
            : base(code switch
            {
                404 => "HTTP 404（该版本在服务器上不存在）",
                403 => "HTTP 403（被拒绝访问）",
                >= 500 => $"HTTP {code}（服务器错误）",
                _ => $"HTTP {code}",
            })
        {
        }
    }
}
