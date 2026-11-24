using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using QControl.Core.Updates;

namespace QControl.App.Updates;

/// <summary>
/// 「下载更新」—— 把检测到的新版本安装包**下到本机**，校验后交给安装器。
///
/// 与 <see cref="UpdateChecker"/> 分工：那边只回答"有没有新版"，这边只管"把那个包弄下来并装上"。
/// 之所以分成两个类：检查结果是进程级事实（每天一次、跨页留存），而下装是**一次会失败的过程**
/// （进度 / 取消 / 校验 / 提权），两者生命周期完全不同，混在一起状态机会乱。
///
/// 流程：选附件 → 流式下载（带进度与 20 秒无数据超时）→ 核对体积 → 核对 SHA256 →
///       <c>ready</c> → 用户点「安装」→ 提权启动安装包（**向导**，不静默）。
///
/// ⚠️ 为什么用向导而不是 `/silent`：静默装完不会把控制中心拉回来，用户会以为程序没了；
///    向导里点「安装」之后安装器自己会请我们退出（它先 POST /api/app/quit 再写文件），
///    中途点了取消则控制中心原封不动 —— 这比自己算退出时机稳。
/// ⚠️ 为什么必须校验：安装包是从**用户可写的目录**（%LocalAppData%）里提权执行的，
///    不校验就等于"谁改了这个文件谁就拿到管理员"。发布带 `SHA256.txt` 时哈希不符**一律拒绝**；
///    发布没带校验和时只核体积，并在界面上如实写"未校验"。
/// ⚠️ 下载目录用 %LocalAppData% 而不是 ProgramData：提权进程与普通进程写的是同一个用户目录，
///    不会被 ACL 绊住；装完的残留也只是几十 MB，重下会覆盖。
/// </summary>
internal sealed class UpdateDownload
{
    /// <summary>发布里的校验和文件名（`pack-release.ps1` 生成的那份）。</summary>
    private const string HashAssetName = "SHA256.txt";

    /// <summary>多久没收到数据就判"卡住了"。国内直连 GitHub 时这个数不罕见。</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 单个更新包的体积上限。安装包实测 60~120 MB，500 MB 足够宽松；
    /// 没有它时源不报 `Content-Length` 就等于"想写多大写多大"。
    /// </summary>
    private const long MaxBytes = 500L * 1024 * 1024;

    public static UpdateDownload Instance { get; } = new();

    // ---------------------------------------------------------------- 状态
    //
    // idle / downloading / cancelling / verifying / ready / installing / error
    // 只有 downloading / cancelling / verifying 需要界面轮询，其余都是"停下来等用户"。
    private string _state = "idle";
    private string? _tag;          // 这一份属于哪个版本（tag，如 v0.2.0）—— 与 latest.tag 比，避免把旧包当新包
    private string? _file;         // 落盘的安装包完整路径
    private long _received;
    private long _total;
    private double _speed;         // 平均速度（字节/秒），只用于显示
    private string? _error;
    private string? _expectedSha;  // 发布公布的 SHA256（没公布就是 null）
    private string? _actualSha;    // 本地算出来的
    private bool _verified;        // true = 哈希核对通过；false = 发布没给哈希，只核了体积
    private string? _target;       // 安装器会装到哪（给确认框用）
    private bool _portable;        // 当前这份不是安装版 ⇒ 安装会在别处多出一份
    private CancellationTokenSource? _cts;

    /// <summary>同一时刻只允许一个下载任务（连点两下不该并发写同一个文件）。</summary>
    private int _running;

    private readonly object _gate = new();

    // ---------------------------------------------------------------- 网络

    /// <summary>
    /// 专门给大文件用的客户端：**总超时关掉**（100 MB 的包在慢网里要十几分钟），
    /// 卡死靠每次 ReadAsync 的 20 秒看门狗，而不是一个算不准的总时长。
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(8),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"{UpdateChecker.DisplayName}/{AppState.AppVersion} (+{UpdateChecker.GitHubRepo})");
        return http;
    }

    // ---------------------------------------------------------------- 对外

    /// <summary>`POST /api/app/update/download`：起一次后台下载，立刻返回当前快照。</summary>
    public JsonObject Start(ReleaseInfo? latest)
    {
        if (latest is null) return Fail("还没查到可用的版本，请先点「检查更新」");

        var asset = PickInstaller(latest);
        if (asset is null) return Fail("这个版本没附安装包，请点「发布页」手动下载");

        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || !AllowedUrl(uri))
            return Fail("安装包地址不是 https，已拦下：" + (asset.Url ?? "（缺失）"));

        // 同一版本已经在跑 / 已经下好：不重复起任务，直接把现状回给界面
        lock (_gate)
        {
            if (string.Equals(_tag, latest.Tag, StringComparison.OrdinalIgnoreCase))
            {
                if (_state is "downloading" or "cancelling" or "verifying" or "ready")
                    return Reply();
            }
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return Fail("已经有一个下载在跑了");

        var fileName = FileNameOf(asset, latest);
        var path = Path.Combine(VersionDir(latest.Tag), fileName);
        var (target, portable) = DetectTarget();
        var cts = new CancellationTokenSource();

        lock (_gate)
        {
            _state = "downloading";
            _tag = latest.Tag;
            _file = path;
            _received = 0;
            _total = asset.Size > 0 ? asset.Size : 0;
            _speed = 0;
            _error = null;
            _expectedSha = null;
            _actualSha = null;
            _verified = false;
            _target = target;
            _portable = portable;
            _cts = cts;
        }

        Log($"开始下载 {fileName}（{(asset.Size > 0 ? Mb(asset.Size) : "体积未知")}）：{uri}");
        _ = Task.Run(() => RunAsync(latest, asset, path, cts.Token));
        return Reply();
    }

    /// <summary>`POST /api/app/update/cancel`：停下载并删掉半截文件。</summary>
    public JsonObject Cancel()
    {
        lock (_gate)
        {
            if (_state is not ("downloading" or "verifying")) return Reply();
            _state = "cancelling";
            _cts?.Cancel();
            return Reply();
        }
    }

    /// <summary>
    /// `POST /api/app/update/install`：把已校验的安装包交给安装器（提权 + 向导）。
    ///
    /// ⚠️ 这里**不主动退出**：安装器写文件前会先 POST /api/app/quit 请我们退，
    ///    那样"用户点了安装向导的取消"时控制中心还活着（自己抢着退就变成"点错就没了"）。
    /// </summary>
    public JsonObject Install()
    {
        string file;
        lock (_gate)
        {
            if (_state is "downloading" or "cancelling" or "verifying")
                return Fail("还在下载，等下载完再安装");
            if (_file is not { Length: > 0 } f || _state is not ("ready" or "installing"))
                return Fail("还没下好安装包，先点「下载」");
            file = f;
        }

        if (!File.Exists(file))
        {
            lock (_gate) { _state = "idle"; _file = null; }
            return Fail("安装包不在了（被清理或杀软隔离），请重新下载");
        }

        try
        {
            Process.Start(new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                Verb = "runas",            // 安装器清单是 requireAdministrator；已提权时不会再弹
                WorkingDirectory = Path.GetDirectoryName(file)!,
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED：用户点了 UAC 的「否」—— 不是故障，把状态放回去
            lock (_gate) { _state = "ready"; }
            return Fail("你取消了管理员授权，安装没有开始");
        }
        catch (Exception e)
        {
            lock (_gate) { _state = "error"; _error = "启动安装程序失败：" + e.Message; }
            return Reply();
        }

        lock (_gate) { _state = "installing"; _error = null; }
        Log("安装程序已启动：" + file);
        return Reply();
    }

    /// <summary>
    /// `GET /api/app/update` 里的 `download` 段。**只拼内存**（界面在下载期间每 500 ms 读一次），
    /// 一次网络请求都不发；注册表探测也在 <see cref="Start"/> 里做过并缓存下来了。
    /// </summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["state"] = _state,
                ["version"] = _tag,
                ["received"] = _received,
                ["total"] = _total,
                ["percent"] = _total > 0 ? (long)Math.Clamp(_received * 100 / _total, 0, 100) : null,
                ["speed"] = (long)_speed,
                ["file"] = _file,
                ["verified"] = _verified,
                ["expected_sha256"] = _expectedSha,
                ["actual_sha256"] = _actualSha,
                ["target"] = _target,
                ["portable"] = _portable,
                ["error"] = _error,
            };
        }
    }

    // ---------------------------------------------------------------- 下载

    private async Task RunAsync(ReleaseInfo latest, ReleaseAsset asset, string path, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            PruneOtherVersions(dir);                       // 旧版本的包没有用，顺手清掉
            Directory.CreateDirectory(dir);

            await DownloadAsync(asset, path, ct).ConfigureAwait(false);

            SetState("verifying");
            var expected = await ExpectedHashAsync(latest.Assets, asset.Name, ct).ConfigureAwait(false);
            lock (_gate) { _expectedSha = expected; }

            if (expected is { Length: 64 })
            {
                var actual = await Sha256Async(path, ct).ConfigureAwait(false);
                lock (_gate) { _actualSha = actual; }
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(path);
                    throw new InvalidDataException(
                        "SHA256 对不上：文件与发布公布的校验和不一致（可能下坏了或被改过），已删除");
                }
                lock (_gate) { _verified = true; }
            }
            else
            {
                lock (_gate) { _verified = false; }
            }

            lock (_gate) { _state = "ready"; _error = null; _speed = 0; }
            Log($"下载完成：{path}" + (expected is null
                ? "（发布未提供 SHA256，只核对了体积）"
                : "（SHA256 校验通过）"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(path);
            lock (_gate)
            {
                _state = "idle";
                _file = null;
                _tag = null;
                _received = 0;
                _total = 0;
                _speed = 0;
                _error = null;
            }
            Log("下载已取消");
        }
        catch (Exception e)
        {
            TryDelete(path);
            lock (_gate) { _state = "error"; _speed = 0; _error = e.Message; }
            Log("下载失败：" + e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>流式下载 + 进度。每次读都挂一个 20 秒看门狗：卡住就报错，而不是挂到天荒地老。</summary>
    private async Task DownloadAsync(ReleaseAsset asset, string path, CancellationToken ct)
    {
        using var resp = await Http
            .GetAsync(asset.Url!, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"更新源返回 HTTP {(int)resp.StatusCode}（下载地址可能已过期）");

        var total = resp.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : 0);
        lock (_gate) { _total = total; }

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                             81920, useAsync: true);

        var buffer = new byte[81920];
        var sw = Stopwatch.StartNew();

        while (true)
        {
            int n;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(StallTimeout);
                try
                {
                    n = await src.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException(
                        $"{StallTimeout.TotalSeconds:F0} 秒没收到数据，下载卡住了（网络中断或被限速）");
                }
            }

            if (n == 0) break;

            // 体积上限：源不报 Content-Length（chunked）时没有任何上界，异常/被劫持的源
            // 可以一直写，把 %LocalAppData% 所在盘写满。500 MB 对安装包足够宽松。
            if (_received + n > MaxBytes)
                throw new InvalidDataException($"安装包超过 {MaxBytes / 1048576} MB，已中止下载");

            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);

            lock (_gate)
            {
                _received += n;
                var secs = sw.Elapsed.TotalSeconds;
                if (secs > 0.2) _speed = _received / secs;
            }
        }

        lock (_gate)
        {
            _speed = 0;
            if (_total > 0 && _received != _total)
                throw new InvalidDataException($"下载不完整：{_received} / {_total} 字节");
            // 源没报长度且一个字节都没拿到 ⇒ 别把 0 字节的"安装包"交给提权执行
            if (_total == 0 && _received == 0)
                throw new InvalidDataException("更新源返回了 0 字节（下载地址可能已失效）");
        }
    }

    /// <summary>
    /// 从发布附件里找 `SHA256.txt` 并取出**这个文件**那一行。取不到就返回 null（不报错）：
    /// 校验是加分项，不该让"发布忘了传校验和"变成"更新功能不可用"。
    /// </summary>
    private static async Task<string?> ExpectedHashAsync(IReadOnlyList<ReleaseAsset> assets,
                                                         string fileName, CancellationToken ct)
    {
        var meta = assets.FirstOrDefault(a => string.Equals(a.Name, HashAssetName, StringComparison.OrdinalIgnoreCase))
                   ?? assets.FirstOrDefault(a => a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                                              || a.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                                                 && a.Name.Contains("SHA256", StringComparison.OrdinalIgnoreCase));
        if (meta?.Url is not { Length: > 0 } url) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedUrl(uri)) return null;

        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"取校验和失败（HTTP {(int)resp.StatusCode}），本次只核对体积");
                return null;
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > 64 * 1024) text = text[..(64 * 1024)];   // 只可能是两行，别被超大响应拖住

            foreach (var line in text.Split('\n'))
            {
                // 形如 `<hash>  <文件名>`；也兼容 `sha256:<hash> *<文件名>`
                var parts = line.Trim().Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!string.Equals(parts[^1], fileName, StringComparison.OrdinalIgnoreCase)) continue;

                var hash = parts[0].Replace("sha256:", "", StringComparison.OrdinalIgnoreCase).Trim();
                if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) return hash.ToLowerInvariant();
            }

            Log("校验和文件里没有 " + fileName + " 那一行，本次只核对体积");
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log("取校验和出错（本次只核对体积）：" + e.Message);
            return null;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- 选择 / 落点

    /// <summary>
    /// 从附件里挑安装包：优先名字带 `setup` 的 exe，其次任意 exe。
    /// **不选 zip**：绿色版包无法在本机"安装"，硬下下来只会给用户一个不知道怎么用的文件。
    /// </summary>
    private static ReleaseAsset? PickInstaller(ReleaseInfo r)
    {
        var exes = r.Assets.Where(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        return exes.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
               ?? exes.FirstOrDefault();
    }

    private static string FileNameOf(ReleaseAsset asset, ReleaseInfo latest)
    {
        var name = Path.GetFileName(asset.Name);
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = $"QControl-{latest.Version}-setup-win-x64.exe";
        return name;
    }

    /// <summary>下载根目录（按用户，不用 ProgramData：提权进程写的是同一个用户目录，不受 ACL 影响）。</summary>
    private static string Root
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            return Path.Combine(local, "Q-Control", "update");
        }
    }

    /// <summary>每个版本一个目录：`…\update\v0.2.0\QControl-0.2.0-setup-win-x64.exe`。</summary>
    private static string VersionDir(string tag)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(tag.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(Root, safe.Length > 0 ? safe : "latest");
    }

    /// <summary>清掉别的版本目录（每次开始新一轮下载前做，免得攒下一堆安装包）。</summary>
    private static void PruneOtherVersions(string keepDir)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var keep = Path.GetFullPath(keepDir).TrimEnd(Path.DirectorySeparatorChar);
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                                  keep, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.Delete(dir, recursive: true);
                Log("清掉旧下载：" + dir);
            }
        }
        catch (Exception e)
        {
            Log("清理旧下载失败（不影响本次）：" + e.Message);
        }
    }

    /// <summary>
    /// 安装器会装到哪 —— 读卸载项的 `InstallLocation`（安装器自己写的），没装过就按默认路径。
    /// 顺带判断"当前这份是不是安装版"：不是的话安装会**在别处多出一份**，确认框要说清楚。
    /// </summary>
    private static (string? Dir, bool Portable) DetectTarget()
    {
        try
        {
            var def = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Q-Control");

            string? installed = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QControl");
                installed = key?.GetValue("InstallLocation") as string;
            }
            catch (Exception e)
            {
                Log("读卸载项失败（按默认路径判断）：" + e.Message);
            }

            var dir = string.IsNullOrWhiteSpace(installed) ? def : installed!.Trim().Trim('"');
            var here = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var there = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            return (there, !string.Equals(here, there, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return (null, false);   // 探测失败不该拦住安装：只是确认框少一句提示
        }
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>
    /// 只允许从 https 下载 —— 这个包最后是要**提权执行**的，明文地址等于把提权入口交给链路上的任何人。
    ///
    /// 唯一的例外：`--update-source` 演练时允许本机回环的 http。
    /// 那条参数本来就把两个更新源都指到了假源上（见 <see cref="UpdateChecker.SourceOverride"/>），
    /// 假源只能起在 127.0.0.1 上、没有证书 —— 不放行的话"下载 / 校验 / 安装"整条链路
    /// 就只能等真发一版才能验。回环地址出不了这台机器，风险为零。
    /// </summary>
    private static bool AllowedUrl(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp
            && uri.IsLoopback
            && UpdateChecker.SourceOverride is { Length: > 0 });

    private void SetState(string state)
    {
        lock (_gate) { _state = state; }
    }

    private JsonObject Reply()
    {
        var o = Snapshot();
        o["ok"] = _error is null;
        if (_error is not null) o["error"] = _error;
        return o;
    }

    /// <summary>失败：把原因写进状态（界面靠它显示一行说明），回 ok:false。</summary>
    private JsonObject Fail(string error)
    {
        lock (_gate) { _error = error; }
        Log(error);
        return Reply();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            // 版本目录只为这一个文件而建：删干净了就把空目录收掉，别在 %LocalAppData% 攒一堆空壳
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir
                && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception e) { Log("删半截文件失败：" + e.Message); }
    }

    private static string Mb(long bytes)
        => bytes >= 1048576
            ? (bytes / 1048576.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " KB";

    private static void Log(string line)
    {
        try { Console.WriteLine("[update] " + line); } catch { /* 控制台没了不影响下载 */ }
        try { Logging.FileLog.Note(line); } catch { /* 日志没开也不影响下载 */ }
    }
}
