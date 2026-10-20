using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.ServiceProcess;
using Microsoft.Win32;
using AutoChangeGateWay.Core;

namespace AutoChangeGateWay.Windows;

public sealed record ServiceInfo(bool Installed, bool Running, string State);

public static class ServiceManagement
{
    public const string Name = "AutoChangeGateWay";
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AutoChangeGateWay");
    public static string Executable => Path.Combine(InstallRoot, "AutoChangeGateWay.exe");
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoChangeGateWay", "Service");
    private static string ImageCommand => $"\"{Executable}\" --service";

    public static ServiceInfo Query()
    {
        using var service = new ServiceController(Name);
        try
        {
            var state = service.Status;
            return new(true, state is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending,
                state switch { ServiceControllerStatus.Running => "运行中", ServiceControllerStatus.Stopped => "已停止",
                    ServiceControllerStatus.StartPending => "正在启动", ServiceControllerStatus.StopPending => "正在停止", _ => state.ToString() });
        }
        catch (InvalidOperationException error) when (error.InnerException is Win32Exception { NativeErrorCode: 1060 })
            { return new(false, false, "未安装"); }
    }

    private static void CheckManaged()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Name);
        if (key != null && !string.Equals(key.GetValue("ImagePath") as string, ImageCommand, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("发现同名但路径不同的 Windows 服务，拒绝覆盖，请先检查服务配置。");
    }

    public static void Apply(AppSettings config)
    {
        config.Validate(); CheckManaged();
        if (Query().Installed)
        {
            Stop();
            if (File.Exists(Path.Combine(DataRoot, "route-session.json")))
                throw new InvalidOperationException("服务仍有未完成的路由恢复记录，不能覆盖服务配置。请连接原网络后启动并停止服务完成恢复。");
        }
        DeployExecutable();
        ProtectDirectory(Path.GetDirectoryName(DataRoot)!); ProtectDirectory(DataRoot);
        new AppStorage(DataRoot).SaveSettings(config with { RuntimeMode = RuntimeMode.WindowsService });
        if (!Query().Installed)
            RunSc("create", Name, "binPath=", ImageCommand, "start=", config.StartWithWindows ? "delayed-auto" : "demand", "DisplayName=", "自动网关切换");
        else RunSc("config", Name, "start=", config.StartWithWindows ? "delayed-auto" : "demand");
        RunSc("description", Name, "持续检测指定网卡的主备网关；停止服务时恢复原路由。");
        RunSc("failure", Name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");
        RunSc("failureflag", Name, "1");
    }

    public static void Start()
    {
        CheckManaged();
        using var service = new ServiceController(Name);
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static void Stop()
    {
        CheckManaged();
        if (!Query().Installed) return;
        using var service = new ServiceController(Name);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(35));
    }

    public static void DisableAutomaticStart()
    {
        CheckManaged();
        if (Query().Installed)
        {
            Stop();
            if (File.Exists(Path.Combine(DataRoot, "route-session.json")))
                throw new InvalidOperationException("服务仍有待恢复的原路由，请先完成恢复，再切换运行方式。");
            RunSc("config", Name, "start=", "demand");
        }
    }

    public static void Uninstall()
    {
        CheckManaged(); if (!Query().Installed) return;
        Stop();
        if (File.Exists(Path.Combine(DataRoot, "route-session.json")))
            throw new InvalidOperationException("服务的原路由尚未恢复，恢复记录已保留，暂不卸载服务。");
        RunSc("delete", Name);
    }

    public static void DeployExecutable()
    {
        ProtectDirectory(InstallRoot);
        var files = Directory.EnumerateFiles(AppContext.BaseDirectory).Where(path =>
            Path.GetFileName(path) == "AutoChangeGateWay.exe" || Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase));
        foreach (var source in files)
        {
            string target = Path.Combine(InstallRoot, Path.GetFileName(source));
            if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true);
        }
    }

    private static void ProtectDirectory(string path)
    {
        Directory.CreateDirectory(path);
        var directory = new DirectoryInfo(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("安装路径不能是链接或重解析点：" + path);
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
        directory.SetAccessControl(security);
    }

    private static void RunSc(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法启动服务配置工具。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new System.TimeoutException("Windows 服务配置超时。"); }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows 服务配置失败（{process.ExitCode}）：{output.Result} {error.Result}");
    }
}
