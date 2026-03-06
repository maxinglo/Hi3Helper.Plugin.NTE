// Endpoint discovery and core implementation idea credit: DynamiByte
// References:
//   https://gist.github.com/DynamiByte/d839bf9f671c975b6666d0f6e6634641
//   https://github.com/Cheu3172/Wuwa-Web-Request
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.Wuwa.Utils;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

[GeneratedComClass]
internal partial class WuwaGlobalLauncherApiMedia(string apiResponseBaseUrl, string gameTag, string authenticationHash) : LauncherApiMediaBase
{
    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??= WuwaUtils.CreateApiHttpClient(ApiResponseBaseUrl);
        set;
    }

    [field: AllowNull, MaybeNull]
    protected HttpClient ApiDownloadHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .SetAllowedDecompression(DecompressionMethods.None)
            .AllowCookies()
            .AllowRedirections()
            .AllowUntrustedCert()
            .Create();
        set;
    }

    protected override string ApiResponseBaseUrl { get; } = apiResponseBaseUrl;
    private WuwaApiResponseMedia? ApiResponse { get; set; }

    public override void GetBackgroundEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        using (ThisInstanceLock.EnterScope())
        {
            PluginDisposableMemory<LauncherPathEntry> backgroundEntries = PluginDisposableMemory<LauncherPathEntry>.Alloc();

            try
            {
                ref LauncherPathEntry entry = ref backgroundEntries[0];

                if (ApiResponse == null)
                {
                    isDisposable = false;
                    handle = nint.Zero;
                    count = 0;
                    isAllocated = false;
                    return;
                }

                entry.Write(ApiResponse.BackgroundImageUrl, Span<byte>.Empty);
                isAllocated = true;
            }
            finally
            {
                isDisposable = backgroundEntries.IsDisposable == 1;
                handle = backgroundEntries.AsSafePointer();
                count = backgroundEntries.Length;
            }
        }
    }

    public override void GetBackgroundFlag(out LauncherBackgroundFlag result)
        // backgroundFileType == 2 indicates a video background; everything else is an image.
        => result = ApiResponse?.BackgroundFileType == 2
            ? LauncherBackgroundFlag.TypeIsVideo
            : LauncherBackgroundFlag.TypeIsImage;

    public override void GetLogoFlag(out LauncherBackgroundFlag result)
        => result = ApiResponse?.SloganUrl != null
            ? LauncherBackgroundFlag.TypeIsImage
            : LauncherBackgroundFlag.None;

    public override void GetLogoOverlayEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (ApiResponse?.SloganUrl == null)
        {
            isDisposable = false;
            handle = nint.Zero;
            count = 0;
            isAllocated = false;
            return;
        }

        using (ThisInstanceLock.EnterScope())
        {
            PluginDisposableMemory<LauncherPathEntry> logoEntries = PluginDisposableMemory<LauncherPathEntry>.Alloc();
            ref LauncherPathEntry entry = ref logoEntries[0];
            entry.Write(ApiResponse.SloganUrl, Span<byte>.Empty);

            isDisposable = logoEntries.IsDisposable == 1;
            handle = logoEntries.AsSafePointer();
            count = logoEntries.Length;
            isAllocated = true;
        }
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        string decodedAuthHash = authenticationHash.AeonPlsHelpMe();
        string decodedGameTag  = gameTag.AeonPlsHelpMe();

        // Step 1 — fetch the stable launcher-config endpoint to resolve the current (rotating) background hash.
        // URL: {CDN}/launcher/launcher/{clientId}/{gameId}/index.json
        string launcherConfigUrl = ApiResponseBaseUrl
            .CombineUrlFromString("launcher", "launcher", decodedAuthHash, decodedGameTag, "index.json");

#if DEBUG
        SharedStatic.InstanceLogger.LogDebug(
            "[WuwaGlobalLauncherApiMedia::InitAsync] Fetching launcher-config: {Url}", launcherConfigUrl);
#endif

        using HttpResponseMessage configResponse = await ApiResponseHttpClient!.GetAsync(launcherConfigUrl, token);
        if (!configResponse.IsSuccessStatusCode)
        {
            SharedStatic.InstanceLogger.LogError(
                "[WuwaGlobalLauncherApiMedia::InitAsync] launcher-config request failed: {StatusCode}", (int)configResponse.StatusCode);
        }
        configResponse.EnsureSuccessStatusCode();

        string configJson = await configResponse.Content.ReadAsStringAsync(token);
        SharedStatic.InstanceLogger.LogTrace("[WuwaGlobalLauncherApiMedia::InitAsync] Launcher-config response: {Json}", configJson);

        WuwaApiResponseLauncherConfig? launcherConfig = JsonSerializer.Deserialize(
            configJson, WuwaApiResponseContext.Default.WuwaApiResponseLauncherConfig);

        string? backgroundHash = launcherConfig?.FunctionCode?.Background;
        if (string.IsNullOrEmpty(backgroundHash))
            throw new InvalidOperationException(
                "launcher-config response did not contain a valid functionCode.background hash.");

#if DEBUG
        SharedStatic.InstanceLogger.LogDebug(
            "[WuwaGlobalLauncherApiMedia::InitAsync] Resolved background hash: {Hash}", backgroundHash);
#endif

        // Step 2 — fetch the wallpapers-slogan endpoint using the dynamic hash.
        // URL: {CDN}/launcher/{clientId}/{gameId}/background/{hash}/en.json
        string wallpaperUrl = ApiResponseBaseUrl
            .CombineUrlFromString("launcher", decodedAuthHash, decodedGameTag, "background", backgroundHash, "en.json");

#if DEBUG
        SharedStatic.InstanceLogger.LogDebug(
            "[WuwaGlobalLauncherApiMedia::InitAsync] Fetching wallpaper: {Url}", wallpaperUrl);
#endif

        using HttpResponseMessage wallpaperResponse = await ApiResponseHttpClient.GetAsync(wallpaperUrl, token);
        if (!wallpaperResponse.IsSuccessStatusCode)
        {
            SharedStatic.InstanceLogger.LogError(
                "[WuwaGlobalLauncherApiMedia::InitAsync] wallpaper request failed: {StatusCode}", (int)wallpaperResponse.StatusCode);
        }
        wallpaperResponse.EnsureSuccessStatusCode();

        string wallpaperJson = await wallpaperResponse.Content.ReadAsStringAsync(token);
        SharedStatic.InstanceLogger.LogTrace("[WuwaGlobalLauncherApiMedia::InitAsync] Wallpaper response: {Json}", wallpaperJson);

        ApiResponse = JsonSerializer.Deserialize(wallpaperJson, WuwaApiResponseContext.Default.WuwaApiResponseMedia)
                      ?? throw new NullReferenceException("Wallpaper API returned a null response!");

        return 0;
    }

    protected override async Task DownloadAssetAsyncInner(HttpClient? client, string fileUrl, Stream outputStream,
        PluginDisposableMemory<byte> fileChecksum, PluginFiles.FileReadProgressDelegate? downloadProgress, CancellationToken token)
    {
        await base.DownloadAssetAsyncInner(ApiDownloadHttpClient, fileUrl, outputStream, fileChecksum, downloadProgress, token);
    }

    public override void Dispose()
    {
        if (IsDisposed)
            return;

        using (ThisInstanceLock.EnterScope())
        {
            ApiResponseHttpClient.Dispose();
            ApiDownloadHttpClient.Dispose();

            ApiResponse = null;
            base.Dispose();
        }
    }
}

