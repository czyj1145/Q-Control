using System;
using System.IO;

namespace QControl.Boot;

/// <summary>
/// 绿色版的目录约定：引导器在根上，主程序整套在 `app\` 里。
///
///     Q-Control\
///       Q-Control.exe        ← 本引导器（zip 里由 QControl.Boot.exe 改名而来）
///       使用说明.txt
///       app\
///         QControl.exe       ← 全程序唯一入口
///         model.json
///         tests\fixtures\…
///
/// 为什么把主程序收进 `app\`：根上只留一个 exe，用户不可能点错。
///
/// 为什么还认平铺布局：旧版本解压出来的目录、以及有人手工把 zip 里的 app\ 摊平，
/// 都能照常跑起来。找不到主程序才算失败。
/// </summary>
internal sealed class AppLayout
{
    private const string MainExeName = "QControl.exe";

    private AppLayout(string rootDir, string appDir)
    {
        RootDir = rootDir;
        AppDir = appDir;

        var main = Path.Combine(appDir, MainExeName);
        MainExe = File.Exists(main) ? main : null;
    }

    /// <summary>引导器所在目录（绿色版根）。</summary>
    public string RootDir { get; }

    /// <summary>主程序目录（正常是 `根\app`，平铺时就是根本身）。</summary>
    public string AppDir { get; }

    /// <summary>`app\QControl.exe` —— 主程序，也是唯一入口（WinExe，无黑框）。</summary>
    public string? MainExe { get; }

    public bool Found => MainExe is not null;

    public static AppLayout Detect()
    {
        var root = AppDomain.CurrentDomain.BaseDirectory;
        if (string.IsNullOrEmpty(root))
            root = Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]) ?? ".";
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var nested = Path.Combine(root, "app");
        if (Directory.Exists(nested) && File.Exists(Path.Combine(nested, MainExeName)))
            return new AppLayout(root, nested);

        return new AppLayout(root, root);
    }
}
