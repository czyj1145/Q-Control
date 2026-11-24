using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QControl.Core.Model;

/// <summary>一次配置操作的结果（形状与 HTTP 契约一致：`{ok, ...}` / `{ok, error}`）。</summary>
public sealed record SetResult(bool Ok, string? Error, JsonObject? Presets)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["ok"] = Ok };
        if (Ok) o["presets"] = Presets?.DeepClone();
        else o["error"] = Error ?? "未知错误";
        return o;
    }

    public static SetResult Fail(string error) => new(false, error, null);
    public static SetResult Success(JsonObject presets) => new(true, null, presets);
}

/// <summary>一条 EC 写操作记录。</summary>
public sealed record OpRecord(
    double Ts, string Cmd, string Action, JsonNode? Detail,
    bool Ok, bool Mock, string? Risk);

/// <summary>
/// `model.json` 的读写与配置树操作（取值 / 合并 / 落盘 / 原子写）。
///
/// 这里集中了几个踩过大坑的规矩，改动前必须读懂：
/// <list type="number">
/// <item><b>坏文件绝不覆盖。</b> 界面鼓励用记事本直接编辑 model.json（保存即热生效），
///   一个手滑的尾逗号就会把用户所有配置冲成默认值 —— 那是不可逆的数据丢失。
///   解析失败 ⇒ 保住内存里上一份、**拒绝写盘**、把坏文件另存 <c>.broken</c>。</item>
/// <item><b>原子写。</b> 先写唯一命名的 tmp，再 <c>Move(overwrite)</c> 换上。
///   直接截断写会让并发读到的空文件被判成"配置损坏"。</item>
/// <item><b>只在首次运行落盘。</b> 「启动一下看看」不该重写用户配置。</item>
/// </list>
///
/// 关于总闸同步：<c>Device</c> 是不可变的，上层每次用 <see cref="EcEnabled"/> 现造即可，
/// 因此这里一个同步动作都不需要 —— "缓存没跟着总闸走"这类坑从根上不存在。
/// </summary>
public sealed class ModelStore
{
    private const int OpLogMax = 200;

    private readonly object _lock = new();
    private readonly Func<JsonObject> _probeFactorySpecs;
    private readonly Action<OpRecord>? _writeLogSink;

    private JsonObject _presets = new();
    private JsonObject _factorySpecs = new();
    private List<OpRecord> _opLog = [];
    private double _mtime;
    private bool _brokenGuard;

    public string Path { get; }
    /// <summary>model.json 解析失败的原因（界面据此提示"你的编辑没被采用，也没被覆盖"）。</summary>
    public string? LoadError { get; private set; }

    /// <summary>
    /// 最近一次载入时把老位置的档位档案搬进 `perf.modes[档]` 挪了多少项
    /// （启动日志用：用户看到自己手改的 `mode_curves` 消失了，日志里得有交代）。
    /// </summary>
    public ModeMigration LastMigration { get; private set; }
    /// <summary>
    /// 早先实现 fan_curve.json 的路径（首次运行迁移用，可空）。
    /// ⚠️ 必须是**构造参数**：Load() 在构造函数里就跑，用对象初始化器赋值
    ///    会赶不上迁移时机。
    /// </summary>
    public string? LegacyCurvePath { get; }

    public ModelStore(string path,
                      string? legacyCurvePath = null,
                      Func<JsonObject>? probeFactorySpecs = null,
                      Action<OpRecord>? writeLogSink = null)
    {
        Path = path;
        LegacyCurvePath = legacyCurvePath;
        // ⚠️ 不能写成 `probeFactorySpecs ?? (() => ...)`：方法组/lambda 与 Func 之间
        //    不能靠 ?? 推断类型，编译器会报 CS0019。用具名方法最省事。
        _probeFactorySpecs = probeFactorySpecs ?? DefaultProbe;
        _writeLogSink = writeLogSink;
        Load();
    }

    private static JsonObject DefaultProbe()
        => (JsonObject)PresetDefaults.FactoryFallback.DeepClone();

    public object SyncRoot => _lock;

    /// <summary>当前配置树（**可变**：改它请持 <see cref="SyncRoot"/> 并随后 <see cref="Save"/>）。</summary>
    public JsonObject Presets => _presets;

    public JsonObject FactorySpecs => _factorySpecs;

    public IReadOnlyList<OpRecord> OpLog => _opLog;

    /// <summary>
    /// 用户 EC 总闸的三态：null = 没选过（跟随档案）、true/false = 明确选择。
    /// 上层（App）每次造 <c>Device</c> 时读它当 <c>ecOverride</c>。
    /// </summary>
    public bool? EcEnabled
    {
        get
        {
            try
            {
                if (_presets["ec"] is not JsonObject ec) return null;
                var v = ec["enabled"];
                if (v is null) return null;
                return Devices.Device.Truthy(v);
            }
            catch
            {
                return null;    // presets 被手工改成非对象：按"没选过"处理
            }
        }
    }

    // ================================================================ 读

    /// <summary>
    /// 载入配置。失败的三种情况必须分清：
    /// 文件不存在（首次运行 ⇒ 用默认值并落盘）、文件坏（⇒ 保住内存、拒写盘）、
    /// 文件好（⇒ 默认值 + 文件内容深合并）。
    /// </summary>
    public ModelStore Load()
    {
        var exists = File.Exists(Path);
        JsonObject? data = null;
        string? broken = null;

        if (exists)
        {
            try
            {
                var text = File.ReadAllText(Path, Encoding.UTF8);
                if (JsonNode.Parse(text) is not JsonObject obj)
                    throw new InvalidDataException("顶层不是 JSON 对象");

                // 类型错误（合法 JSON、错误类型，如 "presets": [...]）必须走损坏分支：
                // 否则后面的 DeepMerge 会对非对象抛异常 —— 冷启动直接闪退。
                foreach (var key in (string[])["presets", "factory_specs"])
                {
                    var v = obj[key];
                    if (v is not null and not JsonObject)
                        throw new InvalidDataException($"{key} 不是 JSON 对象（是 {v.GetValueKind()}）");
                }

                data = obj;
                _mtime = LastWrite();
            }
            catch (Exception e)
            {
                broken = e.Message;
            }
        }

        if (broken is not null)
        {
            lock (_lock)
            {
                LoadError = broken;
                _brokenGuard = true;        // 用户改好之前拒绝任何 Save()
                try { _mtime = LastWrite(); } catch { }

                // ⚠️ 只在坏文件**非空**时才覆盖 .broken —— 空文件往往是别的进程
                //    正在原子替换的瞬间被读到，拿它盖掉上一次真实损坏现场等于毁掉取证材料。
                try
                {
                    if (new FileInfo(Path).Length > 0)
                        File.Copy(Path, Path + ".broken", overwrite: true);
                }
                catch { /* 备份失败不影响主流程 */ }

                if (_factorySpecs.Count == 0) _factorySpecs = SafeProbe();
                // 只有"内存里本来就没有配置"时才真正用默认值兜底（且不写盘）
                if (_presets.Count == 0) _presets = PresetDefaults.CloneDefaults();
            }
            return this;
        }

        lock (_lock)
        {
            LoadError = null;
            _brokenGuard = false;           // 加载成功：解除拒写保护
            data ??= new JsonObject();
            var fresh = !exists;

            // ⚠️ 必须先深拷贝再合并：直接拿 PresetDefaults.Defaults 去合并会**污染全局默认值**
            // ⚠️ 而且整棵树必须先在**局部变量**上建好，最后一次性发布：
            //    采样线程/HTTP 线程无锁读 `_presets`，若先把它指向只含出厂默认的新树、
            //    再慢慢 DeepMerge，中间那一拍会读到 `curve_enabled=false` / 空曲线，
            //    风扇当场按默认值算一次占空比（"曲线突然变默认"）。
            var specs = data["factory_specs"] as JsonObject ?? new JsonObject();
            var merged = PresetDefaults.CloneDefaults();
            if (data["presets"] is JsonObject p) PresetDefaults.DeepMerge(merged, p);

            if (fresh) MigrateLegacyCurve(merged);
            if (specs.Count == 0) specs = SafeProbe();

            // 档位档案归位（2026-11-17）：`fan.mode_curves*` / `backlight.mode_profiles`
            // 搬进 `perf.modes[档].{fan,light}`，搬完删老键。只在真搬了才落盘 ——
            // 「启动一下看看」仍然不重写配置（与 fresh 那条规矩同一理由）。
            LastMigration = ModeBundle.Migrate(merged);

            // 唯一发布点：合并 + 迁移全部完成后才换树
            _factorySpecs = specs;
            _presets = merged;

            // ⚠️ 只在**文件本来不存在**时才落盘。「启动一下看看」不该重写用户配置：
            //    两个进程同时跑时，一方 save 的截断瞬间被另一方读到 ⇒ 空文件 ⇒ 判损坏。
            if (fresh || LastMigration.Changed) Save();
        }
        return this;
    }

    private JsonObject SafeProbe()
    {
        try { return _probeFactorySpecs(); }
        catch { return (JsonObject)PresetDefaults.FactoryFallback.DeepClone(); }
    }

    private double LastWrite() => new FileInfo(Path).LastWriteTimeUtc
        .Subtract(DateTime.UnixEpoch).TotalSeconds;

    /// <summary>
    /// 首次运行时把早先实现 `fan_curve.json` 的曲线迁进来，不让用户重画。
    /// </summary>
    private bool MigrateLegacyCurve(JsonObject presets)
    {
        if (string.IsNullOrEmpty(LegacyCurvePath) || !File.Exists(LegacyCurvePath)) return false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(LegacyCurvePath, Encoding.UTF8)) is not JsonObject old)
                return false;

            if (presets["fan"] is not JsonObject fan) return false;
            if (old["curve"] is JsonArray curve && curve.Count > 0)
                fan["curve"] = curve.DeepClone();

            var src = old["source"]?.GetValue<string>();
            if (src is "cpu" or "gpu" or "max")
                fan["source"] = src;

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================ 写

    /// <summary>外部（记事本）改了 model.json 就热加载，实现「Ctrl+S 即生效」。</summary>
    public bool ReloadIfChanged()
    {
        double m;
        try { m = LastWrite(); }
        catch { return false; }

        if (Math.Abs(m - _mtime) < double.Epsilon) return false;
        try { Load(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// 原子写 + 并发安全。
    /// <paramref name="force"/> = true 绕过「坏文件拒绝写盘」保护，**只给恢复出厂用**
    /// （那是用户明确的覆盖动作）。
    /// </summary>
    public JsonObject? Save(bool force = false)
    {
        if (_brokenGuard && !force)
            return null;        // 不写盘、不抛：调用方照常继续，界面横幅已告知用户

        lock (_lock)
        {
            var data = new JsonObject
            {
                ["factory_specs"] = _factorySpecs.DeepClone(),
                ["presets"] = _presets.DeepClone(),
                ["saved_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            };

            // 唯一 tmp 名：多线程/多进程同时写不会撞车（早先实现踩过）
            var tmp = $"{Path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
            try
            {
                var text = data.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    w.Write(text);
                    w.Flush();
                    fs.Flush(flushToDisk: true);     // fsync 落盘，防掉电丢配置
                }
                // Move(overwrite) 在 Windows 上走 MoveFileEx + REPLACE_EXISTING，是原子的：
                // 读到的要么是旧内容、要么是新内容，绝不会是半截。
                File.Move(tmp, Path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            try { _mtime = LastWrite(); } catch { }
            return data;
        }
    }

    // ================================================================ 配置树

    /// <summary>`Get("fan", "min_duty")` —— 路径断了返回 <paramref name="fallback"/>。</summary>
    public JsonNode? Get(JsonNode? fallback, params string[] path)
    {
        JsonNode? cur = _presets;
        foreach (var k in path)
        {
            if (cur is not JsonObject o || !o.TryGetPropertyValue(k, out var next))
                return fallback;
            cur = next;
        }
        return cur ?? fallback;
    }

    public JsonNode? Get(params string[] path) => Get(null, path);

    /// <summary>
    /// `Set(["fan","min_duty"], 35)` —— 中间层不存在就建，遇到非对象则报错。
    ///
    /// **一级路径 = 按节合并**：`Set(["light"], {…})` 把给出的键逐个写进该节，
    /// 一次落盘定下整节的几个键（`POST /api/model/preset` 靠这个原子地写
    /// `light.auto_off_min` + `light.auto_off_on_lock`）。
    /// ⚠️ 是**合并**不是整节替换：调用方只带自己关心的键，整节换掉会把没提到的键
    /// 一起抹掉 —— `presets.fan.curve` 被抹就等于曲线消失、风扇停在最后一个值。
    /// </summary>
    public SetResult Set(IReadOnlyList<string> path, JsonNode? value)
    {
        if (path.Count == 0) return SetResult.Fail("参数不足");

        lock (_lock)
        {
            if (path.Count == 1)
            {
                var section = path[0];
                if (!PresetDefaults.HasSection(section))
                    return SetResult.Fail($"未知配置节：{section}");
                if (value is not JsonObject patch)
                    return SetResult.Fail($"写 {section} 节时值必须是对象");

                // 该节在文件里被手改成了标量（`"light": 1`）⇒ 没有可合并的目标，就地重建
                if (_presets[section] is not JsonObject target)
                {
                    target = new JsonObject();
                    _presets[section] = target;
                }

                foreach (var kv in patch) target[kv.Key] = kv.Value?.DeepClone();

                // Save() 返回 null = 坏文件拒写保护生效（一个字节都没写）
                // ⇒ 绝不能回 ok，否则界面/接口说"保存成功"而重启后全丢。
                if (Save() is null)
                    return SetResult.Fail("model.json 解析失败，已拒绝覆盖；本次改动没有落盘（请先修好配置文件）");
                return SetResult.Success(_presets);
            }

            JsonNode cur = _presets;
            for (var i = 0; i < path.Count - 1; i++)
            {
                var k = path[i];
                if (cur is not JsonObject obj) return SetResult.Fail($"路径 {k} 不是对象");

                if (obj[k] is JsonObject child)
                {
                    cur = child;
                }
                else if (obj[k] is null)
                {
                    var created = new JsonObject();
                    obj[k] = created;
                    cur = created;
                }
                else
                {
                    // 已经是标量了 —— 不能悄悄覆盖成对象（那会丢掉用户的值）
                    return SetResult.Fail($"路径 {k} 不是对象");
                }
            }

            if (cur is not JsonObject last) return SetResult.Fail($"路径 {path[^1]} 的父级不是对象");
            last[path[^1]] = value?.DeepClone();

            if (Save() is null)
                return SetResult.Fail("model.json 解析失败，已拒绝覆盖；本次改动没有落盘（请先修好配置文件）");
            return SetResult.Success(_presets);
        }
    }

    /// <summary>恢复出厂：presets 回默认、factory_specs 重探（force 覆盖坏文件）。</summary>
    public JsonObject Reset()
    {
        lock (_lock)
        {
            _presets = PresetDefaults.CloneDefaults();
            _factorySpecs = SafeProbe();
            Save(force: true);
            return new JsonObject
            {
                ["ok"] = true,
                ["presets"] = _presets.DeepClone(),
                ["factory_specs"] = _factorySpecs.DeepClone(),
            };
        }
    }

    /// <summary>只恢复某一节（如 fan）。未知节名要**报错**，不能静默当成功。</summary>
    public JsonObject ResetSection(string section)
    {
        if (!PresetDefaults.HasSection(section))
            return new JsonObject { ["ok"] = false, ["error"] = $"未知配置节：{section}" };

        lock (_lock)
        {
            _presets[section] = PresetDefaults.Defaults[section]!.DeepClone();
            Save(force: true);
            return new JsonObject
            {
                ["ok"] = true,
                ["section"] = section,
                ["value"] = _presets[section]?.DeepClone(),
            };
        }
    }

    // ================================================================ 操作日志

    /// <summary>
    /// 记一条 EC 写操作。同时打内存（最近 200 条）与落盘 sink ——
    /// 进程没了也能回溯"到底谁在什么时候写了 EC"。
    /// </summary>
    public OpRecord LogOp(string cmdText, string action, JsonNode? detail = null,
                          bool ok = true, bool mock = false, ushort? cmd = null)
    {
        var rec = new OpRecord(
            Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            Cmd: cmdText,
            Action: action,
            Detail: detail?.DeepClone(),
            Ok: ok,
            Mock: mock,
            Risk: cmd is { } c ? CmdTable.Risk(c) : null);

        lock (_lock)
        {
            _opLog.Add(rec);
            if (_opLog.Count > OpLogMax)
                _opLog = _opLog.Skip(_opLog.Count - OpLogMax).ToList();
        }

        try { _writeLogSink?.Invoke(rec); } catch { /* 落盘失败不能影响主流程 */ }
        return rec;
    }

    /// <summary>便利重载：命令号写成 0xNNNN。</summary>
    public OpRecord LogOp(ushort cmd, string action, JsonNode? detail = null,
                          bool ok = true, bool mock = false)
        => LogOp(CmdTable.Hex(cmd), action, detail, ok, mock, cmd);

    // ================================================================ 视图

    /// <summary>
    /// `/api/model` 的响应体。
    /// <paramref name="deviceSummary"/> 由上层传入（Core 不认识当前机型是怎么识别出来的）。
    /// </summary>
    public JsonObject View(JsonObject deviceSummary)
    {
        lock (_lock)
        {
            var limits = new JsonObject();
            foreach (var (k, lim) in Bounds.All.OrderBy(x => x.Key, StringComparer.Ordinal))
                limits[k] = new JsonObject { ["min"] = lim.Lo, ["max"] = lim.Hi, ["desc"] = lim.Desc };

            var log = new JsonArray();
            foreach (var r in _opLog.Skip(Math.Max(0, _opLog.Count - 50)))
            {
                log.Add(new JsonObject
                {
                    ["ts"] = r.Ts,
                    ["cmd"] = r.Cmd,
                    ["action"] = r.Action,
                    ["detail"] = r.Detail?.DeepClone(),
                    ["ok"] = r.Ok,
                    ["mock"] = r.Mock,
                    ["risk"] = r.Risk,
                });
            }

            return new JsonObject
            {
                ["factory_specs"] = _factorySpecs.DeepClone(),
                ["presets"] = _presets.DeepClone(),
                ["device"] = deviceSummary.DeepClone(),
                ["limits"] = limits,
                ["path"] = Path,
                ["load_error"] = LoadError,
                ["op_log"] = log,
            };
        }
    }
}
