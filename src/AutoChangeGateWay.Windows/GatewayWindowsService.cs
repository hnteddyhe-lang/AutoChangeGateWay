using System.ServiceProcess;
using AutoChangeGateWay.Core;

namespace AutoChangeGateWay.Windows;

public sealed record ServiceRuntimeState(DateTimeOffset UpdatedAt, string Heading, string Message,
    string? CurrentGateway = null, ProbeResult? Primary = null, ProbeResult? Backup = null);

public sealed class GatewayWindowsService : ServiceBase
{
    private readonly string? smokeRoot;
    private CancellationTokenSource? cancellation;
    private Task? worker;

    public GatewayWindowsService(string name = ServiceManagement.Name, string? smokeRoot = null)
    {
        ServiceName = name; CanStop = true; CanShutdown = true; AutoLog = false;
        this.smokeRoot = smokeRoot;
    }

    protected override void OnStart(string[] args)
    {
        cancellation = new CancellationTokenSource();
        worker = Task.Run(async () =>
        {
            try
            {
                if (smokeRoot != null) await SmokeAsync(cancellation.Token);
                else await MonitorAsync(cancellation.Token);
            }
            catch (Exception error)
            {
                if (smokeRoot == null)
                {
                    try
                    {
                        var storage = new AppStorage(ServiceManagement.DataRoot);
                        storage.WriteState(new(DateTimeOffset.Now, "服务异常停止", error.Message));
                        storage.Log("服务异常：" + error);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
        });
        _ = worker.ContinueWith(_ => { ExitCode = 1064; Stop(); }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task SmokeAsync(CancellationToken token)
    {
        Directory.CreateDirectory(smokeRoot!);
        File.WriteAllText(Path.Combine(smokeRoot!, "started.txt"), "服务已启动；测试不调用任何网卡或路由 API。");
        try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
        File.WriteAllText(Path.Combine(smokeRoot!, "stopped.txt"), "服务已停止。");
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        using var ownership = new NetworkOwnership();
        var storage = new AppStorage(ServiceManagement.DataRoot);
        var config = storage.LoadSettings(); config.Validate();
        if (config.RuntimeMode != RuntimeMode.WindowsService) throw new InvalidOperationException("服务配置未选择 Windows 服务运行方式。");
        var network = new WindowsNetwork();
        var manager = new RouteManager(network, storage, AppStorage.BootTime);
        manager.ClearPreviousBootSession();
        var policy = new FailoverPolicy(config);
        string? desired = null, lastMessage = null;
        var lastLog = DateTimeOffset.MinValue;
        bool recovered = manager.Session == null;
        void Report(string heading, string message, string? current = null, ProbeResult? primary = null, ProbeResult? backup = null)
        {
            // 状态/普通日志写入失败不等于网关故障；路由修改所需的恢复记录仍必须成功落盘。
            try { storage.WriteState(new(DateTimeOffset.Now, heading, message, current, primary, backup)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (message != lastMessage || (DateTimeOffset.Now - lastLog).TotalMinutes >= 1)
            {
                try { storage.Log(message); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                lastMessage = message; lastLog = DateTimeOffset.Now;
            }
        }
        try
        {
            Report("服务正在监控", config.DetectionOnly ? "仅检测模式，不修改路由。" : "等待网卡和网关检测。");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!recovered)
                    {
                        manager.Restore(); recovered = true;
                        Report("恢复完成", "已恢复上次服务运行遗留的路由，继续监控。");
                    }
                    var adapter = network.GetAdapter(config.AdapterId); adapter.ValidateGateways(config);
                    var current = network.GetDefaultRoutes(config.AdapterId);
                    var primary = WindowsNetwork.Probe(adapter, config.SourceAddress, config.PrimaryGateway, config.TimeoutMilliseconds);
                    var backup = WindowsNetwork.Probe(adapter, config.SourceAddress, config.BackupGateway, config.TimeoutMilliseconds);
                    token.ThrowIfCancellationRequested();
                    var gateway = current.Count == 1 ? current[0].NextHop : null;
                    var decision = policy.Evaluate(gateway, primary, backup, DateTimeOffset.UtcNow);
                    if (current.Count > 1 && manager.Session == null) decision = new(null, "所选网卡有多条默认路由，暂停切换。");
                    if (!config.DetectionOnly && desired != null && current.Count > 1 &&
                        (desired == config.PrimaryGateway ? primary : backup).Health == Health.Online &&
                        primary.Health != Health.Unavailable && backup.Health != Health.Unavailable)
                        decision = new(desired, "重新应用被 DHCP 注入前正在使用的网关。");
                    if (decision.Target == null && gateway != null && (gateway == config.PrimaryGateway || gateway == config.BackupGateway) &&
                        (gateway == config.PrimaryGateway ? primary : backup).Health == Health.Online &&
                        primary.Health != Health.Unavailable && backup.Health != Health.Unavailable && manager.NeedsDnsSync(config, gateway))
                        decision = new(gateway, "同步当前网关对应的 DNS。");
                    if (!config.DetectionOnly && decision.Target != null)
                    {
                        bool routeChanged = gateway != decision.Target || current.Count != 1;
                        manager.Switch(config, decision.Target); desired = decision.Target; gateway = desired;
                        if (routeChanged) policy.Switched(DateTimeOffset.UtcNow);
                    }
                    Report(config.DetectionOnly ? "服务监控中 · 仅检测" : "服务监控中 · 自动切换",
                        config.DetectionOnly && decision.Target != null ? "仅检测，建议切换至 " + decision.Target : decision.Message,
                        gateway, primary, backup);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception error)
                {
                    policy.ResetCounters();
                    Report("服务等待重试", error.Message);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(config.IntervalSeconds), token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try
            {
                if (manager.Session != null) manager.Restore();
                Report("服务已停止", "监控已停止，本次服务管理的路由已恢复。");
            }
            catch (Exception error)
            {
                Report("服务停止，恢复未完成", error.Message + "；恢复记录已保留。");
                throw;
            }
        }
    }

    private void StopWorker()
    {
        cancellation?.Cancel();
        try
        {
            if (worker != null && !worker.Wait(TimeSpan.FromSeconds(25)))
                throw new System.TimeoutException("服务停止等待超时，恢复记录将保留。");
        }
        catch (AggregateException) { ExitCode = 1064; }
        finally { cancellation?.Dispose(); cancellation = null; }
    }
    protected override void OnStop() { RequestAdditionalTime(30000); StopWorker(); }
    protected override void OnShutdown() => StopWorker();
}
