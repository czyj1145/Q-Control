using System.Runtime.InteropServices;

namespace QControl.Hal.Sys;

/// <summary>
/// `ShellExecuteEx` 的最小封装。全项目只有两个用途：
///   · 以 `runas` 拉起 **UI Access** 程序（HWiNFO64 的清单只认 ShellExecute，
///     `CreateProcess` 必返 `ERROR_ELEVATION_REQUIRED(740)`，见 <see cref="HwInfoProcess"/>）；
///   · 自己提权重启（见 <see cref="Elevation"/>）。
///
/// 为什么合并成一份：`SHELLEXECUTEINFO` 的字段顺序/宽度是 ABI 契约，而且这个结构体
/// 带 `hIcon`/`hMonitor` 联合体和 `cbSize` —— 抄错一处**编译照样通过**，只会在运行时
/// 读到垃圾指针或让 shell 直接拒绝。两份一模一样的手写 P/Invoke 就等于两份将来
/// 各自漂移的风险，所以只留一份。
/// </summary>
internal static class ShellEx
{
    /// <summary>要拿进程句柄（去查 PID）时才带；不关心子进程就别带，省一次句柄。</summary>
    public const uint MaskNoCloseProcess = 0x0000_0040;

    /// <summary>
    /// 调用方**马上就要退出**时必须带：shell 会把工作转投到别的线程上，
    /// 我们一退就没人接，`ShellExecuteEx` 会失败或把错误报给错误的窗口。
    /// </summary>
    public const uint MaskNoAsync = 0x0000_0100;

    /// <summary>出错时不要弹 shell 自己的错误框 —— 原因由调用方翻译成人话。</summary>
    public const uint MaskNoUi = 0x0000_0400;

    public const int ShowHidden = 0;
    public const int ShowNormal = 1;

    /// <summary>不激活、最小化。HWiNFO 用这个：它有自己的托盘图标，不该抢焦点。</summary>
    public const int ShowMinNoActive = 7;

    /// <summary>UAC 弹窗被点了「否」。</summary>
    public const int ErrorCancelled = 1223;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Info
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;          // 与 hMonitor 的联合体
        public IntPtr hProcess;
    }

    /// <summary>一次启动的结果。`Error` 已经是能给人看的中文，`Win32Error` 留给判断。</summary>
    public readonly record struct Result(bool Ok, int? Pid, int? Win32Error, string? Error)
    {
        /// <summary>用户点了 UAC 的「否」（不是故障，别当故障报）。</summary>
        public bool Cancelled => Win32Error == ErrorCancelled;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref Info lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 起一个进程。`verb` = "runas" 走 UAC 提权；null = 交给外壳按扩展名处理。
    /// 不抛异常：失败一律落在返回值里。
    /// </summary>
    public static Result Launch(string file, string? verb, string? parameters,
                                string? workDir, uint mask, int show)
    {
        var info = new Info
        {
            fMask = mask,
            lpVerb = verb,
            lpFile = file,
            lpParameters = parameters,
            lpDirectory = workDir,
            nShow = show,
        };
        info.cbSize = Marshal.SizeOf<Info>();

        try
        {
            if (!ShellExecuteExW(ref info))
            {
                var err = Marshal.GetLastWin32Error();
                return new Result(false, null, err, Translate(err));
            }

            int? pid = null;
            if (info.hProcess != IntPtr.Zero)
            {
                var raw = GetProcessId(info.hProcess);
                if (raw != 0) pid = (int)raw;
                CloseHandle(info.hProcess);
            }
            return new Result(true, pid, null, null);
        }
        catch (Exception e)
        {
            return new Result(false, null, null, "ShellExecuteEx 异常：" + e.Message);
        }
    }

    /// <summary>
    /// 错误码 → 人话。**不改写原因**：取消、找不到、被拒是三种完全不同的处置
    /// （等用户再点一次 / 修部署 / 修权限），糊成一句"启动失败"就没法排了。
    /// </summary>
    private static string Translate(int err) => err switch
    {
        ErrorCancelled => "UAC 弹窗被取消（winerror=1223）",
        2 or 3 => $"找不到要启动的程序（winerror={err}）",
        5 => $"拒绝访问（winerror=5）",
        _ => $"ShellExecuteEx 失败（winerror={err}）",
    };
}
