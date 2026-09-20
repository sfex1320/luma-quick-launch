using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Luma.Host.Interop;
using Luma.Host.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Luma.Host.Windows;

/// <summary>Opaque editor/search windows only; never attach this to the transparent dock.</summary>
internal sealed class WindowTheme : IDisposable
{
    private const int ImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private readonly Window _window;
    private readonly WebView2 _web;
    private readonly ThemeSubscription _subscription;

    public WindowTheme(Window window, WebView2 web, StateStore store)
    {
        _window = window;
        _web = web;
        _subscription = new ThemeSubscription(store,
            action => window.Dispatcher.BeginInvoke(action), Apply);
        window.SourceInitialized += OnSourceInitialized;
    }

    // WebView creation and HWND creation can finish in either order.
    public void Refresh() => _subscription.Refresh(force: true);
    private void OnSourceInitialized(object? sender, EventArgs e) => Refresh();

    private void Apply(bool dark)
    {
        var background = dark ? System.Drawing.Color.FromArgb(27, 26, 23) : System.Drawing.Color.FromArgb(237, 235, 229);
        _window.Background = new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B));
        _web.DefaultBackgroundColor = background;
        if (_web.CoreWebView2 is { } core)
        {
            try
            {
                core.Profile.PreferredColorScheme = dark
                    ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
                Log.Info($"窗口主题 WebView title={_window.Title} preferred={core.Profile.PreferredColorScheme} background={background.Name}");
            }
            catch (Exception ex) { Log.Warn($"WebView2 主题设置失败: {ex.Message}"); }
        }
        var hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var darkResult = SetAttribute(hwnd, ImmersiveDarkMode, dark ? 1 : 0);
        // Explicit colors make the app's saved theme independent of the Windows theme.
        // COLORREF is 0x00BBGGRR: matches the workbench's --side and --ink palette.
        var caption = dark ? 0x001c1f20 : 0x00f2f6f7;
        var text = dark ? 0x00eaf0f2 : 0x00111314;
        var captionResult = SetAttribute(hwnd, CaptionColor, caption);
        var textResult = SetAttribute(hwnd, TextColor, text);
        Log.Info($"窗口主题 DWM title={_window.Title} dark={dark} caption=0x{caption:X6} text=0x{text:X6} results={darkResult:X8}/{captionResult:X8}/{textResult:X8}");
    }

    private static int SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        var result = Win32.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        if (result < 0) Log.Warn($"窗口主题属性 {attribute} 不可用: 0x{result:X8}");
        return result;
    }

    public void Dispose()
    {
        _window.SourceInitialized -= OnSourceInitialized;
        _subscription.Dispose();
    }
}
