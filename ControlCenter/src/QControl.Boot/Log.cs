using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QControl.Boot;

/// <summary>
/// 极简日志。位置**按可用性挑**：
///   ① `&lt;exe 目录&gt;\logs\boot.log` —— 绿色版最想要的位置：用户自己就能找到，
///      出问题时直接把整个文件夹发过来；
///   ② `%TEMP%\QControl.Boot.log` —— 解压到只读位置（U 盘、Program Files）时的兜底。
///
/// ⚠️ 2026-10-29 的教训：一开始只会写 ①=没有、只写 `%TEMP%`，结果在某些启动方式下
///    `Path.GetTempPath()` 指向一个不可写的位置，于是**日志和自检结果全部静默丢失**，
///    排查时只看得到"程序跑了、什么都没留下"。所以现在先探测可写性再定位置，
///    并且把两个候选路径都记进自检报告。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    /// <summary>最终选定的日志文件（初始化失败时是 `&lt;无可用目录&gt;`）。</summary>
    public static string LogPath { get; private set; } = "<未初始化>";

    /// <summary>选位置：exe 目录优先，其次 TEMP。返回选中的路径。</summary>
    public static string Init()
    {
        var candidates = new List<string>();

        try
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(exeDir)) candidates.Add(Path.Combine(exeDir, "logs"));
        }
        catch
        {
        }

        try
        {
            var temp = Path.GetTempPath();
            if (!string.IsNullOrEmpty(temp)) candidates.Add(temp);
        }
        catch
        {
        }

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);

                // 真写一次才算数：目录存在不等于可写（只读盘、权限、杀软都会让写入失败）
                var probe = Path.Combine(dir, ".qcontrol-write-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);

                LogPath = Path.Combine(dir, "boot.log");
                return LogPath;
            }
            catch
            {
                // 换下一个候选
            }
        }

        LogPath = "<无可用目录>";
        return LogPath;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message) => Write("ERR ", message);

    public static void Error(string message, Exception ex) => Write("ERR ", message + " :: " + ex);

    private static void Write(string level, string message)
    {
        if (LogPath.StartsWith("<", StringComparison.Ordinal)) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate) File.AppendAllText(LogPath, line, new UTF8Encoding(false));
        }
        catch
        {
            // 日志写不进去不该让引导器跟着失败
        }
    }

    /// <summary>
    /// 在日志旁写一份完整报告（自检用）。返回值是实际写成功的路径，失败返回 null。
    /// </summary>
    public static string? WriteSibling(string fileName, IEnumerable<string> lines)
    {
        if (LogPath.StartsWith("<", StringComparison.Ordinal)) return null;

        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (string.IsNullOrEmpty(dir)) return null;

            var path = Path.Combine(dir, fileName);
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清掉上一次的日志（每次启动重来，避免几周后变成几 MB）。</summary>
    public static void Reset()
    {
        if (LogPath.StartsWith("<", StringComparison.Ordinal)) return;

        try
        {
            lock (Gate) if (File.Exists(LogPath)) File.Delete(LogPath);
        }
        catch
        {
        }
    }
}
