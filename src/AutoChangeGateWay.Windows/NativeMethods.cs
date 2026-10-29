using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace AutoChangeGateWay.Windows;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Address;
        public static SockaddrInet IPv4(string address) => new()
        {
            Family = 2, Address = BitConverter.ToUInt32(IPAddress.Parse(address).GetAddressBytes())
        };
        public readonly string ToAddress() => new IPAddress(BitConverter.GetBytes(Address)).ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AddressPrefix { public SockaddrInet Prefix; public byte PrefixLength; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RouteRow
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public AddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public int Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public int Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RouteTable { public uint Count; public RouteRow First; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsInterfaceSettings
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain;
        public IntPtr NameServer;
        public IntPtr SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLlmnr;
        public uint QueryAdapterName;
        public IntPtr ProfileNameServer;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetInterfaceDnsSettings(Guid id, ref DnsInterfaceSettings settings);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint SetInterfaceDnsSettings(Guid id, in DnsInterfaceSettings settings);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeInterfaceDnsSettings(ref DnsInterfaceSettings settings);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeMibTable(IntPtr table);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void InitializeIpForwardEntry(out RouteRow row);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint CreateIpForwardEntry2(in RouteRow row);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint DeleteIpForwardEntry2(in RouteRow row);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint ConvertInterfaceGuidToLuid(in Guid guid, out ulong luid);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetBestInterfaceEx(in SockaddrInet destination, out uint index);
    [DllImport("iphlpapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr IcmpCreateFile();
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IcmpCloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern uint IcmpSendEcho2Ex(IntPtr handle, IntPtr ev, IntPtr apc, IntPtr context,
        uint source, uint destination, byte[] data, ushort size, IntPtr options, IntPtr reply, uint replySize, uint timeout);

    internal static void Check(uint code, string action)
    {
        if (code != 0) throw new Win32Exception((int)code, $"{action}失败（{code}）：{new Win32Exception((int)code).Message}");
    }

    internal static void VerifyLayout()
    {
        if (Marshal.SizeOf<SockaddrInet>() != 28 || Marshal.SizeOf<AddressPrefix>() != 32 ||
            Marshal.SizeOf<RouteRow>() != 104 || Marshal.OffsetOf<RouteTable>(nameof(RouteTable.First)).ToInt32() != 8 ||
            Marshal.SizeOf<DnsInterfaceSettings>() != 64)
            throw new PlatformNotSupportedException("当前平台的 Windows 路由结构布局不匹配。");
    }
}
