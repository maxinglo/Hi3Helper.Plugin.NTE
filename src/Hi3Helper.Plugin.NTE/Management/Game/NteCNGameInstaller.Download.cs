using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// NTE 游戏安装器的下载辅助方法（partial class）。
/// 提供文件下载、断点续传、CDN 回退、分块下载等功能。
/// </summary>
internal partial class NteCNGameInstaller
{
    /// <summary>
    /// 从 CDN 下载文件，支持断点续传和 CDN 回退。
    /// </summary>
    /// <param name="cdnBaseUrls">CDN 基础 URL 列表</param>
    /// <param name="branchName">分支名称</param>
    /// <param name="md5">文件 MD5</param>
    /// <param name="filesize">文件大小</param>
    /// <param name="outputPath">输出文件路径</param>
    /// <param name="token">取消令牌</param>
    /// <param name="progressCallback">进度回调（参数为本次新增字节数）</param>
    internal async Task DownloadResourceFileAsync(
        string[] cdnBaseUrls,
        string branchName,
        string md5,
        long filesize,
        string outputPath,
        CancellationToken token,
        Action<long>? progressCallback = null, bool deferVerification = false)
    {
        Exception? lastException = null;

        foreach (string cdnBase in cdnBaseUrls)
        {
            string url = Config.NteConfigProvider.BuildResourceUrl(cdnBase, branchName, md5, filesize);
            long reported = 0;
            void Report(long bytes) { reported += bytes; progressCallback?.Invoke(bytes); }

            try
            {
                await DownloadWholeFileAsync(new Uri(url), outputPath, token, Report, filesize, md5, deferVerification)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                token.ThrowIfCancellationRequested();
                if (reported != 0) progressCallback?.Invoke(-reported);
                SharedStatic.InstanceLogger.LogWarning(
                    "[NteCNInstaller::DownloadResourceFileAsync] CDN {Url} failed: {Msg}", url, ex.Message);
                lastException = ex;
                continue;
            }
            return;
        }

        throw lastException ?? new HttpRequestException($"All CDN attempts failed for MD5={md5}");
    }

    /// <summary>
    /// 通过 HTTP Range 请求从 Pak 文件中下载指定范围的字节。
    /// </summary>
    internal async Task DownloadPakEntryAsync(
        string[] cdnBaseUrls,
        string branchName,
        NtePakInfo pakInfo,
        NtePakEntry entry,
        string outputPath,
        CancellationToken token,
        Action<long>? progressCallback = null, bool deferVerification = false)
    {
        Exception? lastException = null;

        foreach (string cdnBase in cdnBaseUrls)
        {
            string url = pakInfo.BuildDownloadUrl(cdnBase, branchName);
            long reported = 0;
            void Report(long bytes) { reported += bytes; progressCallback?.Invoke(bytes); }
            try
            {
                await DownloadRangeToFileAsync(
                    new Uri(url), outputPath,
                    entry.Offset, entry.Size,
                    token, Report, entry.Md5, deferVerification).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                token.ThrowIfCancellationRequested();
                if (reported != 0) progressCallback?.Invoke(-reported);
                SharedStatic.InstanceLogger.LogWarning(
                    "[NteCNInstaller::DownloadPakEntryAsync] CDN {Url} failed for entry {Name}: {Msg}",
                    url, entry.Name, ex.Message);
                lastException = ex;
                continue;
            }
            return;
        }

        throw lastException ?? new HttpRequestException($"All CDN attempts failed for Pak entry: {entry.Name}");
    }

    /// <summary>
    /// 下载整个文件，支持断点续传。
    /// </summary>
    internal async Task DownloadWholeFileAsync(
        Uri uri, string outputPath, CancellationToken token, Action<long>? progressCallback,
        long expectedSize = -1, string? expectedMd5 = null, bool deferVerification = false)
    {
        string tempPath = outputPath + ".tmp";
        string? dir = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        PrepareTemporaryFile(tempPath, expectedMd5 ?? uri.AbsoluteUri, expectedSize);

        long existingLength = 0;
        if (File.Exists(tempPath))
        {
            existingLength = new FileInfo(tempPath).Length;
            if (expectedSize >= 0 && existingLength == expectedSize)
            {
                progressCallback?.Invoke(existingLength);
                if (!deferVerification) await CompleteDownloadAsync(tempPath, outputPath, expectedSize, expectedMd5, token).ConfigureAwait(false);
                return;
            }
        }

        SharedStatic.InstanceLogger.LogTrace(
            "[NteCNInstaller::DownloadWholeFileAsync] Downloading {Uri} -> {Temp} (resume from {Existing} bytes)",
            uri, tempPath, existingLength);

        HttpResponseMessage? resp = null;
        bool resuming = false;

        try
        {
            HttpRequestMessage request = new(HttpMethod.Get, uri);
            if (existingLength > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingLength, null);
            }

            resp = await _downloadHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (existingLength > 0)
            {
                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    ValidateRangeResponse(resp, existingLength, expectedSize - 1);
                    resuming = true;
                }
                else if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // 服务器返回 416，删除旧的临时文件重新下载
                    resp.Dispose();
                    resp = null;
                    try { File.Delete(tempPath); } catch { /* ignored */ }
                    existingLength = 0;

                    HttpRequestMessage freshRequest = new(HttpMethod.Get, uri);
                    resp = await _downloadHttpClient.SendAsync(freshRequest, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    // 服务器不支持 Range，从头开始
                    existingLength = 0;
                }
            }

            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new HttpRequestException(
                    $"Failed to GET {uri}: {(int)resp.StatusCode} {resp.StatusCode}",
                    null, resp.StatusCode);
            }

            await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            FileMode fileMode = resuming ? FileMode.Append : FileMode.Create;
            await using FileStream fs = new(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (resuming)
            {
                progressCallback?.Invoke(existingLength);
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            nint speedLimiterContext = SpeedLimiterService.CreateServiceContext();
            try
            {
                int read;
                while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                    progressCallback?.Invoke(read);
                    await SpeedLimiterService.AddBytesOrWaitAsync(speedLimiterContext, read, token)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                SpeedLimiterService.FreeServiceContext(speedLimiterContext);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            resp?.Dispose();
        }

        if (!deferVerification) await CompleteDownloadAsync(tempPath, outputPath, expectedSize, expectedMd5, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 使用 HTTP Range 请求下载文件的指定范围到目标文件。
    /// </summary>
    internal async Task DownloadRangeToFileAsync(
        Uri uri, string outputPath, long rangeStart, long rangeLength,
        CancellationToken token, Action<long>? progressCallback, string? expectedMd5 = null, bool deferVerification = false)
    {
        string tempPath = outputPath + ".tmp";
        string? dir = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        PrepareTemporaryFile(tempPath, expectedMd5 ?? $"{uri.AbsoluteUri}:{rangeStart}", rangeLength);

        long existingLength = 0;
        if (File.Exists(tempPath))
        {
            existingLength = new FileInfo(tempPath).Length;
            if (existingLength == rangeLength)
            {
                // 已经下载完成
                progressCallback?.Invoke(rangeLength);
                if (!deferVerification) await CompleteDownloadAsync(tempPath, outputPath, rangeLength, expectedMd5, token).ConfigureAwait(false);
                return;
            }
            if (existingLength > rangeLength) { File.Delete(tempPath); existingLength = 0; }
        }

        long actualRangeStart = rangeStart + existingLength;
        long rangeEnd = rangeStart + rangeLength - 1;

        HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(actualRangeStart, rangeEnd);

        using HttpResponseMessage resp = await _downloadHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        ValidateRangeResponse(resp, actualRangeStart, rangeEnd);

        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException(
                $"Failed to GET {uri} range {actualRangeStart}-{rangeEnd}: {(int)resp.StatusCode}",
                null, resp.StatusCode);
        }

        await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

        FileMode fileMode = existingLength > 0 ? FileMode.Append : FileMode.Create;
        await using FileStream fs = new(tempPath, fileMode, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (existingLength > 0)
        {
            progressCallback?.Invoke(existingLength);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        nint speedLimiterContext = SpeedLimiterService.CreateServiceContext();
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                progressCallback?.Invoke(read);
                await SpeedLimiterService.AddBytesOrWaitAsync(speedLimiterContext, read, token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            SpeedLimiterService.FreeServiceContext(speedLimiterContext);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await fs.DisposeAsync().ConfigureAwait(false);
        if (!deferVerification) await CompleteDownloadAsync(tempPath, outputPath, rangeLength, expectedMd5, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 分块下载大文件（使用 Block 信息）。
    /// </summary>
    internal async Task DownloadBlockedResourceAsync(
        string[] cdnBaseUrls,
        string branchName,
        NteResListEntry entry,
        string outputPath,
        CancellationToken token,
        Action<long>? progressCallback = null, bool deferVerification = false)
    {
        Exception? lastException = null;

        foreach (string cdnBase in cdnBaseUrls)
        {
            string url = entry.BuildDownloadUrl(cdnBase, branchName);
            long reported = 0;
            void Report(long bytes) { reported += bytes; progressCallback?.Invoke(bytes); }

            try
            {
                await DownloadBlockedFileAsync(new Uri(url), outputPath, entry, token, Report, deferVerification)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                token.ThrowIfCancellationRequested();
                if (reported != 0) progressCallback?.Invoke(-reported);
                SharedStatic.InstanceLogger.LogWarning(
                    "[NteCNInstaller::DownloadBlockedResourceAsync] CDN {Url} failed: {Msg}", url, ex.Message);
                lastException = ex;
                continue;
            }
            return;
        }

        throw lastException ?? new HttpRequestException($"All CDN attempts failed for: {entry.Filename}");
    }

    /// <summary>
    /// 使用分块信息下载大文件。每个 Block 使用 HTTP Range 请求。
    /// </summary>
    private async Task DownloadBlockedFileAsync(
        Uri uri, string outputPath, NteResListEntry entry,
        CancellationToken token, Action<long>? progressCallback, bool deferVerification = false)
    {
        string tempPath = outputPath + ".tmp";
        string? dir = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        PrepareTemporaryFile(tempPath, entry.Md5, entry.Filesize);

        long existingLength = 0;
        if (File.Exists(tempPath))
        {
            existingLength = new FileInfo(tempPath).Length;
        }

        await using (FileStream fs = new(tempPath,
            existingLength > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
            nint speedLimiterContext = SpeedLimiterService.CreateServiceContext();

            try
            {
                foreach (NteResBlock block in entry.Blocks)
                {
                    token.ThrowIfCancellationRequested();

                    long blockEnd = block.Start + block.Size - 1;

                    // 跳过已下载的完整块
                    if (existingLength > 0 && existingLength >= blockEnd + 1)
                    {
                        progressCallback?.Invoke(block.Size);
                        continue;
                    }

                    // 计算块内的续传偏移
                    long resumeOffset = 0;
                    if (existingLength > 0 && existingLength > block.Start)
                    {
                        resumeOffset = existingLength - block.Start;
                        progressCallback?.Invoke(resumeOffset);
                    }

                    HttpRequestMessage request = new(HttpMethod.Get, uri);
                    request.Headers.Range = new RangeHeaderValue(block.Start + resumeOffset, blockEnd);

                    using HttpResponseMessage resp = await _downloadHttpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    ValidateRangeResponse(resp, block.Start + resumeOffset, blockEnd);

                    if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new HttpRequestException(
                            $"Failed block download {uri} range {block.Start + resumeOffset}-{blockEnd}: {(int)resp.StatusCode}",
                            null, resp.StatusCode);
                    }

                    await using Stream content = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                    int read;
                    while ((read = await content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        progressCallback?.Invoke(read);
                        await SpeedLimiterService.AddBytesOrWaitAsync(speedLimiterContext, read, token)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                SpeedLimiterService.FreeServiceContext(speedLimiterContext);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (!deferVerification) await CompleteDownloadAsync(tempPath, outputPath, entry.Filesize, entry.Md5, token).ConfigureAwait(false);
    }

    internal static void ValidateRangeResponse(HttpResponseMessage response, long from, long to)
    {
        response.EnsureSuccessStatusCode();
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != from || range.To != to)
            throw new InvalidDataException("CDN did not return the requested byte range.");
        if (response.Content.Headers.ContentLength is { } length && length != to - from + 1)
            throw new InvalidDataException("CDN range body length does not match the resource manifest.");
    }

    internal static Task CompleteDownloadAsync(string temporaryPath, string outputPath, long expectedSize,
        string? expectedMd5, CancellationToken token)
        => Task.Run(() => CompleteDownloadedFile(temporaryPath, outputPath, expectedSize, expectedMd5, token), token);

    internal static void CompleteDownloadedFile(string temporaryPath, string outputPath, long expectedSize,
        string? expectedMd5, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expectedSize >= 0 && new FileInfo(temporaryPath).Length != expectedSize)
            throw new InvalidDataException($"Downloaded file size does not match the manifest: {Path.GetFileName(outputPath)}");
        if (!string.IsNullOrWhiteSpace(expectedMd5))
        {
            using FileStream input = new(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            string actual;
            try
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
                actual = Convert.ToHexString(hash.GetHashAndReset());
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            token.ThrowIfCancellationRequested();
            if (!actual.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                input.Close();
                File.Delete(temporaryPath);
                throw new InvalidDataException($"Downloaded file MD5 does not match the manifest: {Path.GetFileName(outputPath)}");
            }
        }
        token.ThrowIfCancellationRequested();
        File.Move(temporaryPath, outputPath, overwrite: true);
        File.Delete(temporaryPath + ".identity");
    }

    internal static void PrepareTemporaryFile(string path, string identity, long size)
    {
        string marker = path + ".identity";
        string expected = identity + ":" + size.ToString(CultureInfo.InvariantCulture);
        if (File.Exists(path) && (!File.Exists(marker) || File.ReadAllText(marker) != expected ||
            (size >= 0 && new FileInfo(path).Length > size)))
            File.Delete(path);
        File.WriteAllText(marker, expected);
    }
}
