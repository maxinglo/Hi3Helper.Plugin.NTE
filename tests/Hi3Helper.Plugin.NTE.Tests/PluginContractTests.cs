using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

[CollectionDefinition("Plugin locale", DisableParallelization = true)]
public sealed class PluginLocaleCollection;

[Collection("Plugin locale")]
public sealed class PluginContractTests
{
    [Theory]
    [InlineData("en_US", "Neverness to Everness Plugin", "Neverness to Everness")]
    [InlineData("zh_CN", "异环插件", "异环")]
    public void Metadata_UsesRequestedLocale(string locale, string pluginName, string gameName)
    {
        NtePlugin plugin = new();
        try
        {
            plugin.SetPluginLocaleId(locale);
            plugin.GetPluginName(out string name);
            Assert.Equal(pluginName, name);
            plugin.GetPresetConfigCount(out int count);
            Assert.Equal(3, count);
            plugin.GetPresetConfig(0, out var preset);
            preset.comGet_GameName(out string actualGameName);
            Assert.Equal(gameName, actualGameName);
            preset.comGet_ZoneName(out string zone);
            Assert.Equal(locale == "zh_CN" ? "中国大陆" : "Mainland China", zone);
            plugin.GetPresetConfig(1, out var global);
            global.comGet_ProfileName(out string profile);
            Assert.Equal("NteGlobal", profile);
            global.comGet_ZoneName(out string globalZone);
            Assert.Equal(locale == "zh_CN" ? "国际服" : "Global", globalZone);
        }
        finally
        {
            plugin.SetPluginLocaleId("en-US");
        }
    }

    [Fact]
    public void OptionalMetadata_HasDefinedValues()
    {
        NtePlugin plugin = new();
        plugin.GetPluginAppIconUrl(out string icon);
        plugin.GetNotificationPosterUrl(out string poster);
        plugin.GetPluginSelfUpdater(out var updater);
        Assert.True(Uri.TryCreate(icon, UriKind.Absolute, out _));
        Assert.True(Uri.TryCreate(poster, UriKind.Absolute, out _));
        Assert.Null(updater);
    }
}
