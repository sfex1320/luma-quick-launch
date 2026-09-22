using System.Windows;
using Luma.Host.Bridge;
using Luma.Host.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Luma.Host.Windows;

/// <summary>
/// 管理工作台窗口：普通可激活窗口（接受键盘焦点），载入 /?mode=native&section=…；
/// 单例复用由 App 管理，重复 window.openSettings 只导航 section 不重复建窗。
/// </summary>
public sealed class SettingsWindow : Window, IHostClient
{
    public const string ClientIdValue = "settings";
    private readonly WebView2 _web;
    private readonly BridgeRouter _router;
    private readonly WindowTheme _theme;
    private readonly WebViewReliability _reliability;
    private IntPtr _hwnd;
    private string _section;

    public string ClientId => ClientIdValue;
    public IntPtr WindowHandle => _hwnd;
    public CoreWebView2? Core => _web.CoreWebView2;

    public SettingsWindow(BridgeRouter router, CoreWebView2Environment environment, StateStore store, string section, Action? showDock = null)
    {
        _router = router;
        _section = section;
        _web = null!; // 占位，构造下方赋值
        var initial = SectionUrl(section);
        Title = Environment.GetEnvironmentVariable("LUMA_TEST_SESSION") == "soak"
            ? "Luma 设置 · 后台测试副本（独立配置）" : "Luma 设置";
        Width = 1180;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        // Same topmost reason as the search window: the resident dock floats above normal windows.
        Topmost = true;

        var web = new WebView2();
        _web = web;
        _theme = new WindowTheme(this, web, store);
        _reliability = new WebViewReliability(web, "管理窗", message =>
        {
            Log.Error(message);
            MessageBox.Show(message, "Luma WebView2", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        });
        Content = web;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _ = _reliability.SuspendAsync();
            else _reliability.Resume();
        };
        web.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (!e.IsSuccess)
            {
                Log.Error($"管理窗 WebView2 初始化失败: {e.InitializationException?.Message}");
                return;
            }
            var core = web.CoreWebView2!;
            _reliability.Attach(core);
            if (WindowState == WindowState.Minimized) _ = _reliability.SuspendAsync();
            _theme.Refresh();
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("luma.local", App.DistDirectory, CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith("https://luma.local/", StringComparison.Ordinal))
                {
                    Log.Warn($"管理窗拒绝导航：{args.Uri}");
                    args.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                // 前端的「独立预览」在原生环境唤出真实浮岛，其余新窗口仍阻止。
                if (args.Uri == "https://luma.local/index.html?view=dock") showDock?.Invoke();
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) NavigateToSection(_section);
                if (WindowState == WindowState.Minimized) _ = _reliability.RetrySuspendAfterNavigationAsync();
            };
            core.WebMessageReceived += async (_, args) =>
            {
                try
                {
                    if (!(args.Source ?? "").StartsWith("https://luma.local/", StringComparison.Ordinal)) return;
                    var json = args.WebMessageAsJson;
                    if (!string.IsNullOrEmpty(json))
                        await router.HandleMessage(this, json, WebMessageFiles.ExtractPaths(args.AdditionalObjects));
                }
                catch (Exception ex) { Log.Error($"管理窗消息处理失败: {ex.Message}"); }
            };
            router.Attach(this);
            core.Navigate(initial);
            Log.Info($"管理窗 WebView2 就绪，加载 {initial}");
        };
        _ = web.EnsureCoreWebView2Async(environment);
    }

    public static string SectionUrl(string section) =>
        section is "appearance" or "search" or "shortcuts" ? $"https://luma.local/index.html?mode=native&section={section}" : "https://luma.local/index.html?mode=native&section=projects";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
    }

    /// <summary>已开窗口切换 section：直接导航（前端按 query 渲染对应面板）。</summary>
    public void NavigateToSection(string section)
    {
        _section = section;
        var core = _web.CoreWebView2;
        if (core is null) return;
        var url = SectionUrl(section);
        // 页面内切换，避免重载销毁编辑草稿和未落盘的防抖保存。
        var encodedUrl = System.Text.Json.JsonSerializer.Serialize(url);
        _ = core.ExecuteScriptAsync($"history.replaceState(null, '', {encodedUrl}); window.dispatchEvent(new PopStateEvent('popstate'));");
    }

    void IHostClient.PostJson(string json)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception ex) { Log.Warn($"管理窗回应失败: {ex.Message}"); }
        });
    }

    void IHostClient.Detach() => Log.Info("管理窗桥接客户端分离");

    protected override void OnClosed(EventArgs e)
    {
        _reliability.Dispose();
        _theme.Dispose();
        _router.Detach(this);
        // 释放该窗口的 WebView2 UI 资源；常驻部分（浮岛/存储）不受影响。
        _web.Dispose();
        base.OnClosed(e);
    }
}
