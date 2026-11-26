using QControl.Core.Text;

namespace QControl.Tests;

/// <summary>
/// <see cref="Humanize"/> 的边界 —— 这几个函数原先埋在 <c>AboutView.xaml.cs</c> 里，
/// 界面项目进不了测试工程，一条都没被验过。这里把**现有行为**钉住（不是重新设计）：
/// 改动若让这些断言变红，说明用户在界面上看到的字会跟着变。
/// </summary>
public class HumanizeTests
{
    // ---- TryBytes：「有就显示、没有就整块隐藏」的口径 ----

    [Fact]
    public void TryBytes_没有读数返回null_而不是零字节()
    {
        Assert.Null(Humanize.TryBytes(null));
        Assert.Null(Humanize.TryBytes(0));
        Assert.Null(Humanize.TryBytes(-1));
    }

    [Theory]
    [InlineData(1, "1 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    public void TryBytes_一KB以下按字节(long v, string want)
        => Assert.Equal(want, Humanize.TryBytes(v));

    [Theory]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048575, "1024.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(2621440, "2.5 MB")]
    public void TryBytes_一KB以上折算(long v, string want)
        => Assert.Equal(want, Humanize.TryBytes(v));

    // ---- BytesOrDash：进度条两侧，不能空着 ----

    [Fact]
    public void BytesOrDash_缺数据给长破折号()
    {
        Assert.Equal("—", Humanize.BytesOrDash(null));
        Assert.Equal("—", Humanize.BytesOrDash(0));
        Assert.Equal("—", Humanize.BytesOrDash(-5));
    }

    [Theory]
    [InlineData(500, "0 KB")]      // 原实现就这样：不足 1KB 也折成整数 KB
    [InlineData(2048, "2 KB")]
    [InlineData(1048576, "1.0 MB")]
    [InlineData(1177176, "1.1 MB")] // 线上 setup 包的实际体积
    public void BytesOrDash_小于一MB给整数KB(long v, string want)
        => Assert.Equal(want, Humanize.BytesOrDash(v));

    // ---- Rate：慢速时至少显示 1 KB/s，否则进度在动而数字是 0 ----

    [Fact]
    public void Rate_没有速度给零()
    {
        Assert.Equal("0 KB/s", Humanize.Rate(null));
        Assert.Equal("0 KB/s", Humanize.Rate(0));
    }

    [Theory]
    [InlineData(1, "1 KB/s")]        // 不足 1KB/s 抬到 1
    [InlineData(1024, "1 KB/s")]
    [InlineData(51200, "50 KB/s")]
    [InlineData(1048576, "1.0 MB/s")]
    [InlineData(1572864, "1.5 MB/s")]
    public void Rate_按档位折算(long v, string want)
        => Assert.Equal(want, Humanize.Rate(v));

    // ---- Uptime：只给天/小时/分 ----

    [Theory]
    [InlineData(0, "0 秒")]
    [InlineData(9, "9 秒")]
    [InlineData(59, "59 秒")]
    [InlineData(60, "1 分")]
    [InlineData(3599, "59 分")]
    [InlineData(3600, "1 小时 0 分")]
    [InlineData(7200, "2 小时 0 分")]
    [InlineData(86399, "23 小时 59 分")]
    [InlineData(86400, "1 天 0 小时")]
    [InlineData(90000, "1 天 1 小时")]   // 尾数 1 小时 0 分 → 天档不带分
    public void Uptime_按档位取粒度(long sec, string want)
        => Assert.Equal(want, Humanize.Uptime(sec));

    [Fact]
    public void Uptime_负数按零处理()
        => Assert.Equal("0 秒", Humanize.Uptime(-30));

    // ---- Clock：记录计时 mm:ss / h:mm:ss ----

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(9, "00:09")]
    [InlineData(61, "01:01")]
    [InlineData(599, "09:59")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3661, "1:01:01")]
    [InlineData(86399, "23:59:59")]
    public void Clock_分秒两位_有小时才带小时(int sec, string want)
        => Assert.Equal(want, Humanize.Clock(sec));

    // ---- ShortUrl：吐司里不摆协议和末尾斜杠 ----

    [Fact]
    public void ShortUrl_去掉协议与末尾斜杠()
    {
        Assert.Equal("github.com/czyj1145/Q-Control",
            Humanize.ShortUrl("https://github.com/czyj1145/Q-Control"));
        Assert.Equal("github.com/czyj1145/Q-Control",
            Humanize.ShortUrl("https://github.com/czyj1145/Q-Control/"));
    }

    [Fact]
    public void ShortUrl_解析不了原样返回()
    {
        Assert.Equal("", Humanize.ShortUrl(""));
        Assert.Equal("不是地址", Humanize.ShortUrl("不是地址"));
    }

    // ---- Day：发布日期只要日期 ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("2026-1", null)]                 // 短于 10 位当没有
    [InlineData("2026-10-20", "2026-10-20")]
    [InlineData("2026-10-20T12:00:00Z", "2026-10-20")]
    [InlineData("2026-11-24T05:56:33Z", "2026-11-24")]   // 线上 release 的 published_at
    public void Day_取前十位_不足当没有(string? iso, string? want)
        => Assert.Equal(want, Humanize.Day(iso));

    // ---- OneLine：发布说明压成一行 ----

    [Fact]
    public void OneLine_换行与制表符折成空格()
    {
        Assert.Equal("a b", Humanize.OneLine("a\r\nb", 48));
        Assert.Equal("a b", Humanize.OneLine("a\tb", 48));
        Assert.Equal("a b", Humanize.OneLine("a\n\n\nb", 48));
    }

    [Fact]
    public void OneLine_连续空格原样保留_只去掉首尾()
    {
        // ⚠️ 实现如此（老注释写"多空格折成一个"，与实际不符）：压掉会改文案
        Assert.Equal("a  b", Humanize.OneLine("  a  b  ", 48));
    }

    [Fact]
    public void OneLine_超长截断加省略号_刚好等于上限不截()
    {
        Assert.Equal("abcde…", Humanize.OneLine("abcdefg", 5));
        Assert.Equal("abcde", Humanize.OneLine("abcde", 5));
    }

    [Fact]
    public void OneLine_空串还是空串()
        => Assert.Equal("", Humanize.OneLine("   \r\n  ", 48));

    // ---- RateInteger：系统页网速，整数不带小数点 ----

    [Fact]
    public void RateInteger_没有速度给零()
    {
        Assert.Equal("0 KB/s", Humanize.RateInteger(null));
        Assert.Equal("0 KB/s", Humanize.RateInteger(0));
        Assert.Equal("0 KB/s", Humanize.RateInteger(-5));
    }

    [Theory]
    [InlineData(1, "0 KB/s")]          // 不足 512B/s 显示 0（空闲就是没流量，不抬成 1）
    [InlineData(1024, "1 KB/s")]
    [InlineData(51200, "50 KB/s")]
    [InlineData(1048575, "1024 KB/s")] // 严格小于 1MB 仍是 KB
    [InlineData(1048576, "1 MB/s")]
    [InlineData(1572864, "2 MB/s")]    // 1.5 远离零取整
    [InlineData(2097152, "2 MB/s")]
    public void RateInteger_按档位折算且不带小数点(double v, string want)
    {
        var s = Humanize.RateInteger(v);
        Assert.Equal(want, s);
        Assert.DoesNotContain(".", s);
    }

    // ---- TrafficMb：累计流量 MB → GB / TB ----

    [Fact]
    public void TrafficMb_缺数据返回null()
        => Assert.Null(Humanize.TrafficMb(null));

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(512.4, "512 MB")]
    [InlineData(1023.9, "1024 MB")]
    [InlineData(1024, "1.0 GB")]
    [InlineData(1536, "1.5 GB")]
    [InlineData(1048576, "1.00 TB")]
    [InlineData(1572864, "1.50 TB")]
    public void TrafficMb_按档位折算(double mb, string want)
        => Assert.Equal(want, Humanize.TrafficMb(mb));

    // ---- UptimeCompact：系统页时长，没有余数的那一级不带 0 ----

    [Fact]
    public void UptimeCompact_缺数据返回null()
        => Assert.Null(Humanize.UptimeCompact(null));

    [Theory]
    [InlineData(0, "0 分钟")]
    [InlineData(59, "0 分钟")]
    [InlineData(60, "1 分钟")]
    [InlineData(3599, "59 分钟")]
    [InlineData(3600, "1 小时")]
    [InlineData(3660, "1 小时 1 分钟")]
    [InlineData(86399, "23 小时 59 分钟")]
    [InlineData(86400, "1 天")]
    [InlineData(90000, "1 天 1 小时")]
    [InlineData(172800, "2 天")]
    public void UptimeCompact_只在有余数时带后一级(long sec, string want)
        => Assert.Equal(want, Humanize.UptimeCompact(sec));

    [Fact]
    public void UptimeCompact_负数按零()
        => Assert.Equal("0 分钟", Humanize.UptimeCompact(-100));
}
