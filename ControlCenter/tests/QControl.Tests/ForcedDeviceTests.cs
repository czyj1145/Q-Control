using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// `--device &lt;id&gt;`（强制预览某个机型档案）的语义。
///
/// 这块是"给未适配机型改 UI"的唯一抓手（2026-11-02）：本机命中的永远是 nl5，
/// 未适配机型的界面长什么样没人看得到。所以必须钉死两条：
///   ① 能覆盖本机会命中的档案；
///   ② **永远只读** —— 强制预览绝不能变成"给没标定的机器解锁写 EC"的旁路。
/// </summary>
public class ForcedDeviceTests
{
    /// <summary>本机（七彩虹 隐星 G16 Pro）会命中的身份，用来证明"强制能压过正常匹配"。</summary>
    private static JsonObject Nl5Ident => new()
    {
        ["system_vendor"] = "Colorful",
        ["system_product"] = "Colorful G16 Pro",
        ["vendor_registry"] = new JsonObject { ["NL5"] = true },
    };

    [Fact]
    public void 强制通用档案时结果是未验证且写入口关着()
    {
        var normal = DeviceResolver.Resolve(Nl5Ident);
        Assert.NotEqual("quanta-gmwmi-generic", normal.Id);   // 前提：正常匹配不是 generic

        var forced = DeviceResolver.Resolve(Nl5Ident, null, "quanta-gmwmi-generic");
        Assert.Equal("quanta-gmwmi-generic", forced.Id);
        Assert.False(forced.Verified);
        Assert.False(forced.ProfileWrites);
        Assert.False(forced.WritesAllowed);
        // 名字上加标记：截图/界面上必须能看出"这不是本机真实机型"
        Assert.Contains("强制预览", forced.Name);
    }

    [Fact]
    public void 强制已验证档案也不许解锁写权限()
    {
        // nl5 是 verified=true 的真机档案 —— 强制它时同样要压成只读，
        // 否则在别的机器上 `--device quanta-nl5` 就成了绕过 verified 的写入口。
        var forced = DeviceResolver.Resolve(Nl5Ident, null, "quanta-nl5");
        Assert.Equal("quanta-nl5", forced.Id);
        Assert.False(forced.Verified);
        Assert.False(forced.WritesAllowed);
    }

    [Fact]
    public void 强制预览时用户总闸也压不住只读()
    {
        // 用户显式打开过 EC 总闸（`ec.enabled:true` ⇒ ecOverride=true）也不能让
        // `--device` 预览变成可写：WritesAllowed = EcOverride ?? ProfileWrites，
        // 不压这一半，档案里的 writes_allowed=false 会被盖掉（2026-11-26 审查）。
        var forced = DeviceResolver.Resolve(Nl5Ident, ecOverride: true, "quanta-nl5");
        Assert.Equal("quanta-nl5", forced.Id);
        Assert.False(forced.ProfileWrites);
        Assert.False(forced.WritesAllowed);
        Assert.NotEqual(true, forced.EcOverride);
    }

    [Fact]
    public void 不存在的强制档案当没设过()
    {
        var forced = DeviceResolver.Resolve(Nl5Ident, null, "no-such-device");
        Assert.NotEqual("no-such-device", forced.Id);
        Assert.Equal(DeviceResolver.Resolve(Nl5Ident).Id, forced.Id);
    }
}
