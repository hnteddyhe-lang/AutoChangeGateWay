using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AutoChangeGateWay.Core;
using Microsoft.Win32;

namespace AutoChangeGateWay.Windows;

public sealed class WindowsNetwork : IRoutePlatform
{
    public WindowsNetwork() => NativeMethods.VerifyLayout();

    public AdapterInfo[] GetAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.Supports(NetworkInterfaceComponent.IPv4))
        .Select(n =>
        {
            var properties = n.GetIPProperties();
            return new AdapterInfo(Guid.Parse(n.Id), n.Name, n.Description,
                (uint)properties.GetIPv4Properties().Index, n.OperationalStatus == OperationalStatus.Up,
                properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    a.DuplicateAddressDetectionState is not DuplicateAddressDetectionState.Duplicate and not DuplicateAddressDetectionState.Tentative)
                    .Select(a => new AddressInfo(a.Address.ToString(), a.PrefixLength)).ToArray());
        }).OrderByDescending(a => a.IsUp).ThenBy(a => a.Name).ToArray();

    public AdapterInfo GetAdapter(Guid id) => GetAdapters().SingleOrDefault(a => a.Id == id)
        ?? throw new InvalidOperationException("找不到已选择的网卡，请检查连接后刷新。");

    private static NativeMethods.RouteRow[] ReadRows()
    {
        NativeMethods.Check(NativeMethods.GetIpForwardTable2(2, out var table), "读取 IPv4 路由");
        try
        {
            var count = Marshal.ReadInt32(table);
            if (count is < 0 or > 100000) throw new InvalidOperationException("路由表大小无效。");
            int offset = Marshal.OffsetOf<NativeMethods.RouteTable>(nameof(NativeMethods.RouteTable.First)).ToInt32();
            int size = Marshal.SizeOf<NativeMethods.RouteRow>();
            return Enumerable.Range(0, count)
                .Select(i => Marshal.PtrToStructure<NativeMethods.RouteRow>(IntPtr.Add(table, offset + i * size))).ToArray();
        }
        finally { NativeMethods.FreeMibTable(table); }
    }

    private static bool IsDefault(NativeMethods.RouteRow row, ulong luid) => row.InterfaceLuid == luid &&
        row.DestinationPrefix.Prefix.Family == 2 && row.DestinationPrefix.PrefixLength == 0 &&
        row.DestinationPrefix.Prefix.Address == 0 && row.NextHop.Family == 2;

    private static DefaultRoute Describe(NativeMethods.RouteRow row) =>
        new(row.NextHop.ToAddress(), row.Metric, row.Protocol, row.ValidLifetime, row.PreferredLifetime);

    private static ulong Luid(Guid id)
    {
        NativeMethods.Check(NativeMethods.ConvertInterfaceGuidToLuid(in id, out var luid), "定位网卡");
        return luid;
    }

    public IReadOnlyList<DefaultRoute> GetDefaultRoutes(Guid adapterId)
    {
        var luid = Luid(adapterId);
        return ReadRows().Where(r => IsDefault(r, luid)).Select(Describe).OrderBy(r => r.Metric).ThenBy(r => r.NextHop).ToArray();
    }

    public void AddRoute(Guid adapterId, DefaultRoute route)
    {
        IPv4.Parse(route.NextHop);
        var adapter = GetAdapter(adapterId);
        if (!adapter.IsUp) throw new InvalidOperationException("网卡已断开，已取消路由写入。");
        if (!adapter.Addresses.Any(a => IPv4.IsOnLink(a.Address, a.PrefixLength, route.NextHop)))
            throw new InvalidOperationException("目标网关已不在所选网卡的网段。");
        NativeMethods.InitializeIpForwardEntry(out var row);
        row.InterfaceLuid = Luid(adapterId);
        row.InterfaceIndex = adapter.Index;
        row.DestinationPrefix = new() { Prefix = NativeMethods.SockaddrInet.IPv4("0.0.0.0"), PrefixLength = 0 };
        row.NextHop = NativeMethods.SockaddrInet.IPv4(route.NextHop);
        row.SitePrefixLength = 0;
        row.Metric = route.Metric;
        row.Protocol = route.Protocol;
        row.ValidLifetime = route.ValidLifetime;
        row.PreferredLifetime = route.PreferredLifetime;
        NativeMethods.Check(NativeMethods.CreateIpForwardEntry2(in row), "添加目标网卡默认路由");
    }

    public void DeleteRoute(Guid adapterId, DefaultRoute expected)
    {
        var luid = Luid(adapterId);
        var matches = ReadRows().Where(r => IsDefault(r, luid) && Describe(r).SameIdentity(expected)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("待删除路由已发生变化，已取消删除。");
        NativeMethods.Check(NativeMethods.DeleteIpForwardEntry2(in matches[0]), "移除目标网卡旧默认路由");
    }

    public DnsConfiguration GetDns(Guid adapterId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("同步 DNS 需要 Windows 10 2004（19041）或更高版本。");
        GetAdapter(adapterId);
        var settings = new NativeMethods.DnsInterfaceSettings { Version = 1 };
        NativeMethods.Check(NativeMethods.GetInterfaceDnsSettings(adapterId, ref settings), "读取所选网卡 DNS");
        try
        {
            // 注册表保存显式配置；不能把 DHCP 当前下发的 DNS 误当作原来的静态配置。
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapterId.ToString("B"));
            if (key == null) throw new InvalidOperationException("无法读取所选网卡的 DNS 配置来源，已禁止修改。");
            string value = key.GetValue("NameServer") as string ?? "";
            return new DnsConfiguration { Servers = ParseDns(value), ProfileServers = ParseDns(Marshal.PtrToStringUni(settings.ProfileNameServer) ?? "") };
        }
        finally { NativeMethods.FreeInterfaceDnsSettings(ref settings); }
    }

    private static string[] ParseDns(string value) => value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(s =>
        {
            if (!System.Net.IPAddress.TryParse(s, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("该网卡的 IPv4 DNS 配置含无法识别的地址，已禁止修改。");
            return ip.ToString();
        }).ToArray();

    public void SetDns(Guid adapterId, DnsConfiguration configuration)
    {
        var current = GetDns(adapterId);
        foreach (var server in configuration.Servers.Concat(configuration.ProfileServers))
            if (!System.Net.IPAddress.TryParse(server, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("DNS 恢复记录包含无效的 IPv4 地址。");
        var settings = new NativeMethods.DnsInterfaceSettings { Version = 1, Flags = 2 };
        settings.NameServer = Marshal.StringToHGlobalUni(string.Join(",", configuration.Servers));
        try
        {
            if (!current.ProfileServers.SequenceEqual(configuration.ProfileServers, StringComparer.OrdinalIgnoreCase))
            {
                settings.Flags |= 0x200;
                settings.ProfileNameServer = Marshal.StringToHGlobalUni(string.Join(",", configuration.ProfileServers));
            }
            NativeMethods.Check(NativeMethods.SetInterfaceDnsSettings(adapterId, in settings), "设置所选网卡 IPv4 DNS");
        }
        finally
        {
            Marshal.FreeHGlobal(settings.NameServer);
            if (settings.ProfileNameServer != IntPtr.Zero) Marshal.FreeHGlobal(settings.ProfileNameServer);
        }
    }

    public static ProbeResult Probe(AdapterInfo adapter, string source, string gateway, int timeout)
    {
        if (!adapter.IsUp || !adapter.Addresses.Any(a => a.Address == source))
            return new(Health.Unavailable, "网卡断开或源 IP 已变化");
        var destination = NativeMethods.SockaddrInet.IPv4(gateway);
        uint code = NativeMethods.GetBestInterfaceEx(in destination, out var index);
        if (code != 0 || index != adapter.Index)
            return new(Health.Unavailable, "到网关的实际路由不经过所选网卡，请检查 VPN 或重叠网段");
        var handle = NativeMethods.IcmpCreateFile();
        if (handle == new IntPtr(-1)) return new(Health.Unavailable, "无法创建 ICMP 检测句柄");
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            uint count = NativeMethods.IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.SockaddrInet.IPv4(source).Address, destination.Address, [0x47, 0x57, 0x41, 0x59],
                4, IntPtr.Zero, buffer, 512, (uint)timeout);
            int error = count > 0 ? Marshal.ReadInt32(buffer, 4) : Marshal.GetLastWin32Error();
            if (count > 0 && error == 0 && (uint)Marshal.ReadInt32(buffer) == destination.Address)
                return new(Health.Online, $"在线 · {Marshal.ReadInt32(buffer, 8)} ms");
            // 只有明确的超时/不可达算离线；未知 API、资源及参数错误必须暂停切换。
            return error is 11002 or 11003 or 11004 or 11005 or 11010
                ? new(Health.Offline, $"无响应 · ICMP {error}")
                : new(Health.Unavailable, $"ICMP 检测错误 {error}，未确认网关离线");
        }
        finally { Marshal.FreeHGlobal(buffer); NativeMethods.IcmpCloseHandle(handle); }
    }
}
