using System.ComponentModel;
using System.Runtime.InteropServices;

namespace QControl.App.Input;

/// <summary>
/// 「锁定 Win 键」的**系统输入层那一半** —— 低级键盘钩子（WH_KEYBOARD_LL）把左右 Win 键的
/// 按下/抬起吃掉。
///
/// <para>
/// ⚠️ 2026-11-22 起它**不是唯一的一半**了：EC 也能锁（`0x0200` 写 a6=1），
/// 但 EC 只管笔记本内建键盘 —— 外接 USB / 蓝牙键盘根本不经过 EC，实测"只锁了笔记本键盘"。
/// 所以现在的组合是：EC 管内建（固件层、跨进程），这个钩子管外接（系统输入层、进程内）。
/// 见 <c>AppState.SetWinKeyLock</c>。
/// </para>
///
/// 为什么不用注册表 `Scancode Map`：
///   · 那条要写 `HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layout`，
///     **注销/重启才生效**，而且会一直留在系统里 —— 用户打一局游戏临时锁一下，
///     不该在系统配置里留下任何东西；
///   · 钩子是进程内的，**程序一退出（含崩溃）按键立刻恢复正常**，不需要任何清理。
/// 代价：钩子只在本进程运行时有效，且拦不住安全桌面（Ctrl+Alt+Del）—— 那本来也不该拦。
///
/// ⚠️ 钩子必须跑在**自己的消息泵线程**上：低级键盘钩子的回调由"安装它的那个线程"的消息循环
///    驱动。挂在 WPF 主线程上的话，写 EC 卡住 1.4 秒 = 全键盘延迟 1.4 秒（那正是用户在
///    散热页按按钮时最不能接受的事）。
/// ⚠️ 回调里绝不能抛异常、不能做重活：它跑在 Win32 回调栈上，一个没接住的异常会直接
///    终止进程（不是弹个框那么简单）。
/// ⚠️ **平时不装钩子**，只在"锁着"时装：常驻的全局键盘钩子会让反作弊软件起疑，
///    也会给每一次按键都加一次托管回调 —— 功能关着就不该有这些代价。
/// </summary>
internal sealed class WinKeyLock : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private delegate IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Install / Uninstall 串行化：反复快速点开关不该同时起两条钩子线程。</summary>
    private readonly object _gate = new();

    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _ready;
    /// <summary>交给系统的委托**必须**留一个引用：被 GC 回收后系统还在回调那个地址 ⇒ 进程崩。</summary>
    private HookCallback? _proc;
    private IntPtr _hook;
    /// <summary>用户要的是"锁着"。卸载时先清它，避免回调在拆钩子的窗口里还吞键。</summary>
    private volatile bool _want;
    /// <summary>钩子真的挂在系统上（= <see cref="Locked"/> 说的真值，不是"用户点了"）。</summary>
    private volatile bool _installed;

    private string? _error;
    private long _blocked;
    /// <summary>回调收到过多少条键盘事件（**不分是不是 Win 键**）。诊断用：
    /// 它是 0 就说明钩子压根没在这台机器/这个桌面收到输入，而不是判断条件写错了。</summary>
    private long _seen;

    /// <summary>现在是否真的在拦 Win 键（钩子装着 + 用户要锁）。</summary>
    public bool Locked => _installed && _want;

    /// <summary>累计吞掉多少次 Win 键事件（按下 + 抬起各算一次）。诊断 / 真机验证用。</summary>
    public long Blocked => Interlocked.Read(ref _blocked);

    /// <summary>回调收到过多少条键盘事件（含不拦的那些）。见 <see cref="_seen"/>。</summary>
    public long Seen => Interlocked.Read(ref _seen);

    /// <summary>钩子线程还活着吗（钩子被系统摘掉 / 线程崩了就是 false）。</summary>
    public bool ThreadAlive => _thread?.IsAlive == true;

    /// <summary>最近一次失败原因（成功时为 null）。</summary>
    public string? Error => _error;

    /// <summary>开 / 关。返回 false 表示**没成功**（<see cref="Error"/> 里有原因），调用方不许落盘。</summary>
    public bool Set(bool on)
    {
        lock (_gate)
        {
            return on ? Install() : Uninstall();
        }
    }

    private bool Install()
    {
        _error = null;

        // 已经装着：只把语义拨回"锁"，不重建线程。
        if (_installed)
        {
            _want = true;
            return true;
        }

        // 上一轮线程可能还活着（卸载超时 / 钩子被系统摘掉时线程刚好卡在回调里）。
        // 先把它收干净；收不掉就**不硬上第二条钩子** —— 两条钩子谁都摘不掉谁，
        // 最后会变成"关也关不掉的 Win 键"。
        if (_thread is { } old)
        {
            if (old.IsAlive && !StopThread(old))
            {
                _error ??= "上一个键盘钩子线程还没退出，稍后再试";
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
        _thread = new Thread(Pump) { IsBackground = true, Name = "winkey-lock" };
        _thread.Start();

        // 装钩子是同步的（毫秒级），2 秒纯粹是防"线程根本没起来"。
        if (!_ready.Wait(2000))
        {
            _error = "键盘钩子线程没起来（2 秒超时）";
            return false;
        }
        if (!_installed)
        {
            _error ??= "安装键盘钩子失败";
            return false;
        }

        _want = true;
        return true;
    }

    /// <summary>请钩子线程收工（WM_QUIT + 等它退出）。返回 true = 线程已经退出。</summary>
    private bool StopThread(Thread thread)
    {
        var id = _threadId;
        if (id != 0 && !PostThreadMessage(id, WM_QUIT, IntPtr.Zero, IntPtr.Zero))
            _error = "结束键盘钩子线程失败：" + LastWin32();

        try
        {
            if (thread.Join(1000)) return true;
        }
        catch
        {
            return true;        // 线程已经没了 / 不能 Join：当作已退出（退出路径不抛）
        }

        _error = "键盘钩子线程 1 秒内没退出";
        return false;
    }

    private bool Uninstall()
    {
        // 先断语义：拆钩子那一瞬间还有回调在飞的话，它已经被 _want=false 放过去了。
        _want = false;

        var thread = _thread;
        if (thread is null)
        {
            _installed = false;
            return true;
        }

        var stopped = StopThread(thread);

        // ⚠️ 撤销系统钩子只能由钩子线程自己干（句柄属于它）。这里**绝不**抢先把 `_hook`
        //    抹成 0：旧实现 Join 超时后照样清字段，而 Pump 的 finally 读的就是这个字段 ⇒
        //    UnhookWindowsHookEx(0) 失败 ⇒ 钩子永远挂在系统上，Win 键一直吞到进程退出
        //    （界面却已经显示"关"）。字段清理由 Pump 的 finally 独占，这里只在它确实
        //    退出之后清引用。
        _installed = false;                 // 语义已经放开：就算线程还没退，也不再吞键
        if (stopped)
        {
            _thread = null;
            _threadId = 0;
            _hook = IntPtr.Zero;
        }
        return true;
    }

    /// <summary>
    /// 钩子线程：建消息队列 → 装钩子 → 只等 WM_QUIT → 摘钩子。
    /// 它不处理任何别的消息，所以 GetMessage 的返回值只看 &lt;= 0（0 = WM_QUIT，-1 = 出错）。
    /// </summary>
    private void Pump()
    {
        var tid = GetCurrentThreadId();
        _threadId = tid;
        // ⚠️ 用局部引用（同 TrayIcon.Start 的教训）：`_ready` 会被下一轮 Install 换掉、
        //    也可能已被 Dispose；对着字段调用等于把信号发给别人，或抛
        //    ObjectDisposedException 逃出后台线程 —— 那就是整个进程结束。
        var ready = _ready;

        // ⚠️ 必须先碰一次消息队列：线程还没有队列时 PostThreadMessage 会失败，
        //    卸载时那个 WM_QUIT 就发不进来，线程会一直挂着。
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        _proc = OnHook;
        // ⚠️ 摘钩子用**局部句柄**：`_hook` 是共享字段，别人（Uninstall / 下一轮 Install）
        //    可能已经把它清掉了 —— 那样 finally 里摘的就是 0，真钩子留在系统上。
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _hook = hook;
        if (hook == IntPtr.Zero)
            _error = "安装键盘钩子失败：" + LastWin32();
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
                // 没有别的消息要处理：挂在这里等 WM_QUIT
            }
        }
        finally
        {
            UnhookWindowsHookEx(hook);
            if (_hook == hook) _hook = IntPtr.Zero;
            _installed = false;
            // ⚠️ 线程 id 必须清掉：否则下一次 Uninstall 会拿一个**已死线程的 id** 去
            //    PostThreadMessage —— Windows 会把那个 id 复用给新线程，等于朝别人
            //    的消息循环里塞一条 WM_QUIT。只清自己的那份：新的 Pump 可能已经把
            //    字段写成它自己的 id 了，别把新线程的 id 抹掉。
            if (_threadId == tid) _threadId = 0;
        }
    }

    /// <summary>
    /// 钩子回调。**必须极快**：系统给低级钩子的预算只有几百毫秒，
    /// 超时会被静默摘掉（之后 Win 键就"自己好了"，用户会以为功能失灵）。
    ///
    /// <c>lParam</c> 是 KBDLLHOOKSTRUCT*：offset 0 = vkCode，offset 4 = scanCode。
    /// 不区分 down / up 一律吞：只吞 down 的话 up 会漏给系统，某些程序会认为 Win 键卡住。
    ///
    /// ⚠️ vkCode 之外还看 scanCode：输入法把 vk 改写成 VK_PROCESSKEY(0xE5) 时（中文环境
    ///    实测会有），只认 vkCode 就会漏拦 —— 扫描码是硬件给的，不会被改写。
    ///    左 Win 的扫描码 0x5B、右 Win 0x5C，和它们的 vkCode 数值一样。
    /// </summary>
    private IntPtr OnHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            Interlocked.Increment(ref _seen);
            if (_want)
            {
                var vk = Marshal.ReadInt32(lParam);
                var scan = Marshal.ReadInt32(lParam, 4) & 0xFFFF;
                if (vk is VkLWin or VkRWin || scan is VkLWin or VkRWin)
                {
                    Interlocked.Increment(ref _blocked);
                    return (IntPtr)1;      // 非 0 = 吃掉，不再传给后面的钩子与窗口
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        try { Set(false); } catch { /* 退出路径不抛 */ }
        try { _ready?.Dispose(); } catch { }
    }

    private static string LastWin32()
    {
        var code = Marshal.GetLastWin32Error();
        return code == 0 ? "未知错误" : new Win32Exception(code).Message + $"（{code}）";
    }

    // ---------------------------------------------------------------- Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookCallback lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
                                          uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
                                         uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
