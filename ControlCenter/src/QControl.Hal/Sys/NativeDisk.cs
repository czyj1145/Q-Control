using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QControl.Hal.Sys;

/// <summary>
/// 一块物理盘的**原生**档案（IOCTL 直读，不经 WMI）。
/// </summary>
/// <param name="Index">物理盘号（<c>PHYSICALDRIVEn</c> 的 n）。</param>
/// <param name="Model">型号串（等价 <c>Win32_DiskDrive.Model</c>）。</param>
/// <param name="Serial">序列号，取不到时为空串。</param>
/// <param name="Firmware">固件版本，取不到时为空串。</param>
/// <param name="BusType">STORAGE_BUS_TYPE 原值（7=USB、11=SATA、17=NVMe）。</param>
/// <param name="Removable">可移动介质（U 盘 / 读卡器）。</param>
/// <param name="SeekPenalty">true=HDD、false=SSD、null=设备没报（虚拟盘/部分 U 盘）。</param>
/// <param name="SizeBytes">容量字节数，0 = 取不到。</param>
public sealed record NativeDiskInfo(
    int Index,
    string Model,
    string Serial,
    string Firmware,
    int BusType,
    bool Removable,
    bool? SeekPenalty,
    long SizeBytes);

/// <summary>
/// 物理盘 / 卷的原生枚举 —— 全部走 <c>DeviceIoControl</c>，**毫秒级以内**。
///
/// 为什么不用 WMI（2026-10-23）：原来 <c>DiskSensors</c> 走
/// <c>Win32_DiskDrive</c> + <c>MSFT_PhysicalDisk</c> + 两张关联表，
/// 一次全量 60~80 ms，只能靠 300 秒 TTL 压住开销 —— 代价是
/// **U 盘插上/拔出要等 5 分钟才反映**（用户原话「插上和拔出没反应」）。
/// 换成 IOCTL 后整轮 0.05 ms（快 1000 倍），可以**每秒都跑**，
/// 插拔在下一帧（≤1 s）就出现在界面上，根本不需要事件监听。
///
/// ⚠️ 三个坑：
/// <list type="number">
/// <item><c>DeviceIoControl</c> 要 <c>CreateFile</c> 先拿到句柄，且
///   <see cref="FileAccess"/> 一律传 0（只查属性不要读写权限，否则普通用户拿不到 U 盘句柄）；</item>
/// <item>盘符要拼成 <c>\\.\C:</c>（**不带**反斜杠），物理盘是 <c>\\.\PhysicalDriveN</c>；</item>
/// <item><c>STORAGE_DEVICE_DESCRIPTOR</c> 的字符串字段是**偏移量**不是指针，
///   要自己从输出缓冲区里按偏移抠 ASCII 串，且偏移 0 表示没有该项。</item>
/// </list>
/// </summary>
public static class NativeDisk
{
    // ---------------------------------------------------------------- IOCTL 常量

    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlVolumeGetDiskExtents = 0x00560000;

    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;

    private const uint OpenExisting = 3;
    private const uint ShareReadWrite = 3;

    /// <summary>
    /// 打开句柄只要 `FILE_READ_ATTRIBUTES`（0x80），**不能**给 `GENERIC_READ`。
    /// 后者对 `\\.\PhysicalDriveN` 要管理员，普通用户直接 AccessDenied；
    /// 而 0x80 足够跑下面三个 IOCTL，且与「读不需要管理员」的既有铁律一致。
    /// </summary>
    private const uint ReadAttributes = 0x0080;

    /// <summary>物理盘号上限。Windows 默认最多 16 个盘位，留到 32 够用。</summary>
    private const int MaxDrives = 32;

    // ---------------------------------------------------------------- P/Invoke

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code,
        byte[]? inBuf, int inSize, byte[] outBuf, int outSize, out int ret, IntPtr ov);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetLogicalDriveStringsW(int len, [Out] char[] buf);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    private const uint DriveRemote = 4;

    // ---------------------------------------------------------------- 结构体

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public int Version;
        public int Size;
        [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceDescriptor
    {
        public int Version;
        public int Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.U1)] public bool RemovableMedia;
        [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
        public int VendorIdOffset;
        public int ProductIdOffset;
        public int ProductRevisionOffset;
        public int SerialNumberOffset;
        public int BusType;
        public int RawPropertiesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskExtent
    {
        public int DiskNumber;
        public int Pad;
        public long StartingOffset;
        public long ExtentLength;
    }

    // ================================================================ 公开接口

    /// <summary>
    /// 枚举全部物理盘。**按盘号升序**，跳过打不开的盘位。
    /// 耗时实测 0.05 ms（单盘），可以每帧调。
    /// </summary>
    public static List<NativeDiskInfo> Enumerate()
    {
        var list = new List<NativeDiskInfo>();
        for (var i = 0; i < MaxDrives; i++)
        {
            using var h = CreateFileW($@"\\.\PhysicalDrive{i}", ReadAttributes, ShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;

            // ① 型号 / 固件 / 序列号 / 总线 / 可移动
            var q = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceProperty,
                QueryType = PropertyStandardQuery,
            };
            var inBuf = ToBytes(q);
            var outBuf = new byte[1024];
            if (!DeviceIoControl(h, IoctlStorageQueryProperty, inBuf, inBuf.Length,
                    outBuf, outBuf.Length, out var _, IntPtr.Zero))
            {
                // ⚠️ 描述符比缓冲区大时第一次必然失败（ERROR_INSUFFICIENT_BUFFER = 122），
                //    但 `Size` 已经写回来了。以前这里直接 `continue` ⇒ 这类盘整块消失，
                //    而它在指纹里不出现就等于"插上/拔下没反应"，磁盘卡片永远不刷新（2026-11-12 修）。
                var need = FromBytes<StorageDeviceDescriptor>(outBuf).Size;
                if (Marshal.GetLastWin32Error() != 122 || need <= outBuf.Length || need > 64 * 1024)
                    continue;
                outBuf = new byte[need];
                if (!DeviceIoControl(h, IoctlStorageQueryProperty, inBuf, inBuf.Length,
                        outBuf, outBuf.Length, out var _, IntPtr.Zero))
                    continue;
            }

            var d = FromBytes<StorageDeviceDescriptor>(outBuf);
            var model = Str(outBuf, d.ProductIdOffset);
            // ⚠️ 没有 ProductId 的设备（廉价 USB-SATA 桥 / 读卡器 / 部分虚拟盘只填 VendorId）
            //    也要建条目：它不进列表 ⇒ 指纹不变 ⇒ 插拔不反映（2026-11-12 修）。
            if (model.Length == 0) model = Str(outBuf, d.VendorIdOffset);
            if (model.Length == 0) model = $"PhysicalDrive{i}";

            // ② 容量
            long size = 0;
            var lenBuf = new byte[8];
            if (DeviceIoControl(h, IoctlDiskGetLengthInfo, null, 0,
                    lenBuf, lenBuf.Length, out var _, IntPtr.Zero))
                size = BitConverter.ToInt64(lenBuf, 0);

            // ③ 机械 / 固态（部分 U 盘不支持这题）
            bool? seekPenalty = null;
            var q2 = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
            };
            var in2 = ToBytes(q2);
            var out2 = new byte[64];
            if (DeviceIoControl(h, IoctlStorageQueryProperty, in2, in2.Length,
                    out2, out2.Length, out var _, IntPtr.Zero))
                seekPenalty = FromBytes<DeviceSeekPenaltyDescriptor>(out2).IncursSeekPenalty;

            list.Add(new NativeDiskInfo(
                Index: i,
                Model: model,
                Serial: Str(outBuf, d.SerialNumberOffset),
                Firmware: Str(outBuf, d.ProductRevisionOffset),
                BusType: d.BusType,
                Removable: d.RemovableMedia,
                SeekPenalty: seekPenalty,
                SizeBytes: size));
        }
        return list;
    }

    /// <summary>
    /// 盘号 → 盘符列表（如 <c>0 → [C:, D:, E:]</c>）。
    /// 走 <c>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS</c>，每个盘符 50 µs。
    /// 网络盘跳过（它们没有本地扩展区）。
    /// </summary>
    public static Dictionary<int, List<string>> Volumes()
    {
        var map = new Dictionary<int, List<string>>();
        if (DriveRoots() is not { Count: > 0 } roots) return map;

        foreach (var root in roots)
        {
            if (GetDriveTypeW(root) == DriveRemote) continue;

            var letter = root.TrimEnd('\\');
            if (letter.Length < 2) continue;

            using var h = CreateFileW($@"\\.\{letter}", ReadAttributes, ShareReadWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;

            var buf = new byte[8 + 24 * 8];   // 最多 8 个扩展区，够任何本地卷
            if (!DeviceIoControl(h, IoctlVolumeGetDiskExtents, null, 0,
                    buf, buf.Length, out var _, IntPtr.Zero))
                continue;

            var count = BitConverter.ToInt32(buf, 0);
            if (count <= 0 || count > 8) continue;

            var disk = BitConverter.ToInt32(buf, 8);   // 第一个扩展区的 DiskNumber
            if (!map.TryGetValue(disk, out var lst))
                map[disk] = lst = [];
            lst.Add(letter);
        }

        foreach (var lst in map.Values) lst.Sort(StringComparer.Ordinal);
        return map;
    }

    /// <summary>
    /// 全部盘符根（`C:\`、`D:\`…）。
    ///
    /// ⚠️ **必须用 <c>char[]</c> 接，不能用 <see cref="StringBuilder"/>**：
    /// `GetLogicalDriveStrings` 返回的是**多重字符串**（`C:\0D:\0E:\0\0`），
    /// 而 .NET 的 StringBuilder 封送按普通 LPWSTR 处理，**读到第一个 \0 就截断** ——
    /// 于是永远只有 `C:`，D:/E: 的卷映射全丢（2026-10-23 实测，且不报错，最难查）。
    /// </summary>
    public static List<string> DriveRoots()
    {
        var buf = new char[512];
        var len = GetLogicalDriveStringsW(buf.Length, buf);
        if (len <= 0 || len > buf.Length) return [];
        return new string(buf, 0, len)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3)
            .ToList();
    }

    // ---------------------------------------------------------------- 辅助

    private static string Str(byte[] buf, int off)
    {
        if (off <= 0 || off >= buf.Length) return "";
        var end = off;
        while (end < buf.Length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, off, end - off).Trim();
    }

    private static byte[] ToBytes<T>(T s) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var arr = new byte[size];
        var p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(s, p, false);
            Marshal.Copy(p, arr, 0, size);
        }
        finally { Marshal.FreeHGlobal(p); }
        return arr;
    }

    private static T FromBytes<T>(byte[] arr) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(arr, 0, p, Math.Min(size, arr.Length));
            return Marshal.PtrToStructure<T>(p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }
}
