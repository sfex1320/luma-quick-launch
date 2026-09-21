namespace Luma.Host.Services;

internal sealed record ReuseTarget(string Key, string? Folder = null, string? Executable = null);
internal interface IWindowReusePlatform
{
    ReuseTarget Identify(string path);
    nint Find(ReuseTarget target);
    bool Activate(nint window);
    bool Activate(nint window, Func<bool> stillAuthorized) => stillAuthorized() && Activate(window);
    string? Launch(string path);
}

internal sealed class WindowReuseService(IWindowReusePlatform platform, Func<DateTimeOffset>? clock = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _starting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    // Serialized by the STA worker in production; the lock also protects callers in tests.
    public string? Open(string path, CancellationToken cancellation = default, Func<bool>? stillAuthorized = null)
    {
        lock (_gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (stillAuthorized?.Invoke() == false) return "入口或快捷键已变更，请重新操作。";
            var target = platform.Identify(path);
            cancellation.ThrowIfCancellationRequested();
            if (stillAuthorized?.Invoke() == false) return "入口或快捷键已变更，请重新操作。";
            var window = platform.Find(target);
            cancellation.ThrowIfCancellationRequested();
            if (stillAuthorized?.Invoke() == false) return "入口或快捷键已变更，请重新操作。";
            if (window != 0)
            {
                _starting.Remove(target.Key);
                // A foreground lock must never turn a known existing window into another launch.
                return platform.Activate(window, () => !cancellation.IsCancellationRequested && stillAuthorized?.Invoke() != false)
                    ? null : "目标窗口已打开，但 Windows 暂未允许切到前台或操作已取消，请点击任务栏中的窗口。";
            }
            var now = _clock();
            foreach (var key in _starting.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _starting.Remove(key);
            if (_starting.ContainsKey(target.Key)) return null;
            if (_starting.Count >= 256) return "启动请求较多，请稍后重试。";
            cancellation.ThrowIfCancellationRequested();
            if (stillAuthorized?.Invoke() == false) return "入口或快捷键已变更，请重新操作。";
            var error = platform.Launch(path);
            if (error is null) _starting[target.Key] = _clock().AddSeconds(15);
            return error;
        }
    }
}
