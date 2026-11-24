namespace QControl.Core.Logs;

/// <summary>
/// 一键采集写出来的**采集包**（`logs\adopt-*.json` + 同名 `.txt`）的枚举与清理。
///
/// 为什么要单独管这一类文件：它们躺在 `logs\` 里，却**不是日志** ——
/// <c>FileLog.DirStats</c> / <c>FileLog.ClearLogs</c> 只认 `cc-*.log` /
/// `cc-*.old.log` / `capture-*.log` 三类，于是"占用空间"报 0 B、点「清理」也一个不动，
/// 而每采一次就多一对（几十 KB），越积越多 —— 用户看到的就是"清理清不掉采集档案"。
///
/// ⚠️ 与 <c>UserProfileStore</c> / <c>FileLog.ClearLogs</c> 同一条纪律：
///    **只删自己写的那一类文件**（顶层、名字以 `adopt-` 开头、扩展名 json/txt），
///    绝不递归、绝不删子目录、绝不碰别的扩展名。删除不可逆，宁可少删。
/// ⚠️ 采集包是**要发给作者的交付物**（`--import-pack` 吃它），删了就只能重新采一次 ——
///    所以它和机型档案一样是"可选清理"，绝不能跟着"清个日志"顺手带走。
/// </summary>
public static class AdoptPackStore
{
    /// <summary>文件名前缀（见 <c>MachineReport.MakeStem</c>：`adopt-&lt;机型&gt;-&lt;时间&gt;`）。</summary>
    public const string Prefix = "adopt-";

    /// <summary>目录扫描结果（<see cref="Names"/> 已排序，供确认框逐个列出来）。</summary>
    public readonly record struct Stats(int Files, long Bytes, IReadOnlyList<string> Names)
    {
        public static Stats Empty => new(0, 0, Array.Empty<string>());
    }

    /// <summary>统计目录顶层的采集包。目录不存在 / 读不动一律当"没有"。</summary>
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
                if (!IsPack(f)) continue;
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
    /// 删掉目录顶层的采集包。返回（删掉几个、释放多少字节、几个没删掉）。
    /// 没删掉通常是被别的程序独占（编辑器 / 杀毒），照实回 <c>Failed</c>，不假装删了。
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
                if (!IsPack(f)) continue;

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

    /// <summary>我们自己写的那类文件：顶层 `adopt-*.json` / `adopt-*.txt`。</summary>
    public static bool IsPack(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(name);
        return ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }
}
