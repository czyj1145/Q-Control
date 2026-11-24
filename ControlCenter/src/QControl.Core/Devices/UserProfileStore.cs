using System.Text.Json;

namespace QControl.Core.Devices;

/// <summary>
/// 用户机型档案目录（`devices\*.json`，即一键采集写出来的那些）的**枚举与清理**。
///
/// 为什么单独一个类而不是塞进 <see cref="DeviceResolver"/>：这是纯文件操作，
/// 与"挑哪份档案"无关；放在 Core 里是为了能在没有真机、没有管理员的测试里跑。
///
/// ⚠️ 与 <c>FileLog.ClearLogs</c> 同一条纪律：**只删自己写的那一类文件**
///    （目录顶层的 `*.json`），绝不递归、绝不删子目录、绝不碰别的扩展名。
///    删除不可逆，宁可少删。
/// ⚠️ 删完**不会**热生效：当前 Device 是 AppState 构造时解析出来的，
///    已经一路进了 EcHal / 风扇引擎，要重启才回落到内置档案。
/// </summary>
public static class UserProfileStore
{
    /// <summary>目录扫描结果（<see cref="Names"/> 已排序，供确认框逐个列出来）。</summary>
    public readonly record struct Stats(int Files, long Bytes, IReadOnlyList<string> Names)
    {
        public static Stats Empty => new(0, 0, Array.Empty<string>());
    }

    /// <summary>
    /// 统计目录顶层的 `*.json`。目录不存在 / 读不动一律当"没有"——
    /// 统计失败不该让接口失败，界面显示 0 即可。
    /// </summary>
    public static Stats Scan(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return Stats.Empty;
        try
        {
            if (!Directory.Exists(dir)) return Stats.Empty;

            var names = new List<string>();
            var bytes = 0L;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (!IsProfileFile(f)) continue;
                long len;
                try { len = new FileInfo(f).Length; } catch { len = 0; }
                names.Add(Path.GetFileName(f));
                bytes += len;
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return new Stats(names.Count, bytes, names);
        }
        catch
        {
            return Stats.Empty;
        }
    }

    /// <summary>
    /// 删掉目录顶层的 `*.json`。返回（删掉几个、释放多少字节、几个没删掉）。
    ///
    /// 没删掉通常是被别的程序独占（编辑器/杀毒），照实回 <c>Failed</c>，
    /// 不假装删了。
    /// </summary>
    public static (int Deleted, long Freed, int Failed) Delete(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return (0, 0, 0);

        var deleted = 0;
        var freed = 0L;
        var failed = 0;
        try
        {
            if (!Directory.Exists(dir)) return (0, 0, 0);

            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (!IsProfileFile(f)) continue;

                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                try
                {
                    File.Delete(f);
                    deleted++;
                    freed += len;
                }
                catch
                {
                    failed++;
                }
            }
        }
        catch { /* 能删多少删多少 */ }
        return (deleted, freed, failed);
    }

    /// <summary>
    /// 这个 `devices\` 目录像不像**源码仓库里**的那份（旁边的目录有 `QControl.slnx` / `.git`）。
    ///
    /// 为什么要有这条：调试实例的 `model.json` 常常就在工作区里，用户档案目录于是
    /// 跟着指到 `ControlCenter\devices\` —— 那里躺着 `quanta-nl5.json` 这种**手改真源**。
    /// 界面上的"清理日志"绝不该把仓库里的档案删了（2026-11-07 采集覆盖源码档案
    /// 那次已经踩过一次，别再给第二条路）。
    /// 安装版（`%ProgramData%\Q-Control\devices`）旁边没有这些东西，照常可清。
    /// </summary>
    public static bool LooksLikeSourceTree(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(parent)) return false;

            return File.Exists(Path.Combine(parent, "QControl.slnx"))
                   || File.Exists(Path.Combine(parent, "QControl.sln"))
                   || Directory.Exists(Path.Combine(parent, ".git"));
        }
        catch
        {
            // 判不出来就按"不安全"处理：清理档案是可选项，少给一次机会不会坏事
            return true;
        }
    }

    /// <summary>
    /// 「一键采集」能不能把本机档案写进 <paramref name="targetPath"/>。
    ///
    /// 为什么要有：写入路径和清理路径原本不对等 —— <c>ClearLogs(profiles:true)</c> 会
    /// 拒绝动源码仓库里的 `devices\`，但采集是 <c>File.WriteAllText</c> 直接覆盖同名文件。
    /// 调试实例的 model.json 常在工作区，档案目录于是跟着指到
    /// `ControlCenter\devices\quanta-nl5.json` —— 那份是**手改真源**，被草案盖掉就得靠
    /// git 找回（2026-11-07 踩过一次）。同理，任何目录里一份标着 `verified=true` 的
    /// 正式档案也不该被 `verified=false` 的采集草案降级。
    /// ⚠️ 只挡「明显是手改真源」这两种；普通用户档案（`verified=false`）照旧可覆盖，
    ///    反复采集不该攒出一堆同名文件。读不懂的 JSON 当作可覆盖，别让一份坏文件
    ///    把采集整个卡住。
    /// </summary>
    public static (bool Allowed, string? Reason) CanWriteProfile(string? dir, string targetPath)
    {
        if (LooksLikeSourceTree(dir))
            return (false,
                "这次运行用的是源码目录的配置，档案目录指着仓库里的 devices\\ —— "
                + "那里是手改真源，覆盖就没处找回。请用安装版采集，或把采集包交给作者用 --import-pack 导入。");

        try
        {
            if (File.Exists(targetPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(targetPath));
                if (doc.RootElement.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True)
                    return (false, "这个位置已经有一份标为「已验证」的机型档案（手改真源），不覆盖它。");
            }
        }
        catch
        {
            // 读不出 / 不是合法 JSON ⇒ 不猜它是真源，按普通用户档案处理
        }
        return (true, null);
    }

    /// <summary>我们自己写的那类文件：顶层 `*.json`。</summary>
    private static bool IsProfileFile(string path)
        => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
}
