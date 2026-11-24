using System.Runtime.InteropServices;

namespace QControl.App.Osd;

/// <summary>
/// 媒体键提示：播放/暂停、停止、上一首、下一首（`VK_MEDIA_*` = 0xB0~0xB3）。
///
/// 为什么它必须单独一条钩子（而 CapsLock 那些走轮询）：
///   媒体键没有"当前状态"可读（同一时刻播放还是暂停取决于在放什么），
///   只有**按键事件**本身有意义 —— 2026-11-25 实测这四键会以标准
///   `VK_MEDIA_PLAY_PAUSE(0xB3)/STOP(0xB2)/PREV(0xB1)/NEXT(0xB0)` 到达系统，
///   低级键盘钩子读得到（摄像头/FnLock 那类被 EC 吞掉的键就读不到）。
///
/// ⚠️ **只在"提示条归我们"的模式下才装**（`ours` / `both`）：常驻的全局键盘钩子
///    会给每一次按键都加一道托管回调，还可能让反作弊软件起疑 ——
///    功能用不上时就不该有这些代价（与 <see cref="Input.WinKeyLock"/> 同一条原则）。
/// ⚠️ 回调里**只做 Show（内部就是投递一条消息）**，绝不读 EC / 不枚举进程：
///    低级钩子超时会被系统静默摘掉，之后媒体键就"自己好了"。
/// </summary>
internal sealed class MediaKeyWatch : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VkMediaNext = 0xB0;
    private const int VkMediaPrev = 0xB1;
    private const int VkMediaStop = 0xB2;
    private const int VkMediaPlayPause = 0xB3;

    private delegate IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly OsdWindow _osd;
    private readonly object _gate = new();

    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _ready;
    /// <summary>交给系统的委托必须留引用：被 GC 之后系统还在回调那个地址 ⇒ 进程崩。</summary>
    private HookCallback? _proc;
    private IntPtr _hook;
    private volatile bool _installed;
    private string? _error;

    public MediaKeyWatch(OsdWindow osd) => _osd = osd;

    /// <summary>钩子真的挂在系统上。</summary>
    public bool Installed => _installed;

    public string? Error => _error;

    /// <summary>装 / 拆。串行化：反复切模式不该同时存在两条钩子线程。</summary>
    public bool Set(bool on)
    {
        lock (_gate) return on ? Install() : Uninstall();
    }

    public void Dispose() => Set(false);

    // ---------------------------------------------------------------- 线程

    private bool Install()
    {
        _error = null;
        if (_installed) return true;

        if (_thread is { } old)
        {
            if (old.IsAlive && !StopThread(old))
            {
                _error ??= "上一个媒体键钩子线程还没退出";
                return false;
            }
            if (!old.IsAlive)
            {
                _thread = null;
                _threadId = 0;
                _hook = IntPtr.Zero;
            }
        }

        _ready = new ManualResetEventSlim(false);
        _thread = new Thread(Pump) { IsBackground = true, Name = "media-keys" };
        _thread.Start();

        if (!_ready.Wait(2000))
        {
            _error = "媒体键钩子线程没起来（2 秒超时）";
            return false;
        }
        if (!_installed)
        {
            _error ??= "安装媒体键钩子失败";
            return false;
        }
        return true;
    }

    private bool StopThread(Thread thread)
    {
        var id = _threadId;
        if (id != 0) PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (thread.Join(1000)) return true;
        }
        catch
        {
            return true;
        }
        _error = "媒体键钩子线程 1 秒内没退出";
        return false;
    }

    private bool Uninstall()
    {
        var thread = _thread;
        if (thread is null)
        {
            _installed = false;
            return true;
        }

        // ⚠️ 摘钩子只能由钩子线程自己干（句柄属于它）：字段清理由 Pump 的 finally 独占，
        //    这里不抢先抹 `_hook` —— 否则 finally 里摘的是 0，钩子会一直挂在系统上。
        var stopped = StopThread(thread);
        _installed = false;
        if (stopped)
        {
            _thread = null;
            _threadId = 0;
            _hook = IntPtr.Zero;
        }
        return true;
    }

    private void Pump()
    {
        var tid = GetCurrentThreadId();
        _threadId = tid;
        var ready = _ready;

        // 先碰一次消息队列：线程还没有队列时 PostThreadMessage 会失败，卸载时的 WM_QUIT 发不进来。
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        _proc = OnHook;
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _hook = hook;
        if (hook == IntPtr.Zero)
            _error = "安装媒体键钩子失败：" + Marshal.GetLastWin32Error();
        else
            _installed = true;

        try { ready?.Set(); } catch (ObjectDisposedException) { /* 已经没人等了 */ }

        if (hook == IntPtr.Zero)
        {
            if (_threadId == tid) _threadId = 0;
            return;
        }

        try
        {
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // 不处理别的消息：挂在这里等 WM_QUIT
            }
        }
        finally
        {
            UnhookWindowsHookEx(hook);
            if (_hook == hook) _hook = IntPtr.Zero;
            _installed = false;
            if (_threadId == tid) _threadId = 0;    // 别把新线程的 id 抹掉
        }
    }

    /// <summary>
    /// 钩子回调。<c>lParam</c> 是 KBDLLHOOKSTRUCT*：offset 0 = vkCode。
    ///
    /// 只认按下（KEYDOWN/SYSKEYDOWN）—— 提示只需要一次，抬起再来一条就成双了。
    /// 不吞键：一律 <c>CallNextHookEx</c>，媒体键照常交给系统的播放器处理。
    /// </summary>
    private IntPtr OnHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    switch (Marshal.ReadInt32(lParam))
                    {
                        case VkMediaPlayPause: _osd.Show("media_play", "媒体播放", "播放 / 暂停"); break;
                        case VkMediaStop: _osd.Show("media_stop", "媒体播放", "停止"); break;
                        case VkMediaPrev: _osd.Show("media_prev", "媒体播放", "上一首"); break;
                        case VkMediaNext: _osd.Show("media_next", "媒体播放", "下一首"); break;
                    }
                }
            }
        }
        catch
        {
            // 回调里绝不能抛：它跑在 Win32 回调栈上，未处理的异常会直接终止进程
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookCallback proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
