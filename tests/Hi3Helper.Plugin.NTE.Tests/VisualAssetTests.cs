using Hi3Helper.Plugin.NTE.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using System.Net;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class VisualAssetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NteVisualTests", Guid.NewGuid().ToString("N"));
    private const string ImageUrl = "https://example.com/assets/icon.png";

    [Fact]
    public async Task VerticalSprite_ExportsOneNormalAndOneHoverFrame()
    {
        NtePngImage sprite = TestImages.Solid(2, 4, [255, 0, 0, 255]);
        for (int i = 2 * 2 * 4; i < sprite.Pixels.Length; i += 4)
        {
            sprite.Pixels[i] = 0;
            sprite.Pixels[i + 2] = 255;
        }
        using HttpClient client = Client(TestImages.Encode(sprite));
        NteSocialIconProcessor processor = new(new NteLauncherResourceClient(client, "https://example.com/"),
            new NteMediaCache(_directory));
        NteSocialMediaAsset result = await processor.PrepareAsync(new NteSocialMediaSource(
            "Social", "https://example.com/social", ImageUrl, "", 1), TestContext.Current.CancellationToken);
        NtePngImage normal = NtePngImage.Load(await File.ReadAllBytesAsync(result.IconPath, TestContext.Current.CancellationToken));
        NtePngImage hover = NtePngImage.Load(await File.ReadAllBytesAsync(result.IconHoverPath, TestContext.Current.CancellationToken));
        Assert.Equal((2, 2), (normal.Width, normal.Height));
        Assert.Equal((2, 2), (hover.Width, hover.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, normal.Pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, hover.Pixels[..4]);
    }

    [Fact]
    public async Task SingleStateIcon_IsNotCutInHalf()
    {
        using HttpClient client = Client(TestImages.Encode(TestImages.Solid(2, 2, [1, 2, 3, 255])));
        NteSocialIconProcessor processor = new(new NteLauncherResourceClient(client, "https://example.com/"),
            new NteMediaCache(_directory));
        NteSocialMediaAsset result = await processor.PrepareAsync(
            new NteSocialMediaSource("Social", "", ImageUrl, "", 1), TestContext.Current.CancellationToken);
        Assert.Equal(result.IconPath, result.IconHoverPath);
        NtePngImage image = NtePngImage.Load(await File.ReadAllBytesAsync(result.IconPath, TestContext.Current.CancellationToken));
        Assert.Equal(2, image.Height);
    }

    [Fact]
    public void SocialCss_ResolvesUrlsFromStylesheetAndReadsDisplayFrameRatio()
    {
        string html = """<ul class="ewm-list"><li class="icon-weibo"><a href="/social">Official</a><img src="/qr.png"><span class="ewm-tip">Scan &amp; follow</span></li></ul>""";
        string css = ".ewm-list li { width: .35rem; height: .42rem; } .icon-weibo { background: url('../icons/weibo.png') no-repeat 0 0; }";
        NteSocialMediaSource source = Assert.Single(NteLauncherHtmlAssets.ParseSocials(
            html, css, "https://example.com/launcher/page.html", "https://example.com/style/launcher.css"));
        Assert.Equal("https://example.com/icons/weibo.png", source.IconUrl);
        Assert.Equal("https://example.com/social", source.ClickUrl);
        Assert.Equal("https://example.com/qr.png", source.QrUrl);
        Assert.Equal("Scan & follow", source.Description);
        Assert.Equal(42d / 35d, source.FrameAspectRatio, 8);
    }

    [Fact]
    public async Task Overlay_PreservesTransparencyAndUsesConfiguredRectangle()
    {
        using HttpClient client = Client(TestImages.Encode(TestImages.Solid(2, 2, [255, 0, 0, 255])));
        NteOverlayComposer composer = new(new NteLauncherResourceClient(client, "https://example.com/"),
            new NteMediaCache(_directory));
        NteAppearanceOptions options = Options(new NteOverlayElement
            { Id = "custom", Source = ImageUrl, X = 10, Y = 20, Width = 4, Height = 4 });
        string? path = await composer.PrepareAsync(options, "", "https://example.com/", TestContext.Current.CancellationToken);
        Assert.NotNull(path);
        NtePngImage image = NtePngImage.Load(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal((64, 64), (image.Width, image.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Pixels.AsSpan((20 * 64 + 10) * 4, 4).ToArray());
        Assert.Equal(0, image.Pixels[(19 * 64 + 10) * 4 + 3]);
        Assert.Equal(0, image.Pixels[(24 * 64 + 10) * 4 + 3]);
    }

    [Fact]
    public void Composite_UsesSourceAlphaAndClipsOutsideCanvas()
    {
        NtePngImage canvas = TestImages.Solid(2, 2, [0, 0, 255, 255]);
        NtePngImage source = TestImages.Solid(2, 2, [255, 0, 0, 128]);
        canvas.Draw(source, -1, -1, 2, 2, 1, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 128, 0, 127, 255 }, canvas.Pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, canvas.Pixels[4..8]);
    }

    [Fact]
    public async Task DisabledOverlay_DoesNotRequestImages()
    {
        using HttpClient client = new(new ImageHandler(_ => throw new InvalidOperationException("Unexpected network request.")));
        NteOverlayComposer composer = new(new NteLauncherResourceClient(client, "https://example.com/"),
            new NteMediaCache(_directory));
        NteAppearanceOptions options = Options(new NteOverlayElement { Id = "image", Source = ImageUrl, Width = 10, Height = 10 });
        options.OverlayEnabled = false;
        Assert.Null(await composer.PrepareAsync(options, "", "https://example.com/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChangedDecorationAtSameUrl_InvalidatesGeneratedCache()
    {
        byte[] bytes = TestImages.Encode(TestImages.Solid(2, 2, [255, 0, 0, 255]));
        using HttpClient client = new(new ImageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        NteOverlayComposer composer = new(new NteLauncherResourceClient(client, "https://example.com/"), new NteMediaCache(_directory));
        NteAppearanceOptions options = Options(new NteOverlayElement { Id = "image", Source = ImageUrl, Width = 2, Height = 2 });
        string? first = await composer.PrepareAsync(options, "", "https://example.com/", TestContext.Current.CancellationToken);
        bytes = TestImages.Encode(TestImages.Solid(2, 2, [0, 0, 255, 255]));
        string? second = await composer.PrepareAsync(options, "", "https://example.com/", TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task NewsContract_ExportsCroppedIconsAndCorrectFlags()
    {
        byte[] icon = TestImages.Encode(TestImages.Solid(35, 84, [255, 255, 255, 255]));
        using HttpClient client = new(new ImageHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content = path.EndsWith(".css")
                ? new StringContent(".ewm-list li{width:.35rem;height:.42rem}.icon-weibo{background:url(/icon.png)}")
                : path.EndsWith(".js")
                    ? new StringContent("""var yh_data_data={"lb1":[]};""")
                    : path.EndsWith("icon.png")
                        ? new ByteArrayContent(icon)
                        : new StringContent("""<link href="/style/launcher.css"><ul class="ewm-list"><li class="icon-weibo"><a href="/social">Official</a><img src="/qr.png"></li></ul>""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        NteCNLauncherApiNews news = new(client, _directory);
        try
        {
            await news.InitializeNewsAsync(TestContext.Current.CancellationToken);
            using PluginDisposableMemory<LauncherSocialMediaEntry> entries =
                PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(news.GetSocialMediaEntries);
            Assert.Equal(1, entries.Length);
            using LauncherSocialMediaEntry entry = entries[0];
            Assert.Equal(LauncherSocialMediaEntryFlag.IconIsPath | LauncherSocialMediaEntryFlag.IconHoverIsPath |
                LauncherSocialMediaEntryFlag.QrImageIsPath, entry.Flags);
            Assert.NotEqual(entry.IconPath, entry.IconHoverPath);
            Assert.Equal(42, NtePngImage.Load(await File.ReadAllBytesAsync(entry.IconPath!, TestContext.Current.CancellationToken)).Height);
            Assert.Equal(42, NtePngImage.Load(await File.ReadAllBytesAsync(entry.IconHoverPath!, TestContext.Current.CancellationToken)).Height);
        }
        finally { news.Dispose(); }
    }

    private static NteAppearanceOptions Options(NteOverlayElement element)
        => new() { CanvasWidth = 64, CanvasHeight = 64, Elements = [element] };

    private static HttpClient Client(byte[] image) => new(new ImageHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) }));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class ImageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}

internal static class TestImages
{
    internal static NtePngImage Solid(int width, int height, byte[] color)
    {
        NtePngImage image = NtePngImage.Create(width, height);
        for (int i = 0; i < image.Pixels.Length; i += 4)
            color.CopyTo(image.Pixels, i);
        return image;
    }

    internal static byte[] Encode(NtePngImage image)
    {
        using MemoryStream output = new();
        image.Write(output);
        return output.ToArray();
    }
}
