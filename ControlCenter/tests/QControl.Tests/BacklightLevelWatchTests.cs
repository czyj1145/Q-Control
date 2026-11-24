using QControl.Core.Devices;

namespace QControl.Tests;

/// <summary>
/// 「EC 亮度变化算不算用户外部改的」判定（切档 / 接管切换时的亮度跳动防线）。
///
/// 真机事故（2026-11-19 13:41）：开「自定义接管」重放 50%，EC 里还挂着狂暴档的 100%；
/// 老实现拿「档案 1 ≠ EC 2」就写盘，把自定义档改成 100%，以后每次开接管亮度都跳。
/// 现在只有**相对上一拍真的变了**、且不在我们自己写 EC 的静默期内，才认。
/// </summary>
public class BacklightLevelWatchTests
{
    [Fact]
    public void FirstSampleOnlyBuildsBaseline()
    {
        Assert.False(BacklightLevelWatch.IsExternalChange(null, 2, null));
        Assert.False(BacklightLevelWatch.IsExternalChange(null, 0, null));
    }

    [Fact]
    public void UnchangedValueIsResidueNotUserIntent()
    {
        // 切档后 EC 一直挂着旧档的 100%，custom 档案是 50% —— 不能写盘
        Assert.False(BacklightLevelWatch.IsExternalChange(2, 2, null));
        Assert.False(BacklightLevelWatch.IsExternalChange(1, 1, 30_000));
    }

    [Fact]
    public void RealChangeWithoutOurWriteIsAdopted()
    {
        Assert.True(BacklightLevelWatch.IsExternalChange(1, 2, null));     // 从没写过 EC
        Assert.True(BacklightLevelWatch.IsExternalChange(2, 1, 10_000));   // 早就写完了
    }

    [Fact]
    public void ChangeInsideOurWriteQuietWindowIsIgnored()
    {
        Assert.False(BacklightLevelWatch.IsExternalChange(1, 2, 0));
        Assert.False(BacklightLevelWatch.IsExternalChange(1, 2, 200));
        Assert.False(BacklightLevelWatch.IsExternalChange(1, 2, BacklightLevelWatch.QuietMs - 1));
    }

    [Fact]
    public void QuietWindowBoundaryIsAdopted()
    {
        Assert.True(BacklightLevelWatch.IsExternalChange(1, 2, BacklightLevelWatch.QuietMs));
        Assert.True(BacklightLevelWatch.IsExternalChange(1, 2, BacklightLevelWatch.QuietMs + 1));
    }

    [Fact]
    public void ExternalTurnOffIsStillAChange_CallerFiltersZero()
    {
        // 0 的过滤在调用方做（关灯是临时动作，不是亮度偏好）；判定层只认"变了"
        Assert.True(BacklightLevelWatch.IsExternalChange(2, 0, null));
    }
}
