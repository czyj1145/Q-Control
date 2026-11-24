using System.Management;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QControl.Hal.Sensors;

/// <summary>
/// 物理显卡清单 + 有没有核显。
///
/// 为什么要它：显卡模式（GFXT / 0x0203）写进 EC 之后**要重启才由固件应用**，
/// 所以"点一下没黑屏、没变化"是正常现象。独显直连时 BIOS 常在 POST 阶段就把核显
/// 隐藏掉，系统里就看不到 Intel/AMD 核显；混合模式下能看到。
/// ⚠️ 它反映的是**上次开机时**生效的模式，不是刚写入的值 ——
/// 界面上必须写清楚，别让它看起来像"切换没成功"。
/// </summary>
public static class DisplaySensors
{
    private static readonly object Gate = new();
    private static (double At, JsonObject Value)? _cache;

    /// <summary>核显名字的独立缓存；`Value = null` 是**有效结论**（这台机器没有核显），照样缓存。</summary>
    private static (double At, string? Value)? _igpu;

    /// <summary>
    /// 缓存时长（秒）。这张表是**上次开机生效的硬件清单**（见类注释），
    /// 驱动级变化（插拔 eGPU / 改 BIOS 显卡模式）都要重启才反映 —— 60 秒已经保守。
    ///
    /// ⚠️ 为什么必须缓存：本方法原本**每秒查一次 `Win32_VideoController`**，
    ///    每次还 new 一个 `ManagementScope`。实测整套快照分配速率 711 KB/s，
    ///    大头正是这类每拍一条的 WMI 查询；托管堆"活着 3 MB、提交 25 MB"就是它们的账。
    ///    每次失败都不缓存（拿不到就如实回 ok=false，下一拍重试）。
    /// </summary>
    private const double TtlSeconds = 60.0;

    public static JsonObject Read()
    {
        lock (Gate)
        {
            var now = Now();
            if (_cache is { } hit && now - hit.At < TtlSeconds)
                return (JsonObject)hit.Value.DeepClone();   // 缓存只读，别让调用方改进去
        }

        var fresh = ReadNow();
        // 只有成功才进缓存：ok=false（WMI 打嗝）必须下一拍照样重试，否则一卡就是一分钟
        if (fresh["ok"]?.GetValue<bool>() == true)
        {
            lock (Gate)
                _cache = (Now(), fresh);
        }
        return (JsonObject)fresh.DeepClone();
    }

    /// <summary>
    /// 核显的型号名（**没有核显 = null**）。
    ///
    /// 给"独显读数彻底拿不到时改显核显"用（`GpuSensors` 的第三级回退）：
    /// 集显输出模式下系统里只剩这块适配器，总览页显卡卡片至少还能报出是谁、在忙多少。
    /// 只取一次名字 —— 这种硬件清单重启才变，没必要每拍问 WMI。
    /// </summary>
    public static string? IntegratedName()
    {
        lock (Gate)
        {
            var now = Now();
            if (_igpu is { } hit && now - hit.At < TtlSeconds) return hit.Value;
        }

        string? name = null;
        try
        {
            foreach (var (n, ven) in Physical())
            {
                if (Classify(ven, n) != Sys.GpuKind.Integrated) continue;
                name = n;
                break;
            }
        }
        catch
        {
            return null;    // WMI 打嗝：不缓存，下次再问
        }

        lock (Gate) _igpu = (Now(), name);
        return name;
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // ------------------------------------------------------------------ 枚举

    /// <summary>
    /// 挂在 PCI 上的物理显卡（名字 + 厂商 ID）。
    /// **虚拟显示适配器（MuMu / GameViewer / Parsec 那些 `ROOT\DISPLAY\…`）一律不认** ——
    /// 混进来会让人以为"有两块显卡"。
    /// </summary>
    private static List<(string Name, string Ven)> Physical()
    {
        var list = new List<(string, string)>();
        var scope = new ManagementScope(@"\\.\root\cimv2");
        using var searcher = new ManagementObjectSearcher(
            scope, new ObjectQuery("SELECT Name, PNPDeviceID FROM Win32_VideoController"));

        using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
        foreach (ManagementObject mo in rows)
        {
            try
            {
                var name = mo["Name"]?.ToString() ?? "";
                var pnp = (mo["PNPDeviceID"]?.ToString() ?? "").ToUpperInvariant();
                if (!pnp.StartsWith("PCI", StringComparison.Ordinal)) continue;

                var m = Regex.Match(pnp, "VEN_([0-9A-F]{4})");
                list.Add((name, m.Success ? m.Groups[1].Value : ""));
            }
            finally
            {
                mo.Dispose();
            }
        }
        return list;
    }

    /// <summary>
    /// 核显 / 独显的判定统一走 <see cref="Sys.GpuClassification"/>（2026-11-12 修）。
    /// 这里曾经自己按 `VEN_8086/1002/1022 = 核显` 判，而 `1002` 同时是 AMD 核显与 AMD 独显的
    /// 厂商 ID ⇒ AMD 独显机型上「独显直连」永远被判成「混合输出」，核显也认成了独显。
    /// </summary>
    private static Sys.GpuKind Classify(string ven, string name) => Sys.GpuClassification.Of(ven, name);

    private static JsonObject ReadNow()
    {
        try
        {
            var physical = new JsonArray();
            var hasIgpu = false;
            var dgpu = "";

            foreach (var (name, ven) in Physical())
            {
                physical.Add(name);

                switch (Classify(ven, name))
                {
                    case Sys.GpuKind.Integrated:
                        hasIgpu = true;
                        break;
                    case Sys.GpuKind.Discrete:
                        if (dgpu.Length == 0) dgpu = name;   // 多块独显只报第一块
                        break;
                    // Unknown：厂商 ID 分不开、型号名也认不出 —— 两边都不算，别猜
                }
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["igpu"] = hasIgpu,
                ["has_dgpu"] = dgpu.Length > 0,
                ["dgpu"] = dgpu,
                ["physical"] = physical,
            };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }
}
