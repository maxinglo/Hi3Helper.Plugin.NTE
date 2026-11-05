using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LocalUpdateAuditTests
{
    [Fact(Explicit = true)]
    public async Task RecordedRelease_QueriesActualCacheAndAppliesARealChainOutsideTheGameDirectory()
    {
        string game = Path.GetFullPath(Environment.GetEnvironmentVariable("NTE_UPDATE_GAME_ROOT")
            ?? throw new InvalidOperationException("Set NTE_UPDATE_GAME_ROOT for this explicit, read-only installation audit."));
        string metadata = Path.GetFullPath(Environment.GetEnvironmentVariable("NTE_UPDATE_METADATA_DIRECTORY")
            ?? throw new InvalidOperationException("Set NTE_UPDATE_METADATA_DIRECTORY to the downloaded official release evidence."));
        string output = Path.Combine(metadata, "local-audit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Assert.False(output.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(output);
        string before = Fingerprint(game);
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpClient recorded = new(new MetadataHandler(metadata));
        using Cleanup cleanup = new();
        NteGameManager manager = new(recorded);
        cleanup.Actions.Add(manager.Dispose);
        manager.SetGamePath(game);
        manager.LoadConfig();
        manager.GetCurrentGameVersion(out GameVersion installed);
        NteGameInstaller installer = new(manager, recorded) { ReportPerFileProgress = (_, _) => { } };
        cleanup.Actions.Add(installer.Dispose);
        NteResListParser manifest = await installer.GetIntegrityManifestAsync(token);
        List<NteGameInstaller.DownloadTask> plan = installer.BuildUpdateTasks(
            NteGameInstaller.BuildDownloadTasks(manifest, game, false, false, token), game, manager.AppId, token);
        long total = plan.Sum(t => t.TotalSize);
        long downloaded = plan.Sum(t => NteDownloadSpace.Capture(t).ResumableBytes);
        Assert.True(total > downloaded && downloaded > 0);
        Assert.Equal(9, plan.Count(t => t.UpdateOperation?.PatchStages == 2));
        manager.GetApiGameVersion(out GameVersion release);
        Assert.Equal(new("1.5.3"), release);
        string afterPlanning = Fingerprint(game);
        Assert.Equal(before, afterPlanning);
        var summary = new { passed = true, nativePluginLoaded = false, metadataFromAuthenticatedRecordedRelease = true,
            installedVersion = installed.ToString(), releasedVersion = release.ToString(), pendingFiles = plan.Count,
            patchStages = plan.Sum(t => t.UpdateOperation?.PatchStages ?? 0),
            plannedPayloadBytes = total, cachedOrResumableBytes = downloaded, remainingNetworkBytes = total - downloaded,
            installationFingerprintUnchanged = before == afterPlanning, installedGameModified = false,
            gameDownloadStarted = false, gameAcceptanceEvidence = "not-observed" };
        await File.WriteAllTextAsync(Path.Combine(output, "plan.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine(JsonSerializer.Serialize(summary));

        if (Environment.GetEnvironmentVariable("NTE_UPDATE_PATCH_SAMPLE") != "1") return;
        // Apply the real two-stage executable patch in the evidence directory, never in the installation.
        byte[] diff = await File.ReadAllBytesAsync(Path.Combine(metadata, "cdn2", "1.5.3-lastdiff.xml"), token);
        NtePatchList patches = NtePatchList.Parse(diff, await File.ReadAllTextAsync(Path.Combine(metadata, "PC_150-current-config.xml"), token));
        NteResListParser previous = NteResListParser.Parse(NteResListDecryptor.Decrypt(
            await File.ReadAllBytesAsync(Path.Combine(game, NteConfigProvider.CN.SdkDirectory, "ResList.xml"), token), manager.AppId));
        NteGameInstaller.DownloadTask target = NteGameInstaller.BuildDownloadTasks(manifest, game, false, false, token)
            .Single(t => t.OutputPath.EndsWith(Path.Combine("Win64", "HTGame.exe"), StringComparison.OrdinalIgnoreCase));
        NteGameInstaller.DownloadTask sourceDescriptor = NteGameInstaller.BuildDownloadTasks(previous, game, false, false, token)
            .Single(t => t.OutputPath == target.OutputPath);
        NteFileVerificationCache verification = new();
        Assert.True(verification.Verify(target.OutputPath, sourceDescriptor.TotalSize, sourceDescriptor.ExpectedMd5, token));
        NtePatchRoute route = patches.FindRoute(target, sourceDescriptor, (_, _) => false,
            r => verification.Verify(NtePreloadCache.PathFor(game, manager.AppId, r.Md5, r.Filesize), r.Filesize, r.Md5, token) ? 0 : r.Filesize)!;
        Assert.Equal(2, route.Steps.Count);
        using HttpClient network = new(new HttpClientHandler { AllowAutoRedirect = true, UseProxy = false }) { Timeout = TimeSpan.FromMinutes(4) };
        NteGameInstaller downloader = new(manager, network);
        cleanup.Actions.Add(downloader.Dispose);
        string source = target.OutputPath;
        List<object> stages = [];
        foreach (NtePatchEntry step in route.Steps)
        {
            string patch = NtePreloadCache.PathFor(game, manager.AppId, step.Resource.Md5, step.Resource.Filesize);
            bool reused = verification.Verify(patch, step.Resource.Filesize, step.Resource.Md5, token);
            if (!reused)
            {
                patch = Path.Combine(output, NtePatchList.Identity(step.Resource.Md5, step.Resource.Filesize));
                if (step.Resource.HasBlocks)
                    await downloader.DownloadBlockedResourceAsync(["https://yhcdn2.wmupd.com/clientRes"], "PC_150", step.Resource, patch, token);
                else await downloader.DownloadResourceFileAsync(["https://yhcdn2.wmupd.com/clientRes"], "PC_150", step.Resource.Md5, step.Resource.Filesize, patch, token);
            }
            string result = Path.Combine(output, NtePatchList.Identity(step.NewMd5, step.NewSize) + ".exe");
            NteHdiffPatch.Apply(source, patch, result + ".tmp", step.NewSize, token);
            NteGameInstaller.CompleteDownloadedFile(result + ".tmp", result, step.NewSize, step.NewMd5, token);
            stages.Add(new { reused, patchBytes = step.Resource.Filesize, outputBytes = step.NewSize, outputMd5 = step.NewMd5 });
            source = result;
        }
        Assert.True(verification.Verify(source, target.TotalSize, target.ExpectedMd5, token));
        Assert.Equal(before, Fingerprint(game));
        var sample = new { passed = true, stages, finalMd5 = target.ExpectedMd5, finalSize = target.TotalSize,
            installationFingerprintUnchanged = true, installedGameModified = false, outputDirectory = output,
            fullGameUpdatePerformed = false, gameAcceptanceEvidence = "not-observed" };
        await File.WriteAllTextAsync(Path.Combine(output, "real-patch-chain.json"), JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine("Real patch chain: " + Path.Combine(output, "real-patch-chain.json"));
    }

    private static string Fingerprint(string root) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => { FileInfo file = new(path); return new { path = Path.GetRelativePath(root, path), file.Length,
                written = file.LastWriteTimeUtc.Ticks, created = file.CreationTimeUtc.Ticks }; }))));

    private sealed class Cleanup : IDisposable
    {
        internal List<Action> Actions { get; } = [];
        public void Dispose() { foreach (Action action in Actions.AsEnumerable().Reverse()) action(); }
    }

    private sealed class MetadataHandler(string directory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content;
            if (path.EndsWith("Version.ini", StringComparison.Ordinal)) content = new StringContent("FileListURL=https://recorded.test/1/AllFiles.xml");
            else if (path.EndsWith("AllFiles.xml", StringComparison.Ordinal)) content = new StringContent("<All_Files><File Path=\"/ResFilesM/1289/PatcherConfig/PatcherConfig.json\"/></All_Files>");
            else if (path.EndsWith("PatcherConfig.json.zip", StringComparison.Ordinal))
            {
                using MemoryStream buffer = new();
                using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, true))
                using (Stream entry = archive.CreateEntry("PatcherConfig.json").Open())
                    entry.Write(Encoding.UTF8.GetBytes("{\"branchName\":\"PC_150\",\"appId\":\"1289\",\"gameResUrl\":[\"https://recorded.test/clientRes\"]}"));
                content = new ByteArrayContent(buffer.ToArray());
            }
            else if (path.EndsWith("config.xml", StringComparison.Ordinal)) content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(directory, "PC_150-current-config.xml")));
            else if (path.EndsWith("ResList.bin.zip", StringComparison.Ordinal)) content = new ByteArrayContent(File.ReadAllBytes(Path.Combine(directory, "cdn2", "1.5.3-ResList.bin.zip")));
            else throw new InvalidOperationException("The read-only planning audit forbids payload requests: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
