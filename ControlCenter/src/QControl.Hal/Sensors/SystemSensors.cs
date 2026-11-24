using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace QControl.Hal.Sensors;

/// <summary>
/// 内存 / 电池 / 网络 / 开机时长 —— 全部走系统原生接口。
///
/// 内存 / 电池 / 网络 / 系统时间四项读数，字段口径见各处注释。
/// 一条贯穿全类的规矩：**取不到就不给键**（界面整行隐藏），绝不用 0 顶替。
/// </summary>
public sealed class SystemSensors
{
    private readonly object _lock = new();
    private (double At, ulong Total)? _lastNet;
    private (double At, double Wh)? _fullWhCache;
    private (double At, double Wh)? _designWhCache;

    /// <summary>
    /// `BatteryStatus` 的 WMI 结果缓存（2026-11-02 加）。
    ///
    /// 为什么必须缓存：这一条以前**每秒查一次 WMI**（`root\wmi:BatteryStatus`，
    /// 每次还要 new 一个 `ManagementScope`）。实测整套快照的分配速率是 711 KB/s，
    /// 大头就是每拍这 2~3 条 WMI —— 每条走一趟 COM + 属性包 marshal，几十到上百 KB。
    /// 托管堆因此长期是"活着 3 MB、提交 25 MB"，任务管理器里那 50 MB 里的 20 多 MB 就是它。
    /// 电池的电压/容量/充放电功率本来就是**秒级不敏感**的量：5 秒一次完全不掉价，
    /// 而"插没插电"仍由 `GetSystemPowerStatus` 每拍现读（下面 <see cref="Battery"/>），
    /// 所以"拔电自动切办公"一秒都不慢。
    /// </summary>
    private (double At, JsonObject Value)? _bstCache;

    /// <summary>电池扩展读数的缓存时长（秒）。</summary>
    private const double BatteryStatusTtl = 5.0;

    /// <summary>网络累计字节的缓存时长（秒）—— 速率按这个窗口算，界面上看不出差别。</summary>
    private const double NetworkTtl = 3.0;

    /// <summary>上一次真正刷新时的结果（含算好的速率），TTL 内直接复用。</summary>
    private (double At, JsonObject Value)? _netCache;

    // ================================================================ 内存

    /// <summary>
    /// `{total, used, percent}`。
    /// ⚠️ 不下发 `available` —— 它与 percent 说的是同一件事的两面，摆两行只是重复。
    /// </summary>
    public JsonObject Memory()
    {
        var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref m)) return new JsonObject();

        var total = m.ullTotalPhys;
        var used = total - m.ullAvailPhys;

        // ⚠️ 不用 `dwMemoryLoad`：那是 Windows 的"近似占用率"，与
        //    `used/total` 能差 3~4 个百分点。psutil（早先实现）算的是
        //    `(total - available) / total`，这里必须用同一个口径，
        //    否则界面上两版会显示不同的内存占用率。
        var percent = total == 0 ? 0.0 : Math.Round(100.0 * used / total, 1);

        return new JsonObject
        {
            ["total"] = (long)total,
            ["used"] = (long)used,
            ["percent"] = percent,
        };
    }

    // ================================================================ 开机时长

    public JsonObject System()
        => new() { ["uptime_s"] = (long)BootClock.UptimeSeconds() };

    // ---------------------------------------------------------------- 静态辅助
    //
    // 硬件档案（`Sys/HardwareProfile`）只要这两个数，没必要为了它 new 一个实例
    // （实例里带着电池容量的缓存，凭空造一个只会多一份缓存）。

    /// <summary>物理内存总量（字节）。与 `Memory()["total"]` 同源。</summary>
    public static ulong TotalPhysicalBytes()
    {
        var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref m) ? m.ullTotalPhys : 0;
    }

    /// <summary>
    /// 本次开机 / 唤醒至今的秒数。与 `System()["uptime_s"]` 同源，判定口在
    /// <see cref="BootClock"/>（不看 `TickCount64` —— 它跨快速启动不归零）。
    /// </summary>
    public static double UptimeSeconds() => BootClock.UptimeSeconds();

    // ================================================================ 电池

    /// <summary>
    /// 电池卡片。
    /// <para>
    /// `percent` / `plugged` 走 `GetSystemPowerStatus`（快）；
    /// `voltage_v` / `remain_wh` 走 `root\wmi BatteryStatus`（一次查询就带回两个，约 6 ms）；
    /// `full_wh` 走 `BatteryFullChargedCapacity`、`design_wh` 走 `BatteryStaticData`
    /// （两者都是**静态值**，各缓存 5 分钟）；`health_pct` = 满充 ÷ 设计。
    /// </para>
    /// <para>
    /// ⚠️ `BatteryStaticData` 只有 `System.Management`（`Get-WmiObject` 那一套）读得出来，
    /// **`Get-CimInstance` 在本机返回空** —— 当初"设计容量是空的"就是这么误判的。
    /// 另外它的 `DesignedCapacity` 偶尔是 0（电池驱动还没填），0 一律当没有，绝不拿它做分母。
    /// </para>
    /// <para>
    /// ⚠️ 健康度**只许用 满充 ÷ 设计**。不要拿 Remaining 凑 —— 那是"当前充满程度"。
    /// 循环次数（`BatteryCycleCount`）本机恒 0（EC 不上报），所以不下发这个键。
    /// </para>
    /// <para>
    /// ⚠️⚠️ `health_pct` **封顶 100**。`DesignedCapacity` 是 ACPI `_BIF` 里厂商报的**保底标称值**
    /// （本机 62.7 Wh），新电池的实际满充容量常比它略大（63.6 Wh）⇒ 原始比值 101.4%。
    /// 健康度语义上不可能超过 100%（不能比全新更"健康"），摆个 101% 只会被当成算错
    /// —— 2026-10-31 用户当场就问了"怎么超过 100%"。所以这里就封顶，界面不二次处理。
    /// </para>
    /// </summary>
    public JsonObject Battery()
    {
        // ⚠️ Win32 的 `SYSTEM_POWER_STATUS` 用 **255 = unknown**，不是 128。
        //    128 只是 `BatteryFlag` 里"没有系统电池"那一位；驱动还没填状态时（刚唤醒、
        //    ACPI 电池驱动重载、某些虚拟机的电池）三个字段会一起回 255。
        //    旧实现只判 128 ⇒ 界面显示「255%」，`plugged` 变成 false ⇒ 上层误判"拔电"，
        //    触发自动切办公档 + 拉长采样间隔（2026-11-12 审查发现）。
        const byte BatteryFlagNoBattery = 128;
        const byte Unknown = 255;

        if (!GetSystemPowerStatus(out var ps) || ps.BatteryFlag is BatteryFlagNoBattery or Unknown)
            return new JsonObject { ["present"] = false };

        var outObj = new JsonObject { ["present"] = true };

        // 255 = 未知 ⇒ 整行隐藏（本项目规矩：拿不到就不放键），绝不摆个 255% 出去
        if (ps.BatteryLifePercent != Unknown)
            outObj["percent"] = (double)ps.BatteryLifePercent;

        // 1 = 插电 / 0 = 电池 / 255 = 未知。未知时**不放这个键**，上层保持上一拍的判断。
        if (ps.ACLineStatus is 0 or 1)
            outObj["plugged"] = ps.ACLineStatus == 1;

        var bst = BatteryStatus();
        if (bst["voltage_v"] is JsonNode v) outObj["voltage_v"] = v.DeepClone();
        if (bst["remain_wh"] is JsonNode r) outObj["remain_wh"] = r.DeepClone();

        var full = FullChargedWh();
        var design = DesignedWh();
        if (full is { } f) outObj["full_wh"] = f;
        if (design is { } d) outObj["design_wh"] = d;
        if (full is { } f2 && design is { } d2 && d2 > 0)
            outObj["health_pct"] = Math.Round(Math.Min(100.0, f2 / d2 * 100.0), 1);

        return outObj;
    }

    /// <summary>
    /// `BatteryStatus` 的扩展读数。**取到什么给什么**。
    /// 顺带把充放电功率也带回来（`rate_w`，充电为正 / 放电为负；不充不放时没有这个键）。
    /// </summary>
    public JsonObject BatteryStatus()
    {
        // 5 秒一次就够（见 _bstCache 的注）。返回**同一份对象**：调用方只读，
        // 拿到后都自己 DeepClone 进快照，不会有人就地改它。
        lock (_lock)
        {
            var now = Now();
            if (_bstCache is { } hit && now - hit.At < BatteryStatusTtl) return hit.Value;
        }

        var fresh = ReadBatteryStatusWmi();
        lock (_lock) _bstCache = (Now(), fresh);
        return fresh;
    }

    private static JsonObject ReadBatteryStatusWmi()
    {
        var outObj = new JsonObject();
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT * FROM BatteryStatus"));

            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
            {
                try
                {
                    double? Num(string field)
                    {
                        try
                        {
                            var v = mo[field];
                            return v is null ? null : Convert.ToDouble(v);
                        }
                        catch { return null; }
                    }

                    var charge = Num("ChargeRate");
                    var discharge = Num("DischargeRate");
                    if (charge is { } c && c / 1000.0 >= 0.5)
                        outObj["rate_w"] = Math.Round(c / 1000.0, 2);
                    else if (discharge is { } d && d / 1000.0 >= 0.5)
                        outObj["rate_w"] = Math.Round(-d / 1000.0, 2);

                    // 字段是毫伏：0 / 没报 = 当作没有
                    if (Num("Voltage") is { } mv && mv >= 1000.0)
                        outObj["voltage_v"] = Math.Round(mv / 1000.0, 2);

                    // 字段是毫瓦时
                    if (Num("RemainingCapacity") is { } mwh && mwh > 0)
                        outObj["remain_wh"] = Math.Round(mwh / 1000.0, 1);

                    return outObj;      // 一个电池就够
                }
                finally
                {
                    mo.Dispose();
                }
            }
        }
        catch
        {
            // 电池 WMI 不可用（台式机 / 驱动问题）：返回空对象，不报错
        }
        return outObj;
    }

    /// <summary>满充容量（Wh）。静态值，缓存 5 分钟 —— 别每秒查一次。</summary>
    private double? FullChargedWh()
    {
        lock (_lock)
        {
            var now = Now();
            if (_fullWhCache is { } cached && now - cached.At < 300.0) return cached.Wh;

            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                using var searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery("SELECT * FROM BatteryFullChargedCapacity"));

                using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
                {
                    try
                    {
                        var raw = mo["FullChargedCapacity"];
                        if (raw is null) continue;
                        var wh = Math.Round(Convert.ToDouble(raw) / 1000.0, 1);
                        if (wh <= 0) continue;
                        _fullWhCache = (now, wh);
                        return wh;
                    }
                    finally
                    {
                        mo.Dispose();
                    }
                }
            }
            catch { }
            return _fullWhCache?.Wh;
        }
    }

    /// <summary>
    /// 设计容量（Wh）。静态值，缓存 5 分钟 —— 和满充容量同一个理由，别每秒查。
    /// 走 `BatteryStaticData.DesignedCapacity`（**必须**用 `System.Management`，见 <see cref="Battery"/> 注）。
    /// </summary>
    private double? DesignedWh()
    {
        lock (_lock)
        {
            var now = Now();
            if (_designWhCache is { } cached && now - cached.At < 300.0) return cached.Wh;

            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                using var searcher = new ManagementObjectSearcher(
                    scope, new ObjectQuery("SELECT * FROM BatteryStaticData"));

                using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
                {
                    try
                    {
                        var raw = mo["DesignedCapacity"];
                        if (raw is null) continue;
                        var wh = Math.Round(Convert.ToDouble(raw) / 1000.0, 1);
                        if (wh <= 0) continue;      // 0 = 驱动没填，当没有
                        _designWhCache = (now, wh);
                        return wh;
                    }
                    finally
                    {
                        mo.Dispose();
                    }
                }
            }
            catch { }
            return _designWhCache?.Wh;
        }
    }

    // ================================================================ 网络

    /// <summary>
    /// `{rate, recv_mb, sent_mb}`。`rate` 是**两次调用之间的字节/秒**，
    /// 所以第一次调用必然是 0（没有参照）。
    /// </summary>
    public JsonObject Network()
    {
        // 3 秒一次（见 NetworkTtl 的注）：累计字节本身变化很慢，界面看的是 MB 与速率。
        // ⚠️ 连**算好的结果**一起缓存，不能只缓存累计值再重算速率 ——
        //    那样缓存期内的每一拍都会算出 delta=0 ⇒ 速率在 400 KB/s 和 0 之间来回跳，
        //    界面看着像断流。速率按 3 秒窗口算一次、保持 3 秒，才是它本来的意思。
        lock (_lock)
        {
            var now = Now();
            if (_netCache is { } hit && now - hit.At < NetworkTtl)
                return (JsonObject)hit.Value.DeepClone();
        }

        var (sent, recv) = ReadNetTotalsWmi();

        lock (_lock)
        {
            var now = Now();
            if (sent == 0 && recv == 0)
            {
                // 这一趟没读到（WMI 打嗝 / 网卡全灭）：沿用上次那一份，
                // 别把 recv/sent 显示成 0（那是"数据"，但不是真的）。
                if (_netCache is { } stale) return (JsonObject)stale.Value.DeepClone();
                return new JsonObject { ["rate"] = 0.0 };   // 从来没有过数：只给 rate，界面整行隐藏
            }

            var rate = 0.0;
            if (_lastNet is { } last)
            {
                var dt = now - last.At;
                if (dt > 0)
                {
                    // max(0, ...)：累计量在"断 VPN / 切网卡 / 禁用后重启"时会变小，
                    // 直接相除会显示负速率。
                    var delta = (sent + recv) - last.Total;
                    rate = delta > last.Total
                        ? 0.0                                   // 溢出/重置
                        : Math.Max(0.0, (double)delta / dt);
                }
            }
            _lastNet = (now, sent + recv);

            var result = new JsonObject
            {
                ["rate"] = Math.Round(rate, 0),
                ["recv_mb"] = Math.Round(recv / 1048576.0, 1),
                ["sent_mb"] = Math.Round(sent / 1048576.0, 1),
            };
            _netCache = (now, result);
            return (JsonObject)result.DeepClone();
        }
    }

    private static (ulong Sent, ulong Recv) ReadNetTotalsWmi()
    {
        ulong sent = 0, recv = 0;
        try
        {
            // ⚠️ **不能用 `NetworkInterface.GetIPStatistics()` 再按网卡求和**：
            //    那个 API 在 Windows 上返回的是 `GetIpStatisticsEx` 的**系统级**
            //    IPv4 累计值，与调用它的那张网卡无关。遍历 N 张网卡再求和 = 把同一个
            //    数加了 N 遍（本机 6 张网卡 → P7 A/B 里 recv 报成 4845 MB，
            //    实际 808 MB，正好 6 倍）。
            //    改用 WMI 的 `Win32_PerfRawData_Tcpip_NetworkInterface`：按网卡给
            //    累计字节（含 IPv6），和早先实现 psutil 的计数源一致 —— 实测
            //    WLAN 851045564 / 464135140 与 psutil 的 808MB / 441MB 完全对得上。
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT BytesReceivedPersec, BytesSentPersec "
                                + "FROM Win32_PerfRawData_Tcpip_NetworkInterface"));
            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject mo in rows)
            {
                try
                {
                    recv += Convert.ToUInt64(mo["BytesReceivedPersec"] ?? 0UL);
                    sent += Convert.ToUInt64(mo["BytesSentPersec"] ?? 0UL);
                }
                catch
                {
                    // 某张虚拟网卡没有计数很正常，跳过
                }
                finally
                {
                    mo.Dispose();
                }
            }
        }
        catch
        {
            // WMI 不可用：回 (0,0)，由调用方决定"沿用上次"还是"如实不给键"
        }

        return (sent, recv);
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ================================================================ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
