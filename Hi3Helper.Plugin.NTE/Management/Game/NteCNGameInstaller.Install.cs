using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

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
        CancellationToken token)
    {
        NteResListParser? resListParser = _cachedResList;
        if (resListParser == null)
            throw new InvalidOperationException("Resource list not initialized. Call InitAsync first.");

        string gamePath = EnsureAndGetGamePath();
        NteCNGameManager mgr = (NteCNGameManager)GameManager;
        string[] cdnBaseUrls = mgr.GameResBaseUrls;
        string branchName = mgr.BranchName;

        // --- 阶段 1: 收集全部下载任务 ---
        progressStateDelegate?.Invoke(InstallProgressState.Preparing);

        bool onlyMissing = installerKind == GameInstallerKind.Update;
        List<DownloadTask> downloadTasks = BuildDownloadTasks(resListParser, gamePath, onlyMissing);
        long totalBytes = CalculateTotalBytes(downloadTasks);
        int totalFileCount = downloadTasks.Count;

        SharedStatic.InstanceLogger.LogInformation(
            "[NteCNInstaller::RunInstallAsync] Mode={Mode}, Total files: {Count}, Total size: {Size} ({SizeMB:F2} MB)",
            installerKind, totalFileCount, totalBytes, totalBytes / 1024.0 / 1024.0);

        // --- 阶段 2: 执行下载 ---
        progressStateDelegate?.Invoke(InstallProgressState.Download);

        long downloadedBytes = 0;
        int downloadedCount = 0;

        InstallProgress progress = new()
        {
            TotalBytesToDownload = totalBytes,
            TotalCountToDownload = totalFileCount,
            TotalStateToComplete = 2,
            StateCount = 1
        };

        using SemaphoreSlim semaphore = new(MaxParallelDownloads, MaxParallelDownloads);
        List<Task> activeTasks = [];

        foreach (DownloadTask downloadTask in downloadTasks)
        {
            token.ThrowIfCancellationRequested();

            await semaphore.WaitAsync(token).ConfigureAwait(false);

            Task task = Task.Run(async () =>
            {
                try
                {
                    // 检查文件是否已经完整存在
                    if (IsFileAlreadyValid(downloadTask))
                    {
                        long size = downloadTask.TotalSize;
                        Interlocked.Add(ref downloadedBytes, size);
                        Interlocked.Increment(ref downloadedCount);

                        progress.DownloadedBytes = Interlocked.Read(ref downloadedBytes);
                        progress.DownloadedCount = downloadedCount;
                        progressDelegate?.Invoke(in progress);
                        return;
                    }

                    Action<long> progressCallback = bytesRead =>
                    {
                        Interlocked.Add(ref downloadedBytes, bytesRead);
                        progress.DownloadedBytes = Interlocked.Read(ref downloadedBytes);
                        progress.DownloadedCount = downloadedCount;
                        progressDelegate?.Invoke(in progress);
                    };

                    switch (downloadTask.Kind)
                    {
                        case DownloadTaskKind.DirectResource:
                        {
                            NteResListEntry res = downloadTask.ResEntry!;
                            if (res.HasBlocks)
                            {
                                await DownloadBlockedResourceAsync(cdnBaseUrls, branchName, res,
                                    downloadTask.OutputPath, token, progressCallback).ConfigureAwait(false);
                            }
                            else
                            {
                                await DownloadResourceFileAsync(cdnBaseUrls, branchName,
                                    res.Md5, res.Filesize,
                                    downloadTask.OutputPath, token, progressCallback).ConfigureAwait(false);
                            }
                            break;
                        }

                        case DownloadTaskKind.PakEntry:
                        {
                            await DownloadPakEntryAsync(cdnBaseUrls, branchName,
                                downloadTask.PakInfo!, downloadTask.PakEntry!,
                                downloadTask.OutputPath, token, progressCallback).ConfigureAwait(false);
                            break;
                        }
                    }

                    Interlocked.Increment(ref downloadedCount);
                    progress.DownloadedCount = downloadedCount;
                    progressDelegate?.Invoke(in progress);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SharedStatic.InstanceLogger.LogError(ex,
                        "[NteCNInstaller::RunInstallAsync] Failed to download: {Path}", downloadTask.OutputPath);
                    throw;
                }
                finally
                {
                    semaphore.Release();
                }
            }, token);

            activeTasks.Add(task);
        }

        await Task.WhenAll(activeTasks).ConfigureAwait(false);

        // --- 阶段 3: 完成 ---
        progressStateDelegate?.Invoke(InstallProgressState.Completed);
        progress.StateCount = 2;
        progressDelegate?.Invoke(in progress);

        // 保存版本信息
        GameManager.GetApiGameVersion(out GameVersion apiVer);
        GameManager.SetCurrentGameVersion(in apiVer);
        GameManager.SaveConfig();

        SharedStatic.InstanceLogger.LogInformation(
            "[NteCNInstaller::RunInstallAsync] Installation completed. Version set to {Ver}", apiVer);
    }

    /// <summary>
    /// 检查文件是否已经存在且大小正确。
    /// </summary>
    private static bool IsFileAlreadyValid(DownloadTask task)
    {
        if (!File.Exists(task.OutputPath))
            return false;

        try
        {
            FileInfo fi = new(task.OutputPath);
            return fi.Length == task.TotalSize;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 计算已下载文件的总大小。
    /// </summary>
    private static List<DownloadTask> BuildDownloadTasks(NteResListParser resList, string gamePath, bool onlyMissing)
    {
        List<DownloadTask> downloadTasks = [];

        AddDirectResourceTasks(downloadTasks, resList.Resources, gamePath);
        AddPakEntryTasks(downloadTasks, resList.Paks, gamePath);
        AddDirectResourceTasks(downloadTasks, resList.BaseVersionResources, gamePath);
        AddPakEntryTasks(downloadTasks, resList.BaseVersionPaks, gamePath);

        if (!onlyMissing)
            return downloadTasks;

        List<DownloadTask> pendingTasks = [];
        foreach (DownloadTask task in downloadTasks)
        {
            if (!IsFileAlreadyValid(task))
                pendingTasks.Add(task);
        }

        return pendingTasks;
    }

    private static void AddDirectResourceTasks(List<DownloadTask> downloadTasks, IEnumerable<NteResListEntry> resources, string gamePath)
    {
        foreach (NteResListEntry res in resources)
        {
            string outputPath = Path.Combine(gamePath, res.Filename.Replace('/', Path.DirectorySeparatorChar));
            downloadTasks.Add(new DownloadTask
            {
                Kind = DownloadTaskKind.DirectResource,
                ResEntry = res,
                OutputPath = outputPath,
                TotalSize = res.Filesize
            });
        }
    }

    private static void AddPakEntryTasks(List<DownloadTask> downloadTasks, IEnumerable<NtePakInfo> paks, string gamePath)
    {
        foreach (NtePakInfo pak in paks)
        {
            foreach (NtePakEntry entry in pak.Entries)
            {
                string outputPath = Path.Combine(gamePath, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                downloadTasks.Add(new DownloadTask
                {
                    Kind = DownloadTaskKind.PakEntry,
                    PakInfo = pak,
                    PakEntry = entry,
                    OutputPath = outputPath,
                    TotalSize = entry.Size
                });
            }
        }
    }

    private static long CalculateTotalBytes(IEnumerable<DownloadTask> downloadTasks)
    {
        long total = 0;
        foreach (DownloadTask task in downloadTasks)
            total += task.TotalSize;

        return total;
    }

    private long CalculateDownloadedBytes(IEnumerable<DownloadTask> downloadTasks)
    {
        long total = 0;

        foreach (DownloadTask task in downloadTasks)
        {
            long downloaded = GetFileOrTempSize(task.OutputPath);
            total += downloaded > task.TotalSize ? task.TotalSize : downloaded;
        }

        return total;
    }

    /// <summary>
    /// 获取文件或其临时文件的大小。
    /// </summary>
    private static long GetFileOrTempSize(string path)
    {
        try
        {
            if (File.Exists(path))
                return new FileInfo(path).Length;

            string tempPath = path + ".tmp";
            if (File.Exists(tempPath))
                return new FileInfo(tempPath).Length;
        }
        catch
        {
            // ignored
        }

        return 0;
    }

    private enum DownloadTaskKind
    {
        DirectResource,
        PakEntry
    }

    private sealed class DownloadTask
    {
        public DownloadTaskKind Kind { get; init; }
        public NteResListEntry? ResEntry { get; init; }
        public NtePakInfo? PakInfo { get; init; }
        public NtePakEntry? PakEntry { get; init; }
        public string OutputPath { get; init; } = string.Empty;
        public long TotalSize { get; init; }
    }
}
