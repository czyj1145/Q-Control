using Microsoft.Win32;
using QControl.Core.Devices;

namespace QControl.App;

/// <summary>
/// 把「当前这一档的配色」同步进原厂 ControlCenter 的 LED 注册表 —— **原厂存哪我们就存哪**。
///
/// 为什么必须做（2026-11-22 实测定位）：原厂 <c>ControlCenterDaemon</c> 开机启动，
/// <c>Window_Loaded → SetCurrentLED() → LEDModuleControl()</c> 会按注册表
/// <c>LEDModule</c>（本机 = 1 = PERF）取 <c>HighPerformance</c>（本机 =
/// <c>12ff0000;…</c>）**把键盘三区 + 尾灯 + Logo 全写成红色**。
/// 它启动得比我们晚（前面还有 WMI 探测 / 风扇初始化几秒），于是时间线是
/// 「我们启动恢复写完青色 → daemon 写红」，用户看到的就是"写入了蓝色，又被覆盖成红色"。
///
/// 原厂的灯效档案本来就住在这个键里（它自己的控制中心也只写这里，开机由 daemon 重放），
/// 所以对我们来说这是**唯一能持久化的地方** —— EC 的 <c>0x0100</c> 只是实时帧，
/// 断电/睡眠就回上电默认红，原厂也没指望它记住。
///
/// ⚠️ 只改这一套 LED 键，别碰风扇 / 模式那几条（那是 `Hal/Sys/OemModeRegistry` 的地盘）。
/// ⚠️ 键不存在（非 NL5 / 没装原厂）就静默跳过，**不创建**：别的机型不该被塞一棵 NL5 树。
/// 值的编解码（5 段 / 段顺序 / dt3d）在 <see cref="OemLedFormat"/>，有单测钉着。
/// </summary>
internal static class OemLedRegistry
{
    /// <summary>
    /// 原厂 NL5 ControlCenter 那棵键 —— 与 <c>Hal/Sys/OemModeRegistry.RegistryPath</c>（internal，
    /// App 看不到）同一个物理路径；LED 与 ModeBeforeDC / UserFan* 都住在这里。
    /// </summary>
    public const string RegistryPath = @"SOFTWARE\WOW6432Node\NL5\ControlCenter";

    /// <summary>原厂「当前灯效策略」指针的键名。</summary>
    public const string ModuleValue = "LEDModule";

    /// <summary>原厂四种灯效存档里，与我们四档对应的键名。</summary>
    public static string? ValueNameOf(string? modeKey) => modeKey switch
    {
        "office" => "PowerSaving",
        "game" => "Game",
        "beast" => "HighPerformance",
        "custom" => "Custom",
        _ => null,
    };

    /// <summary>
    /// 原厂 <c>LEDModule</c>（<c>REG_MAIN.LED_POLICY</c>）的取值：
    /// 枚举 <c>REG_LED_MODULE</c> = GAME(0) / PERF(1) / AUDIO(2) / CUSTOM(3)。
    /// </summary>
    public static uint? ModuleOf(string? modeKey) => modeKey switch
    {
        "game" => 0,
        "beast" => 1,
        "office" => 2,
        "custom" => 3,
        _ => null,
    };

    private static bool _missingLogged;

    /// <summary>
    /// 写一份档案到原厂注册表。
    /// </summary>
    /// <param name="setModule">
    /// 只有**键盘上正在显示的那一档**才改 <c>LEDModule</c> —— 在别的 tab 上配色只记档案，
    /// 改了这个值等于对原厂谎报"现在显示的是这一档"。
    /// </param>
    /// <returns>注册表这边有没有写成功（不参与"灯写没写进 EC"的判定）。</returns>
    public static bool Mirror(string modeKey, BacklightProfile profile, bool setModule)
    {
        var name = ValueNameOf(modeKey);
        if (name is null) return false;

        try
        {
            // 与原厂同一个视图：它是 32 位程序，写的是 WOW6432Node 的物理路径，
            // 这里钉 64 位视图才不会被动过一次 WOW64 重定向。
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath, writable: true);
            if (key is null)
            {
                if (!_missingLogged)
                {
                    _missingLogged = true;
                    Console.WriteLine("[light/oem] 本机没有原厂 ControlCenter 注册表键，跳过灯效同步"
                                      + "（原厂 daemon 不会替我们重放灯，无需镜像）");
                }
                return false;
            }

            var current = key.GetValue(name) as string;
            var value = OemLedFormat.Build(current, profile);
            var module = setModule ? ModuleOf(modeKey) : null;
            var currentModule = key.GetValue(ModuleValue) is int m ? (uint)m : (uint?)null;

            // 已经一致就别写（每次重放都刷四条日志会把排障信息淹掉，也没必要动注册表）
            if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase)
                && (module is not { } want || currentModule == want))
                return true;

            key.SetValue(name, value, RegistryValueKind.String);
            if (module is { } v) key.SetValue(ModuleValue, v, RegistryValueKind.DWord);

            Console.WriteLine($"[light/oem] 已同步原厂「{name}」= {value}"
                              + (module is { } mm
                                  ? $"（LEDModule={mm}，daemon 下次重放就是这套颜色）"
                                  : "（只记档案，不动 LEDModule）"));
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[light/oem] 同步原厂灯效失败：" + e.Message);
            return false;
        }
    }

    /// <summary>
    /// 只把 <c>LEDModule</c> 指向某一档（键盘上正在显示的那一档）。
    /// 值没变就不写 —— 这是原厂的"当前灯效策略"指针，多写一次没意义。
    /// </summary>
    public static void SetModule(string? modeKey)
    {
        if (ModuleOf(modeKey) is not { } want) return;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath, writable: true);
            if (key is null) return;
            if (key.GetValue(ModuleValue) is int i && (uint)i == want) return;
            key.SetValue(ModuleValue, want, RegistryValueKind.DWord);
            Console.WriteLine($"[light/oem] 原厂当前灯效策略 → LEDModule={want}（{ValueNameOf(modeKey)}）");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[light/oem] 写 LEDModule 失败：" + e.Message);
        }
    }

    /// <summary>
    /// 从原厂注册表读回某一档的配色（我们这边这一档**还没有档案**时用它播种 ——
    /// "原厂存哪我们就存哪"，换个装置/重装后不必让用户重新调一遍色）。
    ///
    /// <paramref name="addrs"/> 是键盘分区地址（本机 A/B/C = 5/4/3，降序），原厂值里只有颜色、
    /// 没有地址，所以地址必须来自机型档案 / `presets.backlight.zones`，绝不猜。
    /// 读不成 / 全是 0 / 段数不够 ⇒ null（由调用方保持"缺档不凑"）。
    /// </summary>
    public static BacklightProfile? Read(string modeKey, IReadOnlyList<int> addrs, int maxZones = 0)
    {
        var name = ValueNameOf(modeKey);
        if (name is null || addrs.Count == 0) return null;

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath);
            return key?.GetValue(name) is string raw
                ? OemLedFormat.Parse(raw, addrs, maxZones)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
