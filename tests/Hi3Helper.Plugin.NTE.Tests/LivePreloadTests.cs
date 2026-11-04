using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LivePreloadTests
{
    [Fact(Explicit = true)]
    public async Task CurrentCnPreload_AuthenticatesManifestsAndDownloadsSmallPatchAndPakSamples()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Hi3Helper.Plugin.NTE.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "research", "live-preload-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = true, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(120) };
        NteGameManager manager = new(client);
        NteGameInstaller installer = new(manager, client) { ReportPerFileProgress = (_, _) => { } };
        try
        {
            Assert.Equal(0, await manager.InitAsyncInner(true, token));
            manager.GetApiGameVersion(out GameVersion activeVersion);
            string root = Path.Combine(directory, "fixture");
            string exe = Path.Combine(root, NteConfigProvider.CN.GameExecutableName);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            await File.WriteAllBytesAsync(exe, [], token);
            manager.SetGamePath(root);
            manager.SetCurrentGameVersion(activeVersion);
            NteResListParser active = await installer.GetIntegrityManifestAsync(token);
            NtePreloadPlan? plan = await installer.GetPreloadPlanAsync(token);
            Assert.NotNull(plan);
            Assert.True(plan.Version > activeVersion);
            var previous = NteGameInstaller.BuildDownloadTasks(active, root, false, false, token).ToDictionary(t => t.OutputPath, StringComparer.OrdinalIgnoreCase);
            var target = NteGameInstaller.BuildDownloadTasks(plan.Manifest, root, false, false, token);
            var changed = target.Where(t => !previous.TryGetValue(t.OutputPath, out var old) ||
                old.TotalSize != t.TotalSize || !old.ExpectedMd5.Equals(t.ExpectedMd5, StringComparison.OrdinalIgnoreCase)).ToArray();
            List<NtePatchEntry> patches = [];
            long previewBytes = 0;
            foreach (var file in changed)
            {
                previous.TryGetValue(file.OutputPath, out var old);
                NtePatchEntry? patch = plan.Patches.Find(file, old);
                if (patch != null) patches.Add(patch);
                previewBytes += patch?.Resource.Filesize ?? file.TotalSize;
            }
            Assert.NotEmpty(patches);
            NtePatchEntry smallest = patches.MinBy(p => p.Resource.Filesize)!;
            Assert.InRange(smallest.Resource.Filesize, 1, 64_000_000);
            string patchPath = Path.Combine(directory, "sample-patch.hdiff");
            await installer.DownloadResourceFileAsync(manager.GameResBaseUrls, plan.BranchName,
                smallest.Resource.Md5, smallest.Resource.Filesize, patchPath, token);
            await using (FileStream stream = File.OpenRead(patchPath))
            {
                byte[] magic = new byte[9];
                await stream.ReadExactlyAsync(magic, token);
                Assert.Equal("HDIFF13&\0", Encoding.ASCII.GetString(magic));
                stream.Position = 0;
                Assert.Equal(smallest.Resource.Md5, Convert.ToHexStringLower(await MD5.HashDataAsync(stream, token)));
            }
            var (pak, entry) = plan.Manifest.Paks.SelectMany(p => p.Entries.Select(e => (p, e)))
                .Where(pair => pair.e.Size is > 0 and < 1_000_000).MinBy(pair => pair.e.Size);
            string rangePath = Path.Combine(directory, "sample-range.bin");
            await installer.DownloadPakEntryAsync(manager.GameResBaseUrls, plan.BranchName, pak, entry, rangePath, token);
            Assert.Equal(entry.Md5, Convert.ToHexStringLower(MD5.HashData(await File.ReadAllBytesAsync(rangePath, token))));
            manager.GetCurrentGameVersion(out GameVersion installed);
            Assert.Equal(activeVersion, installed);
            var report = new
            {
                passed = true, fixture = true, manager.AppId, activeBranch = manager.BranchName, activeVersion = activeVersion.ToString(),
                preloadBranch = plan.BranchName, preloadVersion = plan.Version.ToString(),
                targetFiles = target.Count, changedFiles = changed.Length, deltaFiles = patches.Count,
                changedFullResourceBytes = changed.Sum(t => t.TotalSize), plannedPreloadBytes = previewBytes,
                healthyInstalledManifestAssumed = true,
                samplePatchBytes = smallest.Resource.Filesize, samplePatchMd5 = smallest.Resource.Md5, samplePatchFormat = "HDIFF13 uncompressed",
                samplePakEntry = entry.Name, samplePakBytes = entry.Size, samplePakMd5 = entry.Md5,
                installedGameModified = false, fullPreloadPerformed = false, gameAcceptanceEvidence = "not-observed"
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine("Preload evidence: " + Path.Combine(directory, "result.json"));
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }
}
