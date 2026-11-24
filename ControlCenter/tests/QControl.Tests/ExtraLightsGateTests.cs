using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 独立灯珠（Logo / 尾灯）与**风扇路数分开判定**（2026-11-18 用户要求）。
///
/// 背景：`ec.kb_extra_lights` 写在机型档案里，而机型档案可能是按 EC SKU 升级过来的
/// （三风扇 ⇒ 猎刃16）。但 SKU 只回答"几路风扇"，回答不了"有没有那颗灯" ——
/// 同模具的三风扇机大多数没有 Logo。让两者共用一个判据，就会在没灯的机器上
/// 画出一个点了没反应的色块。
///
/// 2026-11-18 第二轮：**设置页那个「自动 / 强制开 / 关」三态也撤掉了**。
/// 用户的原话是"如果真有灯、且识别到了，还需要这个开关吗" —— 不需要。
/// 所以判定是纯身份判定：名字命中档案才给灯，认不到就不给，没有人为覆盖。
///
/// 这里钉死四条：
///   · 名字命中 ⇒ 灯开（地址照档案）；
///   · 按 SKU 升级 ⇒ 灯关，但 `ExtraLightsGated` 要能说明"是档案有、这次没给"；
///   · 灯关**不许**连带影响风扇路数与其它能力；
///   · 档案压根没声明灯珠时，`gated` 必须是 false（不是被拦，是根本没有）。
/// </summary>
public class ExtraLightsGateTests
{
    private static JsonObject Profile(bool withLogo = true)
    {
        var ec = new JsonObject { ["fans"] = 3 };
        if (withLogo) ec["kb_extra_lights"] = new JsonObject { ["logo"] = 8 };

        return new JsonObject
        {
            ["id"] = "test-lights",
            ["name"] = "测试机",
            ["vendor"] = "TEST",
            ["mold"] = "Quanta NL5",
            ["verified"] = true,
            ["ec"] = ec,
        };
    }

    [Fact]
    public void 名字命中的档案带灯()
    {
        var d = new Device(Profile());
        Assert.True(d.ExtraLightsByProfile);
        Assert.True(d.ExtraLightsAllowed);
        Assert.False(d.ExtraLightsGated);
        Assert.Equal(8, d.ExtraLightAddrs["logo"]);
    }

    [Fact]
    public void 按SKU升级进来的档案不给灯但保留风扇路数()
    {
        var d = new Device(Profile(), null, null, extraLightsByProfile: false);

        Assert.False(d.ExtraLightsAllowed);
        Assert.True(d.ExtraLightsGated);          // 是"有但没给"，不是"机器没有"
        Assert.Empty(d.ExtraLightAddrs);

        // ⚠️ 灯关了**不许**影响风扇：三路是 SKU 判出来的，与灯无关
        Assert.Equal(3, d.Ec.Fans);
        Assert.Equal(3, d.Caps()["fan_count"]!.GetValue<int>());
    }

    [Fact]
    public void 没有用户开关也没有既成事实可改()
    {
        // 撤掉三态之后，判定只由构造参数决定：同一份档案、同一个身份，
        // 反复读永远一个结果 —— 不存在"上次用户强制开过"这种残留。
        var named = new Device(Profile());
        Assert.True(named.ExtraLightsAllowed);
        Assert.True(named.WithEcOverride(true).ExtraLightsAllowed);

        var upgraded = new Device(Profile(), null, null, extraLightsByProfile: false);
        Assert.False(upgraded.ExtraLightsAllowed);
        Assert.False(upgraded.WithEcOverride(false).ExtraLightsAllowed);
    }

    [Fact]
    public void 档案没声明灯珠时既不给也不算gated()
    {
        var d = new Device(Profile(withLogo: false));

        Assert.Empty(d.ExtraLightAddrs);
        Assert.False(d.ExtraLightsGated);         // 不是被拦，是根本没有
        Assert.False(d.ExtraLightsByProfile);
    }

    [Fact]
    public void 换EC总闸不会把灯的判定丢掉()
    {
        var on = new Device(Profile()).WithEcOverride(false);
        Assert.True(on.ExtraLightsAllowed);
        Assert.Equal(8, on.ExtraLightAddrs["logo"]);

        var off = new Device(Profile(), null, null, extraLightsByProfile: false).WithEcOverride(true);
        Assert.False(off.ExtraLightsAllowed);
        Assert.Empty(off.ExtraLightAddrs);
    }

    [Fact]
    public void 设备视图把灯的判定如实吐出来()
    {
        // ⚠️ 灯的判定**不进 caps**（caps 与旧版逐条对齐，见 GoldenDeviceTests）——
        //    走 `lights` 段。这里顺带钉死"gated 与 机器没有 必须分得开"。
        var gated = new Device(Profile(), null, null, extraLightsByProfile: false);
        var v = gated.View()["lights"]!;
        Assert.False(v["logo"]!.GetValue<bool>());
        Assert.True(v["gated"]!.GetValue<bool>());
        Assert.False(v["by_profile"]!.GetValue<bool>());

        var on = new Device(Profile()).View()["lights"]!;
        Assert.True(on["logo"]!.GetValue<bool>());
        Assert.False(on["gated"]!.GetValue<bool>());
        Assert.True(on["by_profile"]!.GetValue<bool>());

        // 压根没声明灯珠的档案：既不是 logo，也不算 gated
        var none = new Device(Profile(withLogo: false)).View()["lights"]!;
        Assert.False(none["logo"]!.GetValue<bool>());
        Assert.False(none["gated"]!.GetValue<bool>());
    }
}
