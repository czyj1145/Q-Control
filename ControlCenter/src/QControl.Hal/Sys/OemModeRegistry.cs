using System.Globalization;
using Microsoft.Win32;

namespace QControl.Hal.Sys;

/// <summary>
/// 原厂 ControlCenter 的「系统模式」真源：
/// <c>HKLM\SOFTWARE\WOW6432Node\NL5\ControlCenter\ModeBeforeDC</c>（DWORD）。
///
/// 实证（2026-11-18，ilspycmd 反编译本机 <c>ControlCenter.exe</c>）：
///   · <c>GMRegisty.REG_MAIN.POWERPLAN</c> 就是枚举值 11 → 值名 <c>ModeBeforeDC</c>，
///     取值 0=狂暴 / 1=游戏 / 2=办公，与 <c>0x0300</c> 写进 EC 的 APMD **数值完全相同**；
///   · <c>Window_Loaded</c> → <c>PowerLineStatusCheck()</c> 的插电分支读它，按值调用
///     <c>power_high/gaming/audio_click_func()</c> → <c>Set_power_plan_immediately()</c>
///     → <c>SetPowerMode(0x0300)</c>：**把 EC 的 APMD 重放成注册表里的旧值**；
///   · FN 性能热键算下一档也是从这个值起步（<c>registry = GetRegistry(POWERPLAN)</c>）。
///
/// ⇒ 这就是「我们切了模式，一打开原厂就被覆盖回去」的原因：我们只写 EC，
///   注册表里还是上一次原厂/CCLite 留下的档。CCLite（control-center-lite）能被原厂读到，
///   是因为它的 <c>PowerModeService.SetMode</c> 在写 EC 之后额外写了这个值。
///
/// ⚠️ 只在**用户主动切档**成功后镜像（界面按钮 / <c>/api/ec/perf</c> / FN 热键）。
///    拔电自动切办公**不写**：原厂自己在 DC 模式下 <c>power_audio_click_func()</c>
///    也刻意不写它（插电时要靠这个值恢复拔电前那一档），写了会把恢复基准冲掉。
///
/// ⚠️ 键不存在（非 NL5 / 没装原厂）就静默跳过，**不要创建**：别的机型不该被塞一棵 NL5 树。
///    写失败也只记日志 —— 切档成败的判据是 EC 的写后读回，不是注册表权限。
/// </summary>
internal static class OemModeRegistry
{
    /// <summary>原厂 NL5 ControlCenter 那棵注册表键（ModeBeforeDC 与 UserFan* 共用）。</summary>
    public const string RegistryPath = @"SOFTWARE\WOW6432Node\NL5\ControlCenter";

    public const string ValueName = "ModeBeforeDC";

    /// <summary>原厂三档的最大合法值（<c>perf.modes[*].apmd</c> 抓包实证恒 0/1/2）。</summary>
    public const uint MaxMode = 2;

    /// <summary>读当前值（诊断用；键/值不存在或读不了都返回 null，不抛）。</summary>
    public static uint? Read()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath);
            var raw = key?.GetValue(ValueName);
            return raw is not null
                   && uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                       NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把已经写进 EC 且**读回校验通过**的 APMD 同步到原厂真源。
    /// 返回值只表示「注册表这边有没有对齐」，不参与切档成败判定。
    /// </summary>
    public static bool Sync(uint apmd)
    {
        if (apmd > MaxMode)
        {
            Console.Error.WriteLine($"[perf/oem] APMD {apmd} 超出原厂三档 0..{MaxMode}，跳过 ModeBeforeDC 同步");
            return false;
        }

        try
        {
            // 显式钉 64 位视图：原厂是 32 位程序，它写的是 HKLM\SOFTWARE\WOW6432Node\...
            // 的物理路径；我们不管编译成 x64 还是 AnyCPU 都认这一棵，别被 WOW64 再重定向一次。
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath, writable: true);
            if (key is null)
            {
                Console.WriteLine("[perf/oem] 本机没有原厂 ControlCenter 注册表键，跳过 ModeBeforeDC 同步");
                return false;
            }

            var raw = key.GetValue(ValueName);
            if (raw is not null
                && uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var old)
                && old == apmd)
                return true;                     // 已经一致：不重复写，免得无谓触发注册表事件

            key.SetValue(ValueName, (int)apmd, RegistryValueKind.DWord);
            Console.WriteLine($"[perf/oem] ModeBeforeDC {raw?.ToString() ?? "(无)"} → {apmd}"
                               + "（与 EC APMD 对齐，原厂打开时不会覆盖回去）");
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[perf/oem] 同步 {ValueName}={apmd} 失败：{e.Message}"
                                    + "（EC 档位已切换成功，仅原厂显示/恢复档不同步）");
            return false;
        }
    }
}
