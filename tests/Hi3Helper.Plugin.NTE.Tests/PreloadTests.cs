using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class PreloadTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NtePreloadTests", Guid.NewGuid().ToString("N"));
    private readonly Fixture fixture = new();
    private readonly HttpClient http;
    private readonly NteGameManager manager;
    private readonly NteGameInstaller installer;
    private CancellationToken Token => TestContext.Current.CancellationToken;

    public PreloadTests()
    {
        Directory.CreateDirectory(root);
        http = new(fixture);
        manager = new(http);
        manager.SetGamePath(root);
        Write("game.dat", Fixture.Old);
        Write("unchanged.dat", "STABLE"u8.ToArray());
        Write(NteConfigProvider.CN.GameExecutableName, "E"u8.ToArray());
        Write(NteConfigProvider.CN.SdkDirectory + "/config.xml", Encoding.UTF8.GetBytes(fixture.CurrentConfiguration));
        Write(NteConfigProvider.CN.SdkDirectory + "/ResList.xml", Encoding.UTF8.GetBytes(fixture.OldManifest));
        Write(".collapse_nte_config", "GameVersion=1.4.18\n"u8.ToArray());
        manager.LoadConfig();
        installer = new(manager, http) { ReportPerFileProgress = (_, _) => { } };
    }

    [Theory]
    [InlineData("start", "PC_150", "1.5.0", true)]
    [InlineData("stop", "PC_150", "1.5.0", false)]
    [InlineData("", "PC_150", "1.5.0", false)]
    [InlineData("start", "../PC_150", "1.5.0", false)]
    [InlineData("start", "PC_150?x=1", "1.5.0", false)]
    [InlineData("start", "PC_150", "1.4.18", false)]
    [InlineData("start", "PC_150", "invalid", false)]
    public void Availability_RequiresAnExplicitStartedNewerRelease(string status, string branch, string version, bool available)
    {
        string xml = $"<config><PreReleaseStatus>{status}</PreReleaseStatus><PreReleaseBranch>{branch}</PreReleaseBranch><PreReleaseVersion>{version}</PreReleaseVersion></config>";
        Assert.Equal(available, NtePreloadRelease.Parse(xml, new("1.4.18")) != null);
    }

    [Fact]
    public async Task Manager_ExposesUpcomingVersionOnlyForAnInstalledUpToDateGame()
    {
        Assert.Equal(0, await manager.InitAsyncInner(true, Token));
        manager.GetApiGameVersion(out GameVersion current);
        manager.GetApiPreloadGameVersion(out GameVersion upcoming);
        manager.IsGameHasPreload(out bool available);
        Assert.Equal(new("1.4.18"), current);
        Assert.Equal(new("1.5.0"), upcoming);
        Assert.True(available);
        manager.SetCurrentGameVersion(new("1.4.17"));
        manager.IsGameHasPreload(out available);
        Assert.False(available);
        manager.SetCurrentGameVersion(GameVersion.Empty);
        manager.IsGameHasPreload(out available);
        Assert.False(available);
    }

    [Fact]
    public async Task PublicPreloadApi_UsesDeltaRangeAndBlockDownloadsWithoutChangingTheInstalledGame()
    {
        var before = SnapshotInstalled();
        long size = await Size(false);
        Assert.Equal(fixture.Patch.Length + 6 + 7, size);
        Assert.True(size < Fixture.New.Length);
        Assert.Equal(0, await Size(true));
        List<InstallProgress> progress = [];
        List<InstallProgressState> states = [];
        Guid id = Guid.NewGuid();
        installer.StartPreloadAsync((in InstallProgress value) => progress.Add(value), states.Add, in id, out nint result);
        await result.AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal(before, SnapshotInstalled());
        manager.GetCurrentGameVersion(out GameVersion current);
        Assert.Equal(new("1.4.18"), current);
        Assert.Equal(size, progress[^1].DownloadedBytes);
        Assert.Equal(size, progress[^1].TotalBytesToDownload);
        Assert.Equal(InstallProgressState.Completed, states[^1]);
        Assert.DoesNotContain(fixture.Requests, r => r.Path.Contains(Fixture.Hash(Fixture.New), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fixture.Requests, r => r.Start == 16 && r.End == 21);
        Assert.Contains(fixture.Requests, r => r.Start == 0 && r.End == 2);
        Assert.Contains(fixture.Requests, r => r.Start == 3 && r.End == 6);
        Assert.Equal(size, await Size(true));
        int requests = fixture.Requests.Count;
        await installer.PreloadAsync(null, null, Token);
        Assert.Equal(requests, fixture.Requests.Count);
    }

    [Fact]
    public async Task CancelledPreload_LeavesTheGameUntouchedAndResumesThePartialPatch()
    {
        var before = SnapshotInstalled();
        fixture.StallPatch = true;
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        List<InstallProgressState> states = [];
        Task preload = installer.PreloadAsync(null, states.Add, cancel.Token);
        await fixture.FirstPatchByte.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preload);
        Assert.Equal(before, SnapshotInstalled());
        Assert.DoesNotContain(InstallProgressState.Completed, states);
        string cache = NtePreloadCache.PathFor(root, "1289", Fixture.Hash(fixture.Patch), fixture.Patch.Length);
        Assert.Equal(1, new FileInfo(cache + ".tmp").Length);
        fixture.StallPatch = false;
        await installer.PreloadAsync(null, null, Token);
        Assert.Equal(fixture.Patch, File.ReadAllBytes(cache));
        Assert.Contains(fixture.Requests, r => r.Path.Contains(Fixture.Hash(fixture.Patch), StringComparison.OrdinalIgnoreCase) && r.Start == 1);
        Assert.Equal(before, SnapshotInstalled());
    }

    [Fact]
    public async Task ReleasedVersion_ReusesPreloadedFilesAndPatchesAcrossTheOfficialBranchSwitch()
    {
        await installer.PreloadAsync(null, null, Token);
        int requests = fixture.Requests.Count;
        fixture.Released = true;
        Assert.Equal(0, await manager.InitAsyncInner(true, Token));
        Assert.Equal(Fixture.New.Length + 13, await Size(false, GameInstallerKind.Update));
        Assert.Equal(Fixture.New.Length + 13, await Size(true, GameInstallerKind.Update));
        Guid id = Guid.NewGuid();
        installer.StartUpdateAsync(null, null, in id, out nint result);
        await result.AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal(requests, fixture.Requests.Count);
        Assert.Equal(Fixture.New, File.ReadAllBytes(Path.Combine(root, "game.dat")));
        Assert.Equal("PACKED", File.ReadAllText(Path.Combine(root, "entry.dat")));
        Assert.Equal("BLOCK!!", File.ReadAllText(Path.Combine(root, "blocked.dat")));
        manager.GetCurrentGameVersion(out GameVersion current);
        Assert.Equal(new("1.5.0"), current);
        var config = NteOfficialPatcherState.ParseXml(File.ReadAllText(Path.Combine(root, NteConfigProvider.CN.SdkDirectory, "config.xml")));
        Assert.Equal("PC_150", config.SelectSingleNode("config/LocalBranch")!.InnerText);
        Assert.Null(config.SelectSingleNode("config/PreReleaseVersion"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidPatchOrChangedSource_FallsBackToTheVerifiedFullResource(bool corruptPatch)
    {
        await installer.PreloadAsync(null, null, Token);
        if (corruptPatch)
        {
            string cache = NtePreloadCache.PathFor(root, "1289", Fixture.Hash(fixture.Patch), fixture.Patch.Length);
            File.WriteAllBytes(cache, new byte[fixture.Patch.Length]);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddSeconds(2));
        }
        else File.WriteAllBytes(Path.Combine(root, "game.dat"), new byte[Fixture.Old.Length]);
        fixture.Released = true;
        Guid id = Guid.NewGuid();
        installer.StartUpdateAsync(null, null, in id, out nint result);
        await result.AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal(Fixture.New, File.ReadAllBytes(Path.Combine(root, "game.dat")));
        Assert.Contains(fixture.Requests, r => r.Path.Contains(Fixture.Hash(Fixture.New), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SameSizeCorruptCache_IsNotReportedAsDownloadedAndIsRepaired()
    {
        await installer.PreloadAsync(null, null, Token);
        string cached = NtePreloadCache.PathFor(root, "1289", Fixture.Hash(fixture.Patch), fixture.Patch.Length);
        File.WriteAllBytes(cached, new byte[fixture.Patch.Length]);
        File.SetLastWriteTimeUtc(cached, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal(13, await Size(true));
        await installer.PreloadAsync(null, null, Token);
        Assert.Equal(fixture.Patch, File.ReadAllBytes(cached));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnsupportedOrIncorrectPatchOutput_NeverReplacesTheInstalledFileBeforeFallback(bool unsupported)
    {
        if (unsupported) fixture.Patch[8] = (byte)'z';
        else fixture.Patch[^1] = 2;
        await installer.PreloadAsync(null, null, Token);
        fixture.BeforeFullDownload = () => Assert.Equal(Fixture.Old, File.ReadAllBytes(Path.Combine(root, "game.dat")));
        fixture.Released = true;
        Guid id = Guid.NewGuid();
        installer.StartUpdateAsync(null, null, in id, out nint result);
        await result.AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal(Fixture.New, File.ReadAllBytes(Path.Combine(root, "game.dat")));
        Assert.Contains(fixture.Requests, r => r.Path.Contains(Fixture.Hash(Fixture.New), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingOptionalDiffList_UsesCompleteChangedResourcesInTheIsolatedCache()
    {
        fixture.OmitDiff = true;
        Assert.Equal(Fixture.New.Length + 13, await Size(false));
        var before = SnapshotInstalled();
        await installer.PreloadAsync(null, null, Token);
        Assert.Equal(before, SnapshotInstalled());
        Assert.Equal(Fixture.New, File.ReadAllBytes(NtePreloadCache.PathFor(root, "1289", Fixture.Hash(Fixture.New), Fixture.New.Length)));
    }

    [Fact]
    public async Task PartialFullResourceCache_IsResumedAfterThePreviewBecomesTheActiveRelease()
    {
        fixture.OmitDiff = true;
        _ = await Size(false);
        string cached = NtePreloadCache.PathFor(root, "1289", Fixture.Hash(Fixture.New), Fixture.New.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        NteGameInstaller.PrepareTemporaryFile(cached + ".tmp", Fixture.Hash(Fixture.New), Fixture.New.Length);
        File.WriteAllBytes(cached + ".tmp", Fixture.New[..128]);
        fixture.Released = true;
        Assert.Equal(0, await manager.InitAsyncInner(true, Token));
        Assert.Equal(128, await Size(true, GameInstallerKind.Update));
        Guid id = Guid.NewGuid();
        installer.StartUpdateAsync(null, null, in id, out nint result);
        await result.AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal(Fixture.New, File.ReadAllBytes(Path.Combine(root, "game.dat")));
        Assert.Contains(fixture.Requests, r => r.Path.Contains(Fixture.Hash(Fixture.New), StringComparison.OrdinalIgnoreCase) && r.Start == 128);
    }

    [Fact]
    public async Task PreloadWithoutAnInstallationPath_ReturnsImmediately()
    {
        manager.SetGamePath("");
        await installer.PreloadAsync(null, null, Token);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task WithdrawnPreview_IsRefreshedBeforeStartingAndClearsTheUpcomingVersion()
    {
        Assert.True(await Size(false) > 0);
        fixture.Availability = "stop";
        await installer.PreloadAsync(null, null, Token);
        Assert.Empty(fixture.Requests);
        manager.GetApiPreloadGameVersion(out GameVersion upcoming);
        Assert.Equal(GameVersion.Empty, upcoming);
        Assert.Equal(0, await Size(false));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("manifest")]
    [InlineData("diff")]
    public async Task InconsistentOfficialMetadata_IsRejectedBeforeDownloadingPayloads(string mismatch)
    {
        fixture.Mismatch = mismatch;
        var before = SnapshotInstalled();
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PreloadAsync(null, null, Token));
        Assert.Empty(fixture.Requests);
        Assert.Equal(before, SnapshotInstalled());
    }

    [Fact]
    public void Cache_IsSeparatedByApplicationAndRejectsUntrustedResourceNames()
    {
        string hash = Fixture.Hash(Fixture.New);
        Assert.NotEqual(NtePreloadCache.PathFor(root, "1289", hash, 1), NtePreloadCache.PathFor(root, "3000001", hash, 1));
        Assert.Throws<InvalidDataException>(() => NtePreloadCache.PathFor(root, "1289", "../outside", 1));
    }

    private async Task<long> Size(bool downloaded, GameInstallerKind kind = GameInstallerKind.Preload)
    {
        Guid id = Guid.NewGuid();
        nint result;
        if (downloaded) installer.GetGameDownloadedSizeAsync(kind, in id, out result);
        else installer.GetGameSizeAsync(kind, in id, out result);
        return await result.AsTask<long>().WaitAsync(TimeSpan.FromSeconds(15), Token);
    }
    private string[] SnapshotInstalled() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => !Path.GetRelativePath(root, p).StartsWith(".collapse_nte_preload", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(p => Path.GetRelativePath(root, p) + ":" + Fixture.Hash(File.ReadAllBytes(p))).ToArray();
    private void Write(string relative, byte[] data)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }
    public void Dispose() { installer.Dispose(); manager.Dispose(); http.Dispose(); Directory.Delete(root, true); }

    private sealed class Fixture : HttpMessageHandler
    {
        internal static readonly byte[] Old = Encoding.UTF8.GetBytes(new string('a', 1024));
        internal static readonly byte[] New = Encoding.UTF8.GetBytes(new string('a', 512) + new string('b', 512));
        internal readonly byte[] Patch = HdiffFixtures.Create(Old, New, [(0, 0, 1024)]);
        internal bool Released, StallPatch, OmitDiff;
        internal Action? BeforeFullDownload;
        internal string Availability = "start", Mismatch = "";
        internal readonly TaskCompletionSource FirstPatchByte = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<(string Path, long? Start, long? End)> Requests = [];
        internal string OldManifest => $"<ResList version=\"1.4.18\">{Res("game.dat", Old)}{Res("unchanged.dat", "STABLE"u8.ToArray())}{Res(NteConfigProvider.CN.GameExecutableName, "E"u8.ToArray())}</ResList>";
        private string NewManifest => $"<ResList version=\"1.5.0\">{Res("game.dat", New)}{Res("unchanged.dat", "STABLE"u8.ToArray())}{Res(NteConfigProvider.CN.GameExecutableName, "E"u8.ToArray())}<Res filename=\"blocked.dat\" filesize=\"7\" md5=\"{Hash("BLOCK!!"u8.ToArray())}\"><Block index=\"0\" start=\"0\" size=\"3\" md5=\"{Hash("BLO"u8.ToArray())}\"/><Block index=\"1\" start=\"3\" size=\"4\" md5=\"{Hash("CK!!"u8.ToArray())}\"/></Res><Package><Pak md5=\"{Hash("fixture-pak"u8.ToArray())}\" filesize=\"22\"><Entry name=\"entry.dat\" offset=\"16\" size=\"6\" md5=\"{Hash("PACKED"u8.ToArray())}\"/></Pak></Package></ResList>";
        private string Diff => $"<PatchList><Patch oldfile=\"{Hash(Old)}.{Old.Length}\" newfile=\"{Hash(New)}.{New.Length}\" patch=\"{Hash(Patch)}.{Patch.Length}\"/></PatchList>";
        internal string CurrentConfiguration => ReleaseConfig("1.4.18", OldManifest, "")
            .Replace("</config>", $"<LocalBranch>PC_140</LocalBranch><PreReleaseBranch>PC_150</PreReleaseBranch><PreReleaseVersion>1.5.0</PreReleaseVersion><PreReleaseStatus>{Availability}</PreReleaseStatus></config>", StringComparison.Ordinal);
        internal static string Hash(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));
        private static string Res(string path, byte[] data) => $"<Res filename=\"{path.Replace('\\', '/')}\" filesize=\"{data.Length}\" md5=\"{Hash(data)}\"/>";
        private static string ReleaseConfig(string version, string manifest, string diff)
            => $"<config><ResVersion>{version}</ResVersion><ResSize>4096</ResSize><Extra><listHash>{Hash(Encoding.UTF8.GetBytes(manifest))}</listHash><diffHash>{Hash(Encoding.UTF8.GetBytes(diff))}</diffHash></Extra></config>";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content;
            HttpStatusCode status = HttpStatusCode.OK;
            bool next = path.Contains("/PC_150/", StringComparison.Ordinal);
            if (path.EndsWith("Version.ini", StringComparison.Ordinal)) content = new StringContent("FileListURL=https://launcher.test/1/AllFiles.xml");
            else if (path.EndsWith("AllFiles.xml", StringComparison.Ordinal)) content = new StringContent("<All_Files><File Path=\"/ResFilesM/1289/PatcherConfig/PatcherConfig.json\"/></All_Files>");
            else if (path.EndsWith("PatcherConfig.json.zip", StringComparison.Ordinal)) content = Zip(("PatcherConfig.json", Encoding.UTF8.GetBytes("{\"branchName\":\"" + (Released ? "PC_150" : "PC_140") + "\",\"appId\":\"1289\",\"gameResUrl\":[\"https://resources.test/clientRes\"]}")));
            else if (path.EndsWith("config.xml", StringComparison.Ordinal))
            {
                string xml = next ? ReleaseConfig(Mismatch == "version" ? "1.5.1" : "1.5.0", NewManifest, Diff) : CurrentConfiguration;
                if (next && Mismatch == "manifest") xml = xml.Replace(Hash(Encoding.UTF8.GetBytes(NewManifest)), new string('0', 32), StringComparison.Ordinal);
                if (next && Mismatch == "diff") xml = xml.Replace(Hash(Encoding.UTF8.GetBytes(Diff)), new string('0', 32), StringComparison.Ordinal);
                content = new StringContent(xml);
            }
            else if (path.EndsWith("ResList.bin.zip", StringComparison.Ordinal)) content = next
                ? OmitDiff ? Zip(("ResList.bin", Encoding.UTF8.GetBytes(NewManifest))) : Zip(("ResList.bin", Encoding.UTF8.GetBytes(NewManifest)), ("lastdiff.bin", Encoding.UTF8.GetBytes(Diff)))
                : Zip(("ResList.bin", Encoding.UTF8.GetBytes(OldManifest)));
            else
            {
                var range = request.Headers.Range?.Ranges.Single();
                lock (Requests) Requests.Add((path, range?.From, range?.To));
                if (path.Contains(Hash(New), StringComparison.Ordinal)) BeforeFullDownload?.Invoke();
                byte[] bytes = path.Contains(Hash(Patch), StringComparison.Ordinal) ? Patch :
                    path.Contains(Hash(New), StringComparison.Ordinal) ? New :
                    path.Contains(Hash("BLOCK!!"u8.ToArray()), StringComparison.Ordinal) ? "BLOCK!!"u8.ToArray() :
                    path.Contains(Hash("fixture-pak"u8.ToArray()), StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("................PACKED") :
                    throw new InvalidOperationException("Unexpected payload: " + path);
                long start = range?.From ?? 0, end = range?.To ?? bytes.Length - 1;
                if (StallPatch && ReferenceEquals(bytes, Patch)) content = new StreamContent(new StallStream(bytes, FirstPatchByte));
                else content = new ByteArrayContent(bytes[(int)start..((int)end + 1)]);
                if (range != null)
                {
                    status = HttpStatusCode.PartialContent;
                    content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, bytes.Length);
                }
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
        private static ByteArrayContent Zip(params (string Name, byte[] Bytes)[] files)
        {
            using MemoryStream stream = new();
            using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
                foreach (var file in files) { using Stream entry = zip.CreateEntry(file.Name).Open(); entry.Write(file.Bytes); }
            return new(stream.ToArray());
        }
    }

    private sealed class StallStream(byte[] data, TaskCompletionSource firstByte) : Stream
    {
        private bool read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!read) { read = true; buffer.Span[0] = data[0]; return 1; }
            firstByte.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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
    }
}
