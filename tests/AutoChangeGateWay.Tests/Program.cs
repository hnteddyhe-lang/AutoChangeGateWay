using AutoChangeGateWay.Core;

var adapterId = Guid.Parse("c2fbf320-3816-4a9f-87d0-2fe7e2ad970c");
var config = new AppSettings { AdapterId = adapterId, SourceAddress = "192.168.88.100", DetectionOnly = false };
var online = new ProbeResult(Health.Online, "在线");
var offline = new ProbeResult(Health.Offline, "超时");
var unavailable = new ProbeResult(Health.Unavailable, "网卡断开");
var now = DateTimeOffset.UtcNow;
int passed = 0, failed = 0;

void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("通过  " + name); }
    catch (Exception e) { failed++; Console.WriteLine("失败  " + name + ": " + e.Message); }
}
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"预期 {expected ?? (object)"null"}，实际 {actual ?? (object)"null"}"); }
void Throws(Action action, string fragment)
{
    try { action(); } catch (Exception e) when (e.Message.Contains(fragment)) { return; }
    throw new Exception("没有抛出包含该信息的异常：" + fragment);
}
(FakePlatform platform, MemoryJournal journal, RouteManager manager) Setup(params DefaultRoute[] original)
{
    var platform = new FakePlatform(new(adapterId, "测试网卡", "模拟", 12, true, [new("192.168.88.100", 24)]), original);
    var journal = new MemoryJournal();
    return (platform, journal, new RouteManager(platform, journal, () => now.AddHours(-1)));
}
var original = new DefaultRoute(config.PrimaryGateway, 25);

Test("主网关连续三次失败才切备用", () =>
{
    var p = new FailoverPolicy(config);
    Equal<string?>(null, p.Evaluate(config.PrimaryGateway, offline, online, now).Target);
    Equal<string?>(null, p.Evaluate(config.PrimaryGateway, offline, online, now).Target);
    Equal(config.BackupGateway, p.Evaluate(config.PrimaryGateway, offline, online, now).Target);
});
Test("成功探测打断失败计数", () =>
{
    var p = new FailoverPolicy(config);
    p.Evaluate(config.PrimaryGateway, offline, online, now);
    p.Evaluate(config.PrimaryGateway, online, online, now);
    p.Evaluate(config.PrimaryGateway, offline, online, now);
    Equal(1, p.Failures);
});
Test("主备全离线保持现有路由", () =>
{
    var p = new FailoverPolicy(config);
    for (int i = 0; i < 10; i++) Equal<string?>(null, p.Evaluate(config.PrimaryGateway, offline, offline, now).Target);
});
Test("检测路径不可用不累计网关故障", () =>
{
    var p = new FailoverPolicy(config);
    p.Evaluate(config.PrimaryGateway, offline, online, now);
    Equal<string?>(null, p.Evaluate(config.PrimaryGateway, unavailable, online, now).Target);
    Equal(0, p.Failures);
    Equal<string?>(null, p.Evaluate(config.PrimaryGateway, offline, online, now).Target);
});
Test("备网关检测路径不明也禁止切换", () =>
{
    var p = new FailoverPolicy(config with { FailureThreshold = 1 });
    Equal<string?>(null, p.Evaluate(config.PrimaryGateway, offline, unavailable, now).Target);
});
Test("默认关闭自动回切", () =>
{
    var p = new FailoverPolicy(config);
    for (int i = 0; i < 10; i++) Equal<string?>(null, p.Evaluate(config.BackupGateway, online, online, now).Target);
});
Test("连续恢复五次才切回", () =>
{
    var p = new FailoverPolicy(config with { AutoFailback = true });
    for (int i = 0; i < 4; i++) Equal<string?>(null, p.Evaluate(config.BackupGateway, online, online, now).Target);
    Equal(config.PrimaryGateway, p.Evaluate(config.BackupGateway, online, online, now).Target);
});
Test("冷却时间防止恢复后立即抖动", () =>
{
    var p = new FailoverPolicy(config with { AutoFailback = true, RecoveryThreshold = 1 });
    p.Switched(now);
    Equal<string?>(null, p.Evaluate(config.BackupGateway, online, online, now.AddSeconds(29)).Target);
    Equal(config.PrimaryGateway, p.Evaluate(config.BackupGateway, online, online, now.AddSeconds(30)).Target);
});
Test("初始网关未配置时优先应用在线主网关", () => Equal(config.PrimaryGateway,
    new FailoverPolicy(config).Evaluate(null, online, online, now).Target));
Test("初始状态也必须累计主网关失联", () => Equal<string?>(null,
    new FailoverPolicy(config).Evaluate(null, offline, online, now).Target));
Test("拒绝简写、前导零与命令注入字符串", () =>
{
    foreach (var value in new[] { "192.168.1", "127.1", "192.168.088.1", "192.168.1.1;whoami", "224.0.0.1", "0.0.0.0" })
        Throws(() => IPv4.Parse(value), "地址");
});
Test("拒绝相同主备与电脑本机地址", () =>
{
    Throws(() => (config with { BackupGateway = config.PrimaryGateway }).Validate(), "不能相同");
    Throws(() => (config with { PrimaryGateway = config.SourceAddress }).Validate(), "自身");
});
Test("子网检查排除跨网段、网络及广播地址", () =>
{
    Equal(true, IPv4.IsOnLink("192.168.88.100", 24, "192.168.88.1"));
    foreach (var gateway in new[] { "192.168.89.1", "192.168.88.0", "192.168.88.255", "192.168.88.100" })
        Equal(false, IPv4.IsOnLink("192.168.88.100", 24, gateway));
    Equal(true, IPv4.IsOnLink("192.168.88.0", 31, "192.168.88.1"));
});
Test("仅检测模式绝不写入路由或恢复文件", () =>
{
    var (p, j, m) = Setup(original);
    Throws(() => m.Switch(config with { DetectionOnly = true }, config.BackupGateway), "仅检测");
    Equal(0, p.Operations.Count); Equal<RouteSession?>(null, j.Value);
});
Test("切换前写恢复记录；先增加后删除", () =>
{
    var (p, j, m) = Setup(original);
    j.BeforeSave = () => Equal(0, p.Operations.Count);
    m.Switch(config, config.BackupGateway);
    Equal("增加:" + config.BackupGateway, p.Operations[0]);
    Equal("删除:" + config.PrimaryGateway, p.Operations[1]);
    Equal(original, j.Value!.Original.Single()); Equal(config.BackupGateway, p.Routes.Single().NextHop);
});
Test("恢复原网关并删除程序新增路由", () =>
{
    var (p, j, m) = Setup(original);
    m.Switch(config, config.BackupGateway); m.Restore();
    Equal(true, RouteManager.SameSet(p.Routes, [original])); Equal<RouteSession?>(null, j.Value);
});
Test("添加失败不会删除原路由", () =>
{
    var (p, _, m) = Setup(original); p.FailNextAdd = true;
    Throws(() => m.Switch(config, config.BackupGateway), "已回滚");
    Equal(true, RouteManager.SameSet(p.Routes, [original]));
    Equal(false, p.Operations.Any(o => o.StartsWith("删除:")));
});
Test("删除旧路由失败时清理新路由并回滚", () =>
{
    var (p, j, m) = Setup(original); p.FailNextDelete = true;
    Throws(() => m.Switch(config, config.BackupGateway), "已回滚");
    Equal(true, RouteManager.SameSet(p.Routes, [original])); Equal(true, j.Value != null);
});
Test("恢复文件写入失败时零网络操作", () =>
{
    var (p, j, m) = Setup(original); j.FailSave = true;
    Throws(() => m.Switch(config, config.BackupGateway), "磁盘"); Equal(0, p.Operations.Count);
});
Test("外部路由改变后禁止覆盖或删除", () =>
{
    var (p, _, m) = Setup(original); m.Switch(config, config.BackupGateway);
    p.Routes.Add(new("192.168.88.200", 17)); int count = p.Operations.Count;
    Throws(m.Restore, "其他程序"); Equal(count, p.Operations.Count);
    Throws(() => m.Switch(config, config.PrimaryGateway), "其他程序"); Equal(count, p.Operations.Count);
});
Test("重启进程可从中断状态恢复", () =>
{
    var (p, j, m) = Setup(original); m.Switch(config, config.BackupGateway);
    p.Routes.Add(original); // 模拟先增加新网关后进程中断，或 DHCP 重新注入原路由。
    var restarted = new RouteManager(p, j, () => now.AddHours(-1)); restarted.Restore();
    Equal(true, RouteManager.SameSet(p.Routes, [original]));
});
Test("DHCP 重新注入已知原网关时能重新应用备用", () =>
{
    var (p, _, m) = Setup(original); m.Switch(config, config.BackupGateway); p.Routes.Add(original);
    m.Switch(config, config.BackupGateway); Equal(config.BackupGateway, p.Routes.Single().NextHop);
});
Test("不接管初始多默认网关", () =>
{
    var (p, _, m) = Setup(original, new(config.BackupGateway, 30));
    Throws(() => m.Switch(config, config.BackupGateway), "多条"); Equal(0, p.Operations.Count);
});
Test("不接管 VPN 的无下一跳默认路由", () =>
{
    var (p, _, m) = Setup(new DefaultRoute("0.0.0.0", 25));
    Throws(() => m.Switch(config, config.BackupGateway), "地址"); Equal(0, p.Operations.Count);
});
Test("没有原默认路由时，恢复会移除新增路由", () =>
{
    var (p, _, m) = Setup(); m.Switch(config, config.BackupGateway); m.Restore(); Equal(0, p.Routes.Count);
});
Test("源 IP 变化后禁止写入或恢复旧网络", () =>
{
    var (p, _, m) = Setup(original); m.Switch(config, config.BackupGateway);
    p.Adapter = p.Adapter with { Addresses = [new("192.168.99.100", 24)] };
    int count = p.Operations.Count; Throws(m.Restore, "IP 已变化");
    Throws(() => m.Switch(config, config.PrimaryGateway), "源 IP"); Equal(count, p.Operations.Count);
});
Test("网卡断开后暂停修改", () =>
{
    var (p, _, m) = Setup(original); p.Adapter = p.Adapter with { IsUp = false };
    Throws(() => m.Switch(config, config.BackupGateway), "未连接"); Equal(0, p.Operations.Count);
});
Test("恢复记录不能用于另一张网卡", () =>
{
    var (p, _, m) = Setup(original); m.Switch(config, config.BackupGateway);
    var other = Guid.NewGuid(); p.Adapter = p.Adapter with { Id = other };
    int count = p.Operations.Count;
    Throws(() => m.Switch(config with { AdapterId = other }, config.BackupGateway), "待恢复"); Equal(count, p.Operations.Count);
});
Test("当前已经使用目标网关时不产生写入", () =>
{
    var (p, j, m) = Setup(original); m.Switch(config, config.PrimaryGateway);
    Equal(0, p.Operations.Count); Equal<RouteSession?>(null, j.Value);
});
Test("旧开机记录清理不触碰当前路由", () =>
{
    var (p, j, m) = Setup(original); m.Switch(config, config.BackupGateway);
    j.Value = j.Value! with { BootTime = now.AddDays(-3) };
    var restarted = new RouteManager(p, j, () => now.AddHours(-1)); int count = p.Operations.Count;
    Equal(true, restarted.ClearPreviousBootSession()); Equal(count, p.Operations.Count); Equal<RouteSession?>(null, j.Value);
});
Test("写恢复记录后发生外部竞态，不删除外部路由", () =>
{
    var (p, j, m) = Setup(original);
    j.BeforeSave = () => p.Routes.Add(new("192.168.88.201", 10));
    Throws(() => m.Switch(config, config.BackupGateway), "回滚也未完成");
    Equal(0, p.Operations.Count); Equal(2, p.Routes.Count);
});
Test("恢复文件清理失败时仍保留恢复入口", () =>
{
    var (p, j, m) = Setup(original); m.Switch(config, config.BackupGateway); j.FailClear = true;
    Throws(m.Restore, "清理失败"); Equal(true, m.Session != null); Equal(true, RouteManager.SameSet(p.Routes, [original]));
    j.FailClear = false; m.Restore(); Equal<RouteSession?>(null, m.Session);
});

Test("DNS 开关默认关闭，切换网关不读取或修改 DNS", () =>
{
    var (p, j, m) = Setup(original); var dns = p.Dns;
    p.ThrowDnsRead = true; m.Switch(config, config.BackupGateway);
    Equal(0, p.DnsReads); Equal(0, p.DnsWrites); Equal(true, p.Dns.Matches(dns)); Equal<DnsConfiguration?>(null, j.Value!.OriginalDns);
});
Test("开启 DNS 跟随时，主备切换同步为对应网关", () =>
{
    var (p, j, m) = Setup(original); var dns = p.Dns;
    var follow = config with { FollowGatewayDns = true };
    m.Switch(follow, config.BackupGateway); Equal(config.BackupGateway, p.Dns.Servers.Single());
    m.Switch(follow, config.PrimaryGateway); Equal(config.PrimaryGateway, p.Dns.Servers.Single());
    Equal(true, j.Value!.OriginalDns!.Matches(dns));
});
Test("网关已是主网关时仍会同步错误的 DNS", () =>
{
    var (p, _, m) = Setup(original); var follow = config with { FollowGatewayDns = true };
    Equal(true, m.NeedsDnsSync(follow, config.PrimaryGateway)); m.Switch(follow, config.PrimaryGateway);
    Equal(config.PrimaryGateway, p.Dns.Servers.Single()); Equal(false, m.NeedsDnsSync(follow, config.PrimaryGateway));
    Equal(false, p.Operations.Any(o => o.StartsWith("增加:") || o.StartsWith("删除:")));
});
Test("开启 DNS 后仅检测依然零写入", () =>
{
    var (p, j, m) = Setup(original);
    Throws(() => m.Switch(config with { FollowGatewayDns = true, DetectionOnly = true }, config.BackupGateway), "仅检测");
    Equal(0, p.Operations.Count); Equal(0, p.DnsWrites); Equal<RouteSession?>(null, j.Value);
});
Test("修改 DNS 前持久化原静态列表及意图", () =>
{
    var (p, j, m) = Setup(original); var dns = p.Dns;
    j.BeforeSave = () => { Equal(0, p.DnsWrites); Equal(0, p.Operations.Count); };
    m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway);
    Equal(true, j.Value!.OriginalDns!.Matches(dns)); Equal(true, j.Value.KnownDns.Any(d => d.Servers.SequenceEqual(new[] { config.BackupGateway })));
});
Test("DNS 设置失败时网关和 DNS 一起回滚", () =>
{
    var (p, _, m) = Setup(original); var dns = p.Dns; p.FailNextDns = true;
    Throws(() => m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway), "已回滚");
    Equal(true, RouteManager.SameSet(p.Routes, [original])); Equal(true, p.Dns.Matches(dns));
});
Test("DNS 两级配置部分成功也能回滚", () =>
{
    var (p, _, m) = Setup(original);
    p.Dns = new() { Servers = ["9.9.9.9"], ProfileServers = ["8.8.4.4"] };
    var dns = p.Dns; p.FailNextDns = true; p.PartialDnsFailure = true;
    Throws(() => m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway), "已回滚");
    Equal(true, p.Dns.Matches(dns)); Equal(true, RouteManager.SameSet(p.Routes, [original]));
});
Test("DNS 写入后会核对，未生效则回滚网关", () =>
{
    var (p, _, m) = Setup(original); var dns = p.Dns; p.IgnoreNextDnsWrite = true;
    Throws(() => m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway), "已回滚");
    Equal(true, p.Dns.Matches(dns)); Equal(true, RouteManager.SameSet(p.Routes, [original]));
});
Test("恢复原配置会恢复原静态 DNS 列表和顺序", () =>
{
    var (p, _, m) = Setup(original); var dns = p.Dns;
    m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway); m.Restore();
    Equal(true, p.Dns.Matches(dns)); Equal(true, RouteManager.SameSet(p.Routes, [original]));
});
Test("原来自动获取 DNS，恢复时恢复自动获取", () =>
{
    var (p, _, m) = Setup(original); p.Dns = new();
    m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway); m.Restore(); Equal(true, p.Dns.IsAutomatic);
});
Test("DNS 被外部修改后暂停操作，不覆盖外部配置", () =>
{
    var (p, _, m) = Setup(original); var follow = config with { FollowGatewayDns = true };
    m.Switch(follow, config.BackupGateway); p.Dns = DnsConfiguration.ForServer("208.67.222.222"); int operations = p.Operations.Count;
    Throws(() => m.Switch(follow, config.PrimaryGateway), "其他程序"); Throws(m.Restore, "其他程序");
    Equal(operations, p.Operations.Count);
});
Test("关闭开关后后续切换不再修改 DNS，退出仍可恢复原值", () =>
{
    var (p, _, m) = Setup(original); var dns = p.Dns;
    m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway); int writes = p.DnsWrites;
    m.Switch(config, config.PrimaryGateway); Equal(writes, p.DnsWrites); Equal(config.BackupGateway, p.Dns.Servers.Single());
    m.Restore(); Equal(true, p.Dns.Matches(dns));
});
Test("已有正确网关和 DNS 时不重复落盘或写入", () =>
{
    var (p, j, m) = Setup(original); p.Dns = DnsConfiguration.ForServer(config.PrimaryGateway);
    m.Switch(config with { FollowGatewayDns = true }, config.PrimaryGateway);
    Equal(0, p.Operations.Count); Equal<RouteSession?>(null, j.Value);
});
Test("读取 DNS 失败时不修改路由", () =>
{
    var (p, _, m) = Setup(original); p.ThrowDnsRead = true;
    Throws(() => m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway), "读取 DNS"); Equal(0, p.Operations.Count);
});
Test("DNS 恢复记录跨重启仍保留，并只恢复 DNS", () =>
{
    var (p, j, m) = Setup(original); p.Dns = new(); m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway);
    j.Value = j.Value! with { BootTime = now.AddDays(-3) };
    p.Routes.Clear(); p.Routes.Add(new("192.168.88.200", 10)); p.Adapter = p.Adapter with { Addresses = [new("192.168.99.100", 24)] };
    int routeOperations = p.Operations.Count(o => !o.StartsWith("DNS:"));
    var restarted = new RouteManager(p, j, () => now.AddHours(-1));
    Equal(false, restarted.ClearPreviousBootSession()); restarted.Restore();
    Equal(true, p.Dns.IsAutomatic); Equal(routeOperations, p.Operations.Count(o => !o.StartsWith("DNS:")));
    Equal("192.168.88.200", p.Routes.Single().NextHop);
});
Test("跨重启静态 DNS 恢复遇到不同网络时保留记录", () =>
{
    var (p, j, m) = Setup(original); m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway);
    j.Value = j.Value! with { BootTime = now.AddDays(-3) }; p.Adapter = p.Adapter with { Addresses = [new("192.168.99.100", 24)] };
    var restarted = new RouteManager(p, j, () => now.AddHours(-1)); int operations = p.Operations.Count;
    Throws(restarted.Restore, "连接原网络"); Equal(operations, p.Operations.Count); Equal(true, restarted.Session != null);
});
Test("旧版路由记录升级后启用跟随会补记原 DNS", () =>
{
    var (p, j, m) = Setup(original); var dns = p.Dns;
    m.Switch(config, config.BackupGateway); Equal<DnsConfiguration?>(null, j.Value!.OriginalDns);
    m.Switch(config with { FollowGatewayDns = true }, config.BackupGateway); Equal(true, j.Value!.OriginalDns!.Matches(dns));
    m.Restore(); Equal(true, p.Dns.Matches(dns)); Equal(true, RouteManager.SameSet(p.Routes, [original]));
});

Console.WriteLine($"\n总计 {passed + failed} 项，通过 {passed} 项，失败 {failed} 项。测试使用模拟网卡，不修改系统网络。");
return failed == 0 ? 0 : 1;

sealed class FakePlatform(AdapterInfo adapter, DefaultRoute[] routes) : IRoutePlatform
{
    public AdapterInfo Adapter { get; set; } = adapter;
    public List<DefaultRoute> Routes { get; } = [.. routes];
    public List<string> Operations { get; } = [];
    public bool FailNextAdd { get; set; }
    public bool FailNextDelete { get; set; }
    public DnsConfiguration Dns { get; set; } = new() { Servers = ["9.9.9.9", "8.8.8.8"] };
    public int DnsReads { get; private set; }
    public int DnsWrites { get; private set; }
    public bool ThrowDnsRead { get; set; }
    public bool FailNextDns { get; set; }
    public bool PartialDnsFailure { get; set; }
    public bool IgnoreNextDnsWrite { get; set; }
    public AdapterInfo GetAdapter(Guid id) => id == Adapter.Id ? Adapter : throw new Exception("网卡不存在");
    public IReadOnlyList<DefaultRoute> GetDefaultRoutes(Guid id) { GetAdapter(id); return Routes.ToArray(); }
    public void AddRoute(Guid id, DefaultRoute route)
    {
        GetAdapter(id); if (FailNextAdd) { FailNextAdd = false; throw new Exception("模拟添加失败"); }
        if (Routes.Any(r => r.NextHop == route.NextHop)) throw new Exception("重复下一跳");
        Operations.Add("增加:" + route.NextHop); Routes.Add(route);
    }
    public void DeleteRoute(Guid id, DefaultRoute expected)
    {
        GetAdapter(id); if (FailNextDelete) { FailNextDelete = false; throw new Exception("模拟删除失败"); }
        Operations.Add("删除:" + expected.NextHop);
        Routes.Remove(Routes.Single(r => r.SameIdentity(expected)));
    }
    public DnsConfiguration GetDns(Guid id)
    {
        GetAdapter(id); DnsReads++;
        if (ThrowDnsRead) throw new Exception("模拟读取 DNS 失败");
        return Dns;
    }
    public void SetDns(Guid id, DnsConfiguration configuration)
    {
        GetAdapter(id); DnsWrites++;
        if (FailNextDns)
        {
            FailNextDns = false;
            if (PartialDnsFailure) Dns = Dns with { Servers = configuration.Servers };
            throw new Exception("模拟 DNS 写入失败");
        }
        if (IgnoreNextDnsWrite) { IgnoreNextDnsWrite = false; return; }
        Operations.Add("DNS:" + configuration); Dns = configuration;
    }
}
sealed class MemoryJournal : IJournal
{
    public RouteSession? Value { get; set; }
    public Action? BeforeSave { get; set; }
    public bool FailSave { get; set; }
    public bool FailClear { get; set; }
    public RouteSession? Load() => Value;
    public void Save(RouteSession session)
    {
        if (FailSave) throw new IOException("模拟磁盘写入失败");
        BeforeSave?.Invoke(); Value = session;
    }
    public void Clear() { if (FailClear) throw new IOException("模拟清理失败"); Value = null; }
}
