using Luma.Host.Bridge;

namespace Luma.Host.Services;

internal sealed class ThemeSubscription : IDisposable
{
    private readonly StateStore _store;
    private readonly Action<Action> _post;
    private readonly Action<bool> _apply;
    private bool? _lastDark;
    private bool _disposed;

    // Construct and dispose on the window's UI thread; state saves may arrive elsewhere.
    public ThemeSubscription(StateStore store, Action<Action> post, Action<bool> apply)
    {
        _store = store;
        _post = post;
        _apply = apply;
        _store.StateChanged += OnStateChanged;
        Refresh();
    }

    private void OnStateChanged(AppState state) => _post(() => Refresh());

    public void Refresh(bool force = false)
    {
        if (_disposed) return;
        // Read at dispatch time so delayed notifications cannot restore an older theme.
        var dark = _store.Current.Preferences.Theme == "dark";
        if (!force && _lastDark == dark) return;
        _apply(dark);
        _lastDark = dark;
    }

    public void Dispose()
    {
        _disposed = true;
        _store.StateChanged -= OnStateChanged;
    }
}
