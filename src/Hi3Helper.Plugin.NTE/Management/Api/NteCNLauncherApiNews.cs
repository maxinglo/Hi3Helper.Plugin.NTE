using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Localization;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Hi3Helper.Plugin.NTE.Management.Api;

[GeneratedComClass]
internal partial class NteCNLauncherApiNews : LauncherApiNewsBase
{
    private readonly NteSocialIconProcessor _icons;
    private (string title, string img, string link)[] _carouselItems = [];
    private (string title, string link, string date, LauncherNewsEntryType type)[] _newsItems = [];
    private NteSocialMediaAsset[] _socialMediaItems = [];
    private readonly NteRegionConfig _region;

    private string PageUrl => _region.IsGlobal
        ? NteGlobalLocalization.GetPageUrl(SharedStatic.PluginLocaleCode)
        : _region.LauncherPageUrl;

    private string SiteUrl => _region.ZoneHomePageUrl;

    internal NteCNLauncherApiNews() : this(NteConfigProvider.CN)
    {
    }

    internal NteCNLauncherApiNews(NteRegionConfig region) : this(
        new PluginHttpClientBuilder().AllowRedirections().Create(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CollapseLauncher", "Plugins", "NTE",
            region.IsOverseas ? region.StorageName + "/Media/icons" : "Media/icons"), region)
    {
    }

    internal NteCNLauncherApiNews(HttpClient client, string cacheDirectory, NteRegionConfig? region = null)
    {
        _region = region ?? NteConfigProvider.CN;
        ApiResponseHttpClient = client;
        _icons = new NteSocialIconProcessor(new NteLauncherResourceClient(client, PageUrl),
            new NteMediaCache(cacheDirectory));
    }

    protected override HttpClient ApiResponseHttpClient { get; set; }

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        var items = _newsItems;
        if (items.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherNewsEntry> entries =
            PluginDisposableMemory<LauncherNewsEntry>.Alloc(items.Length);
        for (int i = 0; i < items.Length; i++)
            entries[i].Write(items[i].title, items[i].title, items[i].link, items[i].date, items[i].type);
        handle = entries.AsSafePointer();
        count = entries.Length;
        isDisposable = entries.IsDisposable == 1;
        isAllocated = true;
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        var items = _carouselItems;
        if (items.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherCarouselEntry> entries =
            PluginDisposableMemory<LauncherCarouselEntry>.Alloc(items.Length);
        for (int i = 0; i < items.Length; i++)
            entries[i].Write(items[i].title, items[i].img, items[i].link);
        handle = entries.AsSafePointer();
        count = entries.Length;
        isDisposable = entries.IsDisposable == 1;
        isAllocated = true;
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable,
        out bool isAllocated)
    {
        NteSocialMediaAsset[] items = _socialMediaItems;
        if (items.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherSocialMediaEntry> entries =
            PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            ref LauncherSocialMediaEntry entry = ref entries[i];
            entry.WriteDescription(items[i].Description);
            entry.WriteIcon(items[i].IconPath);
            entry.WriteIconHover(items[i].IconHoverPath);
            entry.WriteClickUrl(items[i].ClickUrl);
            LauncherSocialMediaEntryFlag flags = LauncherSocialMediaEntryFlag.IconIsPath |
                                                 LauncherSocialMediaEntryFlag.IconHoverIsPath;
            if (!string.IsNullOrEmpty(items[i].QrUrl))
            {
                entry.WriteQrImage(items[i].QrUrl);
                entry.WriteQrImageDescription(items[i].Description);
                flags |= LauncherSocialMediaEntryFlag.QrImageIsPath;
            }

            // Core 0.1.6's WriteIcon helpers set unrelated bits; publish only the flags actually written.
            entry.Flags = flags;
        }

        handle = entries.AsSafePointer();
        count = entries.Length;
        isDisposable = entries.IsDisposable == 1;
        isAllocated = true;
    }

    protected override Task<int> InitAsync(CancellationToken token) => InitializeNewsAsync(token);

    internal async Task<int> InitializeNewsAsync(CancellationToken token)
    {
        Task<(string title, string img, string link)[]> carouselTask = FetchCarouselAsync(token);
        Task<(string, string, string, LauncherNewsEntryType)[]>? taiwanNewsTask = _region.IsTaiwan ? FetchTaiwanNewsAsync(token) : null;
        Task<((string, string, string, LauncherNewsEntryType)[], NteSocialMediaAsset[])> htmlTask =
            FetchHtmlNewsAndSocialsAsync(token);
        await Task.WhenAll(carouselTask, htmlTask).ConfigureAwait(false);
        var (news, socials) = await htmlTask.ConfigureAwait(false);
        if (taiwanNewsTask != null) news = await taiwanNewsTask.ConfigureAwait(false);
        var carousel = await carouselTask.ConfigureAwait(false);
        using (ThisInstanceLock.EnterScope())
        {
            _carouselItems = carousel;
            _newsItems = news;
            _socialMediaItems = socials;
        }

        SharedStatic.InstanceLogger.LogInformation("Loaded NTE launcher news and {Count} cropped social icons.",
            socials.Length);
        return 0;
    }

    private async Task<(string title, string img, string link)[]> FetchCarouselAsync(CancellationToken token)
    {
        try
        {
            if (_region.IsGlobal)
            {
                string html = await ApiResponseHttpClient.GetStringAsync(PageUrl, token).ConfigureAwait(false);
                return await FetchGlobalCarouselAsync(html, token).ConfigureAwait(false);
            }

            if (_region.IsTaiwan)
            {
                string html = await ApiResponseHttpClient
                    .GetStringAsync("https://nte.iwplay.com.tw/CmsBanner/ubanner_A.html", token).ConfigureAwait(false);
                return NteTaiwanLauncherContent.ParseCarousel(html, DateTimeOffset.UtcNow);
            }

            string script = await ApiResponseHttpClient.GetStringAsync(NteConfigProvider.SwiperScriptUrl, token)
                .ConfigureAwait(false);
            return ParseCarouselItems(ExtractJsonObjectFromJsVariable(script, "yh_data_data"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Cannot load NTE carousel entries.");
            return [];
        }
    }

    private async Task<((string, string, string, LauncherNewsEntryType)[], NteSocialMediaAsset[])>
        FetchHtmlNewsAndSocialsAsync(CancellationToken token)
    {
        try
        {
            string html = await ApiResponseHttpClient.GetStringAsync(PageUrl, token).ConfigureAwait(false);
            (string css, string cssUrl) = await FetchCssContentAsync(html, token).ConfigureAwait(false);
            NteSocialMediaSource[] sources = NteLauncherHtmlAssets.ParseSocials(
                html, css, PageUrl, cssUrl);
            List<NteSocialMediaAsset> socials = new(sources.Length);
            foreach (NteSocialMediaSource source in sources)
            {
                try
                {
                    socials.Add(await _icons.PrepareAsync(source, token).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                {
                    SharedStatic.InstanceLogger.LogWarning(ex, "Cannot prepare NTE social icon {Url}.", source.IconUrl);
                }
            }

            return (ParseHtmlNews(html), [.. socials]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(ex, "Cannot load NTE news/social entries.");
            return ([], []);
        }
    }

    private async Task<(string, string, string, LauncherNewsEntryType)[]> FetchTaiwanNewsAsync(CancellationToken token)
    {
        List<(string, string, string, LauncherNewsEntryType)> entries = [];
        foreach (var (path, kind) in new[]
                 {
                     ("indexS_1.html", LauncherNewsEntryType.Info), ("indexS_2.html", LauncherNewsEntryType.Notice),
                     ("indexS_3.html", LauncherNewsEntryType.Event)
                 })
        {
            try
            {
                string html = await ApiResponseHttpClient
                    .GetStringAsync("https://nte.iwplay.com.tw/news/" + path, token).ConfigureAwait(false);
                entries.AddRange(NteTaiwanLauncherContent.ParseNews(html, kind));
            }
            catch (HttpRequestException ex)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "Cannot load Taiwan news fragment {Path}.", path);
            }
        }

        return [.. entries];
    }

    private async Task<(string Content, string Url)> FetchCssContentAsync(string html, CancellationToken token)
    {
        HtmlDocument document = new();
        document.LoadHtml(html);
        HtmlNodeCollection? nodes = document.DocumentNode.SelectNodes("//link[@href]");
        if (nodes != null)
        {
            foreach (HtmlNode link in nodes)
            {
                string href = link.GetAttributeValue("href", "");
                if (!href.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
                    !href.Contains(".css", StringComparison.OrdinalIgnoreCase))
                    continue;
                string url = ResolveSiteUrl(href);
                try
                {
                    return (await ApiResponseHttpClient.GetStringAsync(url, token).ConfigureAwait(false), url);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    SharedStatic.InstanceLogger.LogWarning(ex, "Cannot load NTE CSS {Url}.", url);
                }
            }
        }

        return ("", PageUrl);
    }

    private (string, string, string, LauncherNewsEntryType)[] ParseHtmlNews(string html)
    {
        HtmlDocument document = new();
        document.LoadHtml(html);
        List<(string, string, string, LauncherNewsEntryType)> entries = [];
        HtmlNodeCollection? containers = document.DocumentNode.SelectNodes("//div[contains(@class, 'news-cont')]");
        if (containers == null)
            return [];
        LauncherNewsEntryType[] types =
            [LauncherNewsEntryType.Info, LauncherNewsEntryType.Notice, LauncherNewsEntryType.Event];
        for (int i = 0; i < Math.Min(containers.Count, types.Length); i++)
        {
            HtmlNodeCollection? nodes = containers[i].SelectNodes(".//li");
            if (nodes == null)
                continue;
            foreach (HtmlNode node in nodes)
            {
                HtmlNode? link = node.SelectSingleNode(".//a");
                string title = HtmlEntity.DeEntitize(link?.InnerText ?? "").Trim();
                HtmlNodeCollection? dates = node.SelectNodes(".//span");
                string date = dates?.Count > 0 ? dates[^1].InnerText.Trim() : "";
                if (title.Length != 0)
                    entries.Add((title, ResolveSiteUrl(link?.GetAttributeValue("href", "")), date, types[i]));
            }
        }

        return [.. entries];
    }

    private async Task<(string, string, string)[]> FetchGlobalCarouselAsync(string html, CancellationToken token)
    {
        Match key = Regex.Match(html, @"setNewsBanner\s*\(\s*['"" ]*(lb1_[a-z]+)['"" ]*\s*\)");
        if (!key.Success) throw new InvalidDataException("International launcher page has no localized carousel key.");
        string script = await ApiResponseHttpClient.GetStringAsync(
                "https://www.perfectworld.com/public/commonData/gamesData/gameSwiper/nte-gameSwiper.js", token)
            .ConfigureAwait(false);
        return ParseCarouselItems(ExtractJsonObjectFromJsVariable(script, "nte_data_data"), key.Groups[1].Value);
    }

    internal static (string, string, string)[] ParseCarouselItems(string jsonPayload, string key = "lb1")
    {
        using JsonDocument doc = JsonDocument.Parse(jsonPayload);
        JsonElement root = doc.RootElement;

        List<(string, string, string)> items = new(12);

        if (!root.TryGetProperty(key, out JsonElement entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (JsonElement item in entries.EnumerateArray())
        {
            if (!item.TryGetProperty("bigpic", out JsonElement bigPicElement) ||
                bigPicElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? url = bigPicElement.GetString();
            if (string.IsNullOrWhiteSpace(url)) continue;

            string title = item.TryGetProperty("title", out JsonElement titleElement)
                ? titleElement.GetString() ?? ""
                : "";
            string link = item.TryGetProperty("link", out JsonElement linkElement) ? linkElement.GetString() ?? "" : "";

            items.Add((title, url, link));
        }

        return [.. items];
    }

    private static string ExtractJsonObjectFromJsVariable(string scriptText, string variableName)
    {
        int variableIndex = scriptText.IndexOf(variableName, StringComparison.Ordinal);
        if (variableIndex < 0)
            throw new InvalidOperationException($"Cannot find variable '{variableName}' in response body.");

        int objectStart = scriptText.IndexOf('{', variableIndex);
        if (objectStart < 0)
            throw new InvalidOperationException("Cannot find JSON object start after variable declaration.");

        int objectEnd = FindJsonObjectEnd(scriptText, objectStart);
        if (objectEnd < objectStart)
            throw new InvalidOperationException("Cannot find JSON object end in response body.");

        return scriptText.Substring(objectStart, objectEnd - objectStart + 1);
    }

    private static int FindJsonObjectEnd(string text, int objectStart)
    {
        int depth = 0;
        bool inString = false;
        bool escapeNext = false;

        for (int i = objectStart; i < text.Length; i++)
        {
            char ch = text[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inString)
            {
                if (ch == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0) return i;
        }

        return -1;
    }

    private static void InitializeEmpty(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        handle = nint.Zero;
        count = 0;
        isDisposable = false;
        isAllocated = false;
    }

    private string ResolveSiteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absUri))
            return absUri.ToString();

        if (Uri.TryCreate(new Uri(SiteUrl), url, out Uri? relUri))
            return relUri.ToString();

        return url;
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
}
