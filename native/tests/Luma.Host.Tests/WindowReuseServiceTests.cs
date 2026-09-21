using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class WindowReuseServiceTests
{
    [Theory]
    [InlineData(@"C:\Apps\Editor.exe", "", true)]
    [InlineData(@"C:\Apps\Editor.exe", @"--project C:\Project", false)]
    [InlineData(@"C:\Apps\Editor.exe", @"C:\Document.txt", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", "", false)]
    [InlineData(@"C:\Runtime\python.exe", "", false)]
    [InlineData(@"C:\Runtime\node.exe", "", false)]
    [InlineData(@"C:\Runtime\dotnet.exe", "", false)]
    [InlineData(@"C:\Windows\explorer.exe", "", false)]
    [InlineData(@"C:\Apps\document.txt", "", false)]
    [InlineData("Editor.exe", "", false)]
    public void ExecutableIdentityNeverDropsArgumentsOrMatchesSharedHosts(string path, string arguments, bool expected) =>
        Assert.Equal(expected, WindowsWindowReusePlatform.CanMatchExecutable(path, arguments));

    [Fact]
    public void FolderNormalizationPreservesRootAndRemovesOnlyRedundantSegments()
    {
        Assert.Equal(@"C:\", WindowsWindowReusePlatform.Normalize(@"C:\"));
        Assert.Equal(@"C:\Folder", WindowsWindowReusePlatform.Normalize(@"C:\Other\..\Folder\"));
        Assert.NotEqual(WindowsWindowReusePlatform.Normalize(@"C:\one\same"), WindowsWindowReusePlatform.Normalize(@"C:\two\same"));
    }

    private sealed class Platform : IWindowReusePlatform
    {
        public nint Window;
        public bool CanActivate = true;
        public int Launches, Activations;
        public string? Error;
        public Action? OnFind;
        public ReuseTarget Identify(string path) => new(path.ToUpperInvariant());
        public nint Find(ReuseTarget target) { OnFind?.Invoke(); return Window; }
        public bool Activate(nint window) { Activations++; return CanActivate; }
        public string? Launch(string path) { Launches++; return Error; }
    }

    [Fact]
    public void ExistingWindowIsActivatedWithoutLaunching()
    {
        var os = new Platform { Window = 123 };
        Assert.Null(new WindowReuseService(os).Open(@"C:\App.exe"));
        Assert.Equal(1, os.Activations);
        Assert.Equal(0, os.Launches);
    }

    [Fact]
    public void RepeatedClicksShareStartupLeaseThenActivateNewWindow()
    {
        var os = new Platform();
        var service = new WindowReuseService(os);
        Assert.Null(service.Open(@"C:\App.exe"));
        Assert.Null(service.Open(@"c:\app.EXE"));
        Assert.Equal(1, os.Launches);
        os.Window = 123;
        Assert.Null(service.Open(@"C:\App.exe"));
        Assert.Equal(1, os.Launches);
        Assert.Equal(1, os.Activations);
    }

    [Fact]
    public void FailedActivationNeverCreatesDuplicateWindow()
    {
        var os = new Platform { Window = 123, CanActivate = false };
        Assert.NotNull(new WindowReuseService(os).Open(@"C:\App.exe"));
        Assert.Equal(0, os.Launches);
    }

    [Fact]
    public void FailedLaunchCanRetryImmediately()
    {
        var os = new Platform { Error = "failed" };
        var service = new WindowReuseService(os);
        Assert.NotNull(service.Open(@"C:\App.exe"));
        os.Error = null;
        Assert.Null(service.Open(@"C:\App.exe"));
        Assert.Equal(2, os.Launches);
    }

    [Fact]
    public void ExpiredLeaseAllowsExplicitRetryWithoutIdlePolling()
    {
        var now = DateTimeOffset.UtcNow;
        var os = new Platform();
        var service = new WindowReuseService(os, () => now);
        service.Open(@"C:\App.exe");
        now = now.AddSeconds(16);
        service.Open(@"C:\App.exe");
        Assert.Equal(2, os.Launches);
    }

    [Fact]
    public void TimedOutLookupCannotLaunchLater()
    {
        using var cancellation = new CancellationTokenSource();
        var os = new Platform { OnFind = cancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => new WindowReuseService(os).Open(@"C:\App.exe", cancellation.Token));
        Assert.Equal(0, os.Launches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123)]
    public void AuthorizationRevokedDuringFindPreventsLaunchAndActivation(int found)
    {
        var authorized = true;
        var os = new Platform { Window = found, OnFind = () => authorized = false };
        Assert.NotNull(new WindowReuseService(os).Open(@"C:\App.exe", stillAuthorized: () => authorized));
        Assert.Equal(0, os.Launches);
        Assert.Equal(0, os.Activations);
    }

    [Fact]
    public async Task StaQueuedRequestRevokedBeforeWorkerRunsCannotLaunch()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = new Platform { OnFind = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); } };
        var blockedShell = new RealShellExecutor(blocked);
        var first = Task.Run(() => blockedShell.TryLaunch(@"C:\Busy.exe"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
        var os = new Platform(); var shell = new RealShellExecutor(os); var authorized = true; string? outcome = null;
        var second = new Thread(() => outcome = shell.TryLaunch(@"C:\App.exe", () => Volatile.Read(ref authorized))) { IsBackground = true };
        try
        {
            second.Start();
            Assert.True(SpinWait.SpinUntil(() => (second.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(1)));
            Volatile.Write(ref authorized, false);
        }
        finally { release.Set(); second.Join(3000); await first; }
        Assert.NotNull(outcome);
        Assert.Equal(0, os.Launches);
        Assert.Equal(0, os.Activations);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneTargetLaunchOnlyOnce()
    {
        var os = new Platform();
        var service = new WindowReuseService(os);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => service.Open(@"C:\App.exe"))));
        Assert.Equal(1, os.Launches);
    }
}
