namespace QControl.Core.Model;

/// <summary>
/// 「按名字挑一条 Windows 电源计划」—— 纯字符串规则，不碰系统。
///
/// 为什么要单独一个文件（2026-11-03 用户报「电源计划列表里有两行高性能」）：
/// 本机 `powercfg /list` 里 `高性能` 有两条 —— `8c5e7fda…`（Windows 自带）与
/// `a9932169…`（原厂另建）。以前的挑法是"遇到第一个同名就 break"，于是
/// **切档切到哪条取决于 powercfg 的输出顺序**，等于没有规则。
///
/// 放在 Core 而不是 Hal 的原因：这是纯策略，没有任何 Windows API ——
/// 放在这里测试项目（net10.0）才引用得到，Hal（net10.0-windows）引不了。
/// </summary>
public static class PowerPlanPick
{
    /// <summary>
    /// Windows 自带的四个基础电源计划 GUID（系统固定值，与系统语言无关）。
    /// 只用来回答一件事：「同名多条时，哪条是系统自带的那条」。
    /// </summary>
    public static readonly IReadOnlySet<string> StockGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "381b4222-f694-41f0-9685-ff5bb260df2e",   // 平衡 Balanced
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",   // 高性能 High performance
        "a1841308-3541-4fab-bc81-f71556f20b4a",   // 节能 Power saver
        "e9a42b02-d5df-448d-aa00-03f14749eb61",   // 卓越性能 Ultimate performance
    };

    public static bool IsStock(string? guid) => guid is not null && StockGuids.Contains(guid);

    /// <summary>
    /// 同名候选里挑一条。规则（按优先级）：
    /// **正在用的** &gt; **系统自带的** &gt; 列表里第一条。
    ///
    /// 挑「正在用的」优先：切档的语义是"跟上这个档位该有的计划"，
    /// 而用户手上正在跑的那条一定是他认的那条 —— 换掉它反而会动到他的设置。
    /// </summary>
    /// <param name="matches">同名候选条数（&gt;1 时调用方应把"选了哪条"记进日志）。</param>
    /// <returns>选中的 GUID；没有同名项时为 <c>null</c>。</returns>
    public static string? PickByName(IEnumerable<(string? Guid, string? Name)> plans,
                                     string? want, string? active, out int matches)
    {
        matches = 0;
        if (string.IsNullOrWhiteSpace(want)) return null;

        var target = want.Trim();
        var hits = new List<(string? Guid, string? Name)>();
        foreach (var p in plans)
        {
            if (!string.Equals((p.Name ?? "").Trim(), target, StringComparison.Ordinal)) continue;
            if (!IsGuid(p.Guid)) continue;      // 坏 GUID 不参与挑选，免得选中一条切不动的
            hits.Add(p);
        }

        matches = hits.Count;
        if (matches == 0) return null;
        if (matches == 1) return hits[0].Guid;

        var live = hits.FirstOrDefault(p => string.Equals(p.Guid, active, StringComparison.OrdinalIgnoreCase));
        if (live.Guid is not null) return live.Guid;

        var stock = hits.FirstOrDefault(p => IsStock(p.Guid));
        if (stock.Guid is not null) return stock.Guid;

        return hits[0].Guid;
    }

    /// <summary>GUID 形态校验（与 <c>powercfg</c> 的入参一致：36 位标准格式）。</summary>
    public static bool IsGuid(string? s) => s is not null && s.Length == 36 && Guid.TryParse(s, out _);
}
