using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Wuwa.Utils;
using System.Text.Json.Serialization;
// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseGameConfigRef
{
    [JsonPropertyName("indexFile")] // Mapping: root -> default -> config -> indexFile
    public string? IndexFile { get; set; }

    [JsonPropertyName("version")] // Mapping: root -> default -> config -> version
    [JsonConverter(typeof(GameVersionJsonConverter))]
    public GameVersion CurrentVersion { get; set; }

    [JsonPropertyName("patchType")] // Mapping: root -> default -> config -> patchType
    public string? PatchType { get; set; }

    [JsonPropertyName("size")] // Mapping: root -> default -> config -> size
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ulong? PatchFileSize { get; set; }

    [JsonPropertyName("baseUrl")] // Mapping: root -> default -> config -> baseUrl
    public string? BaseUrl { get; set; }

    [JsonPropertyName("patchConfig")] // Mapping: root -> default -> config -> patchConfig
	public WuwaApiResponseGameConfigRef[]? PatchConfig
    { get; set; }
}