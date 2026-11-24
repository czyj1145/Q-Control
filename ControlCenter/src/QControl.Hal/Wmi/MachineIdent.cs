using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QControl.Core.Devices;
using Microsoft.Win32;

namespace QControl.Hal.Wmi;

/// <summary>
/// 采集本机身份指纹 —— **全部只读，不需要管理员**。
///
/// 输出形状是**契约**：Core 的 <see cref="DeviceMatcher"/> 按这份形状匹配，
/// 少一个键，匹配规则就命不中。
///
/// 为什么原厂注册表项目号比 SMBIOS 可靠：OEM 经常把 SMBIOS 全填成营销名
/// （本机三处都是 "G16 Pro"），而原厂软件会在 `HKLM\SOFTWARE\NL5` 这种
/// 项目号键下留东西 —— 那才是模具指纹。
/// </summary>
public static partial class MachineIdent
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string PnpKey = @"SYSTEM\CurrentControlSet\Enum\ACPI\PNP0C14";

    /// <summary>
    /// 采集身份。键名与早先实现逐一对应，**值缺失时写 JSON null**（不是省略键）——
    /// Core 的 <c>FieldValues</c> 对 null 返回空列表，省略键则要另走一条路。
    /// </summary>
    /// <param name="deep">
    /// 是否探测 `root\WMI` 有哪些兼容类。这一层要起 COM（100~300ms），
    /// 早先实现也只在"轻量识别全没命中"时才做 —— 别给每次启动都加这段延迟。
    /// </param>
    /// <param name="profiles">用于推导候选项目号；不传则用嵌入档案。</param>
    public static JsonObject Collect(bool deep = false,
                                     IReadOnlyList<DeviceProfile>? profiles = null)
    {
        profiles ??= DeviceResolver.AllProfiles;

        var ident = new JsonObject
        {
            ["system_vendor"] = RegGet(BiosKey, "SystemManufacturer"),
            ["system_product"] = RegGet(BiosKey, "SystemProductName"),
            ["system_version"] = RegGet(BiosKey, "SystemVersion"),
            ["system_family"] = RegGet(BiosKey, "SystemFamily"),
            ["board_vendor"] = RegGet(BiosKey, "BaseBoardManufacturer"),
            ["board_product"] = RegGet(BiosKey, "BaseBoardProduct"),
            ["bios_vendor"] = RegGet(BiosKey, "BIOSVendor"),
            ["bios_version"] = RegGet(BiosKey, "BIOSVersion"),
            ["acpi_pnp0c14"] = RegSubkeys(PnpKey),
            ["vendor_registry"] = new JsonObject(),
            ["wmi_classes"] = new JsonObject(),
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
        };

        // 原厂软件注册表项目号（NL5 这种）
        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in profiles)
        {
            if (p.Raw["match_codes"] is JsonArray mc)
                foreach (var c in mc)
                    if (c is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                        codes.Add(s);

            foreach (var c in DeviceMatcher.CodesFromRules(p))
                if (c.Length > 0) codes.Add(c);
        }

        var vendorReg = new JsonObject();
        foreach (var code in codes)
        {
            vendorReg[code] = RegExists($@"SOFTWARE\{code}")
                              || RegExists($@"SOFTWARE\WOW6432Node\{code}");
        }
        ident["vendor_registry"] = vendorReg;

        // 探 WMI 类很贵，只在 deep 或"轻量识别没命中任何档案"时才做
        var needWmi = deep || !profiles.Any(p => DeviceMatcher.Match(p, ident) is not null);
        if (needWmi)
        {
            try { ident["wmi_classes"] = ProbeWmiClasses(profiles); }
            catch { ident["wmi_classes"] = new JsonObject(); }
        }

        return ident;
    }

    /// <summary>只读探测 `root\WMI` 下是否存在各档案声明的兼容类。</summary>
    private static JsonObject ProbeWmiClasses(IReadOnlyList<DeviceProfile> profiles)
    {
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in profiles)
        {
            var cls = p.Raw["ec"]?["wmi_class"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(cls)) wanted.Add(cls);
        }

        var result = new JsonObject();
        if (wanted.Count == 0) return result;

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(@"\\.\root\WMI"),
                new System.Management.ObjectQuery("SELECT * FROM meta_class"));
            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (System.Management.ManagementObject mo in rows)
            {
                var name = mo.SystemProperties["__CLASS"]?.Value as string;
                if (name is not null && wanted.Contains(name))
                    result[name] = true;
                mo.Dispose();
            }
        }
        catch
        {
            // 探测失败就当"都不知道" —— 它是兜底路径，不能反过来挡住启动
        }
        return result;
    }

    // ================================================================ 候选项目号扫描（适配报告专用）

    /// <summary>形如原厂项目号：3~6 位、全大写字母数字、首字符是字母（`NL5` / `GM6PQ` / `PH6`）。</summary>
    [GeneratedRegex(@"^[A-Z][A-Z0-9]{2,5}$")]
    private static partial Regex ProjectCodeRegex();

    /// <summary>长得像项目号、其实是系统/通用组件的键（`HKLM\SOFTWARE\ODBC` 这种）。</summary>
    private static readonly HashSet<string> NotProjectCodes = new(StringComparer.Ordinal)
    {
        "ODBC", "VESA", "OEM", "SETUP", "CDDB", "NTFS", "TWAIN", "VGA",
    };

    /// <summary>
    /// 扫 `HKLM\SOFTWARE`（含 `WOW6432Node`）下**形如原厂项目号**的子键，并把每个键里的
    /// 子键名 / 值名一并带出来 —— 这是给新机型适配找"模具指纹"的唯一可靠线索。
    ///
    /// 为什么要单独一条：<see cref="Collect"/> 里的 `vendor_registry` 只查**已登记**档案声明的
    /// 项目号（NL5 之类），对新机型永远查不到 —— 恰恰是最需要它的场景。这里改成盲扫，
    /// 结果只进适配报告（不参与匹配），由人一眼认出 `sub: ControlCenter` 的那个键。
    ///
    /// ⚠️ 只读、不需要管理员、**绝不在启动路径上调**：要枚举几百个子键，还要逐个 OpenSubKey。
    /// </summary>
    /// <param name="maxKeys">最多带回多少个候选（防止某些机器上 SOFTWARE 塞满乱七八糟的短键）。</param>
    public static JsonObject ScanVendorCandidates(int maxKeys = 120)
    {
        var found = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);

        Scan(@"SOFTWARE", "x64");
        Scan(@"SOFTWARE\WOW6432Node", "x86");

        var outObj = new JsonObject();
        foreach (var (name, entry) in found) outObj[name] = entry;
        return outObj;

        void Scan(string root, string bit)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(root);
                if (k is null) return;

                foreach (var name in k.GetSubKeyNames())
                {
                    if (found.Count >= maxKeys) return;
                    if (!ProjectCodeRegex().IsMatch(name)) continue;
                    if (NotProjectCodes.Contains(name)) continue;

                    // 两个视图下都有同名键（32 位程序装的）⇒ 标成双份，别丢信息
                    if (found.TryGetValue(name, out var exist))
                    {
                        exist["bits"] = "x64+x86";
                        continue;
                    }

                    var entry = new JsonObject { ["bits"] = bit };
                    try
                    {
                        using var sub = k.OpenSubKey(name);
                        if (sub is not null)
                        {
                            if (sub.GetSubKeyNames() is { Length: > 0 } subs)
                                entry["sub"] = Strings(subs, 15);
                            if (sub.GetValueNames() is { Length: > 0 } vals)
                                entry["val"] = Strings(vals, 15);
                        }
                    }
                    catch
                    {
                        // 单个键读不动就只留名字 —— 名字本身就是最重要的证据
                    }
                    found[name] = entry;
                }
            }
            catch
            {
                // SOFTWARE 都读不到（极端受限账户）：返回已扫到的部分，不抛
            }
        }

        static JsonArray Strings(string[] items, int cap)
        {
            var arr = new JsonArray();
            foreach (var s in items.Where(x => x.Length > 0).OrderBy(x => x, StringComparer.Ordinal).Take(cap))
                arr.Add(s);
            return arr;
        }
    }

    // ---------------------------------------------------------------- 注册表小工具

    private static string? RegGet(string subKey, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            var v = k?.GetValue(name);
            return v?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool RegExists(string subKey)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            return k is not null;
        }
        catch
        {
            return false;
        }
    }

    private static JsonArray RegSubkeys(string subKey)
    {
        var arr = new JsonArray();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            if (k is null) return arr;
            foreach (var n in k.GetSubKeyNames()) arr.Add(n);
        }
        catch
        {
            // 读不到就当空 —— ident 里这个字段只是诊断信息，不参与匹配
        }
        return arr;
    }
}
