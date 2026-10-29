using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;
using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// NTE 游戏安装流程编排（partial class）。
/// 处理全新安装和更新的核心逻辑。
/// </summary>
internal partial class NteCNGameInstaller
{
    /// <summary>并行下载数</summary>
    private const int MaxParallelDownloads = 4;

    /// <summary>
    /// 执行完整的安装流程。
    /// 1. 下载所有直接资源文件（<Res>）
    /// 2. 下载 Pak 归档并提取其中的 Entry
    /// 3. 验证 MD5
    /// </summary>
    private async Task RunInstallAsync(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        GameInstallerKind installerKind,
        CancellationToken token,
        HashSet<string>? repairPaths = null,
        bool operationHeld = false)
    {
        NteResListParser? resListParser = _cachedResList;
        if (resListParser == null)
            throw new InvalidOperationException("Resource list not initialized. Call InitAsync first.");

        string gamePath = EnsureAndGetGamePath();
        using FileStream? operation = operationHeld ? null : NteGameOperation.Acquire(gamePath);
        NteCNGameManager mgr = (NteCNGameManager)GameManager;
        NteRegionConfig? installedRegion = NteConfigProvider.DetectInstalledRegion(gamePath);
        if (installedRegion != null && installedRegion.AppId != mgr.AppId)
            throw new InvalidDataException("This game directory belongs to a different NTE region. Select a separate installation directory.");
        NteOfficialPatcherState.EnsureOfficialLauncherClosed(gamePath, mgr.Region);
        string[] cdnBaseUrls = mgr.GameResBaseUrls;
        string branchName = mgr.BranchName;
        // Build the complete target list separately from the unfinished work.
        progressStateDelegate?.Invoke(InstallProgressState.Preparing);
        List<DownloadTask> allTasks = BuildDownloadTasks(resListParser, gamePath, false, false, token);
        if (repairPaths != null) allTasks.RemoveAll(task => !repairPaths.Contains(task.OutputPath));
        long lastPreparation = 0, lastPreparationFile = 0;
        int lastPreparedCount = -1;
        void Preparing(in InstallProgress value)
        {
            long now = Environment.TickCount64;
            if (value.DownloadedCount == lastPreparedCount && now - lastPreparation < 100) return;
            lastPreparation = now; lastPreparedCount = value.DownloadedCount;
            progressDelegate?.Invoke(in value);
        }
        void PreparingFile(long done, long total)
        {
            long now = Environment.TickCount64;
            if (done != 0 && done != total && now - lastPreparationFile < 100) return;
            lastPreparationFile = now; ReportPerFileProgress(done, total);
        }
        List<DownloadTask> downloadTasks = repairPaths != null ? allTasks : await Task.Factory.StartNew(() =>
            BuildDownloadTasks(resListParser, gamePath, true, true, token, _installedResList,
                Preparing, _verificationCache, PreparingFile), token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        downloadTasks.Sort((left, right) => right.TotalSize.CompareTo(left.TotalSize));
        List<NteDownloadJob> jobs = downloadTasks.Select(task => new NteDownloadJob(task)).ToList();
        long totalBytes = CalculateTotalBytes(allTasks);
        int totalFileCount = allTasks.Count;
        long downloadedBytes = totalBytes - CalculateTotalBytes(downloadTasks) + jobs.Sum(job => job.Space.ResumableBytes);
        int downloadedCount = totalFileCount - jobs.Count;
        foreach (NteDownloadJob job in jobs) job.ReportedBytes = job.Space.ResumableBytes;
        DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(gamePath))!);
        NteDownloadScheduler scheduler = new(jobs, () => drive.AvailableFreeSpace);
        SharedStatic.InstanceLogger.LogInformation(
            "[NTE Download] Mode={Mode}, pending files={Files}, remaining network bytes={Remaining}, required additional space={Required}, network slots={Slots}, local verification threads=1",
            installerKind, jobs.Count, jobs.Sum(job => job.Space.NetworkBytes),
            NteDownloadSpace.RequiredFreeBytes(jobs.Select(job => job.Space), NteDownloadScheduler.ReserveBytes), MaxParallelDownloads);

        progressStateDelegate?.Invoke(InstallProgressState.Download);
        InstallProgress progress = new()
        {
            TotalBytesToDownload = totalBytes,
            TotalCountToDownload = totalFileCount,
            TotalStateToComplete = 2,
            StateCount = 1
        };

        object progressLock = new();
        NtePerFileProgress perFile = new();
        Dictionary<DownloadTask, int> fileOrder = [];
        for (int i = 0; i < downloadTasks.Count; i++) fileOrder.Add(downloadTasks[i], i);
        HashSet<NteDownloadJob> networkActive = [];
        InstallProgressState currentState = InstallProgressState.Download;
        long lastPublished = Environment.TickCount64;
        void PublishProgress(bool force = false)
        {
            lock (progressLock)
            {
                InstallProgressState next = downloadedBytes >= totalBytes ? InstallProgressState.Verify : InstallProgressState.Download;
                long now = Environment.TickCount64;
                if (!force && next == currentState && now - lastPublished < 100) return;
                lastPublished = now;
                InstallProgress snapshot = progress;
                snapshot.DownloadedBytes = downloadedBytes;
                snapshot.DownloadedCount = downloadedCount;
                var currentFile = perFile.Current;
                if (currentFile is { } current)
                    ReportPerFileProgress(current.Done, current.Total);
                else
                    ReportPerFileProgress(downloadedCount == totalFileCount ? 1 : 0, 1);
                if (currentState != next)
                {
                    currentState = next;
                    progressStateDelegate?.Invoke(next);
                }
                if (next == InstallProgressState.Verify)
                {
                    snapshot.StateCount = snapshot.DownloadedCount;
                    snapshot.TotalStateToComplete = totalFileCount;
                }
                progressDelegate?.Invoke(in snapshot);
            }
        }
        void ReportBytes(NteDownloadJob job, long value)
        {
            value = Math.Clamp(value, 0, job.File.TotalSize);
            long delta = value - job.ReportedBytes;
            job.ReportedBytes = value;
            downloadedBytes += delta;
            if (networkActive.Contains(job)) perFile.Add(fileOrder[job.File], delta);
        }
        PublishProgress(true);
        await scheduler.RunAsync(jobs, MaxParallelDownloads, async (job, downloadToken) =>
            {
                DownloadTask downloadTask = job.File;
                int order = fileOrder[downloadTask];
                lock (progressLock)
                {
                    networkActive.Add(job);
                    perFile.Begin(order, downloadTask.TotalSize);
                    perFile.Add(order, job.ReportedBytes);
                    PublishProgress(true);
                }
                try
                {
                    long attemptBytes = 0;
                    Action<long> progressCallback = bytesRead =>
                    {
                        lock (progressLock)
                        {
                            attemptBytes += bytesRead;
                            NteDownloadSpace? current = bytesRead < 0 ? NteDownloadSpace.Capture(downloadTask) : null;
                            long temporary = current?.TemporaryBytes ?? Math.Clamp(attemptBytes, 0, downloadTask.TotalSize);
                            Volatile.Write(ref job.TemporaryBytes, temporary);
                            bool firstBytes = job.ReportedBytes == 0 && (current?.ResumableBytes ?? attemptBytes) > 0;
                            ReportBytes(job, current?.ResumableBytes ?? attemptBytes);
                            PublishProgress(firstBytes);
                        }
                    };
                    string[] roots = cdnBaseUrls.Skip(job.Attempt % cdnBaseUrls.Length).Concat(cdnBaseUrls.Take(job.Attempt % cdnBaseUrls.Length)).ToArray();
                    switch (downloadTask.Kind)
                    {
                        case DownloadTaskKind.DirectResource:
                        {
                            NteResListEntry res = downloadTask.ResEntry!;
                            if (res.HasBlocks)
                            {
                                await DownloadBlockedResourceAsync(roots, branchName, res,
                                    downloadTask.OutputPath, downloadToken, progressCallback, true).ConfigureAwait(false);
                            }
                            else
                            {
                                await DownloadResourceFileAsync(roots, branchName,
                                    res.Md5, res.Filesize,
                                    downloadTask.OutputPath, downloadToken, progressCallback, true).ConfigureAwait(false);
                            }
                            break;
                        }

                        case DownloadTaskKind.PakEntry:
                        {
                            await DownloadPakEntryAsync(roots, branchName,
                                downloadTask.PakInfo!, downloadTask.PakEntry!,
                                downloadTask.OutputPath, downloadToken, progressCallback, true).ConfigureAwait(false);
                            break;
                        }
                    }

                }
                finally
                {
                    lock (progressLock) { networkActive.Remove(job); perFile.End(order); PublishProgress(true); }
                }
            }, (job, localToken) =>
            {
                DownloadTask file = job.File;
                string temporary = file.OutputPath + ".tmp";
                if (file.TotalSize == 0 && !File.Exists(temporary))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                    PrepareTemporaryFile(temporary, file.ExpectedMd5, 0);
                    File.Create(temporary).Dispose();
                }
                CompleteDownloadedFile(temporary, file.OutputPath, file.TotalSize, file.ExpectedMd5, localToken);
                _verificationCache.RememberVerified(file.OutputPath, file.TotalSize, file.ExpectedMd5);
            }, job =>
            {
                lock (progressLock) { ReportBytes(job, job.File.TotalSize); downloadedCount++; PublishProgress(true); }
            }, (job, error) =>
            {
                lock (progressLock) { ReportBytes(job, job.Space.ResumableBytes); PublishProgress(true); }
                SharedStatic.InstanceLogger.LogWarning("[NTE Download] Retry {Attempt}/3 for {File}: {Error}", job.Attempt + 1, Path.GetFileName(job.File.OutputPath), error.Message);
            }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        // 保存版本信息
        if (_originalRemoteManifest != null && _selectedManifest != null && mgr.ReleaseConfigXml != null)
            NteOfficialPatcherState.Commit(gamePath, branchName, mgr.AppId, mgr.ReleaseConfigXml,
                _originalRemoteManifest, _selectedManifest, token);
        GameManager.GetApiGameVersion(out GameVersion apiVer);
        GameManager.SetCurrentGameVersion(in apiVer);
        GameManager.SaveConfig();

        progressStateDelegate?.Invoke(InstallProgressState.Completed);
        progress.StateCount = 2;
        progress.DownloadedBytes = totalBytes;
        progress.DownloadedCount = totalFileCount;
        progressDelegate?.Invoke(in progress);

        SharedStatic.InstanceLogger.LogInformation(
            "[NteCNInstaller::RunInstallAsync] Installation completed. Version set to {Ver}", apiVer);
    }

    /// <summary>
    /// 检查文件是否已经存在且大小正确。
    /// </summary>
    private static bool IsFileAlreadyValid(DownloadTask task, bool verifyIntegrity,
        CancellationToken token, NteFileVerificationCache? cache = null, Action<long>? progress = null)
    {
        if (!File.Exists(task.OutputPath))
            return false;

        try
        {
            FileInfo fi = new(task.OutputPath);
            if (fi.Length != task.TotalSize)
                return false;

            if (!verifyIntegrity || string.IsNullOrWhiteSpace(task.ExpectedMd5))
                return true;

            return (cache ?? new NteFileVerificationCache()).Verify(task.OutputPath, task.TotalSize,
                task.ExpectedMd5, token, progress);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 计算已下载文件的总大小。
    /// </summary>
    internal static List<DownloadTask> BuildDownloadTasks(
        NteResListParser resList,
        string gamePath,
        bool onlyMissing,
        bool verifyIntegrity,
        CancellationToken token,
        NteResListParser? installedResList = null,
        InstallProgressDelegate? verificationProgress = null,
        NteFileVerificationCache? verificationCache = null,
        Action<long, long>? perFileProgress = null)
    {
        List<DownloadTask> downloadTasks = [];

        AddDirectResourceTasks(downloadTasks, resList.Resources, gamePath);
        AddPakEntryTasks(downloadTasks, resList.Paks, gamePath);
        AddDirectResourceTasks(downloadTasks, resList.BaseVersionResources, gamePath);
        AddPakEntryTasks(downloadTasks, resList.BaseVersionPaks, gamePath);

        if (!onlyMissing)
            return downloadTasks;

        Dictionary<string, (long Size, string Md5)> installed = new(StringComparer.OrdinalIgnoreCase);
        if (installedResList != null)
        {
            List<DownloadTask> previous = BuildDownloadTasks(installedResList, gamePath, false, false, token);
            foreach (DownloadTask task in previous) installed[task.OutputPath] = (task.TotalSize, task.ExpectedMd5);
        }
        InstallProgress progress = new() { TotalCountToDownload = downloadTasks.Count,
            TotalStateToComplete = downloadTasks.Count, TotalBytesToDownload = CalculateTotalBytes(downloadTasks) };
        List<DownloadTask> pendingTasks = [];
        foreach (DownloadTask task in downloadTasks)
        {
            token.ThrowIfCancellationRequested();
            bool unchanged = installed.TryGetValue(task.OutputPath, out var prior) &&
                prior.Size == task.TotalSize && prior.Md5.Equals(task.ExpectedMd5, StringComparison.OrdinalIgnoreCase);
            perFileProgress?.Invoke(0, task.TotalSize);
            long beforeBytes = progress.DownloadedBytes;
            void ReportHash(long bytes)
            {
                InstallProgress snapshot = progress;
                snapshot.DownloadedBytes = beforeBytes + Math.Min(bytes, task.TotalSize);
                perFileProgress?.Invoke(Math.Min(bytes, task.TotalSize), task.TotalSize);
                verificationProgress?.Invoke(in snapshot);
            }
            // Size queries only estimate changed entries. Hashing belongs to the cancellable, visible update phase.
            bool estimateChanged = !verifyIntegrity && installedResList != null && !unchanged;
            if (estimateChanged || !IsFileAlreadyValid(task, verifyIntegrity && !unchanged, token,
                    verificationCache, ReportHash))
                pendingTasks.Add(task);
            progress.DownloadedBytes += task.TotalSize;
            progress.DownloadedCount++;
            progress.StateCount++;
            perFileProgress?.Invoke(task.TotalSize, task.TotalSize);
            verificationProgress?.Invoke(in progress);
        }

        return pendingTasks;
    }

    private static void AddDirectResourceTasks(List<DownloadTask> downloadTasks, IEnumerable<NteResListEntry> resources, string gamePath)
    {
        foreach (NteResListEntry res in resources)
        {
            string outputPath = ResolveResourcePath(gamePath, res.Filename);
            downloadTasks.Add(new DownloadTask
            {
                Kind = DownloadTaskKind.DirectResource,
                ResEntry = res,
                OutputPath = outputPath,
                TotalSize = res.Filesize,
                ExpectedMd5 = res.Md5
            });
        }
    }

    private static void AddPakEntryTasks(List<DownloadTask> downloadTasks, IEnumerable<NtePakInfo> paks, string gamePath)
    {
        foreach (NtePakInfo pak in paks)
        {
            foreach (NtePakEntry entry in pak.Entries)
            {
                string outputPath = ResolveResourcePath(gamePath, entry.Name);
                downloadTasks.Add(new DownloadTask
                {
                    Kind = DownloadTaskKind.PakEntry,
                    PakInfo = pak,
                    PakEntry = entry,
                    OutputPath = outputPath,
                    TotalSize = entry.Size,
                    ExpectedMd5 = entry.Md5
                });
            }
        }
    }

    internal static string ResolveResourcePath(string gamePath, string relativePath)
    {
        string root = Path.GetFullPath(gamePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Resource path escapes game directory: {relativePath}");
        return path;
    }

    private static long CalculateTotalBytes(IEnumerable<DownloadTask> downloadTasks)
    {
        long total = 0;
        foreach (DownloadTask task in downloadTasks)
            total += task.TotalSize;

        return total;
    }

    internal long CalculateDownloadedBytes(IEnumerable<DownloadTask> downloadTasks, bool includeCompletedFiles = true)
    {
        long total = 0;

        foreach (DownloadTask task in downloadTasks)
        {
            bool complete = File.Exists(task.OutputPath) && new FileInfo(task.OutputPath).Length == task.TotalSize &&
                (includeCompletedFiles || _verificationCache.IsVerified(task.OutputPath, task.TotalSize, task.ExpectedMd5));
            total += complete ? task.TotalSize : NteDownloadSpace.Capture(task).ResumableBytes;
        }

        return total;
    }

    internal enum DownloadTaskKind
    {
        DirectResource,
        PakEntry
    }

    internal sealed class DownloadTask
    {
        public DownloadTaskKind Kind { get; init; }
        public NteResListEntry? ResEntry { get; init; }
        public NtePakInfo? PakInfo { get; init; }
        public NtePakEntry? PakEntry { get; init; }
        public string OutputPath { get; init; } = string.Empty;
        public long TotalSize { get; init; }
        public string ExpectedMd5 { get; init; } = string.Empty;
    }
}
