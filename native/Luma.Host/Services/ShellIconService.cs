using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Luma.Host.Bridge;

namespace Luma.Host.Services;

/// <summary>Read saved entries only. Two background STA workers and a bounded queue keep Shell handlers off the UI thread.</summary>
public sealed class ShellIconService
{
    private sealed record Key(long Revision, string Project, string Item, string Path, int Size);
    private sealed record Cached(string? DataUrl, DateTimeOffset Expires);
    private static readonly Lazy<BlockingCollection<Action>> WorkQueue = new(() =>
    {
        var queue = new BlockingCollection<Action>(128);
        for (var i = 0; i < 2; i++)
        {
            var thread = new Thread(() => { foreach (var work in queue.GetConsumingEnumerable()) work(); })
                { IsBackground = true, Name = $"Luma Shell icons {i}" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        return queue;
    });
    private readonly object _gate = new();
    private readonly StateStore _store;
    private readonly Func<string, int, string?> _extract;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<Key, Cached> _cache = new();
    private readonly Dictionary<Key, Task<string?>> _pending = new();
    private int _cachedCharacters;

    public ShellIconService(StateStore store, Func<string, int, string?>? extract = null, TimeSpan? timeout = null)
    { _store = store; _extract = extract ?? Extract; _timeout = timeout ?? TimeSpan.FromSeconds(4); }

    public async Task<string?> GetAsync(string projectId, string itemId, int size = 64)
    {
        if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 200 || string.IsNullOrWhiteSpace(itemId) || itemId.Length > 200 || size is not (32 or 48 or 64 or 96))
            throw new ArgumentException("图标入口编号或尺寸无效。");
        var state = _store.Current;
        var item = state.Projects.FirstOrDefault(p => p.Id == projectId)?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item?.Kind == "url") return IsPngDataUrl(item.WebsiteIcon) ? item.WebsiteIcon : null;
        if (_store.LoadError is not null || item is null || item.Kind == "folder" || PathRules.Validate(item.Path) is not null || item.Path.StartsWith(@"\\?\") || item.Path.StartsWith(@"\\.\")) return null;
        var key = new Key(state.Revision, projectId, itemId, item.Path, size);
        Task<string?> task;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.DataUrl;
            if (!_pending.TryGetValue(key, out task!))
            {
                if (_pending.Count >= 128) return null;
                var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task;
                _pending[key] = task;
                if (!WorkQueue.Value.TryAdd(() =>
                {
                    string? result = null;
                    try { result = _extract(key.Path, key.Size); }
                    catch (Exception ex) { Log.Info($"图标提取不可用：{ex.GetType().Name}"); }
                    if (!IsPngDataUrl(result)) result = null;
                    lock (_gate)
                    {
                        _pending.Remove(key);
                        var current = _store.Current;
                        if (current.Revision != key.Revision || current.Projects.FirstOrDefault(p => p.Id == key.Project)?.Items.FirstOrDefault(i => i.Id == key.Item)?.Path != key.Path) result = null;
                        else
                        {
                            RemoveCached(key);
                            while (_cache.Count >= 128 || _cachedCharacters + (result?.Length ?? 0) > 6_000_000) RemoveCached(_cache.First().Key);
                            _cache[key] = new(result, DateTimeOffset.UtcNow.AddSeconds(result is null ? 5 : 300));
                            _cachedCharacters += result?.Length ?? 0;
                        }
                    }
                    completion.TrySetResult(result);
                })) { _pending.Remove(key); return null; }
            }
        }
        // A hung Shell extension keeps its worker occupied; a timeout never creates replacement workers.
        try { return await task.WaitAsync(_timeout); }
        catch (TimeoutException) { return null; }
    }

    private void RemoveCached(Key key)
    { if (_cache.Remove(key, out var value)) _cachedCharacters -= value.DataUrl?.Length ?? 0; }

    private static bool IsPngDataUrl(string? value)
    {
        if (value is null || value.Length > 350000 || !value.StartsWith("data:image/png;base64,", StringComparison.Ordinal)) return false;
        try { var bytes = Convert.FromBase64String(value[22..]); return bytes.Length >= 33 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }); }
        catch (FormatException) { return false; }
    }

    private static string? Extract(string path, int size)
    {
        if (!File.Exists(path)) return null;
        object? shellItem = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItem));
            // ICONONLY preserves the Shell item's own icon (including .lnk custom icons), never document thumbnails.
            Marshal.ThrowExceptionForHR(((IShellItemImageFactory)shellItem).GetImage(new NativeSize { Width = size, Height = size }, 0x4 | 0x100, out bitmap));
            if (bitmap == IntPtr.Zero) return null;
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
            if (shellItem is not null && Marshal.IsComObject(shellItem)) Marshal.FinalReleaseComObject(shellItem);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width; public int Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
}
