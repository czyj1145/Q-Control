using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QControl.App.Http;
using QControl.Core.Logs;

namespace QControl.App.Logging;

/// <summary>
/// 文件日志：把 <see cref="Console"/> 的 stdout / stderr **同时**写一份到
/// `logs/cc-yyyy-MM-dd.log`（tee，控制台照常能看到）。
///
/// 为什么需要：`--tray`（开机自启走这条）由静默启动器 `CreateNoWindow` 拉起，
/// 主程序是控制台子系统但**没有控制台** —— `Console.WriteLine` 全部丢进黑洞。
/// 2026-10-20 排查「开机有没有接管 / HWiNFO 有没有被拉起来」时，只能靠
/// `model.json` 的 mtime 反推，一眼日志都看不到，才有了这个文件。
///
/// 设计取舍：
///   · **不去改 130 处 `Console.Write*`**，直接 `Console.SetOut/SetError` 接管，
///     现有输出零改动。
///   · **按行加时间戳**（`HH:mm:ss.fff`）：日期已经在文件名里，行内只给时间。
///   · **绝不因为写日志坏事**：目录建不了 / 盘满 / 文件被占 → 静默退化成只写控制台，
///     一个异常都不往外抛。日志组件的失败不该让主程序起不来。
///   · 按天分文件；单日超 <see cref="MaxBytes"/> 就把当天旧档改名为 `.old`
///     （每天最多两份）；启动时清掉 <see cref="KeepDays"/> 天前的。
///   · **每行立刻 flush**：开机阶段随时可能被强杀 / 断电，宁可慢一点也要留下证据。
///   · **有总开关**（2026-10-29 用户要求）：关掉就 `Detach()` —— 句柄关掉、Console 还原、
///     之后一行都不落盘（不是"写了再删"）。真源是 `presets.log.enabled`，重启后仍然关着。
/// </summary>
internal static partial class FileLog
{
    /// <summary>保留几天的日志（含当天）。</summary>
    public const int KeepDays = 7;

    /// <summary>单个日志文件上限；超了把当天旧档改名 `.old` 后继续写新文件。</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    private static Sink? _sink;
    private static Tee? _outTee;          // 记下来是为了 Detach 时能把 Console 还原回去
    private static Tee? _errTee;

    /// <summary>
    /// 这次运行到底往哪个目录写（**先挂上过一次**才有值，绝对路径）。
    ///
    /// ⚠️ 不能只记在 <see cref="Sink"/> 上（2026-10-29 修）：总开关一关 sink 就没了，
    ///    目录也跟着"忘掉"—— 于是 `--log D:\x` 的进程关一次开关之后：占用统计和
    ///    「打开目录」跑去默认目录，再打开开关又写进默认目录，日志凭空换了地方。
    /// </summary>
    private static string? _dir;

    // 总开关（"用户要不要写"）。默认**开**：这个文件一直是开着的，别把默认态改成关。
    // ⚠️ 与"挂没挂上"是两件事：目录不可写时开关仍是开、但一个字都写不进去（看 OffReason）。
    //    界面上的开关读 Enabled，卡片上的状态标签读 Active/Healthy —— 两者别互相代替。
    private static volatile bool _off;

    /// <summary>
    /// 总开关（`presets.log.enabled` 的生效值）：**用户要不要写日志**。
    /// 注意它不等于"真的在写"—— 目录不可写、sink 写坏时它仍是 true，
    /// 真实状态看 <see cref="Active"/> / <see cref="Healthy"/>，原因看 <see cref="OffReason"/>。
    /// </summary>
    public static bool Enabled => !_off;

    /// <summary>
    /// 没在写日志时的**原因**（在写时为 null）：总开关关着 / 带 --no-log / 目录写不进去。
    /// 界面与「区间记录」都照这个说话 —— 别再把 `--no-log` 写死在界面里，那只是三种之一。
    /// </summary>
    public static string? OffReason { get; private set; }

    /// <summary>当前正在写的日志文件（未启用时为 null）。</summary>
    public static string? CurrentPath => _sink?.CurrentPath;

    public static bool Active => _sink is not null;

    /// <summary>
    /// 日志文件**还能不能写**。`Active` 只表示挂上了 sink；sink 一旦写失败过就会
    /// 永久闭嘴（<c>_dead</c>，见 Sink.EmitLocked），此时先说清楚，别让界面以为写进去了。
    /// </summary>
    public static bool Healthy => _sink is { Dead: false };

    /// <summary>日志目录（未启用也给"将来会写哪"）—— 界面「打开日志目录」用它。</summary>
    /// <remarks>
    /// 优先级：正挂着的 sink > **这次运行已经用过的目录** > 默认目录。
    /// 中间那档必须有：总开关关掉后 sink 是 null，但"我们用哪个目录"不能跟着失忆
    /// （否则 `--log` 关一次开关就漂回默认目录）。
    /// </remarks>
    public static string DirectoryPath => _sink?.Dir ?? _dir ?? DefaultDir();

    /// <summary>
    /// 日志目录占用（界面「清理」按钮用，也让用户一眼看出日志吃了多少盘）。
    ///
    /// 只统计**我们自己这三类**：当天/历史 `cc-*.log`、轮转出来的 `cc-*.old.log`、
    /// 区间导出的 `capture-*.log`。别的一律不碰 —— 用户往这个目录放过东西时不关我们事。
    /// </summary>
    public readonly record struct LogStats(int CcFiles, long CcBytes, int OldFiles, long OldBytes,
                                           int CapFiles, long CapBytes)
    {
        public int Files => CcFiles + OldFiles + CapFiles;
        public long Bytes => CcBytes + OldBytes + CapBytes;
    }

    public static LogStats DirStats()
    {
        try
        {
            var dir = DirectoryPath;
            if (!Directory.Exists(dir)) return default;

            var ccF = 0; var ccB = 0L;
            var oldF = 0; var oldB = 0L;
            var capF = 0; var capB = 0L;

            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var n = Path.GetFileName(f);
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }

                if (n.StartsWith("capture-", StringComparison.Ordinal) && n.EndsWith(".log", StringComparison.Ordinal))
                { capF++; capB += len; }
                else if (n.EndsWith(".old.log", StringComparison.Ordinal))
                { oldF++; oldB += len; }
                else if (CcDayRegex().IsMatch(n))
                { ccF++; ccB += len; }
            }
            return new LogStats(ccF, ccB, oldF, oldB, capF, capB);
        }
        catch
        {
            return default;      // 统计失败不该让接口失败，界面显示 0 即可
        }
    }

    /// <summary>
    /// 删掉日志目录里我们自己那三类文件（当天日志、轮转旧档、区间导出）。
    ///
    /// ⚠️ **只删匹配这三类的文件**，绝不递归、绝不删目录、绝不碰别的文件名 ——
    ///    用户往 logs/ 放过东西时不关我们事（删除是不可逆的，宁可少删）。
    /// ⚠️ 当前正被 writer 开着的当天日志：Windows 上删不掉（共享冲突），
    ///    如实计为"没删掉"，让调用方在回执里说清楚，别假装删了。
    /// </summary>
    public static (int Deleted, long Freed, int Failed) ClearLogs()
    {
        var deleted = 0;
        var freed = 0L;
        var failed = 0;
        try
        {
            var dir = DirectoryPath;
            if (!Directory.Exists(dir)) return (0, 0, 0);

            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var n = Path.GetFileName(f);
                var mine = (n.StartsWith("capture-", StringComparison.Ordinal)
                            && n.EndsWith(".log", StringComparison.Ordinal))
                           || n.EndsWith(".old.log", StringComparison.Ordinal)
                           || CcDayRegex().IsMatch(n);
                if (!mine) continue;

                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                try
                {
                    File.Delete(f);
                    deleted++;
                    freed += len;
                }
                catch
                {
                    failed++;      // 正被 writer 开着 / 权限不足
                }
            }
        }
        catch { /* 见上：能删多少删多少 */ }
        return (deleted, freed, failed);
    }

    /// <summary>当天日志文件名：`cc-yyyy-MM-dd.log`。</summary>
    [GeneratedRegex(@"^cc-\d{4}-\d{2}-\d{2}\.log$", RegexOptions.None)]
    private static partial Regex CcDayRegex();

    /// <summary>
    /// 关掉当前句柄并按需重开一份当天的日志文件。
    ///
    /// 为什么清理后要调：Windows 上**删掉正在写的文件是允许的**（句柄还开着，
    /// 内容继续写进那个已经没有目录项的 inode）—— 表现就是"清理成功，但之后的
    /// 日志你看不到文件"。所以清完必须重开一次，让今天的日志重新可见、从零开始。
    /// ⚠️ 幂等且安全：句柄重建失败就静默退回原状（下一个 EnsureLocked 会再试）。
    /// </summary>
    public static void Reopen()
    {
        var sink = _sink;
        if (sink is null) return;
        try { sink.Roll(); } catch { /* 重开失败不该让清理失败 */ }
    }

    /// <summary>
    /// 读当天日志的尾巴（最多 <paramref name="maxLines"/> 行，返回总字节数）。
    ///
    /// 只读最后 32KB 再从倒二行开始取 —— 日志单日能到 8MB，整读会让「关于」页
    /// 每次切过去都卡一下。窗口起点切断了第一行（还可能切在多字节字符中间），
    /// 所以**不是从文件头读的那一次要丢掉第一行**。
    /// 文件被我们自己的 writer 开着，必须以 ReadWrite + Delete 共享打开。
    /// </summary>
    public static (IReadOnlyList<string> Lines, long Bytes) Tail(int maxLines)
    {
        var path = _sink?.CurrentPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return (Array.Empty<string>(), 0);

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            var size = fs.Length;
            const int window = 32 * 1024;
            var take = (int)Math.Min(size, window);
            if (take <= 0) return (Array.Empty<string>(), size);

            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = 0;
            while (read < take)
            {
                var n = fs.Read(buf, read, take - read);
                if (n <= 0) break;
                read += n;
            }

            var text = Encoding.UTF8.GetString(buf, 0, read);
            var all = text.Replace("\r\n", "\n").Split('\n');
            var from = take < size ? 1 : 0;     // 半截行（见上）
            var lines = all.Skip(from).Where(l => l.Length > 0).ToList();
            if (lines.Count > maxLines)
                lines = lines.Skip(lines.Count - maxLines).ToList();
            return (lines, size);
        }
        catch
        {
            return (Array.Empty<string>(), 0);   // 读日志失败不该让接口失败
        }
    }

    /// <summary>
    /// 接管 Console 输出。**幂等**：重复调用返回同一个路径。
    /// 失败一律静默返回 null（调用方只需决定要不要在 banner 里提一句）。
    /// </summary>
    public static string? Attach(string? dir = null)
    {
        if (_sink is { } existing)
        {
            _off = false;
            OffReason = null;
            return existing.CurrentPath;
        }

        try
        {
            // 一律转成绝对路径：`--log .\logs` 这种相对目录会跟着工作目录变，
            // 界面「当前日志文件」也会显示成 "cc-….log · _tmp/shotlogs" 这种半截路径。
            var target = Path.GetFullPath(dir ?? DefaultDir());
            Directory.CreateDirectory(target);

            var sink = new Sink(target);
            if (!sink.Open())
            {
                // 目录写不进去：**不动总开关**（用户没关，是环境不让写），只说原因
                OffReason = "日志目录写不进去（权限或磁盘问题）";
                return null;
            }
            _sink = sink;
            _dir = target;          // 关掉开关后「占用 / 打开目录 / 再打开」都认这个目录

            var teeOut = new Tee(Console.Out, sink, null);
            var teeErr = new Tee(Console.Error, sink, "ERR");
            Console.SetOut(teeOut);
            Console.SetError(teeErr);
            _outTee = teeOut;
            _errTee = teeErr;

            _off = false;
            OffReason = null;

            // ⚠️ 这里**不写**"进程启动"那行：Attach 也会被总开关的"重新打开"调，
            //    在半夜 12 点补一行"进程启动"会把人骗到。启动横幅由 Program 在
            //    Attach 成功后自己 <see cref="Note"/>。

            // ⚠️ 这里**不挂** ProcessExit：Main 里已经挂了退出清理（交回转速 / 收 HWiNFO），
            //    而 ProcessExit 的处理器执行顺序不保证 —— 日志若先关，后面那几行退出日志就丢了。
            //    每行都 Flush + AutoFlush，进程退不退出都不影响已写内容，句柄交给 OS 回收。
            return sink.CurrentPath;
        }
        catch
        {
            _sink = null;
            OffReason = "日志目录写不进去（权限或磁盘问题）";
            return null;   // 日志失败 = 只写控制台，主程序照跑
        }
    }

    /// <summary>
    /// 不挂日志，并记下原因（启动时预判 `log.enabled=false`、或带了 `--no-log`）。
    /// 与 <see cref="Detach"/> 同一个终点，只是没有句柄可关 —— 合一个方法免得两处各写一遍。
    /// </summary>
    public static void Skip(string reason) => Detach(reason);

    /// <summary>
    /// 关掉日志：句柄释放、Console 还原成原来的 writer，之后再写**一行都不落盘**。
    ///
    /// 为什么必须还原 Console：<see cref="Tee"/> 是我们塞进 `Console.Out` 的；
    /// 只把它关掉不还原，之后所有 Console 输出都会经过一个 _dead 的 sink
    /// （内容还在，但多绕一层、且再也开不回来）。
    /// ⚠️ 幂等：本来就没挂也照样记下原因，界面据此说"为什么没有日志"。
    /// </summary>
    public static void Detach(string reason)
    {
        OffReason = reason;
        _off = true;

        // 先还原 Console 再关 sink：还原这一步本身不该有内容被吞掉
        if (_outTee is { } o) { try { Console.SetOut(o.Inner); } catch { } _outTee = null; }
        if (_errTee is { } e) { try { Console.SetError(e.Inner); } catch { } _errTee = null; }

        var sink = _sink;
        _sink = null;
        try { sink?.Close(); } catch { /* 关不掉也不该让调用方失败 */ }
    }

    /// <summary>
    /// 界面总开关（`POST /api/app/settings {"log_enabled": …}`）。
    /// 关 = 立刻停写（历史文件一个不删）；开 = 立刻接着写，不用重启。
    /// 返回 false 表示**没能打开**，<paramref name="error"/> 给出原因（不装作成功）。
    /// </summary>
    public static bool SetEnabled(bool on, out string? error)
    {
        error = null;
        if (!on)
        {
            Detach("日志总开关已关闭");
            return true;
        }

        // sink 写坏过（_dead）就整个换一个：Attach 见到非空 sink 会直接返回它，
        // 不换掉的话会"报告成功、其实一个字都写不进去"。
        if (_sink is { Dead: true }) Detach("日志文件写坏了，重开一份");

        // ⚠️ 必须带上这次运行用过的目录：`Attach()` 空参走的是默认目录，
        //    带 `--log` 的进程关一次开关再打开，日志就会悄悄搬到默认目录去。
        if (Attach(_dir) is null)
        {
            error = OffReason ?? "日志目录写不进去（权限或磁盘问题）";
            return false;
        }

        Note($"===== 日志已重新开启（pid={Environment.ProcessId}）=====");
        return true;
    }

    /// <summary>往日志里补一行：不带时间戳前缀的说明行（开关动作、启动分隔行用它）。</summary>
    public static void Note(string line)
    {
        try { _sink?.Note(line); } catch { /* 日志失败不影响主流程 */ }
    }

    /// <summary>
    /// 启动时读 `presets.log.enabled`，决定要不要挂日志（默认**开**：这个文件一直是开着的）。
    ///
    /// ⚠️ 故意在这里裸读一遍 model.json，而不是等 ModelStore：日志必须挂得比 AppState 早
    ///    （参数校验、FATAL 也要留证据），那时 ModelStore 还没构造。
    /// ⚠️ 读不到 / 文件坏 / 键缺失 ⇒ 一律按**开**：宁可多写一份日志，
    ///    也不要出现"以为在记、出事了翻不到一行"。
    /// </summary>
    public static bool PrefEnabled(string? modelPath)
    {
        try
        {
            if (modelPath is null || !File.Exists(modelPath)) return true;
            using var fs = new FileStream(modelPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("presets", out var presets)) return true;
            if (!presets.TryGetProperty("log", out var log)) return true;
            if (!log.TryGetProperty("enabled", out var enabled)) return true;
            return enabled.ValueKind != JsonValueKind.False;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 默认目录：`model.json` 所在的 ControlCenter 根下的 `logs/`
    /// （用户好找）；找不到 model.json 就放程序目录。
    /// </summary>
    private static string DefaultDir()
    {
        try
        {
            if (Paths.FindModel(null) is { } model && Path.GetDirectoryName(model) is { } root)
                return Path.Combine(root, "logs");
        }
        catch { }
        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    // ================================================================ 落盘

    private sealed class Sink
    {
        private readonly object _lock = new();
        private readonly string _dir;
        private SharedLogAppender? _appender;    // 跨进程安全追加（见 Core/Logs/SharedLogAppender）
        private DateOnly _day;
        private long _bytes;
        private bool _dead;                      // 写失败一次就永久闭嘴，绝不反复抛异常
        private readonly StringBuilder _pending = new();   // 还没凑满一行的字符

        public Sink(string dir) => _dir = dir;

        public string CurrentPath { get; private set; } = "";

        /// <summary>写到哪个目录（「打开日志目录」用，即使文件还没建出来也给得出）。</summary>
        public string Dir => _dir;

        /// <summary>写失败过一次就永久闭嘴，这里暴露给调用方判断"还写不写得进去"。</summary>
        public bool Dead => _dead;

        public bool Open()
        {
            lock (_lock)
            {
                try
                {
                    PurgeLocked();
                    EnsureLocked();
                    return !_dead;
                }
                catch
                {
                    _dead = true;
                    return false;
                }
            }
        }

        /// <summary>丢掉当前句柄、按需重建当天文件（<see cref="Reopen"/> 用）。</summary>
        public void Roll()
        {
            lock (_lock)
            {
                DisposeAppender();
                _bytes = 0;
                _day = default;          // 与今天不同 ⇒ EnsureLocked 一定会重建
                EnsureLocked();
            }
        }

        /// <summary>写一行「原始」日志（不带时间戳前缀，用于启动分隔行）。</summary>
        public void Note(string line)
        {
            lock (_lock)
            {
                if (_dead) return;
                EmitLocked(line, null);
            }
        }

        public void Push(string? text, string? tag)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                if (_dead) return;
                _pending.Append(text);

                // 按 '\n' 切行；Windows 的 "\r\n" 把 '\r' 丢掉
                var start = 0;
                while (true)
                {
                    var nl = IndexOf(_pending, '\n', start);
                    if (nl < 0) break;
                    var line = _pending.ToString(start, nl - start).TrimEnd('\r');
                    EmitLocked(line, tag);
                    start = nl + 1;
                }
                if (start > 0) _pending.Remove(0, start);

                // 有人用 Write 拼超长不分行（比如进度条）时别把内存撑爆
                if (_pending.Length > 4096)
                {
                    EmitLocked(_pending.ToString(), tag);
                    _pending.Clear();
                }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_pending.Length > 0) { try { EmitLocked(_pending.ToString(), null); } catch { } _pending.Clear(); }
                DisposeAppender();
                _dead = true;
            }
        }

        private void DisposeAppender()
        {
            try { _appender?.Dispose(); } catch { }
            _appender = null;
        }

        private void EmitLocked(string line, string? tag)
        {
            try
            {
                EnsureLocked();
                if (_appender is null) return;
                var prefix = tag is null ? "" : tag + " ";
                var text = $"{DateTime.Now:HH:mm:ss.fff} {prefix}{line}";
                if (!_appender.WriteLine(text))
                {
                    _dead = true;
                    DisposeAppender();
                    return;
                }
                // 取写完之后磁盘上的真实长度：别的进程也在追加时，缓存累加会一直低估，
                // 8MB 轮转就会迟迟不触发（日志无声地长下去）。
                _bytes = _appender.Length;
            }
            catch
            {
                _dead = true;
                DisposeAppender();
            }
        }

        /// <summary>按需开文件：跨天 或 超过上限就换一个。</summary>
        private void EnsureLocked()
        {
            if (_dead) return;

            var today = DateOnly.FromDateTime(DateTime.Now);

            // 常态（同一天、没超限）：一行都不多碰磁盘。
            // ⚠️ 用 Length 实时问，不用缓存的 _bytes —— 另一个进程可能刚写了很多。
            if (_appender is not null && today == _day && _appender.Length < MaxBytes) return;

            var path = Path.Combine(_dir, $"cc-{today:yyyy-MM-dd}.log");

            // ⚠️ 按**磁盘实际大小**判断，不能用缓存的 _bytes：跨天时那是昨天那份的大小。
            long existing = 0;
            try { if (File.Exists(path)) existing = new FileInfo(path).Length; } catch { }
            var rotate = existing >= MaxBytes;

            // ⚠️ **先松句柄再 Move**。Windows 上 rename 需要 DELETE 共享权限，
            //    我们自己开着这个文件（哪怕 FileShare.ReadWrite）也会把 Move 挡成
            //    ERROR_SHARING_VIOLATION —— 而那个异常一旦被 catch 吞掉，
            //    表现就是"超过上限还往里写"，日志无声地长到几个 G。
            DisposeAppender();

            if (rotate)
            {
                try { File.Move(path, Path.Combine(_dir, $"cc-{today:yyyy-MM-dd}.old.log"), overwrite: true); }
                catch { }
            }

            try
            {
                _appender = new SharedLogAppender(path);
                _bytes = _appender.Length;
                _day = today;
                CurrentPath = path;
            }
            catch
            {
                _dead = true;      // 目录忽然写不进去：如实让 Healthy 变 false，别装作还在记
                DisposeAppender();
            }
        }

        private void PurgeLocked()
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var f in Directory.EnumerateFiles(_dir, "cc-*.log"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }

        private static int IndexOf(StringBuilder sb, char c, int from)
        {
            for (var i = from; i < sb.Length; i++) if (sb[i] == c) return i;
            return -1;
        }
    }

    // ================================================================ tee

    /// <summary>同时写原控制台与文件。原控制台写失败（管道断了）不许影响程序。</summary>
    private sealed class Tee : TextWriter
    {
        private readonly Sink _sink;
        private readonly string? _tag;

        public Tee(TextWriter inner, Sink sink, string? tag)
        {
            Inner = inner;
            _sink = sink;
            _tag = tag;
        }

        public TextWriter Inner { get; }

        public override Encoding Encoding => Inner.Encoding;

        public override void Write(char value)
        {
            try { Inner.Write(value); } catch { }
            _sink.Push(value.ToString(), _tag);
        }

        public override void Write(string? value)
        {
            try { Inner.Write(value); } catch { }
            _sink.Push(value, _tag);
        }

        public override void WriteLine(string? value)
        {
            try { Inner.WriteLine(value); } catch { }
            if (_tag == "ERR") LogCapture.SignalError(value);
            _sink.Push((value ?? string.Empty) + "\n", _tag);
        }

        public override void Flush()
        {
            try { Inner.Flush(); } catch { }
        }
    }
}
