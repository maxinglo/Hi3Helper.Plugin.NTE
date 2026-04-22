using Hi3Helper.Plugin.Core;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Hi3Helper.Plugin.NTE.Localization;

public static class NteResourceProvider
{
    private static readonly ResourceManager ResourceManager =
        new("Hi3Helper.Plugin.NTE.Resources.Strings", Assembly.GetExecutingAssembly());

    public static string GetString(string key)
    {
        CultureInfo culture = NteLocaleResolver.ResolveCulture(SharedStatic.PluginLocaleCode);
        string? value = ResourceManager.GetString(key, culture);

        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        value = ResourceManager.GetString(key, NteLocaleResolver.DefaultCulture);
        return string.IsNullOrEmpty(value) ? key : value;
    }
}

