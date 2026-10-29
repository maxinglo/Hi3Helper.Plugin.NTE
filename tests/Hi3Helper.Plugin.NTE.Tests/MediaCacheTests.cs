using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class MediaCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NteCacheTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CancellationDuringWrite_RemovesPartialFile()
    {
        NteMediaCache cache = new(_directory);
        using CancellationTokenSource source = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync(
            "https://example.com/bg.jpg.zip", async (output, token) =>
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
                await source.CancelAsync();
                token.ThrowIfCancellationRequested();
            }, source.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task FailureAfterWrite_RemovesPartialFileAndCanRetry()
    {
        NteMediaCache cache = new(_directory);
        await Assert.ThrowsAsync<IOException>(() => cache.GetOrCreateAsync(
            "https://example.com/bg.jpg.zip", async (output, token) =>
            {
                await output.WriteAsync(new byte[] { 1, 2, 3 }, token);
                throw new IOException("Download interrupted.");
            }, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_directory));
        string path = await cache.GetOrCreateAsync("https://example.com/bg.jpg.zip",
            (output, token) => output.WriteAsync(new byte[] { 4, 5, 6 }, token).AsTask(), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueryString_IsIncludedInKeyAndDoesNotChangeImageExtension()
    {
        NteMediaCache cache = new(_directory);
        Task Write(Stream output, CancellationToken token) => output.WriteAsync(new byte[] { 1 }, token).AsTask();
        string first = await cache.GetOrCreateAsync("https://example.com/bg.jpg.zip?v=1", Write, TestContext.Current.CancellationToken);
        string second = await cache.GetOrCreateAsync("https://example.com/bg.jpg.zip?v=2", Write, TestContext.Current.CancellationToken);
        Assert.EndsWith(".jpg", first);
        Assert.EndsWith(".jpg", second);
        Assert.NotEqual(first, second);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
