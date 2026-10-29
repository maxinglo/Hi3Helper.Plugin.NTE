using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class DownloadSpaceTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteDownloadSpaceTests", Guid.NewGuid().ToString("N"));
    public DownloadSpaceTests() => Directory.CreateDirectory(root);

    [Fact]
    public void FreshInstall_DoesNotReserveTheLargestFourFilesTwice()
    {
        long[] sizes = [14_801_277_872, 12_983_817_824, 11_235_083_312, 9_368_956_041, 49_628_687_197];
        long required = NteDownloadSpace.RequiredFreeBytes(sizes.Select(size => new NteDownloadSpace(size, 0, 0, 0)), NteDownloadScheduler.ReserveBytes);
        Assert.Equal(98_017_822_246 + NteDownloadScheduler.ReserveBytes, required);
        Assert.True(required < 100 * GiB);
    }

    [Theory]
    [InlineData(100, 99, 0, 1)]
    [InlineData(100, 99, 100, 1)]
    [InlineData(100, 50, 0, 50)]
    public void Resume_RequiresOnlyTheUnwrittenSuffix(long size, long temporary, long old, long remaining)
    {
        NteDownloadSpace file = new(size * GiB, temporary * GiB, old * GiB, temporary * GiB);
        Assert.Equal(remaining * GiB, file.NetworkBytes);
        Assert.Equal(remaining * GiB + NteDownloadScheduler.ReserveBytes,
            NteDownloadSpace.RequiredFreeBytes([file], NteDownloadScheduler.ReserveBytes));
    }

    [Fact]
    public void CompletedTemporaryFile_CanBeVerifiedWithoutAdditionalFreeSpace()
        => Assert.Equal(0, NteDownloadSpace.RequiredFreeBytes([new(100 * GiB, 100 * GiB, 100 * GiB, 100 * GiB)], NteDownloadScheduler.ReserveBytes));

    [Fact]
    public void Update_CanCommitASpaceReleasingFileBeforeAnotherDownload()
    {
        NteDownloadSpace grow = new(10 * GiB, 0, 0, 0), shrink = new(5 * GiB, 0, 20 * GiB, 0);
        Assert.Equal(5 * GiB + NteDownloadScheduler.ReserveBytes,
            NteDownloadSpace.RequiredFreeBytes([grow, shrink], NteDownloadScheduler.ReserveBytes));
    }

    [Fact]
    public void PartialBytes_RequireTheMatchingResourceIdentity()
    {
        string path = Path.Combine(root, "file");
        NteCNGameInstaller.PrepareTemporaryFile(path + ".tmp", "old", 10);
        File.WriteAllText(path + ".tmp", "123456");
        NteCNGameInstaller.DownloadTask task = new() { OutputPath = path, TotalSize = 10, ExpectedMd5 = "new" };
        NteDownloadSpace mismatch = NteDownloadSpace.Capture(task);
        Assert.Equal(0, mismatch.ResumableBytes);
        Assert.Equal(10, mismatch.NetworkBytes);
        Assert.Equal(4, mismatch.AdditionalBytes); // Discarding the old temporary file reclaims its six bytes.
        Assert.Equal("123456", File.ReadAllText(path + ".tmp"));
        File.WriteAllText(path + ".tmp.identity", "new:10");
        Assert.Equal(6, NteDownloadSpace.Capture(task).ResumableBytes);
    }

    [Fact]
    public void DownloadedSize_CountsCompletedFilesAndValidPartialData()
    {
        string complete = Path.Combine(root, "complete"), partial = Path.Combine(root, "partial"), stale = Path.Combine(root, "stale");
        File.WriteAllText(complete, "DONE");
        NteCNGameInstaller.PrepareTemporaryFile(partial + ".tmp", "wanted", 10); File.WriteAllText(partial + ".tmp", "123456789");
        NteCNGameInstaller.PrepareTemporaryFile(stale + ".tmp", "old", 10); File.WriteAllText(stale + ".tmp", "123456789");
        NteCNGameManager manager = new(); NteCNGameInstaller installer = new(manager, new HttpClient());
        try
        {
            Assert.Equal(13, installer.CalculateDownloadedBytes([
                new() { OutputPath = complete, TotalSize = 4, ExpectedMd5 = "complete" },
                new() { OutputPath = partial, TotalSize = 10, ExpectedMd5 = "wanted" },
                new() { OutputPath = stale, TotalSize = 10, ExpectedMd5 = "new" }]));
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }

    public void Dispose() => Directory.Delete(root, true);
}
