namespace QControl.App.Ui;

/// <summary>
/// 进程间"把界面叫出来"的信号。
///
/// 场景：控制中心已经常驻在托盘（开机自启就是这样），用户又双击了一次
/// run-as-admin.cmd / exe。第二个进程拿不到第一个进程的 AppState（那是内存里的对象），
/// 所以它**没办法自己开一个"活的"窗口**。
///
/// 办法：第一个实例监听一个按端口命名的 <see cref="EventWaitHandle"/>，
/// 第二个实例 Set 一下就退出。用 ThreadPool 注册等待（不额外占一条线程）。
///
/// ⚠️ 名字带端口：同时跑多个实例（不同 `--port` 调试）时各叫各自的窗口，不会串。
/// ⚠️ 用 "Local\" 前缀：只在当前会话可见，不需要（也不该）跨会话。
/// </summary>
internal static class UiSignal
{
    private static EventWaitHandle? _handle;
    private static RegisteredWaitHandle? _registration;

    public static void Listen(int port, Action onShow)
    {
        try
        {
            _handle = new EventWaitHandle(false, EventResetMode.AutoReset, Name(port));
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _handle,
                (_, _) => onShow(),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            // 信号起不来只影响"第二次双击能不能叫出窗口"，后端功能不受影响
            Console.Error.WriteLine($"[WARN] 界面信号量创建失败：{ex.Message}");
        }
    }

    /// <summary>叫醒已经在跑的那个实例。返回 false = 没人在听（那就该自己开窗口）。</summary>
    public static bool Trigger(int port)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Name(port), out var handle)) return false;
            using (handle)
            {
                handle.Set();
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string Name(int port) => $@"Local\QControl.Show.{port}";
}
