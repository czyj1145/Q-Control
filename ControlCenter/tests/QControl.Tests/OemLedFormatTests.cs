using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 原厂 LED 注册表值的编解码（<c>OemLedFormat</c>）。
///
/// 这些断言护的是一个很贵的事故：**段顺序错一位**，原厂 daemon 下次启动就会把
/// 键盘染成 Logo 的颜色（或者把那条灯带留成旧红），而且只在那时候才看得见。
/// 5 段 = LOGO / TRUNK / KB1..3，每段 <c>{dt3d:X2}{RRGGBB}</c> —— 反编译
/// <c>MY_DATA_LED_GROUP.str_format</c> 与本机实测双向对齐过。
/// </summary>
public class OemLedFormatTests
{
    private static BacklightProfile Profile(int level, int effect, params (int Addr, int[] Rgb)[] zones)
        => new(level, effect, zones);

    [Fact]
    public void Build_把键盘三段写进段3到段5_整条灯带跟主色()
    {
        var p = Profile(2, 1, (5, [0, 255, 255]), (4, [0, 255, 255]), (3, [0, 255, 255]));
        var s = OemLedFormat.Build("12ff0000;12ff0000;12ff0000;12ff0000;12ff0000", p);

        Assert.Equal("12ff0000;1200FFFF;1200FFFF;1200FFFF;1200FFFF", s);
    }

    [Fact]
    public void Build_分区异色时按地址降序对应段3到段5()
    {
        var p = Profile(2, 1, (3, [255, 0, 0]), (5, [0, 255, 0]), (4, [0, 0, 255]));
        var s = OemLedFormat.Build(null, p).Split(';');

        Assert.Equal("1200FF00", s[2]);   // addr 5 = 默认 A 区
        Assert.Equal("120000FF", s[3]);   // addr 4
        Assert.Equal("12FF0000", s[4]);   // addr 3
    }

    [Fact]
    public void Build_单区机型三段重复同一个颜色()
    {
        var s = OemLedFormat.Build(null, Profile(1, 1, (6, [255, 255, 255]))).Split(';');

        Assert.Equal(5, s.Length);
        Assert.All(s[1..], seg => Assert.Equal("11FFFFFF", seg));   // TRUNK 也跟主色
        Assert.Equal("1100FFFF", s[0]);                             // LOGO 段保留兜底白
    }

    [Fact]
    public void Build_没有Logo色时保留原厂Logo段_但有Logo色就覆盖()
    {
        var noLogo = OemLedFormat.Build("12ff0000;12ff0000;12ff0000;12ff0000;12ff0000",
            Profile(2, 1, (5, [0, 255, 255]), (4, [0, 255, 255]), (3, [0, 255, 255])));
        Assert.Equal("12ff0000", noLogo.Split(';')[0]);

        var withLogo = Profile(2, 1, (5, [0, 255, 255]), (4, [0, 255, 255]), (3, [0, 255, 255])) with
        {
            Logo = [255, 128, 0],
        };
        Assert.Equal("12FF8000", OemLedFormat.Build(null, withLogo).Split(';')[0]);
    }

    [Fact]
    public void Build_值比五段短或为空时补原厂默认白()
    {
        var s = OemLedFormat.Build("1100FFFF",
            Profile(2, 1, (5, [0, 255, 255]), (4, [0, 255, 255]), (3, [0, 255, 255]))).Split(';');

        Assert.Equal(5, s.Length);
        Assert.Equal("1100FFFF", s[0]);
    }

    [Fact]
    public void Parse_读回本机那串青色的狂暴档()
    {
        var p = OemLedFormat.Parse("12ff0000;12ff0000;1200FFFF;1200FFFF;1200FFFF", [5, 4, 3]);

        Assert.NotNull(p);
        Assert.Equal(2, p!.Level);          // 0x12 的低 4 位
        Assert.Equal(1, p.Effect);          // 0x12 的高 4 位
        Assert.Equal([5, 4, 3], p.Zones.Select(z => z.Addr).ToArray());
        Assert.All(p.Zones, z => Assert.Equal([0, 255, 255], z.Rgb));
    }

    [Fact]
    public void Parse_段为0的跳过_一条都解不出就返回null()
    {
        Assert.Null(OemLedFormat.Parse("12ff0000;12ff0000;00000000;00000000;00000000", [5, 4, 3]));
        Assert.Null(OemLedFormat.Parse("NULL", [5, 4, 3]));
        Assert.Null(OemLedFormat.Parse(null, [5, 4, 3]));
        Assert.Null(OemLedFormat.Parse("12ff0000;12ff0000;1200FFFF", []));
    }

    [Fact]
    public void Parse_按maxZones裁分区数()
    {
        var p = OemLedFormat.Parse("12ff0000;12ff0000;1200FFFF;12FF0000;120000FF", [5, 4, 3], maxZones: 2);

        Assert.NotNull(p);
        Assert.Equal(2, p!.Zones.Length);
        Assert.Equal(5, p.Zones[0].Addr);
    }

    [Fact]
    public void Build与Parse往返一致()
    {
        var p = Profile(1, 3, (5, [10, 20, 30]), (4, [40, 50, 60]), (3, [70, 80, 90]));
        var back = OemLedFormat.Parse(OemLedFormat.Build(null, p), [5, 4, 3]);

        Assert.NotNull(back);
        Assert.Equal(p.Level, back!.Level);
        Assert.Equal(p.Effect, back.Effect);
        Assert.Equal(p.Zones.Select(z => (z.Addr, z.Rgb[0], z.Rgb[1], z.Rgb[2])),
                     back.Zones.Select(z => (z.Addr, z.Rgb[0], z.Rgb[1], z.Rgb[2])));
    }
}
