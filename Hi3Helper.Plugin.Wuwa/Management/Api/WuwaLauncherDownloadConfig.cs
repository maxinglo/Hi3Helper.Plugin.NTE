using System.Text.Json.Serialization;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

/// <summary>
/// Represents Kuro's launcher download config file (<c>launcherDownloadConfig.json</c>),
/// which the official launcher uses to track the installed game version.
/// </summary>
public class WuwaLauncherDownloadConfig
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("reUseVersion")]
    public string? ReUseVersion { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("isPreDownload")]
    public bool IsPreDownload { get; set; }

    [JsonPropertyName("appId")]
    public string? AppId { get; set; }
}
