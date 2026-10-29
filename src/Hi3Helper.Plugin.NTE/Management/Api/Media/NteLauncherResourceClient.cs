using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal sealed class NteLauncherResourceClient(HttpClient httpClient, string refererUrl)
{
    internal async Task<string> ReadTextAsync(string url, CancellationToken token)
    {
        using MemoryStream output = new();
        await DownloadAsync(url, output, token).ConfigureAwait(false);
        output.Position = 0;
        using StreamReader reader = new(output);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }

    internal Task ExtractAssetAsync(string archiveUrl, Stream output, CancellationToken token)
        => DownloadAsync(archiveUrl, output, token);

    internal async Task<byte[]> ReadBytesAsync(string url, CancellationToken token)
    {
        if (Path.IsPathFullyQualified(url) && !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return await File.ReadAllBytesAsync(url, token).ConfigureAwait(false);
        Uri uri = new(url);
        if (uri.IsFile)
            return await File.ReadAllBytesAsync(uri.LocalPath, token).ConfigureAwait(false);
        using MemoryStream output = new();
        await DownloadAsync(url, output, token).ConfigureAwait(false);
        return output.ToArray();
    }

    private async Task DownloadAsync(string url, Stream output, CancellationToken token)
    {
        Exception? lastError = null;
        foreach (Uri candidate in GetCandidates(new Uri(url)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                output.Position = 0;
                output.SetLength(0);
                using HttpRequestMessage request = new(HttpMethod.Get, candidate);
                request.Headers.Accept.ParseAdd("*/*");
                request.Headers.Referrer = new Uri(refererUrl);
                using HttpResponseMessage response = await httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                if (candidate.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    // A seekable temporary file avoids buffering a ZIP in RAM and makes download cancellation effective.
                    string temporaryArchive = Path.Combine(Path.GetTempPath(), "nte-media-" + Guid.NewGuid().ToString("N") + ".tmp");
                    await using FileStream archiveStream = new(temporaryArchive, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                    await body.CopyToAsync(archiveStream, token).ConfigureAwait(false);
                    archiveStream.Position = 0;
                    using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
                    string expectedName = Uri.UnescapeDataString(Path.GetFileName(candidate.AbsolutePath)[..^4]);
                    ZipArchiveEntry entry = FindEntry(archive, expectedName);
                    await using Stream payload = entry.Open();
                    await payload.CopyToAsync(output, token).ConfigureAwait(false);
                }
                else
                {
                    await body.CopyToAsync(output, token).ConfigureAwait(false);
                }

                await output.FlushAsync(token).ConfigureAwait(false);
                foreach (string parameter in candidate.Query.TrimStart('?').Split('&'))
                {
                    if (!parameter.StartsWith("nte_md5=", StringComparison.Ordinal)) continue;
                    string expected = parameter[8..];
                    if (expected.Length != 32 || !output.CanSeek) throw new InvalidDataException("Invalid launcher resource checksum.");
                    output.Position = 0;
                    byte[] actual = await MD5.HashDataAsync(output, token).ConfigureAwait(false);
                    if (!Convert.ToHexStringLower(actual).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Launcher resource checksum mismatch.");
                    output.Position = output.Length;
                }
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new HttpRequestException($"Unable to load '{url}'.");
    }

    private static ZipArchiveEntry FindEntry(ZipArchive archive, string expectedName)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.Length > 0 && entry.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        throw new InvalidDataException($"ZIP does not contain the expected non-empty resource '{expectedName}'.");
    }

    private static IEnumerable<Uri> GetCandidates(Uri url)
    {
        yield return url;
        string? otherHost = url.Host.ToLowerInvariant() switch
        {
            "yhcdn1.wmupd.com" => "yhcdn2.wmupd.com",
            "yhcdn2.wmupd.com" => "yhcdn1.wmupd.com",
            "ntecdn1.perfectworld.com" => "ntecdn2.perfectworld.com",
            "ntecdn2.perfectworld.com" => "ntecdn1.perfectworld.com",
            _ => null
        };
        if (otherHost != null)
            yield return new UriBuilder(url) { Host = otherHost }.Uri;
    }
}
