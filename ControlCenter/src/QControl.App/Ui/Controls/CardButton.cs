using System.Windows;
using System.Windows.Controls;

namespace QControl.App.Ui.Controls;

/// <summary>
/// "卡片式按钮"，额外带一个 <see cref="IsOn"/> 表达**选中态**。
///
/// 为什么不用 ToggleButton：ToggleButton 点下去会**先自己**把 IsChecked 翻过来，
/// 写失败时界面就先乐观点亮、一秒后被快照打回原样 —— 就是"切模式时界面乱跳"。
/// 这里 IsOn 只由渲染逻辑按真值/待确认窗口设置，
/// 点击本身不改任何状态，于是"写成功才亮"这条纪律是靠结构保证的，不靠记性。
/// </summary>
public class CardButton : Button
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(CardButton), new PropertyMetadata(false));

    /// <summary>选中态：亮渐变底 + 深色字。</summary>
    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }
}
