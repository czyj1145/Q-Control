using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QControl.App.Ui;

/// <summary>
/// 给主窗口补回 <c>WS_CAPTION</c>，让 Windows 自己的收栏动画能播。
///
/// 为什么需要这个（2026-11-06 实测出来的）：
///   `WindowStyle=None` 的窗口 WPF 会去掉 `WS_CAPTION`（它是 `WS_BORDER | WS_DLGFRAME`）。
///   **DWM 的收栏动画依赖窗口有非客户区**，没有 caption 就不播 —— 现象是点最小化，
///   窗口内容 10ms 就没了，一点过渡都没有。
///   对照实验（同一台机器、同一套采样）：记事本窗口中心点保持 170ms 才消失，
///   Q-Control 只有 10ms。差别只在样式，不在系统设置（`MinAnimate=1`）。
///
/// 所以：补回 caption，然后什么都不用做 —— 系统动画自己就出来了（GPU 合成，零成本）。
///
/// ⚠️ 不能直接 `SetWindowLongPtr`：那会触发 `WM_STYLECHANGING`，WPF 的 `HwndStyleManager`
///    会按 `WindowStyle` 属性把 caption 再算掉，实测设完立刻读，值一个 bit 都没变。
///    必须挂窗口过程钩子，在这条消息上把 caption 位补回去并 `handled = true`。
/// ⚠️ 钩子只拦 `GWL_STYLE` 那一路的 `WM_STYLECHANGING`，而且**只在缺 caption 时**才吃消息；
///    别无条件吞 —— 最大化/还原、DPI 变化都依赖这条消息，吞多了会出怪事。
/// ⚠️ 加了 caption 不影响本程序的外观：`WindowChrome` 的 `CaptionHeight=0` +
///    `GlassFrameThickness=0` 会在 `WM_NCCALCSIZE` 里把整窗当客户区，系统那条标题栏不会被画出来。
/// </summary>
public partial class MainWindow
{
    private const int WM_STYLECHANGING = 0x007C;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STYLESTRUCT { public int styleOld; public int styleNew; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>窗口源就绪后调一次（见 MainWindow.xaml.cs 的 OnSourceInitialized）。</summary>
    private void InstallCaptionGuard()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 先装钩子再改样式：改样式会同步发 WM_STYLECHANGING，钩子必须已经在位
        HwndSource.FromHwnd(hwnd)?.AddHook(KeepCaptionStyle);

        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        if ((style & WS_CAPTION) != WS_CAPTION)
            SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)(style | WS_CAPTION));
    }

    /// <summary>
    /// 把 caption 位补回即将生效的样式里，并吃掉这条消息 —— 不让 WPF 按 `WindowStyle`
    /// 再算掉一次。只处理缺 caption 的情况，其余一律放行。
    /// </summary>
    private IntPtr KeepCaptionStyle(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_STYLECHANGING || wParam.ToInt64() != GWL_STYLE) return IntPtr.Zero;

        var ss = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
        if (((uint)ss.styleNew & WS_CAPTION) == WS_CAPTION) return IntPtr.Zero;

        ss.styleNew |= (int)WS_CAPTION;
        Marshal.StructureToPtr(ss, lParam, false);

        handled = true;
        return IntPtr.Zero;
    }
}
