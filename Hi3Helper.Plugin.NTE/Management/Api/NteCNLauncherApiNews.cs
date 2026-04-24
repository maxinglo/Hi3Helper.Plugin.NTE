using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Management;
using HtmlAgilityPack;

namespace Hi3Helper.Plugin.NTE.Management.Api;

[GeneratedComClass]
internal partial class NteCNLauncherApiNews : LauncherApiNewsBase
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

    private (string title, string img, string link)[] _carouselItems = [];
    private (string title, string link, string date, LauncherNewsEntryType type)[] _newsItems = [];
    private (string description, string clickUrl, string iconUrl, string qrUrl)[] _socialMediaItems = [];

    public override void GetNewsEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_newsItems.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherNewsEntry> entriesData = PluginDisposableMemory<LauncherNewsEntry>.Alloc(_newsItems.Length);
        for (int i = 0; i < _newsItems.Length; i++)
        {
            ref LauncherNewsEntry entry = ref entriesData[i];
            entry.Write(_newsItems[i].title, _newsItems[i].title, _newsItems[i].link, _newsItems[i].date, _newsItems[i].type);
        }

        handle = entriesData.AsSafePointer();
        count = entriesData.Length;
        isDisposable = entriesData.IsDisposable == 1;
        isAllocated = true;
    }

    public override void GetCarouselEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_carouselItems.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherCarouselEntry> entriesData = PluginDisposableMemory<LauncherCarouselEntry>.Alloc(_carouselItems.Length);
        for (int i = 0; i < _carouselItems.Length; i++)
        {
            ref LauncherCarouselEntry entry = ref entriesData[i];
            entry.Write(_carouselItems[i].title, _carouselItems[i].img, _carouselItems[i].link);
        }

        handle = entriesData.AsSafePointer();
        count = entriesData.Length;
        isDisposable = entriesData.IsDisposable == 1;
        isAllocated = true;
    }

    public override void GetSocialMediaEntries(out nint handle, out int count, out bool isDisposable, out bool isAllocated)
    {
        if (_socialMediaItems.Length == 0)
        {
            InitializeEmpty(out handle, out count, out isDisposable, out isAllocated);
            return;
        }

        PluginDisposableMemory<LauncherSocialMediaEntry> entriesData = PluginDisposableMemory<LauncherSocialMediaEntry>.Alloc(_socialMediaItems.Length);
        for (int i = 0; i < _socialMediaItems.Length; i++)
        {
            ref LauncherSocialMediaEntry entry = ref entriesData[i];
            entry.WriteDescription(_socialMediaItems[i].description);
            entry.WriteIcon(_socialMediaItems[i].iconUrl);
            entry.WriteClickUrl(_socialMediaItems[i].clickUrl);
            if (!string.IsNullOrEmpty(_socialMediaItems[i].qrUrl))
            {
                entry.WriteQrImage(_socialMediaItems[i].qrUrl);
            }
        }

        handle = entriesData.AsSafePointer();
        count = entriesData.Length;
        isDisposable = entriesData.IsDisposable == 1;
        isAllocated = true;
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        // Fetch carousel and HTML in parallel; each sub-fetch catches independently
        // (following the Wuwa pattern where social/news are fetched separately).
        Task<(string title, string img, string link)[]> carouselTask = FetchCarouselAsync(token);
        Task<((string, string, string, LauncherNewsEntryType)[], (string, string, string, string)[])> htmlTask = FetchHtmlNewsAndSocialsAsync(token);

        await Task.WhenAll(carouselTask, htmlTask);

        var carouselItems = await carouselTask;
        var (newsItems, socialItems) = await htmlTask;

        using (ThisInstanceLock.EnterScope())
        {
            _carouselItems = carouselItems;
            _newsItems = newsItems;
            _socialMediaItems = socialItems;
        }

        SharedStatic.InstanceLogger.LogInformation(
            "[NteCNLauncherApiNews::InitAsync] Loaded {C} carousel, {N} news, {S} socials.",
            carouselItems.Length, newsItems.Length, socialItems.Length);

        return 0;
    }

    private async Task<(string title, string img, string link)[]> FetchCarouselAsync(CancellationToken token)
    {
        try
        {
            using HttpResponseMessage swiperResponse = await ApiResponseHttpClient.GetAsync(NteConfigProvider.SwiperScriptUrl, token);
            swiperResponse.EnsureSuccessStatusCode();
            string scriptText = await swiperResponse.Content.ReadAsStringAsync(token);
            string jsonPayload = ExtractJsonObjectFromJsVariable(scriptText, "yh_data_data");
            return ParseCarouselItems(jsonPayload);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[NteCNLauncherApiNews::FetchCarouselAsync] Failed to load carousel entries: {Error}",
                ex.Message);
            return [];
        }
    }

    private async Task<((string, string, string, LauncherNewsEntryType)[], (string, string, string, string)[])> FetchHtmlNewsAndSocialsAsync(CancellationToken token)
    {
        try
        {
            using HttpResponseMessage htmlResponse = await ApiResponseHttpClient.GetAsync(NteConfigProvider.LauncherHtmlUrl, token);
            htmlResponse.EnsureSuccessStatusCode();
            string htmlContent = await htmlResponse.Content.ReadAsStringAsync(token);

            string cssContent = await FetchCssContentAsync(htmlContent, token);

            return ParseHtmlNewsAndSocials(htmlContent, cssContent);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(
                "[NteCNLauncherApiNews::FetchHtmlNewsAndSocialsAsync] Failed to load news/social entries: {Error}",
                ex.Message);
            return ([], []);
        }
    }

    private async Task<string> FetchCssContentAsync(string htmlContent, CancellationToken token)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(htmlContent);

        var linkNodes = doc.DocumentNode.SelectNodes("//link[@href]");
        if (linkNodes == null)
            return string.Empty;

        foreach (HtmlNode link in linkNodes)
        {
            string href = link.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href) ||
                href.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) < 0 ||
                href.IndexOf(".css", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string cssUrl = ResolveSiteUrl(href);
            if (string.IsNullOrEmpty(cssUrl))
                continue;

            try
            {
                using HttpResponseMessage cssResponse = await ApiResponseHttpClient.GetAsync(cssUrl, token);
                cssResponse.EnsureSuccessStatusCode();
                return await cssResponse.Content.ReadAsStringAsync(token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(
                    "[NteCNLauncherApiNews::FetchCssContentAsync] Failed to load CSS from {Url}: {Error}",
                    cssUrl, ex.Message);
            }
        }

        return string.Empty;
    }

    private static ((string, string, string, LauncherNewsEntryType)[], (string, string, string, string)[]) ParseHtmlNewsAndSocials(string htmlContent, string cssContent)
    {
        HtmlDocument doc = new();
        doc.LoadHtml(htmlContent);

        List<(string, string, string, LauncherNewsEntryType)> newsList = new();
        List<(string, string, string, string)> socialList = new();

        Dictionary<string, string> cssIcons = new();
        if (!string.IsNullOrEmpty(cssContent))
        {
            var matches = Regex.Matches(
                cssContent,
                @"\.(icon-[a-zA-Z0-9_-]+)\s*\{[^}]*?url\(([^)]+)\)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match m in matches)
            {
                string cls = m.Groups[1].Value;
                string url = m.Groups[2].Value.Trim('\'', '"');
                cssIcons[cls] = ResolveSiteUrl(url);
            }
        }

        var newsContNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'news-cont')]");
        if (newsContNodes != null)
        {
            LauncherNewsEntryType[] types = [LauncherNewsEntryType.Info, LauncherNewsEntryType.Notice, LauncherNewsEntryType.Event];
            for (int i = 0; i < Math.Min(newsContNodes.Count, types.Length); i++)
            {
                var liNodes = newsContNodes[i].SelectNodes(".//li");
                if (liNodes == null) continue;

                foreach (var li in liNodes)
                {
                    string title = li.SelectSingleNode(".//a")?.InnerText?.Trim() ?? "";
                    string link = li.SelectSingleNode(".//a")?.GetAttributeValue("href", "") ?? "";
                    link = ResolveSiteUrl(link);
                    
                    var spanNodes = li.SelectNodes(".//span");
                    string date = spanNodes?.Count > 0 ? spanNodes[spanNodes.Count - 1].InnerText?.Trim() ?? "" : "";

                    if (!string.IsNullOrEmpty(title))
                    {
                        newsList.Add((title, link, date, types[i]));
                    }
                }
            }
        }

        var ewmNodes = doc.DocumentNode.SelectNodes("//ul[contains(@class, 'ewm-list')]/li");
        if (ewmNodes != null)
        {
            foreach (var li in ewmNodes)
            {
                string iconClass = li.GetAttributeValue("class", "") ?? "";
                var aNode = li.SelectSingleNode(".//a");
                var imgNode = li.SelectSingleNode(".//img");
                var tipNode = li.SelectSingleNode(".//span[contains(@class, 'ewm-tip')]");

                string description = tipNode?.InnerText?.Trim() ?? aNode?.InnerText?.Trim() ?? "Social Media";
                string clickUrl = aNode?.GetAttributeValue("href", "") ?? "";
                string qrUrl = imgNode?.GetAttributeValue("src", "") ?? "";

                string iconUrl = "";
                foreach (Match classMatch in Regex.Matches(iconClass, @"\bicon-[a-zA-Z0-9_-]+\b"))
                {
                    if (cssIcons.TryGetValue(classMatch.Value, out string? mappedUrl))
                    {
                        iconUrl = mappedUrl;
                        break;
                    }
                }

                clickUrl = ResolveSiteUrl(clickUrl);
                qrUrl = ResolveSiteUrl(qrUrl);
                if (string.IsNullOrWhiteSpace(iconUrl))
                    iconUrl = qrUrl;

                socialList.Add((description, clickUrl, iconUrl, qrUrl));
            }
        }

        return ([.. newsList], [.. socialList]);
    }

    private static (string, string, string)[] ParseCarouselItems(string jsonPayload)
    {
        using JsonDocument doc = JsonDocument.Parse(jsonPayload);
        JsonElement root = doc.RootElement;

        List<(string, string, string)> items = new(12);

        if (!root.TryGetProperty("lb1", out JsonElement entries) ||
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

            string title = item.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() ?? "" : "";
            string link = item.TryGetProperty("link", out JsonElement linkElement) ? linkElement.GetString() ?? "" : "";

            items.Add((title, url, link));
        }

        return [.. items];
    }

    private static string ExtractJsonObjectFromJsVariable(string scriptText, string variableName)
    {
        int variableIndex = scriptText.IndexOf(variableName, StringComparison.Ordinal);
        if (variableIndex < 0) throw new InvalidOperationException($"Cannot find variable '{variableName}' in response body.");

        int objectStart = scriptText.IndexOf('{', variableIndex);
        if (objectStart < 0) throw new InvalidOperationException("Cannot find JSON object start after variable declaration.");

        int objectEnd = FindJsonObjectEnd(scriptText, objectStart);
        if (objectEnd < objectStart) throw new InvalidOperationException("Cannot find JSON object end in response body.");

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
            if (escapeNext) { escapeNext = false; continue; }
            if (inString)
            {
                if (ch == '\\') { escapeNext = true; continue; }
                if (ch == '"') { inString = false; }
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') { depth++; continue; }
            if (ch != '}') { continue; }
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

    private static string ResolveSiteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absUri))
            return absUri.ToString();

        if (Uri.TryCreate(new Uri(NteConfigProvider.OfficialSiteUrl), url, out Uri? relUri))
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
