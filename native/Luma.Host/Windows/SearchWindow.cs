using System.Windows;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Luma.Host.Windows;

/// <summary>Independent search client; never navigates or replaces the settings editor.</summary>
public sealed class SearchWindow : Window, IHostClient
{
    private readonly WebView2 _web;
    private readonly BridgeRouter _router;
    private readonly WindowTheme _theme;
    public string ClientId => "search";
    public SearchWindow(BridgeRouter router, CoreWebView2Environment environment, StateStore store)
    {
        _router = router;
        Title = "Luma 搜索";
        Width = 820; Height = 620; MinWidth = 480; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _web = new WebView2();
        _theme = new WindowTheme(this, _web, store);
        Content = _web;
        _web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) { Log.Error($"搜索窗初始化失败: {e.InitializationException?.Message}"); return; }
            var core = _web.CoreWebView2!;
            _theme.Refresh();
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("luma.local", App.DistDirectory, CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationStarting += (_, args) => args.Cancel = !args.Uri.StartsWith("https://luma.local/", StringComparison.Ordinal);
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.NavigationCompleted += (_, args) => { if (args.IsSuccess) FocusSearch(); else Log.Error($"搜索导航失败: {args.WebErrorStatus}"); };
            core.WebMessageReceived += async (_, args) =>
            {
                try
                {
                    if (!(args.Source ?? "").StartsWith("https://luma.local/", StringComparison.Ordinal)) return;
                    var json = args.WebMessageAsJson;
                    if (!string.IsNullOrEmpty(json))
                        await router.HandleMessage(this, json, WebMessageFiles.ExtractPaths(args.AdditionalObjects));
                }
                catch (Exception ex) { Log.Error($"搜索窗消息处理失败: {ex.Message}"); }
            };
            router.Attach(this);
            core.Navigate("https://luma.local/index.html?view=search&mode=native");
        };
        _ = _web.EnsureCoreWebView2Async(environment);
    }
    public void FocusSearch()
    {
        _web.Focus();
        if (_web.CoreWebView2 is { } core)
            _ = core.ExecuteScriptAsync("document.querySelector('input')?.focus()");
    }
    void IHostClient.PostJson(string json) => Dispatcher.BeginInvoke(() =>
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
        catch (Exception ex) { Log.Warn($"搜索窗回应失败: {ex.Message}"); }
    });
    void IHostClient.Detach() { }
    protected override void OnClosed(EventArgs e)
    {
        _theme.Dispose(); _router.Detach(this); _web.Dispose(); base.OnClosed(e);
    }
}
