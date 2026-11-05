using Hi3Helper.Plugin.NTE.Management.Config;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class RegionDefinitionTests
{
    [Theory]
    [InlineData("CN", "1289", "NTELauncher", "NTEGame.exe", 1u)]
    [InlineData("Global", "3000001", "NTEGlobal", "NTEGlobalGame.exe", 100u)]
    [InlineData("Taiwan", "2000013", "NTETW", "NTETWGame.exe", 100u)]
    public void NativePresetAndLoginHostAgreeOnTheRegionIdentity(string region, string appId, string directory,
        string launcher, uint notification)
    {
        NteHostRegion selected = Enum.Parse<NteHostRegion>(region);
        NteRegionDefinition definition = NteRegionDefinition.From(selected);
        var preset = NteConfigProvider.GetRegion(appId);
        Assert.Equal(appId, definition.AppId);
        Assert.Equal(directory, definition.LauncherDirectory);
        Assert.Equal(launcher, definition.LauncherExecutable);
        Assert.Equal(notification, definition.LoginNotification);
        Assert.Same(definition, NteRegionDefinition.FromLauncherDirectory(directory));
        Assert.Equal(definition.LauncherDirectory, preset.LauncherDirectoryName);
        Assert.Equal(definition.AppId, preset.AppId);
        Assert.Equal(NteRegionDefinition.GameExecutableRelativePath, preset.GameExecutableName);
        Assert.Equal("/Game/LoginAndCreate/Map/Updater/Updater_P -SAVEWINPOS=1", NteConfigProvider.RequiredStartArgument);
        Assert.Equal(NteConfigProvider.RequiredStartArgument, new NteHostLaunchOptions(Path.GetTempPath(), Region: selected).GameArguments);
        if (definition.IsOverseas)
        {
            Assert.Same(definition, NteOverseasRegion.From(selected).Installation);
            Assert.Equal(appId, new NteRegionSwitchOptions(Path.GetTempPath(), selected).TargetAppId);
        }
    }

    [Fact]
    public void EveryDeclaredRegionHasOneSharedDefinitionAndNativePreset()
    {
        Assert.Equal(Enum.GetValues<NteHostRegion>().Length, NteConfigProvider.AllRegions.Count);
        foreach (NteHostRegion region in Enum.GetValues<NteHostRegion>())
        {
            NteRegionDefinition definition = NteRegionDefinition.From(region);
            Assert.Single(NteConfigProvider.AllRegions, preset => preset.AppId == definition.AppId);
            Assert.True(NteRegionDefinition.IsSupportedAppId(definition.AppId));
        }
        Assert.False(NteRegionDefinition.IsSupportedAppId("9999999"));
        Assert.Throws<ArgumentException>(() => NteRegionDefinition.From((NteHostRegion)999));
        Assert.Throws<ArgumentException>(() => NteRegionDefinition.FromLauncherDirectory("../NTELauncher"));
    }
}
