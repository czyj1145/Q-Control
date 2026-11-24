using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using QControl.App.Ui.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace QControl.App.Ui;

/// <summary>一行「标签 + 数值」。数值读不到时整行收起来 —— 项目铁律：不留 "--" 占位。</summary>
internal sealed class KvRow
{
    public required Border Root { get; init; }
    public required TextBlock Value { get; init; }

    /// <summary>null = 这门数据没有 ⇒ 整行隐藏。</summary>
    public void Set(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Root.Visibility = Visibility.Collapsed;
            return;
        }
        Value.Text = text;
        Root.Visibility = Visibility.Visible;
    }

    /// <summary>行恒显示，值有可能是 "--"（温度/频率/显存这类"行本身有意义"的项走这条）。</summary>
    public void SetAlways(string text)
    {
        Value.Text = text;
        Root.Visibility = Visibility.Visible;
    }
}

/// <summary>
/// 纯代码造控件的几个小工厂。
/// 为什么这些不写进 XAML：键值行、状态胶囊都是"同一形状 × 若干行"，用 XAML 抄 4 遍
/// 既容易抄漏，又没法在 code-behind 里一句 Set() 控制显隐。样式仍然全部来自 Theme.xaml，
/// 这里只负责拼装（FindResource 沿父链找，所以样式只维护一份）。
/// </summary>
internal static class UiKit
{
    /// <summary>线性图标：描边跟随最近一层 Control 的 Foreground（悬停/选中/换主题自动跟）。</summary>
    public static ShapePath Icon(Geometry data, double size = 20, double thickness = 1.6)
    {
        var path = new ShapePath
        {
            Data = data,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = null,
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        path.SetBinding(ShapePath.StrokeProperty, new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1),
        });
        return path;
    }

    /// <summary>往 <paramref name="host"/> 里追加一行键值。返回句柄，调用方留着以后每秒刷。</summary>
    public static KvRow MakeKv(StackPanel host, string label)
    {
        var labelBlock = new TextBlock { Text = label, Style = (Style)host.FindResource("KvLabel") };
        var valueBlock = new TextBlock { Text = "--", Style = (Style)host.FindResource("KvValue") };
        valueBlock.HorizontalAlignment = HorizontalAlignment.Right;

        var line = new DockPanel();
        line.Children.Add(valueBlock);                    // 先加的要显式 Dock=Right
        DockPanel.SetDock(valueBlock, Dock.Right);
        line.Children.Add(labelBlock);

        var row = new Border { Child = line, Style = (Style)host.FindResource("KvRow") };
        host.Children.Add(row);
        return new KvRow { Root = row, Value = valueBlock };
    }

    /// <summary>
    /// 一行设置：左边「图标 + 标题 / 说明」，右边控件（钉在右）。
    /// 返回说明文字块 —— EC 控制与原厂软件这两行的说明是**动态**的（机型名、进程现状），
    /// 调用方要留着以后每秒/每次加载改写。
    ///
    /// ⚠️ 图标在这里**显式**设 AccBrush，不能用 <see cref="Icon"/> 默认那条
    ///    "跟随祖先 Control 的 Foreground"绑定：这一行外层是 Border（不是 Control），
    ///    绑定找不到祖先，图标会一根线都不画。
    /// </summary>
    public static (Border Root, TextBlock Desc) MakeSetRow(
        FrameworkElement ctx, Geometry? icon, string title, string desc, FrameworkElement control)
    {
        var (root, descBlock, _) = MakeSetRowFull(ctx, icon, title, desc, control);
        return (root, descBlock);
    }

    /// <summary>
    /// 与 <see cref="MakeSetRow"/> 相同，但**连标题块一起返回**。
    ///
    /// 为什么要这个重载：设置页有一行会换标题（「HWiNFO 未找到」↔「HWiNFO 在运行，但没有数据」）。
    /// 原签名只回 Root / Desc，调用方要么去 `Children[^1]` 里翻控件，要么再建一行把高度叠出去。
    /// ⚠️ 别把三元组直接塞进 <see cref="MakeSetRow"/>：全仓有几十处 `var (a, b) = ...`
    ///    式的二元解构，多一项就会编译不过。
    /// </summary>
    public static (Border Root, TextBlock Desc, TextBlock Title) MakeSetRowFull(
        FrameworkElement ctx, Geometry? icon, string title, string desc, FrameworkElement control)
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(14, 0, 0, 0);

        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            var glyph = Icon(icon, 15, 1.7);
            glyph.SetResourceReference(ShapePath.StrokeProperty, "AccBrush");
            glyph.Opacity = 0.9;
            glyph.Margin = new Thickness(0, 0, 6, 0);
            titleLine.Children.Add(glyph);
        }
        var titleBlock = new TextBlock
        {
            Text = title,
            Style = (Style)ctx.FindResource("SetTitle"),
        };
        titleLine.Children.Add(titleBlock);

        var descBlock = new TextBlock
        {
            Text = desc,
            Style = (Style)ctx.FindResource("SetDesc"),
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleLine);
        text.Children.Add(descBlock);

        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(control, Dock.Right);
        line.Children.Add(control);
        line.Children.Add(text);

        var root = new Border
        {
            Child = line,
            Style = (Style)ctx.FindResource("SetRow"),
        };
        return (root, descBlock, titleBlock);
    }

    /// <summary>
    /// 把一组 <see cref="MakeSetRow"/> 里**最后一行的下边距归零**。
    ///
    /// 每行自带 8(6)px 下边距是用来隔开下一行的；最后一行下面没有下一行，
    /// 那 8px 就和卡片自己的 padding 叠在一起 ⇒ 卡底永远比卡头厚一截（卡内留白不对称），
    /// 而且它是**纯浪费的高度**：设置页四张卡加起来 30 多 px，正好把一屏顶破。
    /// 只改本组最后一个子元素的 Margin（本地值覆盖样式），不动 SetRow 本身。
    /// </summary>
    public static void TrimLastRowGap(Panel host)
    {
        if (host.Children.Count == 0) return;
        if (host.Children[^1] is not FrameworkElement last) return;
        var m = last.Margin;
        last.Margin = new Thickness(m.Left, m.Top, m.Right, 0);
    }

    /// <summary>
    /// 按**当前可见性**重排一组行的行距：可见行之间 6px、最后一个可见行 0。
    ///
    /// 与 <see cref="TrimLastRowGap"/> 的分工：那个只在构造末尾看一遍"最后一个子元素"，
    /// 而设置页有一行会整行收起（「HWiNFO 未找到」只在真出问题时才出现）。
    /// 收起时它的 6px 会留在上面那一行身上 ⇒ 卡底比卡头厚 6px，四张卡各自的卡底一高一低。
    /// 每次显隐变化后调一次，卡内留白重新对称。
    /// </summary>
    public static void NormalizeRowGaps(Panel host, double gap = 6)
    {
        var rows = host.Children.OfType<FrameworkElement>().ToList();
        var lastVisible = rows.LastOrDefault(e => e.Visibility == Visibility.Visible);
        foreach (var row in rows)
        {
            var m = row.Margin;
            var bottom = ReferenceEquals(row, lastVisible) ? 0 : gap;
            if (Math.Abs(m.Bottom - bottom) > 0.01)
                row.Margin = new Thickness(m.Left, m.Top, m.Right, bottom);
        }
    }

    /// <summary>分段选择里的一个按钮。选中态靠 CardButton.IsOn。</summary>
    public static CardButton SegButton(FrameworkElement ctx, string text)
        => new()
        {
            Content = text,
            Style = (Style)ctx.FindResource("SegButton"),
        };

    /// <summary>把若干 <see cref="SegButton"/> 装进一个描边小胶囊。</summary>
    public static Border SegBox(FrameworkElement ctx, params CardButton[] buttons)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons) line.Children.Add(button);
        return new Border { Child = line, Style = (Style)ctx.FindResource("SegBox") };
    }

    /// <summary>
    /// 让页面撑满视口（WPF 按内容自适应会比页框矮一截）。
    /// 做法：给宿主 Grid 的 MinHeight 跟着 ScrollViewer 的视口高走，**减去 <paramref name="slack"/>**。
    /// ⚠️ 用 `ActualHeight` 而不是 `ViewportHeight`：后者在有滚动条时会缩掉 12px，
    ///    于是"给高度 → 冒出滚动条 → 视口变小 → 再给高度"来回抖，滚动条永远不退
    ///    （第一版就是这么在性能页上留了一条常驻滚动条的）。
    /// ⚠️ 减掉的那几像素也不是拍脑袋：内容正好等于视口高时，浮点取整会让它"多出 0.x 像素"。
    /// </summary>
    public static void FillViewport(ScrollViewer scroll, FrameworkElement host, double slack = 6)
    {
        void Apply()
        {
            var want = scroll.ActualHeight - slack;
            if (want > 0 && Math.Abs(host.MinHeight - want) > 0.5) host.MinHeight = want;
        }

        scroll.SizeChanged += (_, _) => Apply();
        scroll.Loaded += (_, _) => Apply();
    }

    /// <summary>
    /// 让一组行在容器里做 **space-evenly** 分布（.kv 在卡片里就是这么排的）。
    ///
    /// 为什么需要它：<c>align-content:space-evenly</c> 会按剩余高度把行距拉开，
    /// WPF 的 StackPanel 只会紧贴在一起 —— 卡片一高，行就挤在
    /// 顶上（或居中但间距偏小）。
    ///
    /// 做法：行距 = (容器高 - 各行高之和) / (行数 + 1)，首尾各留一份 ⇒ 整组天然居中；
    /// 行数或可见性变化时重算（隐藏的行（Visibility.Collapsed）不参与）。
    /// ⚠️ 容器必须 Stretch 撑满可用高度，否则算出来的额外空间永远是 0。
    ///
    /// ⚠️ 行距被 <paramref name="maxGap"/> 夹住时（全屏窗口最常见：卡高了、行就那么几行），
    ///    剩下的余量**对半分到首行上方与末行下方**，而不是堆在末尾 —— 否则整组看起来
    ///    "沉在顶上、底下空一条"。这正是 CSS space-evenly 的语义（首尾各留一份）。
    /// </summary>
    public static void EnableSpaceEvenly(StackPanel host, double minGap = 8, double maxGap = 44)
    {
        host.VerticalAlignment = VerticalAlignment.Stretch;

        // ⚠️ 这一行边距是**写回子元素**的，所以它会算进 host 的 DesiredSize ——
        //    而 host 撑在卡片里，卡片的高度又由这一份 DesiredSize 决定：
        //    "按高度分边距 → 边距把高度顶高 → 再按新高度分边距" 是个正反馈。
        //    整段留 <see cref="SpreadSlack"/> 的余量、且边距**向下取整**，
        //    才能保证 sum + 边距 ≤ 分边距时用的那个高度，正反馈才会收敛在视口以内。
        void Spread()
        {
            var rows = host.Children.OfType<FrameworkElement>()
                .Where(x => x.Visibility == Visibility.Visible).ToList();
            if (rows.Count == 0) return;

            var sum = rows.Sum(x => x.ActualHeight);
            if (sum <= 0) return;                       // 首帧还没量出来

            var room = host.ActualHeight - sum - SpreadSlack;
            var gap = Math.Max(minGap, Math.Min(maxGap, room / (rows.Count + 1)));
            gap = Math.Floor(gap);
            if (Math.Abs(gap - 8) < 0.01 && rows.Count == 1) return;

            // gap 触顶后剩下的高度对半分。gap 没触顶时它恒为 0（余量已全部分完），
            // 内容超高时也是 0（gap = minGap，余量为负）—— 只有"夹住"这一种情况会生效。
            var extra = Math.Floor(Math.Max(0, (host.ActualHeight - sum - gap * (rows.Count + 1)
                                              - SpreadSlack) / 2));

            for (var i = 0; i < rows.Count; i++)
            {
                var want = new Thickness(0,
                    gap + (i == 0 ? extra : 0),
                    0,
                    i == rows.Count - 1 ? gap + extra : 0);
                if (!rows[i].Margin.Equals(want)) rows[i].Margin = want;
            }
        }

        host.SizeChanged += (_, _) => Spread();
        host.LayoutUpdated += (_, _) =>
        {
            // 行的 ActualHeight 在 SizeChanged 那一刻可能还是 0（首次布局），补一次；
            // 只在 Margin 确实要变时才写，避免 LayoutUpdated 里改布局造成的死循环。
            Spread();
        };
    }

    /// <summary>
    /// <see cref="EnableSpaceEvenly"/> 分摊时整段留出的余量（px）。
    ///
    /// 为什么必须有：`UseLayoutRounding` 会把 62.7 这样的半像素边距按设备像素**向上**取整
    /// （150% 缩放下 +0.67），几段加起来正好比视口高不到 1px ⇒ 页面右边挂一条
    /// **永远退不掉**的细滚动条（实测内容 800.7 / 视口 800，滚动条能滚 0.7px，
    /// 什么都看不出来）。
    /// 留 4px（每端 2px）看不出差别，但足以吃掉全部取整误差。
    /// </summary>
    private const double SpreadSlack = 4;

    /// <summary>
    /// 卡片之间的**唯一**间隔（px），1320 窗口下约 14.5 —— 这里取上限 16：一来与页面内 12px 的行距拉开一档层级
    /// （同值时四张卡看着像连成一块），二来固定值不会随窗口宽度抖。
    /// </summary>
    public const double CardGap = 16;

    /// <summary>
    /// 给「装卡片的 Grid」补上 <c>gap</c> 的语义 —— WPF 的 Grid 没有 gap 属性。
    /// 每个子元素按自己的 Row/Column/Span 各让**半个**间隔：相邻两张卡两侧各让一半，
    /// 实际相距正好 <see cref="CardGap"/>，页框边上不多留（= CSS `gap` 不往容器里加 padding）。
    ///
    /// 为什么必须两侧各让一半，而不是「左卡给右 margin」：后者会让 Star 列里两张卡宽度不等。
    /// 实测就是这个问题 —— 左卡 452px、右卡 468px，一整行明显偏左。
    ///
    /// ⚠️ 会**覆盖**子元素的 Margin。用了它，XAML 里就别再给这几张卡写 Margin，
    ///    间隔只有这一个真源（想调就改 <see cref="CardGap"/>）。
    /// ⚠️ 只在「卡片都加完之后」调一次（各页构造末尾）。它不挂事件：间隔不随尺寸变化，
    ///    挂 LayoutUpdated 反而给每秒 tick 的页面添活。
    /// </summary>
    public static void ApplyCardGap(Grid host, double gap = CardGap)
    {
        var half = gap / 2;
        var rows = Math.Max(1, host.RowDefinitions.Count);
        var cols = Math.Max(1, host.ColumnDefinitions.Count);

        foreach (var child in host.Children.OfType<FrameworkElement>())
        {
            var r = Grid.GetRow(child);
            var c = Grid.GetColumn(child);
            var rs = Math.Max(1, Grid.GetRowSpan(child));
            var cs = Math.Max(1, Grid.GetColumnSpan(child));

            var want = new Thickness(
                c > 0 ? half : 0,                    // 左
                r > 0 ? half : 0,                    // 上
                c + cs < cols ? half : 0,            // 右（贴右边界的不让）
                r + rs < rows ? half : 0);           // 下（贴下边界的不让）

            if (!child.Margin.Equals(want)) child.Margin = want;
        }
    }

    /// <summary>顶栏状态胶囊（"CPU 51°C · 23%"）。</summary>
    public static Border Chip(FrameworkElement ctx, string label, out TextBlock value)
    {
        value = new TextBlock { Text = "读取中…", Style = (Style)ctx.FindResource("ChipValue") };
        var text = new TextBlock { Text = label, Style = (Style)ctx.FindResource("ChipLabel") };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(text);
        line.Children.Add(value);

        return new Border { Child = line, Style = (Style)ctx.FindResource("Chip") };
    }
}
