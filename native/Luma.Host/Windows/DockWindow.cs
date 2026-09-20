using System.Windows;
using System.Windows.Interop;
using Luma.Host.Bridge;
using Luma.Host.Interop;
using Luma.Host.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Rect = System.Windows.Rect;

namespace Luma.Host.Windows;

/// <summary>
/// 顶边浮岛承载窗口：默认整窗透明且完全穿透；前端 window.sync 上报真实面板矩形后，
/// 通过 SetWindowRgn 只在面板区域与连接通道内接收鼠标，其余区域点击落到原窗口。
/// WS_EX_NOACTIVATE + ShowActivated=false 保证展示不抢正在输入的应用焦点。
/// </summary>
public sealed class DockWindow : Window, IHostClient
{
    public const string ClientIdValue = "dock";
    private const string DockUrl = "https://luma.local/index.html?view=dock&mode=native";

    private readonly BridgeRouter _router;
    private readonly BackdropService _backdrop;
    private readonly WebView2 _web;
    private readonly WebViewReliability _reliability;
    private readonly LatestStateMessageBuffer _deferredState = new();
    private IntPtr _hwnd;
    private HwndSource? _source;
    private readonly DockMessageQueue _messages = new();
    private bool _expanded;
    private bool _visibleToUser;
    private bool _reloadingRenderer;
    private bool _activationRequested;
    private double _cssPixelScale;
    private IReadOnlyList<Rect> _rectsDip = Array.Empty<Rect>();

    public string ClientId => ClientIdValue;
    public IntPtr WindowHandle => _hwnd;
    public bool IsExpanded => _expanded;
    public bool IsInteracting { get; private set; }
    public IReadOnlyList<Rect> CurrentRects => _rectsDip;
    public bool HasSynced { get; private set; }
    public event Action? LayoutReady;
    public event Action? PixelScaleChanged;
    /// <summary>Renderer reload loses DOM visibility state; App must close the current native gesture before reload.</summary>
    public event Action? RendererReloading;

    public DockWindow(BridgeRouter router, BackdropService backdrop, CoreWebView2Environment environment, double screenWidthDip, double heightDip)
    {
        _router = router;
        _backdrop = backdrop;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        // 整个承载区透明（WPF 不绘制任何像素，视觉全部由 WebView2 透明渲染）；命中由 SetWindowRgn 控制。
        Background = null;
        AllowsTransparency = false; // 与 WebView2 的 HwndHost 不兼容，改用 DWM 扩展帧
        Title = "Luma Dock";
        Width = screenWidthDip;
        Height = heightDip;
        Left = 0;
        Top = 0;

        _web = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Content = _web;
        _reliability = new WebViewReliability(_web, "浮岛", message =>
        {
            Log.Error(message);
            MessageBox.Show(message, "Luma WebView2", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
        }, () =>
        {
            _reloadingRenderer = true;
            HasSynced = false;
            RendererReloading?.Invoke();
        });
        _web.CoreWebView2InitializationCompleted += OnCoreWebView2Ready;
        _ = _web.EnsureCoreWebView2Async(environment);
        // 立即创建原生句柄但不显示：WebView2 初始化、DWM 扩展帧与命中区域都依赖 HWND。
        new WindowInteropHelper(this).EnsureHandle();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        if (_source?.CompositionTarget is { } target)
            target.BackgroundColor = System.Windows.Media.Colors.Transparent;

        var exStyle = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE, exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST);

        // 玻璃面铺满客户区，配合 WebView2 透明背景实现每像素透明；窗口默认区域为空（完全穿透），等前端 sync。
        var margins = Win32.Margins.Sheet;
        var hr = Win32.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        if (hr != 0) Log.Warn($"DwmExtendFrameIntoClientArea hr=0x{hr:X8}，透明承载可能退化为黑底");
        _backdrop.DisableSystemBackdrop(_hwnd);
        _backdrop.ClearHitRegion(_hwnd);

        SourceInitializedSafe?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Source 就绪但尚未显示时的钩子（App 用于注册路由时序）。</summary>
    public event EventHandler? SourceInitializedSafe;

    private void OnCoreWebView2Ready(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Log.Error($"浮岛 WebView2 初始化失败: {e.InitializationException?.Message}");
            return;
        }
        var core = _web.CoreWebView2!;
        _reliability.Attach(core);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.SetVirtualHostNameToFolderMapping("luma.local", App.DistDirectory, CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += (_, args) =>
        {
            Log.Info($"浮岛导航完成 ok={args.IsSuccess} err={args.WebErrorStatus}");
            _reloadingRenderer = false;
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        _router.Attach(this);
        core.Navigate(DockUrl);
        Log.Info($"浮岛 WebView2 就绪，加载 {DockUrl}");
    }

    /// <summary>只允许可信虚拟域导航；阻止第三方来源、非 https 与 file://。</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.Uri.StartsWith("https://luma.local/", StringComparison.Ordinal))
        {
            Log.Warn($"浮岛拒绝导航：{e.Uri}");
            e.Cancel = true;
        }
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (_messages.IsClosed) return;
            var source = e.Source ?? "";
            if (!source.StartsWith("https://luma.local/", StringComparison.Ordinal))
            {
                Log.Warn($"浮岛拒绝非可信来源消息：{source}");
                return;
            }
            var json = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(json)) return;
            var paths = WebMessageFiles.ExtractPaths(e.AdditionalObjects);
            await _messages.DispatchAsync(json, async () =>
            {
                // RasterizationScale 包含系统文字缩放，CSS px 不等于 WPF DIP。
                // 仅在前端布局上报时读取，没有常驻轮询，也不扩展协议字段。
                var raw = await _web.ExecuteScriptAsync("window.devicePixelRatio");
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale)
                    && double.IsFinite(scale) && scale > 0 && scale != _cssPixelScale)
                {
                    _cssPixelScale = scale;
                    PixelScaleChanged?.Invoke();
                }
            }, () => _router.HandleMessage(this, json, paths));
        }
        catch (Exception ex) { Log.Error($"浮岛消息处理失败: {ex.Message}"); }
    }

    #region IHostClient

    void IHostClient.PostJson(string json)
    {
        if (_reliability.IsInactiveDesired && _deferredState.TryDefer(json))
        {
            Log.Info("浮岛暂停期间合并待同步状态");
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_messages.IsClosed) return;
            if (_reliability.IsInactiveDesired && _deferredState.TryDefer(json))
            {
                Log.Info("浮岛暂停期间合并待同步状态");
                return;
            }
            try { _web.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception ex) { Log.Warn($"浮岛回应失败: {ex.Message}"); }
        });
    }

    void IHostClient.Detach() => Log.Info("浮岛桥接客户端分离");

    #endregion

    /// <summary>全宽透明承载窗口绝不能启用整窗 DWM backdrop；材质暂由前端玻璃样式呈现。</summary>
    public void SetMaterial(string material)
    {
        if (_hwnd == IntPtr.Zero) return;
        _backdrop.DisableSystemBackdrop(_hwnd);
    }

    /// <summary>window.sync 应用：rects 为 WebView 客户区 CSS px，按 WebView 实际 devicePixelRatio 换算。
    /// 未唤出（用户不可见）时不应用命中区域，保证顶边热区不被面板区域遮挡。</summary>
    public void ApplySync(bool expanded, IReadOnlyList<Rect> rectsDip, bool interacting = false)
    {
        _expanded = expanded;
        IsInteracting = expanded && interacting;
        _rectsDip = rectsDip;
        RefreshRegion();
        Log.Info($"window.sync 应用 expanded={expanded} rects={rectsDip.Count} visibleToUser={_visibleToUser} cssScale={CssPixelScale:0.##}");
        if (!HasSynced) { HasSynced = true; LayoutReady?.Invoke(); }
        // Suspending before this first accepted sync can deadlock readiness: a suspended document
        // cannot send the layout that App requires before reveal.
        if (!_visibleToUser && !expanded && !_reloadingRenderer && !_activationRequested) _ = _reliability.SuspendAsync();
    }

    /// <summary>仅在用户唤出可见时按缓存 rects 应用命中区域，否则清空（整窗穿透）。</summary>
    private void RefreshRegion()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_visibleToUser && _expanded && _rectsDip.Count > 0)
            _backdrop.ApplyHitRegion(_hwnd, _rectsDip, StateRadius(), CssPixelScale);
        else
            _backdrop.ClearHitRegion(_hwnd);
    }

    private double StateRadius()
    {
        try { return _routerStateRadius?.Invoke() ?? 22; }
        catch { return 22; }
    }

    private Func<double>? _routerStateRadius;
    /// <summary>注入从当前状态读取圆角（DIP）的函数。</summary>
    public void SetRadiusProvider(Func<double> provider) => _routerStateRadius = provider;

    public double DpiScale
    {
        get
        {
            var source = PresentationSource.FromVisual(this);
            return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }
    }

    public double CssPixelScale => _cssPixelScale > 0 ? _cssPixelScale : DpiScale;

    /// <summary>启动预初始化：显示窗口使 WebView2 完成加载，但命中区域保持为空（视觉不可见、不拦热区）。</summary>
    public void PreInitialize()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(PreInitialize); return; }
        _reliability.Resume();
        FlushDeferredState();
        if (!IsVisible) Show();
        RefreshRegion();
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
    }

    /// <summary>显示浮岛（不激活）并恢复命中区域。</summary>
    public void ShowDock()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(ShowDock); return; }
        _reliability.Resume();
        FlushDeferredState();
        _visibleToUser = true;
        _activationRequested = false;
        if (!IsVisible) Show();
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0, Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
        RefreshRegion();
    }

    public void HideDock()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(HideDock); return; }
        _visibleToUser = false;
        _activationRequested = false;
        RefreshRegion();
        _expanded = false;
        IsInteracting = false;
        Hide();
        if (!_reloadingRenderer) _ = _reliability.SuspendAsync();
    }

    /// <summary>Wake the document before App checks whether a fresh layout has arrived.</summary>
    public void ResumeForActivation()
    {
        _activationRequested = true;
        _reliability.Resume();
        FlushDeferredState();
    }

    private void FlushDeferredState()
    {
        var core = _web.CoreWebView2;
        if (core is null) return;
        var json = _deferredState.TakeLatest();
        if (json is null) return;
        try
        {
            core.PostWebMessageAsJson(json);
            Log.Info("浮岛恢复后补发最新状态");
        }
        catch (Exception ex)
        {
            _deferredState.TryDefer(json);
            Log.Warn($"浮岛补发状态失败: {ex.Message}");
        }
    }

    // Consult the actual physical window region, not WindowFromPoint: Explorer's drag
    // image is a separate HWND that may cover the cursor without leaving the dock.
    public bool ContainsPhysicalPoint(int x, int y) =>
        _visibleToUser && _expanded && DockHitTest.Contains(_hwnd, x, y);

    public void RelocatePhysical(Rect workArea, double scale)
    {
        // Desktop origins remain physical: dividing each monitor origin by its own DPI
        // does not produce a consistent WPF virtual-desktop coordinate system.
        Width = workArea.Width / scale;
        Height = Math.Min(640, workArea.Height / scale);
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, (int)workArea.X, (int)workArea.Y,
            (int)workArea.Width, (int)Math.Min(640 * scale, workArea.Height), Win32.SWP_NOACTIVATE);
    }

    /// <summary>显示器变化后的重定位：主屏顶边居中，宽度覆盖工作区。</summary>
    public void Relocate(Rect primaryWorkAreaDip)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Relocate(primaryWorkAreaDip)); return; }
        Left = primaryWorkAreaDip.X;
        Top = primaryWorkAreaDip.Y;
        Width = primaryWorkAreaDip.Width;
        // 承载高度预留展开堆叠：至多 640 DIP，受工作区高度约束。
        Height = Math.Min(640, primaryWorkAreaDip.Height);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _messages.Close();
        _router.Detach(this);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _reliability.Dispose();
        _web.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>Folder/icon/launch IO owns bounded workers; keep state and layout preparation ordered on the UI context.</summary>
internal sealed class DockMessageQueue
{
    private readonly SemaphoreSlim _ordered = new(1, 1);
    private volatile bool _closed;
    public bool IsClosed => _closed;
    public void Close() => _closed = true;

    public async Task DispatchAsync(string json, Func<Task> prepareSync, Func<Task> route)
    {
        if (_closed) return;
        string? method = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("protocol", out var protocol) && protocol.ValueKind == System.Text.Json.JsonValueKind.Number && protocol.TryGetInt32(out var version) && version == 1 &&
                root.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String && type.GetString() == "request" &&
                root.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String &&
                root.TryGetProperty("method", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String)
                method = name.GetString();
        }
        catch (System.Text.Json.JsonException) { /* Router owns validation and error responses. */ }
        if (method is "folder.list" or "folder.open" or "folder.getThumbnail" or "folder.getPath" or "folder.createFolder" or "folder.rename" or "folder.move" or "shell.getIcon" or "shell.openItem" or "shell.getRecent" or "shell.openRecent" or "website.inspect" or "search.open" or "project.detectTest" or "project.runTest")
        {
            // Never hold the layout queue across slow directory, icon or window-reuse IO.
            // The router still validates the request; each service bounds its workers.
            if (!_closed) await route();
            return;
        }
        await _ordered.WaitAsync();
        try
        {
            if (_closed) return;
            if (method == "window.sync") await prepareSync();
            if (!_closed) await route();
        }
        finally { _ordered.Release(); }
    }
}
