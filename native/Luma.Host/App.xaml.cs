using System.IO;
using System.Windows;
using System.Windows.Controls;
using Luma.Host.Bridge;
using Luma.Host.Interop;
using Luma.Host.Services;
using Luma.Host.Windows;
using Microsoft.Web.WebView2.Core;
using Rect = System.Windows.Rect;

namespace Luma.Host;

/// <summary>
/// 组合根：单实例互斥 + 激活信号、服务装配（存储/桥接/启动/热区/显示器/材质）、托盘、生命周期。
/// 两种窗口共用同一 StateStore 与 BridgeRouter。
/// </summary>
public partial class App : Application, IWindowHost
{
    private const string SingleInstanceMutexName = "Luma.ProjectDock.SingleInstance";
    private const string ActivateEventName = "Luma.ProjectDock.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateSignal;
    private EventWaitHandle? _settingsSignal;
    private EventWaitHandle? _searchSignal;
    private readonly DockVisibilityState _dockVisibility = new();
    private readonly System.Windows.Threading.DispatcherTimer _dockCloseTimer = new()
    { Interval = TimeSpan.FromMilliseconds(DockVisibilityState.CloseTimeoutMilliseconds) };
    private System.Windows.Interop.HwndSource? _hotkeySource;
    private SearchWindow? _search;
    private readonly ManualResetEvent _stopListener = new(false);
    private bool _pendingDockShow;
    private Thread? _activateListener;

    private MonitorService? _monitors;
    private StateStore? _store;
    private LaunchService? _launcher;
    private BridgeRouter? _router;
    private BackdropService? _backdrop;
    private EdgeActivation? _edge;
    private DockWindow? _dock;
    private SettingsWindow? _settings;
    private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon? _tray;
    private CoreWebView2Environment? _environment;
    private string[] _args = Array.Empty<string>();

    // 用于隔离原生集成测试/便携配置；默认路径与用户现有安装完全一致。
    public static string DataDirectory { get; } = Environment.GetEnvironmentVariable("LUMA_DATA_DIRECTORY") is { Length: > 0 } data
        ? Path.GetFullPath(data)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StateStore.AppFolderName);
    private static string InstanceSuffix => "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..16];

    /// <summary>前端 dist 定位：优先 exe 同级（发布形态），其次从运行目录向上找仓库 dist（开发形态）。</summary>
    public static string DistDirectory { get; } = ResolveDistDirectory();

    IntPtr IWindowHost.SettingsOwnerHandle => _settings?.WindowHandle ?? IntPtr.Zero;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _args = e.Args;
        Log.Init(DataDirectory);
        _singleInstance = new Mutex(true, SingleInstanceMutexName + InstanceSuffix, out var isFirst);
        if (!isFirst)
        {
            Log.Info("已有 Luma 实例运行，发送激活信号后退出");
            try
            {
                var signalName = ActivateEventName + InstanceSuffix + (_args.Contains("--search") ? ".Search" : _args.Contains("--settings") ? ".Settings" : "");
                if (EventWaitHandle.TryOpenExisting(signalName, out var existing)) { existing.Set(); existing.Dispose(); }
            }
            catch { /* 已有实例退出中的竞态可忽略 */ }
            Shutdown();
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + InstanceSuffix);
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + InstanceSuffix + ".Settings");
        _searchSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + InstanceSuffix + ".Search");
        _activateListener = new Thread(ListenActivation) { IsBackground = true, Name = "LumaActivateListener" };
        _activateListener.Start();

        _monitors = new MonitorService();
        _store = new StateStore(DataDirectory);
        var loadResult = _store.Load();
        if (loadResult.Outcome == StoreOutcome.Corrupted)
        {
            Log.Error($"启动加载配置失败：{loadResult.Message}");
            MessageBox.Show(loadResult.Message, "Luma 配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _launcher = new LaunchService(_store);
        _backdrop = new BackdropService();
        _router = new BridgeRouter(_store, _launcher, new FileDialogFolderPicker(), this, new DispatcherSyncContext(Dispatcher));

        _monitors.DisplaysChanged += OnDisplaysChanged;
        _store.StateChanged += state =>
        {
            // 外观变化（宽度/材质/自动隐藏）影响热区、backdrop 与收起行为，在 UI 线程应用；面板布局由前端 sync 驱动。
            Dispatcher.BeginInvoke(() =>
            {
                _edge?.RelocateHotspot();
                _dock?.SetMaterial(state.Preferences.Material);
                if (_edge is not null) _edge.AutoCollapseEnabled = state.Preferences.AutoHide;
            });
        };

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () => await InitializeAsync()));
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userData = Path.Combine(DataDirectory, "WebView2");
            _environment = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions());
            Log.Info("WebView2 运行环境就绪（共享 userDataFolder）");
        }
        catch (Exception ex)
        {
            Log.Error($"WebView2 环境创建失败: {ex.Message}");
            MessageBox.Show($"WebView2 运行时不可用：{ex.Message}", "Luma", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var primary = _monitors!.Primary;
        var dockHeight = Math.Min(640.0, primary.WorkArea.Height);
        _dock = new DockWindow(_router!, _backdrop!, _environment, primary.WorkArea.Width, dockHeight);
        _dock.LayoutReady += () =>
        {
            if (_pendingDockShow || _args.Contains("--show-dock")) { _pendingDockShow = false; _edge?.TriggerFromTray(); }
        };
        _dock.SetRadiusProvider(() => _store!.Current.Preferences.Radius);
        _dock.PixelScaleChanged += () => _edge?.RelocateHotspot();
        _dock.RelocatePhysical(primary.PhysicalWorkArea, primary.DpiScale);
        // 创建即预显示：窗口命中区域为空（视觉不可见、不接收输入），但 WebView2 得以完成初始化并预加载前端，
        // 首次唤出无白屏等待。visibility 事件仍只由热区/托盘唤出时发送。
        _dock.PreInitialize();
        _dock.SetMaterial(_store!.Current.Preferences.Material);

        _dockCloseTimer.Tick += (_, _) =>
        {
            if (_dockVisibility.CloseExpired(Environment.TickCount64))
            {
                _dockCloseTimer.Stop();
                if (_dock.IsInteracting || EdgeActivation.HasPressedInput()) _edge?.CancelClose();
                // Fullscreen avoidance may refuse reversal. Never leave a Closing
                // dock stranded after stopping its only fallback timer.
                if (_dockVisibility.Phase == DockVisibilityPhase.Closing)
                { Log.Warn("浮岛退出确认超时，执行有界隐藏兜底"); CompleteDockHide(); }
            }
        };
        _edge = new EdgeActivation(() => _dock.WindowHandle, HotspotLayout,
            () => _dock.IsInteracting, _dock.ContainsPhysicalPoint);
        _edge.AutoCollapseEnabled = _store.Current.Preferences.AutoHide;
        _edge.ShowDockRequested += OnShowDock;
        _edge.HideDockRequested += OnHideDock;
        _edge.Start();

        CreateTray();
        RegisterSearchHotkey();
        Log.Info("内核初始化完成：浮岛隐藏待唤出，热区已启动");

        // 验收辅助参数：--settings 直接打开管理窗；--show-dock 直接唤出浮岛。
        if (_args.Contains("--settings")) ((IWindowHost)this).OpenSettings("projects");
        if (_args.Contains("--search")) OpenSearch();
    }

    private void OnShowDock(EdgeActivation.HotspotLayout layout)
    {
        Log.Info($"原生唤出 phase={_dockVisibility.Phase} synced={_dock?.HasSynced} monitor=0x{layout.Monitor:X}");
        if (_dock?.HasSynced != true) { _pendingDockShow = true; _edge?.NotifyDockHidden(); return; }
        _dockCloseTimer.Stop();
        _dockVisibility.RequestShow();
        Log.Info("原生浮岛进入 AwaitingExpanded");
        _dock.RelocatePhysical(layout.WorkArea, layout.Scale);
        // Keep the native region empty while waiting for a fresh expanded layout.
        _dock.PreInitialize();
        _router!.BroadcastVisibility(true, _dockVisibility.VisibilityId);
    }

    public static EdgeActivation.HotspotLayout CreateHotspotLayout(MonitorInfo monitor, double width)
    {
        var work = monitor.PhysicalWorkArea;
        var scale = monitor.DpiScale > 0 ? monitor.DpiScale : 1;
        var widthPhys = (int)Math.Min(work.Width, Math.Round(Math.Max(320, width) * scale));
        return new EdgeActivation.HotspotLayout((int)work.X + ((int)work.Width - widthPhys) / 2,
            (int)work.Y, widthPhys, Math.Max(4, (int)Math.Ceiling(EdgeActivation.HotspotHeightPx * scale)),
            monitor.Handle, work, scale);
    }
    private IReadOnlyList<EdgeActivation.HotspotLayout> HotspotLayout() =>
        _monitors!.Monitors.Select(m => CreateHotspotLayout(m, _store!.Current.Preferences.Width)).ToArray();

    private void OnHideDock()
    {
        Log.Info($"原生浮岛请求退出 previous={_dockVisibility.Phase}");
        if (!_dockVisibility.RequestHide(Environment.TickCount64)) return;
        _router!.BroadcastVisibility(false, _dockVisibility.VisibilityId);
        _dockCloseTimer.Stop();
        _dockCloseTimer.Start();
    }

    private void CompleteDockHide()
    {
        _dockCloseTimer.Stop();
        _dockVisibility.Hide();
        _edge?.NotifyDockHidden();
        _dock?.HideDock();
    }

    private void ListenActivation()
    {
        var signals = new WaitHandle[] { _activateSignal!, _settingsSignal!, _searchSignal!, _stopListener };
        while (true)
        {
            try
            {
                var index = WaitHandle.WaitAny(signals);
                if (index == 3) break;
                Dispatcher.BeginInvoke(() =>
                {
                    if (index == 2) OpenSearch();
                    else if (index == 1) ((IWindowHost)this).OpenSettings("projects");
                    else _edge?.TriggerFromTray();
                });
            }
            catch (ObjectDisposedException) { break; }
        }
    }

    private void OnDisplaysChanged()
    {
        // 协议：先取消进行中的手势 visible=false，更新窗口，再视情况恢复。
        Dispatcher.BeginInvoke(() =>
        {
            var wasVisible = _edge?.DockVisible == true;
            // Geometry changed underneath an exit. Invalidate that exit before relocation;
            // a queued collapsed frame must not close the subsequent reveal.
            if (wasVisible)
            {
                _dockVisibility.RequestImmediateHide();
                _router!.BroadcastVisibility(false, _dockVisibility.VisibilityId);
                CompleteDockHide();
            }
            var primary = _monitors!.Primary;
            _dock?.RelocatePhysical(primary.PhysicalWorkArea, primary.DpiScale);
            _edge?.RelocateHotspot();
            if (wasVisible) _edge?.TriggerFromTray();
        });
    }

    #region IWindowHost

    void IWindowHost.SyncDock(bool expanded, Rect[] rects, bool interacting, long? visibilityId)
    {
        if (_dock is null) return;
        // This gate must precede region changes, interacting and all handshake state.
        // Old expanded+collapsed pairs can otherwise confirm and hide a newer reveal.
        if (!_dockVisibility.AcceptsSync(visibilityId))
        {
            Log.Info($"忽略过期布局 visibilityId={visibilityId} current={_dockVisibility.VisibilityId}");
            return;
        }
        // sync 只更新命中区域与材质裁剪；窗口可见性仅由热区/托盘唤出（EdgeActivation）控制，
        // 避免前端初始布局上报把未唤出的浮岛带出来。
        // Old collapsed reports may already be queued when reveal is requested.
        // They can update preload layout but cannot cancel AwaitingExpanded.
        // ApplySync may synchronously raise LayoutReady and request a reveal.
        // Its initial frame must not acknowledge the request it just caused.
        Log.Info($"原生布局握手 phase={_dockVisibility.Phase} expanded={expanded}");
        var wasAwaiting = _dockVisibility.Phase == DockVisibilityPhase.AwaitingExpanded;
        if (wasAwaiting && !expanded) return;
        // A frontend close has no preceding native Closing phase. Arm before clearing
        // the hit region: Hide/SetWindowRgn can synthesize a hotspot enter under a still cursor.
        // Native auto-hide uses Closing and must preserve its normal hover behavior.
        if (!expanded && _dockVisibility.Phase == DockVisibilityPhase.Visible)
            _edge?.SuppressHoverAfterFrontendCollapse();
        _dock.ApplySync(expanded, rects, interacting);
        if (expanded && interacting && _dockVisibility.Phase == DockVisibilityPhase.Closing)
        {
            _edge?.CancelClose();
            return;
        }
        if (wasAwaiting && _dockVisibility.Acknowledge(expanded, visibilityId))
        {
            _dock.ShowDock();
            _edge?.NotifyDockShown();
        }
        else if (_dockVisibility.ShouldHide(expanded, visibilityId)) CompleteDockHide();
    }

    void IWindowHost.OpenSettings(string section)
    {
        if (section == "search") { OpenSearch(); return; }
        if (_environment is null) return;
        if (_settings is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            existing.NavigateToSection(section);
            return;
        }
        _settings = new SettingsWindow(_router!, _environment, _store!, section, () => _edge?.TriggerFromTray());
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
        Log.Info($"打开管理窗 section={section}");
    }

    void IWindowHost.ShowDock() => _edge?.TriggerFromTray();

    #endregion

    private void OpenSearch()
    {
        if (_environment is null) return;
        if (_search is null)
        {
            _search = new SearchWindow(_router!, _environment, _store!);
            _search.Closed += (_, _) => _search = null;
        }
        _search.Show();
        if (_search.WindowState == WindowState.Minimized) _search.WindowState = WindowState.Normal;
        _search.Activate();
        _search.FocusSearch();
    }

    public void CloseSearch() => _search?.Close();

    private void RegisterSearchHotkey()
    {
        _hotkeySource = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Luma Search Hotkey")
        { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _hotkeySource.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == 0x0312 && wParam.ToInt32() == 1) { OpenSearch(); handled = true; }
            return IntPtr.Zero;
        });
        if (!Win32.RegisterHotKey(_hotkeySource.Handle, 1, 0x0001 | 0x0002 | 0x4000, 0x20))
            Log.Warn("Ctrl+Alt+Space 被其他应用占用；可从托盘打开搜索");
    }

    private void CreateTray()
    {
        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "显示 Luma" };
        showItem.Click += (_, _) => _edge?.TriggerFromTray();
        var pauseItem = new MenuItem { Header = "暂停边缘唤出", IsCheckable = true };
        pauseItem.Click += (_, _) => _edge?.SetPaused(pauseItem.IsChecked);
        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => ((IWindowHost)this).OpenSettings("projects");
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => Shutdown();
        var searchItem = new MenuItem { Header = "搜索 (Ctrl+Alt+Space)" };
        searchItem.Click += (_, _) => OpenSearch();
        menu.Items.Add(searchItem);
        menu.Items.Add(showItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _tray = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon
        {
            ToolTipText = "Luma 项目快捷启动",
            ContextMenu = menu,
            IconSource = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/luma.ico")),
        };
        _tray.TrayLeftMouseUp += (_, _) => _edge?.TriggerFromTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dockCloseTimer.Stop();
        // 已回应成功的配置在保存时即已落盘；此处只做释放。
        if (_hotkeySource is not null) { Win32.UnregisterHotKey(_hotkeySource.Handle, 1); _hotkeySource.Dispose(); }
        _edge?.Dispose();
        _monitors?.Dispose();
        _tray?.Dispose();
        try { _dock?.Close(); } catch { }
        try { _settings?.Close(); } catch { }
        try { _search?.Close(); } catch { }
        _stopListener.Set();
        _activateListener?.Join(1000);
        _activateSignal?.Dispose();
        _settingsSignal?.Dispose();
        _searchSignal?.Dispose();
        _stopListener.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        Log.Info("内核退出");
        base.OnExit(e);
    }

    private static string ResolveDistDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        var local = Path.Combine(exeDir, "dist");
        if (File.Exists(Path.Combine(local, "index.html"))) return local;
        DirectoryInfo? dir = new(exeDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "dist", "index.html");
            if (File.Exists(candidate)) return Path.Combine(dir.FullName, "dist");
            dir = dir.Parent;
        }
        return local;
    }
}
