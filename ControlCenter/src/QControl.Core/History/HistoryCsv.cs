using System.Globalization;
using System.Text;

namespace QControl.Core.History;

/// <summary>
/// 一条历史采样，字段与 `/api/history` 的 `points` 项一一对应（`t` = Unix 秒）。
///
/// 这里刻意**不复用** JSON DTO：DTO 是契约生成物（`spike/_gen_dtos.py`，字段全是可空 long/double），
/// 而落盘要的是「能一行写完、能原样读回」的最小结构。两者形状相同，但改契约不该顺带改存档格式。
/// </summary>
public readonly record struct HistorySample(
    double T,
    double? CpuT,
    double? GpuT,
    double? CpuP,
    double? GpuP,
    double? Fan,
    double? Fan2);

/// <summary>
/// 历史采样的 CSV 编解码 —— **纯函数，不碰文件系统**（落盘与导出在 App 层的 `HistoryStore`）。
///
/// 为什么单独放 Core：这段逻辑（列序、空值、四舍五入、文件名里的日期）是"导出的 CSV
/// 长什么样"的唯一真源，必须能被单测逐字钉住；App 层是 net10.0-windows + WPF，
/// 测试工程引不了它（见 `tests/QControl.Tests` 只引用 Core）。
///
/// 格式（首行表头，一行一条，`,` 分隔，UTF-8 无 BOM，小数点一律 `.`）：
/// <code>
/// time,t,cpu_t,gpu_t,cpu_p,gpu_p,fan,fan2
/// 2026-10-31 22:05:12,1761920112.003,63,53,12.3,8.1,3024,2218
/// </code>
/// 三处约定：
///   · `time` 是**本地时间**（给人看，Excel 里能直接排序），`t` 是 **Unix 秒**（给程序对时）。
///     两个都写：只留本地时间的话，跨时区/夏令时就没法还原；只留 epoch 的话，没人愿意看。
///   · **取不到的读数写空字段**，不写 `null` / `NaN` / `0` —— 空是"这一拍没有这个数"，
///     0 是"读到了 0"，两者在散热曲线上是相反的结论。
///   · 文件按**本地日期**切：`hist-yyyy-MM-dd.csv`，一天一个文件，删旧档只按文件名判，不读内容。
/// </summary>
public static class HistoryCsv
{
    /// <summary>表头（列序即真源）。</summary>
    public const string Header = "time,t,cpu_t,gpu_t,cpu_p,gpu_p,fan,fan2";

    /// <summary>一行必须的列数（含 `time`）。</summary>
    public const int Columns = 8;

    /// <summary>历史档文件名前缀。</summary>
    public const string FilePrefix = "hist-";

    private const string FileSuffix = ".csv";
    private const string DayFormat = "yyyy-MM-dd";
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>拼一行（不含换行）。</summary>
    public static string Format(HistorySample s)
    {
        var sb = new StringBuilder(80);
        sb.Append(LocalTime(s.T)).Append(',');
        Num(sb, s.T, "0.000").Append(',');
        Num(sb, s.CpuT, "0.##").Append(',');
        Num(sb, s.GpuT, "0.##").Append(',');
        Num(sb, s.CpuP, "0.##").Append(',');
        Num(sb, s.GpuP, "0.##").Append(',');
        Num(sb, s.Fan, "0").Append(',');
        Num(sb, s.Fan2, "0");
        return sb.ToString();
    }

    /// <summary>
    /// 解析一行。**严格**：列数不够、`t` 不是数、某个非空字段不是数 ⇒ false（整行丢弃）。
    ///
    /// 为什么不"坏字段当空值"：那样一张图里会凭空多出一段直线（读数被当成"没有"），
    /// 事后没人能分清是"当时真读不到"还是"文件写坏了"。宁可少一行，也不改数据的含义。
    /// </summary>
    public static bool TryParse(string? line, out HistorySample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var parts = line.Split(',');
        if (parts.Length < Columns) return false;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            return false;

        if (!TryField(parts[2], out var cpuT)
            || !TryField(parts[3], out var gpuT)
            || !TryField(parts[4], out var cpuP)
            || !TryField(parts[5], out var gpuP)
            || !TryField(parts[6], out var fan)
            || !TryField(parts[7], out var fan2))
            return false;

        sample = new HistorySample(t, cpuT, gpuT, cpuP, gpuP, fan, fan2);
        return true;
    }

    /// <summary>Unix 秒 → `yyyy-MM-dd HH:mm:ss`（本地时区）。越界/NaN 给空串。</summary>
    public static string LocalTime(double t)
    {
        if (double.IsNaN(t) || double.IsInfinity(t)) return "";
        var ms = (long)Math.Round(t * 1000);
        // DateTimeOffset 的合法范围（0001-01-01 ~ 9999-12-31）：超出就如实给空，
        // 不去猜"用户大概想说什么时间"。
        if (ms < 0 || ms > 253402300799999) return "";
        return DateTimeOffset.FromUnixTimeMilliseconds(ms)
            .ToLocalTime()
            .ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>`hist-2026-10-31.csv`。</summary>
    public static string FileName(DateOnly day)
        => FilePrefix + day.ToString(DayFormat, CultureInfo.InvariantCulture) + FileSuffix;

    /// <summary>从文件名取出日期（只认 `hist-yyyy-MM-dd.csv`，别的返回 false）。</summary>
    public static bool TryFileDay(string? fileName, out DateOnly day)
    {
        day = default;
        if (string.IsNullOrEmpty(fileName)) return false;

        var name = Path.GetFileName(fileName);
        if (!name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(FileSuffix, StringComparison.Ordinal))
            return false;

        // ⚠️ `hist-.csv`（写文件被打断的残名 / 手工改名 / 同步盘冲突重命名）会让
        //    `name[5..^4]` 变成 `name[5..4]`（start > end）直接抛 ArgumentOutOfRangeException，
        //    而调用方把整个列表包在 catch 里 ⇒ 用户看到"历史一条都没有"。
        if (name.Length <= FilePrefix.Length + FileSuffix.Length) return false;

        var core = name[FilePrefix.Length..^FileSuffix.Length];
        return DateOnly.TryParseExact(core, DayFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out day);
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>空值 → 空字段；有值 → 指定格式（InvariantCulture，小数点恒为 `.`）。</summary>
    private static StringBuilder Num(StringBuilder sb, double? v, string format)
    {
        if (v is not { } d || double.IsNaN(d) || double.IsInfinity(d)) return sb;
        return sb.Append(d.ToString(format, CultureInfo.InvariantCulture));
    }

    private static bool TryField(string s, out double? value)
    {
        value = null;
        if (s.Length == 0) return true;              // 空 = 这一拍没有这个读数
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return false;
        value = d;
        return true;
    }
}
