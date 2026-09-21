using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Luma.Host.Services;

/// <summary>Used only by the bounded launch STA. Never resolves a title to a path.</summary>
internal sealed class WindowsWindowReusePlatform(Func<string, string?> launch) : IWindowReusePlatform
{
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "python", "pythonw", "py", "node", "java", "javaw",
        "dotnet", "wscript", "cscript", "rundll32", "mshta", "wsl", "bash", "sh", "conhost",
        "explorer", "wt", "WindowsTerminal", "ApplicationFrameHost", "dllhost", "RuntimeBroker"
    };

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static bool CanMatchExecutable(string path, string arguments) =>
        string.IsNullOrWhiteSpace(arguments) && Path.IsPathFullyQualified(path) &&
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
        !SharedHosts.Contains(Path.GetFileNameWithoutExtension(path));

    public ReuseTarget Identify(string path)
    {
        // Settings URIs are already whitelisted by SearchService; they retain Shell handling.
        if (!Path.IsPathFullyQualified(path)) return new("shell:" + path);
        var normalized = Normalize(path);
        if (Directory.Exists(normalized)) return new("folder:" + normalized, Folder: normalized);
        if (CanMatchExecutable(normalized, "")) return new("exe:" + normalized, Executable: normalized);
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return new("shell:" + normalized);
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(normalized);
            string target = Environment.ExpandEnvironmentVariables((string)((dynamic)shortcut).TargetPath);
            string arguments = (string)((dynamic)shortcut).Arguments;
            if (Path.IsPathFullyQualified(target) && string.IsNullOrWhiteSpace(arguments) && Directory.Exists(target))
                return new("folder:" + Normalize(target), Folder: Normalize(target));
            if (CanMatchExecutable(target, arguments))
                return new("exe:" + Normalize(target), Executable: Normalize(target));
            // Arguments can name a project/document, or an interpreter payload. Launch
            // the original .lnk so that Shell preserves all of its original semantics.
        }
        catch (Exception ex) { Log.Info($"快捷方式身份无法安全识别，沿用 Shell：{ex.GetType().Name}"); }
        finally { Release(shortcut); Release(shell); }
        return new("shell:" + normalized);
    }

    public nint Find(ReuseTarget target) => target.Folder is { } folder ? FindFolder(folder) :
        target.Executable is { } executable ? FindExecutable(executable) : 0;

    private static nint FindFolder(string path)
    {
        object? shell = null, windows = null;
        var rows = new List<(nint Window, string Path)>();
        var watch = Stopwatch.StartNew();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!);
            windows = ((dynamic)shell!).Windows();
            var count = (int)((dynamic)windows).Count;
            if (count > 256) throw new TimeoutException("Shell 窗口数量达到安全检查上限。");
            for (var index = 0; index < count; index++)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Shell window lookup exceeded its budget.");
                object? window = null;
                nint hwnd = 0;
                try
                {
                    window = ((dynamic)windows).Item(index);
                    if (window is null) continue;
                    hwnd = new nint((long)((dynamic)window).HWND);
                    string fullName = (string)((dynamic)window).FullName;
                    if (!Path.GetFileName(fullName).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || !IsWindow(hwnd)) continue;
                    string url = (string)((dynamic)window).LocationURL;
                    // Keep an unknown row so a host containing an unidentified tab cannot
                    // accidentally be treated as an unambiguous matching folder window.
                    var location = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? Normalize(uri.LocalPath) : "";
                    rows.Add((hwnd, location));
                }
                catch (COMException) { if (hwnd != 0) rows.Add((hwnd, "")); }
                finally { Release(window); }
            }
            // Win11 can expose several tabs with the same HWND. Only reuse a host when
            // every reported tab agrees; there is no documented Shell API to select one.
            var safeWindow = rows.GroupBy(row => row.Window)
                .Where(group => group.All(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase)))
                .Select(group => group.Key).FirstOrDefault();
            if (safeWindow != 0) return safeWindow;
            if (rows.GroupBy(row => row.Window).Any(group =>
                group.Any(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase)) &&
                group.Any(row => !string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("匹配目录位于含不同目录的标签窗口，无法安全选择对应标签。");
            return 0;
        }
        finally { Release(windows); Release(shell); }
    }

    private static nint FindExecutable(string path)
    {
        nint found = 0;
        var exceeded = false;
        var budget = new WindowScanBudget();
        var images = new Dictionary<uint, string?>();
        EnumWindows((window, _) =>
        {
            if (!budget.VisitWindow()) { exceeded = true; return false; }
            if (!IsWindowVisible(window) || GetWindow(window, 4) != 0) return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!budget.VisitCandidate()) { exceeded = true; return false; }
            GetWindowThreadProcessId(window, out uint pid);
            if (!images.TryGetValue(pid, out var executable))
            {
                var process = OpenProcess(0x1000, false, pid);
                if (process != 0)
                {
                    try
                    {
                        var image = new StringBuilder(32768);
                        uint size = (uint)image.Capacity;
                        if (QueryFullProcessImageName(process, 0, image, ref size)) executable = Normalize(image.ToString());
                    }
                    finally { CloseHandle(process); }
                }
                // Includes inaccessible processes: several HWNDs from the same PID
                // must not repeat an expensive or denied executable-image lookup.
                images[pid] = executable;
            }
            if (string.Equals(executable, path, StringComparison.OrdinalIgnoreCase))
            { found = window; return false; }
            return true;
        }, 0);
        if (exceeded) throw new TimeoutException("窗口查找达到上限，不重复启动尚未检查到的窗口。");
        return found;
    }

    public bool Activate(nint window) => Activate(window, () => true);

    public bool Activate(nint window, Func<bool> stillAuthorized)
    {
        if (!IsWindow(window)) return false;
        // SW_RESTORE only for minimized windows, preserving an already maximized window.
        var accepted = WindowActivation.RestoreAndActivate(() => IsIconic(window),
            () => ShowWindowAsync(window, 9), () => SetForegroundWindow(window),
            () => GetForegroundWindow() == window, stillAuthorized: stillAuthorized);
        Log.Info($"Shell 复用窗口 hwnd={window} foreground={accepted}");
        return accepted;
    }

    public string? Launch(string path) => launch(path);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}
