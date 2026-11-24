using System.Globalization;

namespace QControl.Core.Text;

/// <summary>
/// 面向用户的一行式格式化：字节数、速率、时长、URL。**纯函数，无 WPF 依赖**。
///
/// 为什么放在 Core：这些规则原先散在 <c>AboutView.xaml.cs</c> 的 private static 里，
/// 界面项目（net10.0-windows）进不了测试工程（net10.0）⇒ 一条都没被覆盖过。
/// 抽出来既让规则可测，也避免「关于页」与「系统页」各自实现对不上（历史上
/// `SysView` 的网络速率和这里就是两套写法）。
///
/// ⚠️ **这几个函数的口径是「用户能看懂」，不是「精确」**：
///   · 字节数 0 / 负数一律视为「没有」，返回 null 或 "—"，不显示 "0 B"；
///   · 已运行时长只给到「天/小时/分」一档 —— 精确到秒对用户没有意义；
///   · 一律 <see cref="CultureInfo.InvariantCulture"/>，避免某些区域设置把小数点写成逗号。
///
/// ⚠️ 两条字节口径**故意不同**，不要合并：
///   · <see cref="TryBytes"/> 用于「有就显示、没有就整块隐藏」（可返 null）；
///   · <see cref="BytesOrDash"/> 用于下载进度这种**必须有宽度**的位置（返 "—"）。
///     前者小于 1 KB 给 "512 B"，后者一律折算成 KB（"0 KB" / "512 KB"）。
/// </summary>
public static class Humanize
{
    /// <summary>B / KB / MB。0 或负数按「没有」处理，返回 null。</summary>
    public static string? TryBytes(long? bytes)
    {
        if (bytes is not { } v || v <= 0) return null;
        if (v < 1024) return v + " B";
        if (v < 1024 * 1024) return (v / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
        return (v / 1024.0 / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>KB / MB，缺数据给 "—"。用于进度条两侧（那里不能一片空白）。</summary>
    public static string BytesOrDash(long? bytes)
    {
        if (bytes is not long b || b <= 0) return "—";
        return b >= 1048576
            ? (b / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB"
            : (b / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
    }

    /// <summary>下载速率：KB/s 取整（至少 1，否则慢速时会显示 "0 KB/s" 而进度还在动），≥1 MB 给一位小数。</summary>
    public static string Rate(long? bytesPerSec)
    {
        if (bytesPerSec is not long s || s <= 0) return "0 KB/s";
        return s >= 1048576
            ? (s / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB/s"
            : Math.Max(1, s / 1024) + " KB/s";
    }

    /// <summary>已运行时长：只给「天/小时/分」这一档粒度（精确到秒对用户没意义）。</summary>
    public static string Uptime(long sec)
    {
        var s = Math.Max(0, sec);
        var d = s / 86400;
        var h = (s % 86400) / 3600;
        var m = (s % 3600) / 60;
        if (d > 0) return $"{d} 天 {h} 小时";
        if (h > 0) return $"{h} 小时 {m} 分";
        if (m > 0) return $"{m} 分";
        return $"{s} 秒";
    }

    /// <summary>区间计时：mm:ss / h:mm:ss。调用方保证 sec ≥ 0（负数没有语义，不在这里编造）。</summary>
    public static string Clock(int sec)
    {
        var h = sec / 3600;
        var m = (sec % 3600) / 60;
        var s = sec % 60;
        return h > 0
            ? $"{h}:{m:00}:{s:00}"
            : $"{m:00}:{s:00}";
    }

    /// <summary>https://github.com/x/y → github.com/x/y（吐司里不摆协议和末尾斜杠）。解析不了就原样返回。</summary>
    public static string ShortUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? u.Host + u.AbsolutePath.TrimEnd('/')
            : url;

    /// <summary>`2026-10-20T12:00:00Z` → `2026-10-20`（日期够用，精确到秒没意义）。短于 10 个字符视为没有。</summary>
    public static string? Day(string? iso)
        => iso is { Length: >= 10 } ? iso[..10] : null;

    /// <summary>
    /// 把多行文本压成一行：换行 / 制表符折成单个空格，超长截断加省略号（卡片不能被撑高）。
    ///
    /// ⚠️ **只折换行与制表符，不合并连续空格**。原来的注释写的是「多空格折成单个空格」，
    ///    但实现对空格原样保留 —— 这里照实现钉住（发布说明里若有对齐用的空格，压掉会改变文案）。
    /// </summary>
    public static string OneLine(string text, int max)
    {
        var s = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
