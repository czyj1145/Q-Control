using System.Globalization;

namespace QControl.Core.Devices;

/// <summary>
/// 原厂 ControlCenter 灯效注册表值的编解码 —— **纯函数，不碰注册表、不碰 EC**。
///
/// 值形状（反编译 <c>MY_DATA_LED_GROUP.str_format</c> + 本机实测双向对齐）：
/// <code>
/// "12ff0000;12ff0000;1200FFFF;1200FFFF;1200FFFF"
///   │          │          │
///   │          │          └─ 段 3/4/5 = 键盘 1..3（daemon 的 LED1/LED2/LED3）
///   │          └──────────── 段 2 = TRUNK（整条灯带，原厂那套"神光同步"）
///   └─────────────────────── 段 1 = LOGO
/// </code>
/// 每段 8 个十六进制字符：<c>{dt3d:X2}{RRGGBB}</c>，
/// <c>dt3d = (effect &lt;&lt; 4) | level</c>（<c>12ff0000</c> = 常亮 / 高亮 / 纯红）。
///
/// 为什么单独抽出来：段顺序写错一位，daemon 就会把键盘染成 Logo 那个颜色，
/// 而且只在它下次启动时才看得见 —— 必须有单测把它钉住。
/// </summary>
public static class OemLedFormat
{
    /// <summary>原厂值里的段数（LOGO / TRUNK / KB1..3）。</summary>
    public const int SegmentCount = 5;

    /// <summary>原厂没写过时它自己的出厂值：常亮(1) / 50%(1) / 全白。</summary>
    public const string FallbackSegment = "1100FFFF";

    /// <summary>
    /// 档案 → 原厂 5 段值。
    ///
    /// <list type="bullet">
    /// <item><b>TRUNK</b> 跟键盘主色走：原厂四个档的值里它本来就与键盘同色，
    ///   而 daemon 在部分 SKU 上会单独重新写这条灯带 —— 保留旧值就等于让它把那条灯带留成旧颜色。</item>
    /// <item><b>LOGO</b> 有档案色才覆盖，没有就保留 <paramref name="old"/> 里那一段
    ///   （没声明独立灯珠的机型不该被我们改）。</item>
    /// <item>键盘三段按 <paramref name="profile"/> 里地址降序（本机 5/4/3 = A/B/C），
    ///   不足三段就重复最后一段（单区机型原厂只读段 3）。</item>
    /// </list>
    /// </summary>
    public static string Build(string? old, BacklightProfile profile)
    {
        var parts = (old ?? string.Empty).Split(';', StringSplitOptions.TrimEntries);
        var slot = new string[SegmentCount];
        for (var i = 0; i < slot.Length; i++)
            slot[i] = i < parts.Length && parts[i].Length > 0 ? parts[i] : FallbackSegment;

        var zones = profile.Zones.OrderByDescending(z => z.Addr).ToList();
        if (zones.Count == 0) return string.Join(';', slot);

        var dt3d = (byte)(((profile.Effect & 0x0F) << 4) | (profile.Level & 0x0F));
        string Hex(int[] rgb) => $"{dt3d:X2}{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";

        slot[1] = Hex(zones[0].Rgb);                       // TRUNK = 键盘主色
        if (profile.Logo is { } logo) slot[0] = Hex(logo);  // 没有就保留原厂 LOGO 段

        for (var i = 0; i < 3; i++)
            slot[2 + i] = Hex(zones[Math.Min(i, zones.Count - 1)].Rgb);

        return string.Join(';', slot);
    }

    /// <summary>
    /// 原厂 5 段值 → 档案。段 3/4/5 依次对应 <paramref name="addrs"/>（降序的键盘地址）；
    /// 段全 0（原厂没设过）的跳过，一条都解不出来 ⇒ null（调用方保持"缺档不凑"）。
    /// 亮度和灯效取第一段有效的 <c>dt3d</c>。
    /// </summary>
    public static BacklightProfile? Parse(string? raw, IReadOnlyList<int> addrs, int maxZones = 0)
    {
        if (string.IsNullOrWhiteSpace(raw) || addrs.Count == 0) return null;

        var parts = raw.Split(';', StringSplitOptions.TrimEntries);
        var zones = new List<(int Addr, int[] Rgb)>();
        int level = 2, effect = 1;

        for (var i = 0; i < 3 && i < addrs.Count; i++)
        {
            if (2 + i >= parts.Length) break;
            if (!uint.TryParse(parts[2 + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
                || v == 0)
                continue;

            var dt3d = (int)((v >> 24) & 0xFF);
            if (zones.Count == 0)
            {
                level = dt3d & 0x0F;
                effect = (dt3d >> 4) & 0x0F;
            }

            zones.Add((addrs[i],
            [
                (int)((v >> 16) & 0xFF),
                (int)((v >> 8) & 0xFF),
                (int)(v & 0xFF),
            ]));

            if (maxZones > 0 && zones.Count >= maxZones) break;
        }

        if (zones.Count == 0) return null;
        return new BacklightProfile(Math.Clamp(level, 0, 2), Math.Clamp(effect, 0, 15), zones.ToArray());
    }
}
