using System.Globalization;
using System.Text.Json.Nodes;
using QControl.Core.Updates;

namespace QControl.App.Updates;

/// <summary>
/// 「检查更新」—— 只做**检测**：查一次最新发布，和本机版本比一比，把结果摆到关于页。
/// 下载与安装是另一件事，见 <see cref="UpdateDownload"/>（它从这里的快照里取附件地址）。
/// 本类不碰 EC、不写文件、不提权。
///
/// 为什么是单例而不是每次检查新建：结果要跨页面留存（切走再切回来还在），
/// 而"有没有新版本"是个进程级事实。所有状态都在 <see cref="_gate"/> 里读写。
///
/// 更新源按 **GitHub → Gitee 回退**：两个仓库同步发布，哪边先通就用哪边。
/// 都没通就把两边的失败原因并排带出来 —— 报障只看"检查失败"是没法查的。
///
/// ⚠️ 自动检查**每天最多一次**（<see cref="AutoCheckInterval"/>），手动点按钮不受限制。
///    失败也算一次，否则网络不通时每开一次窗口都要等 8 秒超时。
/// ⚠️ 出站请求只带 User-Agent（`QControl/版本`），不带机型、不报序列号，
///    更不做任何遥测 —— 这条要守住，不然「检查更新」就成了后门。
/// </summary>
internal sealed class UpdateChecker
{
    public const string DisplayName = "Q-Control";

    /// <summary>仓库主页（与 AboutView 的外链、README 的 badge 同一对地址）。</summary>
    public const string GitHubRepo = "https://github.com/czyj1145/Q-Control";
    public const string GiteeRepo = "https://gitee.com/czyj1145/Q-Control";

    private const string GitHubApi = "https://api.github.com/repos/czyj1145/Q-Control/releases/latest";
    private const string GiteeApi = "https://gitee.com/api/v5/repos/czyj1145/Q-Control/releases/latest";

    /// <summary>
    /// 调试用的源地址覆盖（`--update-source &lt;url&gt;`）。非空时**两个源都指向它**，
    /// 于是本地起一个假 release 源就能把 latest / newer / empty / error 四种态
    /// 挨个渲染出来核对版式 —— 不用等真的发一版。
    ///
    /// ⚠️ 只在命令行显式给出时生效，平时是 null（发布行为与线上完全一致）。
    /// ⚠️ 它会被记进日志与启动横幅：**看到这行说明你现在看的不是真实更新源**。
    /// </summary>
    public static string? SourceOverride { get; set; }

    /// <summary>自动检查的最小间隔。窗口一天开很多次，没必要每次都问一遍。</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

    public static UpdateChecker Instance { get; } = new();

    // ---------------------------------------------------------------- 状态

    /// <summary>idle / checking / latest / newer / empty / error</summary>
    private string _state = "idle";
    private ReleaseInfo? _latest;
    private string? _source;
    private string? _error;
    private DateTime? _checkedAt;     // 最近一次**成功**检查
    private DateTime? _attemptedAt;   // 最近一次尝试（含失败）—— 自动检查的节流依据
    private int _running;

    private readonly object _gate = new();

    // ---------------------------------------------------------------- 网络

    /// <summary>
    /// 复用同一个 HttpClient：每次检查新建一个会重新握手 TLS，白白多几百毫秒；
    /// 常驻的成本只有一个连接池（几十 KB），比反复建划算得多。
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            // DNS 变了要能跟上（GitHub 的 IP 会换），所以连接不能永久复用
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(4),
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        // GitHub 的 API **必须**带 User-Agent，否则直接 403
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"{DisplayName}/{AppState.AppVersion} (+{GitHubRepo})");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return http;
    }

    // ---------------------------------------------------------------- 对外

    /// <summary>开窗时调用：距上次尝试超过 <see cref="AutoCheckInterval"/> 才真去查。</summary>
    public void AutoCheck()
    {
        lock (_gate)
        {
            if (_attemptedAt is { } at && DateTime.Now - at < AutoCheckInterval) return;
        }
        CheckInBackground();
    }

    /// <summary>
    /// 起一次后台检查，**立刻返回**（HTTP 接口不能陪着等 8 秒超时）。
    /// 已在查就什么也不做 —— 连点两下按钮不该发两轮请求。
    /// </summary>
    public void CheckInBackground()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        lock (_gate)
        {
            _state = "checking";
            _error = null;
            _attemptedAt = DateTime.Now;
        }

        _ = Task.Run(async () =>
        {
            try { await CheckOnce().ConfigureAwait(false); }
            catch (Exception e)
            {
                // 兜底：任何没预料到的异常都要变成界面能读的状态，不能留在 "checking" 上
                lock (_gate)
                {
                    _state = "error";
                    _error = "检查更新时出错：" + e.Message;
                }
            }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    /// <summary>
    /// **同步**跑完一次检查（阻塞到出结果）。只给 `--ui-shot` 的版式演练用：
    /// 界面正常路径是"后台查 + 轮询"，绝不阻塞 UI 线程；截图进程没人等，阻塞几秒无妨。
    /// </summary>
    public void CheckAndWait()
    {
        try { CheckOnce().GetAwaiter().GetResult(); }
        catch (Exception e)
        {
            lock (_gate)
            {
                _state = "error";
                _error = "检查更新时出错：" + e.Message;
            }
        }
    }

    /// <summary>
    /// 最后一份成功解析的发布 —— 下载器从这里取附件地址。
    /// 只读、取不到就是 null（那时候「下载」按钮本来也不该出现）。
    /// </summary>
    public ReleaseInfo? Latest
    {
        get { lock (_gate) return _latest; }
    }

    /// <summary>
    /// `GET /api/app/update` 的响应体。界面每秒最多拉几次（只在检查期间），
    /// 所以这里必须便宜：只拼一份内存状态，不发任何请求。
    ///
    /// `download` 段来自 <see cref="UpdateDownload"/> —— 下载进度和"有没有新版"是两份状态，
    /// 但界面只需要一次请求就能把整行画完。
    /// </summary>
    public JsonObject Snapshot(string current)
    {
        lock (_gate)
        {
            var outObj = new JsonObject
            {
                ["ok"] = true,
                ["current"] = current,
                ["state"] = _state,
                ["newer"] = _state == "newer",
                ["checked_at"] = _checkedAt?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["source"] = _source,
                ["error"] = _error,
                ["latest"] = LatestJson(_latest),
                ["download"] = UpdateDownload.Instance.Snapshot(),
            };
            return outObj;
        }
    }

    private static JsonNode? LatestJson(ReleaseInfo? r)
    {
        if (r is null) return null;

        var assets = new JsonArray();
        foreach (var a in r.Assets)
        {
            assets.Add(new JsonObject
            {
                ["name"] = a.Name,
                ["size"] = a.Size,
                ["url"] = a.Url,
            });
        }

        return new JsonObject
        {
            ["version"] = r.Version,
            ["tag"] = r.Tag,
            ["name"] = r.Name,
            ["notes"] = r.Notes,
            ["url"] = r.Url,
            ["published_at"] = r.PublishedAt,
            ["prerelease"] = r.Prerelease,
            ["assets"] = assets,
        };
    }

    // ---------------------------------------------------------------- 查询

    private async Task CheckOnce()
    {
        var errors = new List<(string Name, string? Error)>();
        var missing = 0;

        foreach (var kind in new[] { FeedKind.GitHub, FeedKind.Gitee })
        {
            var (ok, info, error, notFound) = await FetchAsync(kind).ConfigureAwait(false);
            if (!ok || info is null)
            {
                if (notFound) missing++;
                errors.Add((kind == FeedKind.GitHub ? "GitHub" : "Gitee", error));
                continue;
            }

            // 有一个源答上了就下结论 —— 两家发布是同步的，再去问另一家只会让结论互相矛盾
            Settle(kind, info);
            return;
        }

        lock (_gate)
        {
            _latest = null;
            _source = null;

            if (missing == 2)
            {
                // 两家都是 404：**这不是故障**，是"还没有对外发布过任何一个版本"
                // （私有仓库匿名访问也是 404，两种情况对用户是同一件事）。
                // 单独一个状态，界面才有机会说人话，而不是把作者自己吓一跳。
                _state = "empty";
                _error = null;
            }
            else
            {
                _state = "error";
                _error = JoinFailures(errors);
            }
        }
    }

    /// <summary>
    /// 把两家的失败原因合成一句话。
    ///
    /// 两家原因**完全相同**时只留一份：代理拦截那种情况两条一模一样，
    /// 并排排两遍只会把关于页这一行撑成三行，一个字的额外信息都没有。
    /// 原因不同才逐条列 —— 那时候"哪家挂在哪一步"才是排障的关键。
    /// </summary>
    private static string JoinFailures(List<(string Name, string? Error)> failures)
    {
        if (failures.Count == 0) return "检查失败";

        const string unknown = "未知原因";
        var reasons = failures.Select(f => f.Error ?? unknown).Distinct(StringComparer.Ordinal).ToList();
        if (reasons.Count == 1)
            return string.Join(" / ", failures.Select(f => f.Name)) + "：" + reasons[0];

        return string.Join("；", failures.Select(f => f.Name + "：" + (f.Error ?? unknown)));
    }

    private void Settle(FeedKind kind, ReleaseInfo info)
    {
        var current = AppState.AppVersion;

        if (!SemVer.TryParse(current, out var cur) || !SemVer.TryParse(info.Tag, out var lat))
        {
            lock (_gate)
            {
                _state = "error";
                _error = $"版本号无法比较（本机 {current} / 远端 {info.Tag}）";
                _latest = info;
                _source = Name(kind);
            }
            return;
        }

        Log($"更新检查：{Name(kind)} 最新 {lat} / 本机 {cur}");

        lock (_gate)
        {
            _latest = info;
            _source = Name(kind);
            _checkedAt = DateTime.Now;
            _error = null;
            _state = lat.IsNewerThan(cur) ? "newer" : "latest";
        }
    }

    private static string Name(FeedKind kind) => kind == FeedKind.GitHub ? "github" : "gitee";

    /// <summary>
    /// 查一家。返回 (ok, info, error, notFound)；**任何失败都不抛异常** —— 调用方要用它拼可读的错误链。
    /// 、<c>notFound</c> 单独给出来，是因为 404 与"网络不通 / 被限流"对用户是两件事：
    /// 前者代表"还没有发布过版本"（开发期常态），后者才是真故障。
    /// </summary>
    private static async Task<(bool Ok, ReleaseInfo? Info, string? Error, bool NotFound)> FetchAsync(FeedKind kind)
    {
        var (url, repo) = kind == FeedKind.GitHub
            ? (GitHubApi, GitHubRepo)
            : (GiteeApi, GiteeRepo);

        // 调试源：两家都指过去（假源不需要区分 GitHub / Gitee 两种格式）
        if (SourceOverride is { Length: > 0 } over)
            (url, repo) = (over, GitHubRepo);

        try
        {
            using var resp = await Http.GetAsync(url).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            // 404 在两家都表示"没有可读的发布"：GitHub 私有仓库匿名访问也是 404，
            // 所以文案要把两种可能都摆出来（否则作者自己会以为是代码写错了）。
            if (status == 404)
                return (false, null, kind == FeedKind.GitHub
                    ? "GitHub 上没有可读的发布（可能仍是私有仓库，或没建过 release）"
                    : "Gitee 上没有这个仓库，或还没建过 release", true);

            if (status is 403 or 429)
                return (false, null, $"更新源限流或拒绝访问（HTTP {status}，稍后再试）", false);

            if (status >= 500)
                return (false, null, $"更新源暂时不可用（HTTP {status}）", false);

            if (status != 200)
                return (false, null, $"更新源返回 HTTP {status}", false);

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ReleaseFeed.TryParse(kind, body, repo, out var info, out var error)
                ? (true, info, null, false)
                : (false, null, error, false);
        }
        catch (TaskCanceledException)
        {
            // HttpClient.Timeout 到点表现为 TaskCanceledException，不是 "取消"
            return (false, null, "连不上更新服务器（8 秒超时：网络不通或被代理拦了）", false);
        }
        catch (HttpRequestException e)
        {
            return (false, null, "连不上更新服务器：" + e.Message, false);
        }
    }

    private static void Log(string line)
    {
        try { Console.WriteLine("[update] " + line); } catch { /* 控制台没了不影响检查 */ }
    }
}
