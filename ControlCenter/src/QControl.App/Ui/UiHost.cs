using System.Runtime;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QControl.App.Mem;

namespace QControl.App.Ui;

/// <summary>
/// WPF 界面的生命周期管理：**按需起、关掉即释放**。
///
/// 为什么界面跑在一条自建 STA 线程上，而不是主线程：
///   · 主线程在 <c>quit.Wait()</c> 上等退出信号，还要收 Ctrl+C（控制台子系统）；
///     把它改成 WPF 的消息循环，退出路径（交回风扇转速 / 摘托盘图标 / 停 HwInfo）全得重排；
///   · 后台线程 + 自己的 Dispatcher 让"窗口"与"后端"彻底解耦：窗口关掉就
///     <c>Dispatcher.InvokeShutdown()</c>，线程结束、Window 与它持有的整棵可视树被回收，
///     后端继续以原来的工作集跑着（托盘还能再打开）。
///
/// ⚠️ 刻意**不创建 Application**：一个 AppDomain 只允许一个 Application 实例，
///    而这个窗口会反复开关 —— 第二次开就会抛 "Cannot create more than one System.Windows.Application"。
///    没有 Application 也能正常显示窗口/解析资源/跑消息循环，代价是没有默认的 ShutdownMode 语义
///    （我们本来也不需要：进程退出由 Program 的 quit 事件统一管）。
/// ⚠️ WPF 程序集是**首次用到这里才加载**的（运行时按需 JIT + 解析依赖），
///    所以没开过窗口时，这一整套对工作集几乎零成本 —— 这也是"进程内直读"的前提。
/// </summary>
internal static class UiHost
{
    private static readonly object Gate = new();

    private static Thread? _thread;
    private static MainWindow? _window;
    private static bool _starting;
    private static bool _pendingActivate;
    private static string? _pendingPage;

    /// <summary>
    /// 软件渲染已切换的标记（进程级一次性开关）。<see cref="RenderOptions.ProcessRenderMode"/>
    /// 是进程级设置，首次创建任何 WPF 元素前设一次即可；反复设置无意义还多一次查找。
    /// </summary>
    private static bool _renderModeSet;

    public static bool IsOpen
    {
        get { lock (Gate) return _window is not null; }
    }

    /// <summary>打开界面；已经开着就只是叫到前台（并把想要的页切过去）。</summary>
    public static void Show(AppState? state, string? page = null)
    {
        EnsureSoftwareRendering();
        // 界面一开就停掉"空闲归还工作集"：窗口开着时 trim 会让下一次重建 UI 缺页。
        MemTrim.NoteUiOpen();

        MainWindow? existing;
        lock (Gate)
        {
            existing = _window;
            if (existing is null && _starting)
            {
                _pendingActivate = true;
                if (page is not null) _pendingPage = page;
                return;
            }
            if (existing is null)
            {
                _starting = true;
                _thread = new Thread(() => Run(state, page))
                {
                    IsBackground = true,      // 后端退出时不要被这条线程吊住
                    Name = "cc-ui",
                };
                _thread.SetApartmentState(ApartmentState.STA);   // WPF 硬要求
                _thread.Start();
                return;
            }
        }

        Activate(existing, page);
    }

    /// <summary>
    /// 把 WPF 切到软件渲染（进程级，只设一次）。
    ///
    /// 为什么：WPF 默认走 DirectX 硬件渲染，会把显卡用户态驱动（本机 NVIDIA UMD 六件套，
    /// ~178 MB）拉进进程 —— 实测是前台内存的最大头。控制面板是静态 UI + 少量动画
    /// （环形仪表 / 曲线），软件渲染完全够用，省掉这 178 MB 还顺带省了 GPU 往返。
    /// ⚠️ 必须在首次创建任何 WPF 元素之前调（这里在 <c>new MainWindow</c> 之前、
    ///     起 STA 线程之前，安全）。
    /// </summary>
    private static void EnsureSoftwareRendering()
    {
        if (_renderModeSet) return;
        _renderModeSet = true;
        try { RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly; }
        catch { /* 设置失败（极端环境）就退回硬件渲染，界面照常 */ }
    }

    /// <summary>关掉界面（退出程序前调用；窗口没开就什么都不做）。</summary>
    public static void Close()
    {
        MainWindow? window;
        lock (Gate) window = _window;
        if (window is null) return;
        try { window.Dispatcher.BeginInvoke(new Action(() => window.Close())); } catch { /* 退出路径不抛 */ }
    }

    /// <summary>
    /// 关窗后主动回收：GC(2) + LOH 压缩 + **归还工作集**。
    ///
    /// ⚠️ 必须**不阻塞**关窗路径：GC 压缩会有几十毫秒的 STW，直接同步做会让"关窗"
    ///     那一瞬间卡一下。整套动作（含 GC）在 <see cref="MemTrim.TrimAsync"/> 里
    ///     丢到线程池上，反正回收对象已经没引用了，晚几十毫秒无妨。
    /// ⚠️ 避开采样线程：采样线程每秒会碰 EC 单槽缓冲区，GC 压缩的 STW 只是让它那一拍
    ///     晚几十毫秒，不碰它的数据，无需额外协调。
    /// ⚠️ 2026-10-27 起多了一个 `SetProcessWorkingSetSize`：只做 GC 时托盘态停在
    ///     71 MB 下不去（GC 只还托管堆，还不了代码页和"活着但闲置"的代）。
    ///     这一句能把物理页换出去，关窗后落到 20 MB 量级。见 <see cref="MemTrim"/>。
    /// </summary>
    private static void ReclaimAfterClose()
    {
        MemTrim.NoteUiClosed();
        MemTrim.TrimAsync("关窗");
    }

    private static void Activate(MainWindow window, string? page)
    {
        try
        {
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (page is not null) window.ShowPage(page);
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                // 顶一下 Topmost 才能真的抢到前台（Windows 的前台窗口限制没有别的干净办法）
                window.Topmost = true;
                window.Topmost = false;
            }));
        }
        catch
        {
            // 窗口正在关闭时 Dispatcher 可能已经停了，忽略即可
        }
    }

    private static void Run(AppState? state, string? page)
    {
        MainWindow? window = null;
        try
        {
            // 会话开始：换一份**全新**的调色板字典。上一会话的窗口虽然已经关了，
            // 但那份静态字典里还留着它当 owner —— 带着它一起用就是跨线程访问
            // （关窗口再开必崩，见 UiTheme.ResetPalette）。必须在新窗口构造之前。
            UiTheme.ResetPalette();

            window = new MainWindow(state, page);
            window.Closed += (_, _) =>
            {
                lock (Gate)
                {
                    _window = null;
                    _thread = null;
                }
                // 让 Dispatcher.Run() 返回 ⇒ 这个 STA 线程结束 ⇒ Window 与可视树全部可回收
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            };

            lock (Gate)
            {
                _window = window;
                _starting = false;
                page = _pendingPage ?? page;
                _pendingPage = null;
            }

            // ⚠️ 只在**真的不一样**时才切：构造里已经按启动页落过页（`--page` 就是它），
            //    这里再无条件调一次会走 ShowPage 的"重复点当前项"分支 —— 开窗第一帧
            //    就弹一条「性能 · 已刷新」，还多跑一遍 RefreshPage（powercfg / NVML 等慢读）。
            if (page is not null && page != window.CurrentPageKey) window.ShowPage(page);
            window.Show();

            // 开窗时顺手查一次更新：只在"人真的在看界面"时联网，且 UpdateChecker 里
            // 有 24 小时节流。截图 / 自检路径不经过 Run()，所以不会被这条拖慢。
            try { state?.MaybeCheckUpdate(); } catch { /* 查更新失败不影响开窗 */ }

            if (_pendingActivate)
            {
                _pendingActivate = false;
                window.Activate();
            }

            // ⚠️ 保险：这条线程上任何没被接住的异常都**先落全量堆栈再续命**。
            //     没有 Application 就没有 DispatcherUnhandledException 兜底，一个
            //     处理器里抛出的异常会整条线程连坐 —— 窗口凭空消失、后端照跑，
            //     用户看到的就是"闪退"，而日志里只剩一句 Type:Message（异常对象有
            //     堆栈，日志里却没有）。
            //     Handled=true 只保证"窗口不消失"，该修的地方照修 —— 堆栈会说话。
            Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
            {
                Console.Error.WriteLine($"[WARN] WPF 界面未处理异常（已保住窗口）：{e.Exception}");
                e.Handled = true;
            };

            Dispatcher.Run();

            // 正常退出（窗口被关）：Closed 里已退过，幂等；放在这里兜住
            // "Closed 没跑但线程要结束"的一切路径。
            window.DetachTheme();

            // 会话结束：把字典放掉 —— 否则它拉着刚关掉的窗口的整棵可视树不撒手
            // （下次开窗虽然会换新的，但用户不一定会再开一次）。
            UiTheme.ResetPalette();

            // 关窗即回收：把刚卸下来的整棵可视树 / WPF 程序集堆 / LOH 大对象立刻还给 OS。
            // 否则这些 Gen2/LOH 残留在堆里，反复开关窗口后"只托盘"状态会从 85MB 一路涨到
            // 170MB+（.NET 默认很保守，工作集归还滞后）。见 DETAILS「内存对照」。
            // 放在这条 STA 线程里、Disposer.Run() 返回之后：此时可视树已无人引用，可以安全收。
            ReclaimAfterClose();
        }
        catch (Exception ex)
        {
            // UI 起不来不能拖垮后端：记一行日志（会进 cc-yyyy-MM-dd.log），后端照常服务
            // ⚠️ 必须是 ex 全量（ToString 带堆栈），不是 Type:Message ——
            //    "调用线程无法访问此对象"这类没有堆栈根本查不出是谁（见上方保险注释）。
            Console.Error.WriteLine($"[WARN] WPF 界面启动失败：{ex}");
            // 订阅可能已经挂上（构造后半段炸的）：不退就会漏给下一个窗口
            window?.DetachTheme();
            // 构造中途炸掉时字典里可能已经登记了半成品窗口：同样放手（下一次开窗
            // 会换新的，但别让这次的残骸被静态字段钉一辈子）。
            UiTheme.ResetPalette();
            // 界面根本没起来 ⇒ 也该恢复"空闲可以归还工作集"，否则这个标志会被永久钉成开。
            MemTrim.NoteUiClosed();
            lock (Gate)
            {
                _window = null;
                _thread = null;
                _starting = false;
            }
        }
    }
}
