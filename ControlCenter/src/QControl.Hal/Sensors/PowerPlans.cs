using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using QControl.Core.Model;
using QControl.Hal.Sys;

namespace QControl.Hal.Sensors;

/// <summary>
/// Windows 电源计划列表（`GET /api/power/plans`）。
///
/// 解析 `powercfg` 的输出：
/// <code>
///   powercfg /list
///   GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (平衡)
///   电源方案 GUID: ... (高性能) *
/// </code>
/// 三件事必须容忍（否则换个语言/版本就全空）：
///   ① 前缀词不固定（英文 `Power Scheme GUID:`、简中 `电源方案 GUID:`）⇒ 只认
///      `GUID:` 后面那 36 位 + 括号里的名字，前缀一律不管；
///   ② 当前方案用行尾的 `*` 标记，位置在括号**之后**；
///   ③ 输出编码是系统代码页，不是 UTF-8（见 `Sys/Shell.cs`）。
/// </summary>
public static partial class PowerPlans
{
    /// <summary>名字里的括号只能是"(平衡)"这种，不跨行、不贪婪。</summary>
    [GeneratedRegex(@"GUID:\s*([0-9a-fA-F\-]{36})\s*\((.+?)\)\s*(\*)?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PlanLine();

    /// <summary>GUID 白名单：写操作前必须过这一关（`powercfg /setactive` 会拒绝坏 GUID，
    /// 但拒绝得晚，先自己拦掉更清楚）。</summary>
    public static bool IsGuid(string? s) => PowerPlanPick.IsGuid(s);

    /// <summary>
    /// 按名字挑一条的计划（同名多条时给确定性答案：正在用的 &gt; 系统自带 &gt; 第一条）。
    /// 规则本体在 <see cref="PowerPlanPick"/> —— 那里是纯字符串逻辑、有单测；
    /// 这里只是让调用方不必同时 import 两个命名空间。
    /// </summary>
    public static string? PickByName(IEnumerable<(string? Guid, string? Name)> plans,
                                     string? want, string? active, out int matches)
        => PowerPlanPick.PickByName(plans, want, active, out matches);

    public static bool IsStock(string? guid) => PowerPlanPick.IsStock(guid);

    /// <summary>
    /// 这条计划是谁造的 —— 读注册表里的 `FriendlyName`，判据是**名字的形态**：
    ///
    /// ```
    /// 平衡    @C:\WINDOWS\system32\powrprof.dll,-15,Balanced   ← MUI 资源引用（Windows 自带，跟随系统语言）
    /// 办公    办公                                            ← 写死的文本（创建它的程序填的）
    /// ```
    ///
    /// 为什么不用"GUID 在不在标准表里"（<see cref="IsStock"/>）：那是猜。
    /// 这条能直接答"它是不是 Windows 自带的"，而用户问的正是这个 ——
    /// 本机三条非系统计划（办公 / 游戏 / 高性能）的描述分别是「办公模式 / 游戏模式 / 狂暴模式」，
    /// 一眼就能看出是原厂 ControlCenter 开的档位。
    ///
    /// 返回 `"system"` / `"vendor"`；**读不到就返回 null**（界面不标）——
    /// 拿不准来源时宁可空着，也别把"我读不到"显示成"这是厂商的"。
    /// </summary>
    private static string? SourceOf(string guid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SchemesKey + "\\" + guid);
            if (key?.GetValue("FriendlyName") is not string name || name.Length == 0) return null;
            return name.StartsWith('@') ? "system" : "vendor";
        }
        catch
        {
            // 注册表读不到（权限 / 键被删）⇒ 不标，别猜
            return null;
        }
    }

    /// <summary>电源计划都挂在这个键下，每台机器一致（不是每用户方案）。</summary>
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    /// <summary>
    /// 一条计划的关键数值（**有效值**，已经算上继承）。
    ///
    /// 为什么不用注册表：`HKLM\…\PowerSchemes\&lt;guid&gt;` 下只有"自己覆盖过"的项 ——
    /// 本机 平衡 / 办公 / 游戏 三条在注册表里**一个值都没有**（继承 powrprof 的编译期默认），
    /// 直接读会得到空，而 `powercfg /query` 报的是 5% / 5%。所以要么跑 powercfg（每条一次，
    /// 百毫秒级），要么走 powrprof 的 `PowerReadACValueIndex`（进程内，微秒级，且同样返回合并后的有效值）。
    /// 选后者：5 条计划 × 6 个值 = 全在 1 毫秒内读完，还能放心在刷新时反复调。
    ///
    /// 任何一项读失败就是 `null`（界面那边整项不显示）—— 不拿默认值糊过去。
    /// </summary>
    public readonly record struct PlanValues(
        int? MinAc, int? MinDc, int? MaxAc, int? MaxDc, int? CoolAc, int? CoolDc,
        int? BrightAc = null, int? BrightDc = null)
    {
        /// <summary>一个值都没有 ⇒ 调用方当"读不到"处理。</summary>
        public bool Any => MinAc is not null || MinDc is not null || MaxAc is not null
                           || MaxDc is not null || CoolAc is not null || CoolDc is not null
                           || BrightAc is not null || BrightDc is not null;
    }

    // 与 powercfg 的别名一一对应（这几个 GUID 是微软固定值，不随语言变）
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid ThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid SysCoolPol = new("94d3a615-a899-4ac5-ae2b-e4d8f634367f");

    // 「显示」子组里的「显示器亮度」（powercfg 的 SUB_VIDEO / VIDEONORMALLEVEL）——
    // 切档会切电源计划，而亮度是计划里的一项，所以界面要能直接看到每条计划是多少。
    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoNormalLevel = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme,
                                                     ref Guid sub, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme,
                                                     ref Guid sub, ref Guid setting, out uint value);

    /// <summary>读一条计划的处理器上下限、散热方式与屏幕亮度（交流 / 电池各一份）。</summary>
    public static PlanValues Values(string? guid)
    {
        if (!Guid.TryParse(guid, out var scheme)) return default;

        // ⚠️ `sub` 必须当参数传：早先写死 SubProcessor，加「显示」子组时只能再抄一份。
        static int? Read(bool ac, Guid scheme, Guid sub, Guid setting)
        {
            try
            {
                var set = setting;
                var rc = ac
                    ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out var v)
                    : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out v);
                return rc == 0 ? (int)v : null;      // 0 = ERROR_SUCCESS
            }
            catch
            {
                // 老系统没有这两个导出（Vista 起就有，理论上不会）⇒ 当读不到
                return null;
            }
        }

        return new PlanValues(
            Read(true, scheme, SubProcessor, ThrottleMin), Read(false, scheme, SubProcessor, ThrottleMin),
            Read(true, scheme, SubProcessor, ThrottleMax), Read(false, scheme, SubProcessor, ThrottleMax),
            Read(true, scheme, SubProcessor, SysCoolPol), Read(false, scheme, SubProcessor, SysCoolPol),
            Read(true, scheme, SubVideo, VideoNormalLevel), Read(false, scheme, SubVideo, VideoNormalLevel));
    }

    /// <summary>返回 `{"plans":[{"guid","name","source"}…],"active":guid|null}`。
    /// `source` 可能缺席（<see cref="SourceOf"/> 读不到时）—— 契约里它是可空的。
    /// ⚠️ 数值**不进**这个契约：只有界面用得上，走 <see cref="Values"/> 单独读，
    ///    别为了显示"5–100%"让 /api/power/plans 多背十几个字段。</summary>
    public static JsonObject List()
    {
        var plans = new JsonArray();
        string? active = null;

        foreach (var line in Shell.RunText("powercfg", "/list").Split('\n'))
        {
            var m = PlanLine().Match(line.TrimEnd('\r'));
            if (!m.Success) continue;

            var guid = m.Groups[1].Value;
            var row = new JsonObject
            {
                // ⚠️ 名字里偶尔带空格（"高性能 模式"），去首尾空白即可，别做别的清洗
                ["guid"] = guid,
                ["name"] = m.Groups[2].Value.Trim(),
            };
            if (SourceOf(guid) is { } src) row["source"] = src;
            plans.Add(row);

            if (m.Groups[3].Success) active = guid;
        }

        return new JsonObject { ["plans"] = plans, ["active"] = active };
    }

    /// <summary>
    /// 切换电源计划。**必须看退出码**（2026-10-17 的教训）：
    /// `powercfg /setactive` 在「GUID 已删除 / 被安全软件拦 / 权限不足」时返回非 0，
    /// 旧实现写完就报 ok=True —— 界面提示已切换，系统实际没变。
    /// </summary>
    public static JsonObject SetActive(string? guid)
    {
        if (!IsGuid(guid))
            return new JsonObject { ["ok"] = false, ["error"] = "非法 GUID" };

        var (code, _, err) = Shell.Run("powercfg", $"/setactive {guid}");
        if (code != 0)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["guid"] = guid,
                ["error"] = code is null
                    ? $"powercfg 没跑起来：{err}"
                    : $"powercfg /setactive 失败（返回 {code}）",
            };
        }
        return new JsonObject { ["ok"] = true, ["guid"] = guid };
    }
}
