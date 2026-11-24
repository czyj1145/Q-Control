using System.Globalization;
using System.Management;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QControl.Core.Devices;
using QControl.Core.Model;
using QControl.Hal.Nvml;
using QControl.Hal.Sensors;

namespace QControl.Hal.Sys;

/// <summary>
/// 硬件档案采集（`GET /api/hardware`）。
///
/// 这一页是**静态档案**（型号 / 序列号 / 固件 / 内存料号 / 面板型号），
/// 不是实时读数；所以采集一次就缓存，刷新要显式请求。
///
/// 输出形状必须逐字对齐：`{ok, at, took_ms, groups:[{id, name, rows:[{k,v,sub?,mono?,adv?}]}]}`
///   · `adv=true` = 进阶信息，界面默认折叠（值照旧传给界面，报修要的编号一个都不能丢）；
///   · 取不到值的行**整行不出现**（`Row()` 返回 null，`Collect()` 统一筛掉），
///     绝不留 `--` 占位。
/// </summary>
public static class HardwareProfile
{
    /// <summary>分组顺序 = 界面上的先后（小白看得懂的在前，纯进阶的排最后）。</summary>
    private static readonly string[] GroupOrder = ["sys", "cpu", "mem", "gpu", "disk", "display", "net", "ec"];

    private static readonly Dictionary<int, string> MemType = new()
    {
        [20] = "DDR", [21] = "DDR2", [22] = "DDR2 FB-DIMM", [24] = "DDR3",
        [26] = "DDR4", [27] = "LPDDR", [28] = "LPDDR2", [29] = "LPDDR3",
        [30] = "LPDDR4", [34] = "DDR5", [35] = "LPDDR5",
    };

    /// <summary>
    /// 采一次全量。`model` / `status` 可选：给了才带出「EC 与机型实测」那组
    /// 与几个动态字段（电源计划、当前档位）。
    /// </summary>
    public static JsonObject Collect(ModelStore? model = null, JsonObject? status = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 八组之间没有先后依赖 ⇒ **并行**（2026-11-08）。
        // 之前是串行：19 条 WMI 查询一条接一条，整轮 313~393 ms，大头全是等 WMI 往返。
        // 现在每组各跑各的（每组内部仍是串行，组内的查询有依赖顺序），整轮 ≈ 最慢那一组。
        // ⚠️ 线程池线程是 MTA，`System.Management` 在这里可用（采样线程本来就是 MTA）。
        // ⚠️ 字典必须并发：八个 Take 会同时写。
        var groups = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonObject>(
            StringComparer.Ordinal);

        void Take(Func<JsonObject> fn)
        {
            try
            {
                var g = fn();
                if ((g["rows"] as JsonArray)?.Count > 0) groups[(string)g["id"]!] = g;
            }
            catch
            {
                // 单个分组失败只丢它自己（界面上就是没有这张卡），不影响别的
            }
        }

        System.Threading.Tasks.Parallel.Invoke(
            () => Take(() => Machine(model, status)),
            () => Take(Cpu),
            () => Take(Mem),
            () => Take(() => Gpu(status)),
            () => Take(Disk),
            () => Take(Display),
            () => Take(Net),
            () => Take(() => EcGroup(model, status)));

        var ordered = new JsonArray();
        foreach (var k in GroupOrder)
            if (groups.TryGetValue(k, out var g)) ordered.Add(g);

        return new JsonObject
        {
            ["ok"] = true,
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["took_ms"] = (int)sw.ElapsedMilliseconds,
            ["groups"] = ordered,
        };
    }

    // ================================================================ 主板

    private static JsonObject Machine(ModelStore? model, JsonObject? status)
    {
        var r = new Rows();

        foreach (var c in Q("root\\cimv2",
                     "SELECT Name, Vendor, IdentifyingNumber, UUID FROM Win32_ComputerSystemProduct",
                     "Name", "Vendor", "IdentifyingNumber", "UUID").Take(1))
        {
            r.Add(Row("型号", JoinSpace(S(c, "Vendor"), S(c, "Name"))));
            r.Add(Row("序列号", S(c, "IdentifyingNumber"), mono: true, adv: true));
            r.Add(Row("整机 UUID", S(c, "UUID"), mono: true, adv: true));
        }

        foreach (var o in Q("root\\cimv2",
                     "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, CSName"
                     + " FROM Win32_OperatingSystem",
                     "Caption", "Version", "BuildNumber", "OSArchitecture", "InstallDate", "CSName").Take(1))
        {
            var cap = S(o, "Caption");
            var build = S(o, "BuildNumber");
            r.Add(Row("系统", cap + (build.Length > 0 ? $"（Build {build}）" : "")));
            r.Add(Row("计算机名", S(o, "CSName")));
            r.Add(Row("系统内部版本", S(o, "Version"), mono: true, adv: true));
            r.Add(Row("系统架构", S(o, "OSArchitecture"), adv: true));
            r.Add(Row("安装日期", Date8(S(o, "InstallDate")), adv: true));
        }

        foreach (var b in Q("root\\cimv2",
                     "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion,"
                     + " SMBIOSMinorVersion FROM Win32_BIOS",
                     "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate",
                     "SMBIOSMajorVersion", "SMBIOSMinorVersion").Take(1))
        {
            var d = Date8(S(b, "ReleaseDate"));
            r.Add(Row("BIOS 版本", S(b, "SMBIOSBIOSVersion"), mono: true,
                      sub: d.Length > 0 ? d + " 发布" : null, adv: true));
            r.Add(Row("BIOS 厂商", S(b, "Manufacturer"), adv: true));
            if (b.GetValueOrDefault("SMBIOSMajorVersion") is not null
                && b.GetValueOrDefault("SMBIOSMinorVersion") is not null)
                r.Add(Row("SMBIOS", $"{b["SMBIOSMajorVersion"]}.{b["SMBIOSMinorVersion"]}", adv: true));
        }

        foreach (var b in Q("root\\cimv2",
                     "SELECT Manufacturer, Product, Version, SerialNumber FROM Win32_BaseBoard",
                     "Manufacturer", "Product", "Version", "SerialNumber").Take(1))
        {
            r.Add(Row("主板", JoinSpace(S(b, "Manufacturer"), S(b, "Product")), adv: true));
            r.Add(Row("主板版本", S(b, "Version"), adv: true));
            r.Add(Row("主板序列号", S(b, "SerialNumber"), mono: true, adv: true));
        }

        if (status?["power_plan"] is JsonObject gp)
        {
            var nm = Str(gp["name"]) ?? Str(gp["active"]);
            if (!string.IsNullOrEmpty(nm)) r.Add(Row("电源计划", nm, adv: true));
        }

        // ⚠️ 必须用 `model.FactorySpecs`（= model.json **顶层**的 factory_specs 根），
        //    不能用 `Get("factory_specs")`：`ModelStore.Get()` 是从 **presets 起**找路径的
        //    （`Get("fan","min_duty")`），再找 "factory_specs" 永远为空。
        //    早先实现原来是 `_mget(model, "factory_specs")`，同一个错 ⇒ 这行从来没出现过。
        //    2026-10-20 两边一起修好（见 src/README「三个已知缺口」）。
        var fs = model?.FactorySpecs;
        if (fs is not null && fs.Count > 0)
        {
            var src = Str(fs["source"]);
            var keys = fs["registry_keys"] as JsonArray;
            if (!string.IsNullOrEmpty(src))
                r.Add(Row("机型配置来源", src,
                          sub: keys is { Count: > 0 } ? $"从注册表探到 {keys.Count} 个键" : null,
                          adv: true));
        }

        try
        {
            // ⚠️ 起点走 `BootClock`，不能拿 `TickCount64` 反推：那个计数跨快速启动 /
            //    睡眠唤醒不归零，「刚开机」会显示出「2 天」（2026-10-23 实测）。
            if (BootClock.Current() is { } stamp)
            {
                var up = (long)(DateTime.Now - stamp.Start).TotalSeconds;
                if (up > 0)
                {
                    var d = up / 86400;
                    var rem = up % 86400;
                    var h = rem / 3600;
                    var m = (rem % 3600) / 60;
                    r.Add(Row("本次已运行",
                              (d > 0 ? $"{d} 天 " : "") + $"{h} 小时 {m} 分",
                              sub: "自 " + stamp.Start.ToString("yyyy-MM-dd HH:mm",
                                                                CultureInfo.InvariantCulture) + " 起"));
                }
            }
        }
        catch { /* 取不到就不放这行 */ }

        return Group("sys", "主板", r);
    }

    // ================================================================ 处理器

    private static JsonObject Cpu()
    {
        var r = new Rows();
        foreach (var p in Q("root\\cimv2",
                     "SELECT Name, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors,"
                     + " MaxClockSpeed, L2CacheSize, L3CacheSize, AddressWidth, DataWidth,"
                     + " VirtualizationFirmwareEnabled, ProcessorId FROM Win32_Processor",
                     "Name", "SocketDesignation", "NumberOfCores", "NumberOfLogicalProcessors",
                     "MaxClockSpeed", "L2CacheSize", "L3CacheSize", "AddressWidth", "DataWidth",
                     "VirtualizationFirmwareEnabled", "ProcessorId").Take(2))
        {
            r.Add(Row("型号", S(p, "Name")));

            var cores = p.GetValueOrDefault("NumberOfCores");
            var thr = p.GetValueOrDefault("NumberOfLogicalProcessors");
            if (Truthy(cores) && Truthy(thr))
                r.Add(Row("核心 / 线程", $"{cores} 核 / {thr} 线程"));

            r.Add(Row("基准频率", Mhz(p.GetValueOrDefault("MaxClockSpeed"))));
            r.Add(Row("插槽", S(p, "SocketDesignation"), adv: true));

            if (ToDouble(p.GetValueOrDefault("L3CacheSize")) is > 0 and var l3)
                r.Add(Row("L3 缓存", $"{l3 / 1024.0:F1} MB", adv: true));
            if (ToDouble(p.GetValueOrDefault("L2CacheSize")) is > 0 and var l2)
                r.Add(Row("L2 缓存", $"{l2 / 1024.0:F1} MB", adv: true));

            if (ToDouble(p.GetValueOrDefault("AddressWidth")) is > 0 and var aw)
                r.Add(Row("架构", $"{(int)aw} 位", adv: true));

            // ⚠️ 只有**拿到值**才写这一行：null 与 false 意义完全不同
            var vtf = p.GetValueOrDefault("VirtualizationFirmwareEnabled");
            if (vtf is not null)
                r.Add(Row("虚拟化", Truthy(vtf) ? "已启用" : "未启用", adv: true));

            r.Add(Row("处理器 ID", S(p, "ProcessorId"), mono: true, adv: true));
        }
        return Group("cpu", "处理器", r);
    }

    // ================================================================ 内存

    private static JsonObject Mem()
    {
        var r = new Rows();

        var total = SystemSensors.TotalPhysicalBytes();
        if (total > 0) r.Add(Row("总容量", Gb(total, 1)));

        var sticks = Q("root\\cimv2",
            "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber,"
            + " SerialNumber, DeviceLocator, BankLabel, SMBIOSMemoryType, ConfiguredVoltage"
            + " FROM Win32_PhysicalMemory",
            "Capacity", "Speed", "ConfiguredClockSpeed", "Manufacturer", "PartNumber",
            "SerialNumber", "DeviceLocator", "BankLabel", "SMBIOSMemoryType", "ConfiguredVoltage");

        if (sticks.Count > 0)
        {
            r.Add(Row("插槽", $"已用 {sticks.Count} 个"));
            for (var i = 1; i <= sticks.Count; i++)
            {
                var m = sticks[i - 1];
                var cap = m.GetValueOrDefault("Capacity");
                var rated = SafeInt(m.GetValueOrDefault("Speed"));
                var cfg = SafeInt(m.GetValueOrDefault("ConfiguredClockSpeed"));
                var kind = MemType.GetValueOrDefault(SafeInt(m.GetValueOrDefault("SMBIOSMemoryType")), "");
                var spd = (cfg > 0 || rated > 0) ? $"{Math.Max(cfg, rated)} MT/s" : "";
                var mfr = S(m, "Manufacturer");

                r.Add(Row($"内存条 {i}", Join(kind, Gb(cap, 0), spd, mfr)));

                var volt = SafeInt(m.GetValueOrDefault("ConfiguredVoltage"));
                var sn = S(m, "SerialNumber");
                var adv = Join(S(m, "PartNumber"),
                               sn.Length > 0 ? "SN " + sn : "",
                               volt > 0 ? $"{volt} mV" : "");
                if (adv.Length > 0)
                    r.Add(Row($"内存条 {i} 料号", adv, mono: true, adv: true));

                if (cfg > 0 && rated > 0 && cfg != rated)
                    r.Add(Row($"内存条 {i} 额定频率", $"{rated} MT/s", adv: true));
            }

            var arr = Q("root\\cimv2", "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray",
                        "MemoryDevices").Take(1).ToList();
            if (arr.Count > 0)
            {
                var n = SafeInt(arr[0].GetValueOrDefault("MemoryDevices"));
                if (n > 0) r.Add(Row("插槽总数", $"{n} 个", adv: true));
            }
        }
        return Group("mem", "内存", r);
    }

    // ================================================================ 显卡

    private static JsonObject Gpu(JsonObject? status)
    {
        var r = new Rows();

        // 只认挂在 PCI 上的物理显卡：MuMu / GameViewer / Parsec 这类虚拟显示适配器
        // 是 ROOT\DISPLAY\...，混进来会让人以为"有两块显卡"
        var phys = Q("root\\cimv2",
                "SELECT Name, PNPDeviceID, DriverVersion, DriverDate, AdapterRAM"
                + " FROM Win32_VideoController",
                "Name", "PNPDeviceID", "DriverVersion", "DriverDate", "AdapterRAM")
            .Where(v => S(v, "PNPDeviceID").ToUpperInvariant().StartsWith("PCI", StringComparison.Ordinal))
            .OrderBy(v => S(v, "PNPDeviceID").ToUpperInvariant().Contains("VEN_10DE") ? 0 : 1)
            .ToList();

        // 静态字段走**进程内 NVML**（2026-11-08）：型号/驱动/显存总量/VBIOS/PCIe 上限
        // 都是装机后就不再变的东西，原来为了它们起一个 `nvidia-smi` 子进程
        // （实测 58 ms，占整轮采集约 17%）。
        // ⚠️ 只有 NVML **完全不可用**（没驱动 / Optimus 关掉 dGPU）才回落 smi ——
        //    与 NvmlSensors.Read 的策略一致：NVML 是快路，不是唯一的路。
        var nv = NvmlSensors.StaticInfo();
        string[] parts;
        if (nv is not null)
        {
            parts = [];
        }
        else
        {
            var smi = Shell.RunText("nvidia-smi",
                "--query-gpu=name,driver_version,memory.total,vbios_version,"
                + "pcie.link.gen.max,pcie.link.width.max --format=csv,noheader,nounits");
            parts = smi.Length > 0
                ? smi.Split('\n')[0].Split(',').Select(x => x.Trim()).ToArray()
                : [];
        }

        // NVML 的三项（显存总量 / VBIOS / PCIe 上限）；取不到就是 null
        var nvMem = nv?["mem_total"] is { } nm ? ToDouble(nm) : null;
        var nvGen = nv?["pcie_gen"] is { } ng ? ToDouble(ng) : null;
        var nvWidth = nv?["pcie_width"] is { } nw ? ToDouble(nw) : null;
        var nvVbios = nv?["vbios"] is JsonValue vv && vv.TryGetValue<string>(out var vb) ? vb : "";

        foreach (var v in phys)
        {
            var name = S(v, "Name");
            var pnp = S(v, "PNPDeviceID").ToUpperInvariant();

            // ⚠️ 别再用 `VEN_1002` 判独显（2026-11-12 修）：1002 是 AMD **全部**显卡的厂商 ID，
            //    核显（Radeon 780M）与独显（RX 7600M）都是它 ⇒ AMD 核显会被标成"独立显卡"，
            //    同一台机器上出现两张"独立显卡"。判定统一走 GpuClassification（会看型号名）。
            var kind = GpuClassification.Of(VenOf(pnp), name);
            var isDgpu = kind == GpuKind.Discrete;
            var label = kind switch
            {
                GpuKind.Discrete => "独立显卡",
                GpuKind.Integrated => "集成显卡",
                _ => "显卡",                 // 判不出来就别下结论
            };

            r.Add(Row(label, name));

            if (isDgpu && nvMem is { } nvm && nvm > 0)
                r.Add(Row(label + " 显存", $"{nvm / 1024.0:F0} GB"));
            else if (isDgpu && parts.Length > 2 && parts[2].Length > 0)
            {
                var mem = SafeInt(parts[2]);
                r.Add(Row(label + " 显存", $"{mem / 1024.0:F0} GB"));
            }
            else if (v.GetValueOrDefault("AdapterRAM") is { } ram && IsTrustworthyAdapterRam(ram))
            {
                // AdapterRAM 是 uint32：≥4 GiB 会回绕（8 GB→0、6 GB→2 GB），显示出来是假值。
                // 不满足就整行不给（界面按既有约定隐藏），别摆个 "0 GB"。
                r.Add(Row(label + " 显存", Gb(ram, 0)));
            }

            var drv = S(v, "DriverVersion");
            var dd = Date8(S(v, "DriverDate"));
            if (drv.Length > 0)
                r.Add(Row(label + " 驱动", drv, mono: true,
                          sub: dd.Length > 0 ? dd + " 发布" : null));

            var vbios = nvVbios.Length > 0 ? nvVbios : (parts.Length > 3 ? parts[3] : "");
            if (isDgpu && vbios.Length > 0)
                r.Add(Row("VBIOS", vbios, mono: true, adv: true));

            if (isDgpu && nvGen is > 0 && nvWidth is > 0)
                r.Add(Row("PCIe 上限", $"Gen{nvGen:F0} ×{nvWidth:F0}", adv: true));
            else if (isDgpu && parts.Length > 5 && parts[4].Length > 0 && parts[5].Length > 0)
                r.Add(Row("PCIe 上限", $"Gen{parts[4]} ×{parts[5]}", adv: true));

            r.Add(Row(label + " 设备 ID", pnp, mono: true, adv: true));
        }

        if (status?["gpu_info"] is JsonObject gi
            && Truthy(gi["pcie_gen"]) && Truthy(gi["pcie_width"]))
            r.Add(Row("PCIe 当前", $"Gen{gi["pcie_gen"]} ×{gi["pcie_width"]}", adv: true));

        return Group("gpu", "显卡", r);
    }

    // ================================================================ 存储

    private static JsonObject Disk()
    {
        var r = new Rows();
        var disks = Q("root\\cimv2",
            "SELECT Model, Size, InterfaceType, SerialNumber, FirmwareRevision, Partitions"
            + " FROM Win32_DiskDrive",
            "Model", "Size", "InterfaceType", "SerialNumber", "FirmwareRevision", "Partitions");

        for (var i = 1; i <= disks.Count; i++)
        {
            var d = disks[i - 1];
            var mdl = S(d, "Model");
            if (mdl.Length == 0) continue;

            var iface = S(d, "InterfaceType");
            if (iface == "SCSI") iface = "NVMe / SCSI";    // 本机 NVMe 盘在 WMI 里报 SCSI

            r.Add(Row($"硬盘 {i}", Join(mdl, Gb(d.GetValueOrDefault("Size"), 0), iface)));
            r.Add(Row($"硬盘 {i} 固件", S(d, "FirmwareRevision"), mono: true, adv: true));
            r.Add(Row($"硬盘 {i} 序列号", S(d, "SerialNumber"), mono: true, adv: true));

            if (Truthy(d.GetValueOrDefault("Partitions")))
                r.Add(Row($"硬盘 {i} 分区数",
                          $"{SafeInt(d.GetValueOrDefault("Partitions"))} 个", adv: true));
        }
        return Group("disk", "存储", r);
    }

    // ================================================================ 屏幕

    private static JsonObject Display()
    {
        var r = new Rows();

        foreach (var m in Q("root\\wmi",
                     "SELECT ManufacturerName, ProductCodeID, UserFriendlyName, SerialNumberID,"
                     + " YearOfManufacture, WeekOfManufacture FROM WmiMonitorID",
                     "ManufacturerName", "ProductCodeID", "UserFriendlyName",
                     "SerialNumberID", "YearOfManufacture", "WeekOfManufacture").Take(1))
        {
            var mfr = S(Edid(m.GetValueOrDefault("ManufacturerName")));
            var pid = S(Edid(m.GetValueOrDefault("ProductCodeID")));
            var ufn = S(Edid(m.GetValueOrDefault("UserFriendlyName")));
            var sn = S(Edid(m.GetValueOrDefault("SerialNumberID")));

            r.Add(Row("屏幕型号", ufn.Length > 0 ? ufn : JoinSpace(mfr, pid)));

            var y = SafeInt(m.GetValueOrDefault("YearOfManufacture"));
            var w = SafeInt(m.GetValueOrDefault("WeekOfManufacture"));
            if (y > 0)
                r.Add(Row("生产批次", $"{y} 年" + (w > 0 ? $" 第 {w} 周" : ""), adv: true));

            if (mfr.Length > 0 || pid.Length > 0)
                r.Add(Row("EDID 编号", JoinSpace(mfr, pid), mono: true, adv: true));

            if (sn.Length > 0) r.Add(Row("面板序列号", sn, mono: true, adv: true));
        }

        foreach (var b in Q("root\\wmi",
                     "SELECT MaxHorizontalImageSize, MaxVerticalImageSize"
                     + " FROM WmiMonitorBasicDisplayParams",
                     "MaxHorizontalImageSize", "MaxVerticalImageSize").Take(1))
        {
            var hw = SafeInt(b.GetValueOrDefault("MaxHorizontalImageSize"));
            var hh = SafeInt(b.GetValueOrDefault("MaxVerticalImageSize"));
            if (hw > 0 && hh > 0)
            {
                var inch = Math.Sqrt(hw * hw + hh * hh) / 2.54;
                r.Add(Row("屏幕尺寸", $"{inch:F1} 英寸", sub: $"可视区约 {hw} × {hh} cm"));
            }
        }

        // ⚠️ 不能只看第一块显卡：第一块可能是虚拟显示器或已断开的输出，
        //    分辨率为空。早先实现是"遍历到第一块有值的为止"，这里同语义。
        foreach (var v in Q("root\\cimv2",
                     "SELECT CurrentHorizontalResolution, CurrentVerticalResolution,"
                     + " CurrentRefreshRate FROM Win32_VideoController",
                     "CurrentHorizontalResolution", "CurrentVerticalResolution",
                     "CurrentRefreshRate"))
        {
            var wd = v.GetValueOrDefault("CurrentHorizontalResolution");
            var ht = v.GetValueOrDefault("CurrentVerticalResolution");
            var hz = v.GetValueOrDefault("CurrentRefreshRate");
            if (!Truthy(wd) || !Truthy(ht)) continue;
            r.Add(Row("分辨率", $"{wd} × {ht}" + (Truthy(hz) ? $" @ {hz} Hz" : "")));
            break;
        }

        return Group("display", "屏幕", r);
    }

    // ================================================================ 网络

    /// <summary>
    /// 网络组：**先走原生 `iphlpapi`**（见 <see cref="NativeNet"/>），读不出东西才回落 WMI。
    ///
    /// 2026-11-08 实测：`Win32_NetworkAdapter` 一条查询 = **280 ms**，是整轮采集唯一的瓶颈
    /// （其余七组并行只要 60 ms）。换成原生 API 后整组几毫秒。
    /// ⚠️ 回落路径必须留着：老系统 / 权限异常 / 结构体对不上时，宁可慢也不能空。
    /// </summary>
    private static JsonObject Net()
    {
        var nics = NativeNet.Read();
        if (nics.Count == 0) return NetWmi();

        var r = new Rows();
        foreach (var n in nics)
        {
            // 类型判据比名字可靠：71 = 802.11，6 = 以太网（名字里有没有 "LAN" 靠不住）
            var label = NativeNet.IsWireless(n.IfType) ? "无线网卡"
                : NativeNet.IsWired(n.IfType) ? "有线网卡"
                : "网卡";

            var rate = n.SpeedBps >= 1_000_000_000 ? $"{n.SpeedBps / 1e9:F1} Gbps"
                : n.SpeedBps > 0 ? $"{n.SpeedBps / 1e6:F0} Mbps" : "";

            var sub = Join(rate, n.Up ? "已连接" : "未连接",
                           n.Ip.Length > 0 ? "IP " + n.Ip : "", n.Connection);

            r.Add(Row(label, n.Description.Length > 0
                    ? n.Description
                    : (n.Connection.Length > 0 ? n.Connection : "网卡型号未知"),
                sub: sub.Length > 0 ? sub : null));

            var adv = Join(n.Mac.Length > 0 ? "MAC " + n.Mac : "",
                           n.Dhcp ? "DHCP 自动获取" : "手动设置 IP");
            if (adv.Length > 0) r.Add(Row(label + " 标识", adv, adv: true));
        }
        return Group("net", "网络", r);
    }

    /// <summary>
    /// 网络组的老路（WMI）。只在原生 API 读不出东西时用 —— 慢（~280 ms），但拿得到数据。
    ///
    /// 物理网卡判定：Win32_NetworkAdapter 里混着一堆虚拟适配器
    /// （WAN Miniport ×N、VPN、TAP、环回…），它们各自带 MAC，不过滤就会冒出
    /// 七八条"网卡"把真正那块淹掉。判据：PNP 必须 PCI 打头（虚拟的是 ROOT\）。
    /// </summary>
    private static bool IsPhysNic(string name, string pnp)
    {
        if (!pnp.StartsWith("PCI", StringComparison.Ordinal)) return false;
        var low = name.ToLowerInvariant();
        string[] junk = ["wan miniport", "virtual", "vpn", "tap", "loopback", "microsoft", "bluetooth"];
        return !junk.Any(b => low.Contains(b, StringComparison.Ordinal));
    }

    private static JsonObject NetWmi()
    {
        var r = new Rows();

        var cfg = new Dictionary<int, Dictionary<string, object?>>();
        foreach (var c in Q("root\\cimv2",
                     "SELECT Index, MACAddress, IPAddress, IPEnabled, DHCPEnabled"
                     + " FROM Win32_NetworkAdapterConfiguration",
                     "Index", "MACAddress", "IPAddress", "IPEnabled", "DHCPEnabled"))
            cfg[SafeInt(c.GetValueOrDefault("Index"))] = c;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in Q("root\\cimv2",
                     // `WHERE PNPDeviceID LIKE 'PCI%'` 在 WMI 侧就筛掉虚拟适配器：
                     // 这台机器上 Win32_NetworkAdapter 有 15 个实例，物理网卡只有 2 个。
                     "SELECT Index, Name, NetConnectionID, MACAddress, Speed, NetEnabled,"
                     + " PNPDeviceID FROM Win32_NetworkAdapter WHERE PNPDeviceID LIKE 'PCI%'",
                     "Index", "Name", "NetConnectionID", "MACAddress", "Speed",
                     "NetEnabled", "PNPDeviceID"))
        {
            var model = S(a, "Name");                   // Intel(R) Wi-Fi 6E AX211 160MHz
            var conn = S(a, "NetConnectionID");         // WLAN / 以太网
            var pnp = S(a, "PNPDeviceID").ToUpperInvariant();
            if (!IsPhysNic(model, pnp)) continue;

            var mac = S(a, "MACAddress").ToUpperInvariant();
            if (mac.Length > 0 && !seen.Add(mac)) continue;

            var hay = (conn + " " + model).ToLowerInvariant();
            string label;
            if (new[] { "wlan", "wi-fi", "wifi", "wireless", "无线" }.Any(k => hay.Contains(k, StringComparison.Ordinal)))
                label = "无线网卡";
            else if (new[] { "ethernet", "以太", "gbe", "lan" }.Any(k => hay.Contains(k, StringComparison.Ordinal)))
                label = "有线网卡";
            else
                label = "网卡";

            var c = cfg.GetValueOrDefault(SafeInt(a.GetValueOrDefault("Index")));

            var ip = "";
            if (c?.GetValueOrDefault("IPAddress") is string[] ips)
                ip = ips.FirstOrDefault(x => !x.Contains(':')) ?? "";

            var sp = SafeInt(a.GetValueOrDefault("Speed"));
            var state = Truthy(a.GetValueOrDefault("NetEnabled")) ? "已连接" : "未连接";
            var rate = sp >= 1_000_000_000 ? $"{sp / 1e9:F1} Gbps"
                       : sp > 0 ? $"{sp / 1e6:F0} Mbps" : "";

            var sub = Join(rate, state, ip.Length > 0 ? "IP " + ip : "", conn);
            r.Add(Row(label, model.Length > 0 ? model : (conn.Length > 0 ? conn : "网卡型号未知"),
                      sub: sub.Length > 0 ? sub : null));

            var adv = Join(mac.Length > 0 ? "MAC " + mac : "",
                           c is not null ? (Truthy(c.GetValueOrDefault("DHCPEnabled"))
                               ? "DHCP 自动获取" : "手动设置 IP") : "");
            if (adv.Length > 0) r.Add(Row(label + " 标识", adv, adv: true));
        }

        return Group("net", "网络", r);
    }

    // ================================================================ EC 与机型实测

    private static JsonObject EcGroup(ModelStore? model, JsonObject? status)
    {
        var r = new Rows();
        // ⚠️ 取 **presets 根**，不是 `Get("presets")`：`ModelStore.Get()` 从 presets 起找路径，
        //    再找一层 "presets" = `presets["presets"]`，永远为空 ⇒ 这一组永远不产出。
        //    早先实现原来是 `_mget(model, "presets", {})`，同一个错，整组"EC 与机型实测"
        //    因此从来没在界面上出现过（代码在、逻辑对、就是取不到数据）。
        //    2026-10-20 两边一起修好：早先实现改用 `_presets()`，这里改用 `model.Presets`。
        var presets = model?.Presets;
        if (presets is null)
            return Group("ec", "EC 与机型实测", r);

        var modes = (presets["perf"] as JsonObject)?["modes"] as JsonObject ?? new JsonObject();
        var apmd = new List<string>();
        var wall = new List<string>();
        foreach (var (key, label) in new[] { ("office", "办公"), ("game", "游戏"), ("beast", "狂暴") })
        {
            if (modes[key] is not JsonObject m) continue;
            if (m["apmd"] is not null)
                apmd.Add($"{label} 0x{SafeInt(m["apmd"]):X2}");
            if (m["power"] is JsonObject pw && pw["cpu_pl1"] is not null)
                wall.Add($"{label} {ToDouble(pw["cpu_pl1"]) ?? 0:F0} W");
        }
        if (apmd.Count > 0)
            r.Add(Row("系统模式 ↔ EC", string.Join(" · ", apmd),
                      sub: "写的是 EC 的 APMD 寄存器（原厂同款）", adv: true));
        if (wall.Count > 0)
            r.Add(Row("CPU 功耗墙（实测）", string.Join(" · ", wall),
                      sub: "长时墙 PL1；由 EC 的 APMD 决定，跟 Windows 电源计划无关", adv: true));

        var gfx = (presets["gfx"] as JsonObject)?["modes"] as JsonObject;
        if (gfx is not null)
        {
            var g = new List<string>();
            foreach (var code in gfx.Select(kv => kv.Key).OrderBy(SafeInt))
            {
                if (gfx[code] is not JsonObject e) continue;
                var nm = Str(e["short"]) ?? Str(e["name"]);
                if (!string.IsNullOrEmpty(nm)) g.Add($"{nm} {code}");
            }
            if (g.Count > 0)
                r.Add(Row("显卡模式 ↔ EC", string.Join(" · ", g),
                          sub: "写 0xFB00 写组；必须重启才由固件应用到显示输出", adv: true));
        }

        var bl = presets["backlight"] as JsonObject;
        if (bl is not null)
        {
            var zs = bl["zones"] as JsonArray;
            // ⚠️ 分区的 addr 是**数字**（5/4/3），不是字符串 —— 用 `Str()` 会全取成空串，
            //    于是这一行退化成「背光地址 6」，和早先实现（A=5 · B=4 · C=3）对不上。
            var addrs = zs?.OfType<JsonObject>()
                          .Select(z => Scalar(z["addr"]))
                          .Where(x => x.Length > 0).ToList();
            if (addrs is { Count: > 0 })
                r.Add(Row("背光分区地址",
                          string.Join(" · ", addrs.Take(3).Select((a, i) => $"{"ABC"[i]}={a}")),
                          sub: Truthy(bl["zone_addrs_confirmed"]) ? "已实测标定" : "尚未标定",
                          adv: true));
            else if (bl["addr"] is not null)
                r.Add(Row("背光地址", Scalar(bl["addr"]), adv: true));
        }

        if (presets["fan"] is JsonObject fan)
        {
            var bits = new List<string>();
            if (fan["temp_wall"] is not null) bits.Add($"保护墙 {fan["temp_wall"]} °C");
            if (fan["min_duty"] is not null) bits.Add($"转速下限 {fan["min_duty"]}%");
            if (fan["hysteresis"] is not null) bits.Add($"迟滞 {fan["hysteresis"]} °C");
            if (bits.Count > 0) r.Add(Row("风扇曲线参数", string.Join(" · ", bits), adv: true));
        }

        if (presets["ec"] is JsonObject ec && ec["gap"] is not null)
            r.Add(Row("EC 写入节流", $"{ToDouble(ec["gap"]) ?? 0:F2} s",
                      sub: "两次 EC 写入之间的最小间隔（下限 0.20 s）", adv: true));

        if (status is not null)
        {
            var cur = new List<string>();
            if (status["perf"] is JsonObject pf)
            {
                if (Str(pf["mode"]) is { Length: > 0 } m) cur.Add($"当前档位 {m}");
                if (pf["apmd"] is not null) cur.Add($"APMD 0x{SafeInt(pf["apmd"]):X2}");
            }
            if (status["gfx_mode"] is not null) cur.Add($"GFXT {status["gfx_mode"]}");
            if (cur.Count > 0) r.Add(Row("EC 当前状态", string.Join(" · ", cur), adv: true));
        }

        return Group("ec", "EC 与机型实测", r);
    }

    // ================================================================ 基础工具

    /// <summary>
    /// 查一组对象 → 字段字典列表。失败返回空列表（= 界面不显示这一组）。
    ///
    /// ⚠️ 必须显式 `new ManagementScope`：`new ManagementObjectSearcher(string, ObjectQuery)`
    ///    在 C# 里没有这个重载（会命中 (string,string) 的歧义，报"无法从 string 转"）。
    /// </summary>
    private static List<Dictionary<string, object?>> Q(string ns, string query, params string[] fields)
    {
        var list = new List<Dictionary<string, object?>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\" + ns), new ObjectQuery(query));
            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
            {
                using (mo)
                {
                    var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var f in fields)
                    {
                        try { d[f] = mo[f]; } catch { d[f] = null; }
                    }
                    list.Add(d);
                }
            }
        }
        catch { /* 命名空间/类不存在 → 这一组空 */ }
        return list;
    }

    private static string S(Dictionary<string, object?> d, string key) => S(d.GetValueOrDefault(key));

    /// <summary>WMI 常见 None / 空白 / "To be filled by O.E.M." 之类占位值一律当没有。</summary>
    private static string S(object? v)
    {
        if (v is null) return "";
        var t = v.ToString()?.Trim() ?? "";
        if (t.Length == 0) return "";
        var low = t.ToLowerInvariant();

        if (Regex.IsMatch(low, @"^type\d+[a-z]*$")) return "";     // Type1SkuNumber…

        string[] junk =
        [
            "to be filled", "default string", "none", "unknown", "not specified",
            "o.e.m", "system serial number", "base board product", "00000000", "0123456789",
        ];
        if (junk.Any(j => low == j || low.StartsWith(j, StringComparison.Ordinal))) return "";

        // ⚠️ 单个 "0" 只能**全等**比较：旧代码把它塞进上面那组走 startswith，
        //    等于"以 0 开头一律当垃圾" —— MAC 的 OUI 基本都是 00:…，
        //    UUID / ProcessorId / 部分序列号也常以 0 开头，整行数据会静默消失。
        if (low == "0") return "";
        return t;
    }

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>
    /// 把任意标量节点转成界面文本（字符串原样、数字不带引号）。
    ///
    /// ⚠️ 与 <see cref="Str"/> 的区别：`Str()` **只认字符串**，配置里写成数字的字段
    ///    （`backlight.zones[].addr` 是 5/4/3）会被它取成空串。早先实现的 `_s()`
    ///    是 `str(v).strip()`，数字照样出文本 —— 这里是同一个口径。
    /// </summary>
    private static string Scalar(JsonNode? n)
    {
        if (n is not JsonValue v) return "";
        if (v.TryGetValue<string>(out var s)) return s.Trim();
        try { return n.ToJsonString(); }
        catch { return ""; }
    }

    private static double? ToDouble(object? v)
    {
        if (v is null) return null;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    private static double? ToDouble(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.GetValueKind() != System.Text.Json.JsonValueKind.Number) return null;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int SafeInt(object? v)
    {
        if (v is null) return 0;
        try { return (int)Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static int SafeInt(string? s)
        => int.TryParse(s, out var v) ? v : 0;

    private static int SafeInt(JsonNode? n) => (int)(ToDouble(n) ?? 0);

    /// <summary>「真值」判定（0 是假、空串是假、null 是假）。</summary>
    private static bool Truthy(object? v)
    {
        if (v is null) return false;
        if (v is bool b) return b;
        if (v is string s) return s.Length > 0;
        try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0; }
        catch { return true; }
    }

    private static bool Truthy(JsonNode? n) => Device.Truthy(n);

    private static string Join(params string?[] parts)
        => string.Join(" · ", parts.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>空格分隔版（早先实现里那几处用的是 `" ".join(...)`，别和 `·` 混用）。</summary>
    private static string JoinSpace(params string?[] parts)
        => string.Join(" ", parts.Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>字节 → GB 字符串（与早先实现 `f"{n/1024**3:,.{nd}f} GB"` 同格式）。</summary>
    private static string Gb(object? n, int nd)
    {
        if (ToDouble(n) is not { } v) return "";
        return (v / (1024.0 * 1024 * 1024)).ToString("N" + nd, CultureInfo.InvariantCulture) + " GB";
    }

    /// <summary>从 `PCI\VEN_10DE&amp;DEV_…` 里抠出 4 位厂商 ID；抠不到给空串。</summary>
    private static string VenOf(string pnp)
    {
        const string Tag = "VEN_";
        var i = pnp.IndexOf(Tag, StringComparison.Ordinal);
        return i >= 0 && pnp.Length >= i + Tag.Length + 4 ? pnp.Substring(i + Tag.Length, 4) : "";
    }

    /// <summary>
    /// `Win32_VideoController.AdapterRAM` 是 **uint32**：4 GiB 及以上的显存会回绕
    /// （8 GB → 0、12 GB → 4 GB、6 GB → 2 GB），直接显示就是假值。
    /// 只信 (0, 4 GiB) 这个区间；超出就整行不给 —— 拿不到比拿错强（2026-11-12 修）。
    /// </summary>
    private static bool IsTrustworthyAdapterRam(object? ram)
        => ToDouble(ram) is { } v && v > 0 && v < 4.0 * 1024 * 1024 * 1024;

    private static string Mhz(object? n)
    {
        if (ToDouble(n) is not { } v) return "";
        return v >= 1000
            ? $"{v / 1000:F2} GHz"
            : $"{v:F0} MHz";
    }

    /// <summary>WMI 时间（CIM_DATETIME，YYYYMMDDHHMMSS…）→ 2026-10-15。</summary>
    private static string Date8(string s)
    {
        var t = S(s);
        if (t.Length >= 8 && t[..8].All(char.IsDigit))
            return $"{t[0..4]}-{t[4..6]}-{t[6..8]}";
        return t;
    }

    /// <summary>WmiMonitorID 的字段是一串 UInt16（UTF-16 的每个码元），要自己拼。</summary>
    private static string Edid(object? arr)
    {
        try
        {
            if (arr is ushort[] u)
                return new string(u.Where(c => c != 0).Select(c => (char)c).ToArray()).Trim();
            if (arr is Array a)
                return new string(a.Cast<object>()
                    .Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture))
                    .Where(c => c != 0).Select(c => (char)c).ToArray()).Trim();
        }
        catch { /* 形状不对就当没有 */ }
        return "";
    }

    private static JsonObject Group(string id, string name, Rows rows)
        => new() { ["id"] = id, ["name"] = name, ["rows"] = rows.ToArray() };

    /// <summary>一行数据。v 为空 → null（调用方筛掉 = 整行省略，绝不留 "--"）。</summary>
    private static JsonObject? Row(string k, string? v, string? sub = null,
                                   bool mono = false, bool adv = false)
    {
        var val = (v ?? "").Trim();
        if (val.Length == 0) return null;

        var o = new JsonObject { ["k"] = k, ["v"] = val };
        if (!string.IsNullOrEmpty(sub)) o["sub"] = sub;
        if (mono) o["mono"] = true;
        if (adv) o["adv"] = true;
        return o;
    }

    /// <summary>行容器：自动跳过 null（早先实现是 `[r for r in rows if r]`）。</summary>
    private sealed class Rows
    {
        private readonly List<JsonObject> _rows = [];
        public void Add(JsonObject? r) { if (r is not null) _rows.Add(r); }
        public JsonArray ToArray()
        {
            var arr = new JsonArray();
            foreach (var r in _rows) arr.Add(r);
            return arr;
        }
    }
}
