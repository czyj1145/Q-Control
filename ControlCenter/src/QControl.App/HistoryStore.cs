using System.Globalization;
using System.Text;
using QControl.Core.History;

namespace QControl.App;

/// <summary>
/// 历史采样的**落盘与导出**（`history/hist-yyyy-MM-dd.csv`，一天一个文件）。
///
/// 为什么要有它：`/api/history` 的 150 点只在内存里，进程一退就没了 ——
/// 用户回头问"昨晚 22 点风扇为什么一直高转"时，除了日志里那几行事件什么都没有。
/// 现在每秒采样、**每 5 秒落一行**，滚动保留 <see cref="KeepDays"/> 天，
/// 界面上「导出 CSV」把保留期内的数据一次倒出来（Excel 直接能开）。
///
/// 设计取舍（每条都对应一个真实故障或一次明确的取舍）：
///   · **5 秒一行**，不是 1 秒：1 Hz 一天约 4.7 MB、七天 33 MB，而曲线分析要的
///     是"几分钟尺度"的趋势 —— 5 秒已经足够，体积降到 1/5（约 1 MB/天）。
///     界面曲线仍是 1 秒（内存那 150 点），两者互不影响。
///   · **每行立刻 flush**（只到 OS，不 fsync）：程序随时可能被强杀，宁可慢一点
///     也要让最后几秒的数落下去；fsync 每秒一次会真的拖慢采样线程。
///   · **写失败一次就永久闭嘴**（<see cref="_dead"/>，与 <see cref="Logging.FileLog"/> 同规矩）：
///     磁盘满 / 目录只读时不能每 5 秒抛一次异常、刷一屏红字。
///   · **导出写临时文件再改名**：中途失败不会把用户已经选好的同名文件截成半截。
///   · **只删自己认得的文件名**（`hist-yyyy-MM-dd.csv`，日期解析失败一律不碰）——
///     删除不可逆，宁可多留一个文件，也不能误删用户往这个目录里放的别的东西。
///
/// ⚠️ 目录跟着 model.json 走（与 `logs/` 同一套规则）：开发版在仓库里、安装版在
///    `%ProgramData%\Q-Control\history`、便携版在 `app\history`。别改成 exe 目录 ——
///    安装版的那个目录普通用户写不进去。
/// </summary>
internal static class HistoryStore
{
    /// <summary>保留几天（含当天）。与日志的 7 天对齐，用户只需要记一个数。</summary>
    public const int KeepDays = 7;

    /// <summary>落盘间隔（秒）。界面曲线是 1 秒，落盘 5 秒 —— 理由见类注释。</summary>
    public const int SampleIntervalSec = 5;

    private static readonly object _lock = new();
    private static string? _dir;
    private static StreamWriter? _writer;
    private static DateOnly _day;
    private static long _lastTick = long.MinValue / 4;
    private static bool _dead;
    private static string? _error;

    /// <summary>历史目录（绝对路径；可能还不存在）。</summary>
    public static string Dir => _dir ?? DefaultDir();

    /// <summary>这次运行有没有真的在写（打不开目录 / 写坏过 = false）。</summary>
    public static bool Active
    {
        get { lock (_lock) return _writer is not null && !_dead; }
    }

    /// <summary>没在写的原因（在写时为 null）。给诊断口与日志用。</summary>
    public static string? Error
    {
        get { lock (_lock) return _error; }
    }

    /// <summary>
    /// 指定历史目录的根（`model.json` 所在目录）。**在 <see cref="AppState"/> 构造里调一次**。
    /// 调之前 <see cref="Dir"/> 退到 exe 旁边的 `history\`（纯桩模式 / 单测里就是这种）。
    /// </summary>
    public static void Init(string modelPath)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            if (string.IsNullOrEmpty(root)) return;
            lock (_lock) _dir = Path.Combine(root, "history");
        }
        catch
        {
            // 路径解析不出来就保持默认目录，别让"存历史"这件事把启动带崩
        }
    }

    /// <summary>
    /// 追加一拍。<paramref name="sample"/> 的 `T` 是 Unix 秒。
    ///
    /// **节流在这里**（不在调用方）：采样线程每秒都会调一次，落不落盘由
    /// <see cref="SampleIntervalSec"/> 决定。值没变也照写 —— 导出的 CSV 要的是连续时间轴，
    /// 跳过"没变化"的拍子会在文件里留下大段空洞，看图的人会以为那段时间程序没跑。
    /// </summary>
    public static void Append(HistorySample sample)
    {
        var tick = Environment.TickCount64;
        lock (_lock)
        {
            if (_dead) return;
            if (_writer is not null && tick - _lastTick < SampleIntervalSec * 1000L) return;

            try
            {
                EnsureWriterLocked();
                if (_writer is null) return;

                _writer.Write(HistoryCsv.Format(sample));
                _writer.Write('\n');
                _writer.Flush();
                _lastTick = tick;
            }
            catch (Exception e)
            {
                FailLocked("写历史采样失败：" + e.Message);
            }
        }
    }

    /// <summary>导出结果。`Rows == 0` 时 `Ok` 必为 false（不留一个只有表头的空文件给用户）。</summary>
    public readonly record struct ExportResult(
        bool Ok, string? Error, string File, int Rows, long Bytes, double From, double To);

    /// <summary>
    /// 把保留期内的采样导出成 CSV。
    ///
    /// <paramref name="path"/> 为空 ⇒ 落在历史目录下 `qcontrol-history-yyyyMMdd-HHmmss.csv`（接口调用的默认值）。
    /// <paramref name="from"/>/<paramref name="to"/> 是 Unix 秒的闭区间，null = 不限。
    /// </summary>
    public static ExportResult Export(string? path, double? from = null, double? to = null)
    {
        var dir = Dir;
        var files = ListFiles(dir);
        if (files.Count == 0)
            return new ExportResult(false, "还没有历史数据（程序启动后每 5 秒记一行，攒够再来）",
                                    "", 0, 0, 0, 0);

        var target = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(dir, "qcontrol-history-"
                                + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv")
            : path!.Trim();

        string full;
        try
        {
            full = Path.GetFullPath(target);
        }
        catch (Exception e)
        {
            return new ExportResult(false, "导出路径不合法：" + e.Message, target, 0, 0, 0, 0);
        }

        // 先写 .tmp 再改名：目标文件已存在时（用户在保存对话框里选了覆盖），
        // 中途失败也不会把原文件截成半截。
        var tmp = full + ".tmp";
        var rows = 0;
        var minT = double.MaxValue;
        var maxT = double.MinValue;

        try
        {
            if (Path.GetDirectoryName(full) is { Length: > 0 } parent) Directory.CreateDirectory(parent);

            using (var w = new StreamWriter(
                       new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None),
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                w.Write(HistoryCsv.Header);
                w.Write('\n');

                foreach (var file in files)
                {
                    // FileShare.ReadWrite|Delete：正在写的当天文件也能读（writer 以 FileShare.Read 打开）
                    using var r = new StreamReader(
                        new FileStream(file, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete),
                        Encoding.UTF8);

                    while (r.ReadLine() is { } line)
                    {
                        // 坏行（写一半被强杀留下的半截）直接跳过 —— 但**不跳过整份文件**
                        if (!HistoryCsv.TryParse(line, out var s)) continue;
                        if (from is { } lo && s.T < lo) continue;
                        if (to is { } hi && s.T > hi) continue;

                        w.Write(HistoryCsv.Format(s));
                        w.Write('\n');
                        rows++;
                        if (s.T < minT) minT = s.T;
                        if (s.T > maxT) maxT = s.T;
                    }
                }

                w.Flush();
            }

            if (rows == 0)
            {
                TryDelete(tmp);
                return new ExportResult(false,
                    from is not null || to is not null ? "这个时间段里没有历史数据" : "没有可导出的历史数据",
                    full, 0, 0, 0, 0);
            }

            File.Move(tmp, full, overwrite: true);
            long bytes;
            try { bytes = new FileInfo(full).Length; } catch { bytes = 0; }

            Console.WriteLine($"[history] 已导出 {rows} 行 → {full}");
            return new ExportResult(true, null, full, rows, bytes, minT, maxT);
        }
        catch (Exception e)
        {
            TryDelete(tmp);
            return new ExportResult(false, "导出失败：" + e.Message, full, 0, 0, 0, 0);
        }
    }

    /// <summary>历史档现状（文件数 / 总字节 / 最早与最新日期）。目录不在就全 0。</summary>
    public static (int Files, long Bytes, DateOnly? Oldest, DateOnly? Newest) Stats()
    {
        var files = ListFiles(Dir);
        if (files.Count == 0) return (0, 0, null, null);

        var bytes = 0L;
        foreach (var f in files)
        {
            try { bytes += new FileInfo(f).Length; } catch { /* 被占用/刚删：忽略这一份 */ }
        }

        HistoryCsv.TryFileDay(files[0], out var oldest);
        HistoryCsv.TryFileDay(files[^1], out var newest);
        return (files.Count, bytes, oldest, newest);
    }

    /// <summary>收掉写句柄（退出路径调；再 <see cref="Append"/> 会自己重开）。</summary>
    public static void Close()
    {
        lock (_lock)
        {
            CloseWriterLocked();
        }
    }

    // ================================================================ 内部

    private static void CloseWriterLocked()
    {
        if (_writer is null) return;
        try { _writer.Dispose(); } catch { /* 退出路径不抛 */ }
        _writer = null;
    }

    private static void FailLocked(string why)
    {
        _dead = true;
        _error = why;
        CloseWriterLocked();
        Console.Error.WriteLine("[history] " + why + "（本次运行不再写历史档）");
    }

    /// <summary>跨天换文件（同时清旧档）。首次写入时也走这里。</summary>
    private static void EnsureWriterLocked()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && today == _day) return;

        CloseWriterLocked();

        var dir = Dir;
        Directory.CreateDirectory(dir);
        PurgeLocked(dir, today);

        var path = Path.Combine(dir, HistoryCsv.FileName(today));
        var fresh = true;
        try { fresh = !File.Exists(path) || new FileInfo(path).Length == 0; } catch { /* 判不出来就当新文件 */ }

        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _day = today;

        if (fresh)
        {
            // 表头只在空文件上写一次：追加重启时再写一行表头，Excel 会把中间那行当数据
            _writer.Write(HistoryCsv.Header);
            _writer.Write('\n');
            _writer.Flush();
        }
    }

    /// <summary>删掉 <see cref="KeepDays"/> 天之前的档（只认 `hist-yyyy-MM-dd.csv`）。</summary>
    private static void PurgeLocked(string dir, DateOnly today)
    {
        var oldest = today.AddDays(-(KeepDays - 1));
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, HistoryCsv.FilePrefix + "*.csv"))
            {
                if (!HistoryCsv.TryFileDay(f, out var day) || day >= oldest) continue;
                try { File.Delete(f); }
                catch { /* 被别的进程占着：下次开机再删，留着不影响正确性 */ }
            }
        }
        catch
        {
            // 目录读不了（权限/盘掉线）：不清理就是了，别把写历史这件事也带崩
        }
    }

    /// <summary>按日期升序的历史档（读不出来就返回空表）。</summary>
    private static List<string> ListFiles(string dir)
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, HistoryCsv.FilePrefix + "*.csv"))
            {
                if (HistoryCsv.TryFileDay(f, out _)) list.Add(f);
            }
        }
        catch
        {
            return list;
        }

        // 文件名里是 yyyy-MM-dd，字典序即时间序（比逐个解析日期再排更省事）
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就留着，反正叫 .tmp */ }
    }

    private static string DefaultDir()
    {
        try
        {
            if (Http.Paths.FindModel(null) is { } model && Path.GetDirectoryName(model) is { } root)
                return Path.Combine(root, "history");
        }
        catch { /* 见 Init：找目录失败不该抛 */ }
        return Path.Combine(AppContext.BaseDirectory, "history");
    }
}
