using QControl.Core.Window;

namespace QControl.Tests;

/// <summary>
/// 拖动最大化窗口的还原定位。挡的是三类"肉眼才能发现"的事故：
///   · 抓取点比例丢了 —— 抓在标题中间，窗口却跳到光标左上角（手势直接废掉）；
///   · 没夹工作区 —— 光标贴近屏幕右/下边缘时，窗口一半被甩出屏幕，且因为
///     无边框 + 我们的 WM_GETMINMAXINFO 守卫，露出的部分没有任何可抓的边；
///   · 窗口比工作区宽 —— 夹取区间翻转成负数，Clamp 直接抛 ArgumentOutOfRange。
/// </summary>
public class RestoreDragTests
{
    // 本机实测：2560×1600 @150% ⇒ 工作区 1707×1019（任务栏 48 在底部）
    private const double W = 1707, H = 1019;

    [Fact]
    public void 抓在正中间_还原后光标还在中间()
    {
        var (left, _) = RestoreDrag.RestoreTopLeft(
            grabX: W / 2, grabWidth: W, cursorX: 855, cursorY: 500,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: W, workBottom: H);
        Assert.Equal(855 - 660, left, precision: 6);
    }

    [Fact]
    public void 抓取比例原样保留_四分之一处()
    {
        var (left, _) = RestoreDrag.RestoreTopLeft(
            grabX: W * 0.25, grabWidth: W, cursorX: 500, cursorY: 300,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: W, workBottom: H);
        // 500 - 1320×0.25 = 170
        Assert.Equal(170, left, precision: 6);
    }

    [Fact]
    public void 顶边留出抓取高度()
    {
        // cursorY 取 100：窗口高 880，工作区高 1019 ⇒ 不触发下边夹取（hi=139），
        // 这才测得是"留抓取高度"本身，而不是夹取结果。
        var (_, top) = RestoreDrag.RestoreTopLeft(
            grabX: 855, grabWidth: W, cursorX: 855, cursorY: 100,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: W, workBottom: H);
        Assert.Equal(100 - RestoreDrag.GrabOffsetY, top, precision: 6);
    }

    [Fact]
    public void 贴近右边缘_夹在工作区内()
    {
        var (left, _) = RestoreDrag.RestoreTopLeft(
            grabX: W * 0.9, grabWidth: W, cursorX: W - 7, cursorY: 500,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: W, workBottom: H);
        Assert.Equal(W - 1320, left, precision: 6);
    }

    [Fact]
    public void 贴近下边缘_夹在工作区内()
    {
        var (_, top) = RestoreDrag.RestoreTopLeft(
            grabX: 855, grabWidth: W, cursorX: 855, cursorY: H - 4,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: W, workBottom: H);
        Assert.Equal(H - 880, top, precision: 6);
    }

    [Fact]
    public void 光标顶到屏幕上沿_不飞出工作区()
    {
        var (_, top) = RestoreDrag.RestoreTopLeft(
            grabX: 855, grabWidth: W, cursorX: 855, cursorY: 3,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: W, workBottom: H);
        Assert.Equal(0, top, precision: 6);
    }

    [Fact]
    public void 副屏工作区坐标为负_按副屏夹取()
    {
        // 副屏在主屏左边：工作区 (-1707,0)-(0,1019)
        var (left, _) = RestoreDrag.RestoreTopLeft(
            grabX: 855, grabWidth: W, cursorX: -10, cursorY: 500,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: -1707, workTop: 0, workRight: 0, workBottom: H);
        // 想要 -670，夹到左边界 -1707 与右边界 0-1320=-1320 之间 ⇒ -1320
        Assert.Equal(-1320, left, precision: 6);
    }

    [Fact]
    public void 窗口比工作区还宽_贴左上且不抛()
    {
        var (left, top) = RestoreDrag.RestoreTopLeft(
            grabX: 500, grabWidth: 1000, cursorX: 500, cursorY: 400,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: 1000, workBottom: 800);
        Assert.Equal(0, left, precision: 6);
        Assert.Equal(0, top, precision: 6);
    }

    [Fact]
    public void 抓取宽度为零_按正中间算不除零()
    {
        var (left, _) = RestoreDrag.RestoreTopLeft(
            grabX: 0, grabWidth: 0, cursorX: 855, cursorY: 500,
            restoreWidth: 1320, restoreHeight: 880,
            workLeft: 0, workTop: 0, workRight: W, workBottom: H);
        Assert.Equal(855 - 660, left, precision: 6);
    }
}
