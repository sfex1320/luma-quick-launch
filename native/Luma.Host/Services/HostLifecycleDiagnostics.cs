namespace Luma.Host.Services;

/// <summary>Event-only shutdown evidence. Logging never marks an exception handled or resumes the host.</summary>
internal sealed class HostLifecycleDiagnostics(Action<string> write, Action<string>? error = null)
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    private string? _reason;
    private int _completed;

    private void Record(string message) => write($"lifecycle pid={Environment.ProcessId} session={_session} {message}");

    public void RequestExit(string reason)
    {
        Interlocked.CompareExchange(ref _reason, reason, null);
        Record($"exit-request reason={reason} firstReason={_reason}");
    }

    public void RecordException(string source, object? exception, bool terminating) =>
        (error ?? write)($"lifecycle pid={Environment.ProcessId} session={_session} exception source={source} " +
            $"terminating={terminating} reason={_reason ?? "unrequested"} detail={exception}");

    public void ExitStarting(int code) => Record($"exit-start code={code} reason={_reason ?? "unrequested"}");
    public void ExitCompleted(int code)
    {
        Interlocked.Exchange(ref _completed, 1);
        Record($"exit-complete code={code} reason={_reason ?? "unrequested"}");
    }
    public void ProcessExiting(int code) =>
        Record($"process-exit code={code} reason={_reason ?? "unrequested"} completed={Volatile.Read(ref _completed) != 0}");
}
