using System.Windows;

namespace QControl.App.Ui;

/// <summary>
/// XAML 侧的调色板入口 —— Theme.xaml 里写一行 <c>&lt;ui:PaletteDictionary/&gt;</c> 就够了。
///
/// 为什么需要它：调色板画笔（TxtBrush / DimBrush / AccBrush …）是 <see cref="UiPalette"/>
/// 在运行时算出来的，XAML 里没法静态声明；而页面是「先 new、再塞进窗口」的，
/// InitializeComponent 那一刻它**没有父级**，于是沿父链找画笔的 DynamicResource
/// 全部落空 —— 抛 ResourceReferenceKeyNotFoundException，紧接着把占位对象
/// （NamedObject）强转成 Brush 再抛一次 InvalidCastException。
///
/// 它自己不存色，只是把 <see cref="UiTheme.PaletteRes"/> 那份**共享字典**挂进来的壳 ——
/// 于是：① 每个页面/窗口在 InitializeComponent 阶段就查得到画笔，与树无关；
/// ② 换主题只要重灌那一份共享字典，所有引用者一起变，不用挨个通知。
/// </summary>
internal sealed class PaletteDictionary : ResourceDictionary
{
    public PaletteDictionary()
    {
        MergedDictionaries.Add(UiTheme.PaletteRes);
    }
}
