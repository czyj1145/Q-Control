using System.Text.Json.Nodes;
using QControl.Core.Model;

namespace QControl.Core.Devices;

/// <summary>
/// 一份「某个系统模式下的键盘背光档案」：档位 + 灯效 + 各分区颜色。
///
/// 为什么需要它：EC 的 `0x0100` 只接受"当场这一帧"的灯效数据，**它自己不会按性能档切换**
/// （原厂控制中心也没做，这是它的缺陷）。所以"切模式换灯效"只能由我们记住**四套**配置、
/// 在切档时重放一次：办公 / 游戏 / 狂暴，再加原厂那套 `custom`（自定义接管时用它）。
/// </summary>
/// <param name="Level">亮度档位 0/1/2（EC 只认三档）。</param>
/// <param name="Effect">灯效码 0~15（1 常亮 / 3 呼吸 / 6 多彩循环 / 7 流光溢彩）。</param>
/// <param name="Zones">按 EC 地址排的各分区颜色（至少 1 条）。</param>
/// <param name="Logo">Logo 灯颜色（EC 槽位由机型档案声明）；null = 这台机器没有 / 没记过。</param>
/// <param name="Tail">尾灯颜色（同上；**当前已声明机型都没有尾灯**，这一项留给将来真有的档案）。这两路不属于键盘分区表。</param>
public sealed record BacklightProfile(int Level, int Effect, (int Addr, int[] Rgb)[] Zones,
                                      int[]? Logo = null, int[]? Tail = null);

/// <summary>
/// 「某档灯效档案」的解析 / 序列化 / 落地 —— **纯函数，不碰 EC、不碰文件**。
///
/// 档案存在**档位那一整包里**（见 <see cref="ModeBundle"/>）：
/// <code>
/// "perf": { "modes": {
///   "office": { "apmd": 2, …, "light": { "effect": 1, "level": 1,
///                                         "zones": [{"addr":5,"rgb":[255,0,0]}, …] } },
///   "game": { … }, "beast": { … },
///   "custom": { "light": { … } } } },   // 第四套：不是系统档，只放灯效
/// "backlight": { "custom_override": false,
///                "effect": …, "zones": [ … ] }   // 当前生效（EC 里现在是什么）
/// </code>
///
/// 三条规矩（都踩过）：
/// <list type="number">
/// <item><b>缺档 ≠ 报错</b>：某档没有档案时**什么都不写**（保持现状），绝不拿别的档去凑。
///   否则"我只调过办公档"会变成"四套全被办公的配色覆盖"。</item>
/// <item><b>读进来就钳制</b>：配置是可以被记事本手改的，`level` 写成 5、`rgb` 写成 300、
///   地址写成 0（EC 上写 0 = 整条灯灭）都必须当场收进合法范围，不能原样下发。</item>
/// <item><b>分区数受机型约束</b>：`caps.kb_zones` 说几个区就最多几条 —— 探针试色把表撑大的
///   事故在 <see cref="BacklightZones"/> 那边已经踩过一次。</item>
/// </list>
/// </summary>
public static class BacklightModes
{
    /// <summary>
    /// 四套灯效档案的键：`office` / `game` / `beast` / `custom`（顺序 = 界面上的四颗 chip）。
    /// ⚠️ 前三套能跟系统档走；`custom` 只在「自定义接管」打开时被重放。
    /// </summary>
    public static readonly string[] Keys = ModeBundle.LightKeys;

    /// <summary>灯效档案键归一：前三档 + `custom`；野值一律 null。</summary>
    public static string? Key(string? perfKey) => ModeBundle.IsLightKey(perfKey) ? perfKey : null;

    /// <summary>
    /// 切系统档时真正该重放哪一套：自定义接管开着且系统档有效 ⇒ `custom`，否则原系统档。
    /// 系统档读不到时返回 null（什么都不写）—— 接管不是"随便拿个档重放"的借口。
    /// ⚠️ 只决定**读哪一份档案**；绝不把当前灯色写进目标档案（原厂点 Custom 的语义）。
    /// </summary>
    public static string? ReplayKey(string? perfKey, bool customOverride)
    {
        if (!ModeBundle.IsKey(perfKey)) return null;
        return customOverride ? "custom" : perfKey;
    }

    /// <summary>
    /// 在背光页调某一档时，这笔改动要不要**当场下发到键盘**：只有"编辑的这一档"就是
    /// "键盘上正在显示的那一套"才算。编辑别的档只记档案，切过去才生效。
    ///
    /// <paramref name="liveKey"/> 必须是**已经过 <see cref="ReplayKey"/> 解析**的那一套
    /// （自定义接管开着时它是 `custom`，不是系统档），否则接管状态下会误判。
    /// 两边任一读不到就按"下发"处理 —— 那是老行为，且至少不会把用户的改动丢掉。
    /// </summary>
    public static bool AppliesLive(string? editKey, string? liveKey)
        => editKey is null || liveKey is null || editKey == liveKey;

    /// <summary>档位中文短名（界面 chip / 日志 / toast 共用一处，别各写一份）。</summary>
    public static string Label(string? key) => ModeBundle.LightLabel(key);

    /// <summary>
    /// 「编辑的不是当前档」时，这一档该记哪个亮度（纯函数）。
    ///
    /// 背景：界面亮度滑块平时显示的是 **EC 当前值**（键盘上这一档多亮），拿它当"那一档的记忆"
    /// 会串档 —— 在「办公」tab 上只调个颜色，就把办公档的亮度改成当前这档的。
    /// 所以默认**不吃**请求里的 level；只有用户在那一档的 tab 上真的拖了亮度
    /// （界面带 `level_explicit`）才算对那一档的明确设定。
    ///
    /// 返回 null = **别动**这一档已有的 level 键。
    /// </summary>
    public static int? DraftLevel(bool explicitLevel, int? requested, bool hasExisting, int? ecLevel)
    {
        if (explicitLevel)
            return Math.Clamp(requested ?? ecLevel ?? 2, 0, 2);
        if (hasExisting) return null;
        return ecLevel is >= 0 and <= 2 ? ecLevel : 2;
    }

    /// <summary>
    /// 某一档的**自动熄灯策略**：闲置 N 分钟 / 锁屏就熄。跟着配色档案一起走
    /// （三个系统档 + `custom` 各一套，切到哪一档就用哪一档的策略）。
    /// </summary>
    public readonly record struct AutoOff(int Minutes, bool OnLock);

    /// <summary>
    /// 读某档**自己**的自动熄灯策略（`perf.modes[档].light` 里的 `auto_off_min` / `auto_off_on_lock`）。
    ///
    /// 两个键都没有 ⇒ null = **这一档没设过**，调用方退回全局默认（`presets.light`）。
    /// 这与配色的"缺档不凑"同一条精神：没设过的档不该被别的档的策略粘住。
    /// 只写了一个键时，另一个按 0 / false 读 —— 半个存在也算设过。
    /// </summary>
    public static AutoOff? AutoOffOf(JsonObject? presets, string? mode)
    {
        if (ModeBundle.LightBlock(presets, mode) is not JsonObject block) return null;
        if (block["auto_off_min"] is null && block["auto_off_on_lock"] is null) return null;

        var minutes = NumOf(block["auto_off_min"]) is { } m ? (int)Math.Clamp(Math.Round(m), 0, 60) : 0;
        return new AutoOff(minutes, Device.Truthy(block["auto_off_on_lock"]));
    }

    /// <summary>
    /// 把自动熄灯策略写进某档的灯效档案（两个键一起写）。灯效块还不存在时会建一个空块 ——
    /// 空块只带策略、没有 `zones`，读回来仍是"这一档还没有配色档案"，缺档不凑照样成立。
    /// </summary>
    public static bool SetAutoOff(JsonObject presets, string mode, AutoOff value)
    {
        if (!ModeBundle.IsLightKey(mode)) return false;
        if (ModeBundle.EnsureLightBlock(presets, mode) is not { } block) return false;

        block["auto_off_min"] = Math.Clamp(value.Minutes, 0, 60);
        block["auto_off_on_lock"] = value.OnLock;
        return true;
    }

    /// <summary>
    /// 读某档的灯效档案 —— 位置是 **`presets.perf.modes[档].light`**（档位一整包，2026-11-17 归位）。
    /// 没有 / 读不成 / 一条分区都凑不出来 ⇒ null（调用方保持现状，绝不拿别的档凑）。
    /// </summary>
    public static BacklightProfile? ForMode(JsonObject? presets, string? mode, int maxZones = 0)
        => Parse(ModeBundle.LightBlock(presets, mode), maxZones);

    /// <summary>
    /// 解析一份档案 JSON（严格）：任何一项不合法都退回默认值或判整份不可用。
    /// <paramref name="maxZones"/> &gt; 0 时按它裁分区数（机型声明）；0 = 不裁。
    /// </summary>
    public static BacklightProfile? Parse(JsonNode? node, int maxZones = 0)
    {
        if (node is not JsonObject o) return null;

        var level = Bounds.Clamp("backlight_level", (int)Math.Round(NumOf(o["level"]) ?? 2));
        var effect = Math.Clamp((int)Math.Round(NumOf(o["effect"]) ?? 1), 0, 15);

        var zones = ParseZones(o["zones"], maxZones);
        if (zones.Count == 0) return null;

        return new BacklightProfile(level, effect, zones.ToArray(), Rgb(o["logo"]), Rgb(o["tail"]));
    }

    /// <summary>
    /// 把「当前生效的」`presets.backlight` 读成一份档案（用于开启跟随时给当前档播种）。
    /// 兼容老结构：没有 `zones` 就退回单色 `rgb` + 整条地址 `addr`。
    /// </summary>
    public static BacklightProfile? Snapshot(JsonObject? backlight, int? level, int maxZones = 0)
    {
        if (backlight is null) return null;

        var lvl = Bounds.Clamp("backlight_level", level ?? 2);
        var effect = Math.Clamp((int)Math.Round(NumOf(backlight["effect"]) ?? 1), 0, 15);

        var zones = ParseZones(backlight["zones"], maxZones);
        if (zones.Count == 0)
        {
            // 老结构：一个 addr + 一个 rgb（整条灯带同色）
            if (NumOf(backlight["addr"]) is { } addr && addr is >= 1 and <= 255
                && Rgb(backlight["rgb"]) is { } legacy)
                zones.Add(((int)addr, legacy));
        }
        if (zones.Count == 0) return null;

        return new BacklightProfile(lvl, effect, zones.ToArray(),
            Rgb(backlight["logo"]), Rgb(backlight["tail"]));
    }

    /// <summary>
    /// 原厂出厂 `LED_Custom = "1100FFFF;1100FFFF;1100FFFF;1100FFFF;1100FFFF"` ——
    /// 常亮(1) / 50%(1) / 全白，分区地址沿用当前生效表（颜色一律不沿用！）。
    /// 只给**从来没有过 Custom 档案**的机器补默认，绝不拿当前灯色播种 —— 原厂点 Custom
    /// 就是加载 Custom 自己的存档（`LEDModuleControl → GetRegistryStr(get_led_policy())`）。
    /// </summary>
    public static BacklightProfile? OemCustomDefault(JsonObject? backlight, int maxZones = 0,
                                                     bool hasLogo = false, bool hasTail = false)
    {
        var live = Snapshot(backlight, level: 1, maxZones);
        if (live is null) return null;

        return live with
        {
            Effect = 1,
            Zones = live.Zones.Select(z => (z.Addr, new[] { 255, 255, 255 })).ToArray(),
            Logo = hasLogo ? new[] { 255, 255, 255 } : null,
            Tail = hasTail ? new[] { 255, 255, 255 } : null,
        };
    }

    /// <summary>档案 → 可落盘的 JSON（`{effect, level, zones:[{addr,rgb}]}`）。</summary>
    public static JsonObject ToJson(BacklightProfile p)
    {
        var zones = new JsonArray();
        foreach (var (addr, rgb) in p.Zones)
        {
            zones.Add(new JsonObject
            {
                ["addr"] = addr,
                ["rgb"] = new JsonArray(rgb[0], rgb[1], rgb[2]),
            });
        }
        var root = new JsonObject
        {
            ["effect"] = p.Effect,
            ["level"] = p.Level,
            ["zones"] = zones,
        };
        // 没记过独立灯珠的机型不要凭空造键（老机型配置保持逐字节不变）
        if (p.Logo is { } logo) root["logo"] = new JsonArray(logo[0], logo[1], logo[2]);
        if (p.Tail is { } tail) root["tail"] = new JsonArray(tail[0], tail[1], tail[2]);
        return root;
    }

    /// <summary>
    /// 把档案写回 `presets.backlight`（`effect` + `zones`），**保留原有分区的 `name`**。
    ///
    /// 为什么要管 name：界面 tab 的 A/B/C 名字来自这张表（<see cref="BacklightZones"/> 那边
    /// 特意"只补缺不覆盖"就是为了它）。重放时整表重建若丢了 name，下次调色就又变回没名字。
    /// </summary>
    /// <returns>写入的分区条数。</returns>
    public static int WriteSection(JsonObject backlight, BacklightProfile p)
    {
        var names = new Dictionary<int, string>();
        if (backlight["zones"] is JsonArray old)
        {
            foreach (var node in old)
            {
                if (node is not JsonObject zo) continue;
                if (NumOf(zo["addr"]) is not { } a) continue;
                if (zo["name"] is JsonValue nv && nv.TryGetValue<string>(out var nm) && nm.Length > 0)
                    names[(int)a] = nm;
            }
        }

        var zones = new JsonArray();
        foreach (var (addr, rgb) in p.Zones)
        {
            var zo = new JsonObject
            {
                ["addr"] = addr,
                ["rgb"] = new JsonArray(rgb[0], rgb[1], rgb[2]),
            };
            if (names.TryGetValue(addr, out var name)) zo["name"] = name;
            zones.Add(zo);
        }

        backlight["effect"] = p.Effect;
        backlight["zones"] = zones;
        // 有档案才写；没有的机型不碰这两个键（不替用户清空别人的灯）
        if (p.Logo is { } logo) backlight["logo"] = new JsonArray(logo[0], logo[1], logo[2]);
        if (p.Tail is { } tail) backlight["tail"] = new JsonArray(tail[0], tail[1], tail[2]);
        return zones.Count;
    }

    /// <summary>两份档案是否等价（档位 / 灯效 / 各地址颜色）。用于"要不要真写一次"的判断。</summary>
    public static bool Same(BacklightProfile? a, BacklightProfile? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Level != b.Level || a.Effect != b.Effect) return false;
        if (!SameRgb(a.Logo, b.Logo) || !SameRgb(a.Tail, b.Tail)) return false;
        if (a.Zones.Length != b.Zones.Length) return false;

        foreach (var (addr, rgb) in a.Zones)
        {
            var match = false;
            foreach (var (addr2, rgb2) in b.Zones)
            {
                if (addr2 != addr) continue;
                match = rgb[0] == rgb2[0] && rgb[1] == rgb2[1] && rgb[2] == rgb2[2];
                break;
            }
            if (!match) return false;
        }
        return true;
    }

    // ================================================================ 内部

    private static bool SameRgb(int[]? a, int[]? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Length == b.Length && a.Zip(b).All(p => p.First == p.Second);
    }

    private static List<(int Addr, int[] Rgb)> ParseZones(JsonNode? node, int maxZones)
    {
        var list = new List<(int Addr, int[] Rgb)>();
        if (node is not JsonArray arr) return list;

        var seen = new HashSet<int>();
        foreach (var z in arr)
        {
            if (z is not JsonObject zo) continue;
            if (NumOf(zo["addr"]) is not { } rawAddr) continue;

            // 地址 0 在 EC 上是"全部灯"（会连带 logo/灯条），单分区档案里不允许出现
            if (rawAddr < 1) continue;
            var addr = Bounds.Clamp("backlight_alpha", (int)Math.Round(rawAddr));
            if (!seen.Add(addr)) continue;

            var rgb = Rgb(zo["rgb"]) ?? [255, 255, 255];
            list.Add((addr, rgb));

            if (maxZones > 0 && list.Count >= maxZones) break;
        }
        return list;
    }

    /// <summary>`[r,g,b]` → 钳制后的三元组；不是三元素数组就 null。</summary>
    private static int[]? Rgb(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count < 3) return null;
        return
        [
            Bounds.Clamp("backlight_rgb", (int)Math.Round(NumOf(arr[0]) ?? 255)),
            Bounds.Clamp("backlight_rgb", (int)Math.Round(NumOf(arr[1]) ?? 255)),
            Bounds.Clamp("backlight_rgb", (int)Math.Round(NumOf(arr[2]) ?? 255)),
        ];
    }

    /// <summary>
    /// 读一个数字节点。⚠️ 统一走 <see cref="Bounds.TryToDouble"/>（不用 `TryGetValue&lt;double&gt;`）：
    /// 配置里的值可能是 JsonElement（磁盘读回）也可能是强类型 int（本进程写回），两者不互通。
    /// </summary>
    private static double? NumOf(JsonNode? node)
        => Bounds.TryToDouble(node, out var v) ? v : null;
}
