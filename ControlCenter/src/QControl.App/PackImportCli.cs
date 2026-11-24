using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using QControl.Core.Devices;

namespace QControl.App;

/// <summary>
/// `--import-pack &lt;file&gt;`：把用户发来的采集包变成一份能并进 `devices\` 的正式档案。
///
/// 这一步是"用户采集 → 我们适配"里**我们这半边**的入口。设计上的三个取舍：
///   · **不猜**：有阻塞项（match 自检跑不通 / id 会顶掉已验证档案 / 没有草案）就一个字节都不写，
///     只把原因打出来。发出去认不出机器的档案，比没有档案更糟；
///   · **不越权**：默认 `verified=false`，写权限只能靠显式 `--allow-write`（作者复核原厂行为之后）；
///   · **不覆盖**：目标文件已存在时默认拒绝，`--force` 才覆盖 —— 唯一真源 `devices\*.json`
///     是手改的，一次手滑盖掉 nl5 没有任何补救途径。
///
/// 只读、不碰 EC、不提权（在 <c>ElevationPlan.ReadOnlyModes</c> 里），
/// 所以日常实例正在跑的时候也能安全地跑这一条。
/// </summary>
internal static class PackImportCli
{
    /// <summary>成功写出一份档案。</summary>
    private const int ExitOk = 0;

    /// <summary>包本身有问题（解析失败 / 有阻塞项）：没写任何文件。</summary>
    private const int ExitBadPack = 2;

    /// <summary>目标文件已存在且没给 --force，或写盘失败。</summary>
    private const int ExitWriteFailed = 3;

    private const int Width = 74;

    public static int Run(string packPath, string? outDir, bool force, bool allowWrite, bool dryRun)
    {
        var rule = new string('-', Width);

        Console.WriteLine("Q-Control 机型采集包 → 机型档案");
        Console.WriteLine(rule);
        Console.WriteLine();

        // ---------------------------------------------------------------- 读包
        if (!File.Exists(packPath))
        {
            Console.Error.WriteLine($"[x] 找不到采集包：{packPath}");
            Console.Error.WriteLine("    用户在「关于 › 机型适配 › 一键采集」采完之后，文件在控制中心的");
            Console.Error.WriteLine("    logs 目录里，名字形如 adopt-<机型>-<时间>.json。");
            return ExitBadPack;
        }

        JsonObject? pack;
        try
        {
            pack = JsonNode.Parse(File.ReadAllText(packPath)) as JsonObject;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[x] 这个文件不是合法的 JSON：{e.Message}");
            return ExitBadPack;
        }
        if (pack is null)
        {
            Console.Error.WriteLine("[x] 这个文件不是采集包（顶层不是 JSON 对象）。");
            return ExitBadPack;
        }

        // ---------------------------------------------------------------- 转换 + 复核
        var result = PackImport.Import(pack, allowWrite);
        var review = result.Review;

        PrintHeader(packPath, pack);

        if (result.Error is not null)
        {
            Console.Error.WriteLine("[x] " + result.Error);
            return ExitBadPack;
        }

        Console.WriteLine(review.Ready
            ? "  结论      : 可以并入（下面「需要知道的」请先过一遍）"
            : "  结论      : 不能并入 —— 先处理下面这些阻塞项");
        Console.WriteLine();

        if (review.Blockers.Count > 0)
        {
            Console.WriteLine("  阻塞项");
            foreach (var b in review.Blockers) Console.WriteLine("    [x] " + b);
            Console.WriteLine();
        }

        if (review.Warnings.Count > 0)
        {
            Console.WriteLine("  需要知道的");
            foreach (var w in review.Warnings) Console.WriteLine("    [!] " + w);
            Console.WriteLine();
        }

        if (review.Notes.Count > 0)
        {
            Console.WriteLine("  依据");
            foreach (var n in review.Notes) Console.WriteLine("    · " + n);
            Console.WriteLine();
        }

        if (result.Profile is not { } profile)
        {
            // 走到这里 = 有阻塞项。不写文件，但仍然告诉人"改什么就能过"
            Console.WriteLine(rule);
            Console.WriteLine("  没有写出任何文件。");
            return ExitBadPack;
        }

        // ---------------------------------------------------------------- 落盘
        var dir = string.IsNullOrWhiteSpace(outDir)
            ? Path.Combine(Directory.GetCurrentDirectory(), "devices")
            : Path.GetFullPath(outDir);
        var id = Str(profile["id"]);
        var target = Path.Combine(dir, id + ".json");

        Console.WriteLine("  档案 id   : " + id);
        Console.WriteLine("  档案名    : " + Str(profile["name"]));
        Console.WriteLine("  写入权限  : " + (Bool(profile["verified"]) ? "开（作者显式 --allow-write）" : "关（只读展示）"));
        Console.WriteLine("  目标位置  : " + target);
        Console.WriteLine();

        if (dryRun)
        {
            Console.WriteLine(rule);
            Console.WriteLine("  --dry-run：只看结论，没有写文件。");
            return ExitOk;
        }

        if (File.Exists(target) && !force)
        {
            Console.Error.WriteLine("[x] 目标文件已经存在（默认不覆盖）：" + target);
            Console.Error.WriteLine("    devices\\*.json 是手改的唯一真源，覆盖没有补救途径。");
            Console.Error.WriteLine("    确认要替换就再加 --force。");
            return ExitWriteFailed;
        }

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(target, profile.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("[x] 写不进 " + target + "：" + e.Message);
            return ExitWriteFailed;
        }

        Console.WriteLine(rule);
        Console.WriteLine("  已写入    : " + target);
        Console.WriteLine();
        NextSteps(profile);
        return ExitOk;
    }

    private static void PrintHeader(string packPath, JsonObject pack)
    {
        var identity = pack["identity"] as JsonObject;
        var app = pack["app"] as JsonObject;
        var adopt = pack["profile_draft"]?["adopt"] as JsonObject;

        Console.WriteLine("  包        : " + Path.GetFullPath(packPath));
        Console.WriteLine("  采集时间  : " + Str(pack["created"]));
        Console.WriteLine("  采集程序  : " + Str(app?["version"]));
        Console.WriteLine("  采样帧数  : " + ((pack["samples"] as JsonArray)?.Count ?? 0));
        Console.WriteLine("  机型      : " + Join(" / ", Str(identity?["system_vendor"]), Str(identity?["system_product"])));
        Console.WriteLine("  模具      : " + Str(pack["profile_draft"]?["mold"]));
        Console.WriteLine("  采集结果  : " + (Bool(adopt?["layout_verified"]) ? "认出了温度偏移" : "没认出温度偏移（只认机型）"));
        Console.WriteLine();
    }

    /// <summary>
    /// 打完档案之后该干什么。这一段是**给作者看的操作单**：
    /// 采集能自动填的已经填完了，剩下的恰好是采集证明不了的三件事。
    /// </summary>
    private static void NextSteps(JsonObject profile)
    {
        var writeOn = Bool(profile["verified"]);

        Console.WriteLine("  下一步");
        Console.WriteLine("    1. 复核 caps：这份档案现在照抄的是通用档案 —— 风扇几路、有没有 MUX、");
        Console.WriteLine("       RGB 几个区、三档性能对应哪个 APMD，都要按原厂行为确认（docs/DEVICES.md 第三步）。");
        Console.WriteLine("    2. 用真机验读数：装到那台机器上（或它同模具的机器），看温度/转速是否合理。");
        if (!writeOn)
        {
            Console.WriteLine("    3. 写行为确认之后才开写权限：加 --allow-write 重新导入，或手改 verified=true。");
            Console.WriteLine("       采集只能证明「读得出来」，证明不了「写进去是对的」。");
        }
        else
        {
            Console.WriteLine("    3. 这份档案已带写权限（--allow-write）：先在原厂软件里把各模式/灯效拍一张，写错了能照着恢复。");
        }
        Console.WriteLine("    4. 回归：dotnet test tests\\QControl.Tests -c Release");
        Console.WriteLine("       然后打包发版（src\\pack-release.cmd）。");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 小工具

    private static string Str(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static bool Bool(JsonNode? n)
        => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string Join(string sep, params string[] parts)
        => string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
