using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QControl.App.Http;
using QControl.App.Http.Contract;
using QControl.App.Ui.Controls;

namespace QControl.App.Ui.Views;

/// <summary>
/// CoolView 分片：**曲线点位与槽位的纯数据转换**。
///
/// 后端 DTO 有十几种「温度/转速」的列表形状（cpu/gpu × office/game/beast），
/// 它们字段名一样、类型不同，**不能靠泛型擦除统一** —— 所以每个形状一个重载。
/// 这里只做转换与签名（`_curveSig` / `_slotsSig` 用来判断"要不要重画"），
/// 不碰控件、不写接口。加新档位时照现有重载补一条即可。
/// </summary>
public partial class CoolView
{
    private static List<CurvePt> PointsOf(List<FanCurveStateCurveItemDto>? list)
        => PointsOf(list, p => (p.T ?? 0, p.D ?? 0));

    private static List<CurvePt> PointsOf(List<FanCurveStateCurveCpuItemDto>? list)
        => PointsOf(list, p => (p.T ?? 0, p.D ?? 0));

    private static List<CurvePt> PointsOf(List<FanCurveStateCurveGpuItemDto>? list)
        => PointsOf(list, p => (p.T ?? 0, p.D ?? 0));

    private static List<CurvePt> PointsOf(List<FanCurveStateCurveSysItemDto>? list)
        => PointsOf(list, p => (p.T ?? 0, p.D ?? 0));

    private static List<CurvePt> PointsOf(List<FanCurveStateModeCurvesOfficeItemDto>? list)
        => PointsOf(list, p => (p.T ?? 0, p.D ?? 0));

    /// <summary>DTO 里每个 {t,d} 数组都是独立生成的类（字段完全一样），统一走这一个归一。</summary>
    private static List<CurvePt> PointsOf<T>(List<T>? list, Func<T, (double T, long D)> pick)
    {
        var result = new List<CurvePt>();
        if (list is null) return result;
        foreach (var p in list)
        {
            var (t, d) = pick(p);
            result.Add(new CurvePt(t, (int)d));
        }
        result.Sort((a, b) => a.T.CompareTo(b.T));
        return result;
    }

    private static List<CurvePt> PointsOf(JsonArray? arr)
    {
        var result = new List<CurvePt>();
        if (arr is null) return result;
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            if (Num(o["t"]) is not { } t || Num(o["d"]) is not { } d) continue;
            result.Add(new CurvePt(t, (int)d));
        }
        result.Sort((a, b) => a.T.CompareTo(b.T));
        return result;
    }

    /// <summary>DTO 里三档是三个独立类（字段完全一样），这里统一成一份。</summary>
    private static List<(double T, int D)>? SlotPoints(FanCurveStateModeCurvesDto? mc, string? key)
        => key switch
        {
            "office" => mc?.Office?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "game" => mc?.Game?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "beast" => mc?.Beast?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            _ => null,
        };

    private static List<(double T, int D)>? SlotPoints(FanCurveStateModeCurvesCpuDto? mc, string? key)
        => key switch
        {
            "office" => mc?.Office?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "game" => mc?.Game?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "beast" => mc?.Beast?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            _ => null,
        };

    private static List<(double T, int D)>? SlotPoints(FanCurveStateModeCurvesGpuDto? mc, string? key)
        => key switch
        {
            "office" => mc?.Office?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "game" => mc?.Game?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "beast" => mc?.Beast?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            _ => null,
        };

    private static List<(double T, int D)>? SlotPoints(FanCurveStateModeCurvesSysDto? mc, string? key)
        => key switch
        {
            "office" => mc?.Office?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "game" => mc?.Game?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            "beast" => mc?.Beast?.Select(p => ((double)(p.T ?? 0), (int)(p.D ?? 0))).ToList(),
            _ => null,
        };

    /// <summary>把上面那份元组列表转成 CurvePt（三档的 DTO 类型不同，先归一成元组再转）。</summary>
    private static List<CurvePt> PointsOf(List<(double T, int D)>? list)
    {
        var result = new List<CurvePt>();
        if (list is null) return result;
        foreach (var (t, d) in list) result.Add(new CurvePt(t, d));
        result.Sort((a, b) => a.T.CompareTo(b.T));
        return result;
    }

    private static string SlotSig(List<FanCurveStateModeCurvesOfficeItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesGameItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesBeastItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesCpuOfficeItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesCpuGameItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesCpuBeastItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesGpuOfficeItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesGpuGameItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig(List<FanCurveStateModeCurvesGpuBeastItemDto>? list)
        => SlotSig(list, p => (p.T, p.D));

    private static string SlotSig<T>(List<T>? list, Func<T, (long? T, long? D)> pick)
        => list is null ? "" : string.Join(",", list.Select(p => $"{pick(p).T}:{pick(p).D}"));

    private static string Sig(List<CurvePt>? pts)
        => pts is null ? "" : string.Join(",", pts.Select(p => $"{(int)p.T}:{p.D}"));

    private static JsonArray CurveJson(List<CurvePt> pts)
    {
        var arr = new JsonArray();
        foreach (var p in pts)
            arr.Add(new JsonObject { ["t"] = (int)Math.Round(p.T), ["d"] = p.D });
        return arr;
    }

    private static string DutyText(List<CurvePt> pts, double t)
    {
        if (pts.Count == 0) return "--";
        var sorted = pts.OrderBy(p => p.T).ToList();
        if (t <= sorted[0].T) return sorted[0].D + "%";
        if (t >= sorted[^1].T) return sorted[^1].D + "%";
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].T < t) continue;
            var lo = sorted[i - 1];
            var hi = sorted[i];
            var k = (t - lo.T) / Math.Max(1e-6, hi.T - lo.T);
            return (int)Math.Round(lo.D + (hi.D - lo.D) * k) + "%";
        }
        return sorted[^1].D + "%";
    }

    /// <summary>标签配色按语气重设（none / warn / ok / hot）。内容不变就不重复换样式。</summary>
    private void SetTagTone(Border box, TextBlock text, string tone)
    {
        var borderKey = tone switch
        {
            "warn" => "CardTagWarn",
            "ok" => "CardTagOk",
            "hot" => "CardTagHot",
            _ => "CardTag",
        };
        var textKey = tone switch
        {
            "warn" => "CardTagTextWarn",
            "ok" => "CardTagTextOk",
            "hot" => "CardTagTextHot",
            _ => "CardTagText",
        };
        // 每秒都会走一遍：样式没变就别重设（重设会让控件重新套模板）
        var borderStyle = (Style)FindResource(borderKey);
        if (!ReferenceEquals(box.Style, borderStyle)) box.Style = borderStyle;
        var textStyle = (Style)FindResource(textKey);
        if (!ReferenceEquals(text.Style, textStyle)) text.Style = textStyle;
    }

    private void Toast(string text, bool ok) => (Window.GetWindow(this) as MainWindow)?.Toast(text, ok);

    private static string? LabelOf(string? key) => key switch
    {
        null => null,
        "office" => "办公模式",
        "game" => "游戏模式",
        "beast" => "狂暴模式",
        _ => key,
    };

    private static string? StrOf(JsonElement? e)
        => e is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static long? LongOf(JsonElement? e)
        => e is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    private static bool? BoolOf(JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static long? LongOfE(JsonNode? n)
        => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out var l)
            ? l : null;

    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static string Fmt(double value)
        => value.ToString("F0", CultureInfo.InvariantCulture);

    private static string Fmt(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Dump(long? value)
        => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Dump(double? value)
        => value is { } v ? v.ToString("F0", CultureInfo.InvariantCulture) : null;
}
