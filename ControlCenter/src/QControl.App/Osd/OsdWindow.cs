using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QControl.App.Osd;

/// <summary>
/// OSD 提示条窗口（Windows 分层窗口 · 屏幕左上角卡片）。
///
/// 为什么不用 WPF/WinForms 窗体：
///   ① 必须**每像素 alpha**（圆角 + 淡入淡出），WinForms 的 Opacity 是整窗统一透明度，
///      TransparencyKey 会在抗锯齿边缘留一圈硬边；
///   ② 必须不抢焦点、鼠标穿透、不进任务栏/Alt+Tab —— 这些只有
///      `WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW` 组合能做到；
///   ③ 用 `UpdateLayeredWindow` 后可以只把一张画好的位图糊上去，动画只改
///      目标位置和 blend 参数，**不重绘**。
///
/// 线程模型：窗口活在自己的 STA 线程 + 自己的消息循环里（窗口必须有消息循环才会
/// 处理计时器和 PostMessage）。外部 `Show()` 随时可调：把内容塞进"最新一条"槽位，
/// 再 `PostMessage(WM_APP_SHOW)` 唤醒窗口线程。
///
/// ⚠️ 队列语义与早先实现一致：**只保留最后一条**，来不及显示的旧条目直接丢弃
///    （用户 2026-10-13 明确要求"只显示最后一个"）。
/// ⚠️ 配色跟随界面主题（2026-10-24，用户报「OSD 外观不会跟随白天黑夜模式」）：
///    暗/亮两套见 <see cref="OsdPalette"/>，每次弹出时经 <see cref="ThemeToken"/> 现读 token。
/// ⚠️ 动画全部按**时间**推进，不按帧数 —— SetTimer 实际被系统对齐到 15.6ms 的
///    整数倍，按帧算 alpha 会忽快忽慢。
/// </summary>
internal sealed class OsdWindow : IDisposable
{
    // ---------------- 动画（秒）----------------
    private const double FadeIn = 0.11;
    private const double Hold = 1.05;
    private const double FadeOut = 0.22;
    private const int SlideX = 14;          // 滑入距离（逻辑像素）
    private const int TickMs = 14;

    // ---------------- Win32 ----------------
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;

    private const int WmTimer = 0x0113;
    private const int WmDestroy = 0x0002;
    private const int WmAppShow = 0x8000 + 0x51;
    private const int WmAppQuit = 0x8000 + 0x52;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int UlwAlpha = 0x00000002;

    /// <summary>定时器 ID。窗口只有一个计时器，随便取个数。</summary>
    private const int TimerId = 1;

    private readonly object _lock = new();
    private OsdSpec? _pending;              // 最新一条待显示内容（只留最后一条）
    private IntPtr _hwnd;
    private Thread? _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private string? _lastError;
    private bool _available;

    // 窗口线程独占的状态
    private WndProcDelegate? _wndProc;      // 必须存字段，否则委托被 GC 后窗口回调就野了
    private CardBitmap? _bitmap;            // 已渲染的卡片（预乘 BGRA）
    private OsdPalette _lastPalette = OsdPalette.Dark;   // 上次成功解析到的配色（读配置翻车时沿用）
    private float _scale = 1f;
    private int _cardW;
    private int _cardH;
    private Stopwatch _clock = new();
    private bool _showing;

    /// <summary>是否显示提示条（对应 `presets.ui.osd`）。关掉后 Show() 直接返回 false。</summary>
    public volatile bool Enabled = true;

    /// <summary>
    /// 现读 <c>presets.ui.theme</c> 的委托（**每次弹出时读一次**，见 <see cref="TakePending"/>）。
    /// auto 的"跟随系统"由 <see cref="OsdPalette.Resolve"/> 当场算。
    ///
    /// 与托盘菜单同一套路子（<c>TrayIcon</c> 的 <c>_themeToken</c>）：不缓存、不推送 ——
    /// 切完主题立刻按 FN，出来的就是新色。空 = 暗色（与界面默认一致）。
    /// </summary>
    public Func<string?>? ThemeToken { get; set; }

    /// <summary>窗口是否成功建立（建不起来时 Show() 一律 false，不影响后端其余部分）。</summary>
    public bool Available => _available;

    public string? LastError => _lastError;

    // ---------------------------------------------------------------- 生命周期

    public OsdWindow()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "osd",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 建窗口很快；3 秒等不到就认为这台机器上起不来（无交互桌面等）。
        // ⚠️ 不抛异常：OSD 是锦上添花，绝不能因为它把后端拖死。
        _ready.Wait(3000);
        if (_hwnd == IntPtr.Zero) _available = false;
    }

    private void ThreadMain()
    {
        try
        {
            _scale = DpiScale();
            _wndProc = WndProc;

            var hInstance = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = "QControlOsd",
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                // 1410 = 类已注册（同一进程第二次创建 OSD）—— 复用即可，不算失败
                var err = Marshal.GetLastWin32Error();
                if (err != 1410)
                {
                    _lastError = "RegisterClassEx 失败：" + err;
                    _ready.Set();
                    return;
                }
            }

            _hwnd = CreateWindowEx(
                WsExLayered | WsExTopmost | WsExToolWindow | WsExTransparent | WsExNoActivate,
                wc.lpszClassName, "OSD", WsPopup,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _lastError = "CreateWindowEx 失败：" + Marshal.GetLastWin32Error();
                _ready.Set();
                return;
            }

            _available = true;
            _ready.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception e)
        {
            _lastError = e.Message;
            _ready.Set();
        }
        finally
        {
            _bitmap = null;
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try { PostMessage(_hwnd, WmAppQuit, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        // ⚠️ 只有**确认线程已经退出**才释放 `_ready`：Join 超时说明线程还在跑，
        //    它随后那一句 `_ready.Set()`（正常路径与 catch 分支都有）会抛
        //    ObjectDisposedException，而 catch 里又 Set 一次 ⇒ 异常逃出 ThreadMain
        //    ⇒ 后台线程未处理异常会**终止整个进程**（表现就是"退出时偶发闪退"）。
        //    没退出就不释放，交给 GC。
        var joined = false;
        try { joined = _thread?.Join(1000) ?? true; } catch { }
        if (joined)
        {
            try { _ready.Dispose(); } catch { }

            // ⚠️ 顺手把"还活着"的对外状态清掉：窗口线程销毁 hwnd 之后既没清 `_hwnd`
            //    也没清 `_available`，于是 Dispose 之后的任何 `Show()` 都能通过 195 的守卫，
            //    把 WmAppShow 投给一个死句柄，然后 **return true** —— 调用方与 HTTP
            //    都以为弹了。命中率低（只在退出/重建 AppState 之后），但返回值是假的。
            _available = false;
            _hwnd = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- 对外

    /// <summary>
    /// 弹一条提示。任意线程可调。
    /// 返回 false = 没弹（osd 关着 / 窗口起不来 / 内容为空）。
    /// </summary>
    public bool Show(string kind, string title, string subtitle)
    {
        if (!Enabled || !_available || _hwnd == IntPtr.Zero) return false;
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(subtitle)) return false;

        lock (_lock) _pending = new OsdSpec(kind ?? "", title ?? "", subtitle ?? "");
        PostMessage(_hwnd, WmAppShow, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    // ---------------------------------------------------------------- 窗口过程

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmAppShow:
                TakePending();
                return IntPtr.Zero;

            case WmTimer:
                Step();
                return IntPtr.Zero;

            case WmAppQuit:
                KillTimer(hWnd, TimerId);
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void TakePending()
    {
        OsdSpec? spec;
        lock (_lock)
        {
            spec = _pending;
            _pending = null;
        }
        if (spec is null) return;

        try
        {
            var card = OsdCard.Render(spec, _scale, ResolvePalette());
            _bitmap = card;
            _cardW = card.W;
            _cardH = card.H;
            _clock = Stopwatch.StartNew();
            _showing = true;
            ShowWindow(_hwnd, SwShowNoActivate);
            SetTimer(_hwnd, TimerId, TickMs, IntPtr.Zero);
            Paint(1.0, 0);          // 立刻画第一帧，别等第一个 tick
        }
        catch (Exception e)
        {
            _lastError = "渲染失败：" + e.Message;
            Hide();
        }
    }

    /// <summary>
    /// 此刻该用哪套配色。**每次弹出都现算**（读一次 model.json 的 token + 需要时读注册表），
    /// 代价是一毫秒级；换来的是"切完主题立刻生效"。
    ///
    /// 读配置翻车时**沿用上次那套**而不是退回暗色 —— 退回暗色等于在浅色模式下闪一块黑卡片，
    /// 正是这次要修的那个症状。
    /// </summary>
    private OsdPalette ResolvePalette()
    {
        try
        {
            _lastPalette = OsdPalette.Resolve(ThemeToken?.Invoke());
        }
        catch
        {
            // 保持 _lastPalette
        }
        return _lastPalette;
    }

    private void Step()
    {
        if (!_showing) return;

        var t = _clock.Elapsed.TotalSeconds;
        if (t < FadeIn)
        {
            var p = t / FadeIn;
            Paint(p, -(1 - p) * SlideX);
        }
        else if (t < FadeIn + Hold)
        {
            Paint(1.0, 0);
        }
        else if (t < FadeIn + Hold + FadeOut)
        {
            var p = (t - FadeIn - Hold) / FadeOut;
            Paint(1 - p, 0);
        }
        else
        {
            Hide();
        }
    }

    private void Hide()
    {
        _showing = false;
        KillTimer(_hwnd, TimerId);
        ShowWindow(_hwnd, SwHide);
    }

    /// <summary>把当前位图按 alpha / 横向偏移贴上去。位图不重算，只改位置和 blend。</summary>
    private void Paint(double alpha, double offsetLogical)
    {
        if (_bitmap is null || _hwnd == IntPtr.Zero) return;

        var a = (byte)Math.Clamp(alpha * 255, 0, 255);
        var offset = (int)Math.Round(offsetLogical * _scale);

        // 工作区左上角 + 边距（多屏时取主屏）
        var (left, top) = WorkAreaOrigin();
        var ptDst = new POINTINT
        {
            x = left + (int)Math.Round(OsdCard.Margin * _scale) + offset,
            y = top + (int)Math.Round(OsdCard.Margin * _scale),
        };
        var size = new SIZEINT { cx = _cardW, cy = _cardH };
        var ptSrc = new POINTINT { x = 0, y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0,                 // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = a,
            AlphaFormat = 1,             // AC_SRC_ALPHA
        };

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        // 卡片已经是现成的**预乘** BGRA（SoftCanvas 全程按预乘混的），
        // CreateBitmap 直接建 32bpp 位图即可 —— 正是 UpdateLayeredWindow 要的格式。
        var hbmp = CreateBitmap(_cardW, _cardH, 1, 32, _bitmap.PremulBgra);
        if (hbmp == IntPtr.Zero)
        {
            _lastError = "CreateBitmap 失败：" + Marshal.GetLastWin32Error();
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }
        var old = SelectObject(memDc, hbmp);
        try
        {
            if (!UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc,
                                     ref ptSrc, 0, ref blend, UlwAlpha))
            {
                _lastError = "UpdateLayeredWindow 失败：" + Marshal.GetLastWin32Error();
            }
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hbmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>工作区原点（物理像素）。任务栏停在上/左时也能贴对位置。</summary>
    private static (int Left, int Top) WorkAreaOrigin()
    {
        var r = new RECTINT();
        if (SystemParametersInfo(0x0030, 0, ref r, 0)) return (r.left, r.top);  // SPI_GETWORKAREA
        return (0, 0);
    }

    private static float DpiScale()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi > 0 ? dpi / 96f : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZEINT { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTINT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINTINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, int id, int ms, IntPtr proc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr dstDc, ref POINTINT dst,
        ref SIZEINT size, IntPtr srcDc, ref POINTINT src, int colorKey, ref BLENDFUNCTION blend, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref RECTINT rect, uint winIni);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes,
                                              uint nBitCount, byte[]? lpBits);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
