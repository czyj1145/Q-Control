using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QControl.Hal.Sys;

/// <summary>
/// 物理网卡枚举走**原生 `iphlpapi`**（`GetAdaptersInfo` + `GetIfEntry`），不走 WMI。
///
/// 为什么（2026-11-08 实测）：`Win32_NetworkAdapter` 一条查询就要 **280 ms** ——
/// 它是硬件档案整轮采集里唯一的瓶颈（其余七组并行跑完只要 60 ms）。而这 280 ms
/// 花在 WMI 为 15 个适配器逐个去向 NDIS 要驱动信息、速率、状态上，其中 13 个还是
/// WAN Miniport / VPN / TAP 这类虚拟适配器。原生 API 直接读内核里的适配器表，
/// 整组 **几毫秒** 就完了。
///
/// ⚠️ 两个 API 都是老接口但都还在用（`GetAdaptersInfo` 自 Vista 起文档上标了
///   "deprecated"，实际是 `GetAdaptersAddresses` 的简化版：只返回 IPv4 适配器，
///   结构体是**定长**的，比带 union + 变长链表的新版安全得多 —— 我们本来就只显示 IPv4）。
/// ⚠️ **失败必须回落 WMI**：读不出任何网卡 / 结构体明显不对（名字乱码、MAC 长度越界）
///   就整组交回 `HardwareProfile.NetWmi()`。原生是快路，不是唯一的路。
/// ⚠️ 速率用 `GetIfEntry` 补（`dwSpeed` 是 DWORD，>4.29 Gbps 会截断；本机 2.5G / Wi-Fi 6E
///   都在这之内）。取不到就不显示速率，不编。
/// </summary>
internal static class NativeNet
{
    /// <summary>一块物理网卡。字段与界面上的那几行一一对应。</summary>
    internal sealed record Nic(
        string Description,     // Intel(R) Wi-Fi 6E AX211 160MHz
        string Connection,      // 连接名：WLAN / 以太网（注册表里那一份，取不到为空）
        string Mac,             // AA-BB-CC-DD-EE-FF（取不到为空）
        string Ip,              // IPv4 地址（取不到为空）
        long SpeedBps,          // 0 = 读不到
        bool Up,
        uint IfType,            // 6 = 以太网，71 = 802.11
        bool Dhcp);

    private const int ErrorSuccess = 0;
    private const int ErrorBufferOverflow = 111;

    private const uint IfTypeEthernet = 6;
    private const uint IfTypeIeee80211 = 71;

    /// <summary>
    /// 读一遍物理网卡。返回空列表 = 这套路径不可用（调用方回落 WMI）。
    /// </summary>
    internal static List<Nic> Read()
    {
        var list = new List<Nic>();
        try
        {
            // 结构体是定长的（x64 下每块 ~600 字节），32 KB 够放几十块。
            // 万一不够会返回 ERROR_BUFFER_OVERFLOW 并把真实大小写回来，下面按它重试一次。
            var size = 32 * 1024;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetAdaptersInfo(buf, ref size);
                if (rc == ErrorBufferOverflow)
                {
                    Marshal.FreeHGlobal(buf);
                    // ⚠️ 先清空再重分配：万一下面 AllocHGlobal 抛异常（内存不足），
                    //    finally 里那句 `if (buf != IntPtr.Zero) FreeHGlobal(buf)` 就会
                    //    二次释放已经释放过的指针（2026-11-23 审查）。
                    buf = IntPtr.Zero;
                    buf = Marshal.AllocHGlobal(size);
                    rc = GetAdaptersInfo(buf, ref size);
                }
                if (rc != ErrorSuccess) return list;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var p = buf;
                while (p != IntPtr.Zero)
                {
                    var ai = Marshal.PtrToStructure<IpAdapterInfo>(p);
                    p = ai.Next;

                    var model = (ai.Description ?? "").Trim();
                    if (model.Length == 0) continue;

                    // 与 WMI 那条同一套"虚拟适配器"判据：这些名字都不是用户认知里的"网卡"
                    if (IsJunk(model)) continue;

                    var mac = Mac(ai.Address, ai.AddressLength);
                    // 同一块网卡可能有多条 IP（多地址 / IPv4+IPv6 别名）⇒ 按 MAC 去重
                    if (mac.Length > 0 && !seen.Add(mac)) continue;

                    // 「0.0.0.0」是"没拿到地址"，不是地址 —— 显示出来只是噪音
                    var ip = (ai.IpAddressList.IpAddress ?? "").Trim();
                    if (ip == "0.0.0.0") ip = "";

                    var ifRow = IfEntry(ai.Index);
                    list.Add(new Nic(
                        Description: model,
                        Connection: ConnectionName(ai.AdapterName),
                        Mac: mac,
                        Ip: ip,
                        SpeedBps: ifRow?.Speed ?? 0,
                        // ⚠️ 只看 `OperStatus` 会漏：Wi-Fi 网卡连上后 OperStatus 也可能是
                        //    DORMANT/LOWER_LAYER_DOWN（实测本机就是这样），而手里明明有地址。
                        //    有 IPv4 = 用户认知里的"已连接"，这条比状态位更接近事实。
                        Up: (ifRow?.Up ?? false) || ip.Length > 0,
                        IfType: ai.Type,
                        Dhcp: ai.DhcpEnabled != 0));
                }

                // 读到东西也要过一遍基本检查：结构体布局对不上时最典型的症状就是
                // 名字里出现替换符 / 控制字符（那说明偏移错了，后面所有字段都不可信）。
                if (list.Any(n => n.Description.Any(c => char.IsControl(c) || c == '\uFFFD')))
                {
                    list.Clear();
                    return list;
                }
            }
            finally
            {
                // buf 可能被上面的 overflow 分支换过一份，这里统一收最新那份
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            // 任何 P/Invoke 层面的意外（老系统缺导出、内存不够）⇒ 当作不可用
            list.Clear();
        }
        return list;
    }

    /// <summary>虚拟 / 隧道 / 蓝牙适配器一律不要（判据与 WMI 那条同源）。</summary>
    private static bool IsJunk(string name)
    {
        var low = name.ToLowerInvariant();
        string[] junk = ["wan miniport", "virtual", "vpn", "tap", "loopback", "microsoft", "bluetooth"];
        return junk.Any(b => low.Contains(b, StringComparison.Ordinal));
    }

    /// <summary>
    /// `FC:6D:77:69:DB:01` 形式 —— **分隔符必须是冒号**：WMI 的 `MACAddress` 就是冒号，
    /// 换成 `-` 会让"同一块网卡"在复制出来的档案里看起来变了个样（A/B 对账会误判）。
    /// </summary>
    private static string Mac(byte[]? addr, uint len)
    {
        if (addr is null) return "";
        var n = (int)Math.Min(len, (uint)addr.Length);
        if (n <= 0) return "";
        var sb = new System.Text.StringBuilder(n * 3);
        for (var i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(addr[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 连接名（「WLAN」/「以太网」）—— `IP_ADAPTER_INFO` 里没有，只在
    /// `Control\Network\{4D36E972-…}\{GUID}\Connection\Name`。一次注册表读，微秒级。
    /// 取不到就空着（界面那一段只是副标题的一个尾巴）。
    /// </summary>
    private static string ConnectionName(string? adapterGuid)
    {
        if (string.IsNullOrWhiteSpace(adapterGuid)) return "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\"
                + adapterGuid.Trim() + @"\Connection");
            return (key?.GetValue("Name") as string ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>速率 / 连接状态（`GetIfEntry` 按索引查一次）。</summary>
    private static (long Speed, bool Up)? IfEntry(uint index)
    {
        try
        {
            var row = new MibIfRow { dwIndex = index };
            if (GetIfEntry(ref row) != ErrorSuccess) return null;
            // ⚠️ 不是 `== 1`：MIB_IF_OPER_STATUS 里 1=CONNECTED、5=OPERATIONAL，
            //    Win10/11 的 Wi-Fi / 部分虚拟网卡报的就是 5 —— 只认 1 会把"已连接"判成断网。
            return (row.dwSpeed, row.dwOperStatus is 1 or 5);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>类型 71 是 802.11、6 是以太网；其它（USB 网卡 / 蜂窝模块）留给界面当"网卡"。</summary>
    internal static bool IsWireless(uint ifType) => ifType == IfTypeIeee80211;
    internal static bool IsWired(uint ifType) => ifType == IfTypeEthernet;

    // ================================================================ P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct IpAdapterInfo
    {
        public IntPtr Next;
        public uint ComboIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string AdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 132)] public string Description;
        public uint AddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Address;
        public uint Index;
        public uint Type;
        public uint DhcpEnabled;
        public IntPtr CurrentIpAddress;
        public IpAddrString IpAddressList;
        public IpAddrString GatewayList;
        public IpAddrString DhcpServer;
        public int HaveWins;
        public IpAddrString PrimaryWinsServer;
        public IpAddrString SecondaryWinsServer;
        public uint LeaseObtained;
        public uint LeaseExpires;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct IpAddrString
    {
        public IntPtr Next;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string IpAddress;
        // ⚠️ `IP_MASK_STRING` 是 `char[4*4]` = 16，不是 4（2026-11-12 修）。
        //    写成 4 会让整个 IP_ADDR_STRING 从 48 字节缩到 32，GatewayList / DhcpServer /
        //    HaveWins / LeaseObtained 全部错位。当前只读 IpAddressList（偏移恰好一致）所以
        //    一直没暴露；谁哪天读网关就会读到垃圾。
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string IpMask;
        public uint Context;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string wszName;
        public uint dwIndex;
        public uint dwType;
        public uint dwMtu;
        public uint dwSpeed;
        public uint dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] bPhysAddr;
        public uint dwAdminStatus;
        public uint dwOperStatus;
        public uint dwLastChange;
        public uint dwInOctets;
        public uint dwInUcastPkts;
        public uint dwInNUcastPkts;
        public uint dwInDiscards;
        public uint dwInErrors;
        public uint dwInUnknownProtos;
        public uint dwOutOctets;
        public uint dwOutUcastPkts;
        public uint dwOutNUcastPkts;
        public uint dwOutDiscards;
        public uint dwOutErrors;
        public uint dwOutQLen;
        public uint dwDescrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public byte[] bDescr;
    }

    [DllImport("iphlpapi.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int GetAdaptersInfo(IntPtr adapterInfo, ref int sizePointer);

    [DllImport("iphlpapi.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int GetIfEntry(ref MibIfRow row);
}
