using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Luma.Host.Bridge;
using Microsoft.Win32.SafeHandles;

namespace Luma.Host.Services;

public sealed record FolderMutationResult(int ChangedCount, bool Completed, string? ErrorCode = null, string? Message = null);

/// <summary>
/// Explicit disk operations only. Inputs are saved-root capabilities, never caller-supplied paths.
/// Two background slots, no waiting queue. Native handles pin ancestors against rename/delete;
/// mutations use a directory handle plus one validated relative name, never re-traverse an absolute path.
/// Renames use the opened source with ReplaceIfExists=false, never copy/delete fallback.
/// </summary>
public sealed partial class FolderMutationService
{
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private readonly FolderService _folders;
    private readonly TimeSpan _timeout;
    public FolderMutationService(FolderService folders, TimeSpan? timeout = null)
    { _folders = folders; _timeout = timeout ?? TimeSpan.FromSeconds(4); }

    public Task<string> GetPathAsync(string client, string project, string item, string entryId) => Run(deadline =>
    {
        var entry = _folders.ResolveMutationEntry(client, project, item, entryId);
        // Copying an address does not touch content and must also work while an editor has the file open.
        var current = _folders.ResolveMutationEntry(client, project, item, entryId);
        if (entry != current) throw Invalid("目录入口已变更，请重新展开。");
        deadline.Check();
        return entry.Path;
    });

    public Task<FolderMutationResult> CreateFolderAsync(string client, string project, string item, string folderId, string name)
    {
        ValidateName(name);
        return Run(deadline =>
        {
            var folder = _folders.ResolveMutationEntry(client, project, item, folderId);
            if (!folder.Directory) throw Invalid("只能在真实文件夹内新建文件夹。");
            using var lease = new PathLease();
            var parent = lease.Pin(folder.Path, true);
            var target = Child(folder.Path, name);
            EnsureAbsent(target);
            if (folder != _folders.ResolveMutationEntry(client, project, item, folderId)) throw Invalid("目录入口已变更，请重新展开。");
            lease.Verify();
            deadline.StartCommit();
            CreateChildDirectory(parent.Handle, name);
            _folders.InvalidateMutationRoots(folder.Root);
            return new FolderMutationResult(1, true);
        });
    }

    public Task<FolderMutationResult> RenameAsync(string client, string project, string item, string entryId, string name)
    {
        ValidateName(name);
        return Run(deadline =>
        {
            var entry = _folders.ResolveMutationEntry(client, project, item, entryId);
            EnsureNotRoot(entry.Root, entry.Path);
            var parent = Path.GetDirectoryName(entry.Path)!;
            var target = Child(parent, name);
            if (Same(entry.Path, target)) throw Invalid("新名称与当前名称相同。");
            using var lease = new PathLease();
            var source = lease.Pin(entry.Path, entry.Directory, true);
            var parentHandle = lease.Pin(parent, true);
            EnsureAbsent(target);
            if (entry != _folders.ResolveMutationEntry(client, project, item, entryId)) throw Invalid("目录入口已变更，请重新展开。");
            lease.Verify();
            deadline.StartCommit();
            RenameHandle(source.Handle, parentHandle.Handle, name);
            _folders.InvalidateMutationRoots(entry.Root);
            return new FolderMutationResult(1, true);
        });
    }

    public Task<FolderMutationResult> MoveAsync(string client, string sourceProject, string sourceItem, IReadOnlyList<string> sourceEntryIds,
        string targetProject, string targetItem, string targetFolderId)
    {
        if (sourceEntryIds is null || sourceEntryIds.Count is < 1 or > 100 || sourceEntryIds.Any(string.IsNullOrWhiteSpace) ||
            sourceEntryIds.Any(id => id.Length > 200) || sourceEntryIds.Distinct(StringComparer.Ordinal).Count() != sourceEntryIds.Count)
            throw Invalid("每次请选择 1–100 个不同的真实目录项。");
        var ids = sourceEntryIds.ToArray();
        return Run(deadline =>
        {
            var target = _folders.ResolveMutationEntry(client, targetProject, targetItem, targetFolderId);
            if (!target.Directory) throw Invalid("移动目标必须是真实文件夹。");
            var sources = ids.Select(id => _folders.ResolveMutationEntry(client, sourceProject, sourceItem, id)).ToArray();
            foreach (var source in sources)
            {
                EnsureNotRoot(source.Root, source.Path);
                if (source.Directory && Contains(source.Path, target.Path)) throw Invalid("不能把文件夹移动到自身或其子目录。");
                if (sources.Any(other => !Same(other.Path, source.Path) && other.Directory && Contains(other.Path, source.Path)))
                    throw Invalid("不能同时移动父文件夹与其内部条目。");
            }
            if (sources.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length) throw Invalid("移动来源重复。");
            var destinations = sources.Select(source => Child(target.Path, Path.GetFileName(source.Path))).ToArray();
            if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length) throw Invalid("多个来源具有相同名称，请分别处理。");
            using var lease = new PathLease();
            var targetHandle = lease.Pin(target.Path, true);
            var handles = sources.Select(source => lease.Pin(source.Path, source.Directory, true)).ToArray();
            if (handles.Any(handle => handle.Volume != targetHandle.Volume)) throw Invalid("目前只支持同一磁盘卷内移动；请在资源管理器中处理跨卷移动。");
            foreach (var path in destinations) EnsureAbsent(path);
            if (target != _folders.ResolveMutationEntry(client, targetProject, targetItem, targetFolderId)) throw Invalid("目标目录已变更，请重新展开。");
            for (var i = 0; i < ids.Length; i++)
                if (sources[i] != _folders.ResolveMutationEntry(client, sourceProject, sourceItem, ids[i])) throw Invalid("来源目录已变更，请重新展开。");
            lease.Verify();
            deadline.StartCommit();
            var changed = 0;
            try
            {
                for (var i = 0; i < handles.Length; i++)
                {
                    RenameHandle(handles[i].Handle, targetHandle.Handle, Path.GetFileName(destinations[i]));
                    changed++;
                }
                return new FolderMutationResult(changed, true);
            }
            catch (FolderOperationException ex) when (changed > 0)
            {
                // Never roll back by overwriting another concurrent change. The caller must refresh both roots.
                return new FolderMutationResult(changed, false, ex.Code, $"已移动 {changed} 项；其余未移动。{ex.Message}");
            }
            finally
            {
                if (changed > 0) _folders.InvalidateMutationRoots(sources.Select(s => s.Root).Append(target.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
        });
    }

    private async Task<T> Run<T>(Func<Deadline, T> action)
    {
        if (!await Workers.WaitAsync(0)) throw Busy();
        var deadline = new Deadline(_timeout);
        var task = Task.Run(() =>
        {
            try { deadline.Check(); return action(deadline); }
            catch (UnauthorizedAccessException) { throw new FolderOperationException(ProtocolErrors.AccessDenied, "没有权限修改该目录。"); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            { throw new FolderOperationException(ProtocolErrors.PathNotFound, "目录或文件已移动、删除，请重新展开。"); }
            catch (IOException) { throw new FolderOperationException(ProtocolErrors.IoError, "目录操作失败，请检查文件占用和磁盘状态。"); }
            finally { Workers.Release(); }
        });
        try { return await task.WaitAsync(_timeout); }
        catch (TimeoutException)
        {
            if (!deadline.CancelBeforeCommit()) return await task;
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            throw Busy();
        }
    }

    private sealed class Deadline(TimeSpan timeout)
    {
        private readonly object _gate = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _committing, _cancelled;
        public void Check() { lock (_gate) if (_cancelled || (!_committing && Stopwatch.GetElapsedTime(_started) >= timeout)) throw Busy(); }
        public void StartCommit() { lock (_gate) { Check(); _committing = true; } }
        public bool CancelBeforeCommit() { lock (_gate) { if (_committing) return false; _cancelled = true; return true; } }
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))) throw Invalid("名称不可为空、包含路径分隔符或非法字符，也不能以空格或句点结尾。");
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw Invalid("不能使用 Windows 保留设备名称。");
    }
    private static string Child(string parent, string name)
    {
        ValidateName(name);
        var path = Path.Combine(parent, name);
        if (path.Length > 4096 || !Same(Path.GetDirectoryName(Path.GetFullPath(path))!, parent)) throw Invalid("目标路径无效或过长。");
        return path;
    }
    private static void EnsureNotRoot(string root, string path) { if (Same(root, path)) throw Invalid("不能重命名或移动已绑定的根目录；请修改快捷引用。"); }
    private static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    private static bool Contains(string parent, string child) => Same(parent, child) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void EnsureAbsent(string target)
    {
        try { _ = File.GetAttributes(target); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        throw Invalid("目标目录已存在同名文件或文件夹，不会覆盖。");
    }
    private static FolderOperationException Invalid(string message) => new(ProtocolErrors.InvalidRequest, message);
    private static FolderOperationException Busy() => new(ProtocolErrors.Busy, "目录仍在读取或操作中，请稍后刷新重试。");
    private static FolderOperationException NativeError(int? nativeCode = null)
    {
        var code = nativeCode ?? Marshal.GetLastWin32Error();
        return code switch
        {
            2 or 3 => new(ProtocolErrors.PathNotFound, "目录或文件已移动、删除，请重新展开。"),
            5 => new(ProtocolErrors.AccessDenied, "没有权限修改该目录。"),
            32 or 33 => new(ProtocolErrors.Busy, "文件或目录正在被其他程序使用，请稍后重试。"),
            80 or 183 => Invalid("目标目录已存在同名文件或文件夹，不会覆盖。"),
            17 => Invalid("目前只支持同一磁盘卷内移动。"),
            _ => new(ProtocolErrors.IoError, $"目录操作失败（系统错误 {code}）。"),
        };
    }

    private sealed record Pinned(SafeFileHandle Handle, uint Volume);
    private sealed class PathLease : IDisposable
    {
        private readonly Dictionary<string, Pinned> _handles = new(StringComparer.OrdinalIgnoreCase);
        public Pinned Pin(string path, bool directory, bool deleteAccess = false, bool readOnly = false) => PinCore(path, directory, deleteAccess, 0, readOnly);
        private Pinned PinCore(string path, bool directory, bool deleteAccess, int depth, bool readOnly = false)
        {
            path = Path.TrimEndingDirectorySeparator(path);
            if (_handles.TryGetValue(path, out var existing)) return existing;
            if (depth >= 128 || _handles.Count >= 256 || path.Length > 4096) throw Invalid("路径层级或一次操作的目录过多。");
            var parent = Path.GetDirectoryName(path);
            if (parent is not null) { ValidateName(Path.GetFileName(path)); PinCore(parent, true, false, depth + 1); }
            if (_handles.Count >= 256) throw Invalid("一次操作的目录过多。");
            // OPEN_REPARSE_POINT checks the object itself; root-to-leaf pinning prevents traversal races.
            // Omit SHARE_DELETE so the root-to-leaf chain cannot be renamed or substituted.
            // Ancestors permit writes required by the kernel's relative rename; they are never
            // re-traversed by mutation. Source handles additionally disallow write access.
            // FILE_READ_DATA / FILE_LIST_DIRECTORY is essential: metadata-only handles do not participate
            // in Windows share-access enforcement and therefore cannot protect against substitution.
            var handle = CreateFile(path, 0x81u | (deleteAccess ? 0x10000u : 0), deleteAccess || readOnly ? 0x1u : 0x3u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw NativeError(); }
            try
            {
                if (GetFileType(handle) != 1 || !GetFileInformationByHandle(handle, out var info)) throw NativeError();
                if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Device)) != 0)
                    throw new FolderOperationException(ProtocolErrors.AccessDenied, "不允许修改链接、联接点或设备路径。");
                if (((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory) throw Invalid("目录项类型已变更，请重新展开。");
                var buffer = new StringBuilder(8192);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw NativeError();
                var actual = buffer.ToString();
                if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
                else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
                if (!Same(actual, path)) throw Invalid("路径已改变或包含别名，请重新选择真实目录。");
                var pinned = new Pinned(handle, info.VolumeSerialNumber);
                _handles.Add(path, pinned);
                return pinned;
            }
            catch { handle.Dispose(); throw; }
        }
        public void Verify()
        {
            foreach (var pinned in _handles.Values)
            {
                if (!GetFileInformationByHandle(pinned.Handle, out var info)) throw NativeError();
                if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Device)) != 0)
                    throw new FolderOperationException(ProtocolErrors.AccessDenied, "路径已变为链接、联接点或设备，请重新展开。");
            }
        }
        public void Dispose() { foreach (var handle in _handles.Values.Reverse()) handle.Handle.Dispose(); _handles.Clear(); }
    }

    private static void RenameHandle(SafeFileHandle source, SafeFileHandle targetDirectory, string name)
    {
        // FILE_RENAME_INFO has architecture-dependent alignment. FileName is an inline UTF-16 array.
        var encodedName = Encoding.Unicode.GetBytes(name);
        var nameOffset = (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.FirstCharacter));
        var size = nameOffset + encodedName.Length + 2;
        var data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, data, size);
            Marshal.WriteIntPtr(data, (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.Root)), targetDirectory.DangerousGetHandle());
            Marshal.WriteInt32(data, (int)Marshal.OffsetOf<RenameInfo>(nameof(RenameInfo.NameLength)), encodedName.Length);
            Marshal.Copy(encodedName, 0, IntPtr.Add(data, nameOffset), encodedName.Length);
            var status = NtSetInformationFile(source, out _, data, (uint)size, 10);
            if (status < 0) throw NativeError((int)RtlNtStatusToDosError(status));
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    private static void CreateChildDirectory(SafeFileHandle parent, string name)
    {
        var text = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = text }, unicode, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = unicode, Attributes = 0x40 };
            // FILE_CREATE, never OPEN_IF; the name contains no separator. Root is an existing file object,
            // so even an external reparse mutation cannot redirect this operation to its target.
            var status = NtCreateFile(out var created, 0x100081, ref attributes, out _, IntPtr.Zero, 0x10, 7, 2, 0x200021, IntPtr.Zero, 0);
            using (created) { if (status < 0) throw NativeError((int)RtlNtStatusToDosError(status)); }
        }
        finally { Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RenameInfo { public uint Flags; public IntPtr Root; public uint NameLength; public char FirstCharacter; }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public IntPtr Status; public UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus status, IntPtr information, uint length, int informationClass);
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatus status, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}
