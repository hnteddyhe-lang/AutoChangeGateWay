using AutoChangeGateWay.Core;
using AutoChangeGateWay.Windows;

namespace AutoChangeGateWay.App;

public sealed partial class MainForm
{
    private readonly ComboBox runtimeMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly CheckBox startWithWindows = new() { Text = "随 Windows 自动启动", AutoSize = true };
    private readonly CheckBox startMinimized = new() { Text = "启动时收起到托盘", AutoSize = true, Checked = true };
    private readonly Label serviceState = Caption("Windows 服务：未安装");
    private readonly Button applyRuntime = ActionButton("应用运行设置", 120, true);
    private readonly Button startService = ActionButton("启动服务", 98);
    private readonly Button stopService = ActionButton("停止并恢复", 108);
    private readonly Button uninstallService = ActionButton("卸载服务", 98);
    private readonly System.Windows.Forms.Timer runtimeTimer = new() { Interval = 2000 };
    private ServiceInfo serviceInfo = new(false, false, "未安装");
    private NetworkOwnership? networkOwnership;
    private bool ServiceMode => runtimeMode.SelectedIndex == 1;

    private AppSettings ReadInitialSettings()
    {
        var personal = storage.LoadSettings();
        if (preview) return personal;
        serviceInfo = ServiceManagement.Query();
        return serviceInfo.Installed && (serviceInfo.Running || personal.RuntimeMode == RuntimeMode.WindowsService)
            ? AppStorage.ReadSettings(ServiceManagement.DataRoot) ?? personal : personal;
    }

    private Control BuildRuntimePanel()
    {
        runtimeMode.Items.AddRange(["桌面托盘", "Windows 服务"]); runtimeMode.SelectedIndex = 0;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 5, 14, 5),
            Margin = new Padding(0, 0, 0, 6), ColumnCount = 2, RowCount = 3 };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 455));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var caption = Caption("运行方式"); caption.Dock = DockStyle.None; caption.Size = new Size(78, 28);
        choices.Controls.AddRange([caption, runtimeMode, startWithWindows]); startWithWindows.Margin = new Padding(10, 6, 0, 0);
        panel.Controls.Add(choices, 0, 0); panel.Controls.Add(startMinimized, 1, 0); startMinimized.Anchor = AnchorStyles.Left;
        panel.Controls.Add(serviceState, 0, 1);
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        commands.Controls.AddRange([applyRuntime, startService, stopService, uninstallService]); panel.Controls.Add(commands, 1, 1);
        var hint = Caption("桌面模式：当前用户登录后启动；服务模式：开机后可在无人登录时监控。应用或管理服务需要管理员权限。");
        hint.ForeColor = Color.SlateGray; hint.Font = new Font(Font.FontFamily, 9F);
        panel.Controls.Add(hint, 0, 2); panel.SetColumnSpan(hint, 2); return panel;
    }

    private void WireRuntimeEvents()
    {
        runtimeMode.SelectedIndexChanged += (_, _) => UpdateActions();
        applyRuntime.Click += async (_, _) => await RunActionAsync(ApplyRuntimeAsync);
        startService.Click += async (_, _) => await RunActionAsync(async () =>
        {
            RequireRuntimeAdministrator(); await StopMonitoringAsync();
            if (routes.Session != null) await RestoreAsync();
            ReleaseNetworkOwnership();
            await Task.Run(ServiceManagement.Start); PollService(); AddLog("Windows 服务已启动，使用已应用的服务配置。");
        });
        stopService.Click += async (_, _) => await RunActionAsync(async () =>
        {
            RequireRuntimeAdministrator(); await Task.Run(ServiceManagement.Stop); PollService();
            var state = AppStorage.ReadState(ServiceManagement.DataRoot);
            AddLog(state?.Message ?? "Windows 服务已停止。");
        });
        uninstallService.Click += async (_, _) => await RunActionAsync(async () =>
        {
            RequireRuntimeAdministrator(); await Task.Run(ServiceManagement.Uninstall);
            runtimeMode.SelectedIndex = 0; startWithWindows.Checked = false;
            storage.SaveSettings(ReadInputs()); PollService(); AddLog("Windows 服务已卸载，配置和日志保留。");
        });
        runtimeTimer.Tick += (_, _) => PollService();
    }

    private void InitializeRuntime(bool automaticStartup)
    {
        if (!preview) { PollService(); runtimeTimer.Start(); }
        if (!automaticStartup || initial.RuntimeMode != RuntimeMode.Desktop || !initial.StartWithWindows) return;
        if (initial.StartMinimized) { Opacity = 0; ShowInTaskbar = false; }
        Shown += async (_, _) =>
        {
            if (initial.StartMinimized) { Hide(); Opacity = 1; ShowInTaskbar = true; }
            try { await StartMonitoringAsync(true); }
            catch (Exception error)
            {
                ReleaseNetworkOwnership(); AddLog("自动启动监控未完成：" + error.Message);
                SetStatus("自动启动等待处理", error.Message, true);
            }
            UpdateActions();
        };
    }

    private void RequireRuntimeAdministrator()
    {
        if (preview) throw new InvalidOperationException("预览模式禁止修改启动项或服务。");
        if (!administrator) throw new InvalidOperationException("请先点击“管理员重启”，再应用运行设置或管理服务。");
    }

    private async Task ApplyRuntimeAsync()
    {
        RequireRuntimeAdministrator();
        var config = ReadInputs();
        await StopMonitoringAsync();
        if (routes.Session != null) await RestoreAsync();
        ReleaseNetworkOwnership();
        await Task.Run(() =>
        {
            if (config.RuntimeMode == RuntimeMode.WindowsService)
            {
                StartupTask.Remove(); ServiceManagement.Apply(config); ServiceManagement.Start();
            }
            else
            {
                ServiceManagement.DisableAutomaticStart();
                if (config.StartWithWindows) { ServiceManagement.DeployExecutable(); StartupTask.Register(ServiceManagement.Executable); }
                else StartupTask.Remove();
            }
        });
        storage.SaveSettings(config); initial = config; PollService();
        AddLog(ServiceMode ? "服务配置已应用并启动；关闭桌面界面不会停止服务。"
            : config.StartWithWindows ? "已启用当前用户登录后自启动，并使用保存的配置自动监控。" : "已关闭登录自启动，Windows 服务也已停止并改为手动启动。");
    }

    private void PollService()
    {
        if (preview || busy || exiting) return;
        try
        {
            serviceInfo = ServiceManagement.Query(); serviceState.Text = "Windows 服务：" + serviceInfo.State;
            if (ServiceMode && serviceInfo.Installed)
            {
                var state = AppStorage.ReadState(ServiceManagement.DataRoot);
                if (serviceInfo.Running && state != null)
                {
                    bool stale = (DateTimeOffset.Now - state.UpdatedAt).TotalSeconds > 20;
                    SetStatus(stale ? "服务运行中 · 等待新状态" : state.Heading, state.Message, stale || state.Primary?.Health == Health.Unavailable);
                    current.Text = state.CurrentGateway ?? "等待服务检测";
                    primaryState.Text = "主网关：" + (state.Primary?.Detail ?? "等待检测");
                    backupState.Text = "备用网关：" + (state.Backup?.Detail ?? "等待检测");
                }
                else if (!serviceInfo.Running) SetStatus("服务" + serviceInfo.State, state?.Message ?? "点击启动服务开始监控。", false);
            }
            UpdateActions();
        }
        catch (Exception error) { serviceState.Text = "服务状态读取失败：" + error.Message; }
    }

    private void UpdateRuntimeActions(bool running)
    {
        bool available = !running && !busy && !exiting;
        runtimeMode.Enabled = startWithWindows.Enabled = available;
        startMinimized.Enabled = available && !ServiceMode;
        applyRuntime.Enabled = available && administrator;
        startService.Enabled = available && administrator && ServiceMode && serviceInfo.Installed && !serviceInfo.Running;
        stopService.Enabled = available && administrator && serviceInfo.Installed && serviceInfo.Running;
        uninstallService.Enabled = available && administrator && serviceInfo.Installed;
        footer.Text = ServiceMode ? "Windows 服务独立运行；关闭或退出界面不会停止服务。停止服务会尝试恢复原配置。"
            : "关闭窗口会收起到托盘；停止监控会保留当前路由；退出时自动尝试恢复原配置。";
    }

    private void ReleaseNetworkOwnership() { networkOwnership?.Dispose(); networkOwnership = null; }
}
