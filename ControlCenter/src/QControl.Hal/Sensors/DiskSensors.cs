using System.Management;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QControl.Hal.Sensors;

/// <summary>
/// 物理硬盘档案：每帧探盘集合，指纹变了才走 WMI 全量。
///
/// 来源分工（原生优先）：
/// <list type="bullet">
/// <item>身份（型号/容量/固件/序列号）—— `root\cimv2` 的 `Win32_DiskDrive`；</item>
/// <item>介质/总线/健康 —— `root\Microsoft\Windows\Storage` 的 `MSFT_PhysicalDisk`。
///   ⚠️ NVMe 盘在 cimv2 里的 `InterfaceType` 恒报 SCSI，所以总线**只能**看这里；</item>
/// <item>盘 ↔ 卷 —— `Win32_DiskDriveToDiskPartition` + `Win32_LogicalDiskToPartition`。</item>
/// </list>
///
/// **变更探测走原生 IOCTL**（2026-10-23）：原来整块档案挂 300 秒 TTL，
/// 代价是 U 盘插上/拔出要等 **5 分钟**才反映（用户原话「插上和拔出没反应」）。
/// 现在每一帧都用 <see cref="QControl.Hal.Sys.NativeDisk.Enumerate"/>
/// 现查一遍盘集合（0.05 ms，比 WMI 的 70 ms 快 1000 倍），指纹一变就重建 ——
/// **插拔下一帧（≤1 s）就反映**，而 WMI 全量查询只在那一刻跑一次。
///
/// **SMART 温度/寿命/累计读写不在这里** —— 那几项只有 HWiNFO 有，由 P5 合并进来。
/// 所以本类产出的条目**不会带** `temp`/`life`/`spare`/`writes_gb`/`reads_gb`，
/// 界面按既有约定整行隐藏（不显示 `--`，也不填 0）。
///
/// ⚠️ **别再试"把 SMART 改原生"**（2026-11-02 实测到底，细节见 DETAILS 同名节）：
/// 本机存储栈对 `StorageDeviceProtocolSpecificProperty` / `StorageDeviceTemperatureProperty` /
/// `IOCTL_STORAGE_PROTOCOL_COMMAND` 一律回 INVALID_FUNCTION / INVALID_PARAMETER（提权、停掉
/// HWiNFO 的内核驱动都不变），`MSFT_StorageReliabilityCounter` 是 0 行。
/// HWiNFO 能读是因为它自带**内核**驱动直连控制器 —— 纯用户态没有这条路。
/// </summary>
public sealed class DiskSensors
{
    /// <summary>
    /// 原生枚举完全看不到盘（IOCTL 路径整体失效）时的兜底重查间隔。
    /// 正常情况下这个值用不到 —— 插拔由**每帧指纹**驱动，不走 TTL。
    /// </summary>
    private const double IdleRetryTtl = 30.0;

    private static readonly Dictionary<int, string> Bus = new()
    {
        [0] = "", [1] = "SCSI", [2] = "ATAPI", [3] = "ATA", [4] = "1394", [5] = "SSA",
        [6] = "光纤", [7] = "USB", [8] = "RAID", [9] = "iSCSI", [10] = "SAS", [11] = "SATA",
        [12] = "SD", [13] = "MMC", [14] = "虚拟", [15] = "虚拟文件", [16] = "存储池",
        [17] = "NVMe", [18] = "SMB",
    };

    private static readonly Dictionary<int, string> Media = new()
    {
        [3] = "HDD", [4] = "SSD", [5] = "SCM",
    };

    /// <summary>0=Healthy 1=Warning 2=Unhealthy 5=Unknown（1/2 时界面会标红）。</summary>
    private static readonly Dictionary<int, string> Health = new()
    {
        [0] = "健康", [1] = "警告", [2] = "异常", [5] = "健康未知",
    };

    private readonly object _lock = new();
    private List<JsonObject>? _cache;
    /// <summary>缓存对应的盘集合指纹（盘号/型号/序列号/容量/总线/可移动/卷）。</summary>
    private string _sig = "";
    private double _cacheAt;

    /// <summary>
    /// 物理硬盘的**静态部分**。插拔实时反映。
    ///
    /// ⚠️ 一律返回**深拷贝**：调用方会把这些节点挂进新的 JsonArray，
    ///    直接给缓存里的同一批 JsonObject 会在第二次挂载时抛
    ///    "The node already has a parent"（2026-10-18 实测踩到）。
    ///    顺带也避免调用方往对象里合 SMART 字段时污染缓存。
    /// </summary>
    public List<JsonObject> Identity(bool force = false)
    {
        lock (_lock)
        {
            // ① 每帧都用 IOCTL 探一遍盘集合（0.05 ms）。这一层只做「变没变」，
            //    不用它的字段值 —— 数据仍由 WMI 出，字段口径保持与早先实现逐字一致。
            var native = Sys.NativeDisk.Enumerate();
            var vols = native.Count > 0 ? Sys.NativeDisk.Volumes() : [];
            var sig = Signature(native, vols);
            var now = Now();

            // 原生一个盘都看不到 ⇒ IOCTL 路径整体失效，靠低频重查兜底
            var stale = native.Count == 0 && now - _cacheAt >= IdleRetryTtl;

            if (!force && _cache is { Count: > 0 } && sig == _sig && !stale)
                return Clone(_cache);

            // ② 只有真的变了才跑 WMI 全量（4 条并行，约 32 ms）
            var got = Query();
            if (got.Count == 0)
                got = BuildFromNative(native, vols);   // WMI 不可用（策略禁用等）才用原生兜底

            // ⚠️ 空结果**不覆盖**手里的好数据 —— 偶发 WMI 失败不该让整块硬盘卡消失
            if (got.Count > 0)
            {
                _cache = got;
                _sig = sig;
                _cacheAt = now;
            }
            return Clone(_cache ?? got);
        }
    }

    private static List<JsonObject> Clone(List<JsonObject> src)
        => src.Select(x => (JsonObject)x.DeepClone()).ToList();

    /// <summary>
    /// 界面用的完整档案 = 静态身份 + **每次现读的 SMART**。
    ///
    /// SMART 五项（temp/life/spare/writes_gb/reads_gb）只有 HWiNFO 有 ——
    /// 唯一能取 NVMe 温度/磨损的系统接口 `MSFT_StorageReliabilityCounter` 在本机是空的，
    /// NVMe 协议查询/温度属性/透传命令在本机存储栈上也全部被拒（2026-11-02 实测，见类注释）。
    /// 传 null（HWiNFO 没跑）时只回身份字段，那几项**整键不出现**，
    /// 界面按既有约定整行隐藏（不显示 `--`，也不填 0）。
    /// </summary>
    public List<JsonObject> Info(QControl.Hal.HwInfo.HwInfoReader? hw = null)
    {
        var list = Identity();
        if (hw is null) return list;

        List<QControl.Hal.HwInfo.HwInfoReader.SmartRecord> smart;
        try { smart = hw.FindDriveSmart(); }
        catch { return list; }
        if (smart.Count == 0) return list;

        foreach (var d in list)
        {
            var model = d["model"]?.GetValue<string>() ?? "";
            var serial = d["serial"]?.GetValue<string>() ?? "";
            var driveVols = (d["volumes"] as JsonArray)?
                            .Select(x => x?.GetValue<string>() ?? "")
                            .Where(s => s.Length > 0)
                            .ToList() ?? [];
            var rec = MatchSmart(smart, model, serial, driveVols);
            if (rec is null) continue;

            if (rec.Temp is { } t) d["temp"] = t;
            if (rec.Life is { } lf) d["life"] = lf;
            if (rec.Spare is { } sp) d["spare"] = sp;
            if (rec.WritesGb is { } w) d["writes_gb"] = w;
            if (rec.ReadsGb is { } r) d["reads_gb"] = r;

            // 兜底：原生映射没给出卷列表时才用 HWiNFO 组名里带的
            if (!d.ContainsKey("volumes") && rec.Volumes is { Count: > 0 } vols)
            {
                var arr = new JsonArray();
                foreach (var v in vols) arr.Add(v);
                d["volumes"] = arr;
            }
        }
        return list;
    }

    /// <summary>
    /// 把 HWiNFO 的 SMART 记录对到盘上。**序列号 → 卷 → 型号**，逐级降级（2026-11-12 修）。
    ///
    /// 只按型号对，两块同型号盘必然张冠李戴（HWiNFO 的组名里其实带着 SID，就是序列号）；
    /// 而"前缀匹配 + 取第一条"还会在型号 A 是型号 B 前缀时（`…HAJQ` vs `…HAJQ-000L2`）
    /// 把两块盘对到同一条，结果取决于字典枚举顺序。
    /// 所以：序列号全等 → 卷列表有交集（NVMe 的 WMI 序列号是命名空间 ID，和 HWiNFO 的
    /// 真序列号对不上，卷是更靠谱的判据）→ 型号全等 → 唯一的型号前缀；
    /// **一旦有歧义就返回 null**（宁可这一行不显示 SMART，也不要显示另一块盘的温度/写入量）。
    /// </summary>
    private static QControl.Hal.HwInfo.HwInfoReader.SmartRecord? MatchSmart(
        List<QControl.Hal.HwInfo.HwInfoReader.SmartRecord> smart,
        string model,
        string serial,
        List<string> volumes)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var ns = Norm(serial);
        if (ns.Length >= 6)
        {
            var bySn = smart.Where(x => Norm(x.Sid ?? "") == ns).ToList();
            if (bySn.Count == 1) return bySn[0];
            if (bySn.Count > 1) return null;
        }

        if (volumes.Count > 0)
        {
            var byVol = smart.Where(x => x.Volumes is { Count: > 0 } v
                                         && v.Any(vv => volumes.Contains(vv, StringComparer.OrdinalIgnoreCase)))
                             .ToList();
            if (byVol.Count == 1) return byVol[0];
            if (byVol.Count > 1) return null;
        }

        var n = Norm(model);
        if (n.Length == 0) return null;

        var exact = smart.Where(x => Norm(x.Model) == n).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;          // 同型号又没序列号/卷可对：别猜

        var prefix = smart.Where(x =>
        {
            var nk = Norm(x.Model);
            return nk.Length > 0
                   && (nk.StartsWith(n, StringComparison.Ordinal) || n.StartsWith(nk, StringComparison.Ordinal));
        }).ToList();
        return prefix.Count == 1 ? prefix[0] : null;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ================================================================ 变更指纹

    /// <summary>
    /// 盘集合指纹：任何一块盘的增减/换型/改盘符都会让它变。
    /// 只用原生字段，**不掺 WMI** —— 这一层必须便宜（每帧跑）。
    /// </summary>
    private static string Signature(List<Sys.NativeDiskInfo> disks, Dictionary<int, List<string>> vols)
    {
        var sb = new StringBuilder(256);
        foreach (var d in disks)
        {
            sb.Append(d.Index).Append('|')
              .Append(d.Model).Append('|')
              .Append(d.Serial).Append('|')
              .Append(d.SizeBytes).Append('|')
              .Append(d.BusType).Append('|')
              .Append(d.Removable ? 1 : 0).Append('|')
              .Append(vols.TryGetValue(d.Index, out var v) ? string.Join(",", v) : "")
              .Append(';');
        }
        return sb.ToString();
    }

    // ================================================================ WMI 全量

    /// <summary>
    /// WMI 全量查询（只在盘集合变化时跑）。四条并行 —— 串行实测 70 ms，并行 32 ms，
    /// 差别全在 `MSFT_PhysicalDisk` 那一条（Storage 提供程序单次就要 33 ms）。
    /// </summary>
    private static List<JsonObject> Query()
    {
        var drives = new List<JsonObject>();
        try
        {
            List<Dictionary<string, object?>> baseRows = [];
            List<Dictionary<string, object?>> metaRows = [];
            List<Dictionary<string, object?>> dd2p = [];
            List<Dictionary<string, object?>> ld2p = [];

            // ① 物理盘：型号 / 容量 / 固件 / 序列号
            // ② 介质 / 总线 / 健康（存储命名空间）
            // ③ 盘 ↔ 分区、④ 分区 ↔ 卷
            Parallel.Invoke(
                () => baseRows = Rows(@"\\.\root\cimv2",
                    "SELECT Index, Model, Size, SerialNumber, FirmwareRevision FROM Win32_DiskDrive",
                    ["Index", "Model", "Size", "SerialNumber", "FirmwareRevision"]),
                () => metaRows = Rows(@"\\.\root\Microsoft\Windows\Storage",
                    "SELECT DeviceId, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk",
                    ["DeviceId", "MediaType", "BusType", "Size", "HealthStatus"]),
                () => dd2p = Rows(@"\\.\root\cimv2",
                    "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition",
                    ["Antecedent", "Dependent"]),
                () => ld2p = Rows(@"\\.\root\cimv2",
                    "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition",
                    ["Antecedent", "Dependent"]));

            var meta = new Dictionary<int, Dictionary<string, object?>>();
            foreach (var p in metaRows)
            {
                if (int.TryParse(Str(p["DeviceId"]), out var devId))
                    meta[devId] = p;
            }

            var part2disk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in dd2p)
            {
                var pd = WmiKey(Str(a["Dependent"]));
                var dk = WmiKey(Str(a["Antecedent"]));
                if (pd.Length > 0 && dk.Length > 0) part2disk[pd] = dk;
            }

            var disk2vol = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in ld2p)
            {
                var pd = WmiKey(Str(a["Antecedent"]));
                var vol = WmiKey(Str(a["Dependent"]));
                if (part2disk.TryGetValue(pd, out var dk) && vol.Length > 0)
                {
                    if (!disk2vol.TryGetValue(dk, out var list))
                        disk2vol[dk] = list = [];
                    list.Add(vol);
                }
            }

            var i = 0;
            foreach (var b in baseRows)
            {
                i++;
                var model = (Str(b["Model"]) ?? "").Trim();
                if (model.Length == 0) continue;

                var idx = int.TryParse(Str(b["Index"]), out var v) ? v : i - 1;
                meta.TryGetValue(idx, out var m);
                m ??= [];

                var size = Num(m.GetValueOrDefault("Size")) ?? Num(b["Size"]);
                double? sizeGb = size is null ? null : Math.Round(size.Value / Math.Pow(1024, 3));

                var d = new JsonObject
                {
                    ["index"] = idx,
                    ["label"] = $"硬盘 {i}",
                    ["model"] = model,
                };

                if (Bus.TryGetValue((int)(Num(m.GetValueOrDefault("BusType")) ?? -1), out var bus)
                    && bus.Length > 0)
                    d["bus"] = bus;

                if (Media.TryGetValue((int)(Num(m.GetValueOrDefault("MediaType")) ?? -1), out var media))
                    d["media"] = media;

                if (sizeGb > 0) d["size_gb"] = (long)sizeGb.Value;

                if (Health.TryGetValue((int)(Num(m.GetValueOrDefault("HealthStatus")) ?? -1), out var hl))
                    d["health"] = hl;

                var fw = (Str(b["FirmwareRevision"]) ?? "").Trim();
                if (fw.Length > 0) d["firmware"] = fw;

                var sn = (Str(b["SerialNumber"]) ?? "").Trim();
                if (sn.Length > 0) d["serial"] = sn;

                if (disk2vol.TryGetValue($"PHYSICALDRIVE{idx}", out var vols) && vols.Count > 0)
                {
                    vols.Sort(StringComparer.Ordinal);
                    var arr = new JsonArray();
                    foreach (var x in vols) arr.Add(x);
                    d["volumes"] = arr;
                }

                drives.Add(d);
            }
        }
        catch
        {
            // WMI 不可用（策略禁用等）→ 返回已经拿到的部分；
            // 一个都没有时上层会退到原生兜底
        }
        return drives;
    }

    /// <summary>
    /// WMI 整个不可用时的兜底：直接用原生枚举的字段拼。
    /// 拿不到 `HealthStatus` 就**不给 health 键**（界面整行隐藏，不显示 `--`）。
    /// </summary>
    private static List<JsonObject> BuildFromNative(
        List<Sys.NativeDiskInfo> disks,
        Dictionary<int, List<string>> vols)
    {
        var list = new List<JsonObject>(disks.Count);
        var i = 0;

        foreach (var nd in disks)
        {
            i++;
            var d = new JsonObject
            {
                ["index"] = nd.Index,
                ["label"] = $"硬盘 {i}",
                ["model"] = nd.Model,
            };

            if (Bus.TryGetValue(nd.BusType, out var bus) && bus.Length > 0)
                d["bus"] = bus;

            var media = nd.SeekPenalty switch { false => "SSD", true => "HDD", _ => null };
            if (media is not null) d["media"] = media;

            if (nd.SizeBytes > 0)
                d["size_gb"] = (long)Math.Round(nd.SizeBytes / Math.Pow(1024, 3));

            if (nd.Firmware.Length > 0) d["firmware"] = nd.Firmware;
            if (nd.Serial.Length > 0) d["serial"] = nd.Serial;

            if (vols.TryGetValue(nd.Index, out var vl) && vl.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var x in vl) arr.Add(x);
                d["volumes"] = arr;
            }

            list.Add(d);
        }
        return list;
    }

    /// <summary>
    /// 跑一条 WQL。⚠️ 监视器串必须是 `\\.\root\…`（`\\.\` = 本机）——
    /// 少一个反斜杠就报 0x80041021（INVALID_NAMESPACE），**静默拿不到任何行**。
    /// </summary>
    private static List<Dictionary<string, object?>> Rows(string scopePath, string query, string[] fields)
    {
        var result = new List<Dictionary<string, object?>>();
        try
        {
            var scope = new ManagementScope(scopePath);
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var f in fields)
                {
                    try { row[f] = mo[f]; }
                    catch { row[f] = null; }
                }
                result.Add(row);
                mo.Dispose();
            }
        }
        catch
        {
            // 单个命名空间不可用不影响其他部分
        }
        return result;
    }

    /// <summary>
    /// 从 WMI 关联类的引用串里抠出对象键：
    /// `…Win32_DiskDrive.DeviceID="\\.\PHYSICALDRIVE0"` → `PHYSICALDRIVE0`；
    /// `…LogicalDisk.DeviceID="C:"` → `C:`；分区 → 原样。
    /// </summary>
    private static string WmiKey(string? refPath)
    {
        if (string.IsNullOrEmpty(refPath)) return "";
        var m = Regex.Match(refPath, "DeviceID=\"([^\"]*)\"");
        if (!m.Success) return "";
        var v = m.Groups[1].Value;
        var m2 = Regex.Match(v, @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
        return m2.Success ? $"PHYSICALDRIVE{m2.Groups[1].Value}" : v;
    }

    private static string? Str(object? v) => v?.ToString();

    private static double? Num(object? v)
    {
        if (v is null) return null;
        try { return Convert.ToDouble(v); }
        catch { return null; }
    }
}
