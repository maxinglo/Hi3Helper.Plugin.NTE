using Hi3Helper.Plugin.Core.Management;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal static class NteTaiwanLauncherContent
{
    private const string Origin = "https://nte.iwplay.com.tw/";

    internal static (string title, string img, string link)[] ParseCarousel(string html, DateTimeOffset now)
    {
        HtmlDocument document = new(); document.LoadHtml(html);
        List<(string, string, string)> entries = [];
        var nodes = document.DocumentNode.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' carousel-item ')]");
        if (nodes == null) return [];
        foreach (HtmlNode node in nodes)
        {
            if (!TryDate(node.GetAttributeValue("on", ""), out var on) || !TryDate(node.GetAttributeValue("off", ""), out var off) || on > off || now < on || now > off) continue;
            HtmlNode? anchor = node.SelectSingleNode(".//a[@href]");
            HtmlNode? image = anchor?.SelectSingleNode(".//img[@src]");
            string link = HttpUrl(anchor?.GetAttributeValue("href", ""));
            string img = HttpUrl(image?.GetAttributeValue("src", ""));
            string title = HtmlEntity.DeEntitize(image?.GetAttributeValue("alt", "") ?? "").Trim();
            if (title == "...") title = "";
            if (link.Length != 0 && img.Length != 0) entries.Add((title, img, link));
        }
        return [.. entries];
    }

    internal static (string title, string link, string date, LauncherNewsEntryType type)[] ParseNews(string html, LauncherNewsEntryType type)
    {
        HtmlDocument document = new(); document.LoadHtml(html);
        List<(string, string, string, LauncherNewsEntryType)> entries = [];
        HashSet<string> links = new(StringComparer.Ordinal);
        var anchors = document.DocumentNode.SelectNodes("//a[@href and (contains(concat(' ',normalize-space(@class),' '),' intelSlideTit ') or contains(concat(' ',normalize-space(@class),' '),' latestnews-titleurl '))]");
        if (anchors == null) return [];
        foreach (HtmlNode anchor in anchors)
        {
            string link = HttpUrl(anchor.GetAttributeValue("href", ""));
            if (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) || uri.Host != "nte.iwplay.com.tw" || !uri.AbsolutePath.StartsWith("/news/view/", StringComparison.Ordinal) || !links.Add(link)) continue;
            HtmlNode container = anchor.ParentNode ?? anchor;
            HtmlNode? heading = anchor.SelectSingleNode(".//*[contains(@class,'intelSlideTit') or contains(@class,'latestnews-title')]");
            string title = HtmlEntity.DeEntitize(heading?.InnerText ?? anchor.GetAttributeValue("title", "")).Trim();
            if (title.Length == 0) title = HtmlEntity.DeEntitize(anchor.InnerText).Trim();
            string date = HtmlEntity.DeEntitize(container.SelectSingleNode(".//*[contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'time') or contains(translate(@class,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'date')]")?.InnerText ?? "").Trim();
            if (title.Length != 0) entries.Add((title, link, date, type));
        }
        return [.. entries];
    }

    private static string HttpUrl(string? value)
    {
        string resolved = NteLauncherHtmlAssets.ResolveUrl(value, Origin);
        return Uri.TryCreate(resolved, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? resolved : "";
    }

    private static bool TryDate(string value, out DateTimeOffset result)
    {
        if (DateTime.TryParseExact(value, ["yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        { result = new(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), TimeSpan.FromHours(8)); return true; }
        result = default; return false;
    }
}
