using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class RegionInstallationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteRegionInstallationTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InternationalProfile_RejectsChinaDirectoryAndPreservesItsMarker(bool officialOnly)
    {
        string china = CreateGame("china", NteConfigProvider.CN, "1.4.17", officialOnly);
        string marker = Path.Combine(china, officialOnly ? NteConfigProvider.CN.SdkDirectory + "/config.xml" : ".collapse_nte_config");
        byte[] before = File.ReadAllBytes(marker);
        NteGameManager manager = new(region: NteConfigProvider.Global);
        manager.SetGamePath(china);
        manager.LoadConfig();
        manager.IsGameInstalled(out bool installed);
        manager.GetCurrentGameVersion(out GameVersion version);
        manager.GetGamePath(out string? selected);
        Assert.False(installed);
        Assert.Equal(GameVersion.Empty, version);
        Assert.Null(selected);
        manager.SaveConfig();
        Assert.Equal(before, File.ReadAllBytes(marker));
    }

    [Fact]
    public void ChinaProfile_RejectsInternationalDirectory()
    {
        string global = CreateGame("global", NteConfigProvider.Global, "1.4.10");
        NteGameManager manager = new();
        manager.SetGamePath(global); manager.LoadConfig();
        manager.IsGameInstalled(out bool installed);
        Assert.False(installed);
        manager.GetGamePath(out string? selected);
        Assert.Null(selected);
    }

    [Fact]
    public void ChangingPath_ClearsInstalledVersionEvenIfAnotherExecutableExists()
    {
        string installed = CreateGame("global", NteConfigProvider.Global, "1.4.10");
        string incomplete = Path.Combine(root, "incomplete");
        CreateExecutable(incomplete);
        NteGameManager manager = new(region: NteConfigProvider.Global);
        manager.SetGamePath(installed); manager.LoadConfig();
        manager.IsGameInstalled(out bool initiallyInstalled); Assert.True(initiallyInstalled);
        manager.SetGamePath(incomplete); manager.LoadConfig();
        manager.IsGameInstalled(out bool installedAfterChange); Assert.False(installedAfterChange);
        manager.GetCurrentGameVersion(out GameVersion version); Assert.Equal(GameVersion.Empty, version);
    }

    [Fact]
    public void ReapplyingSamePath_DoesNotEraseTheVersionDuringInstallFinalization()
    {
        string installed = CreateGame("global", NteConfigProvider.Global, "1.4.10");
        NteGameManager manager = new(region: NteConfigProvider.Global);
        manager.SetGamePath(installed); manager.LoadConfig();
        manager.SetGamePath(installed); manager.SaveConfig();
        manager.GetCurrentGameVersion(out GameVersion version);
        Assert.Equal(new GameVersion("1.4.10"), version);
        Assert.Contains("GameVersion=1.4.10", File.ReadAllText(Path.Combine(installed, ".collapse_nte_config")));
    }

    [Fact]
    public void RecheckingPath_DoesNotAcceptAForeignMarkerCreatedAfterSelection()
    {
        string path = Path.Combine(root, "changed"); CreateExecutable(path);
        NteGameManager manager = new(region: NteConfigProvider.Global);
        manager.SetGamePath(path);
        File.WriteAllText(Path.Combine(path, ".collapse_nte_config"), "GameVersion=1.4.17\n");
        manager.SetCurrentGameVersion(new GameVersion("1.4.17"));
        manager.IsGameInstalled(out bool installed); Assert.False(installed);
        manager.SaveConfig();
        Assert.Equal("GameVersion=1.4.17\n", File.ReadAllText(Path.Combine(path, ".collapse_nte_config")));
    }

    [Fact]
    public void BothProfiles_RecognizeOnlyTheirOwnInstalledGames()
    {
        string china = CreateGame("china", NteConfigProvider.CN, "1.4.17");
        string global = CreateGame("global", NteConfigProvider.Global, "1.4.10");
        NteGameManager cnManager = new(), globalManager = new(region: NteConfigProvider.Global);
        cnManager.SetGamePath(china); cnManager.LoadConfig();
        globalManager.SetGamePath(global); globalManager.LoadConfig();
        cnManager.IsGameInstalled(out bool cnInstalled); globalManager.IsGameInstalled(out bool globalInstalled);
        Assert.True(cnInstalled); Assert.True(globalInstalled);
        globalManager.SetGamePath(china); globalManager.LoadConfig();
        globalManager.IsGameInstalled(out bool incorrectlyInstalled); Assert.False(incorrectlyInstalled);
        cnManager.GetCurrentGameVersion(out GameVersion chinaVersion); Assert.Equal(new GameVersion("1.4.17"), chinaVersion);
    }

    private string CreateGame(string name, NteRegionConfig region, string version, bool officialOnly = false)
    {
        string path = Path.Combine(root, name); CreateExecutable(path);
        if (officialOnly)
        {
            string sdk = Path.Combine(path, region.SdkDirectory); Directory.CreateDirectory(sdk);
            File.WriteAllText(Path.Combine(sdk, "config.xml"), "<config><ResVersion>" + version + "</ResVersion></config>");
        }
        else File.WriteAllText(Path.Combine(path, ".collapse_nte_config"), "GameVersion=" + version + "\n" + (region.IsGlobal ? "AppId=3000001\n" : ""));
        return path;
    }

    private static void CreateExecutable(string path)
    {
        string exe = Path.Combine(path, NteConfigProvider.GameExecutableRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "fixture executable; never executed");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
