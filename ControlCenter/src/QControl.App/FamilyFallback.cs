using System.Text.Json.Nodes;
using QControl.Core.Devices;
using QControl.Core.Model;
using QControl.Hal.Ec;
using QControl.Hal.Wmi;

namespace QControl.App;

/// <summary>
/// 按 EC 自报的 SKU 认家族/升级到更专用的档案，而不是让用户去跑采集。
///
/// <para>
/// 为什么需要这一层：同一模具换牌子（七彩虹 隐星 G16 Pro / 雷神 猎刃 16）时，
/// SMBIOS 型号名和原厂注册表键都会变，`match` 里的指纹完全可能一个都不中 ——
/// 于是落到通用档案：没有温度偏移、写入默认关闭，等于"必须采集一遍"。
/// 但 EC 本身是同一套固件，它照样会回答"我是哪个型号"（`0x0201`）。
/// </para>
/// <para>
/// 这不是我们发明的路子：那份第三方控制台（FanControlPortable）二进制里**一个品牌串都没有**
/// （雷神/猎刃/COLORFUL/NLXB 全查不到），它压根不认机型，只按 SKU 判"是不是三风扇"。
/// 我们这里把它补齐：SKU 命中哪份档案的 `ec.fans_by_sku`，就用哪份档案。
/// </para>
/// <para>
/// 三重收口，防止"认错模具然后按错的偏移读温度"：
/// <list type="number">
///   <item>只在档案**自己声明了**这个 SKU 时才认领（没写 `fans_by_sku` 的档案永不参选）；</item>
///   <item>已验证机型还要目标档案写明 `sku_claim_fans`、同模具且优先级更高才升级
///     —— 没这个键的机型（包括本机两风扇 G16）一个字节都不动；</item>
///   <item>换档案后 `matched_by` 里追加 <c>ec_sku</c>，界面/采集包能一眼看出"这条路是 SKU 认的"，
///     原来的名字/模具码命中依据仍保留。</item>
///   <item>**独立灯珠（Logo/尾灯）不跟这条路走**：SKU 只说得出"几路风扇"，说不出"有没有那颗灯"。
///     升级过来的档案一律 <c>extraLightsByProfile:false</c> ⇒ 不给灯；要让它有灯，
///     得靠名字命中（改档案 `match` 或放用户档案），**没有用户开关**（2026-11-18 撤掉了）。</item>
/// </list>
/// </para>
/// </summary>
internal static class FamilyFallback
{
    /// <summary>
    /// 探一次 SKU；命中就把机型换成档案里 Claim 了它的那份。
    /// 返回 null = 没换成（读不到 SKU / 没有档案认领 / 传输失败），调用方保持原机型。
    /// </summary>
    public static Device? TryResolveBySku(Device current, ModelStore model)
    {
        // 档案没声明 0x0201 就没得探（未适配的模具连命令表都可能没有）
        if (current.Ec.Sub("sku") < 0) return null;

        uint? sku;
        try
        {
            // 临时一套 GmWmi/EcHal：只用它的读路径拿 SKU，拿到就丢。
            // 为什么不在正式实例上探：正式的 EcHal 是用**当前档案**造的，
            // 换档案必须连带换 EcSpec，只能重建。
            // ⚠️ 只 `using` EcHal —— 它的 Dispose 会关掉 GmWmi（见 EcHal.Dispose），
            //    两边都 using 就是同一个对象关两次。
            var io = new GmWmi(current.Ec);
            using var ec = new EcHal(io, () => current, model);
            sku = ec.ProbeSkuOnce();
        }
        catch
        {
            return null;                                  // 连不上/读不到：保持原样，不进这条兜底
        }

        if (sku is not { } value) return null;

        var profile = DeviceResolver.BetterBySku(current, value);
        if (profile is null) return null;

        // 保留原来的命中依据，只追加一条 ec_sku —— 界面上能同时看到
        // 「家族码命中的」和「三风扇 SKU 升级的」，不是把旧证据抹掉。
        var fields = current.MatchedBy.Concat(["ec_sku"]).Distinct().ToList();
        var block = current.Reason?.Block is { } old ? (JsonObject)old.DeepClone() : new JsonObject();
        block["ec_sku"] = value;

        // ⚠️ 独立灯珠不跟着 SKU 升级走：SKU 只回答"几路风扇"，不回答"有没有 Logo"。
        //    同模具三风扇机一大堆，其中大头没有 Logo 灯 —— 这里给 true 就等于在别人的
        //    背光页上摆一个点了没反应的色块。只有名字命中（Resolve 直出）那条路才带灯；
        //    真机名字认不出来但确实有灯的，**改档案**（match 正则 / 用户档案），不设开关。
        return new Device(profile.Raw, new MatchResult(fields, block), current.EcOverride,
                          extraLightsByProfile: false);
    }
}
