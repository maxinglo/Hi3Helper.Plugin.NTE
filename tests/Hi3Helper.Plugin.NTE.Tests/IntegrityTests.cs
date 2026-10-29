using System.Security.Cryptography;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class IntegrityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NteIntegrityTests", Guid.NewGuid().ToString("N"));
    public IntegrityTests() => Directory.CreateDirectory(_directory);

    private static NteResListParser Manifest(params (string Path, byte[] Bytes)[] entries)
    {
        string elements = string.Concat(entries.Select(e => $"<Res filename=\"{e.Path}\" filesize=\"{e.Bytes.Length}\" md5=\"{Convert.ToHexString(MD5.HashData(e.Bytes))}\"/>"));
        return NteResListParser.Parse($"<ResList version=\"1.4.17\">{elements}</ResList>");
    }

    [Fact]
    public async Task FullCheck_ReportsMissingSizeAndSameSizeCorruptionWithoutWritingGameFiles()
    {
        await File.WriteAllBytesAsync(Path.Combine(_directory, "good.pak"), "right"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "corrupt.pak"), "wrong", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "short.pak"), "x", TestContext.Current.CancellationToken);
        var manifest = Manifest(("good.pak", "right"u8.ToArray()), ("corrupt.pak", "right"u8.ToArray()),
            ("short.pak", "right"u8.ToArray()), ("missing.pak", "right"u8.ToArray()));
        string[] before = Directory.GetFiles(_directory);
        long lastProgress = 0;
        NteIntegrityReport report = await NteGameIntegrity.CheckAsync(manifest, _directory,
            (done, total, count, files, path) => { Assert.True(done >= lastProgress); lastProgress = done; },
            TestContext.Current.CancellationToken);
        Assert.True(report.Completed);
        Assert.False(report.Healthy);
        Assert.Equal(4, report.CheckedFiles);
        Assert.Equal(20, lastProgress);
        Assert.Equal(new[] { "hash-mismatch", "size-mismatch", "missing" }, report.Issues.Select(i => i.Reason));
        Assert.Equal(before, Directory.GetFiles(_directory));
        Assert.Equal("wrong", await File.ReadAllTextAsync(Path.Combine(_directory, "corrupt.pak"), TestContext.Current.CancellationToken));
        string output = Path.Combine(_directory, "report.json");
        NteGameIntegrity.SaveReport(report, output);
        Assert.Contains("hash-mismatch", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FullCheck_HealthyReportRequiresAllHashesToMatch()
    {
        byte[] bytes = "right"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_directory, "good.pak"), bytes, TestContext.Current.CancellationToken);
        var report = await NteGameIntegrity.CheckAsync(Manifest(("good.pak", bytes)), _directory, null, TestContext.Current.CancellationToken);
        Assert.True(report.Healthy);
        Assert.Equal(bytes.Length, report.HashedBytes);
    }

    [Fact]
    public async Task Cancellation_DoesNotMarkPartialCheckHealthy()
    {
        byte[] bytes = new byte[4 * 1024 * 1024];
        await File.WriteAllBytesAsync(Path.Combine(_directory, "large.pak"), bytes, TestContext.Current.CancellationToken);
        using CancellationTokenSource cancel = new();
        var report = await NteGameIntegrity.CheckAsync(Manifest(("large.pak", bytes)), _directory,
            (done, total, count, files, path) => { if (done > 0) cancel.Cancel(); }, cancel.Token);
        Assert.False(report.Completed);
        Assert.False(report.Healthy);
        Assert.True(report.HashedBytes < bytes.Length);
        Assert.Equal(0, report.CheckedFiles);
    }

    [Fact]
    public async Task EmptyManifest_CannotProduceAHealthyReport()
        => await Assert.ThrowsAsync<InvalidDataException>(() => NteGameIntegrity.CheckAsync(
            NteResListParser.Parse("<ResList version=\"1.4.17\"/>"), _directory, null, TestContext.Current.CancellationToken));

    [Fact]
    public void ConcurrentOperations_AreRejectedAndReleased()
    {
        using (NteGameOperation.Acquire(_directory))
            Assert.Throws<IOException>(() => NteGameOperation.Acquire(_directory));
        using FileStream next = NteGameOperation.Acquire(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
