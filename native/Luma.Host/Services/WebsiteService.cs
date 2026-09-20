using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Luma.Host.Services;

public sealed record WebsiteMetadata(string Url, string Title, string? DataUrl);
/// <summary>Explicit URL import only; no cookies, scripts, redirects to LAN or unbounded crawling.</summary>
public sealed class WebsiteService
{
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private readonly Func<HttpMessageHandler> _handler;
    private readonly TimeSpan _timeout;
    public WebsiteService() : this(CreateHandler) { }
    internal WebsiteService(Func<HttpMessageHandler> handler, TimeSpan? timeout = null)
    { _handler = handler; _timeout = timeout ?? TimeSpan.FromSeconds(4); }
    public static bool IsUrl(string? value) => value is { Length: > 0 and <= 4096 } && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '"' or '\\') &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 &&
            !(b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8))) &&
            !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0);
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) &&
            !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) && !(b[0] == 192 && b[1] == 88 && b[2] == 99) &&
            !(b[0] == 198 && b[1] == 51 && b[2] == 100) && !(b[0] == 203 && b[1] == 0 && b[2] == 113) &&
            !(b[0] == 198 && b[1] is 18 or 19);
    }
    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false, MaxResponseHeadersLength = 32,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            // Connect to the exact validated address, never re-resolve the hostname in Socket.Connect.
            var address = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("本地或私有网络不读取网页元数据。");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };
    private static bool CanFetch(Uri target) => IsUrl(target.AbsoluteUri) && target.IsDefaultPort &&
        (!IPAddress.TryParse(target.DnsSafeHost.Trim('[', ']'), out var address) || IsPublic(address)) &&
        !target.DnsSafeHost.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !target.DnsSafeHost.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    public async Task<WebsiteMetadata> ReadAsync(string url)
    {
        if (!IsUrl(url)) throw new ArgumentException("只支持不带账户密码的 HTTP(S) 网址。");
        var uri = new Uri(url); var fallbackTitle = uri.Host[..Math.Min(120, uri.Host.Length)]; var fallback = new WebsiteMetadata(uri.AbsoluteUri, fallbackTitle, null);
        if (!await Slots.WaitAsync(0)) return fallback;
        try
        {
            using var cancel = new CancellationTokenSource(_timeout);
            using var http = new HttpClient(_handler());
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Luma/0.3 URL-metadata");
            async Task<(byte[] Bytes, Uri Final, string? Charset)> Fetch(Uri target, int limit, bool icon = false)
            {
                for (var redirect = 0; redirect < 4; redirect++)
                {
                    if (!CanFetch(target)) throw new HttpRequestException("元数据仅使用公网标准 HTTP(S) 端口。");
                    using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
                    if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location) { target = new Uri(target, location); continue; }
                    response.EnsureSuccessStatusCode();
                    var type = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (!icon && !type.Equals("text/html", StringComparison.OrdinalIgnoreCase) && !type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException("不是 HTML 页面。");
                    if (response.Content.Headers.ContentLength > limit) throw new HttpRequestException("网页资源过大。");
                    await using var input = await response.Content.ReadAsStreamAsync(cancel.Token); using var output = new MemoryStream();
                    var buffer = new byte[8192]; int count;
                    while ((count = await input.ReadAsync(buffer, cancel.Token)) > 0) { if (output.Length + count > limit) throw new HttpRequestException("网页资源过大。"); output.Write(buffer, 0, count); }
                    return (output.ToArray(), target, response.Content.Headers.ContentType?.CharSet);
                }
                throw new HttpRequestException("跳转过多。");
            }
            var page = await Fetch(uri, 524288);
            var encoding = Encoding.UTF8;
            try { if (!string.IsNullOrWhiteSpace(page.Charset)) encoding = Encoding.GetEncoding(page.Charset.Trim('"', '\'')); } catch (ArgumentException) { }
            var html = encoding.GetString(page.Bytes);
            var titleMatch = Regex.Match(html, @"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
            var title = titleMatch.Success ? WebUtility.HtmlDecode(Regex.Replace(titleMatch.Groups[1].Value, "<[^>]*>", "", RegexOptions.None, TimeSpan.FromMilliseconds(100))).Trim() : uri.Host;
            title = Regex.Replace(title, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100));
            title = new string(title.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).Take(120).ToArray()).Trim(); if (title.Length == 0) title = fallbackTitle;
            string? dataUrl = null;
            try
            {
                var iconUrl = new Uri(page.Final, "/favicon.ico");
                foreach (Match link in Regex.Matches(html, @"<link\b[^>]{0,4096}>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)).Cast<Match>().Take(32))
                {
                    if (!Regex.IsMatch(link.Value, "\\srel\\s*=\\s*[\"'][^\"']*\\bicon\\b", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))) continue;
                    var href = Regex.Match(link.Value, "\\shref\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                    if (href.Success && Uri.TryCreate(page.Final, WebUtility.HtmlDecode(href.Groups[1].Value), out var found) && IsUrl(found.AbsoluteUri)) { iconUrl = found; break; }
                }
                var icon = await Fetch(iconUrl, 262144, true);
                if (HasBoundedIconHeader(icon.Bytes)) dataUrl = await DecodeIcon(icon.Bytes, cancel.Token);
            }
            catch (Exception) { /* Missing/unsupported icons do not prevent URL import. */ }
            return new WebsiteMetadata(uri.AbsoluteUri, title, dataUrl);
        }
        catch (Exception) { return fallback; }
        finally { Slots.Release(); }
    }

    // Validate dimensions before WIC can allocate decoded pixels. Accept only static PNG
    // and bounded ICO frames; JPEG/GIF/SVG and animation fall back to the standard URL icon.
    internal static bool HasBoundedIconHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 24 or > 262144) return false;
        if (PngDimensions(bytes, out _, out _)) return true;
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) != 1) return false;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (count is < 1 or > 16 || bytes.Length < 6 + count * 16) return false;
        for (var index = 0; index < count; index++)
        {
            var entry = bytes[(6 + index * 16)..];
            var width = entry[0] == 0 ? 256 : entry[0]; var height = entry[1] == 0 ? 256 : entry[1];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]); var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (offset < 6 + count * 16 || length < 40 || offset > bytes.Length || length > bytes.Length - offset) return false;
            var frame = bytes.Slice((int)offset, (int)length);
            if (PngDimensions(frame, out var pngWidth, out var pngHeight)) { if (pngWidth != width || pngHeight != height) return false; continue; }
            var header = BinaryPrimitives.ReadUInt32LittleEndian(frame);
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(frame[14..]);
            if (header != 40 || frame.Length < header || BinaryPrimitives.ReadInt32LittleEndian(frame[4..]) != width ||
                BinaryPrimitives.ReadInt32LittleEndian(frame[8..]) != height * 2 || BinaryPrimitives.ReadUInt16LittleEndian(frame[12..]) != 1 ||
                bits is not (1 or 4 or 8 or 16 or 24 or 32) || BinaryPrimitives.ReadUInt32LittleEndian(frame[16..]) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(frame[32..]) > (bits <= 8 ? 1u << bits : 0)) return false;
        }
        return true;
    }
    private static bool PngDimensions(ReadOnlySpan<byte> bytes, out uint width, out uint height)
    {
        width = height = 0;
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]); height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        if (width is < 1 or > 256 || height is < 1 or > 256) return false;
        var offset = 8; var hasData = false;
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > bytes.Length - offset - 12) return false;
            var kind = bytes.Slice(offset + 4, 4);
            if ((kind.SequenceEqual("IHDR"u8) && offset != 8) || kind.SequenceEqual("acTL"u8) || kind.SequenceEqual("fcTL"u8) || kind.SequenceEqual("fdAT"u8) ||
                kind.SequenceEqual("iCCP"u8) || kind.SequenceEqual("zTXt"u8) || kind.SequenceEqual("iTXt"u8)) return false;
            if (kind.SequenceEqual("IDAT"u8)) hasData = true;
            if (kind.SequenceEqual("IEND"u8)) return length == 0 && hasData && offset + 12 == bytes.Length;
            offset += (int)length + 12;
        }
        return false;
    }
    public static bool IsIconDataUrl(string value)
    {
        const string prefix = "data:image/png;base64,";
        if (value.Length > 87406 || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try { var bytes = Convert.FromBase64String(value[prefix.Length..]); return bytes.Length <= 65536 && PngDimensions(bytes, out _, out _); }
        catch (FormatException) { return false; }
    }
    private static readonly Lazy<BlockingCollection<Action>> IconQueue = new(() =>
    {
        var queue = new BlockingCollection<Action>(2);
        for (var i = 0; i < 2; i++)
        {
            var worker = new Thread(() => { foreach (var action in queue.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "Luma website icons" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
        }
        return queue;
    });
    private static async Task<string?> DecodeIcon(byte[] bytes, CancellationToken cancel)
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!IconQueue.Value.TryAdd(() =>
        {
            try
            {
                if (cancel.IsCancellationRequested) { result.TrySetResult(null); return; }
                using var stream = new MemoryStream(bytes);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.FirstOrDefault(f => f.PixelWidth is > 0 and <= 256 && f.PixelHeight is > 0 and <= 256);
                if (frame is null) { result.TrySetResult(null); return; }
                using var png = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(frame); encoder.Save(png);
                result.TrySetResult(png.Length <= 65536 && !cancel.IsCancellationRequested ? "data:image/png;base64," + Convert.ToBase64String(png.ToArray()) : null);
            }
            catch (Exception) { result.TrySetResult(null); }
        })) return null;
        return await result.Task.WaitAsync(cancel);
    }
}
