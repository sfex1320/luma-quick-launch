using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public class InstanceShutdownSignalTests
{
    [Fact]
    public void MissingCopyIsNoActionAndDoesNotCreateAnEvent()
    {
        var path = $@"C:\Luma.Tests\{Guid.NewGuid():N}\Luma.exe";
        Assert.False(InstanceShutdownSignal.TryRequest(path));
        Assert.False(InstanceShutdownSignal.TryRequest(path));
    }

    [Fact]
    public void RequestsOnlyWakeTheMatchingExecutableAndPathCaseIsNormalized()
    {
        var root = $@"C:\Luma.Tests\{Guid.NewGuid():N}";
        using var first = new InstanceShutdownSignal(root + @"\one\Luma.exe");
        using var other = new InstanceShutdownSignal(root + @"\two\Luma.exe");
        Assert.True(InstanceShutdownSignal.TryRequest(root.ToLowerInvariant() + @"\ONE\LUMA.EXE"));
        Assert.True(first.Handle.WaitOne(0));
        Assert.False(other.Handle.WaitOne(0));
        Assert.False(first.Handle.WaitOne(0));
    }

    [Fact]
    public void DisposalRemovesOwnedSignal()
    {
        var path = $@"C:\Luma.Tests\{Guid.NewGuid():N}\Luma.exe";
        using (var signal = new InstanceShutdownSignal(path)) Assert.True(InstanceShutdownSignal.TryRequest(path));
        Assert.False(InstanceShutdownSignal.TryRequest(path));
    }
}
