using System.Collections.Concurrent;
using System.Text;

namespace QControl.App.Logging;

/// <summary>一次区间记录的现状（给界面 / GET /api/app/about 用）。</summary>
internal readonly record struct CaptureSnapshot(
    bool Active, int Seq, DateTime Since, string Path, long Offset,
    bool Auto, bool AutoRunning, int AutoTemp, string Reason,
    string? LastFile, string? LastPath, int LastLines, long LastBytes, DateTime? LastAt);

/// <summary>一段已经导出的区间。</summary>
internal readonly record struct CaptureExport(
    string File, string Path, int Lines, long Bytes,
    DateTime From, DateTime To, int Seconds);

/// <summary>
/// 「区间记录」：在日志里打一对开始 / 结束标记，并把中间那段**单独导出成一个文件**。
///
/// 为什么不是"手动写一行日志"：用户要的是「截取出问题的那几分钟」——
/// 整天的大日志（单日可到 8 MB）他不想从头翻，只要那一段。
/// 所以这里的动作是**开 / 关**，不是"写一句备注"：
///   · **开始** → 写一行 `===== 区间记录开始 #N =====`，记下「标记行之前」的字节偏移；
///   · **结束** → 写 `===== 区间记录结束 #N · 共 T 行 =====`，把 [偏移, 结束标记之后)
///     原样复制成 `logs/capture-yyyyMMdd-HHmmss.log`（UTF-8 原始字节，不经二次编码）。
///
/// 设计取舍：
///   · **全量日志照旧一直在写**（开机排障离不开它），这里只是"多切一份"，不改变 FileLog；
///   · 边界用**字节偏移**而不是时间戳过滤：日志行首只有 `HH:mm:ss.fff`，跨零点后
///     比时间会把 23:59 和 00:01 判反；偏移是精确且与内容无关的；
///   · 偏移的代价是**怕换文件**：记录期间跨天或单日超 8 MB 触发轮转 ⇒ 起点失效。
///     那种情况如实报错（并说明重开一段），绝不给一份"看起来完整"的残缺文件；
///   · 状态放在**后端**（静态），所以关掉页面、刷新、隔几分钟再回来都还算数。
///   · 导出文件名用 `capture-` 前缀：FileLog 只清 `cc-*.log`（7 天），不会顺手删掉它。
/// </summary>
internal static class LogCapture
{
    private static readonly object _lock = new();

    private static bool _active;
    private static string _path = "";
    private static long _offset;
    private static DateTime _since;
    private static int _seq;

    /* ---- 自动记录 ----
       为什么要有：手动区间记录要求"问题发生时你人在现场按一下开始"。
       夜里/离开时出问题就抓不到 —— 所以加一个自动档：温度达到阈值或日志里出现
       错误时**自动**开始记录，回落后自动结束并导出。用户回来只看 capture-*.log。
       ⚠️ 默认**关**：它会自己产生日志文件，属于"默认不该默默开着"的那类。 */
    private static bool _auto;
    private static int _autoTemp = 90;
    private static bool _autoRunning;         // 当前这段是自动开的（决定回落时要不要自动收）
    private static string _reason = "";       // 自动段：为什么开的（写进段头，也回给界面）
    private static DateTime _coolSince;       // 温度回落到阈值以下的起始时刻

    /// <summary>
    /// 同一条错误 60 分钟内**只算一次**触发。
    ///
    /// 为什么需要：日志里的 ERR 有一大半是**常驻状态**而不是"现场"——最典型的是 HWiNFO 没装：
    /// 拉起失败后按退避每十几～五十几秒重报一次 ERR，每次都刷新"最近 30 秒有错"这个窗口，
    /// 于是 60°C 的机器也被判成"出事了"，一段接一段地录（2026-11-08 用户报
    /// 「没到温度又开始记录了」，现场就是这条）。现在按**错误的归一化文本**认签名：
    /// 同一条错误 60 分钟内只开一段，换一种错误照旧立刻开 —— 录"新问题"，不重复录"还在报的老问题"。
    /// ⚠️ 只增不删会把整天的错误签名攒在内存里，超过 <see cref="SeenLimit"/> 时按 hold 清一遍。
    /// </summary>
    private static readonly TimeSpan ErrRepeatHold = TimeSpan.FromMinutes(60);
    private const int SeenLimit = 64;
    private static readonly ConcurrentDictionary<int, long> _errSeen = new();   // 签名 → 上次算作触发的 UtcTicks
    private static long _errPendingTicks;     // 还没被自动档用掉的"新错误"（UtcTicks，0 = 没有）

    /// <summary>
    /// 单段自动记录的上限：防止阈值附近反复跳动时永远不结束（5 分钟）。
    /// </summary>
    private static readonly TimeSpan AutoMaxSpan = TimeSpan.FromMinutes(5);

    /// 回落到阈值以下并持续这么久才自动结束（温度墙附近会抖，不能一降就收）。</summary>
    private static readonly TimeSpan AutoCoolHold = TimeSpan.FromSeconds(20);

    private static string? _lastFile;
    private static string? _lastPath;
    private static int _lastLines;
    private static long _lastBytes;
    private static DateTime? _lastAt;

    public static CaptureSnapshot Snapshot()
    {
        lock (_lock) return SnapshotLocked();
    }

    /// <summary>
    /// 「最近导出」那个文件已经不在磁盘上了 ⇒ 忘掉它。
    ///
    /// 为什么需要：`POST /api/app/log/clear` 会把 `capture-*.log` 一起删掉，
    /// 但这段状态是在**内存**里的（<c>_lastFile/_lastPath</c>）—— 界面于是继续显示
    /// 「最近导出 capture-xxxx.log」，点开悬停给出的还是一个不存在的路径。
    /// 清完日志调一次，让界面与磁盘一致（宁可空着，也不摆一个删掉的文件）。
    /// </summary>
    public static void ForgetMissingLast()
    {
        lock (_lock)
        {
            if (_lastPath is null) return;
            bool gone;
            try { gone = !File.Exists(_lastPath); }
            catch { gone = true; }      // 连"在不在"都问不出来：当作没了，别骗人
            if (!gone) return;

            _lastFile = null;
            _lastPath = null;
            _lastLines = 0;
            _lastBytes = 0;
            _lastAt = null;
        }
    }

    /// <summary>此刻是否在记录（采样循环每 2 拍问一次，决定要不要写心跳行）。</summary>
    public static bool Active { get { lock (_lock) return _active; } }

    /// <summary>自动记录开关的当前值（GET /api/app/settings 与设置页对齐用）。</summary>
    public static bool AutoOn { get { lock (_lock) return _auto; } }

    /// <summary>触发温度（°C）。</summary>
    public static int AutoTemp { get { lock (_lock) return _autoTemp; } }

    /// <summary>当前这段是不是**自动**开的（界面显示"正在记录 mm:ss（自动）"）。</summary>
    public static bool AutoRunning { get { lock (_lock) return _autoRunning; } }

    /// <summary>自动记录开关（presets.log.auto）。关掉时若正在自动记录，顺手收一段。</summary>
    public static void SetAuto(bool on, int temp)
    {
        lock (_lock)
        {
            // 重新打开 = 重新开始观察：把"这条错误已经录过"的记忆清掉，
            // 否则用户关掉再打开，明明还在报的错误反而一段都不录（他会以为是开关坏了）。
            if (on && !_auto)
            {
                _errSeen.Clear();
                Volatile.Write(ref _errPendingTicks, 0);
            }

            _auto = on;
            _autoTemp = Math.Clamp(temp, 60, 110);
            if (!on && _autoRunning && _active)
            {
                // 用户手动关开关 ⇒ 把自动开的这段收掉（导出），别留一个永远不结束的记录
                StopLocked(out _, out _);
            }
        }
    }

    /// <summary>有一条 ERR 日志落盘（FileLog 的 tee 调它）—— 自动档的触发器之一。</summary>
    /// <remarks>
    /// ⚠️ **绝不能在这里拿 <c>_lock</c>**：调用方是 FileLog 的 tee，那时
    /// <see cref="Sink"/> 的锁正被持有；而自动档的开录/收段会在 <c>_lock</c> 内
    /// <c>Console.WriteLine</c>（⇒ 要拿 Sink 的锁）—— 两边互相等就是死锁。
    /// 所以这里只用 <see cref="Volatile"/> 写 + 无锁字典（<see cref="ConcurrentDictionary{TKey,TValue}"/>），
    /// 读取方（<see cref="AutoCheck"/>）在自己该拿锁的地方读。
    /// </remarks>
    public static void SignalError(string? line)
    {
        var now = DateTime.UtcNow;
        var sig = Signature(line);
        if (sig == 0 || !Fresh(sig, now)) return;   // 认不出内容 / 这条错误刚录过（见 ErrRepeatHold）

        _errSeen[sig] = now.Ticks;
        Volatile.Write(ref _errPendingTicks, now.Ticks);
    }

    /// <summary>
    /// 自动档的心跳：每 2 拍由采样循环调一次（温度从 /api/status 那一帧里取）。
    ///
    /// 触发 = CPU 或 GPU 达到阈值，或出现**一条还没录过的** ERR 日志；
    /// 结束 = 全都回落到阈值以下并持续 <see cref="AutoCoolHold"/>，或单段超过
    /// <see cref="AutoMaxSpan"/>（防止阈值附近反复抖动时永远不结束）。
    ///
    /// ⚠️ 错误是**一次性**的：读走就清。留着不清的话，一条常驻错误会让这一段永远判不成"冷"，
    ///    自动档就成了"一直在录"（这正是 2026-11-08 那次报障的形态）。
    /// </summary>
    public static void AutoCheck(double? cpuTemp, double? gpuTemp)
    {
        lock (_lock)
        {
            if (!_auto) return;

            var now = DateTime.Now;

            // 温度 / 错误两个触发条件各留一份"为什么"——段头与界面都要说得清
            var hotWhy = "";
            if (cpuTemp is { } c && c >= _autoTemp) hotWhy = $"CPU {c:0}°C ≥ {_autoTemp}°C";
            else if (gpuTemp is { } g && g >= _autoTemp) hotWhy = $"GPU {g:0}°C ≥ {_autoTemp}°C";
            var hot = hotWhy.Length > 0;

            var errTicks = Volatile.Read(ref _errPendingTicks);
            var recentError = errTicks != 0
                              && DateTime.UtcNow.Ticks - errTicks < TimeSpan.FromSeconds(30).Ticks;
            if (recentError) Volatile.Write(ref _errPendingTicks, 0);   // 用掉了（过期的留给下一次覆盖）

            if (!_active)
            {
                if (hot) StartLocked(auto: true, hotWhy);
                else if (recentError) StartLocked(auto: true, "出现错误");
                return;
            }

            if (!_autoRunning) return;     // 手动开的段：自动逻辑绝不插手

            if (now - _since >= AutoMaxSpan) { StopLocked(out _, out _); return; }

            var cool = !hot && !recentError;
            if (!cool) { _coolSince = default; return; }
            if (_coolSince == default) _coolSince = now;
            if (now - _coolSince >= AutoCoolHold) StopLocked(out _, out _);
        }
    }

    private static void StartLocked(bool auto, string reason)
    {
        if (_active) return;
        var path = FileLog.CurrentPath;
        if (string.IsNullOrEmpty(path) || !FileLog.Healthy) return;

        long before = 0;
        try { if (File.Exists(path)) before = new FileInfo(path).Length; } catch { }

        _seq++;
        _reason = auto ? reason : "";
        Console.WriteLine(auto
            ? $"===== 区间记录开始 #{_seq}（自动：{reason}）====="
            : $"===== 区间记录开始 #{_seq} =====");

        _active = true;
        _autoRunning = auto;
        _path = path;
        _offset = before;
        _since = DateTime.Now;
        _coolSince = default;
    }

    /// <summary>这条错误画出来的签名，最近 <see cref="ErrRepeatHold"/> 内没有被算过触发 ⇔ true。</summary>
    private static bool Fresh(int sig, DateTime now)
    {
        if (_errSeen.Count > SeenLimit) PruneSeen(now);
        return !_errSeen.TryGetValue(sig, out var at) || now.Ticks - at >= ErrRepeatHold.Ticks;
    }

    private static void PruneSeen(DateTime now)
    {
        foreach (var kv in _errSeen)
        {
            if (now.Ticks - kv.Value >= ErrRepeatHold.Ticks) _errSeen.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// 错误行的"签名"：掐掉行首时间戳，数字一律折成 `#`、连续空白折成一个空格，取前 64 个字符。
    ///
    /// 目的只有一个 —— 同一件事反复报时落到同一个签名上
    /// （`54s 内不再尝试` 与 `40s 内不再尝试`、`超时 120ms` 与 `超时 350ms` 都是同一件事）。
    /// → 只取前 64 个字符也是这个道理：日志行后半段常带路径与数值，多取只会把同一个错误认成两个。
    /// </summary>
    private static int Signature(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return 0;

        var s = line.Trim();
        var i = 0;
        if (s.Length >= 9 && s[2] == ':' && s[5] == ':') i = 8;    // 行首 HH:mm:ss
        while (i < s.Length && (s[i] == '.' || char.IsDigit(s[i]))) i++;   // 还有 .fff

        var sb = new StringBuilder(72);
        var lastHash = false;
        for (; i < s.Length && sb.Length < 64; i++)
        {
            var ch = s[i];
            if (char.IsDigit(ch))
            {
                if (!lastHash) { sb.Append('#'); lastHash = true; }
                continue;
            }
            lastHash = false;
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length == 0) return 0;

        var h = StringComparer.Ordinal.GetHashCode(sb.ToString());
        return h == 0 ? 1 : h;      // 0 是"没有待触发错误"的哨兵值，撞上就挪一位
    }

    private static bool StopLocked(out string? error, out CaptureExport? export)
    {
        error = null;
        export = null;
        if (!_active) { error = "现在没有在记录"; return false; }

        var path = FileLog.CurrentPath;
        var from = _offset;
        var started = _since;
        var seq = _seq;
        var auto = _autoRunning;
        _active = false;
        _autoRunning = false;

        // 记录期间换了文件（跨天 / 单日超 8MB 轮转）：偏移已经指不到原来的位置。
        if (string.IsNullOrEmpty(path) || !File.Exists(path)
            || !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
        {
            error = "记录期间日志换了文件（跨天，或单日超过 8MB 轮转），这一段没法按偏移完整截取；"
                    + "请重新开始记录";
            return false;
        }

        long mid = 0;
        try { mid = new FileInfo(path).Length; } catch { }
        if (mid < from)
        {
            error = "日志文件被外部截短/删过了，起点已失效；请重新开始记录";
            return false;
        }

        // 先数一遍，把行数写进结束标记 —— 打开导出文件就能看到这段有多大。
        // ⚠️ 口径 = **内容行**（去掉开始标记那一行）：用户看到的"这段有多少行"
        //    不该把开发者自己写的边界行算进去；两端标记都还在文件里，但不算数。
        var body = ReadRange(path, from, mid);
        var lines = Math.Max(0, CountLines(body) - 1);

        Console.WriteLine($"===== 区间记录结束 #{seq} · 内容 {lines} 行 =====");

        long end = mid;
        try { end = new FileInfo(path).Length; } catch { }

        // 再读一次：这次带上结束标记，导出文件的头尾都有关闭边界
        var all = ReadRange(path, from, end);

        var file = "";
        var target = "";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) dir = FileLog.DirectoryPath;
            file = $"capture-{started:yyyyMMdd-HHmmss}.log";
            target = Path.Combine(dir, file);
            if (File.Exists(target))                     // 同一秒连开两段：加序号
            {
                file = $"capture-{started:yyyyMMdd-HHmmss}-{seq}.log";
                target = Path.Combine(dir, file);
            }
            File.WriteAllBytes(target, all);             // 原始字节，不经二次编码（不会加 BOM）
        }
        catch (Exception e)
        {
            error = "区间读出来了，但写不进导出文件：" + e.Message;
            return false;
        }

        _lastFile = file;
        _lastPath = target;
        _lastLines = lines;
        _lastBytes = all.Length;
        _lastAt = DateTime.Now;

        var to = DateTime.Now;
        export = new CaptureExport(file, target, lines, all.Length,
                                   started, to, (int)Math.Max(0, (to - started).TotalSeconds));
        _ = auto;
        return true;
    }

    private static CaptureSnapshot SnapshotLocked()
        => new(_active, _seq, _since, _path, _offset,
               _auto, _autoRunning, _autoTemp, _reason,
               _lastFile, _lastPath, _lastLines, _lastBytes, _lastAt);

    // ================================================================ 开始

    public static bool Start(out CaptureSnapshot snap, out string? error)
    {
        lock (_lock)
        {
            error = null;
            if (_active)
            {
                snap = SnapshotLocked();
                error = "已经在记录了（先「结束并导出」再开新的一段）";
                return false;
            }
            if (!FileLog.Healthy)
            {
                snap = SnapshotLocked();
                // 原因由 FileLog 说：总开关关着 / --no-log / 目录写不进去 —— 三件事别混成一句
                error = FileLog.Active
                    ? "日志文件已经写不进去了，记录会是一片空白"
                    : (FileLog.OffReason ?? "日志没有在写") + "，没有日志可截";
                return false;
            }
            if (string.IsNullOrEmpty(FileLog.CurrentPath))
            {
                snap = SnapshotLocked();
                error = "日志文件还没建出来（第一行还没写），稍等一下再试";
                return false;
            }

            StartLocked(auto: false, "");
            snap = SnapshotLocked();
            return true;
        }
    }

    // ================================================================ 结束 + 导出

    public static bool Stop(out CaptureSnapshot snap, out string? error, out CaptureExport? export)
    {
        lock (_lock)
        {
            var ok = StopLocked(out error, out export);
            snap = SnapshotLocked();
            return ok;
        }
    }

    // ================================================================ 工具

    /// <summary>
    /// 读 [from, to) 的原始字节。日志被我们自己的 writer 开着，
    /// 必须以 ReadWrite + Delete 共享打开，否则会 ERROR_SHARING_VIOLATION。
    /// </summary>
    private static byte[] ReadRange(string path, long from, long to)
    {
        var len = (int)Math.Max(0, Math.Min(to - from, int.MaxValue));
        if (len == 0) return [];

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(from, SeekOrigin.Begin);
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = fs.Read(buf, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        return read == len ? buf : buf[..read];
    }

    /// <summary>
    /// 数行数。日志每行以 '\n' 结尾，**最后一行可能没有换行**（刚写的那一刻），
    /// 所以「换行数」与「最后一段非空」要分开算。
    /// </summary>
    private static int CountLines(byte[] utf8)
    {
        if (utf8.Length == 0) return 0;
        var text = Encoding.UTF8.GetString(utf8);
        var n = 0;
        foreach (var c in text) if (c == '\n') n++;
        return text.EndsWith('\n') ? n : n + 1;
    }
}
