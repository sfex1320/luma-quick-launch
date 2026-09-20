using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Luma.Host.Services;
using Xunit;

namespace Luma.Host.Tests;

public sealed class WebsiteServiceTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("192.88.99.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    public void NonPublicAndTransitionAddressesAreNeverMetadataTargets(string address)
        => Assert.False(WebsiteService.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void PublicAddressesRemainUsable(string address)
        => Assert.True(WebsiteService.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://example.com/\" --profile-directory=x")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com\\path")]
    [InlineData("https://account:password@example.com/")]
    [InlineData("file:///C:/secret")]
    [InlineData("javascript:alert(1)")]
    public void UrlContractRejectsUnsafeRawShellForms(string url) => Assert.False(WebsiteService.IsUrl(url));

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("https://[::ffff:127.0.0.1]/")]
    [InlineData("https://[2002:7f00:1::1]/")]
    [InlineData("https://localhost./")]
    [InlineData("https://private.localhost/")]
    [InlineData("https://example.com:8080/")]
    public async Task PrivateLiteralAndNonstandardPortNeverReachTransport(string url)
    {
        var transport = new Transport(_ => throw new Exception("Network should not be requested"));
        var result = await new WebsiteService(() => transport).ReadAsync(url);
        Assert.Empty(transport.Requests); Assert.Null(result.DataUrl);
    }
    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/")]
    [InlineData("file:///C:/secret")]
    public async Task EveryRedirectIsRevalidatedBeforeTransport(string destination)
    {
        var transport = new Transport(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri(destination) } });
        var result = await new WebsiteService(() => transport).ReadAsync("https://example.com/");
        Assert.Single(transport.Requests); Assert.Equal("example.com", result.Title);
    }
    [Fact]
    public async Task RedirectLoopAndOversizedResponseHaveHardBounds()
    {
        var redirect = new Transport(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/again", UriKind.Relative) } });
        await new WebsiteService(() => redirect).ReadAsync("https://example.com/");
        Assert.Equal(4, redirect.Requests.Count);
        var oversized = new Transport(_ => Html(new string('x', 524289)));
        Assert.Equal("example.com", (await new WebsiteService(() => oversized).ReadAsync("https://example.com/")).Title);
        Assert.Single(oversized.Requests);
        var streamed = new Transport(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string('x', 524289)))) { Headers = { ContentType = new MediaTypeHeaderValue("text/html") } } });
        Assert.Equal("example.com", (await new WebsiteService(() => streamed).ReadAsync("https://example.com/")).Title);
        Assert.Single(streamed.Requests);
    }
    [Fact]
    public async Task TitleIsDecodedSanitizedAndKeptWhenPrivateFaviconIsRefused()
    {
        var transport = new Transport(_ => Html("<title>  MOMO &amp; \n 世界\u202e </title><link rel='icon' href='http://10.0.0.1/a.png'>"));
        var result = await new WebsiteService(() => transport).ReadAsync("https://example.com/page");
        Assert.Equal("MOMO & 世界", result.Title); Assert.Null(result.DataUrl); Assert.Single(transport.Requests);
    }
    [Fact]
    public async Task StandardPngIconIsConvertedAndDimensionBombNeverGetsDecoded()
    {
        var png = SmallPng();
        var transport = new Transport(request => request.RequestUri!.AbsolutePath == "/" ? Html("<title>Example</title><link rel='icon' href='/logo.png'>") : Image(png));
        var result = await new WebsiteService(() => transport).ReadAsync("https://example.com/");
        Assert.Equal("Example", result.Title); Assert.NotNull(result.DataUrl); Assert.True(WebsiteService.IsIconDataUrl(result.DataUrl));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 100000);
        Assert.False(WebsiteService.HasBoundedIconHeader(png));
        var bomb = new Transport(request => request.RequestUri!.AbsolutePath == "/" ? Html("<title>Example</title>") : Image(png));
        result = await new WebsiteService(() => bomb).ReadAsync("https://example.com/");
        Assert.Equal("Example", result.Title); Assert.Null(result.DataUrl);
    }
    [Fact]
    public async Task StandardIcoPngFrameIsDecodedOnlyWhenDirectoryDimensionsAgree()
    {
        var png = SmallPng(); var ico = new byte[22 + png.Length]; ico[2] = 1; ico[4] = 1; ico[6] = ico[7] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22); png.CopyTo(ico, 22);
        Assert.True(WebsiteService.HasBoundedIconHeader(ico));
        var transport = new Transport(request => request.RequestUri!.AbsolutePath == "/" ? Html("<title>ICO</title>") : Image(ico));
        var result = await new WebsiteService(() => transport).ReadAsync("https://example.com/");
        Assert.NotNull(result.DataUrl); Assert.True(WebsiteService.IsIconDataUrl(result.DataUrl));
        ico[6] = 255; Assert.False(WebsiteService.HasBoundedIconHeader(ico));
        ico[4] = 17; Assert.False(WebsiteService.HasBoundedIconHeader(ico));
    }
    [Fact]
    public void ImagePreflightRejectsAnimationCompressedMetadataAndInvalidIcoOffsets()
    {
        var png = SmallPng(); Assert.True(WebsiteService.HasBoundedIconHeader(png));
        foreach (var kind in new[] { "acTL", "iCCP", "zTXt", "iTXt", "IHDR" })
        {
            var extended = new byte[png.Length + 12]; png.AsSpan(0, 33).CopyTo(extended); Encoding.ASCII.GetBytes(kind).CopyTo(extended, 37); png.AsSpan(33).CopyTo(extended.AsSpan(45));
            Assert.False(WebsiteService.HasBoundedIconHeader(extended));
        }
        var ico = new byte[80]; ico[2] = 1; ico[4] = 1; ico[6] = ico[7] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), 40); BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), uint.MaxValue);
        Assert.False(WebsiteService.HasBoundedIconHeader(ico));
        Assert.False(WebsiteService.IsIconDataUrl("data:image/png;base64,AA=="));
        Assert.False(WebsiteService.IsIconDataUrl("data:image/svg+xml;base64,PHN2Zy8+"));
    }
    [Fact]
    public async Task SlowRequestCancelsWithinBudgetAndReturnsUsableUrlFallback()
    {
        var transport = new Transport(_ => throw new Exception("Unused")) { Stall = true };
        var result = await new WebsiteService(() => transport, TimeSpan.FromMilliseconds(50)).ReadAsync("https://example.com/").WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("example.com", result.Title); Assert.Equal("https://example.com/", result.Url); Assert.True(transport.Cancelled);
    }
    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    [Fact]
    public async Task LongHostFallbackStaysWithinSavedNameLimit()
    {
        var host = string.Join(".", Enumerable.Repeat(new string('a', 50), 3)) + ".com";
        var result = await new WebsiteService(() => new Transport(_ => throw new HttpRequestException())).ReadAsync("https://" + host);
        Assert.Equal(120, result.Title.Length);
        Assert.StartsWith("https://", result.Url);
    }
    private static HttpResponseMessage Image(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static byte[] SmallPng()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 128, 0, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
    private sealed class Transport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        public bool Stall, Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (Stall) { try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); } catch (OperationCanceledException) { Cancelled = true; throw; } }
            return respond(request);
        }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}
