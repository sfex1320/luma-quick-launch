using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Luma.Host.Windows;

public enum WebViewFailureAction { Ignore, Reload, Stop }

public sealed class WebViewActivityState
{
    public bool ActiveDesired { get; private set; } = true;
    public void RequestActive() => ActiveDesired = true;
    public void RequestInactive() => ActiveDesired = false;
    public bool ShouldResumeAfterSuspend() => ActiveDesired;
}

public sealed class WebViewReliabilityPolicy
{
    private readonly int _maxReloads;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _reloads = new();

    public WebViewReliabilityPolicy(int maxReloads = 2, TimeSpan? window = null)
    {
        _maxReloads = maxReloads;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    public WebViewFailureAction Decide(CoreWebView2ProcessFailedKind kind, DateTimeOffset now)
    {
        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited) return WebViewFailureAction.Stop;
        if (kind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive or
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited)) return WebViewFailureAction.Ignore;

        while (_reloads.Count > 0 && now - _reloads.Peek() >= _window) _reloads.Dequeue();
        if (_reloads.Count >= _maxReloads) return WebViewFailureAction.Stop;
        _reloads.Enqueue(now);
        return WebViewFailureAction.Reload;
    }
}

/// <summary>Bounds renderer recovery and releases rendering resources while a WebView window is inactive.</summary>
public sealed class WebViewReliability : IDisposable
{
    private readonly WebView2 _web;
    private readonly string _name;
    private readonly Action<string> _fatal;
    private readonly Action? _beforeReload;
    private readonly WebViewReliabilityPolicy _policy = new();
    private CoreWebView2? _core;
    private bool _suspendPending;
    private bool _disposed;
    private readonly WebViewActivityState _activity = new();

    public WebViewReliability(WebView2 web, string name, Action<string> fatal, Action? beforeReload = null)
    {
        _web = web;
        _name = name;
        _fatal = fatal;
        _beforeReload = beforeReload;
    }

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        core.ProcessFailed += OnProcessFailed;
    }

    public async Task SuspendAsync()
    {
        _activity.RequestInactive();
        if (_disposed || _core is null) return;
        // WebView2 only suspends an inactive controller. WPF minimization/empty HWND regions do not
        // reliably update CoreWebView2Controller.IsVisible, so remove the child visual explicitly.
        _web.Visibility = Visibility.Collapsed;
        if (_suspendPending) return;
        try { if (_core.IsSuspended) return; }
        catch (Exception ex) { Log.Warn($"{_name} WebView 读取暂停状态失败: {ex.Message}"); return; }
        _suspendPending = true;
        try
        {
            var suspended = await _core.TrySuspendAsync();
            if (suspended && _activity.ShouldResumeAfterSuspend()) Resume();
            else if (suspended) Log.Info($"{_name} WebView 已暂停");
            else Log.Info($"{_name} WebView 暂停请求未获 runtime 接受，已折叠渲染控件");
        }
        catch (Exception ex) { Log.Warn($"{_name} WebView 暂停失败: {ex.Message}"); }
        finally { _suspendPending = false; }
    }

    public async Task RetrySuspendAfterNavigationAsync()
    {
        if (_activity.ActiveDesired) return;
        await Task.Delay(250);
        if (!_activity.ActiveDesired && !_disposed) await SuspendAsync();
    }

    public void Resume()
    {
        _activity.RequestActive();
        if (_disposed || _core is null) return;
        try
        {
            if (!_core.IsSuspended)
            {
                _web.Visibility = Visibility.Visible;
                return;
            }
            _core.Resume();
            _web.Visibility = Visibility.Visible;
            Log.Info($"{_name} WebView 已恢复");
        }
        catch (Exception ex) { Stop($"恢复失败: {ex.Message}"); }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        var action = _policy.Decide(e.ProcessFailedKind, DateTimeOffset.UtcNow);
        Log.Warn($"{_name} WebView 进程故障 kind={e.ProcessFailedKind} action={action}");
        if (action == WebViewFailureAction.Ignore) return;
        if (action == WebViewFailureAction.Stop) { Stop($"进程故障 {e.ProcessFailedKind}"); return; }
        try { _beforeReload?.Invoke(); _core?.Reload(); }
        catch (Exception ex) { Stop($"重载失败: {ex.Message}"); }
    }

    private void Stop(string detail)
    {
        if (_disposed) return;
        _fatal($"{_name} WebView 无法安全自动恢复（{detail}）。请重启 Luma；已保存的配置不会被删除。");
    }

    public void Dispose()
    {
        _disposed = true;
        if (_core is not null) _core.ProcessFailed -= OnProcessFailed;
        _core = null;
    }
}
