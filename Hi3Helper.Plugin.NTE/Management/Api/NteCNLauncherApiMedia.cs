using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Hi3Helper.Plugin.NTE.Management.Api;

[GeneratedComClass]
internal partial class NteCNLauncherApiMedia(string versionIniUrl, string siteRefererUrl) : LauncherApiMediaBase
{
    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .AllowRedirections()
            .AllowUntrustedCert()
            .Create();
        set;
    }

    protected override string ApiResponseBaseUrl => versionIniUrl;

    private string[] _backgroundImageUrls = [];
    private string? _backgroundVideoUrl;

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        using (ThisInstanceLock.EnterScope())
        {
            result = !string.IsNullOrWhiteSpace(_backgroundVideoUrl)
                ? LauncherBackgroundFlag.TypeIsVideo
                : LauncherBackgroundFlag.TypeIsImage;
        }
    }

    public override void GetLogoFlag(out LauncherBackgroundFlag result) => result = LauncherBackgroundFlag.None;

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
    }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        using (ThisInstanceLock.EnterScope())
        {
            bool hasVideo = !string.IsNullOrWhiteSpace(_backgroundVideoUrl);
            int entryCount = _backgroundImageUrls.Length + (hasVideo ? 1 : 0);

            if (entryCount == 0)
            {
                InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
                return;
            }

            PluginDisposableMemory<LauncherPathEntry> entries = PluginDisposableMemory<LauncherPathEntry>.Alloc(entryCount);

            for (int i = 0; i < _backgroundImageUrls.Length; i++)
            {
                ref LauncherPathEntry entry = ref entries[i];
                entry.Write(_backgroundImageUrls[i], Span<byte>.Empty);
            }

            if (hasVideo)
            {
                ref LauncherPathEntry videoEntry = ref entries[_backgroundImageUrls.Length];
                videoEntry.Write(_backgroundVideoUrl!, Span<byte>.Empty);
            }

            handle = entries.AsSafePointer();
            count = entries.Length;
            isDisposable = entries.IsDisposable == 1;
            isAllocated = true;
        }
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        string versionIni = await GetStringWithHeadersAsync(ApiResponseBaseUrl, token);
        string fileListUrl = ParseFileListUrlFromIni(versionIni);

        string allFilesXml = await GetStringWithHeadersAsync(fileListUrl, token);
        FileListManifest fileList = ParseAllFilesXml(allFilesXml, fileListUrl);

        if (!fileList.TryFindPathBySuffix("/bgimgs/config.json", out string configPath))
        {
            throw new InvalidOperationException("Cannot find bgimgs/config.json in AllFiles.xml.");
        }

        string configJsonUrl = CombineZipUrl(fileList.BaseUrl, configPath);
        string configJson = await GetStringWithHeadersAsync(configJsonUrl, token);

        string[] imageFileNames = ParseBackgroundImageNames(configJson);
        string[] imageUrls = BuildBackgroundImageUrls(fileList, configPath, imageFileNames);

        if (imageUrls.Length == 0 && fileList.TryFindPathBySuffix("/bgimgs/bg_0.jpg", out string fallbackBgPath))
        {
            imageUrls = [CombineUrl(fileList.BaseUrl, fallbackBgPath)];
        }

        // Temporarily disable animated background resources (yh.dat).
        string? videoUrl = null;

        using (ThisInstanceLock.EnterScope())
        {
            _backgroundImageUrls = imageUrls;
            _backgroundVideoUrl = videoUrl;
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[NteCNLauncherApiMedia::InitAsync] Loaded {ImageCount} image entries and video: {HasVideo}.",
            imageUrls.Length,
            !string.IsNullOrWhiteSpace(videoUrl));

        return 0;
    }

    protected override async Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress, CancellationToken token)
    {
        client ??= ApiResponseHttpClient;

        // Launcher expects local background files to keep original extensions (e.g. .jpg/.dat).
        // For wmupd resources we download *.zip internally and extract the payload into outputStream.
        if (ShouldUseZipPackaging(fileUrl) && !IsZipUrl(fileUrl))
        {
            await DownloadZipAssetAsync(client, EnsureZipExtension(fileUrl), outputStream, downloadProgress, token);
            return;
        }

        if (IsZipUrl(fileUrl))
        {
            await DownloadZipAssetAsync(client, fileUrl, outputStream, downloadProgress, token);
            return;
        }

        if (IsYhDatUrl(fileUrl))
        {
            await DownloadYhDatWithoutHeaderAsync(client, fileUrl, outputStream, downloadProgress, token);
            return;
        }

        await base.DownloadAssetAsyncInner(client, fileUrl, outputStream, fileChecksum, downloadProgress, token);
    }

    private static bool ShouldUseZipPackaging(string fileUrl)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return (uri.Host.Equals("yhcdn1.wmupd.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("yhcdn2.wmupd.com", StringComparison.OrdinalIgnoreCase)) &&
               uri.AbsolutePath.Contains("/ResFiles", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetStringWithHeadersAsync(string url, CancellationToken token)
    {
        HttpRequestException? lastHttpException = null;

        foreach (string candidateUrl in BuildUrlCandidates(url))
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, candidateUrl);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");

                if (Uri.TryCreate(siteRefererUrl, UriKind.Absolute, out Uri? refererUri))
                {
                    request.Headers.Referrer = refererUri;
                }

                using HttpResponseMessage response = await ApiResponseHttpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();

                await using Stream responseStream = await response.Content.ReadAsStreamAsync(token);
                if (IsZipUrl(candidateUrl))
                {
                    return await ReadZipEntryAsStringAsync(responseStream, GetZipEntryNameFromUrl(candidateUrl));
                }

                using StreamReader reader = new(responseStream);
                return await reader.ReadToEndAsync();
            }
            catch (HttpRequestException ex)
            {
                lastHttpException = ex;
            }
        }

        throw lastHttpException ?? new HttpRequestException($"Unable to load '{url}'.");
    }

    private static async Task<string> ReadZipEntryAsStringAsync(Stream zipStream, string expectedEntryName)
    {
        using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry entry = GetPreferredZipEntry(archive, expectedEntryName);

        await using Stream entryStream = entry.Open();
        using StreamReader reader = new(entryStream);
        return await reader.ReadToEndAsync();
    }

    private static ZipArchiveEntry GetPreferredZipEntry(ZipArchive archive, string expectedEntryName)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(expectedEntryName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        if (archive.Entries.Count == 0)
        {
            throw new InvalidDataException("Zip archive does not contain any entries.");
        }

        return archive.Entries[0];
    }

    private static string GetZipEntryNameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        string fileName = Path.GetFileName(uri.AbsolutePath);
        return fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    private static bool IsZipUrl(string fileUrl)
        => fileUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> BuildUrlCandidates(string url)
    {
        yield return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            yield break;
        }

        string swappedHost = SwapLauncherCdnHost(uri.Host);
        if (!swappedHost.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            UriBuilder builder = new(uri)
            {
                Host = swappedHost
            };

            yield return builder.Uri.ToString();
        }
    }

    private static string SwapLauncherCdnHost(string host)
    {
        if (host.Equals("yhcdn1.wmupd.com", StringComparison.OrdinalIgnoreCase))
        {
            return "yhcdn2.wmupd.com";
        }

        if (host.Equals("yhcdn2.wmupd.com", StringComparison.OrdinalIgnoreCase))
        {
            return "yhcdn1.wmupd.com";
        }

        return host;
    }

    private static string ParseFileListUrlFromIni(string iniContent)
    {
        using StringReader reader = new(iniContent);

        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            int separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = trimmed[..separatorIndex].Trim();
            if (!key.Equals("FileListURL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = trimmed[(separatorIndex + 1)..].Trim();
            if (value.Length == 0)
            {
                break;
            }

            return value;
        }

        throw new InvalidOperationException("Cannot find FileListURL in Version.ini.");
    }

    private static FileListManifest ParseAllFilesXml(string allFilesXml, string fileListUrl)
    {
        XmlDocument xmlDoc = new();
        xmlDoc.LoadXml(allFilesXml);

        XmlElement? root = xmlDoc.DocumentElement;
        if (root == null)
        {
            throw new InvalidOperationException("AllFiles.xml does not contain a root element.");
        }

        // Resource file paths in AllFiles.xml are resolved against the file-list directory (versioned folder).
        // Example: .../launcher/1.0.4.0415_1/AllFiles.xml + /ResFilesM/... => .../launcher/1.0.4.0415_1/ResFilesM/...
        string baseUrl = GetFileListDirectoryUrl(fileListUrl);

        List<string> paths = [];
        XmlNodeList? fileNodes = root.SelectNodes("/All_Files/File");
        if (fileNodes != null)
        {
            foreach (XmlNode fileNode in fileNodes)
            {
                string? path = fileNode.Attributes?["Path"]?.Value;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(NormalizeManifestPath(path));
                }
            }
        }

        return new FileListManifest(baseUrl, paths);
    }

    private static string GetFileListDirectoryUrl(string fileListUrl)
    {
        int lastSlashIndex = fileListUrl.LastIndexOf('/');
        if (lastSlashIndex <= 0)
        {
            return fileListUrl;
        }

        return fileListUrl[..lastSlashIndex];
    }

    private static string[] ParseBackgroundImageNames(string configJson)
    {
        using JsonDocument doc = JsonDocument.Parse(configJson);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("imgs", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        HashSet<string> dedup = new(StringComparer.OrdinalIgnoreCase);
        List<string> fileNames = [];

        foreach (JsonElement image in images.EnumerateArray())
        {
            if (!image.TryGetProperty("file", out JsonElement fileElement) || fileElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? fileName = fileElement.GetString();
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            if (dedup.Add(fileName))
            {
                fileNames.Add(fileName);
            }
        }

        return [.. fileNames];
    }

    private static string[] BuildBackgroundImageUrls(FileListManifest fileList, string configPath, string[] imageFileNames)
    {
        string configDirectory = GetDirectoryPath(configPath);
        HashSet<string> dedup = new(StringComparer.OrdinalIgnoreCase);
        List<string> imageUrls = [];

        foreach (string fileName in imageFileNames)
        {
            string directPath = CombineRelativePath(configDirectory, fileName);
            if (fileList.ContainsPath(directPath))
            {
                string directUrl = CombineUrl(fileList.BaseUrl, directPath);
                if (dedup.Add(directUrl))
                {
                    imageUrls.Add(directUrl);
                }

                continue;
            }

            if (fileList.TryFindPathBySuffix($"/bgimgs/{fileName}", out string fallbackPath))
            {
                string fallbackUrl = CombineUrl(fileList.BaseUrl, fallbackPath);
                if (dedup.Add(fallbackUrl))
                {
                    imageUrls.Add(fallbackUrl);
                }
            }
        }

        return [.. imageUrls];
    }

    private static string GetDirectoryPath(string path)
    {
        int separatorIndex = path.LastIndexOf('/');
        if (separatorIndex < 0)
        {
            return string.Empty;
        }

        return path[..separatorIndex];
    }

    private static string CombineRelativePath(string directoryPath, string fileName)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return fileName.StartsWith("/", StringComparison.Ordinal) ? fileName : $"/{fileName}";
        }

        return $"{directoryPath.TrimEnd('/')}/{fileName.TrimStart('/')}";
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out Uri? absoluteUri))
        {
            return absoluteUri.ToString();
        }

        return $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private static string CombineZipUrl(string baseUrl, string path)
        => CombineUrl(baseUrl, EnsureZipExtension(path));

    private static string EnsureZipExtension(string path)
        => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path : $"{path}.zip";

    private static bool IsYhDatUrl(string fileUrl)
        => StripZipExtension(fileUrl).EndsWith("/yh.dat", StringComparison.OrdinalIgnoreCase) ||
           StripZipExtension(fileUrl).EndsWith("\\yh.dat", StringComparison.OrdinalIgnoreCase);

    private static string StripZipExtension(string path)
        => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;

    private static async Task DownloadZipAssetAsync(HttpClient client,
        string fileUrl,
        Stream outputStream,
        PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
    {
        if (outputStream.CanSeek)
        {
            outputStream.Seek(0, SeekOrigin.Begin);
            outputStream.SetLength(0);
        }

        using HttpResponseMessage response = await client.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        await using Stream sourceStream = await response.Content.ReadAsStreamAsync(token);
        using ZipArchive archive = new(sourceStream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry entry = GetPreferredZipEntry(archive, GetZipEntryNameFromUrl(fileUrl));

        await using Stream entryStream = entry.Open();
        if (IsYhDatUrl(fileUrl))
        {
            await CopyYhDatPayloadAsync(entryStream, outputStream, downloadProgress, 28, token, entry.Length);
            return;
        }

        await CopyStreamAsync(entryStream, outputStream, downloadProgress, token, entry.Length);
    }

    private static async Task DownloadYhDatWithoutHeaderAsync(HttpClient client,
        string fileUrl,
        Stream outputStream,
        PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token)
    {
        const int headerSize = 28;

        if (outputStream.CanSeek)
        {
            outputStream.Seek(0, SeekOrigin.Begin);
            outputStream.SetLength(0);
        }

        using HttpResponseMessage response = await client.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();

        await using Stream sourceStream = await response.Content.ReadAsStreamAsync(token);
        await CopyYhDatPayloadAsync(sourceStream, outputStream, downloadProgress, headerSize, token, response.Content.Headers.ContentLength);
    }

    private static async Task CopyStreamAsync(Stream sourceStream,
        Stream outputStream,
        PluginFiles.FileReadProgressDelegate? downloadProgress,
        CancellationToken token,
        long totalBytes)
    {
        const int bufferSize = 65536;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long bytesRead = 0;

        try
        {
            int read;
            while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
            {
                await outputStream.WriteAsync(buffer.AsMemory(0, read), token);
                bytesRead += read;
                downloadProgress?.Invoke(read, bytesRead, totalBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await outputStream.FlushAsync(token);
    }

    private static async Task CopyYhDatPayloadAsync(Stream sourceStream,
        Stream outputStream,
        PluginFiles.FileReadProgressDelegate? downloadProgress,
        int headerSize,
        CancellationToken token,
        long? sourceLength = null)
    {
        if (sourceStream.CanSeek)
        {
            long totalBytes = Math.Max(0, sourceStream.Length - headerSize);
            await SkipExactBytesAsync(sourceStream, headerSize, token);
            await CopyStreamAsync(sourceStream, outputStream, downloadProgress, token, totalBytes);
            return;
        }

        long total = sourceLength.HasValue ? Math.Max(0, sourceLength.Value - headerSize) : 0;
        await SkipExactBytesAsync(sourceStream, headerSize, token);
        await CopyStreamAsync(sourceStream, outputStream, downloadProgress, token, total);
    }

    private static async Task SkipExactBytesAsync(Stream stream, int bytesToSkip, CancellationToken token)
    {
        byte[] skipBuffer = ArrayPool<byte>.Shared.Rent(bytesToSkip);

        try
        {
            int totalSkipped = 0;
            while (totalSkipped < bytesToSkip)
            {
                int read = await stream.ReadAsync(skipBuffer.AsMemory(totalSkipped, bytesToSkip - totalSkipped), token);
                if (read == 0)
                {
                    throw new InvalidDataException("yh.dat payload is smaller than the 28-byte header prefix.");
                }

                totalSkipped += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(skipBuffer);
        }
    }

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;

        using (ThisInstanceLock.EnterScope())
        {
            ApiResponseHttpClient.Dispose();
            base.Dispose();
        }
    }

    private sealed class FileListManifest(string baseUrl, List<string> paths)
    {
        private readonly HashSet<string> _pathSet = new(paths, StringComparer.OrdinalIgnoreCase);

        public string BaseUrl { get; } = baseUrl;

        public bool ContainsPath(string path) => _pathSet.Contains(NormalizeManifestPath(path));

        public bool TryFindPathBySuffix(string suffix, out string path)
        {
            string normalizedSuffix = NormalizeManifestPath(suffix);

            foreach (string currentPath in _pathSet)
            {
                if (currentPath.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    path = currentPath;
                    return true;
                }
            }

            path = string.Empty;
            return false;
        }
    }

    private static string NormalizeManifestPath(string path)
        => path.Replace('\\', '/').Trim();
}
