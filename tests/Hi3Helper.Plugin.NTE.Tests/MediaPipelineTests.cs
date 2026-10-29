using System.IO.Compression;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Config;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class MediaPipelineTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "NteMediaTests", Guid.NewGuid().ToString("N"));
    private readonly LauncherHandler _handler = new();
    private readonly NteAppearanceOptions _appearance = new() { OverlayEnabled = false };
    private readonly NteCNLauncherApiMedia _media;

    public MediaPipelineTests()
    {
        _media = new NteCNLauncherApiMedia(LauncherHandler.VersionUrl, "https://yh.wanmei.com/",
            new HttpClient(_handler), _cacheDirectory, () => _appearance);
    }

    [Fact]
    public void BeforeInitialization_EntriesAreEmpty()
    {
        _media.GetBackgroundEntries(out nint handle, out int count, out bool disposable, out bool allocated);
        Assert.Equal(nint.Zero, handle);
        Assert.Equal(0, count);
        Assert.False(disposable);
        Assert.False(allocated);
    }

    [Fact]
    public async Task Initialization_ExposesExtractedLocalImageThroughLauncherContract()
    {
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);

        string path = Assert.Single(GetPaths());
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.EndsWith(".jpg", path);
        Assert.StartsWith(Path.GetFullPath(_cacheDirectory) + Path.DirectorySeparatorChar, path);
        Assert.Equal(LauncherHandler.ImageBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        _media.GetBackgroundFlag(out LauncherBackgroundFlag flag);
        Assert.Equal(LauncherBackgroundFlag.TypeIsImage | LauncherBackgroundFlag.IsSourceFile, flag);
        Assert.Equal(1, _handler.BackgroundRequests);
        Assert.All(_handler.Referers, referer => Assert.Equal("https://yh.wanmei.com/", referer));

        _media.Dispose();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ChangedChecksumAtTheSameUrlRefreshesCachedArtwork()
    {
        _handler.IncludeChecksums = true;
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string first = Assert.Single(GetPaths());
        _handler.ImagePayload = [0xff, 0xd8, 0xff, 0xe0, 9, 8, 7, 6, 0xff, 0xd9];
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string second = Assert.Single(GetPaths());
        Assert.NotEqual(first, second); Assert.Equal(2, _handler.BackgroundRequests);
        Assert.Equal(_handler.ImagePayload, await File.ReadAllBytesAsync(second, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MismatchedCdnChecksumDoesNotPublishTheBackground()
    {
        _handler.IncludeChecksums = true; _handler.WrongChecksum = true;
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.Empty(GetPaths());
        Assert.Empty(Directory.GetFiles(_cacheDirectory));
    }

    [Fact]
    public async Task NativeLauncherSloganTakesPriorityOverOutdatedWebArtwork()
    {
        byte[] png = TestImages.Encode(TestImages.Solid(64, 64, [218, 159, 43, 255]));
        _handler.NativeArchive = QtResourceFixture.Create(png, namedRoot: true);
        _appearance.OverlayEnabled = true; _appearance.CanvasWidth = _appearance.CanvasHeight = 64;
        _appearance.Elements = [new() { Id = "slogan", Source = "slogan", X = 0, Y = 0, Width = 64, Height = 64 }];
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        using PluginDisposableMemory<LauncherPathEntry> entries = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(_media.GetLogoOverlayEntries);
        Assert.Equal(1, entries.Length);
        using LauncherPathEntry entry = entries[0];
        Assert.True(File.Exists(entry.Path));
        Assert.DoesNotContain(_handler.Requests, uri => uri.AbsolutePath.EndsWith(".html") || uri.AbsolutePath.EndsWith("/slogan.png"));
        Assert.Contains(_handler.Requests, uri => uri.AbsolutePath.EndsWith("/ResData/1289.dat.zip") && uri.Query.StartsWith("?nte_md5=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reinitialization_ReusesCompletedCache()
    {
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string first = Assert.Single(GetPaths());
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first, Assert.Single(GetPaths()));
        Assert.Equal(1, _handler.BackgroundRequests);
    }

    [Fact]
    public async Task NewLauncherVersion_DownloadsNewBackground()
    {
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string first = Assert.Single(GetPaths());
        _handler.VersionDirectory = "1.0.5";
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(first, Assert.Single(GetPaths()));
        Assert.Equal(2, _handler.BackgroundRequests);
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task EmptyCachedFile_IsDownloadedAgain()
    {
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string path = Assert.Single(GetPaths());
        await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, _handler.BackgroundRequests);
        Assert.Equal(LauncherHandler.ImageBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrimaryCdnFailure_UsesSecondaryCdnForAllResources()
    {
        _handler.FailPrimary = true;
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.Single(GetPaths());
        Assert.Contains(_handler.Requests, uri => uri.Host == "yhcdn2.wmupd.com" && uri.AbsolutePath.EndsWith("bg_0.jpg.zip"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidConfig_UsesManifestFallback(bool invalid)
    {
        _handler.IncludeConfig = invalid;
        _handler.ConfigJson = "this is not JSON";
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.Single(GetPaths());
    }

    [Theory]
    [InlineData("unrelated.txt")]
    [InlineData("bg_0.jpg", true)]
    public async Task InvalidArchive_DoesNotPublishOrKeepPartialFiles(string entryName, bool empty = false)
    {
        _handler.ImageEntryName = entryName;
        _handler.EmptyImage = empty;
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.Empty(GetPaths());
        Assert.Empty(Directory.GetFiles(_cacheDirectory));
    }

    [Fact]
    public async Task ZipEntryName_CannotControlCacheDestination()
    {
        _handler.ImageEntryName = "../../bg_0.jpg";
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string path = Assert.Single(GetPaths());
        Assert.StartsWith(Path.GetFullPath(_cacheDirectory) + Path.DirectorySeparatorChar, path);
        Assert.Single(Directory.GetFiles(_cacheDirectory));
        Assert.Equal(LauncherHandler.ImageBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledRefresh_PreservesPreviousEntries()
    {
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string path = Assert.Single(GetPaths());
        using CancellationTokenSource source = new();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _media.InitializeMediaAsync(source.Token));
        Assert.Equal(path, Assert.Single(GetPaths()));
        Assert.True(File.Exists(path));
    }

    private string[] GetPaths()
    {
        using PluginDisposableMemory<LauncherPathEntry> entries =
            PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(_media.GetBackgroundEntries);
        string[] paths = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            using LauncherPathEntry entry = entries[i];
            paths[i] = entry.Path!;
        }
        return paths;
    }

    [Fact]
    public async Task VideoIsFirstAndStaticImageRemainsAvailable()
    {
        _handler.IncludeVideo = true;
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        string[] paths = GetPaths();
        Assert.Equal(2, paths.Length);
        Assert.EndsWith(".mp4", paths[0]);
        Assert.EndsWith(".jpg", paths[1]);
        Assert.Equal(LauncherHandler.VideoBytes, await File.ReadAllBytesAsync(paths[0], TestContext.Current.CancellationToken));
        _media.GetBackgroundFlag(out LauncherBackgroundFlag flags);
        Assert.Equal(LauncherBackgroundFlag.TypeIsVideo | LauncherBackgroundFlag.IsSourceFile, flags);
    }

    [Fact]
    public async Task DisabledVideo_IsNotDownloaded()
    {
        _handler.IncludeVideo = true;
        _appearance.DynamicBackground = false;
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.Single(GetPaths());
        Assert.Equal(0, _handler.VideoRequests);
    }

    [Fact]
    public async Task FailedVideo_LeavesUsableStaticBackground()
    {
        _handler.IncludeVideo = true;
        _handler.FailVideo = true;
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        Assert.EndsWith(".jpg", Assert.Single(GetPaths()));
        Assert.Equal(2, _handler.VideoRequests);
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task Initialization_DoesNotFinishBeforeActualVideoResponse()
    {
        _handler.IncludeVideo = true;
        _handler.VideoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task initialize = _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        await _handler.VideoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(initialize.IsCompleted);
        Assert.Empty(GetPaths());
        _handler.VideoGate.SetResult();
        await initialize;
        Assert.Equal(2, GetPaths().Length);
    }

    [Fact]
    public async Task CancellationDuringVideo_DoesNotPublishIncompleteMedia()
    {
        _handler.IncludeVideo = true;
        _handler.VideoGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource source = new();
        Task initialize = _media.InitializeMediaAsync(source.Token);
        await _handler.VideoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
        Assert.Empty(GetPaths());
        Assert.Empty(Directory.GetFiles(_cacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task OverlayEntries_AreAlignedWithBothVideoAndStaticEntries()
    {
        _handler.IncludeVideo = true;
        _appearance.OverlayEnabled = true;
        _appearance.CanvasWidth = 64;
        _appearance.CanvasHeight = 64;
        _appearance.Elements = [new NteOverlayElement
            { Id = "custom", Source = "https://example.com/overlay.png", X = 10, Y = 20, Width = 4, Height = 4 }];
        await _media.InitializeMediaAsync(TestContext.Current.CancellationToken);
        using PluginDisposableMemory<LauncherPathEntry> entries =
            PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(_media.GetLogoOverlayEntries);
        Assert.Equal(GetPaths().Length, entries.Length);
        Assert.Equal(2, entries.Length);
        string? first = entries[0].Path;
        for (int i = 0; i < entries.Length; i++)
        {
            using LauncherPathEntry entry = entries[i];
            Assert.Equal(first, entry.Path);
            Assert.True(File.Exists(entry.Path));
        }
    }

    public void Dispose()
    {
        _media.Dispose();
        if (Directory.Exists(_cacheDirectory))
            Directory.Delete(_cacheDirectory, recursive: true);
    }

    private sealed class LauncherHandler : HttpMessageHandler
    {
        internal const string VersionUrl = "https://yhcdn1.wmupd.com/hd/publish_ob/launcher/Version.ini";
        internal static readonly byte[] ImageBytes = [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 4, 0xff, 0xd9];
        internal static readonly byte[] VideoBytes = [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0, 109, 112, 52, 49, 0, 0, 0, 0];
        internal byte[] ImagePayload { get; set; } = ImageBytes;
        internal bool IncludeChecksums { get; set; }
        internal bool WrongChecksum { get; set; }
        internal byte[]? NativeArchive { get; set; }
        internal string VersionDirectory { get; set; } = "1.0.4.0423_1";
        internal bool FailPrimary { get; set; }
        internal bool IncludeConfig { get; set; } = true;
        internal bool EmptyImage { get; set; }
        internal bool IncludeVideo { get; set; }
        internal bool FailVideo { get; set; }
        internal int VideoRequests { get; private set; }
        internal TaskCompletionSource? VideoGate { get; set; }
        internal TaskCompletionSource VideoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ImageEntryName { get; set; } = "bg_0.jpg";
        internal string ConfigJson { get; set; } = """{"imgs":[{"file":"bg_0.jpg"},{"file":"bg_0.jpg"},{"file":"missing.jpg"}]}""";
        internal int BackgroundRequests { get; private set; }
        internal List<Uri> Requests { get; } = [];
        internal List<string?> Referers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Uri uri = request.RequestUri!;
            Requests.Add(uri);
            Referers.Add(request.Headers.Referrer?.AbsoluteUri);
            if (FailPrimary && uri.Host == "yhcdn1.wmupd.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            byte[] payload;
            if (uri.AbsolutePath.EndsWith("/Version.ini"))
            {
                payload = Encoding.UTF8.GetBytes($"[Version]\nFileListURL={VersionDirectory}/AllFiles.xml\n");
            }
            else if (uri.AbsolutePath.EndsWith("/AllFiles.xml"))
            {
                string config = IncludeConfig ? """<File Path="/ResFilesM/1289/bgimgs/config.json" />""" : "";
                string video = IncludeVideo ? """<File Path="/ResFilesM/1289/bgimgs/bg.mp4" />""" : "";
                string checksum = IncludeChecksums ? $"Checksum=\"{(WrongChecksum ? new string('0', 32) : Convert.ToHexStringLower(MD5.HashData(ImagePayload)))}\"" : "";
                string native = NativeArchive == null ? "" : $"<File Path=\"/ResData/1289.dat\" Checksum=\"{Convert.ToHexStringLower(MD5.HashData(NativeArchive))}\" />";
                payload = Encoding.UTF8.GetBytes($"""<All_Files>{config}{video}{native}<File Path="/ResFilesM/1289/bgimgs/bg_0.jpg" {checksum} /></All_Files>""");
            }
            else if (uri.AbsolutePath.EndsWith("/config.json.zip"))
            {
                string json = IncludeVideo ? ConfigJson.TrimEnd('}') + ""","video":"bg.mp4"}""" : ConfigJson;
                payload = Zip("config.json", Encoding.UTF8.GetBytes(json));
            }
            else if (uri.AbsolutePath.EndsWith("/bg_0.jpg.zip"))
            {
                BackgroundRequests++;
                payload = Zip(ImageEntryName, EmptyImage ? [] : ImagePayload);
            }
            else if (uri.AbsolutePath.EndsWith("/bg.mp4.zip"))
            {
                VideoRequests++;
                if (FailVideo)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                HttpResponseMessage videoResponse = new(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip("bg.mp4", VideoBytes)) };
                VideoStarted.TrySetResult();
                return WaitForVideoAsync(videoResponse, cancellationToken);
            }
            else if (uri.AbsolutePath.EndsWith("/1289.dat.zip") && NativeArchive != null)
            {
                payload = Zip("1289.dat", NativeArchive);
            }
            else if (uri.AbsolutePath.EndsWith("/overlay.png"))
            {
                payload = TestImages.Encode(TestImages.Solid(2, 2, [255, 0, 0, 255]));
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }

        private async Task<HttpResponseMessage> WaitForVideoAsync(HttpResponseMessage response, CancellationToken token)
        {
            try
            {
                if (VideoGate != null)
                    await VideoGate.Task.WaitAsync(token);
                return response;
            }
            catch { response.Dispose(); throw; }
        }

        private static byte[] Zip(string name, byte[] payload)
        {
            using MemoryStream output = new();
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (Stream unrelated = archive.CreateEntry("readme.txt").Open())
                    unrelated.Write("not a background"u8);
                using Stream entry = archive.CreateEntry(name).Open();
                entry.Write(payload);
            }
            return output.ToArray();
        }
    }
}
