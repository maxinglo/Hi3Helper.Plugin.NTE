using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Xml;
using System.Text.RegularExpressions;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal sealed record NteLauncherFile(string Path, long Size, string Md5);

internal sealed class NteLauncherManifest
{
    private readonly Uri _baseUri;
    private readonly HashSet<string> _paths;
    private readonly Dictionary<string, string> _checksums;
    internal List<NteLauncherFile> Files { get; } = [];

    private NteLauncherManifest(Uri baseUri, HashSet<string> paths, Dictionary<string, string> checksums)
    {
        _baseUri = baseUri;
        _paths = paths;
        _checksums = checksums;
    }

    internal static string ParseFileListUrl(string versionIni, string versionIniUrl)
    {
        using StringReader reader = new(versionIni);
        while (reader.ReadLine() is { } line)
        {
            ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty || trimmed[0] is ';' or '#')
                continue;

            int separator = trimmed.IndexOf('=');
            if (separator <= 0 || !trimmed[..separator].Trim().Equals("FileListURL", StringComparison.OrdinalIgnoreCase))
                continue;

            string value = trimmed[(separator + 1)..].Trim().ToString();
            if (value.Length != 0)
                return new Uri(new Uri(versionIniUrl), value).AbsoluteUri;
        }

        throw new InvalidDataException("Cannot find FileListURL in Version.ini.");
    }

    internal static NteLauncherManifest Parse(string allFilesXml, string fileListUrl)
    {
        using StringReader input = new(allFilesXml);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        XmlDocument document = new() { XmlResolver = null };
        document.Load(reader);
        if (document.DocumentElement?.Name != "All_Files")
            throw new InvalidDataException("AllFiles.xml does not contain an All_Files root element.");

        List<NteLauncherFile> entries = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> checksums = new(StringComparer.OrdinalIgnoreCase);
        foreach (XmlNode file in document.DocumentElement.ChildNodes)
        {
            string? path = file.Attributes?["Path"]?.Value;
            if (file.Name == "File" && !string.IsNullOrWhiteSpace(path))
            {
                path = NormalizePath(path); paths.Add(path);
                string? checksum = file.Attributes?["Checksum"]?.Value;
                if (checksum != null && Regex.IsMatch(checksum, "\\A[0-9A-Fa-f]{32}\\z"))
                {
                    checksums[path] = checksum.ToLowerInvariant();
                    if (long.TryParse(file.Attributes?["Size"]?.Value, out long size) && size >= 0)
                        entries.Add(new(path, size, checksum.ToLowerInvariant()));
                }
            }
        }

        NteLauncherManifest manifest = new(new Uri(new Uri(fileListUrl), "."), paths, checksums);
        manifest.Files.AddRange(entries);
        return manifest;
    }

    internal bool TryFindPathBySuffix(string suffix, out string path)
    {
        string normalizedSuffix = NormalizePath(suffix);
        foreach (string candidate in _paths)
        {
            if (candidate.EndsWith(normalizedSuffix, StringComparison.OrdinalIgnoreCase))
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    internal string GetArchiveUrl(string path)
    {
        // Leading slashes in this manifest are relative to the version directory, not the CDN root.
        UriBuilder url = new(new Uri(_baseUri, NormalizePath(path).TrimStart('/')));
        if (!url.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            url.Path += ".zip";
        if (_checksums.TryGetValue(NormalizePath(path), out string? checksum)) url.Query = "nte_md5=" + checksum;
        return url.Uri.AbsoluteUri;
    }

    internal string[] GetBackgroundUrls(string configPath, string configJson)
    {
        using JsonDocument config = JsonDocument.Parse(configJson);
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("imgs", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
            return [];

        string directory = configPath[..(configPath.LastIndexOf('/') + 1)];
        List<string> urls = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object ||
                !image.TryGetProperty("file", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                continue;

            string? fileName = name.GetString();
            if (string.IsNullOrWhiteSpace(fileName))
                continue;

            string path = NormalizePath(directory + fileName);
            if (!_paths.Contains(path) && !TryFindPathBySuffix("/bgimgs/" + fileName, out path))
                continue;

            string url = GetArchiveUrl(path);
            if (seen.Add(url))
                urls.Add(url);
        }

        return [.. urls];
    }

    internal string? GetVideoUrl(string configPath, string configJson)
    {
        using JsonDocument config = JsonDocument.Parse(configJson);
        if (config.RootElement.ValueKind != JsonValueKind.Object ||
            !config.RootElement.TryGetProperty("video", out JsonElement video) || video.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(video.GetString()))
            return null;
        string file = video.GetString()!;
        string extension = Path.GetExtension(file);
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".webm", StringComparison.OrdinalIgnoreCase))
            return null;
        string path = NormalizePath(configPath[..(configPath.LastIndexOf('/') + 1)] + file);
        return _paths.Contains(path) ? GetArchiveUrl(path) : null;
    }

    internal string? GetFallbackImageUrl()
    {
        foreach (string extension in new[] { "png", "jpg", "jpeg" })
            if (TryFindPathBySuffix("/bgimgs/bg_0." + extension, out string path))
                return GetArchiveUrl(path);
        return null;
    }

    private static string NormalizePath(string path) => "/" + path.Replace('\\', '/').Trim().TrimStart('/');
}
