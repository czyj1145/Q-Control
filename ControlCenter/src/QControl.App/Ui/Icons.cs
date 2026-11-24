//   图标几何的真源。**手改**：生成脚本已于 2026-10-24 删除，这里不再有上游可重新生成。
//   坐标系一律 0 0 24 24，描边 1.6~1.7 + Round 端帽/接合。
#nullable enable

using System.Windows.Media;

namespace QControl.App.Ui;

/// <summary>
/// 导航/卡片标题用的线性图标（PathGeometry 迷你语言，绝对坐标）。
/// ⚠️ 必须 public：XAML 里的 {x:Static ui:Icons.Cpu} 走 BAML 反射解析，
///    internal 类型在加载期会抛 XamlParseException（提示是「设置属性 Path.Data 时引发了异常」），
///    编译期却一点提示都没有。
/// ⚠️ 暴露的是 **Geometry 对象**，不是路径字符串：XAML 里 {x:Static} 的结果不会再走
///    类型转换器（BAML 只对字面量属性值做转换），给 string 会当场
///    "不是属性 Data 的有效值"。同理，冻结后多个 Path 共用一个实例。
/// </summary>
public static class Icons
{
    /// <summary>SVG #ic-cpu</summary>
    public static readonly Geometry Cpu = Make("M 8.5,6.5 H 15.5 A 2,2 0 0 1 17.5,8.5 V 15.5 A 2,2 0 0 1 15.5,17.5 H 8.5 A 2,2 0 0 1 6.5,15.5 V 8.5 A 2,2 0 0 1 8.5,6.5 Z M 9.5,3.2 L 9.5,6.5 M 14.5,3.2 L 14.5,6.5 M 9.5,17.5 L 9.5,20.8 M 14.5,17.5 L 14.5,20.8 M 3.2,9.5 L 6.5,9.5 M 3.2,14.5 L 6.5,14.5 M 17.5,9.5 L 20.8,9.5 M 17.5,14.5 L 20.8,14.5");

    /// <summary>SVG #ic-gpu</summary>
    public static readonly Geometry Gpu = Make("M 4.7,5.5 H 19.3 A 2.2,2.2 0 0 1 21.5,7.7 V 15.8 A 2.2,2.2 0 0 1 19.3,18 H 4.7 A 2.2,2.2 0 0 1 2.5,15.8 V 7.7 A 2.2,2.2 0 0 1 4.7,5.5 Z M 5.6,11.8 A 3,3 0 1 1 11.6,11.8 A 3,3 0 1 1 5.6,11.8 Z M 14.6,9.4 L 18.8,9.4 M 14.6,11.8 L 18.8,11.8 M 14.6,14.2 L 17,14.2 M 5.5,18 L 5.5,21 L 11.5,21");

    /// <summary>SVG #ic-chart</summary>
    public static readonly Geometry Chart = Make("M 4,4 L 4,20 L 20,20 M 7.5,15.5 L 11.1,11.2 L 14.1,13.8 L 18.5,8");

    /// <summary>SVG #ic-fan</summary>
    public static readonly Geometry Fan = Make("M 10.827,16.379 A 6.082,6.082 0 0 1 2.209,9.377 L 7.621,10.827 A 6.082,6.082 0 0 1 14.623,2.209 L 13.173,7.621 A 6.082,6.082 0 0 1 21.791,14.623 L 16.379,13.173 A 6.082,6.082 0 0 1 9.377,21.791 L 10.827,16.379 Z M 12,12 L 12,12.01");

    /// <summary>SVG #ic-hw</summary>
    public static readonly Geometry Hardware = Make("M 5.3,4.5 H 18.7 A 1.8,1.8 0 0 1 20.5,6.3 V 13.7 A 1.8,1.8 0 0 1 18.7,15.5 H 5.3 A 1.8,1.8 0 0 1 3.5,13.7 V 6.3 A 1.8,1.8 0 0 1 5.3,4.5 Z M 2,19 L 22,19 M 7.5,8 L 11.5,8 M 7.5,11 L 14.5,11 M 15.5,8 L 16.5,8");

    /// <summary>SVG #ic-sun</summary>
    public static readonly Geometry Sun = Make("M 8,12 A 4,4 0 1 1 16,12 A 4,4 0 1 1 8,12 Z M 12,2.5 L 12,5.1 M 12,18.9 L 12,21.5 M 2.5,12 L 5.1,12 M 18.9,12 L 21.5,12 M 5.3,5.3 L 7.2,7.2 M 16.8,16.8 L 18.7,18.7 M 18.7,5.3 L 16.8,7.2 M 7.2,16.8 L 5.3,18.7");

    /// <summary>SVG #ic-moon</summary>
    public static readonly Geometry Moon = Make("M 20.5,14.3 A 8.6,8.6 0 0 1 9.7,3.5 A 8.6,8.6 0 1 0 20.5,14.3 Z");

    /// <summary>SVG #ic-user</summary>
    public static readonly Geometry User = Make("M 8.4,8.2 A 3.6,3.6 0 1 1 15.6,8.2 A 3.6,3.6 0 1 1 8.4,8.2 Z M 4.9,20.2 A 7.1,7.1 0 0 1 19.1,20.2");

    /// <summary>SVG #ic-mode</summary>
    public static readonly Geometry Mode = Make("M 12,3.4 L 3.8,7.4 L 12,11.4 L 20.2,7.4 Z M 3.8,12.4 L 12,16.4 L 20.2,12.4 M 3.8,16.6 L 12,20.6 L 20.2,16.6");

    /// <summary>SVG #ic-oc</summary>
    public static readonly Geometry Oc = Make("M 4,17.5 L 8.8,12.7 L 12.2,16.1 L 20,8.3 M 15,8.3 L 20,8.3 L 20,13.3");

    /// <summary>SVG #ic-gfx-hybrid</summary>
    public static readonly Geometry GfxHybrid = Make("M 2.4,12 A 5,5 0 1 1 12.4,12 A 5,5 0 1 1 2.4,12 Z M 5.8,12 A 1.6,1.6 0 1 1 9,12 A 1.6,1.6 0 1 1 5.8,12 Z M 7.4,10.4 L 7.4,8 M 8.79,12.8 L 10.89,14.01 M 6.01,12.8 L 3.91,14.01 M 15.8,8.2 H 19.8 A 1.8,1.8 0 0 1 21.6,10 V 14 A 1.8,1.8 0 0 1 19.8,15.8 H 15.8 A 1.8,1.8 0 0 1 14,14 V 10 A 1.8,1.8 0 0 1 15.8,8.2 Z M 16.4,8.2 L 16.4,6 M 19.2,8.2 L 19.2,6 M 16.4,15.8 L 16.4,18 M 19.2,15.8 L 19.2,18");

    /// <summary>SVG #ic-gfx-dgpu</summary>
    public static readonly Geometry GfxDgpu = Make("M 3.2,12 A 8.8,8.8 0 1 1 20.8,12 A 8.8,8.8 0 1 1 3.2,12 Z M 9.2,12 A 2.8,2.8 0 1 1 14.8,12 A 2.8,2.8 0 1 1 9.2,12 Z M 12,9.2 L 12,4.8 M 14.42,13.4 L 18.48,15.74 M 9.58,13.4 L 5.52,15.74");

    /// <summary>SVG #ic-gfx-igpu</summary>
    public static readonly Geometry GfxIgpu = Make("M 7,4.6 H 17 A 2.4,2.4 0 0 1 19.4,7 V 17 A 2.4,2.4 0 0 1 17,19.4 H 7 A 2.4,2.4 0 0 1 4.6,17 V 7 A 2.4,2.4 0 0 1 7,4.6 Z M 13.2,7.4 L 10.1,12.6 L 13,12.6 L 12.1,16.8 L 15.5,11.2 L 12.6,11.2 Z M 9.4,4.6 L 9.4,2.2 M 14.6,4.6 L 14.6,2.2 M 9.4,19.4 L 9.4,21.8 M 14.6,19.4 L 14.6,21.8 M 4.6,9.4 L 2.2,9.4 M 4.6,14.6 L 2.2,14.6 M 19.4,9.4 L 21.8,9.4 M 19.4,14.6 L 21.8,14.6");

    /// <summary>SVG #ic-ec</summary>
    public static readonly Geometry Ec = Make("M 8.5,6.5 H 15.5 A 2,2 0 0 1 17.5,8.5 V 15.5 A 2,2 0 0 1 15.5,17.5 H 8.5 A 2,2 0 0 1 6.5,15.5 V 8.5 A 2,2 0 0 1 8.5,6.5 Z M 12.9,8.8 L 10.3,12.4 L 12.7,12.4 L 11.8,15.4 L 14.7,11.5 L 12.3,11.5 Z M 3.4,10 L 6.5,10 M 3.4,14 L 6.5,14 M 17.5,10 L 20.6,10 M 17.5,14 L 20.6,14");

    /// <summary>SVG #ic-window</summary>
    public static readonly Geometry Window = Make("M 5.2,4 H 18.8 A 2.2,2.2 0 0 1 21,6.2 V 17.8 A 2.2,2.2 0 0 1 18.8,20 H 5.2 A 2.2,2.2 0 0 1 3,17.8 V 6.2 A 2.2,2.2 0 0 1 5.2,4 Z M 3,9 L 21,9 M 6.5,6.5 L 6.51,6.5 M 9.2,6.5 L 9.21,6.5");

    /// <summary>SVG #ic-tray</summary>
    public static readonly Geometry Tray = Make("M 3,13.5 L 3,19 L 21,19 L 21,13.5 M 3,13.5 L 8,13.5 L 9.2,15.7 L 14.8,15.7 L 16,13.5 L 21,13.5 M 12,3.5 L 12,9.5 M 9.4,6.9 L 12,9.5 L 14.6,6.9");

    /// <summary>SVG #ic-power</summary>
    public static readonly Geometry Power = Make("M 12,3.5 L 12,11.5 M 7.2,6.4 A 7.5,7.5 0 1 0 16.8,6.4");

    /// <summary>SVG #ic-home</summary>
    public static readonly Geometry Home = Make("M 4,10.5 L 12,4 L 20,10.5 L 20,20 A 1,1 0 0 1 19,21 L 5,21 A 1,1 0 0 1 4,20 Z M 9.5,21 L 9.5,15 L 14.5,15 L 14.5,21");

    /// <summary>SVG #ic-auto</summary>
    public static readonly Geometry Auto = Make("M 3.4,12 A 8.6,8.6 0 1 1 20.6,12 A 8.6,8.6 0 1 1 3.4,12 Z M 12,3.4 A 8.6,8.6 0 0 1 12,20.6 Z");

    /// <summary>SVG #ic-bell</summary>
    public static readonly Geometry Bell = Make("M 6,16.5 L 6,11 A 6,6 0 1 1 18,11 L 18,16.5 L 19.4,18.7 L 4.6,18.7 L 6,16.5 Z M 10,20.8 A 2.2,2.2 0 0 0 14,20.8");

    /// <summary>SVG #ic-layout</summary>
    public static readonly Geometry Layout = Make("M 5.2,4 H 18.8 A 2.2,2.2 0 0 1 21,6.2 V 17.8 A 2.2,2.2 0 0 1 18.8,20 H 5.2 A 2.2,2.2 0 0 1 3,17.8 V 6.2 A 2.2,2.2 0 0 1 5.2,4 Z M 9.5,4 L 9.5,20 M 3,10.5 L 9.5,10.5");

    /// <summary>SVG #ic-log</summary>
    public static readonly Geometry Log = Make("M 6.2,3.4 L 13.8,3.4 L 18.2,7.8 L 18.2,20.6 L 6.2,20.6 Z M 13.8,3.4 L 13.8,7.8 L 18.2,7.8 M 9.4,9.4 L 11.4,9.4 M 9.4,12.6 L 14.6,12.6 M 9.4,15.8 L 14.6,15.8");

    /// <summary>SVG #ic-disk</summary>
    public static readonly Geometry Disk = Make("M 4.8,4.5 H 19.2 A 1.8,1.8 0 0 1 21,6.3 V 9.7 A 1.8,1.8 0 0 1 19.2,11.5 H 4.8 A 1.8,1.8 0 0 1 3,9.7 V 6.3 A 1.8,1.8 0 0 1 4.8,4.5 Z M 4.8,12.5 H 19.2 A 1.8,1.8 0 0 1 21,14.3 V 17.7 A 1.8,1.8 0 0 1 19.2,19.5 H 4.8 A 1.8,1.8 0 0 1 3,17.7 V 14.3 A 1.8,1.8 0 0 1 4.8,12.5 Z M 7,8 L 7.01,8 M 7,16 L 7.01,16");

    /// <summary>SVG #ic-battery</summary>
    public static readonly Geometry Battery = Make("M 4.7,7 H 16.8 A 2.2,2.2 0 0 1 19,9.2 V 14.3 A 2.2,2.2 0 0 1 16.8,16.5 H 4.7 A 2.2,2.2 0 0 1 2.5,14.3 V 9.2 A 2.2,2.2 0 0 1 4.7,7 Z M 22,10.8 L 22,13.2 M 12.4,9.4 L 9.8,12.8 L 12.1,12.8 L 11.1,15.6 L 13.9,12 L 11.6,12 Z");

    /// <summary>SVG #ic-net</summary>
    public static readonly Geometry Net = Make("M 2.5,9.2 A 15.5,15.5 0 0 1 21.5,9.2 M 6,12.8 A 10,10 0 0 1 18,12.8 M 9.6,16.2 A 4.6,4.6 0 0 1 14.4,16.2 M 10.9,19 A 1.1,1.1 0 1 1 13.1,19 A 1.1,1.1 0 1 1 10.9,19 Z");

    /// <summary>SVG #ic-ram</summary>
    public static readonly Geometry Ram = Make("M 4.1,7 H 19.9 A 1.6,1.6 0 0 1 21.5,8.6 V 12.9 A 1.6,1.6 0 0 1 19.9,14.5 H 4.1 A 1.6,1.6 0 0 1 2.5,12.9 V 8.6 A 1.6,1.6 0 0 1 4.1,7 Z M 6.5,14.5 L 6.5,17 M 10.5,14.5 L 10.5,17 M 14.5,14.5 L 14.5,17 M 18.5,14.5 L 18.5,17 M 6,11 L 18,11");

    /// <summary>SVG #ic-monitor</summary>
    public static readonly Geometry Monitor = Make("M 4.7,4.5 H 19.3 A 2.2,2.2 0 0 1 21.5,6.7 V 14.8 A 2.2,2.2 0 0 1 19.3,17 H 4.7 A 2.2,2.2 0 0 1 2.5,14.8 V 6.7 A 2.2,2.2 0 0 1 4.7,4.5 Z M 9,20.5 L 15,20.5 M 12,17 L 12,20.5");

    /// <summary>SVG #ic-kb</summary>
    public static readonly Geometry Keyboard = Make("M 4.5,7 H 19.5 A 2,2 0 0 1 21.5,9 V 15 A 2,2 0 0 1 19.5,17 H 4.5 A 2,2 0 0 1 2.5,15 V 9 A 2,2 0 0 1 4.5,7 Z M 6,10.4 L 6.01,10.4 M 9.4,10.4 L 9.41,10.4 M 12.8,10.4 L 12.81,10.4 M 16.2,10.4 L 16.21,10.4 M 7.5,14 L 16.5,14");

    /// <summary>SVG #ic-shield</summary>
    public static readonly Geometry Shield = Make("M 12,3 L 5,5.8 L 5,11.8 C 5,16 7.8,19.2 12,21 C 16.2,19.2 19,16 19,11.8 L 19,5.8 Z M 9.3,12.2 L 11.3,14.2 L 14.7,10.6");

    /// <summary>SVG #ic-sliders</summary>
    public static readonly Geometry Sliders = Make("M 4,7 L 10,7 M 14,7 L 20,7 M 4,12 L 14,12 M 18,12 L 20,12 M 4,17 L 7,17 M 11,17 L 20,17 M 10,7 A 2,2 0 1 1 14,7 A 2,2 0 1 1 10,7 Z M 14,12 A 2,2 0 1 1 18,12 A 2,2 0 1 1 14,12 Z M 7,17 A 2,2 0 1 1 11,17 A 2,2 0 1 1 7,17 Z");

    /// <summary>SVG #ic-copy</summary>
    public static readonly Geometry Copy = Make("M 10.5,8.5 H 18.5 A 2,2 0 0 1 20.5,10.5 V 18.5 A 2,2 0 0 1 18.5,20.5 H 10.5 A 2,2 0 0 1 8.5,18.5 V 10.5 A 2,2 0 0 1 10.5,8.5 Z M 15.5,5.5 L 6.5,5.5 A 2,2 0 0 0 4.5,7.5 L 4.5,16.5");

    /// <summary>SVG #ic-refresh</summary>
    public static readonly Geometry Refresh = Make("M 20,11.5 A 8,8 0 1 0 17.4,17.8 M 20,5.5 L 20,11.5 L 14,11.5");

    /// <summary>SVG #ic-board</summary>
    public static readonly Geometry Board = Make("M 5.2,3 H 18.8 A 2.2,2.2 0 0 1 21,5.2 V 18.8 A 2.2,2.2 0 0 1 18.8,21 H 5.2 A 2.2,2.2 0 0 1 3,18.8 V 5.2 A 2.2,2.2 0 0 1 5.2,3 Z M 7.7,6.5 H 10.8 A 1.2,1.2 0 0 1 12,7.7 V 10.8 A 1.2,1.2 0 0 1 10.8,12 H 7.7 A 1.2,1.2 0 0 1 6.5,10.8 V 7.7 A 1.2,1.2 0 0 1 7.7,6.5 Z M 15.5,7.5 L 18,7.5 M 15.5,10 L 18,10 M 15.5,12.5 L 18,12.5 M 8,15 L 8,18 M 12,15 L 12,18 M 16,15 L 16,18");

    /// <summary>SVG #ic-curve</summary>
    public static readonly Geometry Curve = Make("M 4,4 L 4,20 L 20,20 M 6.5,16.5 C 9.5,16.5 10,9.5 13,9.5 C 16,9.5 17,13.5 18.3,13.5");

    /// <summary>SVG #ic-density</summary>
    public static readonly Geometry Density = Make("M 4,6 L 20,6 M 4,10 L 20,10 M 4,14 L 20,14 M 4,18 L 20,18");

    /// <summary>SVG #ic-eff-steady</summary>
    public static readonly Geometry EffSteady = Make("M 8.4,12 A 3.6,3.6 0 1 1 15.6,12 A 3.6,3.6 0 1 1 8.4,12 Z M 12,2.4 L 12,5.2 M 12,18.8 L 12,21.6 M 2.4,12 L 5.2,12 M 18.8,12 L 21.6,12 M 5.2,5.2 L 7.2,7.2 M 16.8,16.8 L 18.8,18.8 M 18.8,5.2 L 16.8,7.2 M 7.2,16.8 L 5.2,18.8");

    /// <summary>SVG #ic-eff-breath</summary>
    public static readonly Geometry EffBreath = Make("M 2.5,9.4 C 4.9,9.4 5.9,6 8.3,6 C 10.7,6 11.7,9.4 14.1,9.4 C 16.5,9.4 17.5,6 19.9,6 M 2.5,17.4 C 4.9,17.4 5.9,14 8.3,14 C 10.7,14 11.7,17.4 14.1,17.4 C 16.5,17.4 17.5,14 19.9,14");

    /// <summary>SVG #ic-eff-cycle</summary>
    public static readonly Geometry EffCycle = Make("M 4,10.6 A 8,8 0 0 1 17.4,5.7 M 18.2,2.6 L 18.2,6.2 L 14.6,6.2 M 20,13.4 A 8,8 0 0 1 6.6,18.3 M 5.8,21.4 L 5.8,17.8 L 9.4,17.8");

    /// <summary>SVG #ic-eff-wave</summary>
    public static readonly Geometry EffWave = Make("M 2.4,7.6 C 4.3,5.3 6.2,5.3 8.1,7.6 C 10,9.9 11.9,9.9 13.8,7.6 C 15.7,5.3 17.6,5.3 19.5,7.6 M 2.4,12.4 C 4.3,10.1 6.2,10.1 8.1,12.4 C 10,14.7 11.9,14.7 13.8,12.4 C 15.7,10.1 17.6,10.1 19.5,12.4 M 2.4,17.2 C 4.3,14.9 6.2,14.9 8.1,17.2 C 10,19.5 11.9,19.5 13.8,17.2 C 15.7,14.9 17.6,14.9 19.5,17.2");

    /// <summary>导航图标</summary>
    public static readonly Geometry Dash = Make("M 4.6,3 H 8.4 A 1.6,1.6 0 0 1 10,4.6 V 8.4 A 1.6,1.6 0 0 1 8.4,10 H 4.6 A 1.6,1.6 0 0 1 3,8.4 V 4.6 A 1.6,1.6 0 0 1 4.6,3 Z M 15.6,3 H 19.4 A 1.6,1.6 0 0 1 21,4.6 V 8.4 A 1.6,1.6 0 0 1 19.4,10 H 15.6 A 1.6,1.6 0 0 1 14,8.4 V 4.6 A 1.6,1.6 0 0 1 15.6,3 Z M 4.6,14 H 8.4 A 1.6,1.6 0 0 1 10,15.6 V 19.4 A 1.6,1.6 0 0 1 8.4,21 H 4.6 A 1.6,1.6 0 0 1 3,19.4 V 15.6 A 1.6,1.6 0 0 1 4.6,14 Z M 15.6,14 H 19.4 A 1.6,1.6 0 0 1 21,15.6 V 19.4 A 1.6,1.6 0 0 1 19.4,21 H 15.6 A 1.6,1.6 0 0 1 14,19.4 V 15.6 A 1.6,1.6 0 0 1 15.6,14 Z");

    /// <summary>导航「perf」图标</summary>
    public static readonly Geometry Perf = Make("M 13,2 L 4.5,13.5 L 11,13.5 L 10,22 L 18.5,10.5 L 12,10.5 L 13,2 Z");

    /// <summary>导航「sys」图标</summary>
    public static readonly Geometry Sys = Make("M 4,6 L 13,6 M 17,6 L 20,6 M 4,12 L 7,12 M 11,12 L 20,12 M 4,18 L 11,18 M 15,18 L 20,18 M 13,6 A 2,2 0 1 1 17,6 A 2,2 0 1 1 13,6 Z M 7,12 A 2,2 0 1 1 11,12 A 2,2 0 1 1 7,12 Z M 11,18 A 2,2 0 1 1 15,18 A 2,2 0 1 1 11,18 Z");

    /// <summary>导航「set」图标</summary>
    public static readonly Geometry Gear = Make("M 9,12 A 3,3 0 1 1 15,12 A 3,3 0 1 1 9,12 Z M 19.4,15 A 1.65,1.65 0 0 0 19.73,16.82 L 19.79,16.88 A 2,2 0 0 1 16.96,19.71 L 16.9,19.65 A 1.65,1.65 0 0 0 15.08,19.32 A 1.65,1.65 0 0 0 14.08,20.83 L 14.08,21 A 2,2 0 0 1 10.08,21 L 10.08,20.91 A 1.65,1.65 0 0 0 9,19.4 A 1.65,1.65 0 0 0 7.18,19.73 L 7.12,19.79 A 2,2 0 0 1 4.29,16.96 L 4.35,16.9 A 1.65,1.65 0 0 0 4.68,15.08 A 1.65,1.65 0 0 0 3.17,14.08 L 3,14.08 A 2,2 0 0 1 3,10.08 L 3.09,10.08 A 1.65,1.65 0 0 0 4.6,9 A 1.65,1.65 0 0 0 4.27,7.18 L 4.21,7.12 A 2,2 0 0 1 7.04,4.29 L 7.1,4.35 A 1.65,1.65 0 0 0 9,4.6 A 1.65,1.65 0 0 0 10,3.09 L 10,3 A 2,2 0 0 1 14,3 L 14,3.09 A 1.65,1.65 0 0 0 15,4.6 A 1.65,1.65 0 0 0 16.82,4.27 L 16.88,4.21 A 2,2 0 0 1 19.71,7.04 L 19.65,7.1 A 1.65,1.65 0 0 0 19.4,9 A 1.65,1.65 0 0 0 20.91,10 L 21,10 A 2,2 0 0 1 21,14 L 20.91,14 A 1.65,1.65 0 0 0 19.4,15 Z");

    /// <summary>模式「办公」图标</summary>
    public static readonly Geometry ModeOffice = Make("M 4,20 C 4,20 6.5,10.5 20,4 C 20,4 21,16.5 12.5,19.5 C 8.4,20.8 4,20 4,20 Z M 5.5,18.5 C 7,13 10.5,9.5 15,7.5");

    /// <summary>模式「游戏」图标</summary>
    public static readonly Geometry ModeGame = Make("M 7,7.5 H 17 A 5,5 0 0 1 22,12.5 V 12.5 A 5,5 0 0 1 17,17.5 H 7 A 5,5 0 0 1 2,12.5 V 12.5 A 5,5 0 0 1 7,7.5 Z M 7,12.5 L 10,12.5 M 8.5,11 L 8.5,14 M 16,11.5 L 16.01,11.5 M 18,13.5 L 18.01,13.5");

    /// <summary>模式「狂暴」图标</summary>
    public static readonly Geometry ModeBeast = Make("M 13,2 L 4.5,13.5 L 11,13.5 L 10,22 L 18.5,10.5 L 12,10.5 L 13,2 Z");


    private static Geometry Make(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();      // 多个 Path 共用一个实例，冻结后免变更通知
        return geometry;
    }
}
