using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// NL5 **模具家族**识别：七彩虹 隐星 G16 Pro、雷神 猎刃 16 等同一模具换牌子的机型，
/// 必须落到同一份档案上，**不需要采集档案**（2026-11-17 用户的要求）。
///
/// 为什么靠模具码而不是型号名：OEM 把 SMBIOS 整片填成营销名（本机三处都是 G16 Pro），
/// 换个牌子型号名就全变了；而模具码（NLXB/NLZD/NLZE/NLY）在整条产线上是同一套。
///
/// 钉死三件事：
///   · 模具码出现在**任一**位置（原厂注册表键 / 板号 / BIOS 版本 / 型号名组合）都能认出来；
///   · 没有模具码的机型**不许**被认成 NL5（雷神别的型号、别家的 G16 都必须落空）；
///   · BIOS 里"ONLY"这种字串不能被 `NLY` 误命中（词边界不是装饰）。
/// </summary>
public class Nl5FamilyMatchTests
{
    private static DeviceProfile Nl5()
        => DeviceResolver.AllProfiles.First(p => p.Id == "quanta-nl5");

    private static IReadOnlyList<string>? Fields(JsonObject ident)
        => DeviceMatcher.Match(Nl5(), ident)?.Fields;

    [Theory]
    [InlineData("NLY")]
    [InlineData("NLXB")]
    [InlineData("NLZD")]
    [InlineData("NLZE")]
    public void 板号是模具码就认(string board)
    {
        var fields = Fields(new JsonObject { ["board_product"] = board });
        Assert.NotNull(fields);
        Assert.Equal("board_product", Assert.Single(fields!));
    }

    [Theory]
    [InlineData("NLY_1.10")]
    [InlineData("NLXB V2.03")]
    [InlineData("InsydeH2O NLZE 1.05")]
    public void BIOS版本里带模具码也认(string bios)
    {
        Assert.NotNull(Fields(new JsonObject { ["bios_version"] = bios }));
    }

    [Fact]
    public void BIOS里的ONLY不算NLY()
    {
        // \b 词边界：ONLY 里的 NLY 前面是字母，不是边界 —— 少了这个会误伤一大片机器。
        Assert.Null(Fields(new JsonObject { ["bios_version"] = "ONLY_TEST_1.0" }));
    }

    [Theory]
    [InlineData("NL5")]
    [InlineData("NLY")]
    [InlineData("NLXB")]
    public void 原厂注册表项目号也算(string code)
    {
        var ident = new JsonObject { ["vendor_registry"] = new JsonObject { [code] = true } };
        Assert.Equal("vendor_registry", Assert.Single(Fields(ident)!));
    }

    [Fact]
    public void 注册表键存在但为假不算()
    {
        var ident = new JsonObject { ["vendor_registry"] = new JsonObject { ["NLY"] = false } };
        Assert.Null(Fields(ident));
    }

    [Theory]
    [InlineData("雷神", "猎刃16")]
    [InlineData("雷神科技", "猎刃 16 Pro")]
    [InlineData("THUNDEROBOT", "LeiShen 16")]
    public void 雷神猎刃16凭型号名组合也认(string vendor, string product)
    {
        var ident = new JsonObject { ["system_vendor"] = vendor, ["system_product"] = product };
        Assert.NotNull(Fields(ident));
    }

    [Fact]
    public void 雷神别的型号不许认成NL5()
    {
        // 只认「猎刃 16」这一档：雷神还有别的模具（猎刃 15 等），认错了就是往别的 EC 写风扇。
        var ident = new JsonObject { ["system_vendor"] = "雷神", ["system_product"] = "猎刃15" };
        Assert.Null(Fields(ident));
    }

    [Fact]
    public void 别家的G16Pro不许认成NL5()
    {
        var ident = new JsonObject { ["system_vendor"] = "ASUS", ["system_product"] = "G16 Pro" };
        Assert.Null(Fields(ident));
    }

    [Fact]
    public void 空身份什么都不命中()
    {
        Assert.Null(Fields(new JsonObject()));
    }

    [Theory]
    [InlineData(0x33u)]
    [InlineData(0x34u)]
    [InlineData(0x35u)]
    public void SKU命中家族表就认到NL5档案(uint sku)
    {
        // 这就是"免采集"的最后一道：机型指纹一个都没中，但 EC 说自己是 0x33/0x34/0x35，
        // 于是仍然落到 NL5 家族档案上（FanControlPortable 只有这条路，连品牌串都没有）。
        // 0x34/0x35 出自原厂 daemon `(sku-52)<=1`，0x33 是第三方工具的判据。
        // 0x34 现在在家族表里判 2 路（SKU 52 的外部样本两风扇），但仍留在表里 ⇒ 照样能被认到；
        // 雷神猎刃16 有更专用的档案（priority 150），所以认到它也算命中家族 ——
        // 关键是"认到一张声明过这个 SKU 的三风扇档案"，不是写死某个 id。
        var profile = DeviceResolver.ProfileBySku(sku);
        Assert.NotNull(profile);
        Assert.Contains(profile!.Id, new[] { "quanta-nl5", "thunderobot-leiren16" });
    }

    [Theory]
    [InlineData(0x00u)]
    [InlineData(0x99u)]
    public void 没人认领的SKU不许乱认(uint sku)
    {
        // 档案没在 ec.fans_by_sku 里声明过的 SKU，一律不认 —— 否则别的模具会被当成 NL5，
        // 按错的偏移读温度（"显示错的值"比"不显示"危险得多）。
        Assert.Null(DeviceResolver.ProfileBySku(sku));
    }

    private static Device DeviceOf(string id)
        => new(DeviceResolver.AllProfiles.First(p => p.Id == id).Raw);

    [Theory]
    [InlineData(0x33u)]
    [InlineData(0x35u)]
    public void 三风扇SKU把已认出的家族档案升级到猎刃16(uint sku)
    {
        // 用户要的「靠有没有 SYS 风扇识别猎刃16」：名字/注册表只认到 NL5 家族，
        // 但 EC SKU 报三路，且猎刃16 档案写了 sku_claim_fans=3 ⇒ 升级，
        // 这样 Logo 行才会出现（家族档案没声明 kb_extra_lights）。
        var family = DeviceOf("quanta-nl5");
        var best = DeviceResolver.BetterBySku(family, sku);

        Assert.NotNull(best);
        Assert.Equal("thunderobot-leiren16", best!.Id);
        Assert.True(new Device(best.Raw).ExtraLightAddrs.ContainsKey("logo"));
    }

    [Fact]
    public void 两风扇的SKU52不许升级到猎刃16()
    {
        // 2026-11-22 外部样本（control-center-lite，隐星 G16 2024 / RTX 4070 / SKU 52 = 0x34）
        // 实测 0x0207 的 a3=0、无第三风扇 ⇒ 0x34 在 NL5 档案里改判 2 路。
        // 这台机器既没有第三风扇也没有 Logo，不能被"升"到猎刃16 那份档案上。
        Assert.Null(DeviceResolver.BetterBySku(DeviceOf("quanta-nl5"), 0x34));
    }

    [Theory]
    [InlineData(0x00u)]
    [InlineData(0x32u)]
    public void 两风扇SKU不升级(uint sku)
    {
        // 本机 G16 就走这条：家族档案已经认出，但 SKU 不是三路 ⇒ 一个字节都不动。
        Assert.Null(DeviceResolver.BetterBySku(DeviceOf("quanta-nl5"), sku));
    }

    [Fact]
    public void 猎刃16档案就是三风扇()
    {
        // 用户 2026-11-22 确认：猎刃16 的 40 系、50 系显卡版本都是三风扇 + Logo 灯。
        // 三风扇是这台机型的配置事实，直接写 `ec.fans: 3`，不靠 SKU 推 —— 0x34 已证是两风扇无 Logo。
        var dev = DeviceOf("thunderobot-leiren16");
        Assert.Equal(3, dev.Ec.Fans);
        Assert.Equal(3, dev.Caps()["fan_count"]!.GetValue<int>());
        Assert.True(dev.ExtraLightAddrs.ContainsKey("logo"));
    }

    [Fact]
    public void 已经认到猎刃16不重复升级()
    {
        Assert.Null(DeviceResolver.BetterBySku(DeviceOf("thunderobot-leiren16"), 0x33));
    }

    [Fact]
    public void 别的模具不许被SKU抢走()
    {
        // 已验证、同模具字符串不同 ⇒ 即使 SKU 表里有也不能升级（防止按错偏移读温度）。
        var raw = DeviceResolver.AllProfiles.First(p => p.Id == "thunderobot-leiren16").Raw;
        var foreign = (JsonObject)raw.DeepClone();
        foreign["id"] = "foreign-three-fan";
        foreign["mold"] = "别的模具";
        foreign["priority"] = 110;                 // 优先级仍低，排除"只是没抢过"的假通过
        Assert.Null(DeviceResolver.BetterBySku(new Device(foreign), 0x33));
    }

    [Fact]
    public void 一键采集草案的模具名与内置档案算同一家族()
    {
        // 草案的 `mold` 是 DeviceProfileDraft 拼的 "Quanta " + 项目号 = "Quanta NL5"；
        // 内置档案写 "Quanta NL5（NLXB / NLZD / NLZE / NLY）"。按整串比 ⇒ 同一台机器
        // 永远升不到正式档案（2026-11-22 对着真机草案发现的）。
        var raw = DeviceResolver.AllProfiles.First(p => p.Id == "quanta-nl5").Raw;
        var draft = (JsonObject)raw.DeepClone();
        draft["id"] = "draft-nl5";
        draft["mold"] = "Quanta NL5";
        draft["priority"] = 50;

        var best = DeviceResolver.BetterBySku(new Device(draft), 0x33);

        Assert.NotNull(best);
        Assert.Equal("thunderobot-leiren16", best!.Id);
    }
}
