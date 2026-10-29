using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.NTE.Localization;
using Hi3Helper.Plugin.NTE.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using System.Collections.Generic;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Hi3Helper.Plugin.Core;

namespace Hi3Helper.Plugin.NTE.Management.PresetConfig;

[GeneratedComClass]
public partial class NteCNPresetConfig : PluginPresetConfigBase
{
    internal NteRegionConfig Region { get; }
    internal NteAppearanceStore Appearance { get; }
    internal NteLaunchSettings LaunchSettings { get; }
    internal NteServerSettings ServerSettings { get; } = new();
    public NteCNPresetConfig() : this(NteConfigProvider.CN) { }
    public NteCNPresetConfig(NteRegionConfig region)
    {
        Region = region;
        Appearance = new(region.IsOverseas ? Path.Combine(Path.GetDirectoryName(NteAppearanceStore.DefaultPath)!, region.StorageName, "appearance.json") : NteAppearanceStore.DefaultPath);
        LaunchSettings = new(NteLaunchSettings.GetPath(region.ProfileName));
        LoginSettings = new(region.IsTaiwan ? Path.Combine(Path.GetDirectoryName(Nte.Login.NteLoginPreferences.DefaultPath)!, "Taiwan", "login.json") : Nte.Login.NteLoginPreferences.GetPath(region.IsGlobal));
    }
    internal NteLoginSettings LoginSettings { get; }
    private IGameManager? _gameManager;
    private IGameInstaller? _gameInstaller;
    private ILauncherApiNews? _launcherApiNews;

    public override string GameName => NteResourceProvider.GetString("GameName");

    public override string GameExecutableName => Region.GameExecutableName;

    public override string GameAppDataPath => Region.GameAppDataPath;

    public override string GameLogFileName => Region.GameLogFileName;

    public override string GameVendorName => Region.GameVendorName;

    public override string GameRegistryKeyName => Region.GameRegistryKeyName;

    public override string ProfileName => Region.ProfileName;

    public override string ZoneDescription => NteResourceProvider.GetString(Region.IsTaiwan ? "TaiwanZoneDescription" : Region.IsGlobal ? "GlobalZoneDescription" : "ZoneDescription");

    public override string ZoneName => NteResourceProvider.GetString(Region.IsTaiwan ? "TaiwanZoneName" : Region.IsGlobal ? "GlobalZoneName" : "ZoneName");

    public override string ZoneFullName => NteResourceProvider.GetString(Region.IsTaiwan ? "TaiwanZoneFullName" : Region.IsGlobal ? "GlobalZoneFullName" : "ZoneFullName");

    public override string ZoneLogoUrl => Region.ZoneLogoUrl;

    public override string ZonePosterUrl => Region.ZonePosterUrl;

    public override string ZoneHomePageUrl => Region.ZoneHomePageUrl;

    public override GameReleaseChannel ReleaseChannel => Region.ReleaseChannel;

    public override string GameMainLanguage => Region.GameMainLanguage;

    public override string LauncherGameDirectoryName => Region.LauncherGameDirectoryName;

    public override List<string> SupportedLanguages => [.. Region.SupportedLanguages];

    public override ILauncherApiMedia? LauncherApiMedia
    {
        get => field ??= new NteCNLauncherApiMedia(
            Region.LauncherVersionIniUrl,
            Region.IsGlobal ? NteGlobalLocalization.GetPageUrl(SharedStatic.PluginLocaleCode) : Region.LauncherPageUrl,
            Appearance.Load, Region);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => _launcherApiNews ??= new NteCNLauncherApiNews(Region);
        set => _launcherApiNews = value;
    }

    public override IGameManager? GameManager
    {
        get => _gameManager ??= new NteGameManager(region: Region);
        set => _gameManager = value;
    }

    public override IGameInstaller? GameInstaller
    {
        get => _gameInstaller ??= new NteGameInstaller(GameManager ?? new NteGameManager(region: Region));
        set => _gameInstaller = value;
    }

    protected override Task<int> InitAsync(CancellationToken token)
    {
        _ = GameManager;
        _ = GameInstaller;
        _ = LauncherApiMedia;
        _ = LauncherApiNews;

        return Task.FromResult(0);
    }
}
