using System.Text;

namespace QControl.Core.Startup;

/// <summary>
/// 自我提权的**决策与命令行拼装**：这一层只回答两件事 ——「这次启动要不要提权」和
/// 「提权时怎么把参数原样带过去」。真正的 `ShellExecuteEx("runas")` 在
/// `Hal/Sys/Elevation.cs`。
///
/// 为什么拆成两层：Core 是 `net10.0`（编译期就不许出现 Windows API），而提权决策
/// 其实是**纯字符串判断**。放在这里才能用单测把"哪些开关不该弹 UAC"钉死 ——
/// 否则每次改动都只能靠人肉双击 exe、点一遍 UAC「否」来验证。
///
/// 背景（ROADMAP R4）：C# 版早先没有任何自我提权，非管理员下写操作一并 403，
/// 体感就是"点了没反应"。这里给出的是**进程内重启**方案，不是
/// `requireAdministrator` 清单 —— 清单会让 `--help` / `--selftest` / `--ui-shot`
/// 每次也弹 UAC，而截图回归根本没人去点那个按钮。
/// </summary>
public static class ElevationPlan
{
    /// <summary>提权后的新进程会带上这个标记（防重复提权/循环）。</summary>
    public const string ElevatedFlag = "--elevated";

    /// <summary>明确要求「只读运行，别弹 UAC」。</summary>
    public const string NoElevateFlag = "--no-elevate";

    /// <summary>
    /// 提权后**接管端口**：请正在跑的实例退出，等它让出端口再绑。
    /// 只由「界面里点『以管理员身份重启』」这条路径设置 —— 普通启动**不带**它，
    /// 因为"已有实例在跑"时的正确反应是打开那个窗口，而不是把人家踢掉。
    /// </summary>
    public const string TakeoverFlag = "--takeover";

    /// <summary>
    /// **不参与**自我提权的模式。三类：
    ///   · 只读诊断：`--help` / `--selftest` / `--ui-shot` / `--ui-actions-selftest`
    ///     / `--ui-fit-selftest` / `--ui-reopen-selftest` / `--ui-refresh-selftest`
    ///     / `--autostart-selftest`
    ///     —— 它们本来就不写硬件（自启自检会自己降级成 LeastPrivilege 并说明没验到），
    ///     为看一份结果弹 UAC 纯属骚扰（截图回归更是没人去点）；
    ///   · 纯文件转换：`--import-pack`（采集包 → devices\*.json，只读写 json，不碰 EC）；
    ///   · 明确表态：`--show`（只把旧实例的窗口叫出来）、`--no-elevate`、`--elevated`。
    ///
    /// ⚠️ `--gpu-oc-selftest` **不在这里**：它要写 NVML，不提权跑等于验了个假的。
    /// </summary>
    private static readonly string[] ReadOnlyModes =
    [
        "--help", "-h",
        "--show",
        "--selftest", "--ui-shot", "--ui-actions-selftest", "--autostart-selftest",
        "--ui-reopen-selftest", "--ui-fit-selftest", "--ui-refresh-selftest",
        "--import-pack",
        ElevatedFlag, NoElevateFlag,
    ];

    /// <summary>这次启动要不要请求管理员权限。</summary>
    public static bool WantsElevation(IReadOnlyList<string> argv)
    {
        foreach (var arg in argv)
            foreach (var mode in ReadOnlyModes)
                if (string.Equals(arg, mode, StringComparison.OrdinalIgnoreCase))
                    return false;
        return true;
    }

    /// <summary>
    /// 提权时要带过去的参数：原样照抄 + 尾巴上加一个 <see cref="ElevatedFlag"/>。
    /// 已有的同名标记先剔掉（否则反复重启会积一串）。
    /// </summary>
    public static string[] ElevatedArguments(IReadOnlyList<string> argv)
    {
        var list = argv
            .Where(a => !string.Equals(a, ElevatedFlag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        list.Add(ElevatedFlag);
        return [.. list];
    }

    /// <summary>拼成一条命令行（逐项 quote）。</summary>
    public static string Join(IReadOnlyList<string> argv)
        => string.Join(' ', argv.Select(Quote));

    /// <summary>
    /// 界面里点「以管理员身份重启」时用的参数。
    ///
    /// 与 <see cref="ElevatedArguments"/> 的两点不同，都是因为"用户此刻正看着窗口"：
    ///   · 去掉 `--tray` / `--no-open` —— 否则重启后的新进程缩进托盘，
    ///     用户看到的是"窗口关了、什么都没回来"；
    ///   · 加上 <see cref="TakeoverFlag"/> —— 由新进程请旧进程退出并接管端口
    ///     （旧的这时候还在跑，端口在它手里）。
    ///
    /// ⚠️ 还必须去掉 `--no-elevate`：只读实例自己是带着它起的，照抄过去
    ///    新进程会被 <see cref="WantsElevation"/> 判成"别提权" —— 点一次按钮还是只读，
    ///    用户只会觉得这个按钮是坏的。（这一条是写测试时才发现漏掉的。）
    /// </summary>
    /// <param name="page">
    /// 重启后要停在的页（null = 不指定，走默认页）。用户是在某一页上点的按钮，
    /// 换完进程跳回总览会让人以为"我刚才那页丢了"。
    /// </param>
    public static string[] TakeoverArguments(IReadOnlyList<string> argv, string? page = null)
    {
        var list = new List<string>(argv.Count + 2);
        for (var i = 0; i < argv.Count; i++)
        {
            var arg = argv[i];
            // --page 要连它的值一起丢（后面按"当前页"重新给），否则旧值会赢：
            // Program 取参数用 Array.IndexOf，只认第一个
            if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (IsOneOf(arg, "--tray", "--no-open", NoElevateFlag, ElevatedFlag, TakeoverFlag))
                continue;
            list.Add(arg);
        }

        if (page is not null)
        {
            list.Add("--page");
            list.Add(page);
        }
        list.Add(ElevatedFlag);
        list.Add(TakeoverFlag);
        return [.. list];
    }

    private static bool IsOneOf(string arg, params string[] modes)
    {
        foreach (var mode in modes)
            if (string.Equals(arg, mode, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// 按 Windows 的 `CommandLineToArgvW` 规则给单个参数加引号。
    ///
    /// 规则只有三条，但漏一条就会在带空格的路径上炸（本项目的路径全都带空格，
    /// 形如 `C:\Users\<你>\My Projects\<项目>\`）：
    ///   1. 没有空格/制表符/引号 → 原样返回（`--tray` 不该变成 `"--tray"`）；
    ///   2. 含这些字符 → 整段用双引号包起来；
    ///   3. 引号前和**结尾**的反斜杠要翻倍 —— 否则它们会把收尾引号转义掉。
    /// </summary>
    public static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";

        var needsQuote = false;
        foreach (var c in arg)
            if (c is ' ' or '\t' or '\n' or '\v' or '"') { needsQuote = true; break; }
        if (!needsQuote) return arg;

        var sb = new StringBuilder(arg.Length + 8);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                sb.Append('\\');
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // 引号前的反斜杠全部翻倍，再补一个给引号本身
                sb.Append('\\', backslashes + 1);
                backslashes = 0;
                sb.Append('"');
                continue;
            }

            sb.Append(c);
            backslashes = 0;
        }
        // 结尾那串反斜杠同样要翻倍，不然会把下面这个收尾引号吃掉
        sb.Append('\\', backslashes);
        sb.Append('"');
        return sb.ToString();
    }
}
