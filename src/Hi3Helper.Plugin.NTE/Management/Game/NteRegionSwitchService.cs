using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed class NteRegionSwitchPlan
{
    public string SourceAppId { get; set; } = "";
    public string TargetAppId { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public int ReusedFiles { get; set; }
    public int CachedFiles { get; set; }
    public int DownloadFiles { get; set; }
    public int DeactivatedFiles { get; set; }
    public long ReusedBytes { get; set; }
    public long CachedBytes { get; set; }
    public long DownloadBytes { get; set; }
    public bool Completed { get; set; }
    public bool RecoveryRequired { get; set; }
    public string? Error { get; set; }
}

internal delegate void
    NteRegionSwitchProgress(int phase, long processed, long total, int files, int count, string path);

internal sealed class NteRegionSwitchService(HttpClient http, Action<string>? ensureIdle = null)
{
    private static readonly NteFileVerificationCache Verification = new();

    private sealed record Item(
        string Path,
        long Size,
        string Md5,
        NteSwitchResource? Resource,
        string? LauncherUrl,
        string? LocalCopy = null);

    private sealed record Context(
        NteRegionConfig Source,
        NteSharedInstallationState State,
        NteGameManager Manager,
        NteGameInstaller Downloader,
        byte[] Selected,
        Dictionary<string, NteSwitchResource> SourceFiles,
        Dictionary<string, NteSwitchResource> TargetFiles,
        List<Item> Items,
        List<Item> Missing,
        NteRegionSwitchPlan Plan);

    internal async Task<NteRegionSwitchPlan> PlanAsync(string root, NteRegionConfig target,
        NteRegionSwitchProgress? progress, CancellationToken token)
    {
        if (!target.IsOverseas) throw new InvalidDataException("Server switching supports Taiwan and Global only.");
        using FileStream operation = NteGameOperation.Acquire(root);
        (ensureIdle ?? NteSharedInstallation.EnsureGamesClosed)(root);
        if (NteSharedInstallation.HasPendingSwitch(root))
            return new() { TargetAppId = target.AppId, RecoveryRequired = true };
        Context context = await PrepareAsync(root, target, progress, token).ConfigureAwait(false);
        try
        {
            return context.Plan;
        }
        finally
        {
            context.Downloader.Dispose();
            context.Manager.Dispose();
        }
    }

    internal async Task<NteRegionSwitchPlan> SwitchAsync(string root, NteRegionConfig target,
        NteRegionSwitchProgress? progress, CancellationToken token, bool operationHeld = false,
        string? indexPath = null)
    {
        if (!target.IsOverseas) throw new InvalidDataException("Server switching supports Taiwan and Global only.");
        root = Path.GetFullPath(root);
        using FileStream? operation = operationHeld ? null : NteGameOperation.Acquire(root);
        (ensureIdle ?? NteSharedInstallation.EnsureGamesClosed)(root);
        NteRegionSwitchTransaction.Recover(root);
        Context context = await PrepareAsync(root, target, progress, token).ConfigureAwait(false);
        NteRegionSwitchTransaction? transaction = null;
        try
        {
            long downloaded = 0;
            int completed = 0;
            foreach (Item item in context.Missing)
            {
                token.ThrowIfCancellationRequested();
                string cached = NteSharedInstallation.ObjectPath(root, item.Path, item.Md5, item.Size);
                Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                long baseDownloaded = downloaded;
                int baseCompleted = completed;

                void Report(long bytes) => progress?.Invoke(2, baseDownloaded + bytes, context.Plan.DownloadBytes,
                    baseCompleted, context.Missing.Count, item.Path);

                if (item.LocalCopy != null)
                {
                    File.Copy(item.LocalCopy, cached, true);
                }
                else if (item.Resource is { } resource)
                {
                    long itemProgress = 0;

                    void DownloadProgress(long delta)
                    {
                        itemProgress += delta;
                        Report(itemProgress);
                    }

                    if (resource.Resource is { } direct)
                    {
                        if (direct.HasBlocks)
                            await context.Downloader.DownloadBlockedResourceAsync(context.Manager.GameResBaseUrls,
                                    context.Manager.BranchName, direct, cached, token, DownloadProgress)
                                .ConfigureAwait(false);
                        else
                            await context.Downloader.DownloadResourceFileAsync(context.Manager.GameResBaseUrls,
                                context.Manager.BranchName,
                                direct.Md5, direct.Filesize, cached, token, DownloadProgress).ConfigureAwait(false);
                    }
                    else
                        await context.Downloader.DownloadPakEntryAsync(context.Manager.GameResBaseUrls,
                            context.Manager.BranchName,
                            resource.Archive!, resource.Entry!, cached, token, DownloadProgress).ConfigureAwait(false);

                    downloaded += item.Size;
                }
                else
                {
                    string temporary = cached + ".launcher.tmp";
                    try
                    {
                        await using (FileStream output = new(temporary, FileMode.Create, FileAccess.ReadWrite,
                                         FileShare.None, 65536, FileOptions.Asynchronous))
                            await new NteLauncherResourceClient(http, target.ZoneHomePageUrl)
                                .ExtractAssetAsync(item.LauncherUrl!, output, token).ConfigureAwait(false);
                        File.Move(temporary, cached, true);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }

                    downloaded += item.Size;
                }

                if (!await NteRegionSwitchTransaction.MatchesAsync(cached, item.Size, item.Md5, token)
                        .ConfigureAwait(false))
                    throw new InvalidDataException("A prepared region file does not match its official manifest.");
                Verification.RememberVerified(cached, item.Size, item.Md5);
                completed++;
                progress?.Invoke(2, downloaded, context.Plan.DownloadBytes, completed, context.Missing.Count,
                    item.Path);
            }

            // Recheck the running processes after the potentially long preparation.
            (ensureIdle ?? NteSharedInstallation.EnsureGamesClosed)(root);
            transaction = NteRegionSwitchTransaction.Begin(root, context.Source.AppId, target.AppId);
            int activated = 0;
            foreach (Item item in context.Items)
            {
                string active = NteSharedInstallation.ManagedPath(root, item.Path);
                if (Verification.IsVerified(active, item.Size, item.Md5) ||
                    NteSharedInstallation.MatchesStamp(root, item.Path, item.Size, item.Md5, context.State)) continue;
                string cached = NteSharedInstallation.ObjectPath(root, item.Path, item.Md5, item.Size);
                await transaction.ReplaceAsync(item.Path, cached, item.Size, item.Md5, token).ConfigureAwait(false);
                Verification.RememberVerified(active, item.Size, item.Md5);
                activated++;
                progress?.Invoke(3, activated, context.Items.Count, activated, context.Items.Count, item.Path);
            }

            foreach (NteSwitchResource file in context.SourceFiles.Values)
            {
                if (context.TargetFiles.ContainsKey(file.Path) ||
                    !File.Exists(NteSharedInstallation.ManagedPath(root, file.Path))) continue;
                await transaction.ReplaceAsync(file.Path, null, 0, "", token).ConfigureAwait(false);
            }

            // Generate official timestamps only after the target files are active.
            string sdk = target.SdkDirectory;
            string configPath = NteSharedInstallation.ManagedPath(root, sdk + "/config.xml");
            string release = context.Manager.ReleaseConfigXml!;
            string current = File.Exists(configPath)
                ? File.ReadAllText(configPath)
                : CreateSeedConfiguration(release, context.Manager.BranchName);
            byte[] localManifest = NteOfficialPatcherState.CreateLocalManifest(context.Selected, target.AppId, root);
            string localConfig =
                NteOfficialPatcherState.CreateConfiguration(current, release, context.Manager.BranchName,
                    context.Selected);
            await ReplaceMetadataAsync(transaction, root, sdk + "/ResList.xml", localManifest, token)
                .ConfigureAwait(false);
            await ReplaceMetadataAsync(transaction, root, sdk + "/config.xml", Encoding.UTF8.GetBytes(localConfig),
                token).ConfigureAwait(false);
            await ReplaceMetadataAsync(transaction, root, ".collapse_nte_config",
                    Encoding.UTF8.GetBytes($"GameVersion={context.Plan.TargetVersion}\nAppId={target.AppId}\n"), token)
                .ConfigureAwait(false);

            context.State.ActiveAppId = target.AppId;
            context.State.Profiles[target.AppId] = new()
            {
                Version = context.Plan.TargetVersion, Branch = context.Manager.BranchName,
                ManifestXml = Encoding.UTF8.GetString(context.Selected), ReleaseXml = release
            };
            context.State.VerifiedFiles.Clear();
            foreach (Item item in context.Items)
                context.State.VerifiedFiles[item.Path] =
                    NteSharedInstallation.Stamp(NteSharedInstallation.ManagedPath(root, item.Path), item.Size,
                        item.Md5);
            token.ThrowIfCancellationRequested();
            transaction.Commit(context.State);
            transaction = null;
            NteSharedInstallation.Register(root, context.State, indexPath);
            context.Plan.Completed = true;
            progress?.Invoke(5, 1, 1, context.Items.Count, context.Items.Count, "");
            return context.Plan;
        }
        catch
        {
            transaction?.Rollback();
            throw;
        }
        finally
        {
            context.Downloader.Dispose();
            context.Manager.Dispose();
        }
    }

    private async Task<Context> PrepareAsync(string root, NteRegionConfig target, NteRegionSwitchProgress? progress,
        CancellationToken token)
    {
        if (!target.IsOverseas) throw new InvalidDataException("Server switching supports Taiwan and Global only.");
        if (!File.Exists(Path.Combine(root, target.GameExecutableName)))
            throw new InvalidDataException("Select a complete NTE game installation.");
        NteRegionConfig source = NteConfigProvider.DetectInstalledRegion(root) ??
                                 throw new InvalidDataException("The source region is unknown.");
        if (!source.IsOverseas) throw new InvalidDataException("Mainland China installations are managed separately.");
        NteSharedInstallationState state = NteSharedInstallation.Load(root) ?? new() { ActiveAppId = source.AppId };
        if (state.ActiveAppId != source.AppId)
            throw new InvalidDataException("The active region marker and shared state disagree.");
        if (!state.Profiles.TryGetValue(source.AppId, out NteSharedProfile? sourceProfile))
        {
            sourceProfile = await ReadSourceAsync(root, source, token).ConfigureAwait(false);
            state.Profiles[source.AppId] = sourceProfile;
        }

        NteGameManager manager = new(http, target);
        NteGameInstaller downloader = new(manager, http);
        try
        {
            if (await manager.InitAsyncInner(true, token).ConfigureAwait(false) != 0 ||
                manager.ReleaseConfigXml == null)
                throw new IOException("Cannot load the target region's official release.");
            manager.GetApiGameVersion(out GameVersion version);
            byte[] remote = await downloader
                .DownloadAndDecryptResListAsync(manager.BranchName, version.ToString(), token).ConfigureAwait(false);
            string expected = NteOfficialPatcherState.ParseXml(manager.ReleaseConfigXml)
                .SelectSingleNode("config/Extra/listHash")?.InnerText ?? "";
            if (!Convert.ToHexStringLower(MD5.HashData(remote)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The target resource manifest does not match its official listHash.");
            string selection = state.Profiles.TryGetValue(target.AppId, out NteSharedProfile? previousTarget)
                ? previousTarget.ManifestXml
                : sourceProfile.ManifestXml;
            byte[] selected = NteRegionSwitchManifest.SelectTags(remote, selection);
            Dictionary<string, NteSwitchResource> sourceFiles =
                NteRegionSwitchManifest.Read(Encoding.UTF8.GetBytes(sourceProfile.ManifestXml));
            Dictionary<string, NteSwitchResource> targetFiles = NteRegionSwitchManifest.Read(selected);
            List<Item> items = targetFiles.Values.Select(file => new Item(file.Path, file.Size, file.Md5, file, null))
                .ToList();
            NteLauncherResourceClient launcherClient = new(http, target.ZoneHomePageUrl);
            string ini = await launcherClient.ReadTextAsync(target.LauncherVersionIniUrl, token).ConfigureAwait(false);
            string launcherUrl = NteLauncherManifest.ParseFileListUrl(ini, target.LauncherVersionIniUrl);
            NteLauncherManifest launcher =
                NteLauncherManifest.Parse(await launcherClient.ReadTextAsync(launcherUrl, token).ConfigureAwait(false),
                    launcherUrl);
            if (launcher.Files.Count == 0)
                throw new InvalidDataException("The target launcher has no verified file inventory.");
            foreach (NteLauncherFile file in launcher.Files)
            {
                string relative = file.Path.TrimStart('/');
                if (relative.Replace('\\', '/').StartsWith("UserData/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Launcher updates cannot overwrite account data.");
                string destination = target.LauncherDirectoryName + "/" + relative;
                string other = source.LauncherDirectoryName + "/" + relative
                    .Replace(target.AppId, source.AppId, StringComparison.Ordinal)
                    .Replace(target.LauncherDirectoryName, source.LauncherDirectoryName, StringComparison.Ordinal);
                items.Add(new(destination, file.Size, file.Md5, null, launcher.GetArchiveUrl(file.Path),
                    source.AppId == target.AppId ? null : NteSharedInstallation.ManagedPath(root, other)));
            }

            NteRegionSwitchPlan plan = new()
            {
                SourceAppId = source.AppId, TargetAppId = target.AppId, TargetVersion = version.ToString(),
                DeactivatedFiles = sourceFiles.Keys.Count(path =>
                    !targetFiles.ContainsKey(path) && File.Exists(NteSharedInstallation.ManagedPath(root, path)))
            };
            List<Item> missing = [];
            long processed = 0, total = items.Sum(item => item.Size);
            int files = 0;
            foreach (Item item in items)
            {
                token.ThrowIfCancellationRequested();
                string active = NteSharedInstallation.ManagedPath(root, item.Path);
                if (NteSharedInstallation.MatchesStamp(root, item.Path, item.Size, item.Md5, state))
                    Verification.RememberVerified(active, item.Size, item.Md5);
                long baseProcessed = processed;
                int baseFiles = files;
                bool valid = await Task
                    .Run(
                        () => Verification.Verify(active, item.Size, item.Md5, token,
                            read => progress?.Invoke(1, baseProcessed + read, total, baseFiles, items.Count,
                                item.Path)), token).ConfigureAwait(false);
                if (valid)
                {
                    plan.ReusedFiles++;
                    plan.ReusedBytes += item.Size;
                }
                else
                {
                    string cached = NteSharedInstallation.ObjectPath(root, item.Path, item.Md5, item.Size);
                    if (await Task.Run(() => Verification.Verify(cached, item.Size, item.Md5, token), token)
                            .ConfigureAwait(false))
                    {
                        plan.CachedFiles++;
                        plan.CachedBytes += item.Size;
                    }
                    else if (item.LocalCopy != null && await Task
                                 .Run(() => Verification.Verify(item.LocalCopy, item.Size, item.Md5, token), token)
                                 .ConfigureAwait(false))
                    {
                        missing.Add(item);
                        plan.CachedFiles++;
                        plan.CachedBytes += item.Size;
                    }
                    else
                    {
                        missing.Add(item with { LocalCopy = null });
                        plan.DownloadFiles++;
                        plan.DownloadBytes += item.Size;
                    }
                }

                processed += item.Size;
                progress?.Invoke(1, processed, total, ++files, items.Count, item.Path);
            }

            return new(source, state, manager, downloader, selected, sourceFiles, targetFiles, items, missing, plan);
        }
        catch
        {
            downloader.Dispose();
            manager.Dispose();
            throw;
        }
    }

    private async Task<NteSharedProfile> ReadSourceAsync(string root, NteRegionConfig region, CancellationToken token)
    {
        string config = Path.Combine(root, region.SdkDirectory, "config.xml");
        string manifest = Path.Combine(root, region.SdkDirectory, "ResList.xml");
        if (!File.Exists(config) || !File.Exists(manifest))
            throw new InvalidDataException(
                "Complete an official installation or repair before enabling shared servers.");
        byte[] bytes = NteResListDecryptor.Decrypt(await File.ReadAllBytesAsync(manifest, token).ConfigureAwait(false),
            region.AppId);
        string xml = await File.ReadAllTextAsync(config, token).ConfigureAwait(false);
        _ = NteRegionSwitchManifest.Read(bytes);
        return new()
        {
            Version = NteGameManager.ParseReleaseVersion(xml).ToString(), Branch = region.DefaultBranch,
            ManifestXml = Encoding.UTF8.GetString(bytes), ReleaseXml = xml
        };
    }

    private static string CreateSeedConfiguration(string releaseXml, string branch)
    {
        XmlDocument seed = NteOfficialPatcherState.ParseXml(releaseXml);
        XmlElement local = seed.CreateElement("LocalBranch");
        local.InnerText = branch;
        seed.DocumentElement!.AppendChild(local);
        return seed.OuterXml;
    }

    private static async Task ReplaceMetadataAsync(NteRegionSwitchTransaction transaction, string root, string relative,
        byte[] bytes, CancellationToken token)
    {
        string md5 = Convert.ToHexStringLower(MD5.HashData(bytes));
        string staged = NteSharedInstallation.ObjectPath(root, relative, md5, bytes.Length);
        NteSharedInstallation.WriteAtomic(staged, bytes);
        await transaction.ReplaceAsync(relative, staged, bytes.Length, md5, token).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NteRegionSwitchPlan))]
internal partial class NteRegionSwitchPlanJsonContext : JsonSerializerContext;
