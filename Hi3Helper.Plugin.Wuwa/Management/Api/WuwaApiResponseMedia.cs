using System.Text.Json.Serialization;
// ReSharper disable IdentifierTypo

namespace Hi3Helper.Plugin.Wuwa.Management.Api;

public class WuwaApiResponseMedia
{
    [JsonPropertyName("backgroundFile")] // Mapping: root -> backgroundFile
    public string? BackgroundImageUrl { get; set; }

    /// <summary>URL of the first-frame thumbnail for the background video.</summary>
    [JsonPropertyName("firstFrameImage")] // Mapping: root -> firstFrameImage
    public string? FirstFrameImageUrl { get; set; }

    /// <summary>URL of the slogan/watermark image displayed over the background.</summary>
    [JsonPropertyName("slogan")] // Mapping: root -> slogan
    public string? SloganUrl { get; set; }

    /// <summary>Background media type: 2 = video, otherwise image.</summary>
    [JsonPropertyName("backgroundFileType")] // Mapping: root -> backgroundFileType
    public int BackgroundFileType { get; set; }
}

