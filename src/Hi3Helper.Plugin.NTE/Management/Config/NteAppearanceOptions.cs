using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace Hi3Helper.Plugin.NTE.Management.Config;

internal sealed class NteAppearanceOptions
{
    public bool DynamicBackground { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;
    public List<NteOverlayElement> Elements { get; set; } =
    [
        new() { Id = "slogan", Source = "slogan", X = 160, Y = 385, Width = 438, Height = 126 },
        new() { Id = "logo", Source = "logo", Enabled = false, X = 160, Y = 100, Width = 240, Height = 100 }
    ];

    internal void Validate()
    {
        if (CanvasWidth is < 64 or > 4096 || CanvasHeight is < 64 or > 4096 || Elements == null || Elements.Count > 32)
            throw new InvalidDataException("Invalid overlay canvas size or element count.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (NteOverlayElement element in Elements)
        {
            if (element == null || string.IsNullOrWhiteSpace(element.Id) || !ids.Add(element.Id) ||
                element.Id.Contains('.', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(element.Source) ||
                !double.IsFinite(element.X) || !double.IsFinite(element.Y) ||
                Math.Abs(element.X) > 8192 || Math.Abs(element.Y) > 8192 ||
                !double.IsFinite(element.Width) || !double.IsFinite(element.Height) ||
                element.Width is <= 0 or > 8192 || element.Height is <= 0 or > 8192 ||
                !double.IsFinite(element.Opacity) || element.Opacity is < 0 or > 1)
                throw new InvalidDataException("Invalid decorative overlay element.");
            if (element.Source is not ("slogan" or "logo") &&
                (!Uri.TryCreate(element.Source, UriKind.Absolute, out Uri? uri) ||
                    uri.Scheme is not ("http" or "https" or "file")))
                throw new InvalidDataException("Decorative image source must be an HTTP URL or an absolute local file.");
        }
    }
}

internal sealed class NteOverlayElement
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Opacity { get; set; } = 1;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(NteAppearanceOptions))]
internal partial class NteAppearanceJsonContext : JsonSerializerContext;
