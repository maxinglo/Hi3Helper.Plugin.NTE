using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Localization;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Api;

[GeneratedComClass]
internal partial class NteCNLauncherApiMedia : LauncherApiMediaBase
{
    private readonly string _versionIniUrl;
    private readonly string _configuredPageUrl;
    private readonly NteRegionConfig _region;
    private string _pageUrl => _region.IsGlobal ? NteGlobalLocalization.GetPageUrl(SharedStatic.PluginLocaleCode) : _configuredPageUrl;
    private readonly NteLauncherResourceClient _resources;
    private readonly NteMediaCache _cache;
    private readonly NteOverlayComposer _overlays;
    private readonly Func<NteAppearanceOptions> _appearanceProvider;
    private string[] _backgroundPaths = [];
    private string? _overlayPath;
    private bool _hasVideo;

    internal NteCNLauncherApiMedia(string versionIniUrl, string siteRefererUrl,
        Func<NteAppearanceOptions>? appearanceProvider = null, NteRegionConfig? region = null)
        : this(versionIniUrl, siteRefererUrl,
            new PluginHttpClientBuilder().AllowRedirections().Create(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CollapseLauncher", "Plugins", "NTE", region?.IsOverseas == true ? region.StorageName + "/Media" : "Media"),
            appearanceProvider ?? new NteAppearanceStore(NteAppearanceStore.DefaultPath).Load, region)
    {
    }

    internal NteCNLauncherApiMedia(string versionIniUrl, string siteRefererUrl,
        HttpClient httpClient, string cacheDirectory, Func<NteAppearanceOptions>? appearanceProvider = null, NteRegionConfig? region = null)
    {
        _versionIniUrl = versionIniUrl;
        _configuredPageUrl = siteRefererUrl;
        _region = region ?? NteConfigProvider.CN;
        ApiResponseHttpClient = httpClient;
        _resources = new NteLauncherResourceClient(httpClient, siteRefererUrl);
        _cache = new NteMediaCache(cacheDirectory);
        _overlays = new NteOverlayComposer(_resources, new NteMediaCache(Path.Combine(cacheDirectory, "overlays")));
        _appearanceProvider = appearanceProvider ?? (() => new NteAppearanceOptions { OverlayEnabled = false });
    }

    protected override HttpClient ApiResponseHttpClient { get; set; }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
    {
        using (ThisInstanceLock.EnterScope())
            result = (_hasVideo ? LauncherBackgroundFlag.TypeIsVideo : LauncherBackgroundFlag.TypeIsImage)
                | LauncherBackgroundFlag.IsSourceFile;
    }

    public override void GetLogoFlag(out LauncherBackgroundFlag result)
    {
        using (ThisInstanceLock.EnterScope())
            result = _overlayPath == null ? LauncherBackgroundFlag.None
                : LauncherBackgroundFlag.TypeIsImage | LauncherBackgroundFlag.IsSourceFile;
    }

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        using (ThisInstanceLock.EnterScope())
        {
            if (_overlayPath == null)
            {
                InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
                return;
            }
            string[] paths = new string[_backgroundPaths.Length];
            Array.Fill(paths, _overlayPath);
            WriteEntries(paths, out handle, out count, out isDisposable, out isAllocated);
        }
    }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        using (ThisInstanceLock.EnterScope())
            WriteEntries(_backgroundPaths, out handle, out count, out isDisposable, out isAllocated);
    }

    protected override Task<int> InitAsync(CancellationToken token) => InitializeMediaAsync(token);

    internal async Task<int> InitializeMediaAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NteAppearanceOptions appearance = _appearanceProvider();
        appearance.Validate();
        string versionIni = await _resources.ReadTextAsync(_versionIniUrl, token).ConfigureAwait(false);
        string fileListUrl = NteLauncherManifest.ParseFileListUrl(versionIni, _versionIniUrl);
        string allFilesXml = await _resources.ReadTextAsync(fileListUrl, token).ConfigureAwait(false);
        NteLauncherManifest manifest = NteLauncherManifest.Parse(allFilesXml, fileListUrl);

        string[] imageUrls = [];
        string? videoUrl = null;
        if (manifest.TryFindPathBySuffix("/" + _region.AppId + "/bgimgs/config.json", out string configPath) ||
            (!_region.IsGlobal && manifest.TryFindPathBySuffix("/bgimgs/config.json", out configPath)))
        {
            try
            {
                string configJson = await _resources.ReadTextAsync(manifest.GetArchiveUrl(configPath), token)
                    .ConfigureAwait(false);
                imageUrls = manifest.GetBackgroundUrls(configPath, configJson);
                if (appearance.DynamicBackground)
                    videoUrl = manifest.GetVideoUrl(configPath, configJson);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "Cannot load NTE background configuration; trying a static fallback.");
            }
        }
        if (imageUrls.Length == 0 && manifest.GetFallbackImageUrl() is { } fallback)
            imageUrls = [fallback];

        List<string> localPaths = new(imageUrls.Length + 1);
        foreach (string imageUrl in imageUrls)
        {
            string? path = await PrepareAssetAsync(imageUrl, token).ConfigureAwait(false);
            if (path != null)
                localPaths.Add(path);
        }

        // Await video preparation as part of InitAsync. The launcher's loading operation must not
        // complete while the ZIP is still being downloaded/extracted. Video is first so it is selectable by default.
        string? videoPath = videoUrl == null ? null : await PrepareAssetAsync(videoUrl, token).ConfigureAwait(false);
        if (videoPath != null)
            localPaths.Insert(0, videoPath);

        string? overlayPath = null;
        if (appearance.OverlayEnabled && localPaths.Count > 0)
        {
            try
            {
                Dictionary<string, byte[]> nativeImages = [];
                if (appearance.Elements.Exists(item => item.Enabled && item.Source == "slogan") &&
                    manifest.TryFindPathBySuffix("/ResData/" + _region.AppId + ".dat", out string resourcePath))
                {
                    try
                    {
                        string resourceUrl = manifest.GetArchiveUrl(resourcePath);
                        string? localResource = await PrepareAssetAsync(resourceUrl, token).ConfigureAwait(false);
                        if (localResource != null)
                        {
                            byte[] packed = await File.ReadAllBytesAsync(localResource, token).ConfigureAwait(false);
                            string artwork = _region.IsGlobal ? NteGlobalLocalization.GetArtworkPath(SharedStatic.PluginLocaleCode)
                                : "res/res/" + _region.AppId + "/main/slogan.png";
                            nativeImages["slogan"] = NteQtResourceImage.ReadPng(packed, artwork);
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                    { SharedStatic.InstanceLogger.LogWarning(ex, "Cannot read current launcher title artwork; trying the web fallback."); }
                }
                bool needsHtml = appearance.Elements.Exists(item => item.Enabled && (item.Source is "slogan" or "logo") && !nativeImages.ContainsKey(item.Source));
                string html = needsHtml ? await _resources.ReadTextAsync(_pageUrl, token).ConfigureAwait(false) : "";
                overlayPath = await _overlays.PrepareAsync(appearance, html, _pageUrl, token, nativeImages).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "Cannot prepare NTE decorative layers.");
            }
        }

        token.ThrowIfCancellationRequested();
        using (ThisInstanceLock.EnterScope())
        {
            _backgroundPaths = [.. localPaths];
            _hasVideo = videoPath != null;
            _overlayPath = overlayPath;
        }
        SharedStatic.InstanceLogger.LogInformation(
            "Prepared {Count} local NTE backgrounds; video={Video}, decorative layers={Overlay}.",
            localPaths.Count, videoPath != null, overlayPath != null);
        return 0;
    }

    private async Task<string?> PrepareAssetAsync(string url, CancellationToken token)
    {
        try
        {
            return await _cache.GetOrCreateAsync(url,
                (output, cancellation) => _resources.ExtractAssetAsync(url, output, cancellation), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Cannot prepare NTE background {Url}.", url);
            return null;
        }
    }

    private static void WriteEntries(string[] paths, out nint handle, out int count,
        out bool isDisposable, out bool isAllocated)
    {
        if (paths.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }
        PluginDisposableMemory<LauncherPathEntry> entries = PluginDisposableMemory<LauncherPathEntry>.Alloc(paths.Length);
        for (int i = 0; i < paths.Length; i++)
            entries[i].Write(paths[i], Span<byte>.Empty);
        handle = entries.AsSafePointer();
        count = entries.Length;
        isDisposable = entries.IsDisposable == 1;
        isAllocated = true;
    }

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }
}
