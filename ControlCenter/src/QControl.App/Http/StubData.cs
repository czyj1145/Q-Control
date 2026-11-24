using System.Collections.Concurrent;

namespace QControl.App.Http;

/// <summary>
/// 桩数据源：把 `tests/fixtures/*.json` 原样回放。
///
/// 为什么是"回放真机样本"而不是"自己编一份"：
///   契约冻结的字面要求是**形状一致**。编出来的样本永远少几个字段，
///   而少字段的后果是 DTO 漏字段、界面整块不显示，且要到 P7 才发现。
///   这些 fixture 是 2026-10-18 从真机 `/api/status` 等端点直接抓的，
///   形状 100% 保真。
///
/// ⚠️ 桩就是桩，不冒充实时数据：
///   **回桩样本的响应**带 `X-CC-Stub: 1` 头（真数据回 `0`），值也是抓取那一刻的旧值。
///   默认端口 8765（P8 起 = 主端口，2026-10-24 起也是唯一后端）。
/// </summary>
internal sealed class StubData
{
    private readonly string _dir;
    // 多个连接各跑一个 HandleAsync Task：普通 Dictionary 并发写会损坏内部结构。
    // 纯桩模式下若某个样本没被启动横幅预热到，第一次并发 GET 就会撞上。
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    public StubData(string fixturesDir) => _dir = fixturesDir;

    /// <summary>读一份样本。缺失返回 false —— 由调用方报 500，不静默造空对象。</summary>
    public bool TryGet(string name, out byte[] body)
    {
        if (_cache.TryGetValue(name, out var cached))
        {
            body = cached;
            return true;
        }

        var path = Path.Combine(_dir, name + ".json");
        try
        {
            if (!File.Exists(path)) { body = []; return false; }
            body = File.ReadAllBytes(path);
            _cache[name] = body;
            return true;
        }
        catch (Exception e)
        {
            // 「样本缺失」和「磁盘/权限故障」不能长得一模一样：原先一律静默回 false，
            // 于是桩缺一份与文件读不出来在日志里都是「桩数据缺失」500，查不出根因。
            Console.Error.WriteLine($"[stub] 读不出 {path}：{e.GetType().Name}: {e.Message}");
            body = [];
            return false;
        }
    }

    /// <summary>启动自检：列出每个期望的样本是否就位。</summary>
    public IEnumerable<(string Name, bool Ok, int Bytes)> Report(IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            var ok = TryGet(n, out var b);
            yield return (n, ok, ok ? b.Length : 0);
        }
    }
}
