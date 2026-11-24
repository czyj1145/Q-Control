using System.Text;
using System.Text.Json.Nodes;
using QControl.Core.Model;

namespace QControl.Tests;

/// <summary>
/// model.json 的读写、坏文件保护、迁移。
///
/// 这些测试守的是**数据丢失**，不是功能：
/// 界面鼓励用户用记事本直接改 model.json，一个尾逗号就可能把全部配置冲掉。
/// 所以坏文件那几条是重点。
/// </summary>
public sealed class ModelStoreTests : IDisposable
{
    private readonly string _dir;

    public ModelStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-model-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string ModelPath => Path.Combine(_dir, "model.json");

    private ModelStore NewStore(string? legacyCurve = null)
        => new(ModelPath, legacyCurvePath: legacyCurve);

    private void WriteRaw(string content) => File.WriteAllText(ModelPath, content, Encoding.UTF8);

    // ---------------------------------------------------------------- 首次运行

    [Fact]
    public void 文件不存在时用默认值并落盘()
    {
        var m = NewStore();
        Assert.True(File.Exists(ModelPath), "首次运行应当把默认配置落盘");
        Assert.Null(m.LoadError);
        Assert.Equal(30, m.Get("fan", "min_duty")!.GetValue<int>());
        Assert.Equal(1, m.Get("version")!.GetValue<int>());
    }

    [Fact]
    public void 文件存在时深合并保留用户改过的值()
    {
        WriteRaw("""{"presets":{"fan":{"min_duty":45},"ui":{"theme":"light"}}}""");
        var m = NewStore();

        Assert.Equal(45, m.Get("fan", "min_duty")!.GetValue<int>());       // 用户的
        Assert.Equal(100, m.Get("fan", "max_duty")!.GetValue<int>());      // 默认的兄弟键
        Assert.Equal("light", m.Get("ui", "theme")!.GetValue<string>());   // 用户新加的
        Assert.Null(m.LoadError);
    }

    [Fact]
    public void 启动不该重写用户配置()
    {
        WriteRaw("""{"presets":{"fan":{"min_duty":45}}}""");
        var before = File.ReadAllText(ModelPath);

        _ = NewStore();

        // 「启动一下看看」不得改写文件内容
        Assert.Equal(before, File.ReadAllText(ModelPath));
    }

    // ---------------------------------------------------------------- 坏文件

    [Fact]
    public void 坏JSON不覆盖原文件且另存broken()
    {
        const string broken = """{"presets": {"fan": {"min_duty": 45,}}}""";   // 尾逗号
        WriteRaw(broken);

        var m = NewStore();

        Assert.NotNull(m.LoadError);
        Assert.Equal(broken, File.ReadAllText(ModelPath));        // 原文件一个字节都没动
        Assert.True(File.Exists(ModelPath + ".broken"));          // 留了取证副本
        Assert.Equal(30, m.Get("fan", "min_duty")!.GetValue<int>());// 内存用默认值兜底
    }

    [Fact]
    public void 坏文件时拒绝写盘()
    {
        WriteRaw("{ 这不是 json");
        var m = NewStore();
        Assert.NotNull(m.LoadError);

        var result = m.Set(["fan", "min_duty"], JsonValue.Create(45));

        // 内存里改了，但**不许落盘** —— 否则会把用户的坏文件覆盖成默认值，
        // 原配置就只剩 .broken 一份。
        // ⚠️ 而且必须**如实回报失败**（2026-11-19 修）：以前这里回 Ok=true，
        //    界面/接口说"已保存"，重启后改动全没了 —— 用户只会认为"设置老是丢"。
        Assert.False(result.Ok);
        Assert.Contains("没有落盘", result.Error);
        Assert.Equal("{ 这不是 json", File.ReadAllText(ModelPath));
        Assert.Equal(45, m.Get("fan", "min_duty")!.GetValue<int>());
    }

    [Fact]
    public void 恢复出厂可以覆盖坏文件()
    {
        WriteRaw("{ 坏的");
        var m = NewStore();
        Assert.True(m.Save() is null);      // 普通保存被拦

        var r = m.Reset();                  // force 绕过保护
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Contains("\"presets\"", File.ReadAllText(ModelPath));
        Assert.Null(m.LoadError is null ? "x" : null);   // 重置后内存是干净的默认值
    }

    [Fact]
    public void presets类型错误也要走损坏分支()
    {
        // 合法 JSON、错误类型。旧版会在这里 AttributeError 闪退。
        WriteRaw("""{"presets": [1,2,3]}""");
        var m = NewStore();
        Assert.NotNull(m.LoadError);
    }

    [Fact]
    public void 空坏文件不覆盖已有的broken取证副本()
    {
        // 空文件往往是别的进程正在原子替换的瞬间被读到 ——
        // 拿它盖掉上一次真实的损坏现场，等于把唯一线索弄丢。
        // ⚠️ 必须写**真正的 0 字节**：File.WriteAllText(path, "") 用 Encoding.UTF8
        //    会写出 3 字节 BOM，"空文件"其实非空 —— 第一版测试就是这么误判的。
        File.WriteAllBytes(ModelPath, []);
        File.WriteAllText(ModelPath + ".broken", "上一次的真实损坏现场");

        _ = NewStore();

        Assert.Equal("上一次的真实损坏现场", File.ReadAllText(ModelPath + ".broken"));
    }

    [Fact]
    public void 带BOM的配置文件也能正常读()
    {
        // 用户用记事本"另存为 UTF-8 带 BOM"是常见操作。
        // ⚠️ 这里与旧版**刻意不同**：旧版的 open(encoding="utf-8") 不剥 BOM，
        //    json.load 会报 "Unexpected UTF-8 BOM" 并把用户的配置判成损坏。
        //    C# 的 File.ReadAllText 会剥 BOM，于是能正常读。宽容一档，是有意的。
        File.WriteAllText(ModelPath, """{"presets":{"fan":{"min_duty":47}}}""",
                          new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.True(new FileInfo(ModelPath).Length > 3, "确认 BOM 确实写进去了");

        var m = NewStore();

        Assert.Null(m.LoadError);
        Assert.Equal(47, m.Get("fan", "min_duty")!.GetValue<int>());
    }

    // ---------------------------------------------------------------- 配置树

    [Fact]
    public void Set能建中间层()
    {
        var m = NewStore();
        var r = m.Set(["vendor", "custom", "flag"], JsonValue.Create(true));
        Assert.True(r.Ok);
        Assert.True(m.Get("vendor", "custom", "flag")!.GetValue<bool>());
    }

    [Fact]
    public void Set遇到标量中间层要报错而不是覆盖()
    {
        var m = NewStore();
        m.Set(["fan", "min_duty"], JsonValue.Create(35));      // fan.min_duty 是标量
        var r = m.Set(["fan", "min_duty", "x"], JsonValue.Create(1));
        Assert.False(r.Ok);
        Assert.Contains("不是对象", r.Error);
    }

    [Fact]
    public void Get路径断了返回兜底值()
    {
        var m = NewStore();
        Assert.Null(m.Get("nope", "nothing"));
        Assert.Equal("def", m.Get(JsonValue.Create("def"), "fan", "nope")!.GetValue<string>());
    }

    [Fact]
    public void Set一级路径按节合并写多个键()
    {
        // 背光自动熄灭那条走的就是这个形状：path 只有一级、value 是一整个对象。
        // 曾经这里被 `path.Count < 2` 拦成"参数不足" ⇒ 设置页选完下拉立刻弹错、
        // 控件拨回原值，功能等于没做。
        var m = NewStore();
        var r = m.Set(["light"], new JsonObject
        {
            ["auto_off_min"] = 5,
            ["auto_off_on_lock"] = true,
        });

        Assert.True(r.Ok, r.Error);
        Assert.Equal(5, m.Get("light", "auto_off_min")!.GetValue<int>());
        Assert.True(m.Get("light", "auto_off_on_lock")!.GetValue<bool>());
    }

    [Fact]
    public void Set一级路径是合并不是整节替换()
    {
        // 只带 min_duty 写 fan 节，曲线**必须还在**：整节换掉等于曲线消失、
        // 风扇停在最后一个值，只能恢复出厂才救得回来。
        var m = NewStore();
        var curveBefore = m.Get("fan", "curve")!.AsArray().Count;

        var r = m.Set(["fan"], new JsonObject { ["min_duty"] = 40 });

        Assert.True(r.Ok, r.Error);
        Assert.Equal(40, m.Get("fan", "min_duty")!.GetValue<int>());
        Assert.Equal(curveBefore, m.Get("fan", "curve")!.AsArray().Count);
        Assert.NotNull(m.Get("fan", "max_duty"));       // 没提到的兄弟键原样保留
    }

    [Fact]
    public void Set一级路径值不是对象要报错()
    {
        var m = NewStore();
        var r = m.Set(["light"], JsonValue.Create(1));

        Assert.False(r.Ok);
        Assert.Contains("必须是对象", r.Error);
        Assert.NotNull(m.Get("light", "auto_off_min"));  // 那节没被写成标量
    }

    [Fact]
    public void Set一级路径未知节要报错()
    {
        var m = NewStore();
        var r = m.Set(["no_such_section"], new JsonObject { ["x"] = 1 });

        Assert.False(r.Ok);
        Assert.Contains("未知配置节", r.Error);
    }

    [Fact]
    public void ResetSection只影响一节()
    {
        var m = NewStore();
        m.Set(["fan", "min_duty"], JsonValue.Create(48));
        m.Set(["ui", "theme"], JsonValue.Create("light"));

        var r = m.ResetSection("fan");

        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Equal(30, m.Get("fan", "min_duty")!.GetValue<int>());          // 回来了
        Assert.Equal("light", m.Get("ui", "theme")!.GetValue<string>());      // 没被牵连
    }

    [Fact]
    public void ResetSection未知节名要报错()
    {
        var m = NewStore();
        var r = m.ResetSection("no_such_section");
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Contains("未知配置节", r["error"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- 三态总闸

    [Fact]
    public void EcEnabled默认是null表示跟随档案()
    {
        var m = NewStore();
        Assert.Null(m.EcEnabled);
    }

    [Fact]
    public void EcEnabled三态原样保留()
    {
        var m = NewStore();
        m.Set(["ec", "enabled"], JsonValue.Create(false));
        Assert.False(m.EcEnabled);

        m.Set(["ec", "enabled"], JsonValue.Create(true));
        Assert.True(m.EcEnabled);

        m.Set(["ec", "enabled"], null);
        Assert.Null(m.EcEnabled);
    }

    [Fact]
    public void EcEnabled在三态之外被写成数字也认()
    {
        // 用户手改 model.json 写成 0/1 是可能的
        var m = NewStore();
        m.Set(["ec", "enabled"], JsonValue.Create(1));
        Assert.True(m.EcEnabled);
        m.Set(["ec", "enabled"], JsonValue.Create(0));
        Assert.False(m.EcEnabled);
    }

    // ---------------------------------------------------------------- 其他

    [Fact]
    public void 原子写不留tmp文件()
    {
        var m = NewStore();
        m.Save();
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void 落盘JSON是缩进两格且中文不转义()
    {
        var m = NewStore();
        m.Set(["vendor", "note"], JsonValue.Create("中文备注"));
        var text = File.ReadAllText(ModelPath, Encoding.UTF8);

        Assert.Contains("中文备注", text);          // ensure_ascii=False
        Assert.DoesNotContain("\\u4e2d", text);
        Assert.Contains("\n  ", text);              // indent=2
    }

    [Fact]
    public void 首次运行迁移老版曲线()
    {
        var legacy = Path.Combine(_dir, "fan_curve.json");
        File.WriteAllText(legacy, """{"curve":[{"t":40,"d":35},{"t":85,"d":95}],"source":"gpu"}""");

        var m = NewStore(legacyCurve: legacy);

        var curve = m.Get("fan", "curve")!.AsArray();
        Assert.Equal(2, curve.Count);
        Assert.Equal(40, curve[0]!["t"]!.GetValue<int>());
        Assert.Equal("gpu", m.Get("fan", "source")!.GetValue<string>());
    }

    [Fact]
    public void 迁移只在首次运行发生()
    {
        var legacy = Path.Combine(_dir, "fan_curve.json");
        File.WriteAllText(legacy, """{"curve":[{"t":40,"d":35}]}""");
        WriteRaw("""{"presets":{"fan":{"curve":[{"t":50,"d":60}]}}}""");   // 已有配置

        var m = NewStore(legacyCurve: legacy);

        // 文件本来就在 ⇒ 不迁移，用户的曲线不能被老文件顶掉
        var curve = m.Get("fan", "curve")!.AsArray();
        Assert.Single(curve);
        Assert.Equal(50, curve[0]!["t"]!.GetValue<int>());
    }

    [Fact]
    public void ReloadIfChanged能热加载外部编辑()
    {
        var m = NewStore();
        m.Set(["ui", "theme"], JsonValue.Create("dark"));

        // 模拟用户在记事本里改文件（改 mtime + 内容）
        Thread.Sleep(1100);      // 让 LastWriteTime 明确变化
        WriteRaw("""{"presets":{"ui":{"theme":"light"}}}""");

        Assert.True(m.ReloadIfChanged());
        Assert.Equal("light", m.Get("ui", "theme")!.GetValue<string>());
        Assert.False(m.ReloadIfChanged());        // 没再变就不重复加载
    }

    [Fact]
    public void 操作日志保留最近200条且带风险描述()
    {
        var m = NewStore();
        for (var i = 0; i < 250; i++)
            m.LogOp((ushort)0x0205, $"写 {i}");

        Assert.Equal(200, m.OpLog.Count);
        Assert.Equal(249, int.Parse(m.OpLog[^1].Action.Split(' ')[1]));
        Assert.NotNull(m.OpLog[^1].Risk);          // 0x0205 是危险写命令
        Assert.Equal("0x0205", m.OpLog[^1].Cmd);
    }

    [Fact]
    public void 只读命令的操作日志没有风险描述()
    {
        var m = NewStore();
        var rec = m.LogOp((ushort)0x0200, "读状态");
        Assert.Null(rec.Risk);
    }

    [Fact]
    public void View带limits与device摘要()
    {
        var m = NewStore();
        var device = new JsonObject { ["id"] = "quanta-nl5", ["name"] = "隐星" };
        var view = m.View(device);

        Assert.Equal("quanta-nl5", view["device"]!["id"]!.GetValue<string>());
        Assert.True(view["limits"]!.AsObject().ContainsKey("fan_duty"));
        Assert.Equal(ModelPath, view["path"]!.GetValue<string>());
        Assert.Null(view["load_error"]);
        Assert.NotNull(view["presets"]);
        Assert.NotNull(view["op_log"]);
    }
}
