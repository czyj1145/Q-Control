using System.Runtime.InteropServices;

namespace QControl.Hal.Sys;

/// <summary>
/// 笔记本盖子状态（合盖 = 内建屏断电）。订阅 `GUID_LIDSWITCH_STATE_CHANGE` 电源事件：
/// 注册时系统立刻回报一次当前值，之后每开 / 合一次回调一次；这里只把它缓存成一个 int。
///
/// 为什么需要它（2026-11-05 用户问「外接屏幕后使用正常吗」）：
/// 屏幕亮度只对**内建屏**有效。合盖只用外接屏时，WMI 的亮度实例通常还在、
/// `WmiSetBrightness` 也照样返回成功、回读还是那个旧值 —— 界面会显示"已调到 60%"，
/// 其实那块屏压根没亮。有了盖子状态，界面就能把那一行置灰并说明原因。
///
/// 三条边界：
///   ① 回调在**系统线程**上：只做一次 `Interlocked.Exchange`，绝不碰 UI、不阻塞、不抛；
///   ② 注册失败或系统不报这个事件（台式机 / 老机型）⇒ <see cref="IsClosed"/> 一直是 null，
///      调用方当"不知道"处理（不置灰、不提示）—— 拿不准就别装作知道；
///   ③ 委托必须自己保引用，否则 GC 之后系统会回调到野指针。
/// </summary>
public static class LidSwitch
{
    private static readonly Guid LidStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    /// <summary>`DEVICE_NOTIFY_CALLBACK`：用回调（而不是窗口 / 服务句柄）收通知。</summary>
    private const uint DeviceNotifyCallback = 2;

    /// <summary>`PBT_POWERSETTINGCHANGE`。</summary>
    private const int PbtPowerSettingChange = 0x8013;

    /// <summary>`POWER_SETTING_VALUE` 里 Data 的偏移：GUID(16) + DataLength(4)。</summary>
    private const int DataOffset = 20;

    private static readonly object Gate = new();

    // ⚠️ 静态字段持有委托：不保引用的话 GC 一收，系统回调就是野指针。
    private static LidNotifyCallback? _callback;

    private static bool _subscribed;

    /// <summary>-1 = 还不知道 / 0 = 盖子开着 / 1 = 合上了。</summary>
    private static int _state = -1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint LidNotifyCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSettingRegisterNotification(
        ref Guid settingGuid, uint flags, ref NotifyParameters recipient, out IntPtr handle);

    /// <summary>
    /// 盖子是否合上。**null = 不知道**（没订阅成功 / 这台机器不报这个事件），
    /// 调用方不要把它当 false。
    /// </summary>
    public static bool? IsClosed => _state switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

    /// <summary>
    /// 订阅一次（幂等）。⚠️ 注册调用本身就会同步回调一次当前状态，
    /// 所以 `_subscribed` 必须**先置位**再注册，否则那次回调会看到"还没订阅"。
    /// </summary>
    public static void EnsureSubscribed()
    {
        if (_subscribed) return;

        lock (Gate)
        {
            if (_subscribed) return;
            _subscribed = true;

            try
            {
                _callback = OnNotify;
                var parameters = new NotifyParameters
                {
                    Callback = Marshal.GetFunctionPointerForDelegate(_callback),
                    Context = IntPtr.Zero,
                };

                // ⚠️ 不能直接 `ref` 静态只读字段（CS0199），先抄一份到局部
                var guid = LidStateChange;
                var rc = PowerSettingRegisterNotification(
                    ref guid, DeviceNotifyCallback, ref parameters, out _);

                if (rc != 0)
                    Console.WriteLine($"[lid] 订阅盖子状态失败（PowerSettingRegisterNotification={rc}）："
                                      + "亮度行不会因合盖置灰");
                else
                    Console.WriteLine("[lid] 已订阅盖子状态（开合盖会实时回报）");
            }
            catch (Exception e)
            {
                // 老系统没有这个导出 / 权限异常 ⇒ 当"不知道"，界面按不置灰处理
                Console.WriteLine("[lid] 订阅盖子状态异常：" + e.Message);
            }
        }
    }

    /// <summary>⚠️ 系统线程回调：只读两个整数、写一个 int，不做任何会抛的事。</summary>
    private static uint OnNotify(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type != PbtPowerSettingChange || setting == IntPtr.Zero) return 0;
            if (Marshal.ReadInt32(setting, 16) < 1) return 0;          // DataLength

            // Data[0]：0 = 合上，1 = 打开（GUID_LIDSWITCH_STATE_CHANGE 的约定）
            var closed = Marshal.ReadByte(setting, DataOffset) == 0;
            Interlocked.Exchange(ref _state, closed ? 1 : 0);
        }
        catch
        {
            // 系统线程上绝不能往外抛
        }
        return 0;
    }
}
