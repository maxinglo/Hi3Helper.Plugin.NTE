using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Resolve the active game branch from the same launcher manifest as the official client.</summary>
internal sealed record NtePatcherConfig(string BranchName, string AppId, string[] ResourceUrls)
{
    internal const string VersionIniUrl = "https://yhcdn1.wmupd.com/hd/publish_ob/launcher/Version.ini";
    private string Referer => NteConfigProvider.GetRegion(AppId).ZoneHomePageUrl;

    internal static async Task<NtePatcherConfig> DiscoverAsync(HttpClient httpClient, CancellationToken token, NteRegionConfig? region = null)
    {
        region ??= NteConfigProvider.CN;
        NteLauncherResourceClient client = new(httpClient, region.ZoneHomePageUrl);
        string versionIni = await client.ReadTextAsync(region.LauncherVersionIniUrl, token).ConfigureAwait(false);
        string manifestUrl = NteLauncherManifest.ParseFileListUrl(versionIni, region.LauncherVersionIniUrl);
        NteLauncherManifest manifest = NteLauncherManifest.Parse(
            await client.ReadTextAsync(manifestUrl, token).ConfigureAwait(false), manifestUrl);
        if (!manifest.TryFindPathBySuffix("/" + region.AppId + "/PatcherConfig/PatcherConfig.json", out string path))
            throw new InvalidDataException("Official launcher manifest has no patcher configuration for " + region.ProfileName + ".");
        return Parse(await client.ReadTextAsync(manifest.GetArchiveUrl(path), token).ConfigureAwait(false), region.AppId);
    }

    internal static NtePatcherConfig Parse(string json, string expectedAppId = "1289")
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string branch = root.GetProperty("branchName").GetString() ?? "";
        string appId = root.GetProperty("appId").GetString() ?? "";
        if (branch.Length == 0 || branch is "." or ".." || branch.IndexOfAny(['/', '\\', '?', '#', ':']) >= 0 || appId != expectedAppId)
            throw new InvalidDataException("Invalid patcher branch or application id.");
        System.Collections.Generic.List<string> urls = [];
        foreach (JsonElement item in root.GetProperty("gameResUrl").EnumerateArray())
        {
            if (!Uri.TryCreate(item.GetString(), UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.Query))
                throw new InvalidDataException("Invalid patcher resource endpoint.");
            urls.Add(uri.AbsoluteUri.TrimEnd('/'));
        }
        if (urls.Count == 0)
            throw new InvalidDataException("No patcher resource endpoints.");
        return new(branch, appId, [.. urls]);
    }

    internal async Task<string> ReadReleaseConfigAsync(HttpClient httpClient, CancellationToken token)
    {
        NteLauncherResourceClient client = new(httpClient, Referer);
        Exception? lastError = null;
        foreach (string root in ResourceUrls)
        {
            try
            {
                return await client.ReadTextAsync($"{root}/{BranchName}/Version/Windows/config.xml", token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                lastError = ex;
            }
        }
        throw lastError ?? new InvalidDataException("Cannot read the active game release.");
    }
}
