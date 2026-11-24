using System.Diagnostics;

namespace QControl.Setup;

/// <summary>
/// `/selftest`：在临时沙箱里把整套安装流程跑一遍。
///
/// 为什么要它：安装/卸载是"一提权就动系统"的东西，出错往往是静默的（快捷方式少一个、
/// 卸载项少一个值、配置没种进去）。这份自检**不需要管理员**：所有路径都通过
/// `/root=` 重定向到临时目录，注册表只写 HKCU 的一个临时键，跑完自己清干净。
///
/// 唯一拒绝执行的情况：控制中心正在运行 —— 自检不会为了测试去关掉用户正在用的实例。
///
/// 退出码：0 全过 / 1 有失败（失败明细见日志 `%TEMP%\QControl.Setup.selftest.log`）。
/// </summary>
internal static class SelfTest
{
    public static int Run(SetupOptions parsed)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "qcontrol-selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var options = SetupOptions.Parse(["/root=" + sandbox, "/dir=" + Path.Combine(sandbox, "app")]);
        _ = parsed;   // 命令行参数在这里用不上（沙箱路径是自定的），留着只为签名一致

        var pass = 0;
        var fail = 0;

        void Check(string name, Func<string?> body)
        {
            try
            {
                var problem = body();
                if (problem is null)
                {
                    pass++;
                    Log.Info($"[  OK  ] {name}");
                }
                else
                {
                    fail++;
                    Log.Error($"[ FAIL ] {name} —— {problem}");
                }
            }
            catch (Exception ex)
            {
                fail++;
                Log.Error($"[ FAIL ] {name} —— {ex.GetType().Name}: {ex.Message}");
            }
        }

        Log.Info("沙箱：" + sandbox);
        Directory.CreateDirectory(sandbox);

        // ---------------------------------------------------------- 载荷
        Check("版本号不是占位值", () => SetupOptions.Version == "0.0.0" ? "还是 0.0.0，VERSION 没被读进来" : null);

        Check("载荷已嵌入", () => Payload.HasPayload ? null
            : "payload.zip 没嵌进来（先跑 src\\pack-release.cmd 生成 dist\\_payload）");

        Check("载荷规模合理", () =>
        {
            var mb = Payload.UncompressedSize() / 1048576d;
            // 框架依赖之后载荷只有 2 MB 上下。这里防的是"打包脚本把运行时又塞回来了"
            // 或者"载荷是空的"这两种量级错误，不是卡某个精确值。
            if (mb <= 0) return "载荷是空的";
            return mb > 40 ? $"解包后有 {mb:F0} MB —— 像是又把运行时打进去了（应为 2 MB 上下）" : null;
        });

        Check("默认配置可解析", () =>
        {
            var text = Payload.ReadModelBytes();
            if (text is null) return "model.json 没嵌进来";

            // net48 没有 System.Text.Json，System.Web.Extensions 又会把 System.Web 拖进
            // XAML 编译器的程序集解析（编译直接失败），所以这里用一个几十行的结构校验：
            // 括号配平 + 字符串闭合。真正要防的是"载荷截断/半截文件"，那正是它能抓到的；
            // 语义解析交给主程序（它读不动会另存 .broken，不会用错数据）。
            var broken = JsonStructureProblem(text);
            if (broken is not null) return "model.json 结构不完整：" + broken;

            foreach (var key in new[] { "presets", "factory_specs" })
                if (!text.Contains("\"" + key + "\"")) return "model.json 缺顶层键 " + key;
            return null;
        });

        // 运行时只报告不判定：开发机装了、用户的干净机器没装，两种都是"正常"。
        Log.Info(DotnetRuntime.IsInstalled
            ? $"[ INFO ] {DotnetRuntime.StatusLine()}（本机安装时会跳过下载）"
            : $"[ INFO ] {DotnetRuntime.StatusLine()}（本机装的话会走下载分支）");
        Log.Info("[ INFO ] 运行时的默认安装位置：" + DotnetRuntime.DefaultRoot);

        // ---------------------------------------------------------- 联网（可选）
        // 只有 `/selftest /net` 才跑：它会真的下 55 MB。
        // 为什么要单独验：发行包只有 1 MB，干净机器上"能不能把运行时拉下来"就是命门，
        // 而这段代码在开发机上永远走不到（开发机必然装着 .NET 10）。
        if (parsed.NetworkTests)
        {
            Check("运行时安装包可下载", () =>
            {
                var target = Path.Combine(sandbox, "windowsdesktop-runtime-probe.exe");
                DotnetRuntime.DownloadForTest(target, CancellationToken.None);

                if (!File.Exists(target)) return "下载后没有文件";
                var size = new FileInfo(target).Length;
                if (size < 20L * 1024 * 1024) return $"只有 {size / 1048576d:F1} MB，像是下到了一半或是错误页";

                // 头两个字节必须是 MZ：重定向到登录页/拦截页时长度也可能很大，得看内容
                using (var fs = File.OpenRead(target))
                {
                    if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') return "下下来的不是 PE 文件（被重定向到网页了？）";
                }

                Log.Info($"       运行时安装包 {size / 1048576d:F1} MB，PE 头正常");
                File.Delete(target);
                return null;
            });
        }

        // ---------------------------------------------------------- 快捷方式
        Check("快捷方式可创建", () =>
        {
            var lnk = Path.Combine(sandbox, "shortcut-probe", "probe.lnk");
            Shortcut.Create(lnk, Path.Combine(Environment.SystemDirectory, "notepad.exe"), "",
                            Environment.SystemDirectory, "自检", Environment.SystemDirectory + "\\notepad.exe");
            if (!File.Exists(lnk)) return ".lnk 没生成（IShellLink 互操作有问题）";
            var size = new FileInfo(lnk).Length;
            return size < 200 ? $".lnk 只有 {size} 字节，多半是空壳" : null;
        });

        // ---------------------------------------------------------- 注册表
        Check("注册表可写（HKCU 临时键）", () =>
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\QControl\SelfTest");
            if (key is null) return "建不了临时键";
            key.SetValue("probe", "1");
            var read = key.GetValue("probe") as string;
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\QControl", throwOnMissingSubKey: false);
            return read == "1" ? null : "写进去读不出来";
        });

        // ---------------------------------------------------------- 全流程
        // 沙箱里跑：不会去碰真实进程（Installer 里 RootOverride 非空时跳过 StopRunning），
        // 所以即使你此刻正开着控制中心，自检也照跑不误。
        {
            Check("安装全流程", () =>
            {
                // 先造两个"上一版留下的"文件再装：用户升级安装的真实样子。
                // 老布局把 240 多个 dll 平铺在安装目录根，只解包不清理的话，装完照样是
                // "新布局 + 旧 dll 一片" —— 这次的改动等于没生效。
                Directory.CreateDirectory(options.InstallDir);
                var staleRoot = Path.Combine(options.InstallDir, "System.Private.CoreLib.dll");
                var staleDir = Path.Combine(options.InstallDir, "old-layout");
                File.WriteAllText(staleRoot, "stale");
                Directory.CreateDirectory(staleDir);
                File.WriteAllText(Path.Combine(staleDir, "leftover.dll"), "stale");

                // 反面：README 教用户把自己那份 HWiNFO 放在 `<安装目录>\HWiNFO\`，
                // 清理残留时**不能**把它当旧文件删掉
                var userHwinfo = Path.Combine(options.InstallDir, "HWiNFO");
                Directory.CreateDirectory(userHwinfo);
                File.WriteAllText(Path.Combine(userHwinfo, "HWiNFO64.exe"), "user's own copy");

                var sw = Stopwatch.StartNew();
                Installer.Install(options, desktopShortcut: true, progress: null, CancellationToken.None);
                sw.Stop();
                Log.Info($"       安装耗时 {sw.Elapsed.TotalSeconds:F1}s");

                if (File.Exists(staleRoot))
                    return "早先实现平铺在根目录的 dll 没被清掉 —— 覆盖安装后会新旧文件混在一起";
                if (Directory.Exists(staleDir))
                    return "早先实现的废弃子目录没被清掉";
                if (!File.Exists(Path.Combine(userHwinfo, "HWiNFO64.exe")))
                    return "用户自己放的 HWiNFO\\ 被当成残留删掉了 —— 升级不能动用户数据";

                foreach (var exe in new[]
                         {
                             Path.Combine(options.InstallDir, SetupOptions.MainExe),
                             options.InstalledSetupExe,
                         })
                {
                    if (!File.Exists(exe)) return "安装目录里缺 " + Path.GetFileName(exe);
                    if (new FileInfo(exe).Length == 0) return Path.GetFileName(exe) + " 是 0 字节";
                }

                // 全程序只有一个 exe。升级安装后还留着旧 launcher ⇒ 残留清理没生效，
                // 或者载荷里又混进了它。
                if (File.Exists(Path.Combine(options.InstallDir, "QControl.Silent.exe")))
                    return "安装目录里还留着旧的 QControl.Silent.exe（自启会指到它）";

                // ⚠️ 这两条是 2026-10-28「瘦身」的守门员：框架依赖之后安装目录根只该有
                //    十来个文件（自己的 6 个 + System.Management + tests\ 与卸载程序副本）。
                //    如果哪天又冒出上百个运行时 dll，说明有人把 --self-contained 改回来了，
                //    安装体积会一夜回到 138 MB。
                var dlls = Directory.EnumerateFiles(options.InstallDir, "*.dll", SearchOption.TopDirectoryOnly).ToList();
                if (dlls.Count > 10)
                    return $"安装目录根有 {dlls.Count} 个 dll（框架依赖下应 ≤10）—— 是不是又改成自包含发布了？";
                if (!File.Exists(Path.Combine(options.InstallDir, "QControl.deps.json")))
                    return "缺 QControl.deps.json —— 框架依赖的程序靠它找共享框架";

                // 缺了它主程序直接 FATAL 退出（"双击没反应"的老事故）
                if (!File.Exists(Path.Combine(options.InstallDir, "tests", "fixtures", "status.json")))
                    return "缺 tests\\fixtures\\status.json —— 主程序会启动即退出";

                if (!File.Exists(Path.Combine(options.DataDir, "model.json")))
                    return "配置目录里没有种下 model.json（主程序会退成纯桩模式）";

                foreach (var lnk in new[]
                         {
                             Path.Combine(options.ProgramMenuDir, SetupOptions.ShortcutName + ".lnk"),
                             Path.Combine(options.ProgramMenuDir, SetupOptions.UninstallLinkName + ".lnk"),
                             Path.Combine(options.DesktopDir, SetupOptions.ShortcutName + ".lnk"),
                         })
                    if (!File.Exists(lnk)) return "快捷方式缺失：" + lnk;

                using var key = options.UninstallRootKey.OpenSubKey(options.UninstallKeyPath);
                if (key is null) return "卸载项没写进注册表";

                var name = key.GetValue("DisplayName") as string;
                if (name != SetupOptions.ProductName) return $"DisplayName 不对：{name}";

                var uninstall = key.GetValue("UninstallString") as string ?? "";
                if (!uninstall.Contains(SetupOptions.SetupExeName) || !uninstall.StartsWith("\"", StringComparison.Ordinal))
                    return "UninstallString 没指向安装目录里的卸载程序（或没加引号）：" + uninstall;

                var version = key.GetValue("DisplayVersion") as string;
                if (version != SetupOptions.Version) return $"DisplayVersion 与程序版本不一致：{version} vs {SetupOptions.Version}";

                return null;
            });

            // 2026-10-30 加（用户报障："卸载不会自动识别安装路径，导致无法卸载"）。
            // 装到**自定义目录**之后，不带 /dir= 的卸载必须自己把那个目录找回来 ——
            // 靠的是写进注册表的 InstallLocation，而不是默认的 Program Files\Q-Control。
            // 这条要是红了，用户点卸载就等于"删了个不存在的目录 + 丢掉卸载项"。
            Check("卸载能自动找回安装位置", () =>
            {
                var probed = SetupOptions.Parse(["/root=" + sandbox, "/uninstall"]);

                if (!string.Equals(probed.InstallDir, options.InstallDir, StringComparison.OrdinalIgnoreCase))
                    return $"探测到 {probed.InstallDir}（{probed.InstallDirSource}），实际装在 {options.InstallDir}";
                if (probed.DetectedDir is null) return "没识别到既有安装（DetectedDir 为空）";

                Log.Info($"       识别来源：{probed.DetectedSource}");
                return null;
            });

            Check("卸载全流程", () =>
            {
                Installer.Uninstall(options, purgeData: true, progress: null, CancellationToken.None);

                // 目录删除是交给临时批处理做的（进程自己删不掉正在跑的 exe），所以要等
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    var gone = !Directory.Exists(options.InstallDir)
                               && !Directory.Exists(options.DataDir)
                               && !Directory.Exists(options.ProgramMenuDir)
                               && !File.Exists(Path.Combine(options.DesktopDir, SetupOptions.ShortcutName + ".lnk"));
                    using var key = options.UninstallRootKey.OpenSubKey(options.UninstallKeyPath);
                    if (gone && key is null) return null;
                    Thread.Sleep(300);
                }

                return "卸载后仍有残留（目录 / 快捷方式 / 卸载项）";
            });
        }

        // ---------------------------------------------------------- 收尾
        try { Directory.Delete(sandbox, recursive: true); } catch { Log.Warn("沙箱没删干净：" + sandbox); }

        Log.Info("");
        Log.Info($"=== 自检结束：{pass} 通过 / {fail} 失败 ===");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 粗粒度 JSON 结构校验：括号配平 + 字符串闭合（认得转义）。
    /// 返回问题描述，没问题返回 null。
    /// </summary>
    private static string? JsonStructureProblem(string text)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        foreach (var c in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}':
                case ']':
                    if (stack.Count == 0) return "多了一个 " + c;
                    if (stack.Pop() != c) return "括号不配平（遇到 " + c + "）";
                    break;
            }
        }

        if (inString) return "字符串没有闭合（文件被截断？）";
        if (stack.Count > 0) return "还有 " + stack.Count + " 层括号没闭合（文件被截断？）";
        return null;
    }
}
