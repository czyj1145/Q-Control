using System.Text.Json.Nodes;

namespace QControl.Core.Devices;

/// <summary>
/// 键盘分区表（`presets.backlight.zones`）的合并规则 —— **只补缺，不覆盖、不删除**。
///
/// 为什么单拎出来：一键采集的「引导」只能看到用户在原厂里点过的那一个分区，
/// 于是采集结果往往只有 1 个地址；直接拿它替换配置，三区机型就被砍成一区
/// （2026-11-14 真机实测：`5/4/3` 变成只剩 `4`，背光页从 3 个 tab 变 1 个，
/// 而用户完全看不出是采集干的）。
///
/// 反过来也不能"不动"：新机型第一次采集时配置里没有分区表，那一次必须写进去。
/// 所以规则是 —— 现有表打底（顺序、颜色、名字全保留），采集到的地址里**没见过的追加**，
/// 但追加数受机型声明的分区数约束（见 <see cref="MaxSupported"/> / <see cref="Sanitize"/>）。
/// </summary>
public static class BacklightZones
{
    /// <summary>
    /// 世上真实存在的键盘分区数上限：1（整块）/ 3（绝大多数）/ 2、4（极罕见）。
    /// **五分区不存在** —— 所以任何"看见 N 个地址就画 N 格"的逻辑都必须拿这个数兜底，
    /// 否则探针往 logo 灯之类的地址试两下，界面上就多出两个不存在的分区（2026-11-17 真事）。
    /// </summary>
    public const int MaxSupported = 4;

    /// <summary>
    /// 把机型声明的分区数收进真实范围：0 = 未声明（不裁），1~4 原样，&gt;4 按 3 处理。
    ///
    /// 为什么 &gt;4 落到 3 而不是"裁到 4"：多出来的地址是脏数据（logo 灯 / 灯条 / 探针试色），
    /// 裁掉两格留下的那 4 格同样是猜的；而三区是唯一常见的真实布局。
    /// </summary>
    public static int Sanitize(int declared)
        => declared <= 0 ? 0 : declared <= MaxSupported ? declared : 3;


    /// <summary>
    /// 合并两张分区表。
    /// </summary>
    /// <param name="existing">配置里现有的（可为 null / 空）。</param>
    /// <param name="incoming">采集 / 引导给出的（只取其中现有表里没有的地址）。</param>
    /// <returns>合并后的新数组（元素已 DeepClone，可以直接挂进 JSON 树）与新增条数。</returns>
    public static (JsonArray Zones, int Added) Merge(JsonArray? existing, JsonArray? incoming)
    {
        var zones = new JsonArray();
        var have = new HashSet<int>();

        if (existing is not null)
        {
            foreach (var z in existing)
            {
                zones.Add(z?.DeepClone());
                if (Addr(z) is { } a) have.Add(a);
            }
        }

        var before = zones.Count;
        if (incoming is not null)
        {
            foreach (var z in incoming)
            {
                if (Addr(z) is not { } a || !have.Add(a)) continue;
                zones.Add(z?.DeepClone());
            }
        }

        return (zones, zones.Count - before);
    }

    /// <summary>
    /// 把一次下发里的颜色合并进现有分区表 —— **以现有表打底**：顺序、`name`、
    /// 以及这次没点名的地址全部原样保留，点名的地址换颜色，表里没有的地址追加
    /// （最多追加到 <paramref name="maxZones"/> 条，见 <see cref="Limit"/>）。
    ///
    /// 为什么不是"拿请求的 zones 整表重建"（旧实现就是这么干的）：
    /// 那样只要有一次请求只带一个地址，三区配置就被砍成一区 —— 背光页从 3 个 tab 变 1 个，
    /// 用户完全看不出是谁干的。2026-11-14 采集事故是这个症状，2026-11-15 探针又踩了一次。
    /// </summary>
    public static JsonArray Apply(JsonArray? existing, IEnumerable<(int Addr, int[] Rgb)> incoming,
                                  int maxZones = 0)
    {
        var wanted = new Dictionary<int, int[]>();
        foreach (var (addr, rgb) in incoming) wanted[addr] = rgb;

        var zones = new JsonArray();
        var done = new HashSet<int>();

        if (existing is not null)
        {
            foreach (var node in existing)
            {
                if (node is JsonObject zo && Addr(zo) is { } a && wanted.TryGetValue(a, out var rgb))
                {
                    var o = new JsonObject
                    {
                        ["addr"] = a,
                        ["rgb"] = new JsonArray(rgb.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                    };
                    if (zo["name"] is JsonValue nv && nv.TryGetValue<string>(out var nm)) o["name"] = nm;
                    zones.Add(o);
                    done.Add(a);
                }
                else
                {
                    zones.Add(node?.DeepClone());
                }
            }
        }

        foreach (var (addr, rgb) in incoming)
        {
            if (!done.Add(addr)) continue;
            // 表已经装够机型声明的分区数 ⇒ 陌生地址**不追加**（见 <see cref="Limit"/> 的说明：
            // 探针往 8/7 试色会把三区表撑成五区，背光页就按五等分画了）
            if (maxZones > 0 && zones.Count >= maxZones) break;
            zones.Add(new JsonObject
            {
                ["addr"] = addr,
                ["rgb"] = new JsonArray(rgb.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            });
        }

        return zones;
    }

    /// <summary>
    /// 按机型声明的分区数裁表 + 同名去重。
    ///
    /// 为什么需要：<see cref="Apply"/> 会把请求里没见过的地址追加进来（新机型第一次采集靠它），
    /// 于是一次**探针**分别往 8 / 7 写了红 / 绿，分区表就从 `5/4/3` 涨成 5 条 ——
    /// 背光页冒出 A~E 五个 tab、键盘示意图按五等分画（2026-11-17 用户："怎么变成五区了"）。
    /// 机型档案的 `caps.kb_zones` 才是这台机器分区的权威数量，多出来的都不是它身上的分区。
    ///
    /// 规则：① 有条目 `name` 时按 name 去重（同名保留第一条 —— A 区不可能有两个）；
    ///       ② `max &lt;= 0`（机型没声明）时只去重、不裁数量。
    /// </summary>
    /// <returns>裁剪后的新数组（元素已 DeepClone）与被剔除的条数。</returns>
    public static (JsonArray Zones, int Removed) Limit(JsonArray? zones, int max)
    {
        var kept = new JsonArray();
        var removed = 0;
        if (zones is null) return (kept, 0);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in zones)
        {
            if (max > 0 && kept.Count >= max) { removed++; continue; }
            if (node is JsonObject zo && zo["name"] is JsonValue nv
                && nv.TryGetValue<string>(out var nm) && nm.Length > 0 && !names.Add(nm))
            {
                removed++;
                continue;
            }
            kept.Add(node?.DeepClone());
        }

        return (kept, removed);
    }

    /// <summary>
    /// 分区表里的地址：`addr` 字段（数字或数字字符串）。
    /// 读不出来返回 null —— 那种条目**不参与**去重，但会被原样保留（不动别人的数据）。
    /// </summary>
    private static int? Addr(JsonNode? node)
    {
        if (node is not JsonObject o || o["addr"] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        return null;
    }
}
