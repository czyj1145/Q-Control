namespace QControl.App.Http;

/// <summary>
/// 定位 `tests/fixtures` 与 `model.json`。
///
/// 不写死绝对路径：从当前工作目录和程序目录分别向上找，最多 6 层。
/// 找不到就**明确失败**，绝不悄悄回空数据 —— 那会让"桩数据缺失"看起来像"EC 读不到"。
/// </summary>
internal static class Paths
{
    private const int MaxUp = 6;

    /// <summary>
    /// 安装版的配置目录名（`%ProgramData%\Q-Control`）。**必须与 QControl.Setup 里的常量一致**：
    /// 安装器往这里放 seed 的 model.json，这里按同一个名字找。
    /// </summary>
    public const string InstallDataFolder = "Q-Control";

    /// <summary>tests/fixtures 目录（含 12 个 .json 契约样本）。</summary>
    public static string? FindFixtures(string? explicitDir)
    {
        if (!string.IsNullOrEmpty(explicitDir))
            return Directory.Exists(explicitDir) ? Path.GetFullPath(explicitDir) : null;
        return FindUp("tests", "fixtures") is { } f ? Path.Combine(f, "tests", "fixtures") : null;
    }

    /// <summary>model.json（用户配置）。找不到就返回 null —— 上层据此退回纯桩模式。</summary>
    public static string? FindModel(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            for (var i = 0; i < MaxUp && dir is not null; i++, dir = dir.Parent)
            {
                var direct = Path.Combine(dir.FullName, "model.json");
                if (File.Exists(direct)) return direct;
                var nested = Path.Combine(dir.FullName, "ControlCenter", "model.json");
                if (File.Exists(nested)) return nested;
            }
        }

        // 安装版（QControl.Setup）：程序在 Program Files 里 —— 那不是用户可写的地方，
        // 配置和日志都不能落在那里（普通用户跑一次就会写失败，日志还会静默闭嘴）。
        // 所以安装器把 model.json 种到 %ProgramData%\Q-Control\ 下，这里作为**最后兜底**找它：
        // 放在最后才不会影响仓库开发（根目录就有 model.json）和便携版（exe 旁边就有一份）。
        if (InstallModel() is { } installed) return installed;

        return null;
    }

    /// <summary>`%ProgramData%\Q-Control\model.json`，没有就返回 null（不抛异常）。</summary>
    private static string? InstallModel()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(root)) return null;
            var path = Path.Combine(root, InstallDataFolder, "model.json");
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;   // 读不到环境变量之类：当作没有，别让找配置这件事把进程带崩
        }
    }

    /// <summary>从 cwd / 程序目录向上逐层找「某目录里存在某文件」。</summary>
    private static string? FindUp(string subDir, string marker)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            for (var i = 0; i < MaxUp && dir is not null; i++, dir = dir.Parent)
            {
                var probe = Path.Combine(dir.FullName, subDir, marker);
                if (File.Exists(probe) || Directory.Exists(probe))
                    return dir.FullName;
            }
        }
        return null;
    }
}
