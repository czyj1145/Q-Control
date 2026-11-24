using System.IO.Compression;

namespace QControl.Setup;

/// <summary>安装进度的一步。</summary>
internal sealed record SetupProgress(int Percent, string Text);

/// <summary>安装器主动抛的"能讲清楚原因"的失败（界面上原样展示，不打堆栈）。</summary>
internal sealed class SetupException(string message) : Exception(message);

/// <summary>
/// 嵌入载荷（`payload.zip` + 首次安装用的 `model.json`）的读取与解包。
///
/// 载荷是**编译期**塞进程序集的（见 csproj 的 EmbeddedResource），所以安装包是
/// 一个文件、可以离线装、不需要旁边躺一堆散件。
/// </summary>
internal static class Payload
{
    public const string ZipResource = "QControl.Setup.payload.zip";
    public const string ModelResource = "QControl.Setup.model.json";

    private static long _sizeCache;

    /// <summary>载荷在不在（没有就说明打包脚本没跑，属于打包错误）。</summary>
    public static bool HasPayload
        => typeof(Payload).Assembly.GetManifestResourceNames().Contains(ZipResource);

    /// <summary>首次安装种子配置。payload 里**故意不含** model.json（见 Installer 注释）。</summary>
    public static string? ReadModelBytes()
    {
        using var s = typeof(Payload).Assembly.GetManifestResourceStream(ModelResource);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>
    /// 载荷里所有文件的相对路径（统一成正斜杠）。
    ///
    /// 用途只有一个：升级安装时判断"这个文件是不是本版该有的"。清单外的就是上一版残留
    /// （老布局把 240 多个 dll 平铺在安装目录根，不清掉的话新布局等于白改）。
    /// 拿不到就返回空集合 —— 调用方会因此跳过清理，而不是把整个安装目录清空。
    /// </summary>
    public static HashSet<string> EntryRelativePaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = OpenArchive(out var cleanup);
            try
            {
                foreach (var entry in zip.Entries)
                    if (entry.Name.Length > 0) set.Add(entry.FullName.Replace('\\', '/'));
            }
            finally
            {
                cleanup();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取载荷清单失败：" + ex.Message);
            set.Clear();
        }
        return set;
    }

    /// <summary>解包后的总字节数（界面上"需要约 xx MB"用）。读不到返回 0。</summary>
    public static long UncompressedSize()
    {
        if (_sizeCache > 0) return _sizeCache;
        try
        {
            using var zip = OpenArchive(out var cleanup);
            try
            {
                _sizeCache = zip.Entries.Sum(e => e.Length);
            }
            finally
            {
                cleanup();
            }
        }
        catch
        {
            _sizeCache = 0;
        }
        return _sizeCache;
    }

    /// <summary>
    /// 解包到 <paramref name="destDir"/>（保留子目录结构）。
    ///
    /// 逐条解而不是 `ZipFile.ExtractToDirectory`：① 要报进度；② 要挡住 zip-slip
    /// （条目名里写 `..\` 就能往安装目录外面写文件，解压器的经典老洞）。
    /// </summary>
    public static void ExtractTo(string destDir, IProgress<SetupProgress>? progress,
                                 int fromPercent, int toPercent, CancellationToken ct)
    {
        var root = Path.GetFullPath(destDir);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);

        using var zip = OpenArchive(out var cleanup);
        try
        {
            var total = Math.Max(1, zip.Entries.Count);
            var done = 0;

            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();

                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException("安装包内容异常（条目越出安装目录）：" + entry.FullName);

                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);      // 目录条目
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }

                done++;
                if (progress is not null && (done % 8 == 0 || done == total))
                {
                    var pct = fromPercent + (int)((long)(toPercent - fromPercent) * done / total);
                    progress.Report(new SetupProgress(pct, $"正在解压程序文件… {done}/{total}"));
                }
            }
        }
        finally
        {
            cleanup();
        }
    }

    /// <summary>
    /// 打开 zip。<c>out</c> 里的动作负责清理（资源流不可定位时要先落临时文件：
    /// 单文件发布下资源流可能不可 seek）。
    /// </summary>
    private static ZipArchive OpenArchive(out Action cleanup)
    {
        var stream = typeof(Payload).Assembly.GetManifestResourceStream(ZipResource)
            ?? throw new SetupException("安装包里没有程序文件（payload.zip）。这是打包错误，请重新获取安装包。");

        if (!stream.CanSeek)
        {
            var temp = Path.Combine(Path.GetTempPath(), "qcontrol-payload-" + Guid.NewGuid().ToString("N") + ".zip");
            using (stream)
            using (var file = File.Create(temp))
                stream.CopyTo(file);

            var read = File.OpenRead(temp);
            var archive = new ZipArchive(read, ZipArchiveMode.Read);
            cleanup = () =>
            {
                archive.Dispose();
                try { File.Delete(temp); } catch { /* 下次启动会被临时目录清理 */ }
            };
            return archive;
        }

        var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        cleanup = () =>
        {
            zip.Dispose();
            stream.Dispose();
        };
        return zip;
    }
}
