using System.IO;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class SearchServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "luma-search-tests", Guid.NewGuid().ToString("N"));
    private readonly StateStore _store;
    private readonly FakeShell _shell = new();
    private readonly FakeProbe _probe = new();
    public SearchServiceTests() { _store = new StateStore(_directory); _store.Load(); }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private void Save(string path) { var state = TestStates.OneProject("p", path); state.Revision = _store.Current.Revision; Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome); }

    [Fact]
    public async Task SavedCapabilityIsIsolatedExpiresAndResolvesLatestSavedPath()
    {
        Save(@"D:\old");
        var now = DateTimeOffset.UtcNow;
        var service = new SearchService(_store, new Provider(), _shell, _probe, () => now);
        var result = Assert.Single((await service.QueryAsync("a", "", "shortcuts")).Results);
        Assert.False(service.Open("b", "r", result.Id).Accepted);
        Assert.False(service.Open("a", "unknown", "guessed").Accepted);
        Save(@"D:\new"); _probe.Existing.Add(@"D:\new");
        Assert.True(service.Open("a", "open", result.Id).Accepted);
        Assert.True(service.Open("a", "open", result.Id).Accepted);
        Assert.Equal(@"D:\new", Assert.Single(_shell.Launched));
        now = now.AddMinutes(6);
        Assert.False(service.Open("a", "expired", result.Id).Accepted);
    }

    [Fact]
    public async Task UnavailableIndexStillReturnsShortcutsAndChineseSettings()
    {
        Save(@"D:\old");
        var service = new SearchService(_store, new Provider { Fail = true });
        var results = await service.QueryAsync("a", "", "all");
        Assert.Contains(results.Results, r => r.Source == "shortcut");
        Assert.Contains(results.Results, r => r.Source == "settings");
        results = await service.QueryAsync("a", "显示", "all");
        Assert.False(results.IndexAvailable);
        Assert.Contains("不可用", results.Note);
        Assert.Contains(results.Results, r => r.Kind == "setting");
    }

    [Fact]
    public async Task SettingsAreNativeWhitelistAndDeduplicateRequests()
    {
        var service = new SearchService(_store, new Provider(), _shell, _probe);
        var result = Assert.Single((await service.QueryAsync("a", "蓝牙", "settings")).Results);
        Assert.True(service.Open("a", "one", result.Id).Accepted);
        Assert.True(service.Open("a", "one", result.Id).Accepted);
        Assert.Equal("ms-settings:bluetooth", Assert.Single(_shell.Launched));
    }

    [Fact]
    public async Task IndexPathsAreValidatedAgainAtOpen()
    {
        var provider = new Provider { Items = new[] { new IndexedSearchItem("notes", @"D:\notes.txt", "file"), new IndexedSearchItem("bad", "https://evil.test", "file") } };
        var service = new SearchService(_store, provider, _shell, _probe);
        var result = Assert.Single((await service.QueryAsync("a", "notes", "files")).Results);
        Assert.False(service.Open("a", "missing", result.Id).Accepted);
        _probe.Existing.Add(@"D:\notes.txt");
        Assert.True(service.Open("a", "valid", result.Id).Accepted);
    }

    [Fact]
    public async Task TimeoutDoesNotAccumulateProviderWorkers()
    {
        var provider = new Provider { Block = new TaskCompletionSource<IReadOnlyList<IndexedSearchItem>>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new SearchService(_store, provider, queryTimeout: TimeSpan.FromMilliseconds(30));
        var first = await service.QueryAsync("a", "notes", "all");
        Assert.False(first.IndexAvailable);
        for (var i = 0; i < 10; i++) Assert.False((await service.QueryAsync("a", "notes", "all")).IndexAvailable);
        Assert.Equal(1, provider.Calls);
        provider.Block.SetResult(Array.Empty<IndexedSearchItem>());
    }

    [Fact]
    public async Task FinalQueryWaitsForActiveProviderAndReturnsItsOwnResults()
    {
        var provider = new FirstQueryBlockedProvider();
        var service = new SearchService(_store, provider, queryTimeout: TimeSpan.FromSeconds(2));
        var first = service.QueryAsync("a", "first", "files");
        await provider.Entered.Task;
        var final = service.QueryAsync("a", "final", "files");
        provider.Release.SetResult();
        await first;
        var response = await final;
        Assert.True(response.IndexAvailable);
        Assert.Equal("final", Assert.Single(response.Results).Title);
        Assert.Equal(new[] { "first", "final" }, provider.Calls);
    }

    [Fact]
    public async Task IntermediatePendingQueryIsReplacedByLatestQuery()
    {
        var provider = new FirstQueryBlockedProvider();
        var service = new SearchService(_store, provider, queryTimeout: TimeSpan.FromSeconds(2));
        var first = service.QueryAsync("a", "first", "files");
        await provider.Entered.Task;
        var intermediate = service.QueryAsync("a", "intermediate", "files");
        var final = service.QueryAsync("a", "final", "files");
        var replaced = await intermediate.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(replaced.IndexAvailable);
        Assert.Single(provider.Calls);
        provider.Release.SetResult();
        await first;
        var response = await final;
        Assert.True(response.IndexAvailable);
        Assert.Equal("final", Assert.Single(response.Results).Title);
        Assert.Equal(new[] { "first", "final" }, provider.Calls);
    }

    private sealed class FirstQueryBlockedProvider : IWindowsSearchProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = new();
        public async Task<IReadOnlyList<IndexedSearchItem>> QueryAsync(string query, string scope, CancellationToken token)
        {
            Calls.Add(query);
            if (Calls.Count == 1) { Entered.SetResult(); await Release.Task; }
            return new[] { new IndexedSearchItem(query, $@"D:\{query}.txt", "file") };
        }
    }

    [Fact]
    public void SqlTreatsApostropheAndOperatorsAsLiteralText()
    {
        var sql = WindowsSearchProvider.BuildSql("a' OR 1=1 -- %_*[]\"", "content");
        Assert.Contains("FREETEXT(System.Search.Contents, 'a'' OR 1=1 -- %_*[]\"')", sql);
        Assert.DoesNotContain("CONTAINS", sql);
        Assert.Throws<ArgumentException>(() => WindowsSearchProvider.BuildSql(new string('x', 201), "files"));
        Assert.Contains("CONTAINS(System.FileName, '\"a*\" AND \"OR*\" AND \"1*\" AND \"1*\"')", WindowsSearchProvider.BuildSql("a' OR 1=1 -- %_*[]\"", "files"));
        Assert.Contains("LIKE '%[%][_]*[[][]]%'", WindowsSearchProvider.BuildSql("%_*[]", "files"));
    }

    [Fact]
    public async Task ResultsAndCapabilitiesHaveHardBounds()
    {
        var provider = new Provider { Items = Enumerable.Range(0, 100).Select(i => new IndexedSearchItem($"item {i}", $@"D:\item{i}.txt", "file")).ToArray() };
        var service = new SearchService(_store, provider, _shell, _probe);
        var first = await service.QueryAsync("a", "item", "files");
        Assert.Equal(60, first.Results.Count);
        for (var i = 0; i < 18; i++) await service.QueryAsync("a", "item", "files");
        _probe.Existing.Add(@"D:\item0.txt");
        Assert.False(service.Open("a", "old", first.Results[0].Id).Accepted);
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync("a", new string('x', 201), "all"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueryAsync("a", "item", "injected"));
    }

    [Fact]
    public async Task RemovedSavedEntryDoesNotLaunchItsFormerPath()
    {
        Save(@"D:\old");
        var service = new SearchService(_store, new Provider(), _shell, _probe);
        var result = Assert.Single((await service.QueryAsync("a", "", "shortcuts")).Results);
        var empty = Luma.Host.Bridge.StateValidator.EmptyState();
        empty.Revision = _store.Current.Revision;
        Assert.Equal(SaveOutcome.Saved, _store.Save(empty, empty.Revision).Outcome);
        _probe.Existing.Add(@"D:\old");
        Assert.False(service.Open("a", "removed", result.Id).Accepted);
        Assert.Empty(_shell.Launched);
    }

    private sealed class Provider : IWindowsSearchProvider
    {
        public bool Fail; public int Calls;
        public IReadOnlyList<IndexedSearchItem> Items = Array.Empty<IndexedSearchItem>();
        public TaskCompletionSource<IReadOnlyList<IndexedSearchItem>>? Block;
        public Task<IReadOnlyList<IndexedSearchItem>> QueryAsync(string query, string scope, CancellationToken token)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("offline");
            return Block?.Task ?? Task.FromResult(Items);
        }
    }
}
