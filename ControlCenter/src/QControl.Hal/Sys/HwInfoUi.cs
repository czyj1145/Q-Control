using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace QControl.Hal.Sys;

/// <summary>
/// 把 HWiNFO「藏起来」的那套 Windows 调用：枚举窗口 / 注册表里的托盘 UID /
/// 托盘图标归属 / 隐藏 / 看门狗 / 状态报告。
///
/// **为什么要藏**：HWiNFO 是 uiAccess + requireAdministrator 的程序，一启动就有
/// 主窗口，还会往托盘放一个图标。控制中心只要它的读数，不要它的界面 ——
/// 用户看到的是「后台莫名多了个 HWiNFO 窗口和图标」。
///
/// **归属铁律（2026-10-14 用户定）**：只碰**我们自己拉起的**那只
/// （<see cref="HwInfoProcess.OursRunning"/>）。用户自己开的 HWiNFO 窗口和托盘图标
/// 是用户的东西 —— 控制中心跑在管理员态，UIPI 拦不住我们的 ShowWindow / NIM_DELETE，
/// 一旦作用到用户的实例上就是「把用户开的东西弄没了」。
///
/// ⚠️ 三个必须照抄的坑（早先实现踩过）：
///   ① **`EnumWindows` 不枚举 message-only 窗口**（`HWND_MESSAGE` 的子窗口）。
///      托盘图标完全可能挂在那种窗口上，只看顶层窗口会得出「它没有托盘图标」的
///      错误结论 ⇒ message-only 扫描必须做，且**同样遵守归属过滤**。
///   ② HWiNFO 的托盘 uID 是 **0xFFFFFFFE**，不是小整数。只扫 0..255 会永远
///      「找不到」，而它明明就在托盘溢出区里 ⇒ 先从注册表拿 uID，兜底才枚举。
///   ③ Win11 的托盘是 XAML，**没有 `ToolbarWindow32` 可读**。判断「图标还在不在」
///      只有一条通用解：`Shell_NotifyIconGetRect` —— 图标真实存在才返回 S_OK。
/// </summary>
public static class HwInfoUi
{
    private const int SwHide = 0;
    private const uint NimDelete = 2;
    private const int HideWatchSeconds = 30;

    /// <summary>HWiNFO 的托盘 uID（2026-10-14 从注册表查实）+ 几个常见特殊值。</summary>
    private static readonly uint[] SpecialTrayUids = [0xFFFFFFFE, 0xFFFFFFFF, 0xFFFFFFFD, 0xFFFFFFFC];

    private const double UidCacheSeconds = 300;

    private static readonly object Gate = new();
    private static readonly List<string> HideEvents = [];
    private static uint[] _uidCache = [];
    private static double _uidAt;
    private static DateTime _watchUntil = DateTime.MinValue;

    // ================================================================ 窗口 / 托盘

    /// <summary>一个属于 HWiNFO 的窗口。</summary>
    public sealed record WinInfo(IntPtr Hwnd, string Class, string Title, bool Visible);

    /// <summary>
    /// HWiNFO 的窗口 → 列表。默认覆盖所有 HWiNFO 实例（诊断口要看全貌）；
    /// **做隐藏动作时必须显式传我们自己的 PID**（见 <see cref="Hide"/>）。
    ///
    /// message-only 窗口以 `Visible = false`、`Title = ""` 出现（它们本来就不显示）。
    /// </summary>
    public static List<WinInfo> Windows(IReadOnlyCollection<int>? pidsFilter = null)
    {
        var want = pidsFilter is null ? HwInfoProcess.Pids() : pidsFilter.ToArray();
        var outList = new List<WinInfo>();
        if (want.Length == 0) return outList;
        var set = want.ToHashSet();

        EnumWindows((hwnd, _) =>
        {
            if (PidOf(hwnd) is { } pid && set.Contains(pid))
                outList.Add(new WinInfo(hwnd, ClassOf(hwnd), TitleOf(hwnd), IsWindowVisible(hwnd)));
            return true;
        }, IntPtr.Zero);

        // ⚠️ message-only 窗口：`EnumWindows` 看不到它们，只能从 HWND_MESSAGE 往下找
        var prev = IntPtr.Zero;
        while (true)
        {
            var hwnd = FindWindowExW(HwndMessage, prev, null, null);
            if (hwnd == IntPtr.Zero) break;
            prev = hwnd;
            if (PidOf(hwnd) is { } pid && set.Contains(pid))
                outList.Add(new WinInfo(hwnd, ClassOf(hwnd), "", false));
        }

        return outList;
    }

    /// <summary>
    /// `HKCU\Control Panel\NotifyIconSettings` 里登记的 HWiNFO uID。
    ///
    /// Win11 把**每个**托盘图标的登记信息（可执行文件路径 + uID + 图标快照）都记在
    /// 这里，比穷举 uID 可靠得多。HWiNFO 不用 GUID（没有 IconGuid 值），uID 就是答案。
    /// 缓存 5 分钟 —— 看门狗每秒都要问一次，不能每秒读一遍注册表。
    /// </summary>
    public static uint[] RegistryUids()
    {
        lock (Gate)
        {
            var now = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            if (_uidAt > 0 && now - _uidAt < UidCacheSeconds) return _uidCache;
        }

        var uids = new List<uint>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root is not null)
            {
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(name);
                        if (sub is null) continue;

                        var exe = sub.GetValue("ExecutablePath") as string;
                        if (string.IsNullOrEmpty(exe)) continue;
                        if (!exe.Replace('/', '\\').ToLowerInvariant()
                                .EndsWith(@"\" + HwInfoProcess.ExeName.ToLowerInvariant(),
                                          StringComparison.Ordinal))
                            continue;

                        // UID 是 REG_DWORD；万一将来变成别的类型，这里如实跳过（不猜）
                        if (sub.GetValue("UID") is not { } raw) continue;
                        var v = raw switch
                        {
                            int i => (uint)i,
                            uint u => u,
                            long l => (uint)l,
                            _ => Convert.ToUInt32(raw),
                        };
                        if (!uids.Contains(v)) uids.Add(v);   // 图吧工具箱那份用的是同一个 uID
                    }
                    catch
                    {
                        // 单个子键读不到不影响其它 —— 注册表这类枚举本来就允许残缺
                    }
                }
            }
        }
        catch
        {
            uids.Clear();
        }

        lock (Gate)
        {
            _uidCache = uids.ToArray();
            _uidAt = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        }
        return _uidCache;
    }

    /// <summary>
    /// 仍在托盘里注册着的图标 → `(hwnd, uid, class, rect)`。正常应为空。
    ///
    /// 顺序：① 注册表里登记的 uID（精准，通常就一个 0xFFFFFFFE）；
    ///       ② 兜底：小 uID 枚举 + 几个特殊值。
    /// ①命中就不再扫 —— 看门狗每秒都要跑，不能每次都穷举一遍。
    /// </summary>
    public static List<(IntPtr Hwnd, uint Uid, string Class, (int L, int T, int R, int B) Rect)>
        TrayIcons(IReadOnlyCollection<int>? pidsFilter = null)
    {
        var wins = Windows(pidsFilter);
        var outList = new List<(IntPtr, uint, string, (int, int, int, int))>();
        if (wins.Count == 0) return outList;

        var known = RegistryUids();
        if (known.Length > 0)
        {
            foreach (var w in wins)
            {
                foreach (var uid in known)
                {
                    if (IconRect(w.Hwnd, uid) is { } r)
                        outList.Add((w.Hwnd, uid, w.Class, r));
                }
            }
            if (outList.Count > 0) return outList;
        }

        var fallback = Enumerable.Range(0, 65).Select(i => (uint)i).Concat(SpecialTrayUids);
        foreach (var w in wins)
        {
            foreach (var uid in fallback)
            {
                if (IconRect(w.Hwnd, uid) is { } r)
                    outList.Add((w.Hwnd, uid, w.Class, r));
            }
        }
        return outList;
    }

    // ================================================================ 藏

    /// <summary>
    /// 把它彻底藏起来：可见窗口按下去 + 注册到托盘的图标抹掉。返回 `(窗口数, 图标数)`。
    ///
    /// ⚠️ **默认只作用于我们自己拉起的实例**（`pidsFilter = null` ⇒ 我们那只）。
    /// 用户自己开的 HWiNFO 一概不碰 —— 见类注释里的归属铁律。
    /// ⚠️ 非管理员时 UIPI 会拦掉 `ShowWindow` / `NIM_DELETE`：如实返回 0，
    ///    不假装藏住了（取数走共享内存，不依赖窗口隐藏）。
    /// </summary>
    public static (int Windows, int Icons) Hide(IReadOnlyCollection<int>? pidsFilter = null)
    {
        int winCount = 0, iconCount = 0;
        var filter = pidsFilter ?? OursOnly();
        if (filter.Count == 0) return (0, 0);          // 不是我们拉的 ⇒ 什么都不许碰

        foreach (var w in Windows(filter))
        {
            if (!w.Visible) continue;
            if (!ShowWindow(w.Hwnd, SwHide)) continue;  // 返回「之前是否可见」，false = 本来就没显示
            winCount++;
            Log($"窗口 {w.Class} / {w.Title}");
        }

        foreach (var (hwnd, uid, cls, _) in TrayIcons(filter))
        {
            if (!DeleteTrayIcon(hwnd, uid)) continue;
            iconCount++;
            Log($"托盘图标 {cls} / uID={uid}");
        }

        return (winCount, iconCount);
    }

    /// <summary>
    /// 启动后的一小段时间里反复收掉**我们自己那只**的窗口 / 托盘图标（后台线程）。
    ///
    /// 为什么需要「一段时间」而不是藏一次就完：HWiNFO 启动时先弹欢迎/进度窗口，
    /// 之后才建主窗口；托盘图标也是稍后才注册的。只藏一次几乎必然漏掉。
    ///
    /// ⚠️ **单例防重入**：ensure 与诊断口每次被调都起一条 30 秒看门狗，
    ///    每条每秒枚举一次窗口 —— 窗口期内重复调用直接返回（2026-10-14 修过同一个问题）。
    /// </summary>
    public static void HideWatchdog(int seconds = HideWatchSeconds)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (now < _watchUntil) return;              // 已有一条在看：不叠加
            _watchUntil = now.AddSeconds(seconds);
        }

        new Thread(() =>
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                try
                {
                    // ⚠️ 只在「现在跑着的确实是我们拉的」时才动手
                    if (HwInfoProcess.OursRunning()) Hide();
                }
                catch
                {
                    // 看门狗不抛：它跑在后台线程上，抛出去没人接
                }
                Thread.Sleep(1000);
            }
        })
        { IsBackground = true, Name = "hwinfo-hide" }.Start();
    }

    // ================================================================ 诊断口

    /// <summary>`ui_report()`：窗口 / 托盘图标现状（**覆盖所有实例**，含用户自己开的）。</summary>
    public static JsonObject Report()
    {
        var pids = HwInfoProcess.Pids();
        var ours = HwInfoProcess.OursRunning();
        var winArr = new JsonArray();
        foreach (var w in Windows())
        {
            winArr.Add(new JsonObject
            {
                ["hwnd"] = "0x" + w.Hwnd.ToInt64().ToString("X8"),
                ["class"] = w.Class,
                ["title"] = w.Title,
                ["visible"] = w.Visible,
                ["ours"] = ours && PidOf(w.Hwnd) == OursPid(),
            });
        }

        var iconArr = new JsonArray();
        foreach (var (hwnd, uid, cls, r) in TrayIcons())
        {
            iconArr.Add(new JsonObject
            {
                ["hwnd"] = "0x" + hwnd.ToInt64().ToString("X8"),
                ["uid"] = "0x" + uid.ToString("X8"),
                ["class"] = cls,
                ["rect"] = new JsonArray(r.L, r.T, r.R, r.B),
            });
        }

        var uidArr = new JsonArray();
        foreach (var uid in RegistryUids()) uidArr.Add("0x" + uid.ToString("X8"));

        var logArr = new JsonArray();
        lock (Gate) foreach (var line in HideEvents) logArr.Add(line);

        return new JsonObject
        {
            ["pid"] = new JsonArray(pids.Select(p => JsonValue.Create(p)).ToArray()),
            ["ours_pid"] = OursPid() is { } op ? JsonValue.Create(op) : null,
            ["ours_running"] = ours,
            ["windows"] = winArr,
            ["visible_windows"] = Windows().Count(w => w.Visible),
            ["registry_uids"] = uidArr,
            ["tray_icons"] = iconArr,
            ["hidden_log"] = logArr,
        };
    }

    // ================================================================ 内部

    private static List<int> OursOnly()
        => HwInfoProcess.OursRunning() && OursPid() is { } id ? [id] : [];

    private static int? OursPid() => HwInfoProcess.OurPid;

    private static void Log(string line)
    {
        lock (Gate)
        {
            HideEvents.Add(line);
            if (HideEvents.Count > 8) HideEvents.RemoveRange(0, HideEvents.Count - 8);
        }
    }

    private static int? PidOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buf = new StringBuilder(256);
        GetClassNameW(hwnd, buf, buf.Capacity);
        return buf.ToString();
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var buf = new StringBuilder(256);
        GetWindowTextW(hwnd, buf, buf.Capacity);
        return buf.ToString();
    }

    /// <summary>
    /// 托盘图标存在 → 返回它的屏幕矩形；不存在 → null。
    ///
    /// ⚠️ `Shell_NotifyIconGetRect` **只在图标真实存在时返回 S_OK** ——
    /// 这就是我们要的判据（比枚举托盘工具栏可靠，也不碰别人的进程）。
    /// </summary>
    private static (int L, int T, int R, int B)? IconRect(IntPtr hwnd, uint uid)
    {
        var id = new NotifyIconIdentifier
        {
            cbSize = Marshal.SizeOf<NotifyIconIdentifier>(),
            hWnd = hwnd,
            uID = uid,
        };
        return Shell_NotifyIconGetRect(ref id, out var rc) == 0
            ? (rc.Left, rc.Top, rc.Right, rc.Bottom)
            : null;
    }

    /// <summary>
    /// 抹掉一个托盘图标（`Shell_NotifyIcon(NIM_DELETE)`）。
    ///
    /// ⚠️ 不声明 `NOTIFYICONDATAW` 结构体，手写三个字段：这个结构体有 V1/V2/V3/V4
    ///    四种大小，而 `NIM_DELETE` 只读 `cbSize / hWnd / uID` 三个字段，且三者
    ///    在**所有版本里偏移都一样**（0 / 8 / 16，x64）。用一个清零的缓冲区按偏移
    ///    写进去，比维护一个含 `ByValTStr` 的结构体安全 —— 后者字段是 `string`，
    ///    没初始化时 marshaler 会抛异常。
    /// </summary>
    private static bool DeleteTrayIcon(IntPtr hwnd, uint uid)
    {
        const int notifyIconDataV4Size = 984;      // x64 下 sizeof(NOTIFYICONDATAW)（V4）
        var buf = Marshal.AllocHGlobal(notifyIconDataV4Size);
        try
        {
            for (var i = 0; i < notifyIconDataV4Size; i += 8)
                Marshal.WriteInt64(buf, i, 0);

            Marshal.WriteInt32(buf, 0, notifyIconDataV4Size);       // cbSize
            Marshal.WriteIntPtr(buf, 8, hwnd);                      // hWnd
            Marshal.WriteInt32(buf, 16, unchecked((int)uid));       // uID

            return Shell_NotifyIconW(NimDelete, buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ================================================================ P/Invoke

    private static readonly IntPtr HwndMessage = new(-3);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buf, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buf, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier id, out NativeRect rect);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, IntPtr data);
}
