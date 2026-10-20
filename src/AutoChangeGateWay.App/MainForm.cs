using System.Diagnostics;
using System.Security.Principal;
using AutoChangeGateWay.Core;
using AutoChangeGateWay.Windows;

namespace AutoChangeGateWay.App;

public sealed partial class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(27, 38, 59);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private readonly WindowsNetwork network = new();
    private readonly AppStorage storage;
    private readonly RouteManager routes;
    private readonly bool preview;
    private readonly bool administrator = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private readonly ComboBox adapters = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox sources = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox primary = new() { Text = "192.168.88.191", Dock = DockStyle.Fill };
    private readonly TextBox backup = new() { Text = "192.168.88.1", Dock = DockStyle.Fill };
    private readonly NumericUpDown interval = Number(1, 300, 3);
    private readonly NumericUpDown timeout = Number(100, 10000, 1000);
    private readonly NumericUpDown failures = Number(1, 30, 3);
    private readonly NumericUpDown recoveries = Number(1, 30, 5);
    private readonly NumericUpDown cooldown = Number(0, 3600, 30);
    private readonly CheckBox detection = new() { Text = "仅检测，不更改网关", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly CheckBox failback = new() { Text = "主网关恢复后自动切回", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label current = Caption("读取中…");
    private readonly Label status = Caption("就绪 · 仅检测");
    private readonly Label detail = Caption("配置主备网关后，点击“开始监控”。");
    private readonly Label primaryState = Caption("主网关：尚未检测");
    private readonly Label backupState = Caption("备用网关：尚未检测");
    private readonly Label footer = Caption("关闭窗口会收起到托盘；停止监控会保留当前路由；退出时自动尝试恢复原配置。");
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(248, 250, 252), Font = new Font("Microsoft YaHei UI", 9F) };
    private readonly Button start = ActionButton("开始监控", 100, true);
    private readonly Button stop = ActionButton("停止监控", 96);
    private readonly Button save = ActionButton("保存配置", 96);
    private readonly Button toPrimary = ActionButton("切到主网关", 108);
    private readonly Button toBackup = ActionButton("切到备用", 96);
    private readonly Button restore = ActionButton("恢复原配置", 108);
    private readonly Button refresh = ActionButton("刷新网卡", 96);
    private readonly Button elevate = ActionButton("管理员重启", 120);
    private readonly Panel settingsPanel = new() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14) };
    private readonly NotifyIcon tray;
    private readonly SemaphoreSlim operation = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? monitorTask;
    private AppSettings initial;
    private bool recoveryPending;
    private bool busy;
    private bool allowClose;
    private bool exiting;
    private string? lastLog;
    private string? desiredGateway;
    private DateTimeOffset lastSummary = DateTimeOffset.MinValue;

    public MainForm(bool preview = false, bool automaticStartup = false)
    {
        this.preview = preview;
        storage = new AppStorage(preview ? Path.Combine(Path.GetTempPath(), "AutoChangeGateWay-preview-" + Guid.NewGuid().ToString("N")) : null);
        routes = new RouteManager(network, storage, AppStorage.BootTime);
        initial = ReadInitialSettings();
        Text = "自动网关切换 · AutoChangeGateWay";
        Font = new Font("Microsoft YaHei UI", 9.5F);
        ForeColor = Ink;
        BackColor = Color.FromArgb(241, 245, 249);
        ClientSize = new Size(1080, 920);
        MinimumSize = new Size(1040, 890);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = SystemIcons.Application;
        BuildLayout();
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "自动网关切换 · 未监控", Visible = !preview };
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => ShowMain());
        menu.Items.Add("停止监控", null, async (_, _) => await StopMonitoringAsync());
        menu.Items.Add("恢复原配置", null, async (_, _) => await RunActionAsync(RestoreAsync));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出并恢复", null, async (_, _) => await ExitAsync());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowMain();
        FormClosing += (_, e) =>
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                tray.ShowBalloonTip(2000, "自动网关切换", "程序已收起到托盘。右键托盘图标可停止、恢复或退出。", ToolTipIcon.Info);
            }
        };
        FormClosed += (_, _) =>
        {
            tray.Visible = false; tray.Dispose(); cancellation?.Cancel();
            networkOwnership?.Dispose(); runtimeTimer.Stop(); runtimeTimer.Dispose();
        };
        WireEvents();
        LoadInputs(initial);
        RefreshAdapters();
        if (routes.ClearPreviousBootSession()) AddLog("已清理上次开机的恢复记录；本次网络配置未被修改。");
        recoveryPending = routes.Session != null;
        if (recoveryPending)
        {
            SetStatus("发现待恢复记录", "上次运行未完成恢复，请先点击“恢复原配置”。可以继续仅检测。", true);
            AddLog("发现未完成的路由恢复记录，禁止直接开始新的自动切换。");
        }
        else AddLog("已就绪。仅检测模式不会修改网关；实际切换需要管理员权限。");
        if (preview)
        {
            SetStatus("就绪 · 仅检测", "选择网卡并填写主备网关。先检测，再启用自动切换。", false);
            primaryState.Text = "主网关：192.168.88.191 · 尚未检测";
            backupState.Text = "备用网关：192.168.88.1 · 尚未检测";
        }
        InitializeRuntime(automaticStartup);
        UpdateActions();
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = false, Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(4, 1, 4, 1) };
    private static NumericUpDown Number(int min, int max, int value) => new()
        { Minimum = min, Maximum = max, Value = value, Dock = DockStyle.Fill, ThousandsSeparator = true };
    private static Button ActionButton(string text, int width, bool accent = false) => new()
    {
        Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat,
        BackColor = accent ? Accent : Color.White, ForeColor = accent ? Color.White : Ink,
        Margin = new Padding(0, 4, 7, 4), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
    };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 10), RowCount = 7, ColumnCount = 1 };
        foreach (var size in new[] { 68F, 94F, 266F, 120F, 48F }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = Caption("自动网关切换"); title.Font = new Font(Font.FontFamily, 21F, FontStyle.Bold);
        header.Controls.Add(title, 0, 0);
        var subtitle = Caption("指定网卡 · 主备检测 · 故障自动切换"); subtitle.ForeColor = Color.SlateGray;
        header.Controls.Add(subtitle, 0, 1); header.Controls.Add(elevate, 1, 0); header.SetRowSpan(elevate, 2);
        elevate.Anchor = AnchorStyles.Right; root.Controls.Add(header, 0, 0);

        var state = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(230, 239, 255),
            Padding = new Padding(14, 7, 14, 7), ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 3, 0, 9) };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60)); state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        state.RowStyles.Add(new RowStyle(SizeType.Percent, 46)); state.RowStyles.Add(new RowStyle(SizeType.Percent, 54));
        status.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold); status.ForeColor = Accent;
        detail.Font = new Font(Font.FontFamily, 9F);
        state.Controls.Add(status, 0, 0); state.Controls.Add(detail, 0, 1);
        state.Controls.Add(primaryState, 1, 0); state.Controls.Add(backupState, 1, 1); root.Controls.Add(state, 0, 1);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 6 };
        foreach (var width in new[] { 92F, 0F, 100F, 0F, 108F, 0F })
            grid.ColumnStyles.Add(new ColumnStyle(width == 0 ? SizeType.Percent : SizeType.Absolute, width == 0 ? 33.33F : width));
        for (int i = 0; i < 6; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / 6));
        void Field(string label, Control control, int column, int row)
        {
            grid.Controls.Add(Caption(label), column, row); control.Margin = new Padding(4, 5, 8, 5); grid.Controls.Add(control, column + 1, row);
        }
        Field("管理网卡", adapters, 0, 0); grid.SetColumnSpan(adapters, 4); grid.Controls.Add(refresh, 5, 0); refresh.Dock = DockStyle.Fill;
        Field("检测源 IP", sources, 0, 1); Field("当前网关", current, 2, 1); grid.SetColumnSpan(current, 3);
        Field("主网关", primary, 0, 2); Field("备用网关", backup, 2, 2); Field("恢复成功次数", recoveries, 4, 2);
        Field("间隔 / 秒", interval, 0, 3); Field("超时 / 毫秒", timeout, 2, 3); Field("连续失败次数", failures, 4, 3);
        Field("冷却 / 秒", cooldown, 0, 4); grid.Controls.Add(detection, 2, 4); grid.SetColumnSpan(detection, 2);
        grid.Controls.Add(failback, 4, 4); grid.SetColumnSpan(failback, 2);
        var hint = Caption("检测网关是否回应 Ping；只管理 IPv4 默认路由，不更改 IP 或 DNS。主备均无响应时保留当前路由。");
        hint.ForeColor = Color.SlateGray; hint.Font = new Font(Font.FontFamily, 9F); grid.Controls.Add(hint, 0, 5); grid.SetColumnSpan(hint, 6);
        settingsPanel.Controls.Add(grid); settingsPanel.Margin = new Padding(0, 0, 0, 6); root.Controls.Add(settingsPanel, 0, 2);
        root.Controls.Add(BuildRuntimePanel(), 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var openLogs = ActionButton("日志目录", 90); var exit = ActionButton("退出", 64);
        actions.Controls.AddRange([start, stop, save, toPrimary, toBackup, restore, openLogs, exit]);
        openLogs.Click += (_, _) =>
        {
            try
            {
                var directory = Path.Combine(ServiceMode ? ServiceManagement.DataRoot : storage.Root, "logs");
                if (ServiceMode && !Directory.Exists(directory)) throw new InvalidOperationException("服务日志尚未生成，请先安装并启动服务。");
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception error) { Report(error); }
        };
        exit.Click += async (_, _) => await ExitAsync(); root.Controls.Add(actions, 0, 4);

        var logPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 250, 252), Padding = new Padding(14, 7, 14, 10), Margin = new Padding(0, 3, 0, 0) };
        var logTitle = Caption("运行日志"); logTitle.Dock = DockStyle.Top; logTitle.Height = 28; logTitle.Font = new Font(Font, FontStyle.Bold);
        logPanel.Controls.Add(log); logPanel.Controls.Add(logTitle); root.Controls.Add(logPanel, 0, 5);
        footer.Font = new Font(Font.FontFamily, 9F); footer.ForeColor = Color.SlateGray; root.Controls.Add(footer, 0, 6);
    }

    private void WireEvents()
    {
        adapters.SelectedIndexChanged += (_, _) => PopulateSources();
        sources.SelectedIndexChanged += (_, _) => ShowCurrentRoutes();
        refresh.Click += (_, _) => { try { RefreshAdapters(); } catch (Exception e) { Report(e); } };
        save.Click += (_, _) => { try { storage.SaveSettings(ReadInputs()); AddLog("桌面配置已保存。自启动或服务配置需点击“应用运行设置”生效。"); } catch (Exception e) { Report(e); } };
        start.Click += async (_, _) => await RunActionAsync(StartMonitoringAsync);
        stop.Click += async (_, _) => await StopMonitoringAsync();
        toPrimary.Click += async (_, _) => await RunActionAsync(() => ManualSwitchAsync(true));
        toBackup.Click += async (_, _) => await RunActionAsync(() => ManualSwitchAsync(false));
        restore.Click += async (_, _) => await RunActionAsync(RestoreAsync);
        elevate.Click += (_, _) => RestartElevated();
        detection.CheckedChanged += (_, _) => UpdateActions();
        WireRuntimeEvents();
    }

    private void LoadInputs(AppSettings value)
    {
        primary.Text = value.PrimaryGateway; backup.Text = value.BackupGateway;
        interval.Value = Math.Clamp(value.IntervalSeconds, 1, 300); timeout.Value = Math.Clamp(value.TimeoutMilliseconds, 100, 10000);
        failures.Value = Math.Clamp(value.FailureThreshold, 1, 30); recoveries.Value = Math.Clamp(value.RecoveryThreshold, 1, 30);
        cooldown.Value = Math.Clamp(value.CooldownSeconds, 0, 3600); failback.Checked = value.AutoFailback; detection.Checked = value.DetectionOnly;
        runtimeMode.SelectedIndex = value.RuntimeMode == RuntimeMode.WindowsService ? 1 : 0;
        startWithWindows.Checked = value.StartWithWindows; startMinimized.Checked = value.StartMinimized;
    }

    private AppSettings ReadInputs()
    {
        var config = new AppSettings
        {
            AdapterId = (adapters.SelectedItem as AdapterInfo)?.Id ?? Guid.Empty,
            SourceAddress = (sources.SelectedItem as AddressInfo)?.Address ?? "",
            PrimaryGateway = primary.Text.Trim(), BackupGateway = backup.Text.Trim(),
            IntervalSeconds = (int)interval.Value, TimeoutMilliseconds = (int)timeout.Value,
            FailureThreshold = (int)failures.Value, RecoveryThreshold = (int)recoveries.Value,
            CooldownSeconds = (int)cooldown.Value, AutoFailback = failback.Checked, DetectionOnly = detection.Checked,
            RuntimeMode = ServiceMode ? RuntimeMode.WindowsService : RuntimeMode.Desktop,
            StartWithWindows = startWithWindows.Checked, StartMinimized = startMinimized.Checked
        };
        config.Validate(); return config;
    }

    private void RefreshAdapters()
    {
        var id = (adapters.SelectedItem as AdapterInfo)?.Id ?? initial.AdapterId;
        var items = network.GetAdapters();
        if (preview) items = [new AdapterInfo(Guid.NewGuid(), "以太网", "示例网卡 · Intel Ethernet", 12, true, [new("192.168.88.100", 24)])];
        adapters.Items.Clear(); adapters.Items.AddRange(items.Cast<object>().ToArray());
        adapters.SelectedItem = items.FirstOrDefault(a => a.Id == id)
            ?? items.FirstOrDefault(a => a.IsUp && a.Addresses.Any(IsGatewaySubnet))
            ?? items.FirstOrDefault(a => a.IsUp && a.Addresses.Length > 0 && network.GetDefaultRoutes(a.Id).Count > 0)
            ?? items.FirstOrDefault(a => a.IsUp && a.Addresses.Length > 0) ?? items.FirstOrDefault();
        if (items.Length == 0) current.Text = "未找到 IPv4 网卡";
    }

    private void PopulateSources()
    {
        var selected = (sources.SelectedItem as AddressInfo)?.Address ?? initial.SourceAddress;
        sources.Items.Clear();
        if (adapters.SelectedItem is AdapterInfo adapter)
        {
            sources.Items.AddRange(adapter.Addresses.Cast<object>().ToArray());
            sources.SelectedItem = adapter.Addresses.FirstOrDefault(a => a.Address == selected)
                ?? adapter.Addresses.FirstOrDefault(IsGatewaySubnet) ?? adapter.Addresses.FirstOrDefault();
        }
        ShowCurrentRoutes();
    }

    private bool IsGatewaySubnet(AddressInfo address)
    {
        try { return IPv4.IsOnLink(address.Address, address.PrefixLength, primary.Text.Trim()) &&
                     IPv4.IsOnLink(address.Address, address.PrefixLength, backup.Text.Trim()); }
        catch (InvalidOperationException) { return false; }
    }

    private void ShowCurrentRoutes()
    {
        if (preview) { current.Text = "192.168.88.191"; return; }
        try { current.Text = adapters.SelectedItem is AdapterInfo a ? DescribeRoutes(network.GetDefaultRoutes(a.Id)) : "未选择网卡"; }
        catch (Exception e) { current.Text = e.Message; }
    }
    private static string DescribeRoutes(IReadOnlyList<DefaultRoute> entries) => entries.Count == 0 ? "无 IPv4 默认网关" :
        string.Join("；", entries.Select(r => $"{r.NextHop}（度量 {r.Metric}）"));

    private async Task RunActionAsync(Func<Task> action)
    {
        if (busy || exiting) return;
        busy = true; UpdateActions();
        try { await action(); } catch (Exception e) { Report(e); }
        finally
        {
            busy = false;
            if (monitorTask == null && routes.Session == null) ReleaseNetworkOwnership();
            UpdateActions();
        }
    }

    private Task StartMonitoringAsync() => StartMonitoringAsync(false);

    private Task StartMonitoringAsync(bool waitForNetwork)
    {
        if (monitorTask != null) return Task.CompletedTask;
        var config = waitForNetwork ? initial : ReadInputs();
        config.Validate();
        if (ServiceMode) throw new InvalidOperationException("服务运行方式请使用“启动服务”。");
        if (!waitForNetwork) network.GetAdapter(config.AdapterId).ValidateGateways(config);
        if (!config.DetectionOnly) { EnsureWritable(); networkOwnership ??= new NetworkOwnership(); }
        storage.SaveSettings(config);
        cancellation = new CancellationTokenSource();
        AddLog(config.DetectionOnly ? "开始仅检测：不会修改路由。" : "开始自动切换监控。修改仅在程序运行期间生效。");
        monitorTask = MonitorAsync(config, cancellation.Token);
        return Task.CompletedTask;
    }

    private async Task MonitorAsync(AppSettings config, CancellationToken token)
    {
        var policy = new FailoverPolicy(config);
        // 让调用方先保存任务引用，再开始第一轮检测。
        await Task.Yield();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await operation.WaitAsync(token);
                try
                {
                    var result = await Task.Run(() =>
                    {
                        var adapter = network.GetAdapter(config.AdapterId); adapter.ValidateGateways(config);
                        var currentRoutes = network.GetDefaultRoutes(config.AdapterId);
                        var p = WindowsNetwork.Probe(adapter, config.SourceAddress, config.PrimaryGateway, config.TimeoutMilliseconds);
                        var b = WindowsNetwork.Probe(adapter, config.SourceAddress, config.BackupGateway, config.TimeoutMilliseconds);
                        return (currentRoutes, p, b);
                    }, token);
                    token.ThrowIfCancellationRequested();
                    current.Text = DescribeRoutes(result.currentRoutes);
                    primaryState.Text = $"主网关 {config.PrimaryGateway}：{result.p.Detail}";
                    backupState.Text = $"备用 {config.BackupGateway}：{result.b.Detail}";
                    var gateway = result.currentRoutes.Count == 1 ? result.currentRoutes[0].NextHop : null;
                    var decision = policy.Evaluate(gateway, result.p, result.b, DateTimeOffset.UtcNow);
                    if (result.currentRoutes.Count > 1 && routes.Session == null)
                        decision = new(null, "所选网卡有多条默认路由，已暂停切换，请检查配置。");
                    if (!config.DetectionOnly && desiredGateway != null && result.currentRoutes.Count > 1 &&
                        (desiredGateway == config.PrimaryGateway ? result.p : result.b).Health == Health.Online &&
                        result.p.Health != Health.Unavailable && result.b.Health != Health.Unavailable)
                        decision = new(desiredGateway, "默认路由被重新注入，重新应用本次运行正在使用的网关。");
                    if (decision.Target != null && !config.DetectionOnly)
                    {
                        await Task.Run(() => routes.Switch(config, decision.Target), token);
                        desiredGateway = decision.Target;
                        policy.Switched(DateTimeOffset.UtcNow);
                        AddLog("已切换到 " + decision.Target + "。" + decision.Message);
                        current.Text = DescribeRoutes(network.GetDefaultRoutes(config.AdapterId));
                        tray.ShowBalloonTip(3000, "网关已切换", $"{config.AdapterId} → {decision.Target}", ToolTipIcon.Info);
                        UpdateActions();
                    }
                    string message = decision.Target != null && config.DetectionOnly ? "仅检测 · 建议切换至 " + decision.Target : decision.Message;
                    bool warning = result.p.Health != Health.Online || result.b.Health == Health.Unavailable;
                    SetStatus(config.DetectionOnly ? "监控中 · 仅检测" : "监控中 · 自动切换", message, warning);
                    if (message != lastLog || (DateTimeOffset.Now - lastSummary).TotalMinutes >= 1)
                    {
                        AddLog(message + $" 主：{result.p.Detail}；备：{result.b.Detail}。");
                        lastLog = message; lastSummary = DateTimeOffset.Now;
                    }
                }
                finally { operation.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                policy.ResetCounters();
                primaryState.Text = "主网关：检测不可用";
                backupState.Text = "备用网关：检测不可用";
                SetStatus("检测暂停 · 等待重试", e.Message, true);
                if (lastLog != e.Message) { AddLog(e.Message); lastLog = e.Message; }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(config.IntervalSeconds), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task StopMonitoringAsync()
    {
        if (monitorTask == null) return;
        cancellation?.Cancel();
        await monitorTask;
        monitorTask = null; cancellation?.Dispose(); cancellation = null;
        SetStatus("已停止", "当前路由保持不变；如需恢复，请点击“恢复原配置”。", false);
        AddLog("监控已停止，当前路由保持不变。"); UpdateActions();
        if (routes.Session == null) ReleaseNetworkOwnership();
    }

    private void EnsureWritable()
    {
        if (preview) throw new InvalidOperationException("界面预览禁止修改网络。");
        if (!administrator) throw new InvalidOperationException("修改网关需要管理员权限，请点击右上角“管理员重启”。");
        if (recoveryPending) throw new InvalidOperationException("请先恢复上次运行的原配置，再开始新的切换。");
        if (ServiceManagement.Query().Running) throw new InvalidOperationException("Windows 服务正在管理网关，请先停止服务。");
    }

    private async Task ManualSwitchAsync(bool usePrimary)
    {
        if (monitorTask != null) throw new InvalidOperationException("请先停止监控。");
        var config = ReadInputs(); EnsureWritable();
        if (config.DetectionOnly) throw new InvalidOperationException("请先取消“仅检测”，再执行手动切换。");
        network.GetAdapter(config.AdapterId).ValidateGateways(config);
        var gateway = usePrimary ? config.PrimaryGateway : config.BackupGateway;
        await operation.WaitAsync();
        try
        {
            var result = await Task.Run(() => WindowsNetwork.Probe(network.GetAdapter(config.AdapterId), config.SourceAddress, gateway, config.TimeoutMilliseconds));
            if (result.Health != Health.Online) throw new InvalidOperationException("目标网关检测未通过：" + result.Detail);
            networkOwnership ??= new NetworkOwnership();
            await Task.Run(() => routes.Switch(config, gateway)); desiredGateway = gateway;
            storage.SaveSettings(config); ShowCurrentRoutes(); AddLog("手动切换完成：" + gateway);
            SetStatus("已手动切换", "当前使用 " + gateway + "；监控尚未启动。", false);
        }
        finally { operation.Release(); }
    }

    private async Task RestoreAsync()
    {
        await StopMonitoringAsync();
        if (routes.Session == null) { AddLog("没有待恢复的配置。"); return; }
        if (!administrator) throw new InvalidOperationException("恢复网关需要管理员权限，请点击“管理员重启”。");
        await operation.WaitAsync();
        try
        {
            networkOwnership ??= new NetworkOwnership();
            await Task.Run(routes.Restore); recoveryPending = false; desiredGateway = null; ReleaseNetworkOwnership();
            ShowCurrentRoutes(); SetStatus("原配置已恢复", "本次运行添加的默认路由已清理。", false); AddLog("原默认路由已恢复，恢复记录已清除。");
        }
        finally { operation.Release(); }
    }

    private void RestartElevated()
    {
        if (administrator || busy || monitorTask != null) return;
        try
        {
            storage.SaveSettings(ReadInputs());
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true, Verb = "runas", Arguments = $"--wait-for-exit {Environment.ProcessId}", WorkingDirectory = AppContext.BaseDirectory
            });
            allowClose = true; Close();
        }
        catch (Exception e) { Report(e); }
    }

    private async Task ExitAsync()
    {
        if (exiting || busy) return;
        exiting = true; UpdateActions();
        await StopMonitoringAsync();
        try { if (routes.Session != null) await RestoreAsync(); }
        catch (Exception e)
        {
            AddLog("退出恢复失败：" + e.Message);
            if (MessageBox.Show(this, "未能恢复原配置：" + e.Message + "\n\n仍要退出吗？恢复记录将保留，重新打开程序后可重试。系统重启会清除本程序的临时路由。",
                    "恢复未完成", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            { exiting = false; UpdateActions(); return; }
        }
        allowClose = true; Close();
    }

    private void UpdateActions()
    {
        bool running = monitorTask != null;
        settingsPanel.Enabled = !running && !busy && !exiting;
        start.Enabled = !ServiceMode && !serviceInfo.Running && !running && !busy && !exiting;
        stop.Enabled = running && !exiting;
        save.Enabled = !running && !busy && !exiting;
        toPrimary.Enabled = toBackup.Enabled = !ServiceMode && !serviceInfo.Running && !running && !busy && !exiting && !detection.Checked && administrator && !recoveryPending;
        restore.Enabled = routes.Session != null && !busy && !exiting;
        elevate.Text = administrator ? "管理员模式" : "管理员重启";
        elevate.Enabled = !administrator && !running && !busy && !exiting;
        UpdateRuntimeActions(running);
    }

    private void SetStatus(string heading, string message, bool warning)
    {
        status.Text = heading; status.ForeColor = warning ? Color.FromArgb(180, 83, 9) : Accent;
        detail.Text = message;
        if (tray != null) tray.Text = ("自动网关切换 · " + heading)[..Math.Min(60, ("自动网关切换 · " + heading).Length)];
    }

    private void AddLog(string message)
    {
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        if (log.Lines.Length > 500) log.Lines = log.Lines.TakeLast(350).ToArray();
        log.SelectionStart = log.TextLength; log.ScrollToCaret();
        try { if (!preview) storage.Log(message); }
        catch (IOException) { footer.Text = "日志文件暂时无法写入，请检查磁盘空间；界面日志仍可查看。"; }
        catch (UnauthorizedAccessException) { footer.Text = "日志目录无法写入，请检查文件权限。"; }
    }

    private void Report(Exception error)
    {
        AddLog("操作失败：" + error.Message); SetStatus("操作未完成", error.Message, true);
        MessageBox.Show(this, error.Message, "自动网关切换", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
}
