using System.Runtime.InteropServices;
using Luma.Host.Services;
using Xunit;
using Xunit.Abstractions;

namespace Luma.Host.Tests;

public sealed class WindowsSearchProviderTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("files")]
    [InlineData("content")]
    [InlineData("all")]
    public async Task ActualWindowsIndexReadOnlyProbe(string scope)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var provider = new WindowsSearchProvider();
            var results = await provider.QueryAsync("Windows", scope, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(6));
            output.WriteLine($"Windows Search {scope} query available: {results.Count} results (paths omitted).");
            Assert.InRange(results.Count, 0, 60);
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or OperationCanceledException)
        {
            output.WriteLine($"Windows Search {scope} unavailable in this environment: {ex.GetType().Name}: {ex.Message}");
            if (ex is COMException com)
            {
                Assert.NotEqual(unchecked((int)0x80041601), com.HResult); // invalid query
                Assert.NotEqual(unchecked((int)0x80041602), com.HResult); // invalid restriction
                Assert.NotEqual(unchecked((int)0x80041609), com.HResult); // invalid output column
            }
            // Availability is environment-dependent; the service fallback has separate deterministic tests.
        }
    }
}
