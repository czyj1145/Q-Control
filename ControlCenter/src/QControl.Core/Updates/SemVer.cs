namespace QControl.Core.Updates;

/// <summary>
/// 三段式版本号（major.minor.patch）＋可选的预发布后缀 —— 只做「谁更新」这一件事。
///
/// 为什么不用 <see cref="System.Version"/>：
///   · 它不认 `v` 前缀（GitHub/Gitee 的 tag 几乎都带 v）；
///   · 它不认 `-beta.1` 这种后缀，而 `1.0.0-beta` 和 `1.0.0` 的比较恰恰是
///     "别把测试版当成正式版推给用户"的关键；
///   · 它按段数比较（2 段 vs 3 段会直接抛）。
///
/// 规则按 SemVer 2.0 的核心部分实现（构建元数据 `+abc1234` 忽略 —— 那是源码链接，
/// 不是版本语义）：
///   · 缺段补 0（`0.2` == `0.2.0`）；
///   · 有预发布后缀的**小于**同号正式版（`0.2.0-beta < 0.2.0`）；
///   · 预发布逐段比：数字段按数值、字母段按序号、数字段小于字母段、前缀短的更小
///     （`0.2.0-beta < 0.2.0-beta.2`、`0.2.0-beta.2 < 0.2.0-rc.1`）。
///
/// ⚠️ 解析失败一律返回 false，**绝不猜**（比如把 "nightly" 当成 0.0.0 就可能误判"有新版本"，
///    然后把人引到一个空发布页）。调用方拿到 false 就当作"这个源的数据不可信"。
/// </summary>
public readonly record struct SemVer : IComparable<SemVer>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>预发布后缀（不含 `-`）。null = 正式版。</summary>
    public string? Pre { get; }

    private SemVer(int major, int minor, int patch, string? pre)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Pre = pre;
    }

    public bool IsPrerelease => Pre is not null;

    /// <summary>解析 `0.2.0` / `v0.2.0` / `0.2` / `1.0.0-beta.2` / `0.2.0+abcdef`。</summary>
    public static bool TryParse(string? text, out SemVer value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();

        // 前导 v/V：GitHub 的 tag 是 v0.2.0，VERSION 文件里是 0.1.0，两种都要吃下
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        // 构建元数据（+ 之后）不参与比较
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (!ValidPre(pre)) return false;
        }

        if (s.Length == 0) return false;
        var parts = s.Split('.');
        if (parts.Length is 0 or > 3) return false;

        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out var n) || n < 0) return false;
            nums[i] = n;
        }

        value = new SemVer(nums[0], nums[1], nums[2], pre);
        return true;
    }

    /// <summary>预发布后缀的合法性：点分段、每段非空、只含字母数字与连字符。</summary>
    private static bool ValidPre(string pre)
    {
        if (pre.Length == 0) return false;
        foreach (var seg in pre.Split('.'))
        {
            if (seg.Length == 0) return false;
            foreach (var c in seg)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
            }
        }
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // 同号：正式版 > 任何预发布
        if (Pre is null && other.Pre is null) return 0;
        if (Pre is null) return 1;
        if (other.Pre is null) return -1;

        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = long.TryParse(a[i], out var av);
            var bn = long.TryParse(b[i], out var bv);
            int seg;
            if (an && bn) seg = av.CompareTo(bv);
            else if (an) seg = -1;              // 数字段 < 字母段（SemVer 规定）
            else if (bn) seg = 1;
            else seg = string.CompareOrdinal(a[i], b[i]);
            if (seg != 0) return seg;
        }
        return a.Length.CompareTo(b.Length);    // 前缀短的更小：beta < beta.1
    }

    public bool IsNewerThan(SemVer other) => CompareTo(other) > 0;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public override string ToString()
        => $"{Major}.{Minor}.{Patch}" + (Pre is null ? "" : "-" + Pre);
}
