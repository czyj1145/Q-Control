using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QControl.App.Ui;

/// <summary>
/// 自绘的模态确认框 / 多选一。
///
/// 为什么不用 <c>MessageBox</c>：系统对话框是白底方角、还带自己的图标与任务栏项，
/// 跟这套无边框深色窗口放一起像两个程序；更重要的是它没法表达多选一
/// （显卡模式写完要问"立即重启 / 稍后重启"），也没法把警告段落排好。
///
/// 实现要点：
///   · 挂在窗口 <see cref="MainWindow.Overlay"/> 上（半透明遮罩 + 卡片），
///     遮罩吃点击 ⇒ 客观上就是模态；Esc = 取消。
///   · 返回 <see cref="Task"/>，调用方 <c>await</c> 即可 —— 绝不在这里跑嵌套消息循环
///     （那种写法会让"关窗口时对话框还挂着"变成退出路径上的死锁）。
///   · 拿不到承载窗口时**按取消处理**（宁可不动硬件，也不静默执行）。
/// </summary>
internal static class UiDialog
{
    /// <summary>二选一确认。返回 false = 用户取消 / 没窗口可挂。</summary>
    public static async Task<bool> ConfirmAsync(
        FrameworkElement ctx,
        string title,
        string message,
        string okText = "继续",
        string cancelText = "取消",
        bool danger = false)
    {
        var picked = await ChooseAsync(ctx, title, message,
        [
            ("ok", okText, true, danger),
            ("cancel", cancelText, false, false),
        ]);
        return picked == "ok";
    }

    /// <summary>
    /// 多选一。返回选中的 key，取消 / 无法显示返回 null。
    /// <paramref name="choices"/> 每项 = (key, 按钮文字, 是否主按钮, 是否危险色)。
    /// </summary>
    public static Task<string?> ChooseAsync(
        FrameworkElement ctx,
        string title,
        string message,
        IReadOnlyList<(string Key, string Label, bool Primary, bool Danger)> choices)
    {
        if (Window.GetWindow(ctx) is not MainWindow window)
        {
            // 没有可挂的窗口（例如 --ui-shot 截图路径）：一律当"用户取消"
            return Task.FromResult<string?>(null);
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlay = window.Overlay;

        // ---- 遮罩 ----
        var mask = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            Focusable = true,       // 收 Esc
        };

        // ---- 卡片 ----
        var card = new Border { Style = (Style)window.FindResource("DialogCard") };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)window.FindResource("DialogTitle"),
        });

        var body = new TextBlock
        {
            Text = message,
            Style = (Style)window.FindResource("DialogBody"),
        };
        stack.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        void Finish(string? key)
        {
            overlay.Children.Remove(mask);
            completion.TrySetResult(key);
        }

        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var styleKey = choice.Primary ? "DialogPrimaryButton"
                : choice.Danger ? "MiniButtonDanger"
                : "DialogGhostButton";
            var button = new Button
            {
                Content = choice.Label,
                Style = (Style)window.FindResource(styleKey),
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = choice.Primary,
                IsCancel = choice.Key == "cancel",
            };
            button.Click += (_, _) => Finish(choice.Key);
            buttons.Children.Add(button);

            // 默认焦点给主按钮：键盘用户按回车就是"继续"
            if (choice.Primary) card.Loaded += (_, _) => button.Focus();
        }

        stack.Children.Add(buttons);
        card.Child = stack;
        mask.Children.Add(card);

        mask.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Finish(null);
        };

        // 对话框**瞬时出现**：2026-10-28 删掉全部装饰动效，原来那条 120ms 卡片淡入
        // 一并去掉（遮罩本来就没动画 —— 铺满整窗做透明度动画等于每帧重画整窗）。
        // ⚠️ 卡片基础值必须是 1：以后谁要加动画，也别再回到"基础值 0 + 动画淡入"，
        //    动画一旦被 BeginAnimation(null) 撤掉，留下的就是一张看不见的对话框。
        card.Opacity = 1;

        overlay.Children.Add(mask);
        mask.Focus();

        return completion.Task;
    }
}
