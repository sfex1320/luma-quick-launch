using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Luma.Host.Services;

/// <summary>Graceful installer shutdown is scoped to the actual executable, never a process name.</summary>
public sealed class InstanceShutdownSignal : IDisposable
{
    public EventWaitHandle Handle { get; }
    public InstanceShutdownSignal(string executablePath) => Handle = new(false, EventResetMode.AutoReset, EventName(executablePath));
    public static bool TryRequest(string executablePath)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName(executablePath), out var signal)) return false;
            using (signal) return signal.Set();
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
    private static string EventName(string executablePath) => "Local\\Luma.ProjectDock.Shutdown." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executablePath).ToUpperInvariant())));
    public void Dispose() => Handle.Dispose();
}
