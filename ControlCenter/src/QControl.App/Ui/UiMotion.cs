using System.Windows.Media.Animation;

namespace QControl.App.Ui;

/// <summary>
/// **没有动效了。** 这里只剩一个限帧工具。
///
/// 2026-10-28 用户要求把界面上的装饰过渡全部删掉（翻页转场、悬停/按下淡入、
/// 对话框与提示条淡入、关于页呼吸点）—— 那些东西的取舍反复过好几轮
/// （"太卡" → "改只动顶栏" → "不够明显" → "都删了吧"），最终结论是**不要**：
/// 点哪儿就瞬时到哪儿，没有过渡帧，也就没有"卡不卡、明不明显"这回事。
/// 相关成员（HeaderIn / Pulse / ConfigEnabled / ForceOff / 各档时长常量）已全部移除，
/// 设置页的「界面动效」开关也一并删了（没有可关的东西）。
///
/// 还留着 <see cref="At"/> 是因为**转速与读数的可视化仍在动**（用户要的）：
/// 风扇叶轮、环形表的扫值、历史曲线。这台机器是 165Hz 屏 + 软件渲染，
/// 不限帧就是把 CPU 白烧（实测散热页 26%~60%，见 DETAILS「动效」一节）。
/// </summary>
internal static class UiMotion
{
    /// <summary>
    /// 给逐帧动画限帧。<c>Timeline.DesiredFrameRate</c> 不设 = 跟着屏幕刷新率走。
    /// 叶轮那种"一直在转"的装饰 60fps 足够（24 根细线本来就是虚影），CPU 却只有 1/3。
    /// </summary>
    public static T At<T>(T timeline, int fps) where T : Timeline
    {
        Timeline.SetDesiredFrameRate(timeline, fps);
        return timeline;
    }
}
