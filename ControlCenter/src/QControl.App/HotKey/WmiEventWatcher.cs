using System.Management;

namespace QControl.App.HotKey;

/// <summary>
/// 订阅 `root\WMI` 下的一个事件类（`GMC_WMIEvent` / `WMI2Event`）。
///
/// 为什么用 `ManagementEventWatcher` 而不是照搬早先实现的
/// `ExecNotificationQuery` + `NextEvent(500)` 轮询：
///   ① 早先实现那套 500ms 超时轮询是为了让 `stop` 能及时生效，还要自己分辨
///      "超时"和"订阅断了"（`_is_wmi_timeout` 那堆注释就是这么来的）；
///   ② `ManagementEventWatcher` 自带后台线程推送 `EventArrived`，`Stop()` 即刻返回，
///      没有"超时被误判成断线 ⇒ 反复重订阅 ⇒ 事件在空窗期丢失"这类问题。
///
/// 多进程同时订阅是安全的（纯只读监听，互不抢占），不会影响原厂软件。
/// </summary>
internal sealed class WmiEventWatcher : IDisposable
{
    private readonly string _className;
    private ManagementEventWatcher? _watcher;

    public string? LastError { get; private set; }
    public bool Available { get; private set; }
    public bool Running { get; private set; }
    public long Count;
    public double? LastAt;

    public WmiEventWatcher(string className) => _className = className;

    /// <summary>
    /// 开始监听。`onEvent` 拿到的是 `EventDetail` 的原始字节（事件里唯一有信息量
    /// 的属性）。失败返回 false 并在 `LastError` 里说明 —— 调用方只记日志，不阻断启动。
    /// </summary>
    public bool Start(Action<byte[]> onEvent)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\WMI");
            scope.Connect();

            // ⚠️ 必须用 (scope, query) 这个构造：`new ManagementEventWatcher(string)`
            //    会把整串当 WQL 解析，而 `new ManagementObjectSearcher(string, ObjectQuery)`
            //    这种重载根本就不存在（编译不过）。
            _watcher = new ManagementEventWatcher(scope, new EventQuery($"SELECT * FROM {_className}"));
            _watcher.EventArrived += (_, e) =>
            {
                try
                {
                    Count++;
                    LastAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    onEvent(ReadDetail(e.NewEvent));
                }
                catch (Exception ex)
                {
                    // 回调里的异常绝不能打死监听线程
                    LastError = "onEvent: " + ex.Message;
                }
            };
            // 订阅断了（WMI 服务重启等）：记下来，不自动重建 —— 重建逻辑在
            // 早先实现里踩过一串坑，这里先如实暴露，等真机验到再说。
            _watcher.Stopped += (_, e) =>
            {
                Running = false;
                LastError = "订阅停止：" + e.Status;
            };

            _watcher.Start();
            Available = true;
            Running = true;
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Available = false;
            Running = false;
            return false;
        }
    }

    /// <summary>
    /// 取 `EventDetail`。CIM 里它是 `uint8[]`：System.Management 一般给 `byte[]`，
    /// 个别包装会给 `object[]`（元素是 byte/ushort）—— 两种都兜住。
    /// </summary>
    private static byte[] ReadDetail(ManagementBaseObject ev)
    {
        try
        {
            var value = ev.Properties["EventDetail"]?.Value;
            switch (value)
            {
                case byte[] bytes:
                    return bytes;
                case object[] arr:
                {
                    var outBytes = new byte[arr.Length];
                    for (var i = 0; i < arr.Length; i++)
                        outBytes[i] = arr[i] is IConvertible c ? Convert.ToByte(c) : (byte)0;
                    return outBytes;
                }
                default:
                    return [];
            }
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        try { if (_watcher is not null) { _watcher.Stop(); } } catch { }
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        Running = false;
    }
}
