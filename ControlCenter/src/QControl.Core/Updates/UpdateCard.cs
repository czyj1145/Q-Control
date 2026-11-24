namespace QControl.Core.Updates;

/// <summary>「有新版本」这一态的子状态。</summary>
public enum UpdateCardStage
{
    /// <summary>还没下载（正常的新版本提示）。</summary>
    Newer,

    Downloading,
    Cancelling,
    Verifying,

    /// <summary>包已下载完、校验过，等用户点安装。</summary>
    Ready,

    /// <summary>安装向导已拉起（按钮变成"再次打开"）。</summary>
    Installing,

    DownloadFailed,
}

/// <summary>
/// 「关于 › 检查更新」卡片的**判定**部分：这份下载属不属于当前新版本、停在哪个子状态、
/// 发布附件里该拿哪个 exe、就绪态的悬停详情怎么拼。文案正文仍留在界面层。
///
/// ⚠️ 判定"这份下载是不是属于当前这个新版本"必须比 **tag** 而不是版本号：
///    用户可能先下过 v0.2.0，之后更新源又发了 v0.3.0 —— 拿旧包去装就是把版本装回去。
/// ⚠️ 比 tag 时忽略大小写（Gitee 的 tag 大小写不一定与 GitHub 一致）。
/// </summary>
public static class UpdateCard
{
    /// <summary>下载记录是不是针对 `latestTag` 这一版。tag 缺失一律视为"不是"。</summary>
    public static bool SameRelease(string? downloadVersion, string? latestTag)
        => !string.IsNullOrWhiteSpace(latestTag)
           && string.Equals(downloadVersion, latestTag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 子状态。`sameRelease` 为 false（下载记录属于别的版本）时一律回到「还没下」——
    /// 未知的 state 字符串同理（后端以后加了状态也不至于把卡片卡住）。
    /// </summary>
    public static UpdateCardStage Stage(bool sameRelease, string? downloadState)
    {
        if (!sameRelease) return UpdateCardStage.Newer;
        return downloadState switch
        {
            "downloading" => UpdateCardStage.Downloading,
            "cancelling" => UpdateCardStage.Cancelling,
            "verifying" => UpdateCardStage.Verifying,
            "ready" => UpdateCardStage.Ready,
            "installing" => UpdateCardStage.Installing,
            "error" => UpdateCardStage.DownloadFailed,
            _ => UpdateCardStage.Newer,
        };
    }

    /// <summary>
    /// 发布附件里该拿哪个 exe：**优先名字带 setup 的**（与后端选包规则一致），
    /// 否则取第一个 .exe；没有 .exe 返回 -1。非 exe（zip / SHA256.txt）一律跳过。
    /// </summary>
    public static int PickInstallerIndex(IReadOnlyList<string?> names)
    {
        var firstExe = -1;
        for (var i = 0; i < names.Count; i++)
        {
            var n = names[i];
            if (string.IsNullOrEmpty(n)) continue;
            if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.Contains("setup", StringComparison.OrdinalIgnoreCase)) return i;
            if (firstExe < 0) firstExe = i;
        }
        return firstExe;
    }

    /// <summary>
    /// 就绪态的悬停详情：装到哪 / 是不是安装版 / 包在哪 / 校验和。
    /// 一条都不成立时退回"发布 &lt;tag&gt;"，**不留空悬停**。
    /// </summary>
    public static string ReadyTip(
        string? target, bool portable, string? file, string? sha256, string? tag, string? version)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(target)) lines.Add("安装到：" + target);
        if (portable)
            lines.Add("当前这份不是安装版：装完会在上面的目录多出一份，当前位置的文件不会被动");
        if (!string.IsNullOrEmpty(file)) lines.Add("安装包：" + file);
        if (!string.IsNullOrEmpty(sha256)) lines.Add("SHA256：" + sha256);
        if (lines.Count == 0) lines.Add("发布 " + (tag ?? version ?? ""));
        return string.Join("\n", lines);
    }
}
