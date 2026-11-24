using System.Globalization;
using Microsoft.Win32;

namespace QControl.Hal.Sys;

/// <summary>
/// 原厂 ControlCenter 的「Win 键禁用」真源：
/// <c>HKLM\SOFTWARE\WOW6432Node\NL5\ControlCenter\WinKey</c>（DWORD，<b>1 = 禁用</b>）。
///
/// <para>
/// 实证（2026-11-22，ilspycmd 反编译本机 <c>ControlCenter.exe</c> 的 <c>SetWinKey(bool IsEnabled)</c>）：
/// <c>0x0200</c> 写组、<c>a6 = IsEnabled ? 0 : 1</c>（0 = Win 键可用，1 = 屏蔽），
/// 写成功后 <c>GMRegisty.SetRegistry(7, …)</c> 同步同一个值 —— 枚举 7 就是 <c>WinKey</c>。
/// </para>
///
/// <para>
/// ⚠️ 为什么必须持久化：<c>0x0200</c> 的**读侧 a6 是键盘亮度档**（本机实测恒 0..2），
/// 不是 Win 键状态 ⇒ 这条命令没有读回通道。不落一个值，程序重启后就没法知道
/// EC 里到底锁着没有，界面只能靠猜（那等于撒谎）。
/// </para>
///
/// <para>
/// ⚠️ 键不存在（非 NL5 / 没装原厂）就静默跳过、**不创建**：别的机型不该被塞一棵 NL5 树
/// （同 <see cref="OemModeRegistry"/>）。
/// </para>
/// </summary>
internal static class OemWinKey
{
    private const string RegistryPath = @"SOFTWARE\WOW6432Node\NL5\ControlCenter";

    public const string ValueName = "WinKey";

    /// <summary>读上一次记录的状态。true = 禁用（锁着）；null = 没有这个值 / 读不了。</summary>
    public static bool? Read()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath);
            var raw = key?.GetValue(ValueName);
            if (raw is null) return null;
            return uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v != 0
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把已经写进 EC 的状态同步到原厂真源。返回值只表示「注册表这边有没有对齐」，
    /// 不参与锁/解锁的成败判定（判据是 EC 那条 SMI 有没有发成功）。
    /// </summary>
    public static bool Sync(bool disabled)
    {
        try
        {
            // 与 OemModeRegistry 一样显式钉 64 位视图：原厂是 32 位程序，写的是
            // WOW6432Node 的物理路径，别让 WOW64 再重定向一次。
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(RegistryPath, writable: true);
            if (key is null)
            {
                Console.WriteLine("[winkey] 本机没有原厂 ControlCenter 注册表键，跳过 WinKey 同步");
                return false;
            }

            var want = disabled ? 1 : 0;
            var raw = key.GetValue(ValueName);
            if (raw is not null
                && uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var old)
                && old == (uint)want)
                return true;                     // 已经一致：不重复写

            key.SetValue(ValueName, want, RegistryValueKind.DWord);
            Console.WriteLine($"[winkey] WinKey {raw?.ToString() ?? "(无)"} → {want}（1 = 禁用、0 = 可用）");
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[winkey] 同步 {ValueName}={disabled} 失败：{e.Message}"
                                    + "（EC 已写成功，仅重启后无法恢复状态）");
            return false;
        }
    }
}
