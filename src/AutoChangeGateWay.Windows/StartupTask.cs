using System.Runtime.InteropServices;
using System.Security.Principal;

namespace AutoChangeGateWay.Windows;

public static class StartupTask
{
    public static string Name => "AutoChangeGateWay.Desktop." + WindowsIdentity.GetCurrent().User!.Value;

    public static void Register(string executable, string arguments = "--autostart", string? taskName = null)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try
        {
            scheduler.Connect();
            dynamic root = scheduler.GetFolder("\\");
            dynamic definition = scheduler.NewTask(0);
            string sid = WindowsIdentity.GetCurrent().User!.Value;
            definition.RegistrationInfo.Description = "当前用户登录后以托盘方式运行自动网关切换。";
            definition.Principal.UserId = sid;
            definition.Principal.LogonType = 3;
            definition.Principal.RunLevel = 1;
            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2;
            dynamic trigger = definition.Triggers.Create(9);
            trigger.UserId = sid; trigger.Delay = "PT15S"; trigger.Enabled = true;
            dynamic action = definition.Actions.Create(0);
            action.Path = executable; action.Arguments = arguments; action.WorkingDirectory = Path.GetDirectoryName(executable);
            root.RegisterTaskDefinition(taskName ?? Name, definition, 6, sid, null, 3, null);
        }
        finally { Marshal.FinalReleaseComObject(scheduler); }
    }

    public static void Remove(string? taskName = null)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        try
        {
            scheduler.Connect(); dynamic root = scheduler.GetFolder("\\");
            root.DeleteTask(taskName ?? Name, 0);
        }
        catch (Exception error) when (error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003)) { }
        finally { Marshal.FinalReleaseComObject(scheduler); }
    }
}
