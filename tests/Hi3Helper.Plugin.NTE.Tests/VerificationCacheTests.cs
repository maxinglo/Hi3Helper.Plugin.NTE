using System.Security.Cryptography;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class VerificationCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NteVerificationTests", Guid.NewGuid().ToString("N"));
    public VerificationCacheTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SizeQueries_DoNotHashChangedFiles_AndVisiblePreparationHashesOnlyOnce()
    {
        string path = Path.Combine(_root, "game.pak");
        File.WriteAllText(path, "right");
        string md5 = Convert.ToHexString(MD5.HashData("right"u8));
        NteResListParser previous = NteResListParser.Parse("<ResList version=\"1.4.9\"><Res filename=\"game.pak\" filesize=\"5\" md5=\"old\"/></ResList>");
        NteResListParser target = NteResListParser.Parse($"<ResList version=\"1.4.17\"><Res filename=\"game.pak\" filesize=\"5\" md5=\"{md5}\"/></ResList>");
        NteFileVerificationCache cache = new();
        for (int i = 0; i < 2; i++)
            Assert.Single(NteCNGameInstaller.BuildDownloadTasks(target, _root, true, false,
                TestContext.Current.CancellationToken, previous, verificationCache: cache));
        Assert.Equal(0, cache.HashReads);
        Assert.Empty(NteCNGameInstaller.BuildDownloadTasks(target, _root, true, true,
            TestContext.Current.CancellationToken, previous, verificationCache: cache));
        Assert.Equal(1, cache.HashReads);
        using FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(NteCNGameInstaller.BuildDownloadTasks(target, _root, true, true,
            TestContext.Current.CancellationToken, previous, verificationCache: cache));
        Assert.Equal(1, cache.HashReads);
    }

    [Fact]
    public void FileChangedAtSameSize_InvalidatesPreviousHash()
    {
        string path = Path.Combine(_root, "game.pak");
        File.WriteAllText(path, "right");
        string md5 = Convert.ToHexString(MD5.HashData("right"u8));
        NteFileVerificationCache cache = new();
        Assert.True(cache.Verify(path, 5, md5, TestContext.Current.CancellationToken));
        File.WriteAllText(path, "wrong");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.False(cache.Verify(path, 5, md5, TestContext.Current.CancellationToken));
        Assert.Equal(2, cache.HashReads);
    }

    [Fact]
    public void CancellationDuringHash_DoesNotCachePartialResult()
    {
        string path = Path.Combine(_root, "game.pak");
        byte[] bytes = new byte[4 * 1024 * 1024];
        File.WriteAllBytes(path, bytes);
        string md5 = Convert.ToHexString(MD5.HashData(bytes));
        NteFileVerificationCache cache = new();
        using CancellationTokenSource cancel = new();
        Assert.ThrowsAny<OperationCanceledException>(() => cache.Verify(path, bytes.Length, md5, cancel.Token, _ => cancel.Cancel()));
        Assert.True(cache.Verify(path, bytes.Length, md5, TestContext.Current.CancellationToken));
        Assert.Equal(2, cache.HashReads);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
