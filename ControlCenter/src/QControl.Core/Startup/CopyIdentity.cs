namespace QControl.Core.Startup;

/// <summary>
/// 「这份 exe 是不是本程序的另一份副本」—— 只做判定，不碰系统状态。
///
/// **为什么需要（2026-10-30 真机实测）**：同一个产品可能同时存在两份可执行文件 ——
/// 安装版（`%ProgramFiles%\Q-Control`）与绿色版（用户解压到 Downloads 的任意目录）。
/// 开机自启是**全机唯一**的一条入口（计划任务 `QControl`），于是：
///   · 14:05 绿色版先启动 → 任务被写成指向绿色版；
///   · 14:06 安装版装好并启动 → `EnsureDefault` 的原则是"不重写已有任务" → 任务照旧指着绿色版；
///   · 此后每次双击安装版：`RunNow()` 用任务拉起的是**绿色版**，安装版自己叫醒窗口后退出。
/// 表现就是「明明双击的是安装版，跑的一直是另一份，放在安装目录里的 HWiNFO 永远看不见」。
///
/// 判据刻意保守（三条同时成立才算），因为一旦判错就会去改用户的开机程序：
///   ① 文件名与主程序同名（`QControl.exe`）—— 指向别的程序一律不算；
///   ② 目录不同 —— 同一目录就是它自己，没什么可改的；
///   ③ 对方目录里也有 `QControl.dll` —— 证明那是本程序的完整副本，
///      而不是某个恰好同名的其它工具。
/// </summary>
public static class CopyIdentity
{
    /// <summary>主程序文件名。全项目只有这一个入口 exe（安装器/引导器也认这个名字）。</summary>
    public const string MainExeName = "QControl.exe";

    /// <summary>主程序集文件名。副本身份的第二条凭证（绿色版的 app\ 与安装目录都有它）。</summary>
    public const string CoreAssemblyName = "QControl.dll";

    /// <summary>
    /// <paramref name="otherExe"/> 是不是「同一个程序的另一份副本」。
    ///
    /// <paramref name="exists"/> 只给单测替换用（默认走真实文件系统）。
    /// </summary>
    public static bool IsSameProductCopy(string? otherExe, string mineExe,
                                         Func<string, bool>? exists = null)
    {
        if (string.IsNullOrWhiteSpace(otherExe) || string.IsNullOrWhiteSpace(mineExe))
            return false;

        if (!Path.GetFileName(otherExe).Equals(MainExeName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Path.GetFileName(mineExe).Equals(MainExeName, StringComparison.OrdinalIgnoreCase))
            return false;

        string otherDir, mineDir;
        try
        {
            otherDir = Path.GetFullPath(Path.GetDirectoryName(otherExe) ?? "");
            mineDir = Path.GetFullPath(Path.GetDirectoryName(mineExe) ?? "");
        }
        catch
        {
            return false;   // 非法路径（含通配符、坏盘符）：不猜
        }

        if (otherDir.Length == 0 || mineDir.Length == 0) return false;
        if (string.Equals(otherDir.TrimEnd('\\'), mineDir.TrimEnd('\\'),
                          StringComparison.OrdinalIgnoreCase))
            return false;

        var probe = exists ?? File.Exists;
        try
        {
            return probe(Path.Combine(otherDir, CoreAssemblyName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 两个路径是否指向**同一个文件**。给「任务指向 == 我这份吗」这类比较用：
    /// COM 返回的路径与 `AppContext.BaseDirectory` 拼出来的写法不一定逐字相同
    /// （斜杠、大小写、`..`），直接字符串比较会误判成"不是自己"。
    /// </summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
                                 Path.GetFullPath(b).TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'),
                                 StringComparison.OrdinalIgnoreCase);
        }
    }
}
