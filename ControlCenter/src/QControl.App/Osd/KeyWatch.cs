using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace QControl.App.Osd;

/// <summary>
/// 系统开关类提示：CapsLock / NumLock / ScrollLock / 触控板 / 麦克风静音。
///
/// 为什么走"轮询系统状态"而不是 EC 事件（2026-11-25 实测）：
///   触控板、摄像头、麦克风、FnLock 四个键按下去，EC 只抛同一种事件 `evid=0x06`，
///   detail 里只有第 7 个字节在 01/02 之间跳，**没有键 ID**；`0x0200` 读组整段 32 字节
///   一个字节都没变。也就是说事件里没有"这是哪个键、现在是开还是关"的信息。
///   原厂 `HkeyOSDSvis.exe` 同样得另找状态源（它读 `IAudioEndpointVolume` 就是这么干的）。
///
/// 代价：每 250 ms 读三个键灯（`GetKeyState`，只是查内存），每 1 s 读一次触控板注册表
///   和麦克风静音。这条线程优先级低于普通，出错一律吞掉 —— OSD 是锦上添花，
///   绝不能把后端拖死（与 <see cref="OsdWindow"/> 同一原则）。
///
/// ⚠️ 第一轮只建基线、不弹：否则一开机就连续弹三条"大写锁定 已关闭"。
/// </summary>
internal sealed class KeyWatch
{
    private const int FastMs = 250;     // 三个键灯
    private const int SlowMs = 1000;    // 触控板 / 麦克风

    /// <summary>CapsLock / NumLock / ScrollLock 的虚拟键码。</summary>
    private const int VkCapital = 0x14;
    private const int VkNumLock = 0x90;
    private const int VkScroll = 0x91;

    private const string TouchpadKey =
        @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";

    private readonly OsdWindow _osd;
    private readonly Thread _thread;
    private volatile bool _stop;

    // 上一轮的值。null = 还没建基线。
    private bool? _caps, _num, _scroll, _touchpad, _mic;
    private bool _micBroken;        // COM 连续失败到上限后就不再试（别每秒抛一次）
    private int _micFails;          // 连续失败次数（插拔耳机那一下会瞬时失败，别一次就判死）

    public KeyWatch(OsdWindow osd)
    {
        _osd = osd;
        _thread = new Thread(Loop)
        {
            IsBackground = true,        // 进程退出就跟着走，不需要 Dispose
            Name = "keywatch",
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public void Start()
    {
        _thread.Start();
    }

    /// <summary>退出时叫停。线程是后台线程，进程直接退出也不会挂住；这里只是让退出更干净。</summary>
    public void Stop()
    {
        _stop = true;
    }

    private void Loop()
    {
        var lastSlow = 0L;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (!_stop)
        {
            try
            {
                // ---- 三个键灯（便宜）：一次 attach 读三个，见 ReadLocks ----
                var (caps, num, scroll) = ReadLocks();

                Emit(ref _caps, caps, "caps", "大写锁定", caps ? "已开启" : "已关闭");
                Emit(ref _num, num, "num", "数字键盘", num ? "已开启" : "已关闭");
                Emit(ref _scroll, scroll, "scroll", "滚动锁定", scroll ? "已开启" : "已关闭");

                // ---- 触控板 / 麦克风（较贵，1 s 一次）----
                if (clock.ElapsedMilliseconds - lastSlow >= SlowMs)
                {
                    lastSlow = clock.ElapsedMilliseconds;

                    if (TouchpadEnabled() is { } pad)
                        Emit(ref _touchpad, pad, "touchpad", "触控板", pad ? "已开启" : "已关闭");

                    if (!_micBroken && MicMuted() is { } muted)
                        Emit(ref _mic, muted, "mic", "麦克风", muted ? "已静音" : "已开启");
                }
            }
            catch
            {
                // 轮询里任何异常都不该影响别的：抛了就等下一轮
            }

            Thread.Sleep(FastMs);
        }
    }

    /// <summary>
    /// 值变了就弹一条。第一次读到（prev 为 null）只建基线。
    /// 直接调 <see cref="OsdWindow.Show"/>，不经过热键的 1.5 s 去重 ——
    /// 这里每次变化都是真的状态翻转，不该被吞。
    /// </summary>
    private void Emit(ref bool? prev, bool now, string kind, string title, string subtitle)
    {
        var before = prev;
        prev = now;
        if (before is null || before.Value == now) return;
        // 诊断（2026-11-26「连弹两次」）：一次物理按键只该有**一行**。
        // 若这里出现两行，说明状态源本身抖了；只有一行则第二次是原厂 OSD 画的。
        Console.WriteLine($"[keywatch] {kind} {before}->{now}");
        if (!_osd.Enabled) return;
        _osd.Show(kind, title, subtitle);
    }

    // ---------------------------------------------------------------- 状态源

    /// <summary>
    /// 一次性读三个键灯（Caps / Num / Scroll）。
    ///
    /// <para>
    /// `GetKeyState` 的 `0x0001` 位才是**当前锁定状态**（灯亮/灭），这就是我们要的。
    /// 本机实测：后台线程直接调用就能读到真实灯态（探针读到 `num=1`）；另外
    /// `AttachThreadInput` 在这台机器上返回 0（失败），所以不需要、也不该附加线程。
    /// </para>
    ///
    /// <para>
    /// ⚠️ **别改用 `GetAsyncKeyState` 的 `0x0001`**（2026-11-26 踩过）：那个位是
    /// "上次调用以来是否被按过"，下一拍自己清 0。用它做判据，一次物理按键会连弹两条
    /// ——先「已开启」再「已关闭」——用户看到的就是"连弹两次、第二次才对"。
    /// </para>
    /// </summary>
    private static (bool Caps, bool Num, bool Scroll) ReadLocks()
        => ((GetKeyState(VkCapital) & 0x0001) != 0,
            (GetKeyState(VkNumLock) & 0x0001) != 0,
            (GetKeyState(VkScroll) & 0x0001) != 0);

    /// <summary>
    /// 触控板开关。Windows 的精准触控板状态存在这里，FN 键切换时系统会同步改写。
    /// 读不到（老式触控板 / 没这个键）返回 null —— 那就永远不弹，别猜。
    /// </summary>
    private static bool? TouchpadEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TouchpadKey);
            if (key?.GetValue("Enabled") is not { } v) return null;
            return Convert.ToInt32(v) != 0;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 默认录音端点是否静音。走 CoreAudio 的 `IAudioEndpointVolume::GetMute`。
    ///
    /// ⚠️ **连续 3 次**失败才认定这台机器没有可用录音端点 / COM 被拦，然后不再重试
    /// （2026-11-26 改）：插拔耳机、切默认录音设备的那一两秒，COM 会瞬时拿不到端点，
    /// 一次失败就永久关掉的话，用户会以为"麦克风提示坏了"、只能重启程序。
    /// 成功一次就把计数清零。全程单线程（轮询线程），不需要锁。
    /// </summary>
    private bool? MicMuted()
    {
        try
        {
            var muted = MicProbe.GetMute();
            if (muted is not null) _micFails = 0;
            return muted;
        }
        catch
        {
            if (++_micFails >= 3) _micBroken = true;
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);
}

/// <summary>
/// 读默认录音端点静音状态的最小 CoreAudio 封装。
///
/// 只用到三个接口的一段 vtable，**方法顺序必须与 SDK 头文件逐字一致** ——
/// 少一个占位方法，后面的函数指针就整体错位，调用会直接崩（这不是能 catch 的异常）。
/// 顺序：IMMDeviceEnumerator{EnumAudioEndpoints, GetDefaultAudioEndpoint, GetDevice,
/// RegisterEndpointNotificationCallback, UnregisterEndpointNotificationCallback}；
/// IMMDevice{Activate, OpenPropertyStore, GetId, GetState}；
/// IAudioEndpointVolume{RegisterControlChangeNotify, UnregisterControlChangeNotify,
/// GetChannelCount, SetMasterVolumeLevel, SetMasterVolumeLevelScalar, GetMasterVolumeLevel,
/// GetMasterVolumeLevelScalar, SetChannelVolumeLevel, SetChannelVolumeLevelScalar,
/// GetChannelVolumeLevel, GetChannelVolumeLevelScalar, SetMute, GetMute}。
/// </summary>
internal static class MicProbe
{
    private const int EConsole = 0;
    private const int ECapture = 1;

    private static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public static bool? GetMute()
    {
        object? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidMmDeviceEnumerator)!);
            if (enumerator is not IMMDeviceEnumerator en) return null;
            if (en.GetDefaultAudioEndpoint(ECapture, EConsole, out device) != 0 || device is null)
                return null;

            var iid = IidAudioEndpointVolume;
            if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var epv) != 0
                || epv == IntPtr.Zero)
                return null;

            try
            {
                volume = (IAudioEndpointVolume)Marshal.GetObjectForIUnknown(epv);
                var hr = volume.GetMute(out var mute);
                return hr == 0 ? mute : null;
            }
            finally
            {
                Marshal.Release(epv);
            }
        }
        finally
        {
            if (volume is not null) Marshal.ReleaseComObject(volume);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
