using Luma.Host.Windows;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace Luma.Host.Tests;

public sealed class WebViewReliabilityPolicyTests
{
    [Fact]
    public void Render_failures_are_reloaded_only_twice_inside_window()
    {
        var policy = new WebViewReliabilityPolicy(2, TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.Parse("2026-09-20T10:00:00Z");

        Assert.Equal(WebViewFailureAction.Reload, policy.Decide(CoreWebView2ProcessFailedKind.RenderProcessExited, now));
        Assert.Equal(WebViewFailureAction.Reload, policy.Decide(CoreWebView2ProcessFailedKind.RenderProcessExited, now.AddSeconds(10)));
        Assert.Equal(WebViewFailureAction.Stop, policy.Decide(CoreWebView2ProcessFailedKind.RenderProcessExited, now.AddSeconds(20)));
        Assert.Equal(WebViewFailureAction.Reload, policy.Decide(CoreWebView2ProcessFailedKind.RenderProcessExited, now.AddMinutes(2)));
    }

    [Fact]
    public void Browser_exit_is_never_reloaded_on_dead_control()
    {
        var policy = new WebViewReliabilityPolicy(2, TimeSpan.FromMinutes(1));

        Assert.Equal(WebViewFailureAction.Stop,
            policy.Decide(CoreWebView2ProcessFailedKind.BrowserProcessExited, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Auxiliary_process_exit_is_logged_without_page_reload()
    {
        var policy = new WebViewReliabilityPolicy(2, TimeSpan.FromMinutes(1));

        Assert.Equal(WebViewFailureAction.Ignore,
            policy.Decide(CoreWebView2ProcessFailedKind.UtilityProcessExited, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Latest_activity_request_wins_while_suspend_is_pending()
    {
        var state = new WebViewActivityState();
        state.RequestInactive();
        state.RequestActive();
        state.RequestInactive();

        Assert.False(state.ActiveDesired);
        Assert.False(state.ShouldResumeAfterSuspend());
    }

    [Fact]
    public void Active_request_during_suspend_requires_immediate_resume()
    {
        var state = new WebViewActivityState();
        state.RequestInactive();
        state.RequestActive();

        Assert.True(state.ShouldResumeAfterSuspend());
    }
}
