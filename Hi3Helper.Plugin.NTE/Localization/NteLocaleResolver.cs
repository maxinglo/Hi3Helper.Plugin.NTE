using System;
using System.Globalization;

namespace Hi3Helper.Plugin.NTE.Localization;

public static class NteLocaleResolver
{
    public static readonly CultureInfo DefaultCulture = CultureInfo.InvariantCulture;

    public static CultureInfo ResolveCulture(string? locale)
    {
        string normalized = Normalize(locale);

        try
        {
            return CultureInfo.GetCultureInfo(normalized);
        }
        catch (CultureNotFoundException)
        {
            return DefaultCulture;
        }
    }

    public static string Normalize(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return DefaultCulture.Name;
        }

        ReadOnlySpan<char> localeSpan = locale.AsSpan().Trim();
        Span<Range> splitRanges = stackalloc Range[2];
        int splitCount = localeSpan.SplitAny(splitRanges, "-_", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (splitCount < 1)
        {
            return DefaultCulture.Name;
        }

        string language = localeSpan[splitRanges[0]].ToString().ToLowerInvariant();
        if (language == "zh")
        {
            return "zh-CN";
        }

        if (language == "en")
        {
            return "en-US";
        }

        if (splitCount == 2)
        {
            string region = localeSpan[splitRanges[1]].ToString().ToUpperInvariant();
            return $"{language}-{region}";
        }

        return language;
    }
}

