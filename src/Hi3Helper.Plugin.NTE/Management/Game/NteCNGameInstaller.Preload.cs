using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal partial class NteGameInstaller
{
    internal async Task<NtePreloadPlan?> GetPreloadPlanAsync(CancellationToken token)
    {
        if (_cachedResList == null && await InitAsync(token).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Cannot resolve the official game release.");
        NteGameManager manager = (NteGameManager)GameManager;
        manager.IsGameHasPreload(out bool available);
        if (!available || manager.PreloadRelease is not { } release) return null;
        if (_preloadPlan is { } cached && cached.Version == release.Version && cached.BranchName == release.BranchName)
            return cached;

        NtePatcherConfig config = new(release.BranchName, manager.AppId, manager.GameResBaseUrls);
        string xml = await config.ReadReleaseConfigAsync(_downloadHttpClient, token).ConfigureAwait(false);
        if (NteGameManager.ParseReleaseVersion(xml) != release.Version)
            throw new InvalidDataException("The preload configuration differs from the advertised version.");
        var archive = await DownloadManifestArchiveAsync(release.BranchName, release.Version.ToString(), token).ConfigureAwait(false);
        NtePatchList.ValidateHash(archive.Manifest,
            NteOfficialPatcherState.ParseXml(xml).SelectSingleNode("config/Extra/listHash")?.InnerText);
        GameManager.GetGamePath(out string? root);
        NteResListParser manifest = NteResListParser.Parse(
            NteOfficialPatcherState.SelectInstalledBaseVersions(archive.Manifest, root, manager.Region));
        if (manifest.Version != release.Version.ToString())
            throw new InvalidDataException("The preload manifest differs from the advertised version.");
        return _preloadPlan = new(release.BranchName, release.Version, manifest, NtePatchList.Parse(archive.Patches, xml));
    }

    private async Task<long> GetPreloadSizeAsync(bool downloaded, CancellationToken token)
    {
        NtePreloadPlan? plan = await GetPreloadPlanAsync(token).ConfigureAwait(false);
        if (plan == null) return 0;
        string root = EnsureAndGetGamePath();
        return await Task.Factory.StartNew(() =>
        {
            var tasks = BuildPreloadTasks(plan, _cachedResList!, root, ((NteGameManager)GameManager).AppId, token);
            if (!downloaded) return CalculateTotalBytes(tasks);
            long bytes = 0;
            foreach (DownloadTask task in tasks)
            {
                token.ThrowIfCancellationRequested();
                bytes += IsFileAlreadyValid(task, true, token, _verificationCache)
                    ? task.TotalSize : NteDownloadSpace.Capture(task).ResumableBytes;
            }
            return bytes;
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
    }

    internal async Task PreloadAsync(InstallProgressDelegate? progress, InstallProgressStateDelegate? state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GameManager.GetGamePath(out string? selectedRoot);
        if (string.IsNullOrWhiteSpace(selectedRoot)) return;
        string root = EnsureAndGetGamePath();
        using FileStream operation = NteGameOperation.Acquire(root);
        state?.Invoke(InstallProgressState.Preparing);
        // Availability can change while the launcher is open. Never act on yesterday's preview metadata.
        if (await InitAsync(token).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Cannot refresh official preload availability.");
        NtePreloadPlan? plan = await GetPreloadPlanAsync(token).ConfigureAwait(false);
        if (plan == null) return;
        await RunInstallAsync(progress, state, GameInstallerKind.Preload, token, operationHeld: true, preload: plan).ConfigureAwait(false);
    }

    internal static List<DownloadTask> BuildPreloadTasks(NtePreloadPlan plan, NteResListParser current,
        string gameRoot, string appId, CancellationToken token)
    {
        Dictionary<string, DownloadTask> previous = BuildDownloadTasks(current, gameRoot, false, false, token)
            .ToDictionary(t => t.OutputPath, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DownloadTask> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (DownloadTask target in BuildDownloadTasks(plan.Manifest, gameRoot, false, false, token))
        {
            token.ThrowIfCancellationRequested();
            previous.TryGetValue(target.OutputPath, out DownloadTask? old);
            bool sourceExists = old != null && File.Exists(old.OutputPath) && new FileInfo(old.OutputPath).Length == old.TotalSize;
            if (sourceExists && old!.TotalSize == target.TotalSize &&
                old.ExpectedMd5.Equals(target.ExpectedMd5, StringComparison.OrdinalIgnoreCase)) continue;
            NtePatchEntry? patch = sourceExists ? plan.Patches.Find(target, old) : null;
            NteResListEntry? resource = patch?.Resource ?? target.ResEntry;
            string md5 = patch?.Resource.Md5 ?? target.ExpectedMd5;
            long size = patch?.Resource.Filesize ?? target.TotalSize;
            string path = NtePreloadCache.PathFor(gameRoot, appId, md5, size);
            result.TryAdd(path, new()
            {
                Kind = patch != null ? DownloadTaskKind.DirectResource : target.Kind,
                ResEntry = resource, PakInfo = target.PakInfo, PakEntry = target.PakEntry,
                ExpectedMd5 = md5, TotalSize = size, OutputPath = path
            });
        }
        return [.. result.Values];
    }

    private List<DownloadTask> FindMissingPreloadTasks(List<DownloadTask> tasks, CancellationToken token,
        InstallProgressDelegate? report, Action<long, long> fileProgress)
    {
        List<DownloadTask> missing = [];
        InstallProgress progress = new() { TotalCountToDownload = tasks.Count, TotalBytesToDownload = CalculateTotalBytes(tasks) };
        foreach (DownloadTask task in tasks)
        {
            token.ThrowIfCancellationRequested();
            if (!IsFileAlreadyValid(task, true, token, _verificationCache, done => fileProgress(done, task.TotalSize)))
                missing.Add(task);
            progress.DownloadedCount++;
            progress.DownloadedBytes += task.TotalSize;
            report?.Invoke(in progress);
        }
        return missing;
    }

    private static DownloadTask CacheTask(DownloadTask target, string path) => new()
    {
        Kind = target.Kind, OutputPath = path, TotalSize = target.TotalSize, ExpectedMd5 = target.ExpectedMd5,
        ResEntry = target.ResEntry, PakInfo = target.PakInfo, PakEntry = target.PakEntry
    };

    internal List<DownloadTask> BuildUpdateTasks(List<DownloadTask> targets, string gameRoot, string appId, CancellationToken token)
    {
        NteUpdateContext context = new(gameRoot, appId, _verificationCache);
        Dictionary<string, DownloadTask> previous = _installedResList == null ? [] :
            BuildDownloadTasks(_installedResList, gameRoot, false, false, token).ToDictionary(t => t.OutputPath, StringComparer.OrdinalIgnoreCase);
        List<DownloadTask> result = [];
        List<NteUpdateOperation> operations = [];
        foreach (DownloadTask target in targets)
        {
            token.ThrowIfCancellationRequested();
            previous.TryGetValue(target.OutputPath, out DownloadTask? old);
            FileInfo installed = new(target.OutputPath);
            bool unchanged = old != null && old.TotalSize == target.TotalSize &&
                old.ExpectedMd5.Equals(target.ExpectedMd5, StringComparison.OrdinalIgnoreCase);
            if (installed.Exists && installed.Length == target.TotalSize &&
                (unchanged || IsFileAlreadyValid(target, true, token, _verificationCache))) continue;
            if (!NtePatchList.IsMd5(target.ExpectedMd5)) { result.Add(target); continue; }
            if (old != null && (!installed.Exists || installed.Length != old.TotalSize ||
                    _verificationCache.KnownValidity(target.OutputPath, old.TotalSize, old.ExpectedMd5) == false)) old = null;
            NteDownloadSpace actual = NteDownloadSpace.Capture(target);
            var cachedPartial = NteDownloadSpace.Capture(NteUpdateContext.CacheTask(
                context.PathFor(target.ExpectedMd5, target.TotalSize), target.ExpectedMd5, target.TotalSize));
            long fullRemaining = target.TotalSize - Math.Max(actual.ResumableBytes, cachedPartial.ResumableBytes);
            NtePatchRoute? route = _releasePatches.FindRoute(target, old,
                (md5, size) => context.Cached(md5, size, token), resource => context.Remaining(resource, token),
                (md5, size) => installed.Exists && installed.Length == size &&
                    _verificationCache.Verify(target.OutputPath, size, md5, token));
            if (route != null && route.NetworkBytes >= fullRemaining && route.Steps.Count != 0) route = null;
            NteUpdateOperation operation = new(target, context, route);
            operations.Add(operation);
            result.Add(operation.AsTask());
        }
        context.Register(operations);
        return result;
    }

    private void RestorePreloadedFiles(List<DownloadTask> targets, string gameRoot, string appId, CancellationToken token,
        InstallProgressDelegate? report, Action<long, long> fileProgress)
    {
        Dictionary<string, DownloadTask> previous = _installedResList == null ? [] :
            BuildDownloadTasks(_installedResList, gameRoot, false, false, token).ToDictionary(t => t.OutputPath, StringComparer.OrdinalIgnoreCase);
        InstallProgress progress = new() { TotalCountToDownload = targets.Count };
        foreach (DownloadTask target in targets)
        {
            token.ThrowIfCancellationRequested();
            report?.Invoke(in progress);
            progress.DownloadedCount++;
            if (!NtePatchList.IsMd5(target.ExpectedMd5)) continue;
            previous.TryGetValue(target.OutputPath, out DownloadTask? old);
            if (old != null && old.TotalSize == target.TotalSize &&
                old.ExpectedMd5.Equals(target.ExpectedMd5, StringComparison.OrdinalIgnoreCase)) continue;
            string cached = NtePreloadCache.PathFor(gameRoot, appId, target.ExpectedMd5, target.TotalSize);
            string temporary = target.OutputPath + ".tmp";
            if (_verificationCache.Verify(cached, target.TotalSize, target.ExpectedMd5, token, done => fileProgress(done, target.TotalSize)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                PrepareTemporaryFile(temporary, target.ExpectedMd5, target.TotalSize);
                File.Move(cached, temporary, overwrite: true);
                CompleteDownloadedFile(temporary, target.OutputPath, target.TotalSize, target.ExpectedMd5, token);
                _verificationCache.RememberVerified(target.OutputPath, target.TotalSize, target.ExpectedMd5);
                continue;
            }
            DownloadTask cachedTask = CacheTask(target, cached);
            long resumable = NteDownloadSpace.Capture(cachedTask).ResumableBytes;
            if (resumable > 0 && resumable > NteDownloadSpace.Capture(target).ResumableBytes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                File.Move(cached + ".tmp", temporary, overwrite: true);
                File.Move(cached + ".tmp.identity", temporary + ".identity", overwrite: true);
            }
            NtePatchEntry? patch = _releasePatches.Find(target, old);
            if (patch == null) continue;
            string patchPath = NtePreloadCache.PathFor(gameRoot, appId, patch.Resource.Md5, patch.Resource.Filesize);
            if (!File.Exists(patchPath)) continue;
            bool started = false, completed = false;
            try
            {
                if (!_verificationCache.Verify(patchPath, patch.Resource.Filesize, patch.Resource.Md5, token,
                        done => fileProgress(done, patch.Resource.Filesize)) ||
                    !_verificationCache.Verify(target.OutputPath, patch.OldSize, patch.OldMd5, token,
                        done => fileProgress(done, patch.OldSize))) continue;
                DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(gameRoot))!);
                if (drive.AvailableFreeSpace < checked(target.TotalSize + NteDownloadScheduler.ReserveBytes))
                    throw new IOException("Insufficient free space to safely apply the preloaded patch.");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                PrepareTemporaryFile(temporary, target.ExpectedMd5, target.TotalSize);
                started = true;
                NteHdiffPatch.Apply(target.OutputPath, patchPath, temporary, target.TotalSize, token, fileProgress);
                CompleteDownloadedFile(temporary, target.OutputPath, target.TotalSize, target.ExpectedMd5, token);
                _verificationCache.RememberVerified(target.OutputPath, target.TotalSize, target.ExpectedMd5);
                completed = true;
                File.Delete(patchPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "[NTE Preload] Cannot reuse patch for {File}; using the normal verified download.",
                    Path.GetFileName(target.OutputPath));
            }
            finally
            {
                if (started && !completed)
                {
                    File.Delete(temporary);
                    File.Delete(temporary + ".identity");
                }
            }
        }
    }
}
