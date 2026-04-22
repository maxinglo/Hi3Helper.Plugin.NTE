using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

[GeneratedComClass]
internal partial class NteCNGameInstaller(IGameManager gameManager) : GameInstallerBase(gameManager)
{
    private readonly HttpClient _downloadHttpClient = new PluginHttpClientBuilder()
        .SetAllowedDecompression(DecompressionMethods.GZip)
        .AllowCookies()
        .AllowRedirections()
        .AllowUntrustedCert()
        .Create();

    /// <summary>缓存的资源清单</summary>
    private NteResListParser? _cachedResList;

    /// <summary>
    /// 初始化安装器：
    /// 1. 初始化 GameManager 以获取 API 版本
    /// 2. 下载并解密 ResList.bin.zip
    /// 3. 解析资源清单
    /// </summary>
    protected override async Task<int> InitAsync(CancellationToken token)
    {
        SharedStatic.InstanceLogger.LogDebug("[NteCNInstaller::InitAsync] Starting initialization...");

        // 初始化 GameManager
        if (GameManager is not NteCNGameManager mgr)
            throw new InvalidOperationException("GameManager is not NteCNGameManager");

        int mgrResult = await mgr.InitAsyncInner(true, token).ConfigureAwait(false);
        if (mgrResult != 0)
        {
            SharedStatic.InstanceLogger.LogError("[NteCNInstaller::InitAsync] GameManager init failed: {Result}", mgrResult);
            return mgrResult;
        }

        GameManager.GetApiGameVersion(out GameVersion apiVer);
        string gameVersion = apiVer.ToString();
        string branchName = mgr.BranchName;

        SharedStatic.InstanceLogger.LogInformation(
            "[NteCNInstaller::InitAsync] API Version={V}, Branch={B}", gameVersion, branchName);

        // 下载 ResList.bin.zip
        byte[] resListXmlBytes = await DownloadAndDecryptResListAsync(branchName, gameVersion, token)
            .ConfigureAwait(false);

        // 解析资源清单
        _cachedResList = NteResListParser.Parse(resListXmlBytes);

        SharedStatic.InstanceLogger.LogInformation(
            "[NteCNInstaller::InitAsync] Parsed ResList: {ResCount} resources, {PakCount} paks, " +
            "{BvResCount} base-version resources, {BvPakCount} base-version paks, " +
            "total install size: {Size:F2} GB",
            _cachedResList.Resources.Count,
            _cachedResList.Paks.Count,
            _cachedResList.BaseVersionResources.Count,
            _cachedResList.BaseVersionPaks.Count,
            _cachedResList.GetTotalInstallSize() / 1024.0 / 1024.0 / 1024.0);

        return 0;
    }

    /// <summary>
    /// 获取游戏安装所需的总大小。
    /// </summary>
    protected override async Task<long> GetGameSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        if (_cachedResList == null)
            await InitAsync(token).ConfigureAwait(false);

        if (_cachedResList == null)
            return 0L;

        return gameInstallerKind switch
        {
            GameInstallerKind.Install => _cachedResList.GetTotalInstallSize(),
            GameInstallerKind.Update => _cachedResList.GetTotalInstallSize(), // 简化处理：更新时返回全量大小
            _ => 0L
        };
    }

    /// <summary>
    /// 获取已下载的文件总大小。
    /// </summary>
    protected override async Task<long> GetGameDownloadedSizeAsyncInner(GameInstallerKind gameInstallerKind, CancellationToken token)
    {
        if (_cachedResList == null)
            await InitAsync(token).ConfigureAwait(false);

        if (_cachedResList == null)
            return 0L;

        GameManager.GetGamePath(out string? gamePath);
        if (string.IsNullOrEmpty(gamePath))
            return 0L;

        return CalculateDownloadedBytes(_cachedResList, gamePath);
    }

    /// <summary>
    /// 开始全新安装。
    /// </summary>
    protected override async Task StartInstallAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        if (_cachedResList == null)
            await InitAsync(token).ConfigureAwait(false);

        await RunInstallAsync(progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 开始更新（当前实现：重新下载有变化的文件）。
    /// </summary>
    protected override async Task StartUpdateAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
    {
        if (_cachedResList == null)
            await InitAsync(token).ConfigureAwait(false);

        // 当前简化实现：和全新安装相同。
        // 已存在且大小匹配的文件会被跳过。
        await RunInstallAsync(progressDelegate, progressStateDelegate, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 预加载（当前未实现）。
    /// </summary>
    protected override Task StartPreloadAsyncInner(
        InstallProgressDelegate? progressDelegate,
        InstallProgressStateDelegate? progressStateDelegate,
        CancellationToken token)
        => Task.CompletedTask;

    /// <summary>
    /// 卸载游戏。
    /// </summary>
    protected override async Task UninstallAsyncInner(CancellationToken token)
    {
        GameManager.IsGameInstalled(out bool isInstalled);
        if (!isInstalled)
            return;

        GameManager.GetGamePath(out string? installPath);
        if (string.IsNullOrEmpty(installPath))
            return;

        SharedStatic.InstanceLogger.LogInformation("[NteCNInstaller::UninstallAsyncInner] Uninstalling from: {Path}", installPath);
        await Task.Run(() => Directory.Delete(installPath, true), token).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _downloadHttpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    #region ResList 下载和解密

    /// <summary>
    /// 下载 ResList.bin.zip，解压并解密，返回 XML 字节。
    /// </summary>
    private async Task<byte[]> DownloadAndDecryptResListAsync(
        string branchName, string gameVersion, CancellationToken token)
    {
        Exception? lastException = null;

        // 尝试所有 CDN
        for (int i = 0; i < 2; i++)
        {
            string url = NteConfigProvider.BuildResListUrl(branchName, gameVersion, i);

            try
            {
                SharedStatic.InstanceLogger.LogDebug(
                    "[NteCNInstaller::DownloadAndDecryptResListAsync] Downloading ResList from: {Url}", url);

                using HttpResponseMessage response = await _downloadHttpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using Stream zipStream = await response.Content.ReadAsStreamAsync(token)
                    .ConfigureAwait(false);

                // 解压 zip（包含 ResList.bin 和 lastdiff.bin）
                using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: false);

                ZipArchiveEntry? resListEntry = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("ResList.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        resListEntry = entry;
                        break;
                    }
                }

                if (resListEntry == null)
                    throw new InvalidDataException("ResList.bin not found in zip archive");

                // 读取加密数据
                await using Stream entryStream = resListEntry.Open();
                byte[] encryptedData = await ReadAllBytesAsync(entryStream, token).ConfigureAwait(false);

                // 解密
                byte[] decryptedXml = NteResListDecryptor.Decrypt(encryptedData.AsSpan());

                SharedStatic.InstanceLogger.LogInformation(
                    "[NteCNInstaller::DownloadAndDecryptResListAsync] Successfully decrypted ResList: {Size} bytes",
                    decryptedXml.Length);

                return decryptedXml;
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(ex,
                    "[NteCNInstaller::DownloadAndDecryptResListAsync] CDN {Index} failed: {Msg}", i, ex.Message);
                lastException = ex;
            }
        }

        throw lastException ?? new HttpRequestException("Failed to download ResList from all CDNs");
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken token)
    {
        using MemoryStream ms = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    #endregion
}
