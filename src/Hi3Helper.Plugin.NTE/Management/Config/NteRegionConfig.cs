using Hi3Helper.Plugin.Core.Management;
using System.Collections.Generic;
using Nte.Login;

namespace Hi3Helper.Plugin.NTE.Management.Config;

public sealed record NteRegionConfig(
    string ProfileName,
    string ZoneName,
    string ZoneLogoUrl,
    string ZonePosterUrl,
    string ZoneHomePageUrl,
    string GameExecutableName,
    string LauncherGameDirectoryName,
    string GameAppDataPath,
    string GameLogFileName,
    string GameVendorName,
    string GameRegistryKeyName,
    string GameMainLanguage,
    GameReleaseChannel ReleaseChannel,
    IReadOnlyList<string> SupportedLanguages
)
{
    public string AppId { get; init; } = NteRegionDefinition.CnAppId;
    public string LauncherVersionIniUrl { get; init; } = NteConfigProvider.LauncherVersionIniUrl;
    public string LauncherDirectoryName { get; init; } = NteRegionDefinition.CnLauncherDirectory;
    public string DefaultBranch { get; init; } = NteConfigProvider.DefaultBranchName;
    public string[] ResourceUrls { get; init; } = NteConfigProvider.GameResBaseUrls;
    public bool IsGlobal => AppId == NteRegionDefinition.GlobalAppId;
    public bool IsTaiwan => AppId == NteRegionDefinition.TaiwanAppId;
    public bool IsOverseas => IsGlobal || IsTaiwan;
    public string LauncherPageUrl { get; init; } = NteConfigProvider.LauncherHtmlUrl;
    public string StorageName => IsGlobal ? "Global" : IsTaiwan ? "Taiwan" : "";
    public string SdkDirectory => LauncherDirectoryName + "/" + NteRegionDefinition.PatcherSdkRelativePath;
}

