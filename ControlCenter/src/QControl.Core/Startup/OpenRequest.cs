namespace QControl.Core.Startup;

/// <summary>
/// 「请下一个实例把界面打开」的一次性请求 —— 让**任务代启**也能带上一句人话。
///
/// 为什么需要它：用 `schtasks`/COM 触发计划任务**传不了参数**（任务的 Arguments 是
/// 固定字符串，启动链里写死的是 `--tray --port 8765`）。于是「用户点徽章要换管理员
/// 进程」这件事故不上——新进程照样只进托盘，用户看到的是"窗口没了"。
/// 用一张小纸条传话：发起方写（要开在哪一页），任务起的进程读一次就删。
///
/// 三条设计约束：
///   · **过期即作废**（默认 120 秒）：上次写了但新进程没起来（用户点了取消 / 任务被
///     禁用），纸条不能留到下次开机把窗口莫名其妙弹出来；
///   · **读走即删**：一次请求只兑现一次，重复读不会重复开窗；
///   · 放在 `%LOCALAPPDATA%`，不碰安装目录（那里可能只读 / 被 UAC 虚拟化）。
///
/// ⚠️ 失败一律静默（返回 false）：这是"锦上添花"的提示，写不了最多是窗口不自动开，
///    绝不能因为一张纸条让整个提权流程失败。
/// </summary>
public static class OpenRequest
{
    /// <summary>超过这个秒数的请求视为过期（任务代启通常 1~3 秒完成，120 秒足够宽）。</summary>
    private const int MaxAgeSec = 120;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QControl", "open.req");

    /// <summary>写一张纸条。`page` 为 null = 用默认页。</summary>
    public static bool Write(string? page)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 第一行页码（可空），第二行 UTC 秒。用两行纯文本，出问题能直接打开看。
            var body = (page ?? "") + "\n"
                       + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "\n";
            File.WriteAllText(FilePath, body);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读走并删除。`true` = 这是一张**新鲜**的请求（该开窗），`page` 可能为 null（默认页）。
    /// 过期 / 格式坏 / 读不了都返回 false，并且顺手把文件清掉（不留垃圾）。
    /// </summary>
    public static bool TryTake(out string? page)
    {
        page = null;
        try
        {
            if (!File.Exists(FilePath)) return false;

            var lines = File.ReadAllLines(FilePath);
            TryDelete();                                     // 读走即删：一次请求只兑现一次

            if (lines.Length < 2) return false;
            if (!long.TryParse(lines[1].Trim(), out var stamp)) return false;

            var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp;
            if (age < 0 || age > MaxAgeSec) return false;      // 时钟跳变 / 过期

            var p = lines[0].Trim();
            if (p.Length > 0) page = p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>丢弃未兑现的请求（发起方在启动任务失败时要回滚，别留下"下次启动莫名开窗"）。</summary>
    public static void Discard() => TryDelete();

    private static void TryDelete()
    {
        try { File.Delete(FilePath); } catch { /* 删不掉也没关系，下次会因过期被忽略 */ }
    }
}
