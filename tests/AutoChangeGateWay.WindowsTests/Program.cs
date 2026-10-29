using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using AutoChangeGateWay.Core;
using AutoChangeGateWay.Windows;

string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "test-data"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0;
void Test(string name, Action action)
{
    try { action(); passed++; Console.WriteLine("通过  " + name); }
    catch (Exception error) { failed++; Console.WriteLine("失败  " + name + ": " + error); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var settings = new AppSettings { AdapterId = Guid.NewGuid(), SourceAddress = "192.168.88.100" };

Test("旧版配置缺少运行选项时保持手动桌面模式", () =>
{
    var legacy = JsonSerializer.Deserialize<AppSettings>("{\"PrimaryGateway\":\"192.168.88.191\"}")!;
    Assert(legacy.RuntimeMode == RuntimeMode.Desktop && !legacy.StartWithWindows && legacy.StartMinimized && !legacy.FollowGatewayDns, "旧配置默认行为改变。");
});
Test("服务与自启动设置可以完整保存及重新加载", () =>
{
    var store = new AppStorage(Path.Combine(root, "config-" + Guid.NewGuid().ToString("N")));
    var value = settings with { RuntimeMode = RuntimeMode.WindowsService, StartWithWindows = true, StartMinimized = false, FollowGatewayDns = true };
    store.SaveSettings(value); Assert(store.LoadSettings() == value, "运行配置未完整往返保存。");
});
Test("运行方式无效时拒绝配置", () =>
{
    try { (settings with { RuntimeMode = (RuntimeMode)100 }).Validate(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("接受了未知运行方式。");
});
Test("服务状态文件可供另一个进程读取", () =>
{
    var store = new AppStorage(Path.Combine(root, "state-" + Guid.NewGuid().ToString("N")));
    var state = new ServiceRuntimeState(DateTimeOffset.UtcNow, "服务监控中", "仅检测", settings.PrimaryGateway,
        new(Health.Online, "在线"), new(Health.Offline, "超时"));
    store.WriteState(state); Assert(AppStorage.ReadState(store.Root) == state, "服务状态往返不一致。");
});
Test("界面连续读取状态时后台可原子更新状态文件", () =>
{
    var store = new AppStorage(Path.Combine(root, "concurrent-state-" + Guid.NewGuid().ToString("N")));
    var state = new ServiceRuntimeState(DateTimeOffset.UtcNow, "监控中", "0"); store.WriteState(state);
    var writer = Task.Run(() => { for (int i = 1; i <= 64; i++) store.WriteState(state with { Message = i.ToString() }); });
    while (!writer.IsCompleted)
    {
        Assert(AppStorage.ReadState(store.Root)?.Heading == "监控中", "并发读取发生丢失或损坏。"); Thread.Sleep(1);
    }
    writer.GetAwaiter().GetResult(); Assert(AppStorage.ReadState(store.Root)?.Message == "64", "未读取到最新状态。");
});
Test("恢复记录跨实例加载时保留原配置", () =>
{
    var store = new AppStorage(Path.Combine(root, "journal-" + Guid.NewGuid().ToString("N")));
    var session = new RouteSession { AdapterId = settings.AdapterId, SourceAddress = settings.SourceAddress,
        Original = [new(settings.PrimaryGateway, 25)], KnownRoutes = [new(settings.BackupGateway, 25)] };
    store.Save(session); var loaded = new AppStorage(store.Root).Load()!;
    Assert(loaded.AdapterId == session.AdapterId && loaded.Original.Single().SameIdentity(session.Original.Single()), "恢复记录不完整。");
});
Test("桌面与服务使用同名互斥锁时不能同时取得所有权", () =>
{
    string name = @"Local\AutoChangeGateWay.Validation." + Guid.NewGuid().ToString("N");
    using var first = new NetworkOwnership(name);
    try { using var second = new NetworkOwnership(name); }
    catch (InvalidOperationException) { return; }
    throw new Exception("两个监控实例同时取得了所有权。");
});
Test("异步线程释放所有权后可以重新取得", () =>
{
    string name = @"Local\AutoChangeGateWay.Validation." + Guid.NewGuid().ToString("N");
    var first = new NetworkOwnership(name); Task.Run(first.Dispose).GetAwaiter().GetResult();
    using var second = new NetworkOwnership(name);
});
Test("删除不存在的自启动任务可重复执行", () =>
{
    string name = "AutoChangeGateWay.Validation.Missing." + Guid.NewGuid().ToString("N");
    StartupTask.Remove(name); StartupTask.Remove(name);
});
Test("只读查询服务状态可以识别未安装或已有服务", () =>
{
    var info = ServiceManagement.Query(); Assert(!string.IsNullOrWhiteSpace(info.State), "服务状态为空。");
});

if (args.Contains("--integration"))
{
    int index = Array.IndexOf(args, "--app");
    string app = Path.GetFullPath(args[index + 1]);
    void Sc(params string[] values)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in values) info.ArgumentList.Add(value);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(output, error);
        Assert(process.ExitCode == 0, $"sc.exe 失败（{process.ExitCode}）：{output.Result} {error.Result}");
    }
    void AwaitFile(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(200);
        Assert(File.Exists(path), "未收到测试进程标记文件：" + path);
    }
    Test("真实计划任务能注册、启动无网络测试程序并删除", () =>
    {
        string name = "AutoChangeGateWay.Validation.Task." + Guid.NewGuid().ToString("N");
        string marker = Path.Combine(root, "task-started-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            StartupTask.Register(app, $"--startup-smoke \"{marker}\"", name);
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            try { scheduler.Connect(); scheduler.GetFolder("\\").GetTask(name).Run(null); AwaitFile(marker); }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(scheduler); }
        }
        finally { StartupTask.Remove(name); }
    });
    Test("真实 Windows 服务能启动、接受停止请求并删除", () =>
    {
        string name = "AutoChangeGateWay.Validation.Service." + Guid.NewGuid().ToString("N");
        string data = Path.Combine(root, name); Directory.CreateDirectory(data);
        bool created = false;
        try
        {
            Sc("create", name, "binPath=", $"\"{app}\" --service-smoke {name} \"{data}\"", "start=", "demand"); created = true;
            using var service = new ServiceController(name);
            service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
            AwaitFile(Path.Combine(data, "started.txt"));
            service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            AwaitFile(Path.Combine(data, "stopped.txt"));
        }
        finally
        {
            if (created)
            {
                using (var service = new ServiceController(name))
                {
                    if (service.Status != ServiceControllerStatus.Stopped)
                    { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
                }
                Sc("delete", name);
            }
        }
    });
}
Console.WriteLine($"Windows 运行支持测试：通过 {passed}，失败 {failed}。不修改网络配置。");
return failed == 0 ? 0 : 1;
