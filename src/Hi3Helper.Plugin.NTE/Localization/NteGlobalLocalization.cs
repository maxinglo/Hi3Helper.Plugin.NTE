using System;

namespace Hi3Helper.Plugin.NTE.Localization;

/// <summary>Official international launcher language and artwork identifiers.</summary>
internal static class NteGlobalLocalization
{
    internal static string ResolveLanguage(string? locale)
    {
        string value = locale?.Trim().Replace('_', '-').ToLowerInvariant() ?? "";
        if (value == "zh" || value.StartsWith("zh-", StringComparison.Ordinal))
            return value.Contains("hant", StringComparison.Ordinal) || value is "zh-tw" or "zh-hk" or "zh-mo" ? "zh-tw" : "zh-cn";
        string language = value.Split('-')[0];
        return language is "en" or "ja" or "ko" or "de" or "fr" or "ru" or "es" ? language : "en";
    }

    internal static string GetPageUrl(string? locale)
    {
        string language = ResolveLanguage(locale);
        string suffix = language switch { "zh-cn" => "cn", "zh-tw" => "tc", "ja" => "jp", "ko" => "kr", _ => language };
        return "https://nte.perfectworld.com/launcher/launcher_ob2_" + suffix + ".html";
    }

    internal static string GetArtworkPath(string? locale, string image = "slogan")
        => "res/res/3000001/image_lang/" + ResolveLanguage(locale) + "/main/" + image + ".png";
}
