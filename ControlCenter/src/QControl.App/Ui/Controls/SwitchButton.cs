using System.Windows;
using System.Windows.Controls;

namespace QControl.App.Ui.Controls;

/// <summary>
/// 药丸开关：40×22 圆角胶囊 + 滑点。
///
/// 与 <see cref="CardButton"/> 同一个纪律：**点击不改状态**，只有
/// <see cref="IsOn"/> 被渲染逻辑设置时才亮。用 ToggleButton 的话，点下去会先自己翻
/// IsChecked，写失败再弹回来 —— 就是"先亮一秒再弹回"「开关乱跳」。
/// </summary>
public class SwitchButton : Button
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(SwitchButton), new PropertyMetadata(false));

    /// <summary>真值：后端确认"已开启"才为 true。</summary>
    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }
}
