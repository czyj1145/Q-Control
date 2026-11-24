using System.Runtime.InteropServices;
using QControl.App.Render;
using QControl.App.Ui;

namespace QControl.App.Tray;

/// <summary>托盘菜单里的命令。数值只在本进程内用（不是消息 ID）。</summary>
internal enum TrayCommand
{
    None = 0,
    Open,
    Autostart,
    /// <summary>锁定 / 解锁 Win 键（进程内键盘钩子，见 `AppState.SetWinKeyLock`）。</summary>
    WinKeyLock,
    Quit,
}

/// <summary>菜单里的一项。<see cref="Separator"/> = 分隔线（Text / Command 都忽略）。</summary>
internal readonly record struct TrayMenuItem(string Text, TrayCommand Command, bool Separator = false)
{
    public static TrayMenuItem Sep => new("", TrayCommand.None, true);
}

/// <summary>
/// 托盘右键菜单 —— **自绘**（分层窗口 + <see cref="SoftCanvas"/>），配色跟 App 的
/// 暗/亮设置走（见 <see cref="TrayPalette"/>）。
///
/// 为什么不用 <c>CreatePopupMenu</c> + <c>TrackPopupMenu</c>（2026-10-22 用户要求
/// 「菜单也跟随白天黑夜」）：原生弹出菜单的外观由系统主题决定，本软件切到暗色时它会
/// 白着一张脸（或者反过来：系统暗色 + 软件浅色 → 一片黑）。要么跟系统（跟不了我们的设置），
/// 要么自己画。既然界面、OSD、托盘图标全是自绘的，菜单也自绘，两套色号才真的统一。
///
/// 行为对齐原生菜单：
///   · 弹出的同时成为前台窗口（Esc / ↑↓ / Enter 可用），关掉后**把前台还给原来的窗口**；
///   · 单击外部 / 右键 / 切到别的程序 → 关闭且**吞掉那一下点击**（与 TrackPopupMenu 一致）；
///   · 贴着光标弹出，超出工作区就往上/往左翻。
///
/// ⚠️ 阻塞语义与 <c>TrackPopupMenu</c> 相同：<see cref="Show"/> 里跑一个**嵌套消息循环**，
///    直到用户选完或关掉才返回。调用方（<see cref="TrayIcon"/> 的窗口过程）不用改成回调。
/// ⚠️ 窗口在本线程首次弹出时才创建，之后只 Show/Hide —— 而且**必须**在托盘线程里销毁
///    （窗口归属创建它的线程）。
/// </summary>
internal sealed class TrayMenu : IDisposable
{
    // ---------------- 版面（逻辑像素）----------------
    private const int ItemH = 34;
    private const int SepH = 9;
    private const int PadV = 7;
    private const int PadX = 18;
    private const int MinW = 184;
    private const int HoverInset = 5;
    private const int ShadowPad = 9;      // 位图里给投影留的边
    private const float Radius = 10f;
    private const float FontPx = 13.5f;
    private const byte HoverFillAlpha = 0x1f;

    private const string ClassName = "QControlTrayMenu";

    // ---------------- 窗口样式 / 消息 ----------------
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;

    private const uint WmDestroy = 0x0002;
    private const uint WmActivateApp = 0x001C;
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseActivate = 0x0021;
    private const uint WmKeyDown = 0x0100;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmCaptureChanged = 0x0215;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int UlwAlpha = 0x00000002;
    private const int MaActivate = 1;
    private const int IdcArrow = 32512;
    private const int VkEscape = 0x1B;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkReturn = 0x0D;
    private const int VkSpace = 0x20;

    /// <summary>一行在位图里的位置（物理像素）。<c>Item</c> = items 索引，-1 = 分隔线。</summary>
    private readonly record struct Row(int Top, int Height, int Item);

    private static bool _classRegistered;

    /// <summary>
    /// 系统箭头光标（<c>IDC_ARROW</c>）。**必须自己设**：窗口类没光标又不处理
    /// <c>WM_SETCURSOR</c> 时，DefWindowProc 会"什么都不做"，于是光标沿用进入菜单前的
    /// 形状 —— 托盘那边 explorer 被拖住时挂出的"转圈"就是这么被带进菜单、并且一直
    /// 转着的（2026-10-23）。系统光标是共享的，不要 <c>DestroyCursor</c>。
    /// </summary>
    private static IntPtr _hArrow;

    private static IntPtr Arrow()
    {
        if (_hArrow == IntPtr.Zero) _hArrow = LoadCursor(IntPtr.Zero, IdcArrow);
        return _hArrow;
    }

    private IntPtr _hwnd;
    private WndProcDelegate? _wndProc;      // 必须存字段：委托被 GC 后窗口回调就野了

    private readonly List<Row> _rows = new();
    private IReadOnlyList<TrayMenuItem> _items = Array.Empty<TrayMenuItem>();
    private TrayPalette _palette = TrayPalette.Dark;
    private float _scale = 1f;

    private int _bmpW, _bmpH, _cardW, _cardH, _shadow;
    private int _hover = -1;      // 行索引
    private int _pressed = -1;    // 行索引
    private bool _open;
    private TrayCommand _result;
    private IntPtr _prevFg;

    /// <summary>
    /// 弹出时记下的锚点（鼠标位置）。**重绘一律用它，不再重读光标**：
    /// 菜单下方放不开会往上翻，若按当前光标重算，鼠标在菜单里一移动，整块菜单就跟着漂。
    /// </summary>
    private PointI _anchor;

    // ================================================================ 对外

    /// <summary>
    /// 在光标处弹出菜单并**等用户选完**（嵌套消息循环）。返回 <see cref="TrayCommand.None"/>
    /// = 被关掉了（点外部 / Esc / 切窗）。窗口起不来或已有菜单在开 = 直接返回 None。
    /// </summary>
    public TrayCommand Show(IntPtr owner, IReadOnlyList<TrayMenuItem> items, TrayPalette palette)
    {
        if (_open || items.Count == 0) return TrayCommand.None;
        if (!EnsureWindow(owner)) return TrayCommand.None;

        _open = true;
        _result = TrayCommand.None;
        _items = items;
        _palette = palette;
        _hover = -1;
        _pressed = -1;

        try
        {
            _anchor = Cursor();
            _scale = DpiScaleAt(_anchor);
            Layout();
            if (!Paint()) return TrayCommand.None;

            // 先让本进程拿到前台，再让菜单激活 —— 顺序反了会被前台锁拦下来，
            // 结果就是"菜单能点但 Esc 没反应"。
            _prevFg = GetForegroundWindow();
            SetForegroundWindow(owner);
            ShowWindow(_hwnd, SwShowNoActivate);
            SetForegroundWindow(_hwnd);
            SetCapture(_hwnd);

            // 弹出来这一刻主动把光标钉成箭头：菜单弹出前那一瞬间鼠标还在任务栏上，
            // 那边（explorer / 通知区域）要是留着等待圈，WM_SETCURSOR 又只在鼠标**移动**
            // 时才来，鼠标不动就会把那个圈一直顶在菜单上。
            SetCursor(Arrow());

            while (_open)
            {
                var got = GetMessage(out var msg, IntPtr.Zero, 0, 0);
                if (got <= 0)
                {
                    // 0 = WM_QUIT：正常退出流程路过这里，把退出码还回去，别吞掉
                    if (got == 0) PostQuitMessage(0);
                    break;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            return _result;
        }
        finally
        {
            _open = false;
            if (_hwnd != IntPtr.Zero)
            {
                ShowWindow(_hwnd, SwHide);
                if (GetCapture() == _hwnd) ReleaseCapture();
            }
            // 前台还给原来那个窗口：不还的话，用户按 Esc 关掉菜单，焦点就落在
            // 我们那个看不见的托盘窗口上 —— 看起来像"整个桌面失焦了"。
            if (_prevFg != IntPtr.Zero && IsWindow(_prevFg)) SetForegroundWindow(_prevFg);
            _prevFg = IntPtr.Zero;
        }
    }

    /// <summary>销毁菜单窗口。**只能在托盘线程调**（窗口归属它）。</summary>
    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        try { DestroyWindow(_hwnd); } catch { }
        _hwnd = IntPtr.Zero;
    }

    // ================================================================ 窗口

    private bool EnsureWindow(IntPtr owner)
    {
        if (_hwnd != IntPtr.Zero) return true;

        if (!_classRegistered)
        {
            _wndProc = WndProc;
            var wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                hCursor = Arrow(),
                lpszClassName = ClassName,
            };
            if (RegisterClassEx(ref wc) == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410) return false;    // 1410 = 类已注册，复用即可
            }
            _classRegistered = true;
        }

        // WS_EX_TOOLWINDOW：不进 Alt+Tab；LAYERED：整张位图贴上去（圆角 + 投影）。
        // 尺寸给 0，真正的尺寸由 UpdateLayeredWindow 定。
        _hwnd = CreateWindowEx(WsExLayered | WsExTopmost | WsExToolWindow,
                               ClassName, "menu", WsPopup,
                               0, 0, 0, 0, owner, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        return _hwnd != IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmMouseMove:
            {
                var row = HitRow(lParam);
                if (row != _hover)
                {
                    _hover = row;
                    Repaint();
                }
                return IntPtr.Zero;
            }

            case WmLButtonDown:
            {
                var row = HitRow(lParam);
                if (row < 0) { Dismiss(); return IntPtr.Zero; }   // 点外部：关掉并吞掉这一下
                _pressed = row;
                return IntPtr.Zero;
            }

            case WmLButtonUp:
            {
                var row = HitRow(lParam);
                var pressed = _pressed;
                _pressed = -1;
                if (row < 0) { Dismiss(); return IntPtr.Zero; }
                if (row == pressed) Choose(row);      // 按下又抬起在同一行才算点中（原生菜单口径）
                return IntPtr.Zero;
            }

            case WmRButtonDown:
                Dismiss();
                return IntPtr.Zero;

            case WmKeyDown:
                HandleKey((int)wParam.ToInt64());
                return IntPtr.Zero;

            case WmCaptureChanged:
                if (lParam != _hwnd) Dismiss();       // 捕获被系统收走（切窗/别的程序抢）
                return IntPtr.Zero;

            case WmActivateApp:
                if (wParam == IntPtr.Zero) Dismiss();
                return IntPtr.Zero;

            case WmMouseActivate:
                return new IntPtr(MaActivate);

            case WmSetCursor:
                // 自绘窗口没有边框/系统菜单，光标只能自己给。给了箭头就一定是箭头，
                // 不会把外面残留的等待圈一路带进菜单。
                SetCursor(Arrow());
                return new IntPtr(1);

            case WmDestroy:
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleKey(int vk)
    {
        switch (vk)
        {
            case VkEscape:
                Dismiss();
                break;
            case VkUp:
                MoveHover(-1);
                break;
            case VkDown:
                MoveHover(+1);
                break;
            case VkReturn:
            case VkSpace:
                if (_hover >= 0) Choose(_hover);
                break;
        }
    }

    private void MoveHover(int delta)
    {
        var selectable = new List<int>();
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Item >= 0) selectable.Add(i);
        if (selectable.Count == 0) return;

        var at = selectable.IndexOf(_hover);
        var next = at < 0
            ? (delta > 0 ? 0 : selectable.Count - 1)
            : (at + delta + selectable.Count) % selectable.Count;
        _hover = selectable[next];
        Repaint();
    }

    private void Choose(int row)
    {
        if (row < 0 || row >= _rows.Count) return;
        var item = _rows[row].Item;
        if (item < 0 || item >= _items.Count) return;
        if (_items[item].Command == TrayCommand.None) return;

        _result = _items[item].Command;
        _open = false;          // 让嵌套循环退出；窗口在 Show 的 finally 里隐藏
    }

    private void Dismiss() => _open = false;

    // ================================================================ 版面

    private void Layout()
    {
        var s = _scale;
        _shadow = Math.Max(2, (int)Math.Round(ShadowPad * s));
        var padV = Math.Max(2, (int)Math.Round(PadV * s));
        var padX = Math.Max(4, (int)Math.Round(PadX * s));
        var itemH = Math.Max(12, (int)Math.Round(ItemH * s));
        var sepH = Math.Max(4, (int)Math.Round(SepH * s));
        var fontPx = FontPx * s;

        var textW = 0f;
        foreach (var it in _items)
            if (!it.Separator) textW = MathF.Max(textW, GdiText.Measure(it.Text, fontPx));

        _cardW = Math.Max((int)Math.Round(MinW * s), (int)Math.Ceiling(textW) + padX * 2);
        _rows.Clear();
        var y = _shadow + padV;
        var total = padV * 2;
        for (var i = 0; i < _items.Count; i++)
        {
            var h = _items[i].Separator ? sepH : itemH;
            _rows.Add(new Row(y, h, _items[i].Separator ? -1 : i));
            y += h;
            total += h;
        }
        _cardH = total;
        _bmpW = _cardW + _shadow * 2;
        _bmpH = _cardH + _shadow * 2;
    }

    private int HitRow(IntPtr lParam)
    {
        var l = lParam.ToInt64();
        var x = (int)(short)(l & 0xFFFF);
        var y = (int)(short)((l >> 16) & 0xFFFF);
        if (x < _shadow || x >= _shadow + _cardW) return -1;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Item >= 0 && y >= row.Top && y < row.Top + row.Height) return i;
        }
        return -1;
    }

    // ================================================================ 绘制

    private bool Paint()
    {
        if (_bmpW <= 0 || _bmpH <= 0) return false;

        var canvas = new SoftCanvas(_bmpW, _bmpH);
        Render(canvas, 0, 0, _palette, _hover);

        var (left, top) = Place();
        var dst = new PointI { x = left, y = top };
        var size = new SizeI { cx = _bmpW, cy = _bmpH };
        var src = new PointI { x = 0, y = 0 };
        var blend = new BlendFunction
        {
            BlendOp = 0,             // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1,         // AC_SRC_ALPHA
        };

        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = CreateBitmap(_bmpW, _bmpH, 1, 32, canvas.Buffer);
        if (hbmp == IntPtr.Zero)
        {
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            return false;
        }

        var old = SelectObject(mem, hbmp);
        try
        {
            return UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void Repaint()
    {
        if (!_open || _hwnd == IntPtr.Zero) return;
        Paint();
    }

    /// <summary>菜单窗口左上角（屏幕物理像素）。位图比卡片大一圈，所以整体左上移一个投影宽度。</summary>
    private (int Left, int Top) Place()
    {
        var pt = _anchor;
        var work = WorkArea(pt);

        var cardLeft = pt.x;
        var cardTop = pt.y;
        if (cardTop + _cardH > work.Bottom) cardTop = pt.y - _cardH;      // 下面放不开 → 往上翻
        if (cardLeft + _cardW > work.Right) cardLeft = work.Right - _cardW;
        cardLeft = Math.Max(cardLeft, work.Left);
        cardTop = Math.Max(cardTop, work.Top);

        return (cardLeft - _shadow, cardTop - _shadow);
    }

    /// <summary>
    /// 把整张菜单画进画布（<paramref name="ox"/>/<paramref name="oy"/> 是给截图留的偏移）。
    /// 顺序：投影 → 卡片底 → 各行 → 描边。带 alpha 的东西都画在不透明底之后。
    /// </summary>
    private void Render(SoftCanvas c, int ox, int oy, TrayPalette p, int hover)
    {
        var s = _scale;

        // 投影：从最外圈往里一圈圈很淡的黑。SDF 填充会累加，于是越靠卡片越深，
        // 九圈就够糊出软过渡（圆形菜单很小，再多也看不出来）。
        for (var i = _shadow; i >= 1; i--)
            c.FillRounded(ox + _shadow - i, oy + _shadow - i,
                          _cardW + 2 * i, _cardH + 2 * i,
                          Radius * s + i, 0, 0, 0, p.ShadowAlpha);

        // 卡片：与界面卡片同一套底色（Panel → CardBottom 的竖向渐变）
        c.FillRoundedGradient(ox + _shadow, oy + _shadow, _cardW, _cardH, Radius * s,
                              (p.BgTop.R, p.BgTop.G, p.BgTop.B, (byte)255),
                              (p.BgBottom.R, p.BgBottom.G, p.BgBottom.B, (byte)255));

        var fontPx = FontPx * s;
        var (ascent, descent) = GdiText.VMetrics(fontPx);
        var lineH = ascent + descent;
        var padX = Math.Max(4, (int)Math.Round(PadX * s));
        var inset = Math.Max(2, (int)Math.Round(HoverInset * s));

        for (var r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];

            if (row.Item < 0)
            {
                // 分隔线：左右各留 12 逻辑像素，比整条横线轻
                var gap = (int)Math.Round(12 * s);
                var ly = oy + row.Top + row.Height / 2;
                c.FillRounded(ox + _shadow + gap, ly,
                              Math.Max(1, _cardW - gap * 2), Math.Max(1, (int)Math.Round(s)),
                              0f, p.Line.R, p.Line.G, p.Line.B, 255);
                continue;
            }

            if (r == hover)
            {
                var v = Math.Max(1, (int)Math.Round(2 * s));
                c.FillRounded(ox + _shadow + inset, oy + row.Top + v,
                              Math.Max(1, _cardW - inset * 2),
                              Math.Max(1, row.Height - v * 2),
                              MathF.Max(2f, 7f * s),
                              p.Acc.R, p.Acc.G, p.Acc.B, HoverFillAlpha);
            }

            var centerY = row.Top + row.Height / 2f;
            var baseline = centerY - lineH / 2f + ascent;
            GdiText.Draw(c, _items[row.Item].Text, fontPx, ox + _shadow + padX, oy + baseline,
                         p.Txt.R, p.Txt.G, p.Txt.B, 255, shadow: false);
        }

        c.StrokeRounded(ox + _shadow, oy + _shadow, _cardW, _cardH, Radius * s,
                        p.Line.R, p.Line.G, p.Line.B, 255);
    }

    // ================================================================ 开发用截图

    /// <summary>
    /// 把菜单渲成两张 PNG（暗/亮各一张，第二项高亮），验"两套主题长什么样"。
    /// 菜单是分层窗口 + 位图，走不了 <c>UiShot</c> 的窗口截图路径，这里直接渲画布。
    /// 只在 <c>--tray-menu-shot</c> 时调用，正常启动一行都不碰（也不加载 WPF 编码器）。
    /// </summary>
    public static int Shot(string path)
    {
        var items = new List<TrayMenuItem>
        {
            new("打开控制中心", TrayCommand.Open),
            TrayMenuItem.Sep,
            new("锁定 Win 键（当前：关）", TrayCommand.WinKeyLock),
            new("开机自启（当前：开）", TrayCommand.Autostart),
            TrayMenuItem.Sep,
            new("退出", TrayCommand.Quit),
        };

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(full);
        Directory.CreateDirectory(dir);

        var variants = new (string Name, TrayPalette Pal, Rgb DeskTop, Rgb DeskBottom)[]
        {
            ("dark", TrayPalette.Dark, Rgb.Hex("#2a2f3a"), Rgb.Hex("#1a1e26")),
            ("light", TrayPalette.Light, Rgb.Hex("#dfe4ec"), Rgb.Hex("#cfd6e2")),
        };

        foreach (var v in variants)
        {
            using var menu = new TrayMenu();
            menu._items = items;
            menu._scale = 1.25f;            // 常见缩放，字才看得清
            menu.Layout();

            const int pad = 18;
            var c = new SoftCanvas(menu._bmpW + pad * 2, menu._bmpH + pad * 2);
            // 模拟桌面底色：菜单需要背景才看得出投影和圆角半径对不对
            c.FillRoundedGradient(0, 0, c.Width, c.Height, 0f,
                                  (v.DeskTop.R, v.DeskTop.G, v.DeskTop.B, (byte)255),
                                  (v.DeskBottom.R, v.DeskBottom.G, v.DeskBottom.B, (byte)255));

            menu._hover = 2;                // 「锁定 Win 键」高亮：一张图同时看常态与悬停
            menu.Render(c, pad, pad, v.Pal, menu._hover);

            PngSave.FromCanvas(c, Path.Combine(dir, stem + "-" + v.Name + ".png"));
        }

        Console.WriteLine($"[shot] 已写入 {Path.Combine(dir, stem)}-dark.png / -light.png");
        return 0;
    }

    // ================================================================ 屏幕度量

    private static PointI Cursor()
    {
        var pt = new PointI();
        return GetCursorPos(ref pt) ? pt : default;
    }

    private readonly record struct WorkRect(int Left, int Top, int Right, int Bottom);

    private static WorkRect WorkArea(PointI pt)
    {
        var mon = MonitorFromPoint(pt, 2);        // MONITOR_DEFAULTTONEAREST
        if (mon != IntPtr.Zero)
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(mon, ref info))
                return new WorkRect(info.rcWork.left, info.rcWork.top,
                                    info.rcWork.right, info.rcWork.bottom);
        }
        return new WorkRect(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
    }

    /// <summary>光标所在那块屏的 DPI 缩放（菜单跟着光标走，不看主屏）。</summary>
    private static float DpiScaleAt(PointI pt)
    {
        try
        {
            var mon = MonitorFromPoint(pt, 2);
            if (mon != IntPtr.Zero
                && GetDpiForMonitor(mon, 0, out var dpiX, out _) == 0
                && dpiX > 0)
                return dpiX / 96f;
        }
        catch (DllNotFoundException) { }          // Win7 没有 shcore
        catch (EntryPointNotFoundException) { }

        try
        {
            var dpi = GetDpiForWindow(IntPtr.Zero);
            if (dpi == 0) dpi = GetDpiForSystem();
            return dpi > 0 ? dpi / 96f : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    // ================================================================ P/Invoke

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointI { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeI { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectI { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectI rcMonitor;
        public RectI rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int cursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref PointI pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointI pt, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType,
                                               out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr dstDc, ref PointI dst,
        ref SizeI size, IntPtr srcDc, ref PointI src, int colorKey, ref BlendFunction blend, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bitCount, byte[]? bits);

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
