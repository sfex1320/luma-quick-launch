using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class HostLifecycleDiagnosticsTests
{
    [Fact]
    public void ExitReasonIsRecordedBeforeCleanupAndPreservedThroughFailure()
    {
        var messages = new List<string>();
        var diagnostics = new HostLifecycleDiagnostics(messages.Add);
        diagnostics.RequestExit("tray-exit");
        diagnostics.ExitStarting(0);
        diagnostics.RecordException("exit-cleanup", new InvalidOperationException("cleanup failed"), true);
        diagnostics.ProcessExiting(1);
        Assert.Contains(messages, x => x.Contains("exit-request") && x.Contains("tray-exit"));
        Assert.Contains(messages, x => x.Contains("exit-start") && x.Contains("tray-exit"));
        Assert.Contains(messages, x => x.Contains("InvalidOperationException") && x.Contains("cleanup failed"));
        Assert.Contains(messages, x => x.Contains("process-exit") && x.Contains("completed=False"));
    }

    [Fact]
    public void UnrequestedExitIsDistinctFromRequestedAndCompletedExit()
    {
        var messages = new List<string>();
        var diagnostics = new HostLifecycleDiagnostics(messages.Add);
        diagnostics.ExitStarting(0);
        diagnostics.ExitCompleted(0);
        diagnostics.ProcessExiting(0);
        Assert.Contains(messages, x => x.Contains("reason=unrequested"));
        Assert.Contains(messages, x => x.Contains("process-exit") && x.Contains("completed=True"));
    }

    [Fact]
    public void FirstRequestIsNotOverwrittenByDuplicateShutdown()
    {
        var messages = new List<string>();
        var diagnostics = new HostLifecycleDiagnostics(messages.Add);
        diagnostics.RequestExit("update-apply");
        diagnostics.RequestExit("instance-shutdown");
        diagnostics.ExitStarting(0);
        Assert.Contains("reason=update-apply", messages[^1]);
    }
}
