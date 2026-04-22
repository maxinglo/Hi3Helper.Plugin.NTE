using Hi3Helper.Plugin.Core.Management;
using System.Collections.Generic;

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
);

