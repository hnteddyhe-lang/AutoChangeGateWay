using System.Net;
using System.Net.Sockets;

namespace AutoChangeGateWay.Core;

public enum RuntimeMode { Desktop, WindowsService }

public sealed record AppSettings
{
    public Guid AdapterId { get; init; }
    public string SourceAddress { get; init; } = "";
    public string PrimaryGateway { get; init; } = "192.168.88.191";
    public string BackupGateway { get; init; } = "192.168.88.1";
    public bool FollowGatewayDns { get; init; }
    public int IntervalSeconds { get; init; } = 3;
    public int TimeoutMilliseconds { get; init; } = 1000;
    public int FailureThreshold { get; init; } = 3;
    public int RecoveryThreshold { get; init; } = 5;
    public int CooldownSeconds { get; init; } = 30;
    public bool AutoFailback { get; init; }
    public bool DetectionOnly { get; init; } = true;
    public RuntimeMode RuntimeMode { get; init; }
    public bool StartWithWindows { get; init; }
    public bool StartMinimized { get; init; } = true;

    public void Validate()
    {
        if (!Enum.IsDefined(RuntimeMode)) throw new InvalidOperationException("运行方式无效。");
        if (AdapterId == Guid.Empty) throw new InvalidOperationException("请选择网卡。");
        var source = IPv4.Parse(SourceAddress);
        var primary = IPv4.Parse(PrimaryGateway);
        var backup = IPv4.Parse(BackupGateway);
        if (primary.Equals(backup)) throw new InvalidOperationException("主网关和备用网关不能相同。");
        if (source.Equals(primary) || source.Equals(backup)) throw new InvalidOperationException("网关不能填写电脑自身的 IP 地址。");
        if (IntervalSeconds is < 1 or > 300 || TimeoutMilliseconds is < 100 or > 10000 ||
            FailureThreshold is < 1 or > 30 || RecoveryThreshold is < 1 or > 30 || CooldownSeconds is < 0 or > 3600)
            throw new InvalidOperationException("检测参数超出允许范围。");
    }

    public string DnsForGateway(string gateway) => gateway == PrimaryGateway || gateway == BackupGateway ? gateway
        : throw new InvalidOperationException("DNS 目标必须对应已配置的主网关或备用网关。");
}

public static class IPv4
{
    public static IPAddress Parse(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9') ||
            (p.Length > 1 && p[0] == '0') || !byte.TryParse(p, out _)) ||
            !IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException($"请输入完整的 IPv4 地址：{value}");
        var bytes = address.GetAddressBytes();
        if (bytes[0] is 0 or 127 || bytes[0] >= 224 || address.Equals(IPAddress.Broadcast))
            throw new InvalidOperationException($"该地址不能作为本地地址或网关：{value}");
        return address;
    }

    public static bool IsOnLink(string local, int prefixLength, string gateway)
    {
        if (prefixLength is < 1 or > 32) return false;
        var a = Parse(local).GetAddressBytes();
        var b = Parse(gateway).GetAddressBytes();
        uint x = ((uint)a[0] << 24) | ((uint)a[1] << 16) | ((uint)a[2] << 8) | a[3];
        uint y = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        uint mask = uint.MaxValue << (32 - prefixLength);
        if ((x & mask) != (y & mask) || x == y) return false;
        return prefixLength >= 31 || ((y & ~mask) != 0 && (y & ~mask) != ~mask);
    }
}

public sealed record AddressInfo(string Address, int PrefixLength)
{
    public override string ToString() => $"{Address} / {PrefixLength}";
}

public sealed record AdapterInfo(Guid Id, string Name, string Description, uint Index, bool IsUp, AddressInfo[] Addresses)
{
    public override string ToString() => $"{Name}  ·  {(IsUp ? "已连接" : "未连接")}  ·  {Description}";

    public void ValidateGateways(AppSettings settings)
    {
        if (!IsUp) throw new InvalidOperationException("所选网卡未连接，已暂停修改路由。");
        var address = Addresses.SingleOrDefault(a => a.Address == settings.SourceAddress)
            ?? throw new InvalidOperationException("检测源 IP 已变化，请停止监控后重新选择。");
        if (!IPv4.IsOnLink(address.Address, address.PrefixLength, settings.PrimaryGateway) ||
            !IPv4.IsOnLink(address.Address, address.PrefixLength, settings.BackupGateway))
            throw new InvalidOperationException("两个网关必须是所选源 IP 所在子网中的可用主机地址。");
    }
}

public sealed record DefaultRoute(string NextHop, uint Metric, int Protocol = 3,
    uint ValidLifetime = uint.MaxValue, uint PreferredLifetime = uint.MaxValue)
{
    // 生存期会自然减少，不作为路由所有权判断条件。
    public bool SameIdentity(DefaultRoute other) => NextHop == other.NextHop && Metric == other.Metric && Protocol == other.Protocol;
}

public enum Health { Online, Offline, Unavailable }

// 空的服务器列表表示自动获取；配置文件中的网络配置文件级覆盖也必须一并备份。
public sealed record DnsConfiguration
{
    public string[] Servers { get; init; } = [];
    public string[] ProfileServers { get; init; } = [];
    public bool IsAutomatic => Servers.Length == 0 && ProfileServers.Length == 0;
    public bool Matches(DnsConfiguration other) => Servers.SequenceEqual(other.Servers, StringComparer.OrdinalIgnoreCase) &&
        ProfileServers.SequenceEqual(other.ProfileServers, StringComparer.OrdinalIgnoreCase);
    public static DnsConfiguration ForServer(string server, bool profileOverride = false) => new()
        { Servers = [server], ProfileServers = profileOverride ? [server] : [] };
    public override string ToString() => IsAutomatic ? "自动获取 DNS" : string.Join("，", ProfileServers.Length > 0 ? ProfileServers : Servers);
}
public sealed record ProbeResult(Health Health, string Detail);
public sealed record Decision(string? Target, string Message);

public interface IRoutePlatform
{
    AdapterInfo GetAdapter(Guid id);
    IReadOnlyList<DefaultRoute> GetDefaultRoutes(Guid adapterId);
    void AddRoute(Guid adapterId, DefaultRoute route);
    void DeleteRoute(Guid adapterId, DefaultRoute expected);
    DnsConfiguration GetDns(Guid adapterId);
    void SetDns(Guid adapterId, DnsConfiguration configuration);
}

public interface IJournal
{
    RouteSession? Load();
    void Save(RouteSession session);
    void Clear();
}

public sealed record RouteSession
{
    public Guid AdapterId { get; init; }
    public string SourceAddress { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public DateTimeOffset BootTime { get; init; }
    public DefaultRoute[] Original { get; init; } = [];
    public DefaultRoute[] KnownRoutes { get; init; } = [];
    public DnsConfiguration? OriginalDns { get; init; }
    public DnsConfiguration[] KnownDns { get; init; } = [];
}
