using System.Diagnostics;
using System.Text.Json;
using System.ServiceProcess;
using AutoChangeGateWay.Windows;

namespace AutoChangeGateWay.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // 服务在会话 0 内运行，必须在初始化任何桌面界面前交给服务控制管理器。
            if (args.Length == 1 && args[0] == "--service")
            {
                ServiceBase.Run(new GatewayWindowsService()); return 0;
            }
            if (args.Length == 3 && args[0] == "--service-smoke")
            {
                ServiceBase.Run(new GatewayWindowsService(args[1], args[2])); return 0;
            }
            if (args.Length == 2 && args[0] == "--startup-smoke")
            {
                File.WriteAllText(args[1], "计划任务启动成功；未调用网络 API。"); return 0;
            }
            ApplicationConfiguration.Initialize();
            if (args.Length == 2 && args[0] == "--diagnose")
            {
                var network = new WindowsNetwork();
                var data = network.GetAdapters().Select(a =>
                {
                    var entries = network.GetDefaultRoutes(a.Id);
                    var source = a.Addresses.FirstOrDefault();
                    var probe = a.IsUp && source != null && entries.Count > 0 && entries[0].NextHop != "0.0.0.0"
                        ? WindowsNetwork.Probe(a, source.Address, entries[0].NextHop, 1000) : null;
                    return new { Adapter = a, Routes = entries, Probe = probe };
                }).ToArray();
                File.WriteAllText(args[1], JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--ui-smoke")
            {
                using var form = new MainForm(preview: true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-30000, -30000);
                form.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--wait-for-exit" && int.TryParse(args[1], out var processId))
            {
                try { Process.GetProcessById(processId).WaitForExit(15000); } catch (ArgumentException) { }
            }
            using var mutex = new Mutex(true, @"Local\AutoChangeGateWay.Desktop", out bool created);
            if (!created)
            {
                MessageBox.Show("程序已在运行，请从任务栏右下角托盘打开。", "自动网关切换");
                return 0;
            }
            Application.Run(new MainForm(automaticStartup: args.Contains("--autostart")));
            return 0;
        }
        catch (Exception error)
        {
            if (args.Contains("--service") || args.Contains("--service-smoke")) return 1;
            if (args.Length == 2 && args[0] is "--diagnose" or "--ui-smoke")
            {
                File.WriteAllText(args[1] + ".error.txt", error.ToString());
                return 1;
            }
            MessageBox.Show("启动失败：" + error.Message + "\n配置与恢复记录没有被清空。", "自动网关切换", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
