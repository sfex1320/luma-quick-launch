using System.IO;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class WindowThemeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "luma-theme-" + Guid.NewGuid());
    private readonly StateStore _store;
    public WindowThemeTests() { _store = new StateStore(_directory); _store.Load(); }

    [Fact]
    public void BothOpenWindowsReceiveSavedDarkAndLightTheme()
    {
        SaveTheme("dark");
        var settings = new List<bool>();
        var search = new List<bool>();
        using var first = new ThemeSubscription(_store, action => action(), settings.Add);
        using var second = new ThemeSubscription(_store, action => action(), search.Add);
        SaveTheme("light");
        SaveTheme("light"); // Other preference saves must not repaint the frame.
        Assert.Equal(new[] { true, false }, settings);
        Assert.Equal(new[] { true, false }, search);
    }

    [Fact]
    public void DelayedDispatchReadsLatestSavedTheme()
    {
        var queue = new Queue<Action>();
        var observed = new List<bool>();
        using var subscription = new ThemeSubscription(_store, queue.Enqueue, observed.Add);
        SaveTheme("dark");
        SaveTheme("light");
        while (queue.TryDequeue(out var action)) action();
        Assert.Equal(new[] { false }, observed);
    }

    [Fact]
    public void ClosingWindowUnsubscribesAndCancelsAlreadyQueuedUpdate()
    {
        var queue = new Queue<Action>();
        var observed = new List<bool>();
        var subscription = new ThemeSubscription(_store, queue.Enqueue, observed.Add);
        SaveTheme("dark");
        subscription.Dispose();
        while (queue.TryDequeue(out var action)) action();
        SaveTheme("light");
        Assert.Empty(queue);
        Assert.Equal(new[] { false }, observed);
    }

    [Fact]
    public void FailedSaveDoesNotApplyUnsavedTheme()
    {
        var observed = new List<bool>();
        using var subscription = new ThemeSubscription(_store, action => action(), observed.Add);
        var state = StateValidator.EmptyState();
        state.Preferences.Theme = "dark";
        state.Revision = 1;
        Assert.Equal(SaveOutcome.RevisionConflict, _store.Save(state, 1).Outcome);
        Assert.Equal(new[] { false }, observed);
    }

    private void SaveTheme(string theme)
    {
        var state = StateValidator.EmptyState();
        state.Revision = _store.Current.Revision;
        state.Preferences.Theme = theme;
        Assert.Equal(SaveOutcome.Saved, _store.Save(state, state.Revision).Outcome);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
