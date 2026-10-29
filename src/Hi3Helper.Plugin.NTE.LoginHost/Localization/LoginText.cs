using System.Globalization;
using System.Resources;

namespace Nte.LoginHost.Localization;

internal static class LoginText
{
    private static readonly ResourceManager Resources = new("Nte.LoginHost.Localization.Strings", typeof(LoginText).Assembly);
    internal static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("zh-CN");
    internal static string WebLanguage => Culture.Name.ToLowerInvariant();
    internal static void UseLanguage(string? language)
    {
        string normalized = language?.Replace('_', '-').ToLowerInvariant() ?? "en";
        string value = normalized.StartsWith("zh", StringComparison.Ordinal)
            ? normalized.Contains("hant", StringComparison.Ordinal) || normalized is "zh-tw" or "zh-hk" or "zh-mo" ? "zh-TW" : "zh-CN"
            : normalized.Split('-')[0];
        Culture = CultureInfo.GetCultureInfo(value is "zh-CN" or "zh-TW" or "en" or "ja" or "ko" or "de" or "fr" or "ru" or "es" ? value : "en");
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }
    internal static string Get(string key, params object[] arguments)
    {
        string text = Resources.GetString(key, Culture) ?? throw new MissingManifestResourceException("Missing login text: " + key);
        return arguments.Length == 0 ? text : string.Format(Culture, text, arguments);
    }
}
