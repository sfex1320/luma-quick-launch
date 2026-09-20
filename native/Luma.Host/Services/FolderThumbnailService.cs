using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

/// <summary>Static Shell thumbnails only; all capability/file IO and extraction run on two bounded STA workers.</summary>
public sealed class FolderThumbnailService
{
    private sealed record Request(string Client, string Project, string Item, string Entry, int Size);
    private sealed record Key(string Path, long Length, long Modified, int Size);
    private sealed record Cached(string? Data, DateTimeOffset Expires);
    private static readonly Lazy<BlockingCollection<Action>> Queue = new(() =>
    {
        var queue = new BlockingCollection<Action>(128);
        for (var i = 0; i < 2; i++)
        {
            var thread = new Thread(() => { foreach (var work in queue.GetConsumingEnumerable()) work(); })
                { IsBackground = true, Name = $"Luma folder thumbnails {i}" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        return queue;
    });
    private readonly object _gate = new();
    private readonly FolderService _folders;
    private readonly Func<string, int, string?> _extract;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<Request, Task<string?>> _pending = new();
    private readonly Dictionary<Key, Cached> _cache = new();
    private int _characters;

    public FolderThumbnailService(FolderService folders, Func<string, int, string?>? extract = null, TimeSpan? timeout = null)
    { _folders = folders; _extract = extract ?? Extract; _timeout = timeout ?? TimeSpan.FromSeconds(4); }

    public async Task<string?> GetAsync(string client, string project, string item, string entry, int size = 64)
    {
        if (new[] { client, project, item, entry }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 200) || size is not (64 or 96 or 128))
            throw new ArgumentException("缩略图入口编号或尺寸无效。");
        var request = new Request(client, project, item, entry, size);
        Task<string?> work;
        lock (_gate)
        {
            if (!_pending.TryGetValue(request, out work!))
            {
                if (_pending.Count >= 128) return null;
                var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                work = completion.Task;
                _pending.Add(request, work);
                var started = Stopwatch.GetTimestamp();
                if (!Queue.Value.TryAdd(() => Process(request, started, completion)))
                { _pending.Remove(request); return null; }
            }
        }
        // Timeouts retain the occupied worker/pending slot. No replacement threads or late queued extraction.
        try { return await work.WaitAsync(_timeout); }
        catch (TimeoutException) { return null; }
    }

    private void Process(Request request, long started, TaskCompletionSource<string?> completion)
    {
        string? data = null;
        Exception? error = null;
        bool Expired() => Stopwatch.GetElapsedTime(started) >= _timeout;
        try
        {
            if (Expired()) return;
            var path = _folders.ResolveThumbnailEntry(request.Client, request.Project, request.Item, request.Entry);
            var key = Snapshot(path, request.Size);
            if (Expired()) return;
            bool found;
            lock (_gate)
            {
                found = _cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow;
                if (found) data = cached!.Data;
            }
            if (!found)
            {
                try { data = _extract(path, request.Size); }
                catch (Exception ex) { Log.Info($"内容缩略图不可用：{ex.GetType().Name}"); }
                if (!IsPng(data, request.Size)) data = null;
            }
            if (Expired()) { data = null; return; }
            // Never return/cache an image after deletion, root change, link substitution, token expiry or detach.
            var current = _folders.ResolveThumbnailEntry(request.Client, request.Project, request.Item, request.Entry);
            if (key != Snapshot(current, request.Size) || Expired()) { data = null; return; }
            if (!found)
            {
                lock (_gate)
                {
                    Remove(key);
                    while (_cache.Count >= 128 || _characters + (data?.Length ?? 0) > 6_000_000) Remove(_cache.First().Key);
                    _cache[key] = new(data, DateTimeOffset.UtcNow.AddSeconds(data is null ? 5 : 300));
                    _characters += data?.Length ?? 0;
                }
            }
        }
        catch (FolderOperationException ex) { error = ex; data = null; }
        catch (UnauthorizedAccessException) { error = new FolderOperationException(ProtocolErrors.AccessDenied, "没有权限读取该文件。"); data = null; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { error = new FolderOperationException(ProtocolErrors.PathNotFound, "文件已移动或删除，请重新展开。"); data = null; }
        catch (IOException) { error = new FolderOperationException(ProtocolErrors.IoError, "文件暂时无法读取。"); data = null; }
        catch (Exception ex) { Log.Info($"内容缩略图不可用：{ex.GetType().Name}"); data = null; }
        finally
        {
            lock (_gate) _pending.Remove(request);
            if (error is not null && !Expired()) completion.TrySetException(error);
            else completion.TrySetResult(Expired() ? null : data);
        }
    }

    private static Key Snapshot(string path, int size)
    {
        var file = new FileInfo(path);
        return new(path, file.Length, file.LastWriteTimeUtc.Ticks, size);
    }
    private void Remove(Key key) { if (_cache.Remove(key, out var value)) _characters -= value.Data?.Length ?? 0; }
    internal (int Entries, int Characters, int Pending) CacheUsage { get { lock (_gate) return (_cache.Count, _characters, _pending.Count); } }

    internal static bool IsPng(string? data, int maximum)
    {
        if (data is null || data.Length > 350000 || !data.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) return false;
        try
        {
            var bytes = Convert.FromBase64String(data[22..]);
            if (bytes.Length is < 33 or > 262000 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return false;
            var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > maximum || height > maximum) return false;
            using var stream = new MemoryStream(bytes, false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count == 1 && decoder.Frames[0].PixelWidth == width && decoder.Frames[0].PixelHeight == height;
        }
        catch (Exception) { return false; }
    }

    private static string? Extract(string path, int size)
    {
        object? item = null;
        var bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
            // SIIGBF_THUMBNAILONLY | RESIZETOFIT: no generic icon and no optional codec installation.
            // PSD/AI/CDR (and videos/documents) depend entirely on registered Windows Shell providers.
            if (((IShellItemImageFactory)item).GetImage(new NativeSize { Width = size, Height = size }, 0x8, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
            if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var dimensions) == 0 ||
                dimensions.Width <= 0 || dimensions.Height <= 0 || dimensions.Width > size || dimensions.Height > size) return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.Length <= 262000 ? "data:image/png;base64," + Convert.ToBase64String(stream.ToArray()) : null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width; public int Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
}
