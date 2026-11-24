using System.Text.Json.Nodes;
using QControl.Core.Data;
using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「用户采集包 → 正式机型档案」的导入逻辑。
///
/// 这一段决定两件事，都必须钉死：
///   · **不给写权限**：包里的 `verified` / `writes_allowed` 是用户机器上的值，一律不信；
///   · **不产出认不出机器的档案**：match 用采集时的身份跑不通就拒绝，
///     否则发版出去对谁都不生效（包括采集它的人自己）。
/// </summary>
public class PackImportTests
{
    // ---------------------------------------------------------------- 样本

    private static JsonObject Identity(bool withProject = true)
    {
        var o = new JsonObject
        {
            ["system_vendor"] = "COLORFUL",
            ["system_product"] = "G16 Pro",
            ["board_vendor"] = "COLORFUL",
            ["board_product"] = "G16 Pro",
        };
        if (withProject) o["vendor_registry"] = new JsonObject { ["NL5"] = true };
        return o;
    }

    private static JsonObject Candidates(bool withProject = true)
        => withProject
            ? new JsonObject { ["NL5"] = new JsonObject { ["sub"] = new JsonArray("ControlCenter") } }
            : new JsonObject();

    private static List<LayoutSample> CpuLoadSamples()
    {
        double[] cpuW = [20, 28, 38, 48, 58, 68, 76, 84, 88, 90];
        double[] cpuT = [52, 56, 61, 66, 70, 74, 77, 79, 80, 81];

        var list = new List<LayoutSample>();
        for (var i = 0; i < cpuW.Length; i++)
            list.Add(new LayoutSample(cpuW[i], double.NaN, 45, new Dictionary<string, uint[]>(StringComparer.Ordinal)
            {
                ["status"] = [(uint)cpuT[i], 46u, (uint)(2000 + cpuT[i] * 25), (uint)(2200 + cpuT[i] * 18), 2u],
            }));
        return list;
    }

    /// <summary>拼一份和真机 `logs\adopt-*.json` 同构的采集包。</summary>
    private static JsonObject Pack(
        bool withProject = true,
        LayoutAdvice? advice = null,
        bool withDraft = true,
        string id = "quanta-nl5")
    {
        var adv = advice ?? EcLayoutAdvisor.Advise(CpuLoadSamples());
        var profile = DeviceProfileDraft.Build(
            Identity(withProject), Candidates(withProject),
            EmbeddedData.ReadObject("devices/quanta-gmwmi-generic.json"),
            adv, "0.1.0", samples: 10,
            refs: new JsonObject { ["cpu_power"] = true, ["cpu_temp"] = false, ["gpu_temp"] = true },
            selfCheck: []);
        profile["id"] = id;

        var pack = new JsonObject
        {
            ["schema"] = PackImport.SchemaId,
            ["schema_version"] = 1,
            ["created"] = "2026-11-07 12:00:00 +08:00",
            ["app"] = new JsonObject { ["version"] = "0.1.0" },
            ["identity"] = Identity(withProject),
            ["vendor_candidates"] = Candidates(withProject),
            ["samples"] = new JsonArray(),
        };
        if (withDraft) pack["profile_draft"] = profile;
        return pack;
    }

    /// <summary>这个 id 一定要避开内置档案，否则"同名拒绝"那条规则会先命中。</summary>
    private const string FreshId = "quanta-nl9";

    private static JsonObject Draft(JsonObject pack) => pack["profile_draft"]!.AsObject();

    // ---------------------------------------------------------------- 正常路径

    [Fact]
    public void 导入后得到可并入的正式档案()
    {
        var result = PackImport.Import(Pack(id: FreshId));

        Assert.True(result.Ok, string.Join(" / ", result.Review.Blockers));
        var p = result.Profile!;

        Assert.Equal(FreshId, p["id"]!.GetValue<string>());
        // 正式档案与内置已验证机型同档；草案里那个 50 是"本机临时档"的下限
        Assert.Equal((double)PackImport.FormalPriority, p["priority"]!.GetValue<double>());
        Assert.True(p["priority"]!.GetValue<double>() > DeviceProfile.UserPriorityFloor);
        Assert.True(p["priority"]!.GetValue<double>() > 10, "必须压过 generic");

        // 只读导入：写入恒关
        Assert.False(p["verified"]!.GetValue<bool>());
        // 这个键存在时会盖过 verified（Device.ProfileWrites），不能留在正式档案里
        Assert.False(p.ContainsKey("writes_allowed"));

        // 认出偏移这件事要原样带过来
        Assert.True(p["ec"]!["layout_verified"]!.GetValue<bool>());
        Assert.Equal(4, p["ec"]!["layout"]!["cpu_temp"]![0]!.GetValue<int>());

        // 来源可追溯
        var import = p["adopt"]!["import"]!.AsObject();
        Assert.Equal("2026-11-07 12:00:00 +08:00", import["pack_created"]!.GetValue<string>());
        Assert.Equal("0.1.0", import["pack_app"]!.GetValue<string>());
        Assert.Contains("未开启", import["write_allowed_by"]!.GetValue<string>());
    }

    [Fact]
    public void match自检用采集机身份跑通()
    {
        var result = PackImport.Import(Pack(id: FreshId));

        Assert.True(result.Ok);
        Assert.False(string.IsNullOrWhiteSpace(result.MatchedBy), "应报出命中了哪些字段");
        Assert.Contains("vendor_registry", result.MatchedBy);
    }

    // ---------------------------------------------------------------- 写权限

    [Fact]
    public void 包里自称的写权限一律不信()
    {
        // 有人手改包、或者旧版采集带了 verified=true —— 都不该变成正式档案的写权限
        var pack = Pack(id: FreshId);
        Draft(pack)["verified"] = true;
        Draft(pack)["writes_allowed"] = true;

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        Assert.False(result.Profile!["verified"]!.GetValue<bool>(), "包里的 verified 不可信");
        Assert.False(result.Profile!.ContainsKey("writes_allowed"));
    }

    [Fact]
    public void 只有显式allowWrite才开写权限()
    {
        var result = PackImport.Import(Pack(id: FreshId), allowWrite: true);

        Assert.True(result.Ok);
        Assert.True(result.Profile!["verified"]!.GetValue<bool>());
        Assert.Contains("作者显式", result.Profile!["adopt"]!["import"]!["write_allowed_by"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- 拒绝的情形

    [Fact]
    public void match跑不通就拒绝且不产出档案()
    {
        // 把规则改成一条采集机身份里根本不存在的项目号
        var pack = Pack(id: FreshId);
        Draft(pack)["match"] = new JsonObject
        {
            ["any"] = new JsonArray(new JsonObject { ["vendor_registry"] = new JsonArray("^ZZ9$") }),
        };

        var result = PackImport.Import(pack);

        Assert.False(result.Ok);
        Assert.Null(result.Profile);
        Assert.Contains(result.Review.Blockers, b => b.Contains("match 自检"));
    }

    [Fact]
    public void 与内置已验证档案同名时拒绝()
    {
        // 本机 NL5 采出来的草案 id 正是 quanta-nl5 —— 导入它会把已验证标定顶掉
        var result = PackImport.Import(Pack(id: "quanta-nl5"));

        Assert.False(result.Ok);
        Assert.Null(result.Profile);
        Assert.Contains(result.Review.Blockers, b => b.Contains("quanta-nl5"));
    }

    [Fact]
    public void 旧版采集包没有草案就明确拒绝()
    {
        var result = PackImport.Import(Pack(withDraft: false));

        Assert.False(result.Ok);
        Assert.Null(result.Profile);      // 拒绝就不产出档案
        Assert.Contains("profile_draft", result.Error);
    }

    [Fact]
    public void 不是采集包时拒绝()
    {
        var devices = EmbeddedData.ReadObject("devices/quanta-nl5.json");
        var result = PackImport.Import(devices);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains(PackImport.SchemaId, result.Error);
    }

    [Fact]
    public void 结构怪异的包不抛异常()
    {
        var pack = Pack(id: FreshId);
        pack["identity"] = new JsonArray(1, 2, 3);    // 类型都不对

        var result = PackImport.Import(pack);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void 没有项目号时仍可导入但必须警告()
    {
        var result = PackImport.Import(Pack(withProject: false, id: "quanta-g16pro"));

        Assert.True(result.Ok, string.Join(" / ", result.Review.Blockers));
        Assert.Contains(result.Review.Warnings, w => w.Contains("项目号"));
        // match 退回到机型名，自检仍然要能过
        Assert.False(string.IsNullOrWhiteSpace(result.MatchedBy));
    }

    // ---------------------------------------------------------------- 证据齐全度

    [Fact]
    public void 没认出偏移时警告layout为空()
    {
        var result = PackImport.Import(Pack(advice: EcLayoutAdvisor.Advise([]), id: FreshId));

        Assert.True(result.Ok, "只认机型也是一份能用的档案，不该拦");
        Assert.Contains(result.Review.Warnings, w => w.Contains("layout 是空的"));
    }

    [Fact]
    public void 没有采样帧时提示无法复算()
    {
        var pack = Pack(id: FreshId);
        pack.Remove("samples");

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        Assert.Contains(result.Review.Warnings, w => w.Contains("采样帧"));
    }

    [Fact]
    public void 照抄的caps要被点出来()
    {
        var result = PackImport.Import(Pack(id: FreshId));

        Assert.Contains(result.Review.Warnings, w => w.Contains("caps 是照抄通用档案的"));
    }

    [Fact]
    public void 认出的字段写进依据()
    {
        var result = PackImport.Import(Pack(id: FreshId));

        Assert.Contains(result.Review.Notes, n => n.Contains("cpu_temp"));
    }

    // ---------------------------------------------------------------- 用户自测写入

    private static JsonObject WriteTest(int ok, int denied, int error)
        => new()
        {
            ["ec_writes_allowed"] = true,
            ["admin"] = true,
            ["log"] = new JsonObject
            {
                ["kept"] = ok + denied + error,
                ["dropped"] = 0,
                ["ok"] = ok,
                ["denied"] = denied,
                ["error"] = error,
                ["items"] = new JsonArray(),
            },
            ["apmd_now"] = 3,
            ["perf_key"] = "beast",
        };

    [Fact]
    public void 旧包没有写入测试时要点出这条路是空白()
    {
        var result = PackImport.Import(Pack(id: FreshId));

        Assert.True(result.Ok);
        Assert.Contains(result.Review.Warnings, w => w.Contains("包里没有写入记录"));
    }

    [Fact]
    public void 写探针验过也只进依据不放开写权限()
    {
        var pack = Pack(id: FreshId);
        pack["write_test"] = ProbeWriteTest(perfEffective: true, lightEffective: true);

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        // 读回一致 = 命令真生效；但"哪一档对应哪种功耗"仍没验 ⇒ 只进依据
        Assert.Contains(result.Review.Notes, n => n.Contains("写探针验过 0x0300"));
        Assert.Contains(result.Review.Notes, n => n.Contains("写探针验过 0x0100"));
        Assert.False(result.Profile!["verified"]!.GetValue<bool>());

        var probe = result.Profile!["adopt"]!["import"]!["probe"]!.AsObject();
        Assert.True(probe["perf_effective"]!.GetValue<bool>());
        Assert.True(probe["backlight_effective"]!.GetValue<bool>());
        Assert.Equal(20, probe["backlight_offset"]!.GetValue<int>());
    }

    [Fact]
    public void 写探针对不上时要明确拦住能开写的错觉()
    {
        var pack = Pack(id: FreshId);
        pack["write_test"] = ProbeWriteTest(perfEffective: false, lightEffective: false);

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        Assert.Contains(result.Review.Warnings, w => w.Contains("读回对不上"));
        Assert.Contains(result.Review.Warnings, w => w.Contains("没在 status 帧里对上档位"));
    }

    [Fact]
    public void 只读采集的包要说明写这条路没有证据()
    {
        var pack = Pack(id: FreshId);
        pack["write_test"] = new JsonObject
        {
            ["mode"] = "readonly",
            ["admin"] = true,
            ["skip"] = "只读采集：没有向 EC 写任何东西。",
            ["log"] = new JsonObject { ["kept"] = 0, ["dropped"] = 0, ["ok"] = 0, ["denied"] = 0, ["error"] = 0 },
        };

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        Assert.Contains(result.Review.Warnings, w => w.Contains("只读采集"));
    }

    [Fact]
    public void 风扇永远标注未验证()
    {
        // 采集故意不写风扇（写进去 EC 会一直按那个值转）—— 这条必须在每一份导入里都说
        var pack = Pack(id: FreshId);
        pack["write_test"] = ProbeWriteTest(perfEffective: true, lightEffective: true);

        var result = PackImport.Import(pack);

        Assert.Contains(result.Review.Warnings, w => w.Contains("风扇写入仍未验证"));
    }

    [Fact]
    public void 一次都没写出去时要拦住能写的错觉()
    {
        var pack = Pack(id: FreshId);
        pack["write_test"] = WriteTest(ok: 0, denied: 3, error: 0);

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        Assert.Contains(result.Review.Warnings, w => w.Contains("一次都没拿到 EC 回帧"));
    }

    /// <summary>新版「一键采集（完整）」产出的 write_test：带写探针的逐次读回。</summary>
    private static JsonObject ProbeWriteTest(bool perfEffective, bool lightEffective)
        => new()
        {
            ["mode"] = "probe",
            ["admin"] = true,
            ["ec_writes_allowed"] = false,
            ["perf"] = new JsonObject
            {
                ["attempted"] = true,
                ["cmd"] = "0x0300",
                ["before"] = 1,
                ["steps"] = new JsonArray(
                    new JsonObject { ["wrote"] = 0, ["readback"] = perfEffective ? 0 : 7, ["effective"] = perfEffective },
                    new JsonObject { ["wrote"] = 1, ["readback"] = 1, ["effective"] = perfEffective },
                    new JsonObject { ["wrote"] = 2, ["readback"] = perfEffective ? 2 : 9, ["effective"] = perfEffective }),
                ["effective"] = perfEffective,
                ["restored"] = true,
            },
            ["backlight"] = new JsonObject
            {
                ["attempted"] = true,
                ["cmd"] = "0x0100",
                ["wrote"] = new JsonArray(0, 2),
                ["found_offset"] = lightEffective ? 20 : null,
                ["effective"] = lightEffective,
                ["restored"] = true,
                ["restored_level"] = 2,
            },
            ["log"] = new JsonObject
            {
                ["kept"] = 6, ["dropped"] = 0, ["ok"] = 6, ["denied"] = 0, ["error"] = 0,
                ["items"] = new JsonArray(),
            },
            ["apmd_now"] = 1,
            ["perf_key"] = "game",
        };

    // ---------------------------------------------------------------- 观察原厂

    [Fact]
    public void 观察原厂结论被并入档案并转述()
    {
        var pack = Pack(id: FreshId);
        var draft = Draft(pack);
        var adopt = draft["adopt"]!.AsObject();
        adopt["observation"] = new JsonObject
        {
            ["write_cmds"] = new JsonArray(new JsonObject { ["gwf1"] = "0x0100" }),
            ["conclusion"] = new JsonObject { ["keyboard_rgb"] = true, ["gfx_switch"] = true },
            ["summary"] = new JsonObject(),
            ["notes"] = new JsonArray(),
        };
        // Collect 会把观察结论合并进 caps
        draft["caps"] = new JsonObject { ["keyboard_rgb"] = true, ["gfx_switch"] = true };

        var result = PackImport.Import(pack);

        Assert.True(result.Ok, string.Join(" / ", result.Review.Blockers));
        Assert.Contains(result.Review.Notes, n => n.Contains("观察原厂抓到 1 条写命令"));
        Assert.Contains(result.Review.Notes, n => n.Contains("keyboard_rgb") && n.Contains("gfx_switch"));
        Assert.True(result.Profile!["caps"]!["keyboard_rgb"]!.GetValue<bool>());
        // 有观察时，"照抄通用档案"那条警告换成"部分来自观察"
        Assert.DoesNotContain(result.Review.Warnings, w => w.Contains("caps 是照抄通用档案"));
        Assert.Contains(result.Review.Warnings, w => w.Contains("观察原厂"));
    }

    [Fact]
    public void 观察但一条写命令都没抓到要警告()
    {
        var pack = Pack(id: FreshId);
        Draft(pack)["adopt"]!.AsObject()["observation"] = new JsonObject
        {
            ["write_cmds"] = new JsonArray(),
            ["conclusion"] = new JsonObject(),
            ["summary"] = new JsonObject(),
            ["notes"] = new JsonArray(),
        };

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        Assert.Contains(result.Review.Warnings, w => w.Contains("一条写命令都没抓到"));
    }

    [Fact]
    public void 观察原厂被跳过时要说清原因()
    {
        var pack = Pack(id: FreshId);
        Draft(pack)["adopt"]!.AsObject()["observation"] = new JsonObject
        {
            ["write_cmds"] = new JsonArray(),
            ["skipped"] = "原厂控制中心没在运行",
        };

        var result = PackImport.Import(pack);

        Assert.True(result.Ok);
        Assert.Contains(result.Review.Warnings,
            w => w.Contains("观察原厂没做成") && w.Contains("原厂控制中心没在运行"));
    }
}
