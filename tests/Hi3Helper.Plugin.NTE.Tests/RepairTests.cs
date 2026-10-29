using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class RepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NteRepairTests", Guid.NewGuid().ToString("N"));
    public RepairTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Repair_OnlyDownloadsMissingAndCorruptFilesAndVerifiesResult()
    {
        Handler handler = new();
        using HttpClient http = new(handler);
        NteGameManager manager = new(http);
        manager.SetGamePath(_root);
        manager.SetCurrentGameVersion(new GameVersion("1.4.17"));
        NteGameInstaller installer = new(manager, http);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "good.pak"), "GOOD", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(_root, "bad.pak"), "BAD!", TestContext.Current.CancellationToken);
            DateTime goodTimestamp = File.GetLastWriteTimeUtc(Path.Combine(_root, "good.pak"));
            NteResListParser manifest = await installer.GetIntegrityManifestAsync(TestContext.Current.CancellationToken);
            NteIntegrityReport report = await NteGameIntegrity.CheckAsync(manifest, _root, null, TestContext.Current.CancellationToken);
            Assert.Equal(2, report.Issues.Count);
            using (NteGameOperation.Acquire(_root))
                await installer.RepairFilesAsync(report, null, null, TestContext.Current.CancellationToken);
            Assert.True((await NteGameIntegrity.CheckAsync(manifest, _root, null, TestContext.Current.CancellationToken)).Healthy);
            Assert.Equal(2, handler.Downloads.Count);
            Assert.DoesNotContain(Hash("GOOD"), handler.Downloads);
            Assert.Equal(goodTimestamp, File.GetLastWriteTimeUtc(Path.Combine(_root, "good.pak")));
            Assert.Equal("GameVersion=1.4.17\n", await File.ReadAllTextAsync(Path.Combine(_root, ".collapse_nte_config"), TestContext.Current.CancellationToken));
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public async Task Repair_FailedHashPreservesOriginalAndDoesNotAdvanceVersion()
    {
        Handler handler = new() { CorruptDownloads = true };
        using HttpClient http = new(handler);
        NteGameManager manager = new(http);
        manager.SetGamePath(_root);
        manager.SetCurrentGameVersion(new GameVersion("1.4.17"));
        NteGameInstaller installer = new(manager, http);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "good.pak"), "GOOD", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(_root, "bad.pak"), "BAD!", TestContext.Current.CancellationToken);
            NteResListParser manifest = await installer.GetIntegrityManifestAsync(TestContext.Current.CancellationToken);
            manager.SetCurrentGameVersion(new GameVersion("1.4.9"));
            NteIntegrityReport report = await NteGameIntegrity.CheckAsync(manifest, _root, null, TestContext.Current.CancellationToken);
            using (NteGameOperation.Acquire(_root))
                await Assert.ThrowsAsync<InvalidDataException>(() => installer.RepairFilesAsync(report, null, null, TestContext.Current.CancellationToken));
            Assert.Equal("BAD!", await File.ReadAllTextAsync(Path.Combine(_root, "bad.pak"), TestContext.Current.CancellationToken));
            manager.GetCurrentGameVersion(out GameVersion version);
            Assert.Equal(new GameVersion("1.4.9"), version);
            Assert.False(File.Exists(Path.Combine(_root, ".collapse_nte_config")));
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public async Task CdnRetry_DoesNotCountFailedAttemptTwiceAndCompletesAfterVerification()
    {
        Handler handler = new() { CorruptPrimary = true };
        using HttpClient http = new(handler);
        NteGameManager manager = new(http);
        manager.SetGamePath(_root);
        manager.SetCurrentGameVersion(new GameVersion("1.4.17"));
        NteGameInstaller installer = new(manager, http);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "good.pak"), "GOOD", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(_root, "bad.pak"), "BAD!", TestContext.Current.CancellationToken);
            var manifest = await installer.GetIntegrityManifestAsync(TestContext.Current.CancellationToken);
            var report = await NteGameIntegrity.CheckAsync(manifest, _root, null, TestContext.Current.CancellationToken);
            List<InstallProgress> samples = [];
            List<InstallProgressState> states = [];
            List<(long Done, long Total)> fileSamples = [];
            installer.ReportPerFileProgress = (done, total) => fileSamples.Add((done, total));
            using (NteGameOperation.Acquire(_root))
                await installer.RepairFilesAsync(report, (in InstallProgress value) => samples.Add(value),
                    state => states.Add(state), TestContext.Current.CancellationToken);
            Assert.All(samples, value => Assert.InRange(value.DownloadedBytes, 0L, value.TotalBytesToDownload));
            Assert.Equal(9, samples[^1].DownloadedBytes);
            Assert.Equal(2, samples[^1].DownloadedCount);
            Assert.Contains(InstallProgressState.Verify, states);
            Assert.Equal(InstallProgressState.Completed, states[^1]);
            Assert.True((await NteGameIntegrity.CheckAsync(manifest, _root, null, TestContext.Current.CancellationToken)).Healthy);
            Assert.Equal(4, handler.Downloads.Count);
            Assert.Contains(fileSamples, value => value.Done == 0 && value.Total > 1);
            Assert.All(fileSamples, value => Assert.InRange(value.Done, 0, value.Total));
            Assert.DoesNotContain(fileSamples, value => value.Total == 9); // Total-download bytes never replace the current file size.
            Assert.Equal((1L, 1L), fileSamples[^1]);
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }

    [Fact]
    public async Task Pause_DrainsAllWorkersAndDoesNotReportMd5OrAdvanceVersion()
    {
        Handler handler = new() { SlowDownloads = true, AdditionalResources = 5 };
        using HttpClient http = new(handler);
        NteGameManager manager = new(http);
        manager.SetGamePath(_root);
        manager.SetCurrentGameVersion(new GameVersion("1.4.17"));
        NteGameInstaller installer = new(manager, http);
        using CancellationTokenSource cancel = new();
        List<(long Done, long Total)> fileSamples = [];
        installer.ReportPerFileProgress = (done, total) => fileSamples.Add((done, total));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "good.pak"), "GOOD", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(_root, "bad.pak"), "BAD!", TestContext.Current.CancellationToken);
            var manifest = await installer.GetIntegrityManifestAsync(TestContext.Current.CancellationToken);
            var report = await NteGameIntegrity.CheckAsync(manifest, _root, null, TestContext.Current.CancellationToken);
            manager.SetCurrentGameVersion(new GameVersion("1.4.9"));
            using (NteGameOperation.Acquire(_root))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.RepairFilesAsync(report,
                    (in InstallProgress value) => { if (fileSamples.Any(file => file.Done > 0 && file.Done < file.Total)) cancel.Cancel(); }, null, cancel.Token));
            Assert.Equal(0, handler.ActiveStreams);
            Assert.Equal("BAD!", await File.ReadAllTextAsync(Path.Combine(_root, "bad.pak"), TestContext.Current.CancellationToken));
            manager.GetCurrentGameVersion(out GameVersion version);
            Assert.Equal(new GameVersion("1.4.9"), version);
            Assert.False(File.Exists(Path.Combine(_root, ".collapse_nte_config")));
            Assert.Contains(fileSamples, value => value.Done > 0 && value.Done < value.Total);
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }

    private static string Hash(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private sealed class Handler : HttpMessageHandler
    {
        internal bool CorruptDownloads { get; init; }
        internal bool CorruptPrimary { get; init; }
        internal bool SlowDownloads { get; init; }
        internal int AdditionalResources { get; init; }
        internal int ActiveStreams;
        internal List<string> Downloads { get; } = [];
        private readonly Dictionary<string, string> _resources = new() { [Hash("GOOD")] = "GOOD", [Hash("FIX!")] = "FIX!", [Hash("ADDED")] = "ADDED" };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content;
            if (path.EndsWith("Version.ini")) content = new StringContent("FileListURL=https://launcher.test/1/AllFiles.xml");
            else if (path.EndsWith("AllFiles.xml")) content = new StringContent("<All_Files><File Path=\"/ResFilesM/1289/PatcherConfig/PatcherConfig.json\"/></All_Files>");
            else if (path.EndsWith("PatcherConfig.json.zip")) content = Zip("PatcherConfig.json", """{"branchName":"PC_140","appId":"1289","gameResUrl":["https://game.test/clientRes","https://fallback.test/clientRes"]}""");
            else if (path.EndsWith("config.xml")) content = new StringContent("<config><ResVersion>1.4.17</ResVersion></config>");
            else if (path.EndsWith("ResList.bin.zip"))
            {
                List<(string Name, string Body)> files = [("good.pak", "GOOD"), ("bad.pak", "FIX!"), ("missing.pak", "ADDED")];
                for (int i = 0; i < AdditionalResources; i++) files.Add(($"extra{i}.pak", $"EXTRA{i}"));
                foreach (var file in files) _resources[Hash(file.Body)] = file.Body;
                content = Zip("ResList.bin", "<ResList version=\"1.4.17\">" + string.Concat(files.Select(file =>
                    $"<Res filename=\"{file.Name}\" filesize=\"{file.Body.Length}\" md5=\"{Hash(file.Body)}\"/>")) + "</ResList>");
            }
            else
            {
                string hash = Path.GetFileName(path).Split('.')[0];
                lock (Downloads) Downloads.Add(hash);
                if (SlowDownloads) content = new StreamContent(new SlowStream(this));
                else content = new StringContent(CorruptDownloads || (CorruptPrimary && request.RequestUri.Host == "game.test")
                    ? new string('X', _resources[hash].Length) : _resources[hash]);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        private static ByteArrayContent Zip(string name, string body)
        {
            using MemoryStream output = new();
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, true))
            using (Stream stream = archive.CreateEntry(name).Open()) stream.Write(Encoding.UTF8.GetBytes(body));
            return new(output.ToArray());
        }
        private sealed class SlowStream : Stream
        {
            private readonly Handler _parent;
            private bool _read;
            private int _disposed;
            internal SlowStream(Handler parent) { _parent = parent; Interlocked.Increment(ref parent.ActiveStreams); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (!_read) { _read = true; buffer.Span[0] = 1; return 1; }
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    await Task.Delay(50, CancellationToken.None);
                    Interlocked.Decrement(ref _parent.ActiveStreams);
                }
                GC.SuppressFinalize(this);
            }
            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref _parent.ActiveStreams);
                base.Dispose(disposing);
            }
        }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
