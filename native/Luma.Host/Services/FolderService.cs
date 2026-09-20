using System.IO;
using System.Runtime.InteropServices;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

public sealed record FolderEntry(string Id, string Name, string Kind);
public sealed record FolderListing(string FolderId, string Name, IReadOnlyList<FolderEntry> Entries, string? ParentId, bool Truncated);
public sealed record FolderDiskEntry(string Path, string Name, bool IsDirectory);

public sealed class FolderOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Only one level is enumerated; the service consumes at most 201 entries.</summary>
public interface IFolderFileSystem
{
    FileAttributes GetAttributes(string path);
    IEnumerable<FolderDiskEntry> Enumerate(string path);
}

public sealed class RealFolderFileSystem : IFolderFileSystem
{
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IEnumerable<FolderDiskEntry> Enumerate(string path)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        }))
            yield return new(entry.FullName, entry.Name, (entry.Attributes & FileAttributes.Directory) != 0);
    }
}

/// <summary>Opaque, client/root-bound capabilities. No paths are accepted from the web page.</summary>
public sealed class FolderService
{
    private sealed record Capability(string Client, string Project, string Item, string Root, string Path, bool Directory, DateTimeOffset Expires);
    // A timed-out network operation retains its slot until the actual IO ends. No abandoned-task growth.
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Capability> _tokens = new();
    private readonly Dictionary<(string Client, string Request), (Lazy<Task<LaunchOutcome>> Work, DateTimeOffset At)> _requests = new();
    private readonly StateStore _store;
    private readonly IShellExecutor _shell;
    private readonly IFolderFileSystem _files;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;

    public FolderService(StateStore store, IShellExecutor? shell = null, IFolderFileSystem? files = null,
        Func<DateTimeOffset>? clock = null, TimeSpan? timeout = null)
    {
        _store = store; _shell = shell ?? RealShellExecutor.Shared; _files = files ?? new RealFolderFileSystem();
        _clock = clock ?? (() => DateTimeOffset.UtcNow); _timeout = timeout ?? TimeSpan.FromSeconds(4);
    }

    public Task<FolderListing> ListAsync(string clientId, string projectId, string itemId, string? folderId = null)
    {
        ValidateIds(clientId, projectId, itemId);
        return RunBounded(token => List(clientId, projectId, itemId, folderId, token));
    }

    public Task<LaunchOutcome> OpenAsync(string clientId, string requestId, string projectId, string itemId, string entryId)
    {
        ValidateIds(clientId, projectId, itemId);
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200) throw Invalid("请求编号无效。");
        lock (_gate)
        {
            var key = (clientId, requestId);
            if (_requests.TryGetValue(key, out var existing) && (!existing.Work.IsValueCreated || !existing.Work.Value.IsCompleted || _clock() - existing.At < TimeSpan.FromMinutes(1)))
                return existing.Work.Value;
            foreach (var expired in _requests.Where(p => p.Value.Work.IsValueCreated && p.Value.Work.Value.IsCompleted && _clock() - p.Value.At >= TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) _requests.Remove(expired);
            if (_requests.Count >= 512) throw new FolderOperationException(ProtocolErrors.Busy, "打开请求较多，请稍后重试。");
            var work = new Lazy<Task<LaunchOutcome>>(() => RunBounded(token =>
            {
                var root = ResolveRoot(projectId, itemId);
                var capability = ResolveToken(clientId, projectId, itemId, root, entryId);
                ValidatePath(root, capability.Path, capability.Directory);
                // Shell links can target a different root. Do not silently follow them.
                if (Path.GetExtension(capability.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(capability.Path).Equals(".url", StringComparison.OrdinalIgnoreCase))
                    throw new FolderOperationException(ProtocolErrors.AccessDenied, "快捷方式可能指向当前目录之外，请将目标另存为入口后打开。");
                EnsureRootUnchanged(projectId, itemId, root);
                token.ThrowIfCancellationRequested();
                var error = _shell.TryLaunch(capability.Path);
                return error is null ? LaunchOutcome.Ok() : LaunchOutcome.Fail(ProtocolErrors.AccessDenied, error);
            }));
            _requests[key] = (work, _clock());
            return work.Value;
        }
    }

    public void Detach(string clientId)
    {
        lock (_gate)
            foreach (var id in _tokens.Where(p => p.Value.Client == clientId).Select(p => p.Key).ToArray()) _tokens.Remove(id);
    }

    // Only the bounded explicit disk-operation service consumes this internal authority snapshot.
    internal (string Root, string Path, bool Directory) ResolveMutationEntry(string client, string project, string item, string entryId)
    {
        ValidateIds(client, project, item, entryId);
        var root = ResolveRoot(project, item);
        var capability = ResolveToken(client, project, item, root, entryId);
        var path = capability.Path;
        if (!Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0 || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") ||
            !SamePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(path)) ||
            path[Path.GetPathRoot(path)!.Length..].Contains(':')) throw Invalid("路径无效。");
        ValidatePath(root, path, capability.Directory);
        if ((_files.GetAttributes(path) & FileAttributes.Device) != 0) throw Invalid("不支持设备路径。");
        EnsureRootUnchanged(project, item, root);
        ResolveToken(client, project, item, root, entryId);
        return (root, path, capability.Directory);
    }

    internal void InvalidateMutationRoots(params string[] roots)
    {
        // Include overlapping saved roots and every client; a moved ancestor invalidates their path capabilities too.
        bool Overlaps(string a, string b) => SamePath(a, b) ||
            a.StartsWith(Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(Path.EndsInDirectorySeparator(a) ? a : a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        lock (_gate)
            foreach (var id in _tokens.Where(p => roots.Any(root => Overlaps(p.Value.Root, root))).Select(p => p.Key).ToArray()) _tokens.Remove(id);
    }

    // Called only by a bounded background service. Never accepts a web-supplied path.
    internal string ResolveTestDirectory(string client, string project, string item, string folderId)
    {
        ValidateIds(client, project, item, folderId);
        var root = ResolveRoot(project, item);
        var capability = ResolveToken(client, project, item, root, folderId);
        if (!capability.Directory) throw Invalid("该入口不是文件夹。");
        ValidatePath(root, capability.Path, true);
        EnsureRootUnchanged(project, item, root);
        return capability.Path;
    }

    // Thumbnail workers call this outside their cache lock. Capabilities never expose paths to the page.
    internal string ResolveThumbnailEntry(string client, string project, string item, string entryId)
    {
        ValidateIds(client, project, item, entryId);
        var root = ResolveRoot(project, item);
        var capability = ResolveToken(client, project, item, root, entryId);
        if (capability.Directory) throw Invalid("缩略图仅支持文件。");
        var path = capability.Path;
        // This is an already-issued file capability, never a command. Shell keywords
        // and ampersands are ordinary filename characters here.
        if (!Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0 || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") ||
            !SamePath(Path.GetFullPath(path), path) || path[Path.GetPathRoot(path)!.Length..].Contains(':'))
            throw Invalid("文件路径无效。");
        if (Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url" or ".website")
            throw new FolderOperationException(ProtocolErrors.AccessDenied, "链接不提供内容缩略图。");
        ValidatePath(root, path, false);
        if ((_files.GetAttributes(path) & FileAttributes.Device) != 0)
            throw new FolderOperationException(ProtocolErrors.AccessDenied, "设备不提供内容缩略图。");
        EnsureRootUnchanged(project, item, root);
        // A detach during slow file validation must also invalidate this read.
        ResolveToken(client, project, item, root, entryId);
        return path;
    }

    // Immutable saved configuration accompanies the existing directory capability. Child folders never inherit a root command.
    internal (string Directory, LaunchConfiguration? Launch) ResolveProjectTestContext(string client, string project, string item, string folderId)
    {
        var directory = ResolveTestDirectory(client, project, item, folderId);
        var root = ResolveRoot(project, item);
        var saved = _store.Current.Projects.FirstOrDefault(p => p.Id == project)?.Items.FirstOrDefault(i => i.Id == item);
        if (saved is null || !SamePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(saved.Path)), root))
            throw Invalid("目录入口已变更，请重新展开。");
        var launch = SamePath(directory, root) ? saved.Launch : null;
        if (!SamePath(directory, ResolveTestDirectory(client, project, item, folderId)))
            throw Invalid("目录入口已变更，请重新展开。");
        return (directory, launch);
    }

    private FolderListing List(string client, string project, string item, string? folderId, CancellationToken cancellation)
    {
        var root = ResolveRoot(project, item);
        var path = root;
        if (folderId is not null)
        {
            var capability = ResolveToken(client, project, item, root, folderId);
            if (!capability.Directory) throw Invalid("该入口不是文件夹。");
            path = capability.Path;
        }
        ValidatePath(root, path, true);
        var rows = new List<FolderDiskEntry>(201);
        foreach (var row in _files.Enumerate(path))
        {
            cancellation.ThrowIfCancellationRequested();
            rows.Add(row);
            if (rows.Count == 201) break;
        }
        ValidatePath(root, path, true);
        EnsureRootUnchanged(project, item, root);
        cancellation.ThrowIfCancellationRequested();
        var truncated = rows.Count > 200;
        // Bound work even for enormous folders; order only the bounded page.
        rows = rows.Take(200).OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, NaturalNames.Instance).ToList();
        lock (_gate)
        {
            PruneTokens();
            // Reserve a complete page before reusing tokens, so eviction cannot invalidate this response.
            while (_tokens.Count > 4096 - 202) _tokens.Remove(_tokens.First().Key);
            string Issue(string target, bool directory)
            {
                var existing = _tokens.FirstOrDefault(p => p.Value.Client == client && p.Value.Project == project && p.Value.Item == item && SamePath(p.Value.Root, root) && SamePath(p.Value.Path, target) && p.Value.Directory == directory);
                if (existing.Key is not null) return existing.Key;
                var id = Guid.NewGuid().ToString("N");
                _tokens[id] = new(client, project, item, root, target, directory, _clock().AddMinutes(5));
                return id;
            }
            var current = Issue(path, true);
            var parent = SamePath(path, root) ? null : Issue(Path.GetDirectoryName(path)!, true);
            var entries = rows.Select(row => new FolderEntry(Issue(row.Path, row.IsDirectory), row.Name, ShortcutImportService.Classify(row.Path, row.IsDirectory))).ToArray();
            return new(current, BridgeRouter.FolderDisplayName(path), entries, parent, truncated);
        }
    }

    private string ResolveRoot(string projectId, string itemId)
    {
        if (_store.LoadError is { } error) throw new FolderOperationException(ProtocolErrors.IoError, error);
        var item = _store.Current.Projects.FirstOrDefault(p => p.Id == projectId)?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null) throw new FolderOperationException(ProtocolErrors.PathNotFound, "该目录入口已被删除，请重新选择。");
        if (item.Kind != "folder") throw Invalid("该入口不是文件夹。");
        if (PathRules.Validate(item.Path) is { } invalid) throw Invalid(invalid);
        if (item.Path.StartsWith(@"\\?\", StringComparison.Ordinal) || item.Path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw Invalid("不支持设备路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path));
    }

    private void EnsureRootUnchanged(string project, string item, string root)
    {
        if (!SamePath(ResolveRoot(project, item), root)) throw Invalid("目录入口已变更，请重新展开。");
    }

    private Capability ResolveToken(string client, string project, string item, string root, string id)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(id) || !_tokens.TryGetValue(id, out var cap) || cap.Expires <= _clock() || cap.Client != client || cap.Project != project || cap.Item != item || !SamePath(cap.Root, root))
                throw Invalid("目录内容已过期或入口不匹配，请重新展开。");
            return cap;
        }
    }

    private void ValidatePath(string root, string path, bool directory)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!SamePath(path, root) && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw Invalid("目录入口越过了已保存的根目录。");
        // Check every component, including ancestors of the saved root: junction substitution is rejected.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = _files.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new FolderOperationException(ProtocolErrors.AccessDenied, "此路径包含链接或联接点，请将真实目标目录另存为入口。");
            if (SamePath(current, path) && ((attributes & FileAttributes.Directory) != 0) != directory)
                throw Invalid(directory ? "该路径已不再是文件夹。" : "该文件已变为文件夹，请重新展开。");
        }
    }

    private async Task<T> RunBounded<T>(Func<CancellationToken, T> action)
    {
        if (!await Workers.WaitAsync(0)) throw new FolderOperationException(ProtocolErrors.Busy, "目录仍在读取中，请稍后重试。");
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var work = Task.Run(() =>
        {
            try { return action(token); }
            finally { Workers.Release(); }
        });
        try { return await work.WaitAsync(_timeout); }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            _ = work.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            throw new FolderOperationException(ProtocolErrors.Busy, "目录响应超时，请检查磁盘或网络后重试。");
        }
        catch (UnauthorizedAccessException) { throw new FolderOperationException(ProtocolErrors.AccessDenied, "没有权限读取或打开该目录。"); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw new FolderOperationException(ProtocolErrors.PathNotFound, "目录或文件已移动、删除，请重新展开。"); }
        catch (IOException) { throw new FolderOperationException(ProtocolErrors.IoError, "目录暂时无法读取，请检查磁盘或网络后重试。"); }
    }

    private void PruneTokens()
    {
        foreach (var id in _tokens.Where(p => p.Value.Expires <= _clock()).Select(p => p.Key).ToArray()) _tokens.Remove(id);
    }
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static FolderOperationException Invalid(string message) => new(ProtocolErrors.InvalidRequest, message);
    private static void ValidateIds(params string[] ids)
    {
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 200)) throw Invalid("目录入口编号无效。");
    }
    private sealed class NaturalNames : IComparer<string>
    {
        public static readonly NaturalNames Instance = new();
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int StrCmpLogicalW(string first, string second);
        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
    }
}
