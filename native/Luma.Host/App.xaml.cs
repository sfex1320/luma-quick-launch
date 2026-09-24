using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private InstanceShutdownSignal? _shutdownSignal;
    private readonly DockVisibilityState _dockVisibility = new();
    private readonly System.Windows.Threading.DispatcherTimer _dockCloseTimer = new()
    { Interval = TimeSpan.FromMilliseconds(DockVisibilityState.CloseTimeoutMilliseconds) };
    private System.Windows.Interop.HwndSource? _hotkeySource;
    private ShortcutService? _shortcuts;
    private readonly PendingShortcutActivation _shortcutActivation = new();
    private readonly System.Windows.Threading.DispatcherTimer _recordingTimer = new();
    private volatile bool _exiting;
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
    private readonly HostLifecycleDiagnostics _lifecycle = new(Log.Info, Log.Error);

    public App()
    {
        // Record before WPF/CLR apply their normal unhandled-exception policy. Never set
        // Handled or SetObserved here: unknown failures must not resume a damaged host.
        DispatcherUnhandledException += (_, e) => _lifecycle.RecordException("dispatcher", e.Exception, true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _lifecycle.RecordException("app-domain", e.ExceptionObject, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            _lifecycle.RecordException("unobserved-task", e.Exception, false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _lifecycle.ProcessExiting(Environment.ExitCode);
    }

    internal void RequestShutdown(string reason, int exitCode = 0)
    {
        _lifecycle.RequestExit(reason);
        Shutdown(exitCode);
    }

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
        Log.Init(DataDirectory);
        base.OnStartup(e);
        _args = e.Args;
        // Installer/uninstaller control does not initialize WebView, state, tray or activation.
        if (_args.Contains("--shutdown"))
        {
            InstanceShutdownSignal.TryRequest(Environment.ProcessPath!);
            RequestShutdown("shutdown-command-completed");
            return;
        }
        if (!EnsureRuntimePrerequisites())
        {
            RequestShutdown("runtime-prerequisite-unavailable", 1);
            return;
        }
        _singleInstance = new Mutex(true, SingleInstanceMutexName + InstanceSuffix, out var isFirst);
        if (!isFirst)
        {
            if (_args.Contains("--startup")) { RequestShutdown("duplicate-startup"); return; }
            Log.Info("已有 Luma 实例运行，发送激活信号后退出");
            try
            {
                var signalName = ActivateEventName + InstanceSuffix + (_args.Contains("--search") ? ".Search" : _args.Contains("--settings") ? ".Settings" : "");
                if (EventWaitHandle.TryOpenExisting(signalName, out var existing)) { existing.Set(); existing.Dispose(); }
            }
            catch { /* 已有实例退出中的竞态可忽略 */ }
            RequestShutdown("duplicate-activation-forwarded");
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + InstanceSuffix);
        _settingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + InstanceSuffix + ".Settings");
        _searchSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName + InstanceSuffix + ".Search");
        _shutdownSignal = new InstanceShutdownSignal(Environment.ProcessPath!);
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
        // 便携更新：替换脚本启动后由桥接在应答发出后触发优雅退出（UpdateService 不依赖 Application.Current）。
        _router = new BridgeRouter(_store, _launcher, new FileDialogFolderPicker(), this, new DispatcherSyncContext(Dispatcher),
            updater: new UpdateService(() => Dispatcher.BeginInvoke(() => RequestShutdown("update-apply"))));

        _monitors.DisplaysChanged += OnDisplaysChanged;
        _store.StateChanged += state =>
        {
            // 外观变化（宽度/材质/自动隐藏）影响热区、backdrop 与收起行为，在 UI 线程应用；面板布局由前端 sync 驱动。
            Dispatcher.BeginInvoke(() =>
            {
                _shortcuts?.Reconcile(); // Resolve latest saved state inside the dispatcher; never capture an obsolete save.
                _edge?.RelocateHotspot();
                _dock?.SetMaterial(state.Preferences.Material);
                if (_edge is not null) _edge.AutoCollapseEnabled = state.Preferences.AutoHide;
            });
        };

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () => await InitializeAsync()));
    }

    private static bool EnsureRuntimePrerequisites()
    {
        if (!RuntimePreflight.IsSupportedWindows(Environment.OSVersion.Version))
        {
            MessageBox.Show("此版本需要 Windows 10 22H2（内部版本 19045）或更高版本。", "Luma 无法启动",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        if (!RuntimePreflight.IsSupportedArchitecture(RuntimeInformation.ProcessArchitecture))
        {
            MessageBox.Show("此交付包仅包含 x64 程序。请使用 x64 Windows，或在 Windows on ARM 的 x64 模拟环境中运行。",
                "Luma 无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var registry = new WindowsRegistryValueReader();
        bool IsInstalled() => RuntimePreflight.IsWebView2Installed(registry, Environment.Is64BitOperatingSystem);
        if (IsInstalled()) return true;

        var bootstrapper = Path.Combine(AppContext.BaseDirectory, RuntimePreflight.BootstrapperFileName);
        if (!File.Exists(bootstrapper))
        {
            MessageBox.Show("缺少 Microsoft Edge WebView2 Runtime，且交付目录中没有安装程序。请重新下载完整的 Luma 安装包。",
                "Luma 依赖缺失", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        if (!RuntimePreflight.HasTrustedMicrosoftSignature(bootstrapper))
        {
            MessageBox.Show("WebView2 安装程序的 Microsoft 数字签名无效。为保护设备，Luma 不会运行此文件；请重新下载完整的 Luma 安装包。",
                "Luma 依赖校验失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        if (MessageBox.Show("Luma 需要 Microsoft Edge WebView2 Runtime。是否现在按当前用户安装？\n\n安装程序由 Microsoft 签名，需要联网，不会请求管理员权限。",
                "安装 Luma 运行依赖", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return false;

        int LaunchInstaller()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(bootstrapper, "/silent /install")
                {
                    UseShellExecute = false,
                    WorkingDirectory = AppContext.BaseDirectory
                });
                if (process is null) return -1;
                if (process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds)) return process.ExitCode;
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch { /* 已超时；不保留第二个并行安装进程。 */ }
                return RuntimePreflight.InstallTimedOut;
            }
            catch
            {
                return -1;
            }
        }

        var result = RuntimePreflight.InstallWithRetry(LaunchInstaller, IsInstalled);
        if (result.Succeeded) return true;
        MessageBox.Show($"WebView2 Runtime 安装失败（退出代码：{string.Join("、", result.ExitCodes)}）。请检查网络连接后重新启动 Luma 重试。",
            "Luma 依赖安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        return false;
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
            RequestShutdown("webview-environment-failed", 1);
            return;
        }

        var primary = _monitors!.Primary;
        var dockHeight = Math.Min(640.0, primary.WorkArea.Height);
        _dock = new DockWindow(_router!, _backdrop!, _environment, primary.WorkArea.Width, dockHeight);
        _dock.RendererReloading += () =>
        {
            Log.Warn($"浮岛渲染器恢复前结束当前可见性会话 visibilityId={_dockVisibility.VisibilityId}");
            _dockVisibility.RequestImmediateHide();
            _router!.BroadcastVisibility(false, _dockVisibility.VisibilityId);
            CompleteDockHide();
        };
        _dock.LayoutReady += () =>
        {
            if (_pendingDockShow || _args.Contains("--show-dock")) { _pendingDockShow = false; _edge?.TriggerFromTray("layout-ready"); }
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
                if (EdgeActivation.InputKeepsDockOpen(closing: true, _dock.IsInteracting, EdgeActivation.HasPressedInput())) _edge?.CancelClose();
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
        _dock?.ResumeForActivation();
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
        _shortcutActivation.Clear();
        _dockCloseTimer.Stop();
        _dockVisibility.Hide();
        _edge?.NotifyDockHidden();
        _dock?.HideDock();
    }

    private void ListenActivation()
    {
        var signals = new WaitHandle[] { _activateSignal!, _settingsSignal!, _searchSignal!, _shutdownSignal!.Handle, _stopListener };
        while (true)
        {
            try
            {
                var index = WaitHandle.WaitAny(signals);
                if (index == 4) break;
                Dispatcher.BeginInvoke(() =>
                {
                    if (index == 3) RequestShutdown("instance-shutdown-signal");
                    else if (index == 2) OpenSearch();
                    else if (index == 1) ((IWindowHost)this).OpenSettings("projects");
                    else _edge?.TriggerFromTray("instance-signal");
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
            if (wasVisible) _edge?.TriggerFromTray("display-change-restore");
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
            if (_shortcutActivation.Take(_store!.Current, true, Environment.TickCount64) is { } activation)
                _dock.PostShortcutActivation(activation.Binding.Id, activation.Serial);
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
        _settings = new SettingsWindow(_router!, _environment, _store!, section, () => _edge?.TriggerFromTray("settings-preview"));
        _settings.Deactivated += (_, _) => _shortcuts?.Detach("settings");
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
        Log.Info($"打开管理窗 section={section}");
    }

    void IWindowHost.ShowDock() => _edge?.TriggerFromTray("bridge-show");

    bool IWindowHost.IsShortcutClientFocused(string clientId) => clientId switch
    {
        "dock" => _dock?.HasShortcutFocus == true && _dockVisibility.Phase == DockVisibilityPhase.Visible,
        "settings" => _settings is { WindowState: not WindowState.Minimized } && Win32.GetForegroundWindow() == _settings.WindowHandle,
        _ => false,
    };

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
            if (message == 0x0312 && _shortcuts is not null) { _ = _shortcuts.HandleHotkeyAsync(wParam.ToInt32()); handled = true; }
            return IntPtr.Zero;
        });
        _shortcuts = new ShortcutService(() => _store!.Current, new WindowsHotkeyPlatform(_hotkeySource.Handle), DispatchShortcut,
            (binding, request) => Task.Run(() =>
            {
                bool Authorized() => !_exiting && (_store!.Current.Preferences.Shortcuts ?? []).Contains(binding);
                return Authorized() && _launcher!.OpenItem(request, binding.ProjectId!, binding.ItemId!, Authorized).Accepted;
            }));
        _router!.Shortcuts = _shortcuts;
        _recordingTimer.Tick += (_, _) => { _recordingTimer.Stop(); _shortcuts.ExpireRecordings(); };
        _shortcuts.RecordingChanged += () =>
        {
            _recordingTimer.Stop();
            if (_shortcuts.NextRecordingExpiry is { } deadline)
            {
                _recordingTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds));
                _recordingTimer.Start();
            }
        };
        _dock!.Deactivated += (_, _) => _shortcuts.Detach("dock");
        _shortcuts.Reconcile();
    }

    private void DispatchShortcut(ShortcutBinding binding, long serial)
    {
        switch (binding.Action)
        {
            case "search": OpenSearch(); break;
            case "settings": ((IWindowHost)this).OpenSettings("projects"); break;
            case "dock": _edge?.TriggerFromTray("shortcut-dock"); break;
            case "project":
            case "edit":
                _shortcutActivation.Request(binding, serial, Environment.TickCount64);
                if (_dockVisibility.Phase == DockVisibilityPhase.Visible)
                {
                    // Even an already-visible document must acknowledge a fresh visibility/layout turn.
                    _dock!.ResumeForActivation();
                    _dockVisibility.RequestShow();
                    _router!.BroadcastVisibility(true, _dockVisibility.VisibilityId);
                }
                else _edge?.TriggerFromTray("shortcut-project-edit");
                if (_dockVisibility.Phase == DockVisibilityPhase.Hidden && !_pendingDockShow) _shortcutActivation.Clear();
                break;
        }
    }

    private void CreateTray()
    {
        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "显示 Luma" };
        showItem.Click += (_, _) => _edge?.TriggerFromTray("tray-menu");
        var pauseItem = new MenuItem { Header = "暂停边缘唤出", IsCheckable = true };
        pauseItem.Click += (_, _) => _edge?.SetPaused(pauseItem.IsChecked);
        var settingsItem = new MenuItem { Header = "设置" };
        settingsItem.Click += (_, _) => ((IWindowHost)this).OpenSettings("projects");
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => RequestShutdown("tray-exit");
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
            ToolTipText = Environment.GetEnvironmentVariable("LUMA_TEST_SESSION") == "soak"
                ? "Luma 后台稳定性测试（独立配置）" : "Luma 项目快捷启动",
            ContextMenu = menu,
            IconSource = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/luma.ico")),
        };
        _tray.TrayLeftMouseUp += (_, _) => _edge?.TriggerFromTray("tray-click");
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _lifecycle.RequestExit($"windows-session-ending:{e.ReasonSessionEnding}");
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifecycle.ExitStarting(e.ApplicationExitCode);
        try
        {
            CleanupHost();
            base.OnExit(e);
            _lifecycle.ExitCompleted(e.ApplicationExitCode);
        }
        catch (Exception ex)
        {
            _lifecycle.RecordException("exit-cleanup", ex, true);
            throw;
        }
    }

    private void CleanupHost()
    {
        _dockCloseTimer.Stop();
        _exiting = true;
        _recordingTimer.Stop();
        _shortcuts?.Dispose();
        // 已回应成功的配置在保存时即已落盘；此处只做释放。
        _hotkeySource?.Dispose();
        _edge?.Dispose();
        _monitors?.Dispose();
        _tray?.Dispose();
        try { _dock?.Close(); } catch (Exception ex) { _lifecycle.RecordException("close-dock", ex, false); }
        try { _settings?.Close(); } catch (Exception ex) { _lifecycle.RecordException("close-settings", ex, false); }
        try { _search?.Close(); } catch (Exception ex) { _lifecycle.RecordException("close-search", ex, false); }
        _stopListener.Set();
        _activateListener?.Join(1000);
        _activateSignal?.Dispose();
        _settingsSignal?.Dispose();
        _searchSignal?.Dispose();
        _shutdownSignal?.Dispose();
        _stopListener.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        Log.Info("内核退出");
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
