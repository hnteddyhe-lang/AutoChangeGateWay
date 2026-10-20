using System.Text;
using System.Text.Json;
using AutoChangeGateWay.Core;

namespace AutoChangeGateWay.Windows;

public sealed class AppStorage : IJournal
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string Root { get; }
    public string LogFile => Path.Combine(Root, "logs", DateTime.Now.ToString("yyyy-MM-dd") + ".log");
    private string JournalPath => Path.Combine(Root, "route-session.json");
    public AppStorage(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoChangeGateWay");
        Directory.CreateDirectory(Root);
    }

    public AppSettings LoadSettings() => Read<AppSettings>(Path.Combine(Root, "settings.json")) ?? new();
    public static AppSettings? ReadSettings(string root) => Read<AppSettings>(Path.Combine(root, "settings.json"));
    public void SaveSettings(AppSettings settings) => AtomicWrite(Path.Combine(Root, "settings.json"), settings);
    public void WriteState(ServiceRuntimeState state) => AtomicWrite(Path.Combine(Root, "service-status.json"), state);
    public static ServiceRuntimeState? ReadState(string root) => Read<ServiceRuntimeState>(Path.Combine(root, "service-status.json"));
    public RouteSession? Load() => Read<RouteSession>(JournalPath);
    public void Save(RouteSession session) => AtomicWrite(JournalPath, session);
    public void Clear() => File.Delete(JournalPath);

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        // 允许后台服务在界面读取状态时原子替换文件，避免 Windows 文件共享冲突。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"配置文件为空或损坏：{path}");
    }

    private static void AtomicWrite<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(true);
        }
        // Windows 文件系统及杀毒软件可能短暂占用目标，有限重试后仍失败则禁止后续路由写入。
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, path, true); return; }
            catch (IOException) when (attempt < 5) { Thread.Sleep(10 << attempt); }
            catch (UnauthorizedAccessException) when (attempt < 5) { Thread.Sleep(10 << attempt); }
        }
    }

    public void Log(string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        File.AppendAllText(LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}{Environment.NewLine}", Encoding.UTF8);
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(LogFile)!, "*.log")
                     .Where(p => File.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddDays(-30))) File.Delete(old);
    }

    public static DateTimeOffset BootTime() => DateTimeOffset.UtcNow.AddMilliseconds(-Environment.TickCount64);
}
