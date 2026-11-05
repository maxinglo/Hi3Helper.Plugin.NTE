using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Resolve the active game branch from the same launcher manifest as the official client.</summary>
internal sealed record NtePatcherConfig(string BranchName, string AppId, string[] ResourceUrls)
{
    internal static string VersionIniUrl => NteConfigProvider.LauncherVersionIniUrl;
    internal static bool IsValidBranch(string branch) => branch.Length != 0 && branch is not ("." or "..") &&
        branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    private string Referer => NteConfigProvider.GetRegion(AppId).ZoneHomePageUrl;

    internal static async Task<NtePatcherConfig> DiscoverAsync(HttpClient httpClient, CancellationToken token, NteRegionConfig? region = null)
    {
        region ??= NteConfigProvider.CN;
        NteLauncherResourceClient client = new(httpClient, region.ZoneHomePageUrl, NteLauncherResourceClient.MetadataTimeout);
        SharedStatic.InstanceLogger.LogInformation("[NTE Metadata] Reading the official launcher version for {Region}", region.ProfileName);
        string versionIni = await client.ReadTextAsync(region.LauncherVersionIniUrl, token).ConfigureAwait(false);
        string manifestUrl = NteLauncherManifest.ParseFileListUrl(versionIni, region.LauncherVersionIniUrl);
        SharedStatic.InstanceLogger.LogInformation("[NTE Metadata] Reading the official launcher file list");
        NteLauncherManifest manifest = NteLauncherManifest.Parse(
            await client.ReadTextAsync(manifestUrl, token).ConfigureAwait(false), manifestUrl);
        if (!manifest.TryFindPathBySuffix("/" + region.AppId + "/PatcherConfig/PatcherConfig.json", out string path))
            throw new InvalidDataException("Official launcher manifest has no patcher configuration for " + region.ProfileName + ".");
        SharedStatic.InstanceLogger.LogInformation("[NTE Metadata] Reading the active patcher branch");
        NtePatcherConfig result = Parse(await client.ReadTextAsync(manifest.GetArchiveUrl(path), token).ConfigureAwait(false), region.AppId);
        return result with { ResourceUrls = result.ResourceUrls.OrderByDescending(url =>
            new Uri(url).Host.Equals(client.LastSuccessfulHost, StringComparison.OrdinalIgnoreCase)).ToArray() };
    }

    internal static NtePatcherConfig Parse(string json, string expectedAppId = Nte.Login.NteRegionDefinition.CnAppId)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string branch = root.GetProperty("branchName").GetString() ?? "";
        string appId = root.GetProperty("appId").GetString() ?? "";
        if (!IsValidBranch(branch) || appId != expectedAppId)
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
        NteLauncherResourceClient client = new(httpClient, Referer, NteLauncherResourceClient.MetadataTimeout);
        SharedStatic.InstanceLogger.LogInformation("[NTE Metadata] Reading the game release for branch {Branch}", BranchName);
        return await client.ReadTextAsync(ResourceUrls.Select(root => $"{root}/{BranchName}/Version/Windows/config.xml"), token)
            .ConfigureAwait(false);
    }
}
