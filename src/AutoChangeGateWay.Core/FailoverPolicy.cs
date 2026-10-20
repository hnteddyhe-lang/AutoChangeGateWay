namespace AutoChangeGateWay.Core;

public sealed class FailoverPolicy(AppSettings settings)
{
    public int Failures { get; private set; }
    public int Recoveries { get; private set; }
    private DateTimeOffset? lastSwitch;

    public void ResetCounters() { Failures = 0; Recoveries = 0; }
    public void Switched(DateTimeOffset now) { lastSwitch = now; ResetCounters(); }

    public Decision Evaluate(string? current, ProbeResult primary, ProbeResult backup, DateTimeOffset now)
    {
        if (primary.Health == Health.Unavailable || backup.Health == Health.Unavailable)
        {
            ResetCounters();
            return new(null, "检测路径不可用，暂停切换：" +
                (primary.Health == Health.Unavailable ? primary.Detail : backup.Detail));
        }
        Failures = primary.Health == Health.Offline ? Math.Min(Failures + 1, settings.FailureThreshold) : 0;
        Recoveries = primary.Health == Health.Online ? Math.Min(Recoveries + 1, settings.RecoveryThreshold) : 0;
        if (lastSwitch.HasValue && (now - lastSwitch.Value).TotalSeconds < settings.CooldownSeconds)
            return new(null, "切换冷却中，继续检测。");

        if (current == settings.PrimaryGateway)
        {
            if (Failures >= settings.FailureThreshold && backup.Health == Health.Online)
                return new(settings.BackupGateway, "主网关连续失联，备用网关在线，切换到备用。");
            if (primary.Health == Health.Online) return new(null, "主网关在线，当前使用主网关。");
            return new(null, backup.Health == Health.Offline ? "主备均无响应，保留当前路由。" : $"主网关无响应（{Failures}/{settings.FailureThreshold}），继续确认。");
        }
        if (current == settings.BackupGateway)
        {
            if (settings.AutoFailback && Recoveries >= settings.RecoveryThreshold)
                return new(settings.PrimaryGateway, "主网关已连续恢复，切回主网关。");
            return new(null, primary.Health == Health.Online
                ? (settings.AutoFailback ? $"主网关恢复确认中（{Recoveries}/{settings.RecoveryThreshold}）。" : "主网关在线；自动切回已关闭，保持备用。")
                : (backup.Health == Health.Online ? "当前使用备用网关，主网关仍无响应。" : "主备均无响应，保留当前路由。"));
        }
        if (primary.Health == Health.Online) return new(settings.PrimaryGateway, "主网关在线，应用主网关。");
        if (Failures >= settings.FailureThreshold && backup.Health == Health.Online)
            return new(settings.BackupGateway, "主网关连续失联，应用备用网关。");
        return new(null, "当前网关不在主备配置中，等待检测确认。");
    }
}
