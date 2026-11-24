namespace QControl.Core.Power;

/// <summary>电源计划卡片悬停提示的素材。全是素朴值 —— Core 不认识 DTO，也不认识 Hal 的 PlanValues。</summary>
public readonly record struct PlanTipInput(
    string? Name,
    string? Guid,
    bool DuplicateName,
    int? MinAc,
    int? MaxAc,
    int? MinDc,
    int? MaxDc,
    int? CoolAc,
    int? CoolDc,
    int? BrightAc,
    int? BrightDc);

/// <summary>
/// 「电源计划」卡片的悬停提示：名称 / 重名时补 GUID / 处理器上下限 / 散热策略 / 屏幕亮度。
///
/// ⚠️ **只有名称时返回 null**（调用方据此不挂 ToolTip）—— 一条只剩名字的提示不如不显示。
/// ⚠️ 处理器那一行的「电池 xx–yy%」是**可选尾缀**：只在两个电池值都读到时才拼，
///    缺一个就整段不写，而不是写一半变成 "20–%"。
/// ⚠️ 散热值 0/1 是 Windows 的「被动/主动」，其它数字原样显示（厂家自定义值）。
/// </summary>
public static class PlanTooltip
{
    public static string? Build(PlanTipInput i)
    {
        var lines = new List<string> { (i.Name ?? "").Trim() };
        if (i.DuplicateName && i.Guid is { Length: > 0 }) lines.Add(i.Guid);

        if (i.MinAc is { } a1 && i.MaxAc is { } a2)
        {
            var dc = i.MinDc is { } b1 && i.MaxDc is { } b2
                ? $" · 电池 {b1}–{b2}%"
                : "";
            lines.Add($"处理器 交流 {a1}–{a2}%{dc}");
        }

        var coolAc = Cooling(i.CoolAc);
        var coolDc = Cooling(i.CoolDc);
        if (coolAc is not null || coolDc is not null)
            lines.Add($"散热 交流 {coolAc ?? "—"} · 电池 {coolDc ?? "—"}");

        if (i.BrightAc is not null || i.BrightDc is not null)
        {
            lines.Add("屏幕亮度 交流 " + (i.BrightAc is { } ba ? ba.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—")
                      + "% · 电池 " + (i.BrightDc is { } bd ? bd.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—") + "%");
        }

        return lines.Count > 1 ? string.Join("\n", lines) : null;
    }

    private static string? Cooling(int? v) => v switch
    {
        0 => "被动",
        1 => "主动",
        null => null,
        _ => v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
