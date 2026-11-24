using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using QControl.Core.FanCurve;
using QControl.Core.Model;

namespace QControl.App;

/// <summary>
/// 原厂三档曲线的**只读**导入。
///
/// 与原厂交回功能（已移除）的区别：这里只读注册表里的 UserFan1/2/3，
/// 把原厂每档的曲线抄进我们自己的 `perf.modes[档].fan`，不写注册表、不碰
/// FanControlStatus、不通知原厂 daemon —— 单纯"把原厂曲线读回来"。
///
/// 唯一入口是散热页卡头的「恢复风扇出厂」：先预览原厂真值给用户看，确认后
/// 重置整节 fan 参数 + 把原厂三档曲线写进档案。用户三档曲线被「应用到全部模式」
/// 刷成同一条时，这是恢复成原厂各档真曲线的唯一途径。
/// </summary>
internal static class FanCurveModePresets
{
    /// <summary>原厂注册表路径（与已移除的 OemFanHandoff 同一棵键，写死在这里避免依赖被删的类）。</summary>
    public const string OemRegistryPath = @"SOFTWARE\WOW6432Node\NL5\ControlCenter";

    private static readonly string[] UserFanKeys = ["UserFan1", "UserFan2", "UserFan3"];
    private static readonly string[] SysFanKeys = ["UserFan1_SYS", "UserFan2_SYS", "UserFan3_SYS"];

    /// <summary>只读：把原厂三档 UserFan 解析出来给界面预览（不写任何配置）。</summary>
    public static JsonObject PreviewOem()
    {
        using var key = Registry.LocalMachine.OpenSubKey(OemRegistryPath);
        if (key is null)
            return Fail("找不到原厂注册表 " + OemRegistryPath + "：这台机器没有原厂 NL5 控制中心的曲线配置");

        var modes = new JsonObject();
        var okCount = 0;
        for (var i = 0; i < FanCurveEngine.ModeKeys.Length; i++)
        {
            var mode = FanCurveEngine.ModeKeys[i];
            var raw = Str(key, UserFanKeys[i]);
            var sysRaw = Str(key, SysFanKeys[i]);
            var one = ParseMode(raw, sysRaw);
            one["userfan"] = raw;
            one["userfan_sys"] = sysRaw;
            modes[mode] = one;
            if (one["ok"]?.GetValue<bool>() == true) okCount++;
        }

        return new JsonObject
        {
            ["ok"] = okCount > 0,
            ["registry_path"] = OemRegistryPath,
            ["available_modes"] = okCount,
            ["modes"] = modes,
            ["error"] = okCount == 0
                ? "原厂三档 UserFan 串都是空的或格式不对：先在原厂控制中心里设过自定义曲线再导入"
                : null,
        };
    }

    /// <summary>
    /// 把原厂三档曲线导入我们自己的档案。slot 顺序固定 office→UserFan1 / game→2 / beast→3
    /// （与已移除的 OemFanHandoff.SlotOf 一致）。只有解析成功且非全 0 的档才写；
    /// 缺档不凑 —— 没读到的档保持原样，绝不用别的档曲线顶上去。
    /// </summary>
    public static JsonObject ImportOem(ModelStore model, FanCurveEngine fan)
    {
        var preview = PreviewOem();
        if (preview["ok"]?.GetValue<bool>() != true) return preview;

        var written = new JsonArray();
        var skipped = new JsonArray();
        lock (model.SyncRoot)
        {
            var modes = preview["modes"]!.AsObject();
            foreach (var mode in FanCurveEngine.ModeKeys)
            {
                var one = modes[mode]!.AsObject();
                if (one["ok"]?.GetValue<bool>() != true)
                {
                    skipped.Add(new JsonObject
                    {
                        ["mode"] = mode,
                        ["reason"] = one["error"]?.GetValue<string>() ?? "解析失败",
                    });
                    continue;
                }

                ModeBundle.SetFanCurve(model.Presets, mode, ModeBundle.FanCpu, one["cpu"]!.DeepClone());
                ModeBundle.SetFanCurve(model.Presets, mode, ModeBundle.FanGpu, one["gpu"]!.DeepClone());
                // SYS：原厂有非空 SYS 串才写；没有就保持这一档原来的 SYS 档案（别用别的档凑）
                if (one["sys"] is JsonArray sys) ModeBundle.SetFanCurve(model.Presets, mode, ModeBundle.FanSys, sys.DeepClone());

                written.Add(new JsonObject
                {
                    ["mode"] = mode,
                    ["points"] = one["cpu"]!.AsArray().Count,
                    ["sys"] = one["sys"] is not null,
                });
            }
            model.Save();
        }

        // 跟随 / 固定两种模式都对齐当前生效档，下一轮就用上（不等 6 秒节流）
        var realigned = fan.RealignActive();
        return new JsonObject
        {
            ["ok"] = written.Count > 0,
            ["written"] = written,
            ["skipped"] = skipped,
            ["realigned"] = realigned,
            ["error"] = written.Count == 0 ? "没有可导入的档位" : null,
        };
    }

    /// <summary>解析一档：8 点 CPU + 8 点 GPU + 可选 SYS。全 0 视为原厂没配。</summary>
    private static JsonObject ParseMode(string raw, string sysRaw)
    {
        if (!OemFanCurve.TryParseUserFan(raw, out var cpu, out var gpu, out var error))
            return new JsonObject { ["ok"] = false, ["error"] = error };
        if (OemFanCurve.AllZero(cpu) && OemFanCurve.AllZero(gpu))
            return new JsonObject { ["ok"] = false, ["error"] = "原厂这一档是空曲线（全 0），没配过自定义曲线" };

        var one = new JsonObject
        {
            ["ok"] = true,
            ["cpu"] = FanCurveEngine.PointsToJson(OemFanCurve.ToCurve(cpu)),
            ["gpu"] = FanCurveEngine.PointsToJson(OemFanCurve.ToCurve(gpu)),
        };
        if (OemFanCurve.TryParseDuties(sysRaw, out var sys, out _) && !OemFanCurve.AllZero(sys))
            one["sys"] = FanCurveEngine.PointsToJson(OemFanCurve.ToCurve(sys));
        return one;
    }

    private static string Str(RegistryKey key, string name)
        => key.GetValue(name, "") is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    private static JsonObject Fail(string message) => new() { ["ok"] = false, ["error"] = message };
}
