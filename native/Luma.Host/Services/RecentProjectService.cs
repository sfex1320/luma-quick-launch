using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

public sealed record RecentProjectEntry(string Id, string Name, string Path, string Kind);
public sealed record RecentProjectListing(IReadOnlyList<RecentProjectEntry> Entries, string Note);
public sealed record RecentProjectDocument(string Path, DateTimeOffset Modified);
public interface IRecentProjectSource
{
    string? ResolveExecutable(string savedPath, CancellationToken cancellation);
    IEnumerable<RecentProjectDocument> ReadRecent(string executable, CancellationToken cancellation);
    bool IsRegularFile(string path);
}

/// <summary>Windows Recent metadata only. No private app databases, recursive searches or command execution.</summary>
public sealed class WindowsRecentProjectSource(string? recentDirectory = null) : IRecentProjectSource
{
    public string? ResolveExecutable(string savedPath, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!IsRegularFile(savedPath)) return null;
        if (Path.GetExtension(savedPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && new FileInfo(savedPath).Length > 1024 * 1024) return null;
        var path = Path.GetExtension(savedPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? ReadLink(savedPath) : savedPath;
        cancellation.ThrowIfCancellationRequested();
        return path is not null && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            RecentProjectService.SafePath(path) && IsRegularFile(path) ? path : null;
    }

    public IEnumerable<RecentProjectDocument> ReadRecent(string executable, CancellationToken cancellation)
    {
        var recent = recentDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrEmpty(recent) || !Directory.Exists(recent)) yield break;
        // Bound enumeration before filtering/sorting. Windows Recent can contain arbitrarily many entries.
        var names = Directory.EnumerateFileSystemEntries(recent).Take(256).ToArray();
        foreach (var name in names)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Path.GetExtension(name).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            RecentProjectDocument? row = null;
            try
            {
                if (IsRegularFile(name) && new FileInfo(name).Length <= 1024 * 1024)
                {
                    var target = ReadLink(name);
                    if (target is not null) row = new(target, File.GetLastWriteTimeUtc(name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { }
            if (row is not null) yield return row;
        }
    }

    public bool IsRegularFile(string path)
    {
        if (!RecentProjectService.SafePath(path)) return false;
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return false;
            var parent = Path.GetDirectoryName(path);
            for (var depth = 0; parent is not null; depth++)
            {
                if (depth >= 128 || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
                parent = Path.GetDirectoryName(parent);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? ReadLink(string path)
    {
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            link = shell!.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            // Do not reinterpret argument-bearing links as an associated recent document/app.
            if (!string.IsNullOrWhiteSpace((string)link!.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, link, null)!)) return null;
            return Environment.ExpandEnvironmentVariables((string)link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null)!);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException) { return null; }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}

/// <summary>Reads Adobe MediaBrowser's per-application MRU. It never scans document directories.</summary>
public sealed class WindowsAppRecentSource : IRecentProjectSource
{
    internal sealed record ApplicationMetadata(string? Company, string? Product, string? OriginalFilename);
    private readonly WindowsRecentProjectSource _paths = new();
    private readonly Func<string, CancellationToken, IEnumerable<RecentProjectDocument>> _reader;
    private readonly Func<string, ApplicationMetadata> _metadata;
    public WindowsAppRecentSource() : this(ReadRegistry) { }
    internal WindowsAppRecentSource(Func<string, CancellationToken, IEnumerable<RecentProjectDocument>> reader,
        Func<string, ApplicationMetadata>? metadata = null)
    { _reader = reader; _metadata = metadata ?? ReadMetadata; }
    public string? ResolveExecutable(string savedPath, CancellationToken cancellation)
    {
        var executable = _paths.ResolveExecutable(savedPath, cancellation);
        var app = executable is null ? null : Application(executable, cancellation);
        cancellation.ThrowIfCancellationRequested();
        return app is null ? null : executable;
    }
    public bool IsRegularFile(string path) => _paths.IsRegularFile(path);

    public IEnumerable<RecentProjectDocument> ReadRecent(string executable, CancellationToken cancellation)
    {
        var app = Application(executable, cancellation);
        if (app is null) yield break;
        foreach (var row in _reader(app, cancellation).Take(256))
        { cancellation.ThrowIfCancellationRequested(); yield return row; }
    }

    private string? Application(string executable, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var app = Path.GetFileName(executable).ToLowerInvariant() switch
        {
            "photoshop.exe" => "Photoshop",
            "illustrator.exe" => "illustrator",
            "indesign.exe" => "indesign",
            _ => null,
        };
        if (app is null) return null;
        try
        {
            var metadata = _metadata(executable);
            cancellation.ThrowIfCancellationRequested();
            var company = metadata.Company?.Trim();
            var product = metadata.Product?.Trim();
            var expectedProduct = "Adobe " + app;
            // Local product resources establish application association, not publisher trust.
            // Accept observed modern and historical Adobe company names without a network signature check.
            var adobe = company is not null && new[] { "Adobe", "Adobe Inc.", "Adobe Inc", "Adobe Systems Incorporated", "Adobe Systems, Incorporated" }
                .Contains(company, StringComparer.OrdinalIgnoreCase);
            var productMatches = product is not null && (product.Equals(expectedProduct, StringComparison.OrdinalIgnoreCase) ||
                product.StartsWith(expectedProduct + " ", StringComparison.OrdinalIgnoreCase));
            return adobe && productMatches && string.Equals(metadata.OriginalFilename?.Trim(), Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase)
                ? app : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static ApplicationMetadata ReadMetadata(string executable)
    {
        var info = FileVersionInfo.GetVersionInfo(executable);
        return new(info.CompanyName, info.ProductName, info.OriginalFilename);
    }

    private static IEnumerable<RecentProjectDocument> ReadRegistry(string app, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var root = Registry.CurrentUser.OpenSubKey($@"Software\Adobe\MediaBrowser\MRU\{app}\FileList");
        if (root is null) yield break;
        foreach (var row in ReadRegistryRows(root, cancellation)) yield return row;
    }

    internal static IEnumerable<RecentProjectDocument> ReadRegistryRows(RegistryKey root, CancellationToken cancellation)
    {
        // RegEnumKeyEx is incremental; GetSubKeyNames would allocate/enumerate the entire key first.
        var nameBuffer = new StringBuilder(256);
        for (uint index = 0; index < 256; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            nameBuffer.Clear();
            uint characters = 256;
            var result = RegEnumKeyEx(root.Handle, index, nameBuffer, ref characters, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (result == 259) yield break; // ERROR_NO_MORE_ITEMS
            if (result != 0) continue;
            var name = nameBuffer.ToString();
            if (!DateTimeOffset.TryParse(name, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var modified)) continue;
            string? path = null;
            try { using var row = root.OpenSubKey(name); if (row is not null) path = ReadRegistryPath(row, cancellation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            if (!string.IsNullOrWhiteSpace(path)) yield return new(path, modified.ToUniversalTime());
        }
    }

    internal static string? ReadRegistryPath(RegistryKey row, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        uint bytes = 0;
        if (RegQueryValueEx(row.Handle, null, IntPtr.Zero, out var type, null, ref bytes) != 0 ||
            type != 1 || bytes is < 2 or > 8194 || bytes % 2 != 0) return null; // REG_SZ, 4096 UTF-16 units + optional NUL
        var buffer = new byte[bytes];
        cancellation.ThrowIfCancellationRequested();
        if (RegQueryValueEx(row.Handle, null, IntPtr.Zero, out type, buffer, ref bytes) != 0 ||
            type != 1 || bytes > buffer.Length || bytes % 2 != 0) return null;
        cancellation.ThrowIfCancellationRequested();
        string value;
        try { value = new UnicodeEncoding(false, false, true).GetString(buffer, 0, (int)bytes); }
        catch (DecoderFallbackException) { return null; }
        if (value.EndsWith('\0')) value = value[..^1];
        return value.Length <= 4096 && !value.Contains('\0') ? value : null;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumKeyExW")]
    private static extern int RegEnumKeyEx(SafeRegistryHandle key, uint index, StringBuilder name, ref uint nameLength,
        IntPtr reserved, IntPtr keyClass, IntPtr classLength, IntPtr lastWriteTime);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string? name, IntPtr reserved, out uint type,
        [Out] byte[]? data, ref uint dataLength);
}

public sealed class RecentProjectService
{
    private sealed record Capability(string Client, string Project, string Item, string SavedPath, string Executable, string Path, DateTimeOffset Expires);
    private const string Note = "来自该 Adobe 软件的本机最近列表；最多读取 256 条样本，可能遗漏更新记录。打开使用 Windows 默认文件关联。";
    private static readonly Lazy<BlockingCollection<Action>> Queue = new(() =>
    {
        var queue = new BlockingCollection<Action>(16);
        for (var i = 0; i < 2; i++)
        {
            var worker = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); })
                { IsBackground = true, Name = $"Luma recent documents {i}" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        return queue;
    });
    private readonly object _gate = new();
    private readonly Dictionary<string, Capability> _tokens = new();
    private readonly Dictionary<CancellationTokenSource, string> _operations = new();
    private readonly StateStore _store;
    private readonly IRecentProjectSource _source;
    private readonly Func<string, CancellationToken, string?> _open;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;

    public RecentProjectService(StateStore store, IRecentProjectSource? source = null,
        Func<string, CancellationToken, string?>? open = null, Func<DateTimeOffset>? clock = null, TimeSpan? timeout = null)
    {
        _store = store; _source = source ?? new WindowsAppRecentSource();
        _open = open ?? RealShellExecutor.Shared.TryLaunch;
        _clock = clock ?? (() => DateTimeOffset.UtcNow); _timeout = timeout ?? TimeSpan.FromSeconds(4);
    }

    public Task<bool> SupportsRecentAsync(string client, string project, string item) => Bounded(client, cancel =>
    {
        ValidateIds(client, project, item);
        var saved = SavedPath(project, item);
        var executable = _source.ResolveExecutable(saved, cancel);
        cancel.ThrowIfCancellationRequested();
        if (!Same(saved, SavedPath(project, item))) throw Invalid("软件入口已变更，请重新展开。");
        return Extensions(executable).Length > 0;
    });

    public Task<RecentProjectListing> GetAsync(string client, string project, string item, int limit = 6) => Bounded(client, cancel =>
    {
        ValidateIds(client, project, item);
        if (limit is < 6 or > 10) throw Invalid("最近项目数量必须为 6 到 10。");
        var saved = SavedPath(project, item);
        var executable = _source.ResolveExecutable(saved, cancel);
        var extensions = Extensions(executable);
        cancel.ThrowIfCancellationRequested();
        if (extensions.Length == 0) return new RecentProjectListing([], "该软件暂未接入可靠的专属最近文件来源。");
        var rows = _source.ReadRecent(executable!, cancel).Take(256).OrderByDescending(row => row.Modified).ToArray();
        var selected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            cancel.ThrowIfCancellationRequested();
            if (SafePath(row.Path) && extensions.Contains(Path.GetExtension(row.Path), StringComparer.OrdinalIgnoreCase) &&
                seen.Add(row.Path) && _source.IsRegularFile(row.Path)) selected.Add(row.Path);
            if (selected.Count == limit) break;
        }
        if (!Same(saved, SavedPath(project, item))) throw Invalid("软件入口已变更，请重新展开。");
        lock (_gate)
        {
            cancel.ThrowIfCancellationRequested();
            Prune();
            while (_tokens.Count > 512 - limit) _tokens.Remove(_tokens.First().Key);
            var entries = selected.Select(path =>
            {
                var id = Guid.NewGuid().ToString("N");
                _tokens[id] = new(client, project, item, saved, executable!, path, _clock().AddMinutes(2));
                return new RecentProjectEntry(id, Path.GetFileName(path), path, "file");
            }).ToArray();
            return new RecentProjectListing(entries, Note);
        }
    });

    public Task<bool> OpenAsync(string client, string project, string item, string entryId) => Bounded(client, cancel =>
    {
        ValidateIds(client, project, item, entryId);
        Capability cap;
        lock (_gate)
        {
            Prune();
            if (!_tokens.TryGetValue(entryId, out cap!) || cap.Client != client || cap.Project != project || cap.Item != item)
                throw Invalid("最近项目入口已过期或不匹配，请重新展开。");
        }
        var saved = SavedPath(project, item);
        var executable = _source.ResolveExecutable(saved, cancel);
        cancel.ThrowIfCancellationRequested();
        if (!Same(saved, cap.SavedPath) || !Same(executable, cap.Executable) || !SafePath(cap.Path) ||
            !Extensions(executable).Contains(Path.GetExtension(cap.Path), StringComparer.OrdinalIgnoreCase)) throw Invalid("软件或最近项目入口已变更，请重新展开。");
        if (!_source.IsRegularFile(cap.Path)) throw new FolderOperationException(ProtocolErrors.PathNotFound, "最近项目已移动、删除或是链接。");
        if (!Same(saved, SavedPath(project, item))) throw Invalid("软件入口已变更，请重新展开。");
        lock (_gate)
        {
            cancel.ThrowIfCancellationRequested();
            if (!_tokens.ContainsKey(entryId) || cap.Expires <= _clock()) throw Invalid("最近项目入口已过期，请重新展开。");
        }
        cancel.ThrowIfCancellationRequested();
        var error = _open(cap.Path, cancel);
        if (error is not null) throw new FolderOperationException(ProtocolErrors.AccessDenied, error);
        return true;
    });

    public void Detach(string client)
    {
        lock (_gate)
        {
            foreach (var id in _tokens.Where(pair => pair.Value.Client == client).Select(pair => pair.Key).ToArray()) _tokens.Remove(id);
            foreach (var operation in _operations.Where(pair => pair.Value == client)) operation.Key.Cancel();
        }
    }

    private string SavedPath(string project, string item)
    {
        if (_store.LoadError is not null) throw new FolderOperationException(ProtocolErrors.IoError, "配置无法读取。");
        var saved = _store.Current.Projects.FirstOrDefault(p => p.Id == project)?.Items.FirstOrDefault(i => i.Id == item);
        if (saved is null) throw new FolderOperationException(ProtocolErrors.PathNotFound, "软件入口不存在。");
        if (saved.Kind != "app" || !SafePath(saved.Path)) throw Invalid("最近项目仅适用于已保存的软件入口。");
        return saved.Path;
    }
    private static string[] Extensions(string? executable) => executable is null || !SafePath(executable) ? [] : Path.GetFileName(executable).ToLowerInvariant() switch
    {
        "photoshop.exe" => [".psd", ".psb"],
        "illustrator.exe" => [".ai", ".eps"],
        "indesign.exe" => [".indd", ".idml"],
        _ => [],
    };
    internal static bool SafePath(string path)
    {
        try
        {
            return path.Length <= 4096 && PathRules.Validate(path) is null && Path.IsPathFullyQualified(path) &&
                !path.StartsWith(@"\\?\") && !path.StartsWith(@"\\.\") &&
                Same(Path.GetFullPath(path), path) && !path[Path.GetPathRoot(path)!.Length..].Contains(':');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static void ValidateIds(params string[] values)
    { if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 200)) throw Invalid("入口编号无效。"); }
    private void Prune()
    { foreach (var id in _tokens.Where(pair => pair.Value.Expires <= _clock()).Select(pair => pair.Key).ToArray()) _tokens.Remove(id); }
    private static FolderOperationException Invalid(string message) => new(ProtocolErrors.InvalidRequest, message);

    private async Task<T> Bounded<T>(string client, Func<CancellationToken, T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource();
        lock (_gate)
        {
            if (_operations.Count >= 18) { cancellation.Dispose(); throw new FolderOperationException(ProtocolErrors.Busy, "最近项目正在读取，请稍后重试。"); }
            _operations[cancellation] = client;
        }
        if (!Queue.Value.TryAdd(() =>
        {
            try { cancellation.Token.ThrowIfCancellationRequested(); completion.TrySetResult(action(cancellation.Token)); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { lock (_gate) { _operations.Remove(cancellation); cancellation.Dispose(); } }
        }))
        { lock (_gate) { _operations.Remove(cancellation); cancellation.Dispose(); } throw new FolderOperationException(ProtocolErrors.Busy, "最近项目队列已满，请稍后重试。"); }
        try { return await completion.Task.WaitAsync(_timeout); }
        catch (TimeoutException)
        {
            lock (_gate) { if (_operations.ContainsKey(cancellation)) cancellation.Cancel(); }
            _ = completion.Task.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            throw new FolderOperationException(ProtocolErrors.Busy, "最近项目读取超时，请稍后重试。");
        }
        catch (OperationCanceledException) { throw new FolderOperationException(ProtocolErrors.Cancelled, "最近项目操作已取消。"); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { throw new FolderOperationException(ProtocolErrors.AccessDenied, "没有权限读取最近项目。"); }
        catch (Exception ex) when (ex is IOException or COMException) { throw new FolderOperationException(ProtocolErrors.IoError, "最近项目暂时无法读取。"); }
    }
}
