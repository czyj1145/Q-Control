using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QControl.App.Ui;

/// <summary>
/// 主窗口的「最大化不越界」部分（partial，单独一个文件： Win32 钩子与逐页迁移的改动互不干扰）。
///
/// 要解决的问题：
///   点最大化后窗口不是停在**工作区**上，而是连任务栏一起盖住。实测
///   （2560×1600 @150%，工作区 1707×1019 逻辑像素，任务栏 48px 在底部）：
///      最大化后窗口矩形 = (-7,-7)-(1713,1073)   ← 1720×1080，比工作区多 54px 高
///   于是侧栏最底下那颗「设置」被任务栏吃掉 42/58 px，只剩齿轮顶缘露一线
///   （同排的「白天/黑夜」「关于」、页面内容底边、toast 全都在遭殃范围内）。
///
/// 根因：WindowStyle=None + WindowChrome 的窗口，最大化时系统会问每条路径
///   （最大化按钮 / 双击顶栏 / Win+↑ / 拖到屏幕顶 / Aero Snap）都会走的
///   **WM_GETMINMAXINFO**「该多大、放哪」，默认答案按整块显示器算 ——
///   这是 WPF 无边框窗口的已知行为（官方文档原话：另一个副作用是窗口在最大化时
///   将覆盖 Windows 任务栏）。钩子在这条消息上把答案改成**当前显示器的工作区**
///   （扣掉任务栏），窗口随后严丝合缝落在工作区内，一处钩住覆盖全部最大化路径。
/// </summary>
public partial class MainWindow
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    /// <summary>按**屏幕坐标**（设备像素）找显示器。MonitorFromPoint 的 pt 是结构体值，照传。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hmonitor, ref MONITORINFO info);

    /// <summary>
    /// 光标（**设备像素**）落在哪块显示器上，返回它的工作区（设备像素）。
    /// 拖动还原时要用：窗口必须夹回"光标所在那一块"的工作区，夹错屏会把窗口
    /// 甩到主屏外面去（多屏且副屏在左/上时坐标为负，尤其明显）。
    /// </summary>
    internal static bool WorkAreaUnder(Point devicePoint, out Rect workDevice)
    {
        var monitor = MonitorFromPoint(new POINT { X = (int)devicePoint.X, Y = (int)devicePoint.Y },
                                       MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            workDevice = default;
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            workDevice = default;
            return false;
        }

        workDevice = new Rect(info.rcWork.Left, info.rcWork.Top,
                              info.rcWork.Right - info.rcWork.Left,
                              info.rcWork.Bottom - info.rcWork.Top);
        return true;
    }

    /// <summary>
    /// 装最大化守卫。窗口源就绪后调一次（见 <see cref="OnSourceInitialized"/>）。
    /// </summary>
    private void InstallMaximizeGuard()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        HwndSource.FromHwnd(hwnd).AddHook(MaximizeWndProc);
    }

    /// <summary>
    /// 回 WM_GETMINMAXINFO：最大化矩形 = 当前显示器工作区。
    ///
    /// ⚠️ 必须在钩子里**现算**显示器（MonitorFromWindow），不能缓存：窗口可以拖到
    ///    另一块屏上再最大化，缓存第一次的矩形会让它在第二块屏上越界。
    /// ⚠️ 不按 ResizeBorderThickness 外扩：WindowChrome 的"边框"是假的
    ///    （整窗即客户区，WM_NCCALCSIZE 被它吃掉），外扩多少就把任务栏盖掉多少。
    /// ⚠️ 只改 ptMaxPosition / ptMaxSize，其余字段保持系统算好的值
    ///    （最小跟踪尺寸等），别整块覆盖 MINMAXINFO。
    /// </summary>
    private IntPtr MaximizeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        // ptMaxPosition 是相对显示器左上角的偏移；工作区在哪，最大化窗口的左上就在哪
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(mmi, lParam, false);

        handled = true;
        return IntPtr.Zero;
    }
}
