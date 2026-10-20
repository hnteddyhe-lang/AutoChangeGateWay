namespace AutoChangeGateWay.Windows;

// Mutex 由专用线程持有和释放，使异步监控可跨线程使用，同时在进程崩溃后自动释放。
public sealed class NetworkOwnership : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private readonly Thread thread;
    private bool disposed;

    public NetworkOwnership(string name = @"Global\AutoChangeGateWay.NetworkOwner")
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(() =>
        {
            try
            {
                using var mutex = new Mutex(false, name);
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("另一个桌面实例或 Windows 服务正在管理网关，请先停止并恢复该实例。");
                try { ready.SetResult(); release.Wait(); }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "网关管理互斥锁" };
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { release.Set(); thread.Join(); release.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; release.Set(); thread.Join(); release.Dispose();
    }
}
