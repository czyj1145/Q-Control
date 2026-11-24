using System.Management;
using System.Text.Json.Nodes;

namespace QControl.Hal.Sys;

/// <summary>
/// 内建屏亮度（0~100）。走 WMI 的 `root\WMI`：`WmiMonitorBrightness` 读、`WmiMonitorBrightnessMethods` 写。
///
/// 为什么要有它（2026-11-05 用户报「切系统模式会改变亮度」）：
/// 切性能模式会同时切 Windows 电源计划（`AppState.SyncPlanFor` / 性能页按钮），
/// 而**每条电源计划各自存着一个「显示器亮度」索引** —— 本机实测：平衡 75 / 办公 55 /
/// 游戏 50 / 高性能 40。所以一切档亮度就被重放成目标计划的值，跟上一档完全不同。
/// 界面上给一个能自己拨的入口，用户至少可以就地拨回来。
///
/// 三条必须知道的边界：
///   ① **只对内建屏有效**：外接显示器（DDC/CI）不在这套 WMI 里，读不到就如实回来 ok:false
///      —— 调用方据此整行隐藏，不留一个拖了没反应的滑块；
///   ② **写会同时改写"当前电源计划的亮度索引"**（实测：`WmiSetBrightness(60)` 之后
///      `powercfg /q SCHEME_CURRENT … SUB_VIDEO` 的 VIDEONORMALLEVEL 也变成 60）。
///      换句话说"在哪个档调的亮度就记在哪个档"，换档仍会回那条计划自己的值 ——
///      这是用户确认过的语义（每档各自记住），不要偷偷改成"切档保持亮度"；
///   ③ 一次调用 20~50ms（WMI + 驱动往返），**不进每秒快照**：界面进页读一次、
///      每 10 秒补一次、写完回读，别在采样线程每拍调。
/// </summary>
public static class ScreenBrightness
{
    /// <summary>亮度 WMI 类都挂在这个命名空间下（每台机器一致）。</summary>
    private const string WmiScope = @"\\.\root\WMI";

    /// <summary>驱动把进度量化到最近档位时允许的误差（百分之一）。超出就如实报失败。</summary>
    private const int QuantizeTolerance = 2;

    /// <summary>
    /// 读当前亮度。返回 `{ok, level, max}` 或 `{ok:false, error}`。
    /// ⚠️ `max` 恒为 100：这套 WMI 的 Brightness/CurrentBrightness 就是百分比，
    ///    支持的档位表（BrightnessLevel）只是"能停在哪几个数"，不是量程上限。
    /// </summary>
    public static JsonObject Read()
    {
        try
        {
            // ⚠️ ManagementScope 不实现 IDisposable（只管路径/选项），别写 using var
            var scope = new ManagementScope(WmiScope);
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));

            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject monitor in rows)
            {
                using (monitor)
                {
                    if (monitor["CurrentBrightness"] is not { } raw) continue;

                    var level = Convert.ToInt32(raw);
                    if (level is < 0 or > 100) continue;

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["level"] = level,
                        ["max"] = 100,
                    };
                }
            }

            return new JsonObject
            {
                ["ok"] = false,
                ["error"] = "没找到可调亮度的内建屏（外接显示器不走这套接口）",
            };
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
    }

    /// <summary>
    /// 写亮度（自动钳 [0,100]）并**回读确认**。
    /// 返回 `{ok, level, requested}` —— `level` 是回读到的真实值（驱动可能量化），
    /// 界面拿它渲染，绝不停在"我以为写进去的那个数"。
    /// </summary>
    public static JsonObject Set(int level)
    {
        var want = Math.Clamp(level, 0, 100);

        try
        {
            var scope = new ManagementScope(WmiScope);
            using var searcher = new ManagementObjectSearcher(
                scope, new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));

            var wrote = 0;
            using var rows = searcher.Get();   // 集合自己就是 COM 枚举器，不 Dispose 就只能等终结器
            foreach (ManagementObject monitor in rows)
            {
                using (monitor)
                {
                    using var inParams = monitor.GetMethodParameters("WmiSetBrightness");
                    inParams["Timeout"] = (uint)0;          // 0 = 立即生效，不等
                    inParams["Brightness"] = (byte)want;
                    (monitor.InvokeMethod("WmiSetBrightness", inParams, null) as IDisposable)?.Dispose();
                    wrote++;
                }
            }

            if (wrote == 0)
            {
                return new JsonObject
                {
                    ["ok"] = false,
                    ["requested"] = want,
                    ["error"] = "这台机器没有可写的亮度接口（内建屏）",
                };
            }
        }
        catch (Exception e)
        {
            return new JsonObject { ["ok"] = false, ["requested"] = want, ["error"] = e.Message };
        }

        // 回读：WMI 在"通道正常但驱动没接"时会静默无效，只看返回值就会变成假成功。
        var after = Read();
        var applied = after["level"] is JsonValue v && v.TryGetValue<int>(out var got) ? got : (int?)null;

        if (applied is null)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["requested"] = want,
                ["error"] = "写入后读不回亮度，无法确认是否生效",
            };
        }

        var ok = Math.Abs(applied.Value - want) <= QuantizeTolerance;
        var result = new JsonObject
        {
            ["ok"] = ok,
            ["requested"] = want,
            ["level"] = applied.Value,
        };
        if (!ok) result["error"] = $"写入后回读不一致（要 {want}%，实际 {applied.Value}%）";
        return result;
    }
}
