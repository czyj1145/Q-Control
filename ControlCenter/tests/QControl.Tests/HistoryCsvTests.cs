using System.Globalization;
using QControl.Core.History;

namespace QControl.Tests;

/// <summary>
/// 历史采样 CSV 的编解码 —— 落盘与导出的**唯一格式真源**。
///
/// 这些用例防的是"导出的文件看起来对、其实读不回来"：
///   · 列序/表头两边不一致 ⇒ 用户拿 Excel 分析时列错位，没人会怀疑是程序的问题；
///   · 空值被写成 0 ⇒ 曲线凭空多一段 0 度/0 转，看图的人会以为当时真这么凉；
///   · 坏行被"猜"成有效值 ⇒ 事后无法区分"当时读不到"和"文件损坏"。
/// 精度上只钉到 CSV 里写出来的位数（`t` 到毫秒、功耗/温度两位小数），不追求 double 逐位相等。
/// </summary>
public sealed class HistoryCsvTests
{
    private static readonly HistorySample Sample =
        new(T: 1761920112.003, CpuT: 63, GpuT: 53, CpuP: 12.3, GpuP: 8.1, Fan: 3024, Fan2: 2218);

    [Fact]
    public void 表头有八列且列序固定()
    {
        var cols = HistoryCsv.Header.Split(',');
        Assert.Equal(HistoryCsv.Columns, cols.Length);
        Assert.Equal(["time", "t", "cpu_t", "gpu_t", "cpu_p", "gpu_p", "fan", "fan2"], cols);
    }

    [Fact]
    public void 写一行_列序与数值格式固定()
    {
        var cols = HistoryCsv.Format(Sample).Split(',');
        Assert.Equal(HistoryCsv.Columns, cols.Length);

        // time 是本地时间（时区随机器变，只校验形状）
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", cols[0]);
        Assert.Equal("1761920112.003", cols[1]);
        Assert.Equal("63", cols[2]);
        Assert.Equal("53", cols[3]);
        Assert.Equal("12.3", cols[4]);
        Assert.Equal("8.1", cols[5]);
        Assert.Equal("3024", cols[6]);
        Assert.Equal("2218", cols[7]);
    }

    [Fact]
    public void 读不到的读数写空字段_不是零也不是null()
    {
        var line = HistoryCsv.Format(new HistorySample(1761920112, null, null, null, null, null, null));
        var cols = line.Split(',');
        Assert.Equal(HistoryCsv.Columns, cols.Length);
        for (var i = 2; i < cols.Length; i++) Assert.Equal("", cols[i]);
        Assert.DoesNotContain("null", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NaN", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 写出去再读回来_字段不丢()
    {
        Assert.True(HistoryCsv.TryParse(HistoryCsv.Format(Sample), out var back));
        Assert.Equal(Sample.T, back.T, 3);          // epoch 精确到毫秒
        Assert.Equal(Sample.CpuT, back.CpuT);
        Assert.Equal(Sample.GpuT, back.GpuT);
        Assert.Equal(Sample.CpuP, back.CpuP);
        Assert.Equal(Sample.GpuP, back.GpuP);
        Assert.Equal(Sample.Fan, back.Fan);
        Assert.Equal(Sample.Fan2, back.Fan2);
    }

    [Fact]
    public void 读空字段_得到null()
    {        var line = "2026-10-31 22:05:12,1761920112.000,,,,,,";
        Assert.True(HistoryCsv.TryParse(line, out var s));
        Assert.Equal(1761920112, s.T);
        Assert.Null(s.CpuT);
        Assert.Null(s.GpuT);
        Assert.Null(s.CpuP);
        Assert.Null(s.GpuP);
        Assert.Null(s.Fan);
        Assert.Null(s.Fan2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2026-10-31 22:05:12,1761920112")]                 // 列数不够
    [InlineData("2026-10-31 22:05:12,not-a-number,,,,,,")]         // t 不是数
    [InlineData("2026-10-31 22:05:12,1761920112,,,abc,,,")]        // 读数不是数
    [InlineData(HistoryCsv.Header)]                                 // 表头本身不是数据
    public void 坏行一律丢掉_不猜(string? line)
    {
        Assert.False(HistoryCsv.TryParse(line, out _));
    }

    [Fact]
    public void 文件名带本地日期_可往返()
    {
        var day = new DateOnly(2026, 10, 31);
        var name = HistoryCsv.FileName(day);
        Assert.Equal("hist-2026-10-31.csv", name);
        Assert.True(HistoryCsv.TryFileDay(name, out var back));
        Assert.Equal(day, back);

        // 带目录的完整路径也认（清理旧档时拿到的是全路径）
        Assert.True(HistoryCsv.TryFileDay(@"D:\ProgramData\Q-Control\history\hist-2026-10-31.csv", out var fromPath));
        Assert.Equal(day, fromPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cc-2026-10-31.log")]        // 日志文件绝不能当成历史档删掉
    [InlineData("hist-2026-13-01.csv")]      // 月份非法
    [InlineData("hist-2026-10-31.csv.tmp")]  // 导出用的临时文件
    [InlineData("hist-20261031.csv")]
    public void 不是历史档的文件名_一个都不认(string? name)
    {
        Assert.False(HistoryCsv.TryFileDay(name, out _));
    }

    [Fact]
    public void 本地时间_format与界面同款()
    {
        var text = HistoryCsv.LocalTime(1761920112);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1761920112).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            text);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(1e18)]      // 超出 DateTime 范围：如实给空，不猜
    public void 本地时间_越界给空串(double t)
    {
        Assert.Equal("", HistoryCsv.LocalTime(t));
    }

    /// <summary>
    /// 残名（`hist-.csv`）必须**返回 false**，不能抛。
    ///
    /// `StartsWith("hist-") &amp;&amp; EndsWith(".csv")` 都成立，但 `name[5..^4]` 会变成
    /// `name[5..4]`（start &gt; end）⇒ `ArgumentOutOfRangeException`。
    /// 而 `HistoryStore.ListFiles` 把整段包在 catch 里 ⇒ 用户看到"历史记录一条都没有"，
    /// 实际文件都在（写文件被打断 / 手工改名 / 同步盘冲突重命名都会留下这种名字）。
    /// </summary>
    [Theory]
    [InlineData("hist-.csv")]
    [InlineData(@"C:\logs\hist-.csv")]
    public void 残名不抛异常且不认(string name)
    {
        Assert.False(HistoryCsv.TryFileDay(name, out var day));
        Assert.Equal(default, day);
    }
}
