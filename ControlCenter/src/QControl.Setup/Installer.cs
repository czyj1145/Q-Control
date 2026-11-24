using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace QControl.Setup;

/// <summary>当前进程是不是管理员。安装/卸载都必须提权（清单里已声明 requireAdministrator）。</summary>
internal static class Admin
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 真正的安装/卸载动作。**界面只负责调这里、把进度画出来** —— 所有会动系统的
/// 事都在这一个文件里，方便读、也方便 `/silent` 和向导共用同一套实现。
///
/// 装到哪：
///   · 程序     → `%ProgramFiles%\Q-Control`（普通用户只读，防止改 exe 提权）
///   · 配置+日志 → `%ProgramData%\Q-Control`（授权 Users 可写 —— 见 <see cref="GrantDataWrite"/>）
///   · 快捷方式 → 所有用户的开始菜单 / 桌面
///   · 卸载项   → HKLM\...\Uninstall\QControl
/// </summary>
internal static class Installer
{
    // ================================================================ 安装

    public static void Install(SetupOptions o, bool desktopShortcut,
                               IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        if (!Payload.HasPayload)
            throw new SetupException("安装包里没有程序文件（payload.zip 缺失）。这是打包错误，请重新获取安装包。");

        var installDir = o.InstallDir;
        Log.Info($"安装开始：{installDir}（来源：{o.InstallDirSource}）");

        // ⚠️ 先确认这个目录能装（2026-11-23 审查发现）：`/dir=` 是用户/脚本给的，
        //    而下面的 CleanStale 会删掉"不在载荷清单里"的文件 —— 指到一个已有内容的目录
        //    （D:\Games、D:\Program Files）就等于把人家清空，还报"安装成功"。
        //    判据与卸载一致（LooksLikeInstallDir）；只有用户数据/载荷文件的目录仍允许。
        EnsureInstallableDir(installDir);

        // ---- 0. 运行环境 -----------------------------------------------------
        // 主程序是**框架依赖**发布（约 2 MB，见 QControl.App.csproj），机器上得有
        // .NET 10 桌面运行时。安装版**替用户装好**（2026-10-28 定；绿色版没有安装器，
        // 只能靠使用说明把用户带到官网自己装）。
        //
        // 放在最前面是刻意的：缺运行时/下载失败时，用户那台正在跑的控制中心一点都没被打扰
        // —— 而不是"先把你正在用的程序关掉，再说下载失败"。
        // 沙箱自检（/root=）跳过：自检不该去装系统级运行时，也不该联网。
        if (o.RootOverride is null)
        {
            ct.ThrowIfCancellationRequested();
            DotnetRuntime.EnsureInstalled(progress, 1, 34, ct);
        }
        else
        {
            Log.Info("沙箱自检：跳过运行时检查（不联网、不装系统组件）");
        }

        // ---- 1. 先让正在跑的实例自己退出 -------------------------------------
        progress?.Report(new SetupProgress(35, "正在停止已运行的控制中心…"));
        // 沙箱（/selftest）里不碰真实进程：自检是为了验证安装流程，不该顺手关掉用户正在用的实例
        if (o.RootOverride is null && !StopRunning(TimeSpan.FromSeconds(10)))
            throw new SetupException("控制中心正在运行，暂时无法覆盖安装。\n\n请先在托盘图标上右键退出，然后重试。");

        // ---- 2. 解包 ---------------------------------------------------------
        ct.ThrowIfCancellationRequested();
        progress?.Report(new SetupProgress(37, "正在准备安装目录…"));
        Directory.CreateDirectory(installDir);
        Payload.ExtractTo(installDir, progress, 38, 84, ct);

        if (!File.Exists(Path.Combine(installDir, SetupOptions.MainExe)))
            throw new SetupException("解包后没找到 " + SetupOptions.MainExe + "，安装包可能不完整。");

        // ---- 2b. 清掉上一版留下的文件 ------------------------------------------
        // ⚠️ 旧版本（自包含时代的布局）把 240 多个运行时 dll 平铺在安装目录根、其余收进
        //    bin\。只解包不清理的话，升级安装会变成"新布局 + 一堆上一版的运行时 dll"，
        //    装完占的盘比装之前还大 —— 等于这次瘦身白费。
        progress?.Report(new SetupProgress(86, "正在清理旧版本文件…"));
        CleanStale(installDir);

        // ---- 3. 配置目录 + 种子配置 ------------------------------------------
        ct.ThrowIfCancellationRequested();
        progress?.Report(new SetupProgress(88, "正在初始化配置目录…"));
        SeedConfig(o);

        // ---- 3b. HWiNFO 落脚点 -----------------------------------------------
        // 装完必须**看得见**这个文件夹：PCH 温度 / 电压 / 显存结温 / PL1·PL2 / 硬盘 SMART
        // 这五项只有 HWiNFO 拿得到，而它不能随包分发（第三方软件，EULA 禁止 bundling）。
        // 于是唯一的引导就是"把你自己那份 HWiNFO64.exe 放到 install\HWiNFO\ 里"——
        // 目录不存在的话，这句话等于让用户自己建目录，等于没有引导。
        EnsureHwinfoDir(installDir);

        // ---- 4. 卸载程序副本（卸载入口指向它，原下载文件删了也能卸） -------------
        progress?.Report(new SetupProgress(92, "正在写入卸载程序…"));
        CopySelf(installDir);
        // ---- 5. 快捷方式 -----------------------------------------------------
        progress?.Report(new SetupProgress(95, "正在创建快捷方式…"));
        CreateShortcuts(o, desktopShortcut);

        // ---- 6. 卸载项 -------------------------------------------------------
        progress?.Report(new SetupProgress(98, "正在登记卸载信息…"));
        WriteUninstallEntry(o);

        progress?.Report(new SetupProgress(100, "安装完成"));
        Log.Info("安装完成");
    }

    /// <summary>
    /// 把 `model.json` 种到配置目录（**只在不存在时**：升级安装绝不能覆盖用户配置）。
    ///
    /// 为什么要种：主程序找不到 model.json 会退回"纯桩模式"（界面全是旧快照），
    /// 那不是"能用"的状态；而它自己并不会凭空生成这个文件。
    ///
    /// 为什么载荷里**故意不含** model.json：主程序找配置的顺序是"exe 目录向上 → ProgramData"。
    /// 要是安装目录里也躺一份，就会先命中 Program Files 里那份 —— 那里普通用户写不进去，
    /// 保存设置会静默失败。所以安装版只在 ProgramData 有一份（便携版才放在 exe 旁边）。
    /// </summary>
    private static void SeedConfig(SetupOptions o)
    {
        var dataDir = o.DataDir;
        Directory.CreateDirectory(dataDir);
        GrantDataWrite(dataDir);

        var modelPath = Path.Combine(dataDir, "model.json");
        if (File.Exists(modelPath))
        {
            Log.Info("配置已存在，保留：" + modelPath);
            return;
        }

        var seed = Payload.ReadModelBytes()
            ?? throw new SetupException("安装包里没有默认配置（model.json）。这是打包错误。");
        File.WriteAllText(modelPath, seed);
        Log.Info("已写入默认配置：" + modelPath);
    }

    /// <summary>
    /// 建好 `&lt;安装目录&gt;\HWiNFO\` 并放一张说明。
    ///
    /// 为什么**只建目录、不放程序**：HWiNFO 是第三方软件，EULA 不允许随包分发（README 里
    /// 也是这么写的）。但"没带程序"不等于"不给位置" —— 用户装完打开安装目录，看到
    /// `HWiNFO\` 和里面那张字条，才知道那五项缺的数据该往哪儿补。
    ///
    /// 为什么只写说明、不覆盖：这个目录是**用户的**。升级安装时里面可能已经躺着他自己的
    /// HWiNFO64.exe 和 HWiNFO64.INI，一个字都不能动（<see cref="IsUserData"/> 也放过它）。
    /// </summary>
    private static void EnsureHwinfoDir(string installDir)
    {
        try
        {
            var dir = Path.Combine(installDir, "HWiNFO");
            Directory.CreateDirectory(dir);

            var note = Path.Combine(dir, "把 HWiNFO64.exe 放在这里.txt");
            if (File.Exists(note)) return;

            // 带 BOM：记事本、PowerShell 5.1、cmd type 都认，不依赖系统代码页
            //
            // ⚠️ 引导的落点就是**这个文件夹本身**（2026-10-30 用户定）：安装器已经把目录建好、
            //    把说明放好，用户只需要把 exe 拖进来。不要再指向 `%ProgramData%` 之类的
            //    "共享位置" —— 那要用户自己建文件夹、还得知道路径怎么写，多一个会失配的地方。
            File.WriteAllText(note,
                "Q-Control 不随包分发 HWiNFO（第三方软件，许可不允许）。\r\n" +
                "\r\n" +
                "下面这几项只有 HWiNFO 拿得到，想看到它们，把你自己的 HWiNFO 放进来：\r\n" +
                "  · PCH 温度      · CPU / GPU 核心电压   · 显存结温\r\n" +
                "  · CPU PL1 / PL2 · 硬盘 SMART\r\n" +
                "\r\n" +
                "做法：把 HWiNFO64.exe（便携版即可）放到当前这个文件夹里，然后重启 Q-Control。\r\n" +
                "不用改任何设置 —— 程序启动时会先在这个文件夹里找。\r\n" +
                "\r\n" +
                "这个文件夹空的、或者放的不是 HWiNFO，都不影响其它功能，只是上面那几行整行隐藏\r\n" +
                "（不会显示假数据）。\r\n" +
                "\r\n" +
                "只有想放到别处时才需要设：环境变量 CC_HWI_DIR = 你那份 HWiNFO 的目录\r\n" +
                "（它是最后一级，程序目录里这份存在时不会被用到）。\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Log.Info("已建 HWiNFO 目录与说明：" + dir);
        }
        catch (Exception ex)
        {
            // 装不上这个目录不该让整个安装失败：程序本身照跑，只是少一份引导
            Log.Warn("建 HWiNFO 目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 给配置目录授 `Users:(OI)(CI)M`。
    ///
    /// 为什么必须授：配置和日志要能被**普通用户进程**写。不授的话，从 Planner 直接双击
    /// 起来的只读实例一改设置就失败，日志也会静默闭嘴（FileLog 写失败一次就再也不写）。
    ///
    /// 为什么只授 ProgramData 这一处、不授 Program Files：那里是可执行文件。让普通用户
    /// 能改 exe，再加上自启任务跑的是 HighestAvailable 令牌 —— 那就是一条标准的本地提权路径。
    ///
    /// 用 SID（`*S-1-5-32-545`）而不是 "Users"：中文系统上组名是"Users"没错，但换了
    /// 语言/本地化策略就可能对不上，SID 永远对。
    /// </summary>
    private static void GrantDataWrite(string dataDir)
    {
        var rc = RunHidden("icacls.exe", $"\"{dataDir}\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q", 30000);
        if (rc != 0) Log.Warn($"icacls 返回 {rc}：配置目录可能无法被普通用户写入（{dataDir}）");
        else Log.Info("配置目录已授权 Users 修改：" + dataDir);
    }

    /// <summary>
    /// 清掉安装目录里**不属于本版载荷**的残留文件。
    ///
    /// 只按"载荷清单"判断，不做任何启发式删除：清单里的路径留着（刚解包的新文件），
    /// 清单外的才是上一版留下的（例如老布局平铺在根目录的那 240 个 dll）。
    ///
    /// ⚠️ 删不掉是**预期内**的情况：从安装目录里的卸载程序重装时，安装目录里的
    ///    `Uninstall.exe` 正被自己占用。这类文件收集起来交给退出后的临时批处理。
    /// </summary>
    private static void CleanStale(string installDir)
    {
        var keep = Payload.EntryRelativePaths();
        if (keep.Count == 0)
        {
            Log.Warn("读不到载荷清单，跳过残留清理（升级安装可能留下旧文件）");
            return;
        }

        var locked = new List<string>();
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories))
        {
            var rel = Compat.GetRelativePath(installDir, file).Replace('\\', '/');
            if (keep.Contains(rel)) continue;
            if (IsUserData(rel)) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                locked.Add(file);                       // 正被占用（多半是安装器自己）
            }
            catch (Exception ex)
            {
                Log.Warn($"清理残留失败（{rel}）：{ex.Message}");
            }
        }

        // 空了的老目录也一并收掉（例如新版不再生成的 zh-Hant\ 里要是只剩空壳）
        // ⚠️ 用户目录（logs\ / HWiNFO\ / tools\）例外：空着也留着，别删用户放东西的地方
        foreach (var dir in Directory.EnumerateDirectories(installDir, "*", SearchOption.AllDirectories)
                                     .OrderByDescending(d => d.Length))
        {
            var relDir = Compat.GetRelativePath(installDir, dir).Replace('\\', '/');
            if (IsUserDataDir(relDir)) continue;

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { /* 非空/被占用都正常，留着不影响启动 */ }
        }

        Log.Info($"清理旧版本残留：删除 {removed} 个文件，占用中 {locked.Count} 个");
        if (locked.Count > 0) ScheduleDeleteFiles(locked);
    }

    /// <summary>把安装器自己复制到安装目录，卸载项指向这份副本。</summary>
    private static void CopySelf(string installDir)
    {
        // net48 没有 Environment.ProcessPath，只能问主模块要路径
        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(self) || !File.Exists(self))
        {
            Log.Warn("拿不到安装器自身路径，跳过卸载程序副本");
            return;
        }

        var target = Path.Combine(installDir, SetupOptions.SetupExeName);
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return;                                  // 就是从安装目录里重装的：不用自己拷自己

        File.Copy(self, target, overwrite: true);
        Log.Info("卸载程序已就位：" + target);
    }

    private static void CreateShortcuts(SetupOptions o, bool desktop)
    {
        // 唯一的入口就是主程序：它自己会先试 HighestAvailable 任务（零 UAC），
        // 没有任务才 runas 自己一次 —— 旧版本由静默启动器代劳的那套逻辑。
        var target = Path.Combine(o.InstallDir, SetupOptions.MainExe);
        var icon = target;

        Shortcut.Create(
            Path.Combine(o.ProgramMenuDir, SetupOptions.ShortcutName + ".lnk"),
            target, SetupOptions.ShortcutArgs, o.InstallDir,
            SetupOptions.ProductName, icon);

        // 「卸载」跟启动项放同一个开始菜单文件夹。
        // 为什么必须有：注册表卸载项只在「设置 → 应用」里能看到，而绝大多数人找卸载
        // 入口是翻开始菜单 —— 缺了这一项，用户的第一反应就是"这软件没卸载程序"。
        // 指向安装目录里的副本（不是下载的那个安装包），所以下载文件删了也能卸。
        // 安装目录里**再放一份**（同上理由，翻文件夹的人也该一眼看到 —— 2026-10-29 加）：
        // 那个副本现在叫 Uninstall.exe（原来叫 QControl.Setup.exe，双击会打开安装向导，
        // 名字和行为都是反的）。名字对了，双击它本身就是卸载，快捷方式里再带个 /uninstall 只是显式表达。
        if (File.Exists(o.InstalledSetupExe))
        {
            var uninstallLinkName = SetupOptions.UninstallLinkName + ".lnk";

            Shortcut.Create(
                Path.Combine(o.ProgramMenuDir, uninstallLinkName),
                o.InstalledSetupExe, "/uninstall", o.InstallDir,
                "卸载 " + SetupOptions.ProductName, o.InstalledSetupExe);

            Shortcut.Create(
                Path.Combine(o.InstallDir, uninstallLinkName),
                o.InstalledSetupExe, "/uninstall", o.InstallDir,
                "卸载 " + SetupOptions.ProductName, o.InstalledSetupExe);
        }
        else
        {
            Log.Warn("没有卸载程序副本，跳过「卸载」快捷方式：" + o.InstalledSetupExe);
        }

        if (desktop)
        {
            Shortcut.Create(
                Path.Combine(o.DesktopDir, SetupOptions.ShortcutName + ".lnk"),
                target, SetupOptions.ShortcutArgs, o.InstallDir,
                SetupOptions.ProductName, icon);
        }
    }

    private static void WriteUninstallEntry(SetupOptions o)
    {
        // 正常情况：指向安装目录里的副本（下载的安装包删了也能卸）。
        // 万一副本没拷成（被占用 / 权限），**退回**当前正在跑的这份安装包 —— 宁可指向
        // 一个会被用户删掉的路径，也不能在「应用和功能」里留一条点了没反应的死链。
        var uninstallExe = File.Exists(o.InstalledSetupExe)
            ? o.InstalledSetupExe
            : Process.GetCurrentProcess().MainModule?.FileName ?? o.InstalledSetupExe;

        if (!string.Equals(uninstallExe, o.InstalledSetupExe, StringComparison.OrdinalIgnoreCase))
            Log.Warn("卸载项退回指向当前安装包（安装目录里没有副本）：" + uninstallExe);

        // 加引号 —— Program Files 有空格，不加就必然失败。
        var uninstall = $"\"{uninstallExe}\" /uninstall";

        using var key = o.UninstallRootKey.CreateSubKey(o.UninstallKeyPath, writable: true)
            ?? throw new SetupException("写卸载项注册表失败（权限不足？）");
        key.SetValue("DisplayName", SetupOptions.ProductName, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("DisplayVersion", SetupOptions.Version, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("Publisher", SetupOptions.Publisher, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("InstallLocation", o.InstallDir, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("DisplayIcon", Path.Combine(o.InstallDir, SetupOptions.MainExe) + ",0", Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("UninstallString", uninstall, Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("QuietUninstallString", uninstall + " /silent", Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("Comments", "广达模具 EC 控制中心：风扇曲线 / 性能模式 / 灯效 / 显卡超频", Microsoft.Win32.RegistryValueKind.String);
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(o.InstallDir), Microsoft.Win32.RegistryValueKind.DWord);
        Log.Info("卸载项已写入：" + o.UninstallKeyPath);
    }

    /// <summary>目录占用（KB）：给界面显示"当前占用"、给卸载项的 EstimatedSize 用。</summary>
    public static int EstimateSizeKb(string dir)
    {
        try
        {
            var bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                                 .Sum(f => new FileInfo(f).Length);
            return (int)Math.Min(int.MaxValue, bytes / 1024);
        }
        catch
        {
            return 0;
        }
    }

    // ================================================================ 卸载

    public static void Uninstall(SetupOptions o, bool purgeData,
                                 IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var installDir = o.InstallDir;
        Log.Info($"卸载开始：{installDir}（来源：{o.InstallDirSource}，删除配置={purgeData}）");

        progress?.Report(new SetupProgress(4, "正在停止控制中心…"));
        // 停不掉也继续：卸载不该被一个卡住的进程永远卡死。沙箱里同样跳过（见 Install 里那条注释）
        if (o.RootOverride is null) StopRunning(TimeSpan.FromSeconds(10));

        progress?.Report(new SetupProgress(20, "正在删除自启任务…"));
        // 沙箱自检里跳过：那个任务从来没被建过（建任务是主程序的事），删它只会去调
        // schtasks.exe —— 而受控环境（企业策略、CI 沙箱）经常直接禁止启动它，
        // 于是"删一个不存在的任务"反倒把整个卸载流程卡在这里。
        if (o.RootOverride is null) DeleteTask();

        progress?.Report(new SetupProgress(35, "正在删除快捷方式…"));
        DeleteShortcuts(o);

        progress?.Report(new SetupProgress(50, "正在移除卸载信息…"));
        try
        {
            o.UninstallRootKey.DeleteSubKeyTree(o.UninstallKeyPath, throwOnMissingSubKey: false);
            Log.Info("卸载项已移除");
        }
        catch (Exception ex)
        {
            Log.Warn("移除卸载项失败：" + ex.Message);
        }

        if (purgeData)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new SetupProgress(65, "正在删除配置与日志…"));
            TryDeleteDirectory(o.DataDir);
        }

        progress?.Report(new SetupProgress(80, "正在删除程序文件…"));
        // 安装程序自己就在 installDir 里跑着，删不掉正在执行的 exe ⇒ 交给一个临时批处理，
        // 等本进程退出后再删（这是 Windows 上唯一可靠的做法）。
        //
        // ⚠️ 递归删除前必须确认这真是我们的目录：卸载路径现在可以是自动探测来的、
        //    也可以是用户手填的（2026-10-30 起卸载页可改路径）。一旦指错，
        //    `rmdir /s /q` **不进回收站**，删掉的东西拿不回来 —— 而且这里删的往往是
        //    "上一个目录"，用户连发现的机会都没有。判据见 SetupOptions.LooksLikeInstallDir。
        if (SetupOptions.LooksLikeInstallDir(installDir))
        {
            ScheduleDeleteDirectory(installDir);
        }
        else if (Directory.Exists(installDir))
        {
            Log.Warn($"安装目录里没有 Q-Control 程序，跳过递归删除（{installDir}）");
            TryDeleteEmptyDirectory(installDir);
        }
        else
        {
            Log.Info("安装目录已不存在，无需删除：" + installDir);
        }

        progress?.Report(new SetupProgress(100, "卸载完成"));
        Log.Info("卸载完成（目录将在几秒后删除）");
    }

    /// <summary>
    /// 只删**空**目录。用在"用户指的目录里没找到 Q-Control 程序"这条路上：
    /// 空壳可以顺手收掉，里面有东西就一定不是我们该删的（可能是用户自己的文件，
    /// 也可能是他填错了盘符）。
    /// </summary>
    private static void TryDeleteEmptyDirectory(string dir)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                Log.Info("已删除空目录：" + dir);
            }
            else
            {
                Log.Warn("目录非空且不像安装目录，保留原样：" + dir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除空目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 安装目录里**属于用户、不属于程序**的文件：清理旧版本时必须放过。
    ///
    /// 为什么要这份白名单：安装版把配置放在 `%ProgramData%`，但便携版（以及从便携版转过来用的人）
    /// 会在 exe 旁边留一份 `model.json` 和 `logs\`；`HWiNFO\` 更是 README 明确教用户放的
    /// （自己那份 HWiNFO 便携版，用来补 PCH 温度等五项数据）。
    /// 清理是为了收掉上一版的**程序文件** —— 顺手删掉用户改过的配置或第三方工具，
    /// 就变成"装个新版把设置弄丢了"。
    /// </summary>
    /// <summary>
    /// 安装目录能不能动：`/dir=` 指到一个"不是本程序"的非空目录时直接拒绝。
    ///
    /// 后面的 <see cref="CleanStale"/> 会把"不在载荷清单里、又不是用户数据"的文件删掉
    /// （升级时要清上一版残留的 dll），这个动作建立在一个用户输入目录上就必须先验身份 ——
    /// 卸载那边早有 <see cref="SetupOptions.LooksLikeInstallDir"/> 兜底，安装这里原先没有。
    /// </summary>
    private static void EnsureInstallableDir(string dir)
    {
        if (!Directory.Exists(dir)) return;              // 新目录：后面 CreateDirectory

        // 既有安装：里面"多出来的文件"就是上一版的残留，CleanStale 本来就该清
        if (SetupOptions.LooksLikeInstallDir(dir)) return;

        var keep = Payload.EntryRelativePaths();
        if (keep.Count == 0) return;                     // 读不到清单时 CleanStale 也会跳过

        var strangers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Compat.GetRelativePath(dir, file).Replace('\\', '/');
            if (keep.Contains(rel) || IsUserData(rel)) continue;
            strangers.Add(rel);
            if (strangers.Count >= 5) break;
        }
        if (strangers.Count == 0) return;                // 空目录 / 只有用户数据或载荷文件

        var sb = new StringBuilder();
        sb.AppendLine("这个目录里已经有别的东西，看起来不是 Q-Control 的安装目录：");
        sb.AppendLine();
        foreach (var s in strangers) sb.AppendLine("  · " + s);
        if (strangers.Count >= 5) sb.AppendLine("  · …");
        sb.AppendLine();
        sb.AppendLine("装到这里会把它们删掉。请换一个空目录（例如 D:\\Program Files\\Q-Control），");
        sb.Append("或先把这些文件移走再装。");
        throw new SetupException(sb.ToString());
    }

    private static bool IsUserData(string rel)
        => rel.Equals("model.json", StringComparison.OrdinalIgnoreCase)
           || rel.Equals("model.json.broken", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)
           // 历史采样档（2026-10-31）：便携版落在 app\history\，升级时不能当"旧版本残留"删掉
           || rel.StartsWith("history/", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("HWiNFO/", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("tools/HWiNFO/", StringComparison.OrdinalIgnoreCase);

    /// <summary>目录版白名单：这几个顶层目录（及它们下面）不因为"空了"被删掉。</summary>
    private static bool IsUserDataDir(string rel)
        => rel.Equals("logs", StringComparison.OrdinalIgnoreCase)
           || rel.Equals("history", StringComparison.OrdinalIgnoreCase)
           || rel.Equals("HWiNFO", StringComparison.OrdinalIgnoreCase)
           || rel.Equals("tools", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("history/", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("HWiNFO/", StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith("tools/", StringComparison.OrdinalIgnoreCase);

    private static void DeleteTask()
    {
        var rc = RunHidden("schtasks.exe", $"/delete /tn \"\\{SetupOptions.TaskName}\" /f", 20000);
        Log.Info($"schtasks /delete 返回 {rc}（0=删掉了，非 0 通常是本来就没有）");
    }

    private static void DeleteShortcuts(SetupOptions o)
    {
        foreach (var lnk in new[]
                 {
                     Path.Combine(o.ProgramMenuDir, SetupOptions.ShortcutName + ".lnk"),
                     Path.Combine(o.ProgramMenuDir, SetupOptions.UninstallLinkName + ".lnk"),
                     Path.Combine(o.DesktopDir, SetupOptions.ShortcutName + ".lnk"),
                     Path.Combine(o.InstallDir, SetupOptions.UninstallLinkName + ".lnk"),   // 安装目录里那份
                 })
        {
            try
            {
                if (File.Exists(lnk)) { File.Delete(lnk); Log.Info("已删除：" + lnk); }
            }
            catch (Exception ex)
            {
                Log.Warn($"删除快捷方式失败（{lnk}）：{ex.Message}");
            }
        }

        // 开始菜单那个文件夹只装我们两个快捷方式：清空后要一起收掉，否则留下一个空壳文件夹
        try
        {
            if (Directory.Exists(o.ProgramMenuDir) && !Directory.EnumerateFileSystemEntries(o.ProgramMenuDir).Any())
            {
                Directory.Delete(o.ProgramMenuDir);
                Log.Info("已删除开始菜单文件夹：" + o.ProgramMenuDir);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除开始菜单文件夹失败（{o.ProgramMenuDir}）：{ex.Message}");
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); Log.Info("已删除目录：" + dir); }
        }
        catch (Exception ex)
        {
            Log.Warn($"删除目录失败（{dir}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 等本进程退出后再删安装目录。**整目录删，不留任何东西。**
    ///
    /// 分工（2026-10-30 用户定，别搞混）：
    ///   · **卸载** = 用户不要了 ⇒ 全删，包括 `HWiNFO\` 里他自己放的 HWiNFO64.exe；
    ///   · **覆盖安装 / 升级** = 换程序、数据照旧 ⇒ 靠 <see cref="CleanStale"/> 的
    ///     `IsUserData` 白名单保住 `HWiNFO\`、`logs\`、`model.json`（见 <see cref="IsUserData"/>）。
    ///   两个方向相反，不要为了"保护用户"把卸载也改成保留 —— 那会让卸载永远卸不干净。
    ///
    /// 这件事有**三个**坑，任何一个踩中都等于"卸载完了文件夹还在"：
    ///
    ///   1. **不能靠 `timeout /t 2` 做延迟**。这个 cmd 是用 `CreateNoWindow` 起的，
    ///      timeout 拿不到控制台输入会立刻报 "Input redirection is not supported" 返回，
    ///      延迟等于没做 —— 于是 rmdir 在卸载器自己还占着 `Uninstall.exe` 的那一刻就跑，
    ///      删不掉。
    ///   2. **不能只删一次**。用户在「卸载完成」页上停留多久不由我们决定；哪怕延迟做对了，
    ///      他先看两秒再点「完成」也照样占用。一失败就没人再管 ⇒ 目录永久残留，而卸载项
    ///      已经从「应用和功能」里删掉了 ⇒ 用户从此**再也卸不掉**（只剩手动删）。
    ///      所以改成轮询：每 1 秒试一次，直到目录真的消失（上限约 60 秒）。
    ///   3. **工作目录不能落在待删目录里**（见 <see cref="StartDetachedScript"/>）。
    /// </summary>
    private static void ScheduleDeleteDirectory(string dir)
    {
        StartDetachedScript("qcontrol-uninstall-",
            "@echo off\r\n" +
            "for /l %%i in (1,1,60) do (\r\n" +
            $"  if not exist \"{dir}\" goto qc_done\r\n" +
            $"  rmdir /s /q \"{dir}\" 2>nul\r\n" +
            $"  if not exist \"{dir}\" goto qc_done\r\n" +
            "  ping -n 2 127.0.0.1 >nul\r\n" +          // 等 1 秒再试（ping 比 timeout 可靠）
            ")\r\n" +
            ":qc_done\r\n" +
            "del /f /q \"%~f0\"\r\n");
        Log.Info("已安排删除安装目录（整目录，每秒重试直到消失）：" + dir);
    }

    /// <summary>
    /// 本进程退出后再删这批文件（现在删不掉 = 被自己占用）。升级安装时用来收掉上一版
    /// 平铺在安装目录根的那些 dll，以及旧位置上的安装器副本。
    ///
    /// 同样是"重试到删掉为止"：`timeout` 在无窗口 cmd 里不可用（见 ScheduleDeleteDirectory），
    /// 只删一轮的话，还握着句柄的那几个 dll 会一直留在安装目录里，与新版混在一起。
    /// </summary>
    private static void ScheduleDeleteFiles(IReadOnlyCollection<string> files)
    {
        var body = new System.Text.StringBuilder();
        body.Append("for /l %%i in (1,1,15) do (\r\n");
        foreach (var f in files) body.Append($"  del /f /q \"{f}\" 2>nul\r\n");
        body.Append("  ping -n 2 127.0.0.1 >nul\r\n");
        body.Append(")\r\n");
        body.Append("del /f /q \"%~f0\"\r\n");

        StartDetachedScript("qcontrol-cleanup-", "@echo off\r\n" + body);
        Log.Info($"已安排退出后删除 {files.Count} 个占用中的旧文件（每轮重试，最多约 15 秒）");
    }

    /// <summary>写一个临时 .cmd 并在隐藏窗口里放它自己跑（脚本末尾会把自己也删掉）。</summary>
    private static void StartDetachedScript(string prefix, string content)
    {
        try
        {
            var cmd = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(cmd, content, Encoding.Default);   // cmd.exe 认的是 OEM 代码页

            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // ⚠️ 工作目录必须挪出安装目录。卸载器是从安装目录里双击起来的，不指定的话
                //    cmd 继承它的 CWD —— 而"当前目录落在某个目录里"会让该目录**无法删除**
                //    （rmdir 报"另一个程序正在使用此文件"，哪怕里面一个文件都不剩）。
                //    临时目录里起脚本，删安装目录时就没有这层隐形占用。
                WorkingDirectory = Path.GetTempPath(),
                Arguments = $"/c \"{cmd}\"",
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("安排后台清理失败：" + ex.Message);
        }
    }

    // ================================================================ 停止实例

    /// <summary>
    /// 让正在运行的控制中心**自己**退出。
    ///
    /// ⚠️ 绝不用 `taskkill /F`：那会跳过 on_quit，风扇就留在手动占空比上一直转
    ///    （EC 拿到非零 UFA 会一直照那个值转，只有重启/睡眠才能回去）。
    ///    所以这里只发 `/api/app/quit`，让程序自己把风扇交回去。
    /// </summary>
    public static bool StopRunning(TimeSpan wait)
    {
        if (!AnyRunning()) return true;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var _ = http.PostAsync($"http://127.0.0.1:{SetupOptions.Port}/api/app/quit", null)
                              .GetAwaiter().GetResult();
            Log.Info("已请求控制中心退出（/api/app/quit）");
        }
        catch (Exception ex)
        {
            Log.Warn("请求退出失败（可能端口被改过）：" + ex.Message);
        }

        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (!AnyRunning()) return true;
            Thread.Sleep(250);
        }
        return !AnyRunning();
    }

    /// <summary>
    /// 有没有在跑的控制中心。`Process.GetProcessesByName` 返回的每只都持有句柄，
    /// 这里要一并释放 —— 轮询循环 10 秒能调 40 次，不释放就是一堆等终结器回收的句柄
    /// （2026-11-23 审查）。只取个数，不碰进程本身。
    /// </summary>
    private static bool AnyRunning()
    {
        var procs = Process.GetProcessesByName("QControl");
        try
        {
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    // ================================================================ 杂项

    /// <summary>跑一个不弹窗的命令，返回退出码（拿不到进程时返回 -1）。</summary>
    public static int RunHidden(string exe, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;

            // ⚠️ 必须先**异步**把两个管道排空，再等退出。
            //    旧写法是先 `ReadToEnd()` 再 `WaitForExit(timeout)`：
            //      · `ReadToEnd` 在子进程不退出时永不返回 ⇒ 后面的超时形同虚设；
            //      · stdout 读完了但 stderr 写满 4 KB 管道时会互等 ⇒ 死锁。
            //    真实触发场景：`icacls /T /C` 在大量拒绝项上狂写 stderr、`schtasks` 被组策略挂住。
            //    症状是安装向导停在「正在初始化配置目录…」永远不动，而此时目录已经被覆盖 —— 半装状态。
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { /* 已经退了 */ }
                Log.Warn($"{exe} 超时（{timeoutMs} ms），已终止");
                return -1;
            }

            var stdout = so.GetAwaiter().GetResult();
            var stderr = se.GetAwaiter().GetResult();
            if (stdout.Trim().Length > 0) Log.Info($"{exe}: {stdout.Trim()}");
            if (stderr.Trim().Length > 0) Log.Warn($"{exe}: {stderr.Trim()}");
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"{exe} 执行失败：{ex.Message}");
            return -1;
        }
    }
}
