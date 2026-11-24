using Microsoft.Win32;

namespace QControl.Hal.Sys;

/// <summary>
/// 注册表读取的小工具。**只读** —— 写注册表的地方一律写在本类外面，
/// 好让"这文件永远不会改系统"这件事一眼可查。
///
/// ⚠️ 32/64 位视图：`HKCU\...\Run` 和 `HKLM\SYSTEM\CurrentControlSet\Services`
/// 在 64 位系统上默认就读 64 位视图，和早先实现的 `winreg`（同样默认）一致。
/// 需要 Wow6432Node 的地方调用方自己拼路径（`MachineIdent` 就是这么做的）。
/// </summary>
internal static class Reg
{
    /// <summary>读字符串值；键/值不存在或读不了都返回 null（不抛）。</summary>
    public static string? Str(RegistryKey root, string subKey, string name)
    {
        try
        {
            using var k = root.OpenSubKey(subKey);
            return k?.GetValue(name)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读 DWORD。⚠️ 用 `ToString()` 再 parse：类型可能是 int 也可能是 long。</summary>
    public static int? Int(RegistryKey root, string subKey, string name)
    {
        var raw = Str(root, subKey, name);
        return int.TryParse(raw, out var v) ? v : null;
    }

    /// <summary>键是否存在。</summary>
    public static bool Exists(RegistryKey root, string subKey)
    {
        try
        {
            using var k = root.OpenSubKey(subKey);
            return k is not null;
        }
        catch
        {
            return false;
        }
    }
}
