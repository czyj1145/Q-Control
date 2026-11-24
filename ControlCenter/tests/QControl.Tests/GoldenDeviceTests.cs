using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 机型识别与能力表：逐条比旧版 `match_profile` / `resolve` / `Device.caps`。
///
/// 这块决定「界面画不画某个功能」和「后端拦不拦写入」，两件事都不能凭感觉。
/// 尤其 caps：写总闸一关，fan_manual / fan_curve / gfx_switch / keyboard_rgb
/// 必须同时灭、perf_modes 必须清空 —— 少灭一个就是"点了没反应"。
/// </summary>
public class GoldenDeviceTests
{
    [Fact]
    public void 每份档案的匹配结果逐条对齐黄金样本()
    {
        var doc = Golden.Load("device_match.json");
        var profiles = DeviceResolver.AllProfiles.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var c in doc["match"]!.AsArray())
        {
            var ident = c!["ident"]!.AsObject();
            var profileId = c["profile"]!.GetValue<string>();
            var expected = c["matched"]!.GetValue<bool>();
            var expectedFields = c["fields"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();

            if (!profiles.TryGetValue(profileId, out var profile))
            {
                failures.Add($"C# 侧没有档案 {profileId}");
                continue;
            }

            var got = DeviceMatcher.Match(profile, ident);
            if ((got is not null) != expected)
            {
                failures.Add($"{profileId} + {ident.ToJsonString()} 期望命中={expected} 实得={got is not null}");
                continue;
            }
            if (got is not null && !got.Fields.SequenceEqual(expectedFields))
            {
                failures.Add($"{profileId} + {ident.ToJsonString()} 命中字段 "
                             + $"[{string.Join(",", got.Fields)}] ≠ [{string.Join(",", expectedFields)}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void 解析结果逐条对齐黄金样本()
    {
        var doc = Golden.Load("device_match.json");
        var failures = new List<string>();

        foreach (var c in doc["resolve"]!.AsArray())
        {
            var ident = c!["ident"]!.AsObject();
            var expectedId = c["id"]!.GetValue<string>();
            var got = DeviceResolver.Resolve(ident);
            if (got.Id != expectedId)
                failures.Add($"{ident.ToJsonString()} 期望 {expectedId} 实得 {got.Id}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void 专用档案必须排在通用档案前面()
    {
        // priority 100 的 nl5 必须先命中，否则所有机型都会落到 generic 上。
        // ident 同时能命中两份档案时，顺序就是唯一的分水岭。
        var ident = new JsonObject { ["vendor_registry"] = new JsonObject { ["NL5"] = true } };
        Assert.Equal("quanta-nl5", DeviceResolver.Resolve(ident).Id);
    }

    [Fact]
    public void 能力表逐条对齐黄金样本()
    {
        // 2026-11-22：`quanta-gmwmi-generic` 改为借 NL5 的 layout / 背光分区 ⇒ 它的三条向量
        // 按新契约更新（ec_read true、kb_zones 3、开写后 keyboard_rgb true）；其余条目未动。
        var doc = Golden.Load("device_caps.json");
        var failures = new List<string>();

        foreach (var c in doc.AsArray())
        {
            var profileId = c!["profile"]!.GetValue<string>();
            var raw = profileId == "unsupported"
                ? DeviceResolver.Unsupported
                : DeviceResolver.AllProfiles.FirstOrDefault(p => p.Id == profileId)?.Raw;

            if (raw is null)
            {
                failures.Add($"C# 侧没有档案 {profileId}");
                continue;
            }

            bool? ovr = c["override"] is null ? null : c["override"]!.GetValue<bool>();
            var dev = new Device(raw, null, ovr);

            if (dev.WritesAllowed != c["writes_allowed"]!.GetValue<bool>())
                failures.Add($"{profileId}/ovr={ovr?.ToString() ?? "null"} writes_allowed 不符");
            if (dev.EcCapable != c["ec_capable"]!.GetValue<bool>())
                failures.Add($"{profileId} ec_capable 不符");
            if (dev.EcDefault != c["ec_default"]!.GetValue<bool>())
                failures.Add($"{profileId} ec_default 不符");
            if (Golden.FirstDiff(dev.Caps(), c["caps"], "caps") is { } diff)
                failures.Add($"{profileId}/ovr={ovr?.ToString() ?? "null"} {diff}");
            if (dev.DenyHint() != c["deny_hint"]!.GetValue<string>())
                failures.Add($"{profileId}/ovr={ovr?.ToString() ?? "null"} deny_hint 文案不符：\n"
                             + $"  C#     {dev.DenyHint()}\n  旧版 {c["deny_hint"]!.GetValue<string>()}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void 总闸关掉时四个写能力必须一起灭()
    {
        // 这是 2026-10-18 那个「设置页关了 EC、散热页还在」事故的回归测试。
        var raw = DeviceResolver.AllProfiles.First(p => p.Id == "quanta-nl5").Raw;
        var on = new Device(raw, null, true);
        var off = new Device(raw, null, false);

        Assert.True(Device.Truthy(on.Caps()["fan_manual"]));
        Assert.False(Device.Truthy(off.Caps()["fan_manual"]));
        Assert.False(Device.Truthy(off.Caps()["fan_curve"]));
        Assert.False(Device.Truthy(off.Caps()["gfx_switch"]));
        Assert.False(Device.Truthy(off.Caps()["keyboard_rgb"]));
        Assert.Empty(off.Caps()["perf_modes"]!.AsArray());
        // 读能力不受写总闸影响
        Assert.True(Device.Truthy(off.Caps()["ec_read"]));
    }

    [Fact]
    public void 不可变换总闸不会污染原实例()
    {
        // 旧版靠 _caps 缓存 + _caps_ovr 快照修这个坑；C# 干脆不可变。
        var raw = DeviceResolver.AllProfiles.First(p => p.Id == "quanta-nl5").Raw;
        var original = new Device(raw);
        var closed = original.WithEcOverride(false);

        Assert.True(original.WritesAllowed);          // 原实例不受影响
        Assert.False(closed.WritesAllowed);
        Assert.True(original.EcOverride is null);     // 原实例的总闸也没被改
    }

    [Fact]
    public void 未识别机型总闸打开也救不回来()
    {
        // 没有命令表 ⇒ 连"这是哪台机器"都不知道，用户开关开到底也不能写。
        var dev = new Device(DeviceResolver.Unsupported, null, true);
        Assert.False(dev.EcCapable);
        Assert.False(dev.WritesAllowed);
    }
}
