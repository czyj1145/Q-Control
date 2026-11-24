namespace QControl.Core.Window;

/// <summary>
/// 「拖动最大化的窗口」的还原定位算术。**纯函数**：不碰 Win32、不碰 WPF 类型，
/// 输入输出全是 double —— 所以它能被单测逐条钉住（见 RestoreDragTests）。
///
/// 要复制的系统行为：无边框窗口最大化后，用户在顶栏上按下并拖动，系统标题栏会
/// **先把窗口还原、再让它跟着光标走**，而且抓取点在标题栏上的相对位置不变
/// （抓在正中间，还原后光标还在正中间）。本项目的顶栏是自绘的
/// （WindowChrome CaptionHeight=0 + 手动 DragMove），系统不认这块地方，
/// 所以"还原到哪"必须自己算 —— 就是这个文件。
///
/// 坐标约定：全部是 **WPF 的 DIP**（设备无关像素）。调用方负责把光标的设备像素
/// 换过来（PresentationSource.CompositionTarget.TransformFromDevice 是 Matrix，
/// 只有 Transform(Point)，矩形要自己用两个角点换）。
/// </summary>
public static class RestoreDrag
{
    /// <summary>抓取点落在顶栏的哪个高度：还原后窗口顶边到光标的距离（px）。
    /// 取顶栏高度 56 的三分之一不到 —— 光标落在标题文字那一行上，符合直觉。</summary>
    public const double GrabOffsetY = 10;

    /// <summary>
    /// 算还原后窗口的左上角（DIP）。
    /// </summary>
    /// <param name="grabX">抓取点相对**最大化窗口**的 X（DIP）——用来求比例。</param>
    /// <param name="grabWidth">抓取时窗口的宽度（= 最大化宽度，DIP）。</param>
    /// <param name="cursorX">光标的 X（DIP，已换算）。</param>
    /// <param name="cursorY">光标的 Y（DIP，已换算）。</param>
    /// <param name="restoreWidth">还原后的窗口宽（= RestoreBounds.Width）。</param>
    /// <param name="restoreHeight">还原后的窗口高（= RestoreBounds.Height）。</param>
    /// <param name="workLeft">工作区左（光标所在显示器，DIP）。</param>
    /// <param name="workTop">工作区上。</param>
    /// <param name="workRight">工作区右。</param>
    /// <param name="workBottom">工作区下。</param>
    public static (double Left, double Top) RestoreTopLeft(
        double grabX, double grabWidth,
        double cursorX, double cursorY,
        double restoreWidth, double restoreHeight,
        double workLeft, double workTop, double workRight, double workBottom)
    {
        // ① 抓取点在旧宽度里的比例。还原后按同一比例贴回光标 —— 少了这一步，
        //    抓在哪儿窗口都"跳"到光标左上角，手势直接废掉。
        var ratio = grabWidth > 0 ? grabX / grabWidth : 0.5;
        ratio = Math.Clamp(ratio, 0.0, 1.0);

        var left = cursorX - restoreWidth * ratio;
        var top = cursorY - GrabOffsetY;

        // ② 夹回工作区。夹取区间写成 (lo, hi) 而不是 Math.Clamp(left, workLeft, workRight)
        //    是为了"窗口比工作区还宽"时不出现负数区间 —— 那种情况贴工作区左上角。
        left = Clamp(left, workLeft, workRight - restoreWidth);
        top = Clamp(top, workTop, workBottom - restoreHeight);
        return (left, top);
    }

    private static double Clamp(double value, double lo, double hi) => hi <= lo ? lo : Math.Clamp(value, lo, hi);
}
