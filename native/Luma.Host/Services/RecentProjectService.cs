using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

public sealed record RecentProjectEntry(string Id, string Name, string Path, string Kind);
public sealed record RecentProjectListing(IReadOnlyList<RecentProjectEntry> Entries, string Note);
public sealed record RecentProjectDocument(string Path, DateTimeOffset Modified);
public interface IRecentProjectSource
{
    string? ResolveExecutable(string savedPath, CancellationToken cancellation);
    IEnumerable<RecentProjectDocument> ReadRecent(CancellationToken cancellation);
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

    public IEnumerable<RecentProjectDocument> ReadRecent(CancellationToken cancellation)
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

public sealed class RecentProjectService
{
    private sealed record Capability(string Client, string Project, string Item, string SavedPath, string Executable, string Path, DateTimeOffset Expires);
    private const string Note = "仅按软件格式匹配 Windows 最近文档，在最多 256 条枚举样本内排序，可能遗漏更新记录；不代表软件私有最近列表。打开使用 Windows 默认文件关联。";
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
        _store = store; _source = source ?? new WindowsRecentProjectSource();
        _open = open ?? RealShellExecutor.Shared.TryLaunch;
        _clock = clock ?? (() => DateTimeOffset.UtcNow); _timeout = timeout ?? TimeSpan.FromSeconds(4);
    }

    public Task<RecentProjectListing> GetAsync(string client, string project, string item, int limit = 6) => Bounded(client, cancel =>
    {
        ValidateIds(client, project, item);
        if (limit is < 6 or > 10) throw Invalid("最近项目数量必须为 6 到 10。");
        var saved = SavedPath(project, item);
        var executable = _source.ResolveExecutable(saved, cancel);
        var extensions = Extensions(executable);
        cancel.ThrowIfCancellationRequested();
        if (extensions.Length == 0) return new RecentProjectListing([], "暂无法从已保存软件身份确定专属格式。" + Note);
        var rows = _source.ReadRecent(cancel).Take(256).OrderByDescending(row => row.Modified).ToArray();
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
        "afterfx.exe" => [".aep", ".aepx"],
        "adobe premiere pro.exe" => [".prproj"],
        "blender.exe" => [".blend"],
        "figma.exe" => [".fig"],
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
        catch (UnauthorizedAccessException) { throw new FolderOperationException(ProtocolErrors.AccessDenied, "没有权限读取最近项目。"); }
        catch (Exception ex) when (ex is IOException or COMException) { throw new FolderOperationException(ProtocolErrors.IoError, "最近项目暂时无法读取。"); }
    }
}
