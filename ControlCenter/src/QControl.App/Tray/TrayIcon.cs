using System.Runtime.InteropServices;
using QControl.App.Render;

namespace QControl.App.Tray;

/// <summary>
/// 系统托盘图标 + 右键菜单（纯 Win32，不依赖 WinForms 的 NotifyIcon）。
///
/// 菜单四项（不多不少，免得同一个软件两个长相）：
///   · 打开控制中心   ← 左键点图标也是这一项
///   · 锁定 Win 键（当前：开/关）   ← 打游戏时随手切，不用开界面
///   · 开机自启（当前：开/关）
///   · 退出
///
/// 菜单本身是**自绘**的（<see cref="TrayMenu"/>），配色跟 App 的暗/亮设置走 ——
/// 原生 <c>TrackPopupMenu</c> 只能跟系统主题，跟不了本软件的三态设置。
///
/// ⚠️ 为什么不用 WinForms 的 <c>NotifyIcon</c>：那一下会把整个
///    <c>Microsoft.WindowsDesktop.App</c> 框架拖进进程（工作集 +7 MB 起步，
///    还要多养一条 STA 消息循环线程）。这里自己发 <c>Shell_NotifyIcon</c>、
///    自己画菜单，功能一样，运行时零额外框架。
///
/// ⚠️ 退出顺序是生死线（2026-10-14 早先实现踩过）：**先 <c>NIM_DELETE</c>
///    再让进程走**。反过来的话进程先死，图标会变成"幽灵"，鼠标划过才消失。
///    所以"退出"只做三件事：摘图标 → 结束消息循环 → 在**别的线程**跑真正的
///    清理（清理里有几百毫秒的风扇交回，不能占着窗口线程）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string Caption = "Q-Control 控制中心";

    // ---- 消息 ----
    private const uint WmApp = 0x8000;
    private const uint WmTray = WmApp + 0x61;      // 托盘回调（uCallbackMessage）
    private const uint WmQuit = WmApp + 0x62;      // 外部请求退出（摘图标 + 收线程）
    private const uint WmShowMenu = WmApp + 0x63;  // 弹右键菜单（**异步**，见 WndProc 注释）
    private const uint WmOpen = WmApp + 0x64;      // 打开界面（同上：不占着 explorer 的调用）
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmDestroy = 0x0002;
    private const int IdcArrow = 32512;

    // ---- Shell_NotifyIcon ----
    private const int NimAdd = 0x0000;
    private const int NimModify = 0x0001;
    private const int NimDelete = 0x0002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifWarning = 0x00000002;

    private readonly Action _onOpen;
    private readonly Action _onQuit;
    private readonly Func<bool, (bool Ok, string Detail)> _setAutostart;
    /// <summary>Win 键锁的**实际**状态（钩子装着没有），不是配置里写的值。</summary>
    private readonly Func<bool> _winKeyLocked;
    private readonly Func<bool, (bool Ok, string Detail)> _setWinKeyLock;
    private readonly Func<string?> _themeToken;
    private readonly TrayMenu _menu = new();

    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _hicon;

    /// <summary>
    /// explorer 重启后广播的 "TaskbarCreated" 消息 ID（运行期注册，不是常量）。
    ///
    /// 为什么必须处理：通知区域的图标是 **explorer 持有**的，它一重启（崩溃、更新、
    /// 任务管理器结束进程）所有图标就全没了，而我们的 <c>NIM_ADD</c> 记录也跟着失效。
    /// 系统在这种时候会给所有顶层窗口广播这条消息，收到就得重新注册一次 ——
    /// 不处理的话托盘图标会**永久消失**，直到程序重启（2026-11-06 用户报"托盘图标消失了"
    /// 就是这个：代码里从来没有这条消息）。
    ///
    /// ⚠️ 注册失败返回 0，此时 `msg == 0` 永不成立（窗口消息不会是 0），逻辑自然失效但不炸。
    /// </summary>
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _closing;
    private string? _error;

    private TrayIcon(Action onOpen, Action onQuit,
                     Func<bool, (bool Ok, string Detail)> setAutostart,
                     Func<bool> winKeyLocked,
                     Func<bool, (bool Ok, string Detail)> setWinKeyLock,
                     Func<string?> themeToken)
    {
        _onOpen = onOpen;
        _onQuit = onQuit;
        _setAutostart = setAutostart;
        _winKeyLocked = winKeyLocked;
        _setWinKeyLock = setWinKeyLock;
        _themeToken = themeToken;
    }

    // ---------------------------------------------------------------- 对外

    /// <summary>
    /// 进程级 DPI 感知。**必须在任何窗口创建之前调**（Program.Main 第一件事）。
    ///
    /// PerMonitorV2 = WinForms <c>SetHighDpiMode</c> 的同档；已被设过（清单 /
    /// 老系统不支持）就静默失败，不影响功能。
    /// </summary>
    public static void InitDpiMode()
    {
        try { SetProcessDpiAwarenessContext(PerMonitorAwareV2); } catch { /* 同上 */ }
    }

    /// <summary>
    /// 起托盘。单开一条后台线程建隐藏窗口 + 消息循环（主线程在 <c>quit.Wait()</c>
    /// 上等着）。返回 null = 起不来（无交互桌面 / 远程会话 / 窗口类异常）——
    /// 调用方记一条警告继续跑，**绝不因为托盘失败就不开后端**。
    ///
    /// <paramref name="themeToken"/> = 现读 <c>presets.ui.theme</c> 的委托（每次弹菜单才读，
    /// 切完主题立刻生效，不需要谁推送）。读失败/给 null 都算暗色。
    /// </summary>
    public static TrayIcon? Start(Action onOpen, Action onQuit,
                                  Func<bool, (bool Ok, string Detail)> setAutostart,
                                  Func<bool> winKeyLocked,
                                  Func<bool, (bool Ok, string Detail)> setWinKeyLock,
                                  Func<string?> themeToken,
                                  out string? error)
    {
        TrayIcon? created = null;
        string? failure = null;
        // ⚠️ **不要 `using`**：托盘线程可能在 3 秒超时之后才走到 `ready.Set()`，
        //    而 `using` 早就把它 Dispose 了 ⇒ Set 抛 ObjectDisposedException。
        //    更糟的是 catch 分支里还有第二次 `ready.Set()`（同样抛），异常会逃出线程 lambda，
        //    而 .NET 后台线程未处理异常 = **整个进程结束**（表现：启动时偶发闪退）。
        //    一个 ManualResetEventSlim 只创建一次，不释放不构成泄漏。
        var ready = new ManualResetEventSlim(false);
        var abandon = 0;          // 1 = 主线程已放弃等待（超时），线程起来后自己收摊

        var thread = new Thread(() =>
        {
            try
            {
                created = new TrayIcon(onOpen, onQuit, setAutostart, winKeyLocked, setWinKeyLock, themeToken);
                if (!created.CreateWindowAndIcon())
                {
                    // 建不起来：把原因带出去，线程直接结束（没有消息循环要收）
                    failure = created._error ?? "托盘窗口创建失败";
                    created = null;
                    ready.Set();
                    return;
                }
                ready.Set();

                // 主线程 3 秒没等到就放弃了（见下面的 Wait）：那时它已经返回 null，
                // 没人能再调 Shutdown ⇒ 这里自己摘图标退出，否则留下一个幽灵图标
                // 和一条永远不会结束的消息循环。
                if (Volatile.Read(ref abandon) == 1) return;

                // 消息循环：跑到 WmQuit / DestroyWindow 为止
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception e)
            {
                failure ??= e.Message;
                // 兜底：即使 ready 已被别人释放，也不许把异常漏出线程（那会杀进程）
                try { ready.Set(); } catch (ObjectDisposedException) { }
            }
            finally
            {
                // 任何路径都不留幽灵图标：能摘就再摘一次（幂等）
                if (created is not null)
                {
                    created.RemoveIcon();
                    created._menu.Dispose();      // 菜单窗口归属这条线程，只能在这里销毁
                }
            }
        })
        {
            IsBackground = true,
            Name = "tray",
        };
        thread.Start();

        // 窗口 + 图标创建很快；3 秒等不到就说明卡住了，不能把启动流程一起拖死
        ready.Wait(3000);
        error = failure;
        if (created is null && failure is null)
        {
            error = "托盘线程 3 秒内未就绪";
            Volatile.Write(ref abandon, 1);      // 让线程起来后自己摘图标退出
        }
        if (created is not null) created._thread = thread;
        return created;
    }

    /// <summary>
    /// 从**任意线程**摘掉图标并结束消息循环，等线程收尾（最多 <paramref name="timeoutMs"/>）。
    /// 退出流程（Ctrl+C / POST /api/app/quit / 关控制台）都必须先调它，
    /// 再让进程退出 —— 否则托盘会留下一个"鼠标划过才消失"的幽灵图标。
    /// </summary>
    public void Shutdown(int timeoutMs = 2000)
    {
        if (!_closing)
        {
            _closing = true;
            if (_hwnd != IntPtr.Zero)
            {
                try { PostMessage(_hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero); } catch { }
            }
        }

        try { _thread?.Join(timeoutMs); } catch { }

        // 极端情况（窗口线程卡死）：进程还是要走，图标不能留着
        RemoveIcon();
    }

    public void Dispose() => Shutdown();

    // ---------------------------------------------------------------- 窗口与图标

    private bool CreateWindowAndIcon()
    {
        var hInstance = GetModuleHandle(null);

        _wndProc = WndProc;   // 必须存字段：委托被 GC 后窗口回调就野了
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursor(IntPtr.Zero, IdcArrow),
            lpszClassName = "QControlTray",
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            // 1410 = 类已注册（同进程第二次建托盘）—— 复用即可，不算失败
            if (err != 1410)
            {
                _error = "RegisterClassEx 失败：" + err;
                return false;
            }
        }

        // 普通隐藏顶层窗口（**不是** message-only）：TrackPopupMenu 需要
        // SetForegroundWindow 才正常消失，message-only 窗口做不了前台。
        _hwnd = CreateWindowEx(0, wc.lpszClassName, Caption, WsPopup,
                               0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            _error = "CreateWindowEx 失败：" + Marshal.GetLastWin32Error();
            return false;
        }

        _hicon = BuildGlyphIcon();
        if (_hicon == IntPtr.Zero)
        {
            _error = "图标创建失败：" + Marshal.GetLastWin32Error();
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            return false;
        }

        if (!AddIcon())
        {
            _error = "Shell_NotifyIcon(NIM_ADD) 失败：" + Marshal.GetLastWin32Error();
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            return false;
        }
        return true;
    }

    /// <summary>
    /// 往通知区域注册图标。<see cref="Show"/> 第一次注册、以及 explorer 重启后
    /// 收到 <c>TaskbarCreated</c> 时重新注册，都走这里。
    /// </summary>
    private bool AddIcon()
    {
        if (_hwnd == IntPtr.Zero || _hicon == IntPtr.Zero) return false;

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmTray,
            hIcon = _hicon,
            szTip = Caption,        // 托盘提示上限 63 字符，远小于 128 的缓冲
        };
        return Shell_NotifyIcon(NimAdd, ref data);
    }

    /// <summary>摘图标（幂等；线程退出前与 Shutdown 兜底都会调）。</summary>
    private void RemoveIcon()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIcon(NimDelete, ref data);
        }
        catch { /* 进程正在死，摘不掉也只能算了 */ }

        if (_hicon != IntPtr.Zero)
        {
            try { DestroyIcon(_hicon); } catch { }
            _hicon = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- 窗口过程

    private WndProcDelegate? _wndProc;

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // explorer 重启了：它手里的图标全没了，重新注册一遍（见 TaskbarCreated 的注释）。
        // 必须放在 switch 之前 —— 这个 ID 是运行期注册的，没法写成 case 常量。
        if (msg == TaskbarCreated)
        {
            _ = AddIcon();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmTray:
            {
                // ⚠️ 这条消息是 **explorer 同步发来的**（Shell_NotifyIcon 的回调走
                // SendMessage）。在这里跑任何会等用户/耗时的事（弹模态菜单、开界面窗口）
                // 等于把 explorer 的窗口线程按住不放：超过几秒 Windows 就把它判成"无响应"，
                // 挂出转圈光标；而自绘菜单窗口若不自己设光标，那个圈会一路留在菜单上
                // （2026-10-23）。所以这里只丢一张纸条给自己，立刻返回，真正的活儿
                // 在下面两条消息里干 —— 与 TrackPopupMenu 时代"先 PostMessage 再弹"同款做法。
                var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouse == WmLButtonUp) PostMessage(hWnd, WmOpen, IntPtr.Zero, IntPtr.Zero);
                else if (mouse == WmRButtonUp) PostMessage(hWnd, WmShowMenu, IntPtr.Zero, IntPtr.Zero);
                return IntPtr.Zero;
            }

            case WmShowMenu:
                try { ShowMenu(); } catch { /* 弹菜单失败不能带走托盘线程 */ }
                return IntPtr.Zero;

            case WmOpen:
                try { _onOpen(); } catch { /* 同上 */ }
                return IntPtr.Zero;

            case WmQuit:
                // 先摘图标再收窗口（顺序见类注释）
                RemoveIcon();
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ---------------------------------------------------------------- 菜单

    /// <summary>菜单元文本。每次点开菜单前重算，所以不会显示过期状态。</summary>
    private static string AutoText() => "开机自启（当前：" + (ReadAuto() ? "开" : "关") + "）";

    private static bool ReadAuto()
    {
        try { return Hal.Sys.Autostart.IsOn(); }
        catch { return false; }
    }

    /// <summary>同理：读**钩子实际状态**（装失败时配置可能是开着，但那不叫锁上）。</summary>
    private bool ReadWinLock()
    {
        try { return _winKeyLocked(); }
        catch { return false; }
    }

    private string WinLockText() => "锁定 Win 键（当前：" + (ReadWinLock() ? "开" : "关") + "）";

    /// <summary>此刻该用哪套菜单配色（暗/亮）。读失败一律当暗色，不让它耽误弹菜单。</summary>
    private TrayPalette CurrentPalette()
    {
        try { return TrayPalette.Resolve(_themeToken()); }
        catch { return TrayPalette.Dark; }
    }

    private void ShowMenu()
    {
        if (_hwnd == IntPtr.Zero) return;

        var items = new List<TrayMenuItem>
        {
            new("打开控制中心", TrayCommand.Open),
            TrayMenuItem.Sep,
            new(WinLockText(), TrayCommand.WinKeyLock),
            new(AutoText(), TrayCommand.Autostart),
            TrayMenuItem.Sep,
            new("退出", TrayCommand.Quit),
        };

        // 阻塞到用户选完（内部是嵌套消息循环，语义与 TrackPopupMenu 相同）
        var cmd = _menu.Show(_hwnd, items, CurrentPalette());
        switch (cmd)
        {
            case TrayCommand.Open: _onOpen(); break;
            case TrayCommand.WinKeyLock: ToggleWinKeyLock(); break;
            case TrayCommand.Autostart: ToggleAutostart(); break;
            case TrayCommand.Quit: Quit(); break;
        }
    }

    /// <summary>
    /// 锁定 / 解锁 Win 键。菜单里点一下就要有回音：成功弹一条"现在按 Win 没反应"，
    /// 失败说清原因（钩子装不上通常是安全软件在挡）。
    /// </summary>
    private void ToggleWinKeyLock()
    {
        var want = !ReadWinLock();
        var (ok, detail) = _setWinKeyLock(want);
        ShowBalloon(ok ? (want ? "Win 键已锁定" : "Win 键已解锁") : "Win 键切换失败",
                    detail, ok);
    }

    private void ToggleAutostart()
    {
        var now = !ReadAuto();
        var (ok, detail) = _setAutostart(now);
        // 失败必须让用户看见：托盘菜单里点一下没反应是最糟的反馈。
        // 成功也给一条 —— 用户需要知道"下次开机会自己起来"这件事已经成立。
        ShowBalloon(ok ? (now ? "开机自启已开启" : "开机自启已关闭") : "开机自启设置失败",
                    detail, ok);
    }

    /// <summary>弹一条系统通知（托盘气泡）。失败/被关掉通知中心都不影响菜单。</summary>
    private void ShowBalloon(string title, string? text, bool ok)
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NifInfo,
                szInfo = text ?? "",
                szInfoTitle = title,
                dwInfoFlags = ok ? NiifInfo : NiifWarning,
                uTimeout = 4000,     // v4+ 已忽略该字段，留给老系统
            };
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch { /* 通知中心被关掉时不让它影响菜单 */ }
    }

    /// <summary>
    /// 退出。**必须是这个顺序**：先摘图标，再结束消息循环，最后在后台跑清理。
    /// 清理（风扇交回 + 关监听）要几百毫秒，占着窗口线程会让托盘卡住。
    /// </summary>
    private void Quit()
    {
        if (_closing) return;
        _closing = true;

        RemoveIcon();
        if (_hwnd != IntPtr.Zero)
        {
            try { PostQuitMessage(0); } catch { }
        }

        _ = Task.Run(() =>
        {
            try { _onQuit(); } catch { /* 退出路径不抛 */ }
        });
    }

    // ---------------------------------------------------------------- 图标

    /// <summary>
    /// 画图标：渐变圆环 + 深底 + **Q 字形**（2026-10-23 用户要求把五角星换掉）。
    ///
    /// 自绘而不是带 .ico 资源：颜色跟界面主色一致，改主题色时不用再导一次图。
    /// 32×32 交给外壳缩放到托盘尺寸；软件光栅化出预乘 BGRA 后建位图 + 图标。
    ///
    /// ⚠️ 字形几何与 `spike/_gen_app_icon.py`、`Ui/MainWindow.xaml` 的侧栏 logo **同源**
    ///    （设计空间 16×16：碗心 (8,8)、中心线半径 5.6、描边 2.8；尾巴从 (10.263,10.263)
    ///    画到 (15.2,15.2)、宽 2.5、沿 45° 从碗心里穿出去）。三处任一改动都要同步，
    ///    否则托盘 / 侧栏 / exe 三个图标会长得不一样。
    ///    **别把尾巴起点放在中心线上** —— 那样会被读成放大镜手柄。
    /// </summary>
    private static IntPtr BuildGlyphIcon()
    {
        const int n = 32;
        var c = new SoftCanvas(n, n);
        var cx = n / 2f;
        var cy = n / 2f;

        // 渐变圆环（上 #4DD6FF → 下 #7C5CFF，与旧 GDI+ 渐变刷同向）
        c.FillEllipseGradient(new PointF(cx, cy), n / 2f - 0.5f,
                              (77, 214, 255, 255), (124, 92, 255, 255));

        // 深色内圆
        c.FillEllipse(new PointF(cx, cy), n / 2f - 2.4f, 16, 20, 28, 255);

        // Q 字形：设计单位 → 像素（k=1.13 ⇒ 墨迹约 17.5px，与原来那颗星的分量相当）
        const float k = 1.13f;
        const float bowlC = 8f;        // 碗心（设计）
        const float bowlR = 5.6f;      // 碗的中心线半径（设计）
        const float bowlW = 2.8f;      // 碗的描边（设计）
        const float tailW = 2.5f;      // 尾巴宽度（设计，比碗细一档）
        // ⚠️ 下面两个是**坐标偏移**（碗心 → 端点，x 与 y 同值），不是半径。
        //    生成脚本里尾巴写的是两个点 (10.263,10.263) → (15.2,15.2)，
        //    偏移量 = 这两个数减碗心 8；再乘 √2 当"半径"用就把尾巴缩回碗里了（踩过）。
        const float tailOff0 = 2.263f; // = 3.2 × 0.7071
        const float tailOff1 = 7.2f;   // = 15.2 − 8
        const float inkC = 8.725f;     // 墨迹中心（设计）——见生成脚本里的推导
        const float inkShift = n / 2f - inkC * k;   // 让墨迹落在 32×32 的正中

        var gx = inkShift + bowlC * k;
        var gy = gx;
        c.StrokeEllipse(new PointF(gx, gy), bowlR * k, bowlW * k, 77, 214, 255, 255);
        c.StrokeLine(new PointF(gx + tailOff0 * k, gy + tailOff0 * k),
                     new PointF(gx + tailOff1 * k, gy + tailOff1 * k),
                     tailW * k, 77, 214, 255, 255);

        // 预乘 BGRA → 32bpp 位图；配一张全 0 的 1bpp 掩码（alpha 通道说了算）
        var hbmpColor = CreateBitmap(n, n, 1, 32, c.Buffer);
        var mask = new byte[(n + 7) / 8 * n];   // 全 0 = 全透明（由 alpha 覆盖）
        var hbmpMask = CreateBitmap(n, n, 1, 1, mask);
        if (hbmpColor == IntPtr.Zero || hbmpMask == IntPtr.Zero)
        {
            if (hbmpColor != IntPtr.Zero) DeleteObject(hbmpColor);
            if (hbmpMask != IntPtr.Zero) DeleteObject(hbmpMask);
            return IntPtr.Zero;
        }

        var info = new IconInfo
        {
            fIcon = true,
            xHotspot = 0,
            yHotspot = 0,
            hbmMask = hbmpMask,
            hbmColor = hbmpColor,
        };
        var hicon = CreateIconIndirect(ref info);
        DeleteObject(hbmpColor);
        DeleteObject(hbmpMask);
        return hicon;
    }

    // ---------------------------------------------------------------- P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int WsPopup = unchecked((int)0x80000000);

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
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

    /// <summary>
    /// NOTIFYICONDATAW **v3** 布局（szTip/szInfo/szInfoTitle 齐，无 guidItem/hBalloonIcon）。
    ///
    /// ⚠️ 故意不补齐 v4 的两个尾巴字段：cbSize 一旦覆盖到 v4，外壳就把
    ///    <c>uTimeout/uVersion</c> 联合槽按 uVersion 读 —— 我们写的 4000ms
    ///    会被当成"版本 4000"，NIM_MODIFY 直接失败、气泡不弹。
    ///    v3 下该槽就是 uTimeout，语义正确；代价只是不支持自定义气泡图标（用不到）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public PointI pt;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpdata);

    /// <summary>注册一条系统广播消息的 ID（"TaskbarCreated" 就是这么来的）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes,
                                              uint nBitCount, byte[]? lpBits);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo piconInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int cursorName);
}
