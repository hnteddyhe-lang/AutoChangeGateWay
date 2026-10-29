namespace AutoChangeGateWay.Core;

// 所有网络写操作串行执行；先把可恢复的意图写入磁盘，再增加新路由，最后移除旧路由。
public sealed class RouteManager(IRoutePlatform platform, IJournal journal, Func<DateTimeOffset> bootTime)
{
    public RouteSession? Session { get; private set; } = journal.Load();

    public bool ClearPreviousBootSession()
    {
        if (Session == null || Math.Abs((Session.BootTime - bootTime()).TotalSeconds) < 120) return false;
        // DNS 是持久配置，跨重启也不能丢弃恢复记录；Restore 将仅恢复 DNS，不回写旧路由。
        if (Session.OriginalDns != null) return false;
        journal.Clear(); // 原生 API 仅操作运行时路由；上次开机的记录不能用于覆盖本次 DHCP 配置。
        Session = null;
        return true;
    }

    public void Switch(AppSettings settings, string gateway)
    {
        settings.Validate();
        if (settings.DetectionOnly) throw new InvalidOperationException("仅检测模式禁止修改路由。");
        if (gateway != settings.PrimaryGateway && gateway != settings.BackupGateway)
            throw new InvalidOperationException("目标必须为已配置的主网关或备用网关。");
        var adapter = platform.GetAdapter(settings.AdapterId);
        adapter.ValidateGateways(settings);
        if (Session != null && (Session.AdapterId != settings.AdapterId || Session.SourceAddress != settings.SourceAddress))
            throw new InvalidOperationException("存在待恢复的路由记录，请先恢复原配置，再更换网卡或源 IP。");

        var before = platform.GetDefaultRoutes(settings.AdapterId).ToArray();
        if (Session == null && before.Length > 1)
            throw new InvalidOperationException("所选网卡存在多条默认路由，首版不接管该配置，请先整理该网卡的默认路由。");
        if (Session == null)
        {
            var source = adapter.Addresses.Single(a => a.Address == settings.SourceAddress);
            if (before.Any(r => !IPv4.IsOnLink(source.Address, source.PrefixLength, r.NextHop)))
                throw new InvalidOperationException("原默认路由不是同网段的普通网关，首版不接管该配置。");
        }
        var beforeDns = settings.FollowGatewayDns ? platform.GetDns(settings.AdapterId) : null;
        var targetDns = beforeDns != null ? DnsConfiguration.ForServer(settings.DnsForGateway(gateway), beforeDns.ProfileServers.Length > 0) : null;
        if (Session != null)
        {
            EnsureKnown(before, Session);
            if (beforeDns != null) EnsureKnownDns(beforeDns, Session);
        }
        if (before.Length == 1 && before[0].NextHop == gateway && (targetDns == null || beforeDns!.Matches(targetDns))) return;
        var target = before.FirstOrDefault(r => r.NextHop == gateway)
            ?? new DefaultRoute(gateway, Session?.Original.FirstOrDefault()?.Metric ?? before.FirstOrDefault()?.Metric ?? 25);
        var session = Session ?? new RouteSession
        {
            AdapterId = settings.AdapterId, SourceAddress = settings.SourceAddress,
            CapturedAt = DateTimeOffset.UtcNow, BootTime = bootTime(), Original = before
        };
        session = session with
        {
            KnownRoutes = session.KnownRoutes.Concat(before).Append(target).Distinct().ToArray(),
            OriginalDns = session.OriginalDns ?? beforeDns,
            KnownDns = beforeDns != null ? session.KnownDns.Append(beforeDns).Append(targetDns!)
                .DistinctBy(d => string.Join(",", d.Servers) + "|" + string.Join(",", d.ProfileServers)).ToArray() : session.KnownDns
        };
        journal.Save(session);
        Session = session;
        try
        {
            Replace(settings.AdapterId, before, [target]);
            if (beforeDns != null) ReplaceDns(settings.AdapterId, beforeDns, targetDns!);
        }
        catch (Exception error)
        {
            try
            {
                var current = platform.GetDefaultRoutes(settings.AdapterId).ToArray();
                EnsureKnown(current, session);
                Replace(settings.AdapterId, current, before);
                if (beforeDns != null)
                {
                    var currentDns = platform.GetDns(settings.AdapterId);
                    EnsureKnownDns(currentDns, session);
                    ReplaceDns(settings.AdapterId, currentDns, beforeDns);
                }
            }
            catch (Exception rollback)
            {
                throw new InvalidOperationException($"切换失败：{error.Message}；回滚也未完成：{rollback.Message}。恢复记录已保留，请点击恢复原配置。", error);
            }
            throw new InvalidOperationException($"切换失败，已回滚到切换前的路由和 DNS：{error.Message}", error);
        }
    }

    public void Restore()
    {
        var session = Session ?? throw new InvalidOperationException("没有待恢复的路由记录。");
        if (Math.Abs((session.BootTime - bootTime()).TotalSeconds) >= 120)
        {
            RestoreDnsAfterReboot(session);
            journal.Clear(); Session = null; return;
        }
        var adapter = platform.GetAdapter(session.AdapterId);
        if (!adapter.IsUp) throw new InvalidOperationException("原网卡未连接，请连接后再恢复。");
        var source = adapter.Addresses.SingleOrDefault(a => a.Address == session.SourceAddress)
            ?? throw new InvalidOperationException("原网卡的 IP 已变化，暂不能恢复。请连接原网络后重试；重启会清除本程序的临时路由。");
        foreach (var route in session.Original)
            if (!IPv4.IsOnLink(source.Address, source.PrefixLength, route.NextHop))
                throw new InvalidOperationException("原网关不在当前网段，已停止恢复。");
        var elapsed = (uint)Math.Clamp((DateTimeOffset.UtcNow - session.CapturedAt).TotalSeconds, 0, uint.MaxValue);
        var original = session.Original.Select(r => r with
        {
            ValidLifetime = Remaining(r.ValidLifetime, elapsed),
            PreferredLifetime = Remaining(r.PreferredLifetime, elapsed)
        }).Where(r => r.ValidLifetime > 0).ToArray();
        var current = platform.GetDefaultRoutes(session.AdapterId).ToArray();
        EnsureKnown(current, session);
        var currentDns = session.OriginalDns != null ? platform.GetDns(session.AdapterId) : null;
        if (currentDns != null) EnsureKnownDns(currentDns, session);
        Replace(session.AdapterId, current, original);
        if (currentDns != null) ReplaceDns(session.AdapterId, currentDns, session.OriginalDns!);
        journal.Clear();
        Session = null;
    }

    public bool NeedsDnsSync(AppSettings settings, string gateway)
    {
        if (!settings.FollowGatewayDns) return false;
        var dns = platform.GetDns(settings.AdapterId);
        return !dns.Matches(DnsConfiguration.ForServer(settings.DnsForGateway(gateway), dns.ProfileServers.Length > 0));
    }

    private void RestoreDnsAfterReboot(RouteSession session)
    {
        var original = session.OriginalDns ?? throw new InvalidOperationException("恢复记录属于上次开机，不能应用到当前网络。");
        var adapter = platform.GetAdapter(session.AdapterId);
        if (!original.IsAutomatic && !adapter.Addresses.Any(a => a.Address == session.SourceAddress))
            throw new InvalidOperationException("上次运行的静态 DNS 尚未恢复，请连接原网络后重试；恢复记录已保留。");
        var current = platform.GetDns(session.AdapterId);
        EnsureKnownDns(current, session);
        ReplaceDns(session.AdapterId, current, original);
    }

    private static void EnsureKnownDns(DnsConfiguration current, RouteSession session)
    {
        if (session.OriginalDns == null) return; // 兼容旧版仅保存路由的恢复记录。
        var known = session.KnownDns.Append(session.OriginalDns).ToArray();
        // API 可能在两级 DNS 配置之间部分成功，允许这些已知组合用于回滚。
        if (!known.Any(d => d.Servers.SequenceEqual(current.Servers, StringComparer.OrdinalIgnoreCase)) ||
            !known.Any(d => d.ProfileServers.SequenceEqual(current.ProfileServers, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("检测到其他程序更改了该网卡的 DNS，已暂停写入，请检查 DNS 配置。");
    }

    private void ReplaceDns(Guid adapterId, DnsConfiguration before, DnsConfiguration after)
    {
        if (!platform.GetDns(adapterId).Matches(before)) throw new InvalidOperationException("DNS 在操作前发生变化，已取消写入。");
        if (before.Matches(after)) return;
        platform.SetDns(adapterId, after);
        if (!platform.GetDns(adapterId).Matches(after)) throw new InvalidOperationException("DNS 写入后校验未通过。");
    }

    private static uint Remaining(uint lifetime, uint elapsed) => lifetime == uint.MaxValue ? lifetime : lifetime > elapsed ? lifetime - elapsed : 0;

    private static void EnsureKnown(IEnumerable<DefaultRoute> current, RouteSession session)
    {
        if (current.Any(r => !session.KnownRoutes.Concat(session.Original).Any(r.SameIdentity)))
            throw new InvalidOperationException("检测到其他程序或 DHCP 更改了该网卡的默认路由，已暂停写入；请检查网络配置。");
    }

    private void Replace(Guid adapter, DefaultRoute[] before, DefaultRoute[] after)
    {
        if (!SameSet(platform.GetDefaultRoutes(adapter), before))
            throw new InvalidOperationException("路由在操作前发生变化，已取消本次写入。");
        foreach (var route in after.Where(a => !before.Any(a.SameIdentity)))
        {
            // 同一下一跳属性改变时不猜测外部配置的意图。
            if (before.Any(r => r.NextHop == route.NextHop))
                throw new InvalidOperationException("已有相同网关但路由属性不同，请检查配置。");
            platform.AddRoute(adapter, route);
        }
        foreach (var route in before.Where(b => !after.Any(b.SameIdentity))) platform.DeleteRoute(adapter, route);
        if (!SameSet(platform.GetDefaultRoutes(adapter), after))
            throw new InvalidOperationException("写入后路由校验未通过。");
    }

    public static bool SameSet(IReadOnlyList<DefaultRoute> a, IReadOnlyList<DefaultRoute> b) =>
        a.Count == b.Count && a.All(r => b.Any(r.SameIdentity));
}
