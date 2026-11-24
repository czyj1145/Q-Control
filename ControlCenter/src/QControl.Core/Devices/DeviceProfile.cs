using System.Text.Json.Nodes;
using QControl.Core.Data;

namespace QControl.Core.Devices;

/// <summary>
/// 一份机型档案（`devices/*.json`）。
///
/// 档案 = 「这台机器能干什么」的知识库；用户策略在 model.json，两者不能混。
/// 加载时**坏档案只跳过、绝不挡住启动** —— 早先实现踩过「一个坏 profile
/// 让整个控制中心起不来」的坑。
/// </summary>
public sealed class DeviceProfile
{
    /// <summary>
    /// 用户档案（`%ProgramData%\Q-Control\devices\*.json`，由「一键采集」产出）的优先级下限。
    ///
    /// **必须低于任何具体机型档案、高于 generic**：generic 是 10、nl5 是 100，所以取 50。
    /// 这条顺序是有安全含义的 ——
    ///   · 未适配机型只命中 generic(10) ⇒ 本机采集的 50 胜出，"采完就能显示温度"成立；
    ///   · 已适配机型命中 nl5(100) ⇒ **采集出来的档案抢不掉它**。否则用户在一次
    ///     "温度没动、没认出偏移"的采集之后，会把自己机器上原本正确的标定覆盖成空布局，
    ///     表现是"点了一下，温度反而不显示了"（2026-10-31 本机实测踩到）。
    /// </summary>
    public const double UserPriorityFloor = 50;

    public JsonObject Raw { get; }
    public string Id { get; }
    public double Priority { get; }

    /// <summary>来自用户档案目录（而不是打包时嵌进程序的 devices/*.json）。</summary>
    public bool FromUser { get; }

    /// <summary>这份档案是否经过验证（写入行为可信）。一键采集产物恒为 false。</summary>
    public bool Verified { get; }

    private DeviceProfile(JsonObject raw, string id, double priority, bool fromUser)
    {
        Raw = raw;
        Id = id;
        Priority = priority;
        FromUser = fromUser;
        Verified = raw["verified"] is JsonValue vv && vv.TryGetValue<bool>(out var b) && b;
    }

    /// <summary>从 json 构造；没有 id 的（不是有效档案）返回 null。</summary>
    public static DeviceProfile? TryLoad(JsonObject raw, bool fromUser = false)
    {
        var id = raw["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(id)) return null;

        var priority = 0.0;
        if (raw["priority"] is JsonValue pv)
        {
            if (pv.TryGetValue<double>(out var d)) priority = d;
            else if (pv.TryGetValue<string>(out var ps)
                     && double.TryParse(ps, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out var pd))
                priority = pd;
        }
        if (fromUser && priority < UserPriorityFloor) priority = UserPriorityFloor;

        return new DeviceProfile(raw, id!, priority, fromUser);
    }

    /// <summary>
    /// 加载全部档案，按 priority 降序、同 priority 按 id 升序。
    /// 顺序即匹配优先级 —— 专用档案（nl5，priority 100）必须排在通用档案（0）前面。
    ///
    /// <paramref name="userDir"/> 非空时把该目录下的 `*.json` 一并加载：
    ///   · **同 id 取"更强"的那份**：强度 = (已验证, 优先级)，同强度时用户档案（后来者）胜。
    ///     这样未适配机型的采集档案（verified=false、prio=50）能压过 generic(10)，
    ///     但已验证内置档案（如 nl5、prio=100、verified=true）**绝不会被一次低置信采集
    ///     （verified=false）顶掉** —— 否则用户点一下"一键采集"，反而把自己机器上原本
    ///     正确的标定覆盖成空布局（2026-10-31 真机踩到：采集出的 quanta-nl5 把内置那份干掉，
    ///     温度、转速、风扇曲线一夜之间全没了）。
    ///   · priority 有下限 <see cref="UserPriorityFloor"/>；
    ///   · 读不动 / 写坏了的文件只跳过并计数（见 <see cref="LoadAllDetailed"/>）。
    /// </summary>
    public static IReadOnlyList<DeviceProfile> LoadAll(string? userDir = null)
        => LoadAllDetailed(userDir).Profiles;

    /// <summary>
    /// 同 <see cref="LoadAll(string?)"/>，但把"哪个文件没读进来"也带出来 ——
    /// 采集器要把这句话原样告诉用户，而不是"保存成功、实际没生效"。
    /// </summary>
    public static (IReadOnlyList<DeviceProfile> Profiles, IReadOnlyList<string> Skipped)
        LoadAllDetailed(string? userDir = null)
    {
        var list = new List<DeviceProfile>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var skipped = new List<string>();

        // 同 id 时保留更强的那份：强度 = (已验证, 优先级)，同强度用户档案（后来者）胜。
        static bool IsStronger(DeviceProfile candidate, DeviceProfile existing)
        {
            var cv = candidate.Verified ? 1 : 0;
            var ev = existing.Verified ? 1 : 0;
            if (cv != ev) return cv > ev;                       // 已验证优先于未验证
            if (candidate.Priority != existing.Priority) return candidate.Priority > existing.Priority;
            return candidate.FromUser;                          // 同强度：用户档案胜（允许手调覆盖升级）
        }

        void Add(DeviceProfile p)
        {
            if (index.TryGetValue(p.Id, out var i))
            {
                if (IsStronger(p, list[i])) list[i] = p;        // 只覆盖"更强"的，不无脑后来居上
            }
            else
            {
                index[p.Id] = list.Count;
                list.Add(p);
            }
        }

        foreach (var path in EmbeddedData.List("devices"))
        {
            try
            {
                if (EmbeddedData.ReadNode(path) is JsonObject o && TryLoad(o) is { } p)
                    Add(p);
            }
            catch
            {
                // 坏档案只跳过。启动不能因为一份 json 写错就整个起不来。
            }
        }

        foreach (var file in UserFiles(userDir))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject o && TryLoad(o, fromUser: true) is { } p)
                    Add(p);
                else
                    skipped.Add(Path.GetFileName(file) + "（没有 id 字段，不像一份机型档案）");
            }
            catch (Exception e)
            {
                skipped.Add(Path.GetFileName(file) + "（" + e.Message + "）");
            }
        }

        return (list.OrderByDescending(p => p.Priority)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .ToList(), skipped);
    }

    /// <summary>用户档案目录下的 json（按文件名排序，保证同一目录两次加载顺序一致）。</summary>
    public static IReadOnlyList<string> UserFiles(string? userDir)
    {
        if (string.IsNullOrWhiteSpace(userDir) || !Directory.Exists(userDir)) return [];
        try
        {
            return Directory.EnumerateFiles(userDir, "*.json", SearchOption.TopDirectoryOnly)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch
        {
            return [];   // 目录在但读不了（权限）：按"没有用户档案"处理，不挡住启动
        }
    }
}
