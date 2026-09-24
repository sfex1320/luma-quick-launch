using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Luma.Host.Bridge;
using Microsoft.Win32.SafeHandles;

namespace Luma.Host.Services;

public sealed partial class FolderMutationService
{
    private const int TransferEntryLimit = 200;
    private const int TransferDepthLimit = 16;
    private const long TransferByteLimit = 2L * 1024 * 1024 * 1024;
    private sealed record TransferStream(string Name, SafeFileHandle Source, long Length);
    private sealed record TransferEntry(string Path, bool Directory, Pinned Source, long Length, List<TransferEntry> Children, List<TransferStream> Streams);
    private sealed class TransferStreamLease : IDisposable
    {
        public readonly List<SafeFileHandle> Handles = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }

    /// <summary>
    /// Paths must come only from native WebView file objects or revalidated saved IDs, never JSON paths.
    /// Copy/move/link is an explicit disk action. The callback revalidates the source capabilities before commit.
    /// A changed item includes a partly copied top-level entry: errors never conceal a filesystem change.
    /// </summary>
    public Task<FolderMutationResult> TransferAsync(string client, string targetProject, string targetItem,
        string targetFolderId, IReadOnlyList<string> sourcePaths, string operation, Action? validateSources = null)
    {
        if (operation is not ("copy" or "move" or "link")) throw Invalid("仅支持复制、移动或创建快捷方式。");
        if (sourcePaths is null || sourcePaths.Count is < 1 or > 100) throw Invalid("每次请选择 1–100 个文件或文件夹。");
        var paths = sourcePaths.Select(ValidateTransferPath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw Invalid("传输来源重复。");
        return Run(deadline =>
        {
            validateSources?.Invoke();
            var target = _folders.ResolveMutationEntry(client, targetProject, targetItem, targetFolderId);
            if (!target.Directory) throw Invalid("传输目标必须是真实文件夹。");
            using var lease = new PathLease();
            using var streamLease = new TransferStreamLease();
            var targetHandle = lease.Pin(target.Path, true);
            var count = 0; long bytes = 0;
            var sources = paths.Select(path => ReadTransferEntry(path, 0, operation, lease, streamLease, deadline, ref count, ref bytes)).ToArray();
            foreach (var source in sources)
            {
                if (source.Directory && Contains(source.Path, target.Path)) throw Invalid("不能传输文件夹到自身或其子目录。");
                if (sources.Any(other => !Same(other.Path, source.Path) && other.Directory && Contains(other.Path, source.Path)))
                    throw Invalid("不能同时传输父文件夹和其中的条目。");
            }
            if (operation == "move" && sources.Any(s => s.Source.Volume != targetHandle.Volume))
                throw Invalid("目前只支持同一磁盘卷内移动；请在资源管理器中处理跨卷移动。");
            var names = paths.Select(path => Path.GetFileName(path) + (operation == "link" ? ".lnk" : "")).ToArray();
            var destinations = names.Select(name => Child(target.Path, name)).ToArray();
            if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length) throw Invalid("多个来源具有相同名称，请分别处理。");
            foreach (var destination in destinations) EnsureAbsent(destination);
            // Build Shell Link data in memory before any destination file is created. SetPath does not launch or resolve it.
            var links = operation == "link" ? paths.Select(CreateShortcutBytes).ToArray() : null;
            validateSources?.Invoke();
            if (target != _folders.ResolveMutationEntry(client, targetProject, targetItem, targetFolderId)) throw Invalid("目标目录已变更，请重新展开。");
            lease.Verify();
            deadline.StartCommit();
            var changed = 0;
            try
            {
                for (var i = 0; i < sources.Length; i++)
                {
                    if (operation == "move")
                    {
                        RenameHandle(sources[i].Source.Handle, targetHandle.Handle, names[i]);
                        changed++;
                    }
                    else if (operation == "link")
                    {
                        using var destination = CreateTransferChild(targetHandle.Handle, names[i], false);
                        changed++;
                        RandomAccess.Write(destination, links![i], 0);
                        RandomAccess.FlushToDisk(destination);
                    }
                    else CopyTransferEntry(sources[i], targetHandle.Handle, names[i], () => changed++);
                }
                return new FolderMutationResult(changed, true);
            }
            catch (Exception ex) when (changed > 0 && ex is FolderOperationException or IOException or UnauthorizedAccessException or COMException)
            {
                var code = ex is FolderOperationException folderError ? folderError.Code : ex is UnauthorizedAccessException ? ProtocolErrors.AccessDenied : ProtocolErrors.IoError;
                return new FolderMutationResult(changed, false, code, $"已改动 {changed} 项；其中可能含未复制完整的项目，请刷新检查。{ex.Message}");
            }
            finally
            {
                if (changed > 0) _folders.InvalidateMutationRoots(paths.Append(target.Root).ToArray());
            }
        });
    }

    private static string ValidateTransferPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.IndexOf('\0') >= 0 ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\")) throw Invalid("来源文件路径无效。");
        var root = Path.GetPathRoot(path)!;
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Same(normalized, path) || path[root.Length..].Contains(':') || Same(root, path)) throw Invalid("不支持磁盘根目录、设备或别名路径。");
        ValidateName(Path.GetFileName(normalized));
        return normalized;
    }

    private static TransferEntry ReadTransferEntry(string path, int depth, string operation, PathLease lease, TransferStreamLease streamLease, Deadline deadline, ref int count, ref long bytes)
    {
        deadline.Check();
        if (++count > TransferEntryLimit || depth > TransferDepthLimit) throw Invalid("一次传输最多包含 200 个条目，文件夹深度最多 16 层。");
        var attrs = File.GetAttributes(path);
        var directory = (attrs & FileAttributes.Directory) != 0;
        if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new FolderOperationException(ProtocolErrors.AccessDenied, "不允许传输链接、联接点或设备路径。");
        var pinned = lease.Pin(path, directory, operation == "move", operation == "copy");
        if (!GetFileInformationByHandle(pinned.Handle, out var info)) throw NativeError();
        var size = directory ? 0 : ((long)info.SizeHigh << 32) | info.SizeLow;
        var children = new List<TransferEntry>();
        var streams = new List<TransferStream>();
        if (operation == "copy")
        {
            if ((attrs & FileAttributes.Encrypted) != 0) throw Invalid("暂不支持复制 EFS 加密文件，请使用资源管理器处理。");
            bytes = checked(bytes + size);
            if (bytes > TransferByteLimit) throw Invalid("一次复制的数据总量最多为 2 GiB，请分批处理。");
            streams = ReadTransferStreams(path, pinned, streamLease, deadline, ref bytes);
            if (directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                    children.Add(ReadTransferEntry(Child(path, Path.GetFileName(child)), depth + 1, operation, lease, streamLease, deadline, ref count, ref bytes));
        }
        return new(path, directory, pinned, size, children, streams);
    }

    private static void CopyTransferEntry(TransferEntry source, SafeFileHandle parent, string name, Action? created = null)
    {
        using var target = CreateTransferChild(parent, name, source.Directory);
        created?.Invoke();
        if (source.Directory)
        {
            foreach (var child in source.Children) CopyTransferEntry(child, target, Path.GetFileName(child.Path));
        }
        else CopyTransferBytes(source.Source.Handle, target, source.Length);
        foreach (var stream in source.Streams)
        {
            // The new base entry remains pinned without SHARE_DELETE; its stream cannot target a substituted file.
            using var destinationStream = CreateTransferChild(parent, name, false, stream.Name);
            CopyTransferBytes(stream.Source, destinationStream, stream.Length);
        }
    }

    private static void CopyTransferBytes(SafeFileHandle source, SafeFileHandle target, long length)
    {
        // Read through the pinned source handle; never resolve its path again after validation.
        var buffer = new byte[128 * 1024];
        long offset = 0;
        while (offset < length)
        {
            var read = RandomAccess.Read(source, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
            if (read == 0) throw new IOException("来源文件在复制期间长度发生变化。");
            RandomAccess.Write(target, buffer.AsSpan(0, read), offset);
            offset += read;
        }
        RandomAccess.FlushToDisk(target);
    }

    private static SafeFileHandle CreateTransferChild(SafeFileHandle parent, string name, bool directory, string? streamName = null)
    {
        ValidateName(name);
        if (streamName is not null) { ValidateTransferStreamName(streamName); name += streamName; }
        var text = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = text }, unicode, false);
            var attrs = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = unicode, Attributes = 0x40 };
            // FILE_CREATE and one relative component; no overwrite, reparse traversal, rename, or substitution.
            var status = NtCreateFile(out var result, directory ? 0x100081u : 0x100002u, ref attrs, out _, IntPtr.Zero,
                directory ? 0x10u : 0x80u, 1, 2, directory ? 0x200021u : 0x200060u, IntPtr.Zero, 0);
            if (status < 0) { result.Dispose(); throw NativeError((int)RtlNtStatusToDosError(status)); }
            return result;
        }
        finally { Marshal.FreeHGlobal(unicode); Marshal.FreeHGlobal(text); }
    }

    private static void ValidateTransferStreamName(string name)
    {
        if (name.Length < 8 || !name.StartsWith(':') || !name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase))
            throw Invalid("不支持该命名数据流。");
        ValidateName(name[1..^6]);
    }

    private static List<TransferStream> ReadTransferStreams(string path, Pinned source, TransferStreamLease lease, Deadline deadline, ref long bytes)
    {
        var result = new List<TransferStream>();
        var search = FindFirstStream(path, 0, out var data, 0);
        if (search == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return result; // Empty files/directories can have no streams.
            throw NativeError(error);
        }
        try
        {
            do
            {
                deadline.Check();
                if (data.Name == "::$DATA") continue;
                ValidateTransferStreamName(data.Name);
                if (result.Count >= 32 || lease.Handles.Count >= 128) throw Invalid("每个条目最多复制 32 个命名流，每批最多 128 个命名流。");
                // Both the base file and all its ancestors are already pinned. A stream handle also
                // denies writes/deletion, and its file identity must match the pinned base object.
                var handle = CreateFile(path + data.Name, 0x81, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw NativeError(); }
                lease.Handles.Add(handle);
                if (!GetFileInformationByHandle(handle, out var info) || !GetFileInformationByHandle(source.Handle, out var baseInfo)) throw NativeError();
                if (info.VolumeSerialNumber != baseInfo.VolumeSerialNumber || info.IndexHigh != baseInfo.IndexHigh || info.IndexLow != baseInfo.IndexLow ||
                    (info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Device)) != 0) throw Invalid("命名流来源已变更。");
                var length = RandomAccess.GetLength(handle);
                bytes = checked(bytes + length);
                if (bytes > TransferByteLimit) throw Invalid("一次复制的数据总量最多为 2 GiB，请分批处理。");
                result.Add(new(data.Name, handle, length));
            } while (FindNextStream(search, out data));
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 38) throw NativeError(lastError);
        }
        finally { FindClose(search); }
        return result;
    }

    private static byte[] CreateShortcutBytes(string path)
    {
        var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
        IStream? stream = null;
        try
        {
            ((ITransferShellLink)obj).SetPath(path);
            Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(IntPtr.Zero, true, out stream));
            ((ITransferPersistStream)obj).Save(stream, true);
            stream.Stat(out var stat, 1);
            if (stat.cbSize is < 1 or > 1024 * 1024) throw Invalid("快捷方式数据大小无效。");
            var bytes = new byte[(int)stat.cbSize];
            stream.Seek(0, 0, IntPtr.Zero);
            var read = Marshal.AllocHGlobal(sizeof(int));
            try { stream.Read(bytes, bytes.Length, read); if (Marshal.ReadInt32(read) != bytes.Length) throw new IOException("快捷方式数据读取不完整。"); }
            finally { Marshal.FreeHGlobal(read); }
            return bytes;
        }
        catch (COMException) { throw new FolderOperationException(ProtocolErrors.IoError, "无法创建 Windows 快捷方式。"); }
        finally
        {
            if (stream is not null) Marshal.FinalReleaseComObject(stream);
            Marshal.FinalReleaseComObject(obj);
        }
    }

    [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(IntPtr memory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IStream stream);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TransferStreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out TransferStreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStream(IntPtr find, out TransferStreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr find);
    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferPersistStream
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load(IStream stream);
        void Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        void GetSizeMax(out long size);
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITransferShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
