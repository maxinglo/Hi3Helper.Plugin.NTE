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

namespace Hi3Helper.Plugin.NTE.Management.PresetConfig;

[GeneratedComClass]
public partial class NteCNPresetConfig : PluginPresetConfigBase
{
    private static readonly NteRegionConfig Region = NteConfigProvider.CN;
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

    public override string ZoneDescription => NteResourceProvider.GetString("ZoneDescription");

    public override string ZoneName => Region.ZoneName;

    public override string ZoneFullName => NteResourceProvider.GetString("ZoneFullName");

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
            NteConfigProvider.LauncherVersionIniUrl,
            NteConfigProvider.LauncherHtmlUrl);
        set;
    }

    public override ILauncherApiNews? LauncherApiNews
    {
        get => _launcherApiNews ??= new NteCNLauncherApiNews();
        set => _launcherApiNews = value;
    }

    public override IGameManager? GameManager
    {
        get => _gameManager ??= new NteCNGameManager();
        set => _gameManager = value;
    }

    public override IGameInstaller? GameInstaller
    {
        get => _gameInstaller ??= new NteCNGameInstaller(GameManager ?? new NteCNGameManager());
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
