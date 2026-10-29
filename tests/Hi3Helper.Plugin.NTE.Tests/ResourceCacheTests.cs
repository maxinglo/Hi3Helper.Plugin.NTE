using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class ResourceCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NteResourceCacheTests", Guid.NewGuid().ToString("N"));
    public ResourceCacheTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DeletedInstallation_CanBeRestoredFromVerifiedIndependentCache()
    {
        string source = Path.Combine(_root, "game.pak");
        byte[] bytes = "resource payload"u8.ToArray();
        string md5 = Convert.ToHexString(MD5.HashData(bytes));
        await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);
        NteResourceCache cache = new(Path.Combine(_root, "cache"));
        await cache.StoreAsync(source, md5, bytes.Length, TestContext.Current.CancellationToken);
        File.Delete(source);
        long restored = 0;
        Assert.True(await cache.TryRestoreAsync(source, md5, bytes.Length, count => restored += count, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(bytes.Length, restored);
        Assert.True(File.Exists(cache.GetPath(md5, bytes.Length)));
    }

    [Fact]
    public async Task CorruptCache_LeavesExistingGameFileAndRollsBackItsProgress()
    {
        string output = Path.Combine(_root, "game.pak");
        NteResourceCache cache = new(Path.Combine(_root, "cache"));
        string md5 = Convert.ToHexString(MD5.HashData("right"u8));
        string path = cache.GetPath(md5, 5)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "wrong", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output, "original", TestContext.Current.CancellationToken);
        long counted = 0;
        Assert.False(await cache.TryRestoreAsync(output, md5, 5, bytes => counted += bytes, TestContext.Current.CancellationToken));
        Assert.Equal(0, counted);
        Assert.Equal("original", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CacheIdentity_CannotEscapeTheCacheDirectory()
        => Assert.Throws<InvalidDataException>(() => new NteResourceCache(_root).GetPath("../unrelated-file", 5));

    [Theory]
    [InlineData("whole")]
    [InlineData("blocked")]
    [InlineData("archive")]
    public async Task ReleaseDownloader_UsesHttpWithoutTheDevelopmentCache(string kind)
    {
        byte[] bytes = "cached payload"u8.ToArray();
        string md5 = Convert.ToHexString(MD5.HashData(bytes));
        string source = Path.Combine(_root, "source");
        await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);
        NteResourceCache cache = new(Path.Combine(_root, "cache"));
        await cache.StoreAsync(source, md5, bytes.Length, TestContext.Current.CancellationToken);
        Handler handler = new(bytes);
        using HttpClient http = new(handler);
        NteGameManager manager = new(http);
        NteGameInstaller installer = new(manager, http);
        string output = Path.Combine(_root, "game.pak");
        long reported = 0;
        try
        {
            Action<long> progress = count => reported += count;
            string[] cdns = ["https://example.invalid"];
            if (kind == "whole")
                await installer.DownloadResourceFileAsync(cdns, "PC_140", md5, bytes.Length, output, TestContext.Current.CancellationToken, progress);
            else if (kind == "blocked")
                await installer.DownloadBlockedResourceAsync(cdns, "PC_140", new NteResListEntry
                { Md5 = md5, Filesize = bytes.Length, Blocks = [new() { Index = 0, Start = 0, Size = bytes.Length, Md5 = md5 }] }, output, TestContext.Current.CancellationToken, progress);
            else
                await installer.DownloadPakEntryAsync(cdns, "PC_140", new NtePakInfo { Md5 = md5, Filesize = bytes.Length }, new NtePakEntry { Md5 = md5, Size = bytes.Length }, output, TestContext.Current.CancellationToken, progress);
            Assert.Equal(1, handler.Requests);
            Assert.Equal(bytes.Length, reported);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public void ReleaseAssembly_DoesNotContainTheDevelopmentCacheImplementation()
    {
        Assert.Null(typeof(NtePlugin).Assembly.GetType("Hi3Helper.Plugin.NTE.Management.Game.NteResourceCache"));
        Assert.NotEqual(typeof(NtePlugin).Assembly, typeof(NteResourceCache).Assembly);
    }

    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            ByteArrayContent content = new(bytes);
            bool range = request.Headers.Range != null;
            if (range) content.Headers.ContentRange = new ContentRangeHeaderValue(0, bytes.Length - 1, bytes.Length);
            return Task.FromResult(new HttpResponseMessage(range ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content });
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
