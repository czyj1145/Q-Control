using System.Reflection;
using Microsoft.Win32;

namespace QControl.Setup;

/// <summary>安装器有哪几种跑法（都从命令行 / 参数进来）。</summary>
internal enum SetupMode
{
    /// <summary>默认：带向导安装。</summary>
    Install,

    /// <summary>`/uninstall`：带向导卸载。</summary>
    Uninstall,

    /// <summary>`/extract=&lt;dir&gt;`：只把载荷解出来，不碰系统（便携版/排障用）。</summary>
    Extract,

    /// <summary>`/selftest`：在临时目录里把不碰系统的逻辑跑一遍（开发用）。</summary>
    SelfTest,

    /// <summary>`/ui-shot=&lt;前缀&gt;`：把向导渲成 PNG 后退出（开发用，看版式）。</summary>
    Shot,
}

/// <summary>
/// 安装器用到的全部路径/常量与命令行解析。
///
/// 所有"装到哪、配置放哪、卸载项在哪"的答案都必须从这里取 —— 自检（/selftest）
/// 就是靠把 <see cref="Root"/> 指到临时目录，把整套流程在不动系统的情况下跑一遍的。
/// </summary>
internal sealed class SetupOptions
{
    // ---------------------------------------------------------------- 常量

    public const string ProductName = "Q-Control 控制中心";
    public const string Publisher = "The QControl Authors";
    public const string ProductId = "QControl";

    /// <summary>主程序 —— 也是**唯一**的入口（快捷方式、自启任务、绿色版引导都指它）。</summary>
    public const string MainExe = "QControl.exe";

    /// <summary>
    /// 卸载时要复制的自己：卸载入口就指向安装目录里的这份。
    ///
    /// 名字就是承诺：它叫 `Uninstall.exe`，双击就是**卸载**（判定见 <see cref="Parse"/>）。
    /// 2026-10-29 之前它叫 `QControl.Setup.exe` —— 光看名字没人猜得到它能卸载，
    /// 更糟的是双击它会打开**安装向导**（无参数 = 安装），点"卸载程序"反而装了第二遍。
    ///
    /// ⚠️ 用 ASCII 名而不是「卸载 Q-Control.exe」：卸载完成后有个临时批处理要删安装目录，
    ///    脚本内容是 `System.Text.Encoding.Default`（OEM 代码页）写出来的，文件名里带中文
    ///    会在非中文系统上写花、导致残留目录删不掉。中文留给旁边的 `卸载 Q-Control.lnk`。
    /// </summary>
    public const string SetupExeName = "Uninstall.exe";

    /// <summary>开始菜单里的卸载快捷方式名（和启动项同放一个文件夹）。</summary>
    public const string UninstallLinkName = "卸载 Q-Control";

    /// <summary>卸载项注册表路径（HKLM）。</summary>
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductId;

    /// <summary>
    /// 本次运行的卸载项路径。
    /// 自检（`/root=`）时加 `.SelfTest` 后缀：万一自检中途崩了，也不会在用户真实的
    /// 「应用和功能」里留下一条指向临时目录的假条目。
    /// </summary>
    public string UninstallKeyPath => RootOverride is null ? UninstallKey : UninstallKey + ".SelfTest";

    /// <summary>自启任务名，与 `QControl.Hal/Sys/Autostart.cs` 一致；卸载要清掉。</summary>
    public const string TaskName = "QControl";

    /// <summary>
    /// 配置目录名（`%ProgramData%\Q-Control`）。
    /// ⚠️ 必须与 `QControl.App/Http/Paths.cs` 的 `InstallDataFolder` 一致。
    /// </summary>
    public const string DataFolderName = "Q-Control";

    /// <summary>后端默认端口；停止实例时往这里发 quit。</summary>
    public const int Port = 8765;

    /// <summary>开始菜单里的文件夹与快捷方式名。</summary>
    public const string ShortcutName = ProductName;

    /// <summary>
    /// 快捷方式的参数：**空**。
    ///
    /// 旧版本这里是 `--elevate`（指向静默启动器，让它走任务代启 / runas 提权）。
    /// 2026-10-30 合并成一个 exe 之后，主程序自己就会先试 HighestAvailable 任务
    /// （零 UAC）、再退回 runas —— 正好是 `--elevate` 那句要求的全部内容，
    /// 所以快捷方式不再需要带任何参数。
    /// </summary>
    public const string ShortcutArgs = "";

    // ---------------------------------------------------------------- 解析结果

    public SetupMode Mode { get; private init; } = SetupMode.Install;
    public bool Silent { get; private init; }
    public string? DirOverride { get; private init; }
    public string? ExtractDir { get; private init; }

    /// <summary>
    /// `/selftest /net`：自检里额外跑一次"下载 .NET 运行时安装包"（55 MB，要联网）。
    /// 默认不跑 —— 自检的第一要求是快、且不依赖网络。
    /// </summary>
    public bool NetworkTests { get; private init; }

    /// <summary>`/ui-shot=` 的路径前缀（不含 `-install/-uninstall.png` 后缀）。</summary>
    public string? ShotPrefix { get; private init; }

    /// <summary>
    /// 探测到的**既有安装目录**：用户上次装在哪儿。没装过就是 null。
    ///
    /// 为什么非有不可（2026-10-30 用户报障）：安装目录在向导里是可以改的，于是
    /// "装到哪"不再等于 `%ProgramFiles%\Q-Control`。卸载和升级安装都不能再靠默认
    /// 路径去猜 —— 猜错的后果有两种，都很糟：
    ///   · 卸载：去删一个空目录，真正的安装目录原封不动，却把卸载项从「应用和功能」
    ///     里抹掉了 ⇒ 从此**再也卸不掉**（用户报的就是这个）。
    ///   · 升级：装出第二套，旧的那套仍在开机自启、仍在抢 8765 端口。
    /// 判定顺序见 <see cref="Detect"/>。
    /// </summary>
    public string? DetectedDir { get; private init; }

    /// <summary>上面那个目录是怎么找到的。既显示给用户看，也是排障的第一手信息。</summary>
    public string? DetectedSource { get; private init; }

    // ---------------------------------------------------------------- 路径

    /// <summary>
    /// 安装根目录。优先级：命令行 `/dir=` → 自动探测到的既有安装 → 默认位置。
    ///
    /// `/dir=` 永远最高：用户/脚本明说要装到或卸掉某个目录，就不该被注册表覆盖。
    /// </summary>
    // ⚠️ 一定要吃掉首尾引号：`/dir="D:\Program Files\Q-Control"` 这种写法在命令行里很常见，
    //    用户在卸载页手填时也常常粘贴带引号的路径。引号留在字符串里会让 Path.GetFullPath
    //    直接抛 ArgumentException（非法字符）—— 表现是"点卸载，弹一个'安装程序出错'"。
    public string InstallDir
        => AlignCase(Path.GetFullPath((DirOverride ?? DetectedDir ?? DefaultInstallDir).Trim().Trim('"', ' ')));

    /// <summary>当前生效的安装目录是从哪儿来的（写日志、显示在向导上）。</summary>
    public string InstallDirSource
        => DirOverride is not null ? "命令行 /dir="
         : DetectedDir is not null ? DetectedSource ?? "自动识别"
         : "默认位置";

    /// <summary>配置与日志目录（普通用户也可写，安装时给 Users 授 Modify）。</summary>
    public string DataDir => Path.Combine(RootOverride ?? CommonAppData, DataFolderName);

    /// <summary>安装目录里的卸载程序（和 QControl.exe 并排）。</summary>
    public string InstalledSetupExe => Path.Combine(InstallDir, SetupExeName);

    /// <summary>`/selftest` 专用：把 ProgramData / ProgramFiles 都换到临时沙箱。</summary>
    public string? RootOverride { get; private init; }

    /// <summary>`/selftest` 专用：注册表根（自检写 HKCU，绝不碰 HKLM）。</summary>
    public bool UseCurrentUserRegistry { get; private init; }

    public static string DefaultInstallDir => Path.Combine(ProgramFiles, "Q-Control");

    public static string ProgramFiles
        => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static string CommonAppData
        => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>所有用户的开始菜单「程序」目录。</summary>
    public static string ProgramMenu
        => Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

    /// <summary>所有用户的桌面。</summary>
    public static string PublicDesktop
        => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>
    /// 开始菜单里的**程序文件夹**（快捷方式落点）：`…\开始菜单\程序\Q-Control 控制中心\`。
    ///
    /// 为什么不直接丢在「程序」根下：那里要放两个东西（启动 + 卸载），散着放会跟别的软件
    /// 混在一起，而且「卸载」这一项本来就该在软件的文件夹里 —— 用户也是在那儿找它。
    /// `/selftest` 时换到沙箱里（自检绝不许往真实的开始菜单里塞东西）。
    /// </summary>
    public string ProgramMenuDir
        => RootOverride is null
            ? Path.Combine(ProgramMenu, ProductName)
            : Path.Combine(RootOverride, "SandboxStartMenu", ProductName);

    /// <summary>桌面目录（同上，自检时进沙箱）。</summary>
    public string DesktopDir
        => RootOverride is null ? PublicDesktop : Path.Combine(RootOverride, "SandboxDesktop");

    /// <summary>版本号：来自 `ControlCenter/VERSION`（见 src/Directory.Build.props）。</summary>
    public static string Version
        => typeof(SetupOptions).Assembly
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               .Split('+')[0] ?? "0.0.0";

    // ---------------------------------------------------------------- 命令行

    /// <summary>
    /// 解析参数。宽容形式：`/x`、`--x`、`-x` 都认，`=` 与空格两种赋值都认。
    ///
    ///   /uninstall            带向导卸载
    ///   /silent               静默（配合 /uninstall 做静默卸载；默认即静默安装）
    ///   /dir=&lt;路径&gt;          换安装目录
    ///   /extract=&lt;路径&gt;      只解包，不装
    ///   /selftest             自检
    ///
    /// 另有一条隐式规则：进程名叫 `Uninstall.exe` 且没给模式参数 ⇒ 按**卸载**跑
    /// （安装目录里那份副本就是这个名字，双击即卸载）。
    /// </summary>
    public static SetupOptions Parse(string[] argv)
    {
        SetupMode mode = SetupMode.Install;
        var silent = false;
        string? dir = null, extract = null, root = null, shot = null;
        var useHkcu = false;
        var net = false;

        for (var i = 0; i < argv.Length; i++)
        {
            var raw = argv[i];

            // ⚠️ 只小写**开关名**，绝不能小写整条参数：值里是路径，`/dir=D:\Program Files\Q-Control`
            //    一旦整条 ToLower 就变成 `d:\program files\q-control`，然后被原样写进注册表
            //    `InstallLocation` ⇒ 下次覆盖安装、卸载页、日志里全是小写路径（2026-10-30 用户报障
            //    "覆盖安装识别到的路径是小写 d"）。能跑，但看着像坏了。
            var body = raw.TrimStart('/', '-');
            var inline = "";
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                // net48 没有 System.Index/Range，用 Substring 而不是 body[(eq+1)..]
                inline = body.Substring(eq + 1);   // 值保持原样（大小写就是用户/注册表给的）
                body = body.Substring(0, eq);
            }
            var key = body.ToLowerInvariant();

            string Value() => inline.Length > 0 ? inline
                : i + 1 < argv.Length ? argv[++i] : "";

            switch (key)
            {
                case "uninstall": mode = SetupMode.Uninstall; break;
                case "silent" or "s": silent = true; break;
                case "dir" or "d": dir = Value(); break;
                case "extract": mode = SetupMode.Extract; extract = Value(); break;
                case "selftest": mode = SetupMode.SelfTest; useHkcu = true; break;
                case "net": net = true; break;                        // 自检时额外跑联网检查
                case "ui-shot": mode = SetupMode.Shot; shot = Value(); break;
                case "root": root = Value(); useHkcu = true; break;   // 自检沙箱
                case "" or "?" or "help" or "h": break;               // 忽略空参数与占位
            }
        }

        // ============================================================ 文件名即模式
        // 安装目录里那份副本叫 Uninstall.exe，用户双击它就该卸载 —— 而不是因为
        // "没有 /uninstall 参数"又打开安装向导。这里只兜默认值：显式给了 /extract、
        // /selftest、/ui-shot 的进程，mode 早就不是 Install 了。
        if (mode == SetupMode.Install && LooksLikeUninstaller()) mode = SetupMode.Uninstall;

        // ============================================================ 上次装在哪
        // 没给 /dir= 时自动找回来：卸载必须删对目录，升级必须装回原处（用户报的
        // "无法卸载 / 不能一键升级"两条都出在这里）。`/dir=` 显式给了就不探测。
        // net48 的 string.IsNullOrWhiteSpace 没有 NotNullWhen 标注，显式判空（否则一堆 CS8602）
        var explicitDir = dir is null || dir.Trim().Length == 0 ? null : dir.Trim();
        var (detectedDir, detectedSource) = explicitDir is null
            ? Detect(useHkcu ? Registry.CurrentUser : Registry.LocalMachine,
                     useHkcu ? UninstallKey + ".SelfTest" : UninstallKey)
            : (null, null);

        return new SetupOptions
        {
            Mode = mode,
            Silent = silent,
            DirOverride = explicitDir,
            DetectedDir = detectedDir,
            DetectedSource = detectedSource,
            ExtractDir = string.IsNullOrWhiteSpace(extract) ? null : extract,
            ShotPrefix = string.IsNullOrWhiteSpace(shot) ? null : shot,
            RootOverride = string.IsNullOrWhiteSpace(root) ? null : root,
            UseCurrentUserRegistry = useHkcu,
            NetworkTests = net,
        };
    }

    // ================================================================ 安装位置探测

    /// <summary>
    /// 找出上次装到哪了。**顺序即优先级**：
    ///
    ///   1. **本进程自己所在的目录** —— 卸载程序就躺在安装目录里
    ///      （`&lt;安装目录&gt;\Uninstall.exe`），它脚下无疑就是安装目录。这条比任何注册表
    ///      都准，也不怕注册表被"清理软件"顺手删掉；用户从开始菜单/「应用和功能」
    ///      点卸载走的正是这条路。
    ///   2. 注册表 `InstallLocation`（安装时写下的）。
    ///   3. 注册表 `UninstallString` 里那个 exe 的目录（老条目 / 被别的工具改写时的兜底）。
    ///
    /// 只接受**目录确实存在且像我们的安装目录**的结果：用户手工删过目录的话，拿它去装
    /// 或去删都只会失败。全都不中就返回 null，由调用方回落到默认位置。
    /// </summary>
    private static (string? Dir, string? Source) Detect(RegistryKey root, string keyPath)
    {
        // 1. 自身所在目录
        if (SelfDirectory() is { } selfDir && LooksLikeInstallDir(selfDir))
            return (Path.GetFullPath(selfDir), "本程序所在目录");

        string? location = null, command = null;
        try
        {
            using var key = root.OpenSubKey(keyPath);
            location = key?.GetValue("InstallLocation") as string;
            command = key?.GetValue("UninstallString") as string;
        }
        catch (Exception ex)
        {
            // 读不到注册表不该让安装/卸载失败：后面还有默认位置兜底
            Log.Warn("读卸载项失败（继续用默认位置）：" + ex.Message);
        }

        // 2. InstallLocation（⚠️ 要验证目录还像我们的：用户可能把程序目录改名或删了）
        if (NormalizeExisting(location) is { } fromLocation && LooksLikeInstallDir(fromLocation))
            return (fromLocation, "注册表 InstallLocation");

        // 3. UninstallString 里的 exe 目录
        if (NormalizeExisting(ExecutableDirOf(command)) is { } fromCommand && LooksLikeInstallDir(fromCommand))
            return (fromCommand, "注册表 UninstallString");

        return (null, null);
    }

    /// <summary>当前进程 exe 所在目录（net48 没有 Environment.ProcessPath，只能问主模块）。</summary>
    private static string? SelfDirectory()
    {
        try
        {
            var self = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrEmpty(self) ? null : Path.GetDirectoryName(self);
        }
        catch
        {
            return null;   // 拿不到就当探测不到，绝不因此拒跑
        }
    }

    /// <summary>
    /// 这个目录**像不像**我们的安装目录：里面有没有 Q-Control 自己的文件。
    ///
    /// 判据要满足两个方向，都很要紧：
    ///   · 够宽 —— 主程序被手工删过、只剩残渣的现场也得认得出来（那正是最需要卸载来收拾的），
    ///     所以「只剩一个 Uninstall.exe」也算。
    ///   · 够严 —— 必须挡掉"安装包躺在下载文件夹里"这种误判：卸载的目录删除是
    ///     `rmdir /s /q`，**不进回收站**，认错目录就是不可逆的损失。
    ///
    /// ⚠️ 因此**不能**用 `QControl*` 通配：安装包自己的名字就是 `QControl.Setup.exe`，
    ///    通配会把任何一个放着安装包的文件夹（下载目录！）当成安装目录。只认安装目录里
    ///    才会出现的三个具体文件。
    /// </summary>
    public static bool LooksLikeInstallDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;

        try
        {
            return File.Exists(Path.Combine(dir, MainExe))        // 主程序（唯一入口）
                || File.Exists(Path.Combine(dir, "QControl.dll")) // 框架依赖布局下的主程序集
                || File.Exists(Path.Combine(dir, SetupExeName));  // 卸载器副本（程序被删剩它也算）
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把路径的大小写对齐到**磁盘上真实的**那一个。
    ///
    /// 为什么需要（2026-10-30 用户报障"覆盖安装识别到的路径是小写 d"）：注册表
    /// `InstallLocation` 里存的是上一版安装器写下的值，而上一版把整条命令行参数都
    /// ToLower 了（`/dir=D:\Program Files\Q-Control` → `d:\program files\q-control`）。
    /// 于是向导里、卸载页里、日志里那个路径全是小写 —— 功能上没事（Windows 不分大小写），
    /// 但看着像装坏了，而且会一直被下一次覆盖安装原样继承下去。
    ///
    /// 磁盘才是真源：**逐层**拿系统返回的拼写（`EnumerateFileSystemEntries` + 不分大小写
    /// 比较，返回的是目录项里真正存的名字），盘符统一大写。还没建的层（全新安装）原样保留
    /// 用户给的写法。UNC 路径直接放过 —— 枚举远程共享可能一卡十几秒，为改个大小写不值。
    ///
    /// 任何一步出错都退回原路径：**绝不**因为这个让安装/卸载失败。
    /// </summary>
    public static string AlignCase(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            if (root.Length == 0 || root.StartsWith(@"\\", StringComparison.Ordinal)) return full;

            var cur = root.ToUpperInvariant();   // 盘符永远大写：`d:\` → `D:\`
            var rest = full.Substring(root.Length)
                           .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                                  StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in rest)
            {
                var next = Path.Combine(cur, part);
                if (!Directory.Exists(next)) { cur = next; continue; }   // 不存在：保留原写法
                cur = RealEntryName(cur, part) ?? next;
            }
            return cur;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>`parent` 下与 `name` 同（不分大小写）的那一项的完整路径，即磁盘上的真实拼写。</summary>
    private static string? RealEntryName(string parent, string name)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                    return entry;
        }
        catch (Exception ex)
        {
            Log.Warn($"读目录失败（路径大小写按原样保留）：{parent} —— {ex.Message}");
        }
        return null;
    }

    /// <summary>规整成绝对路径，且要求目录真的存在；否则 null。</summary>
    private static string? NormalizeExisting(string? dir)
    {
        if (dir is null || dir.Trim().Length == 0) return null;
        try
        {
            var full = Path.GetFullPath(dir.Trim().Trim('"', ' '));
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从 `"C:\…\Uninstall.exe" /uninstall` 里取出 `C:\…`（带不带引号都认）。</summary>
    private static string? ExecutableDirOf(string? commandLine)
    {
        if (commandLine is null || commandLine.Trim().Length == 0) return null;
        var text = commandLine.Trim();

        string exe;
        if (text.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = text.IndexOf('"', 1);
            exe = end > 1 ? text.Substring(1, end - 1) : text.Trim('"');
        }
        else
        {
            var space = text.IndexOf(' ');   // 无引号时只能按空格切（路径带空格就没辙，但这本就是兜底）
            exe = space > 0 ? text.Substring(0, space) : text;
        }

        try
        {
            return string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前进程的 exe 名（不含扩展名）是不是 `Uninstall`。</summary>
    private static bool LooksLikeUninstaller()
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
            return string.Equals(name, "uninstall", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;   // 拿不到命令行就当普通安装包，绝不因此拒跑
        }
    }

    /// <summary>卸载项注册表根（HKLM，或自检时的 HKCU）。</summary>
    public Microsoft.Win32.RegistryKey UninstallRootKey
        => UseCurrentUserRegistry ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;

    /// <summary>换一个安装目录（属性是 init-only，界面用它做浅拷贝）。</summary>
    public SetupOptions WithInstallDir(string dir) => new()
    {
        Mode = Mode,
        Silent = Silent,
        DirOverride = dir,
        DetectedDir = DetectedDir,          // 留着：卸载页要显示"自动识别自…"，日志也用它
        DetectedSource = DetectedSource,
        ExtractDir = ExtractDir,
        ShotPrefix = ShotPrefix,
        RootOverride = RootOverride,
        UseCurrentUserRegistry = UseCurrentUserRegistry,
        NetworkTests = NetworkTests,
    };
}
