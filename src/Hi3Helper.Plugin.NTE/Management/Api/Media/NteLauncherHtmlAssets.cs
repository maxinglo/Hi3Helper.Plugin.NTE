using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal sealed record NteSocialMediaSource(
    string Description,
    string ClickUrl,
    string IconUrl,
    string QrUrl,
    double FrameAspectRatio,
    string? HoverIconUrl = null);

internal sealed record NteSocialMediaAsset(
    string Description,
    string ClickUrl,
    string IconPath,
    string IconHoverPath,
    string QrUrl);

internal static class NteLauncherHtmlAssets
{
    internal static string FindImageUrl(string html, string className, string pageUrl)
    {
        HtmlDocument document = new();
        document.LoadHtml(html);
        HtmlNode? image = document.DocumentNode.SelectSingleNode(
            "//img[contains(concat(' ', normalize-space(@class), ' '), ' " + className + " ')]");
        return ResolveUrl(image?.GetAttributeValue("src", ""), pageUrl);
    }

    internal static NteSocialMediaSource[] ParseSocials(string html, string css, string pageUrl, string cssUrl)
    {
        Dictionary<string, string> icons = new(StringComparer.Ordinal);
        Dictionary<string, string> hoverIcons = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(css,
                     @"\.(icon-[a-zA-Z0-9_-]+)(::before|::after)?\s*\{[^}]*?url\(([^)]+)\)",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var target = match.Groups[2].Value.Equals("::after", StringComparison.OrdinalIgnoreCase)
                ? hoverIcons
                : icons;
            target[match.Groups[1].Value] = ResolveUrl(match.Groups[3].Value.Trim().Trim('\'', '"'), cssUrl);
        }

        double frameAspect = GetFrameAspectRatio(css);
        HtmlDocument document = new();
        document.LoadHtml(html);
        HtmlNodeCollection? nodes = document.DocumentNode.SelectNodes(
            "//ul[contains(concat(' ', normalize-space(@class), ' '), ' ewm-list ')]/li");
        List<NteSocialMediaSource> sources = [];
        if (nodes == null)
            return [];
        foreach (HtmlNode node in nodes)
        {
            string? iconUrl = null;
            string? hoverUrl = null;
            foreach (Match match in Regex.Matches(node.GetAttributeValue("class", ""), @"\bicon-[a-zA-Z0-9_-]+\b"))
            {
                if (icons.TryGetValue(match.Value, out iconUrl))
                {
                    hoverIcons.TryGetValue(match.Value, out hoverUrl);
                    break;
                }
            }

            if (string.IsNullOrEmpty(iconUrl))
                continue;
            HtmlNode? link = node.SelectSingleNode(".//a[@href]");
            string title = HtmlEntity.DeEntitize(
                node.SelectSingleNode(".//span[contains(@class, 'ewm-tip')]")?.InnerText
                ?? link?.GetAttributeValue("title", "") ?? "").Trim();
            if (title.Length == 0) title = HtmlEntity.DeEntitize(link?.InnerText ?? "").Trim();
            if (title.Length == 0) title = "Customer Service";
            string qr = ResolveUrl(node.SelectSingleNode(".//img")?.GetAttributeValue("src", ""), pageUrl);
            sources.Add(new NteSocialMediaSource(title, ResolveUrl(link?.GetAttributeValue("href", ""), pageUrl),
                iconUrl, qr, frameAspect, hoverUrl));
        }

        return [.. sources];
    }

    internal static string ResolveUrl(string? value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#') ||
            value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return "";
        return Uri.TryCreate(new Uri(baseUrl), HtmlEntity.DeEntitize(value.Trim()), out Uri? uri)
            ? uri.AbsoluteUri
            : "";
    }

    private static double GetFrameAspectRatio(string css)
    {
        Match rule = Regex.Match(css, @"\.ewm-list\s+li\s*\{([^}]+)\}", RegexOptions.IgnoreCase);
        Match width = Regex.Match(rule.Groups[1].Value, @"(?:^|;)\s*width\s*:\s*([\d.]+)(rem|px)",
            RegexOptions.IgnoreCase);
        Match height = Regex.Match(rule.Groups[1].Value, @"(?:^|;)\s*height\s*:\s*([\d.]+)(rem|px)",
            RegexOptions.IgnoreCase);
        if (width.Success && height.Success &&
            width.Groups[2].Value.Equals(height.Groups[2].Value, StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(width.Groups[1].Value, CultureInfo.InvariantCulture, out double w) && w > 0 &&
            double.TryParse(height.Groups[1].Value, CultureInfo.InvariantCulture, out double h) && h > 0)
            return h / w;
        return 42d / 35d;
    }
}
