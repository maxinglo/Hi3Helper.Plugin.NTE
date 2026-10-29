using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.NTE.Management.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal sealed class NteOverlayComposer(NteLauncherResourceClient resources, NteMediaCache cache)
{
    internal async Task<string?> PrepareAsync(NteAppearanceOptions options, string html, string pageUrl, CancellationToken token,
        IReadOnlyDictionary<string, byte[]>? launcherImages = null)
    {
        options.Validate();
        if (!options.OverlayEnabled)
            return null;
        List<(NteOverlayElement Element, NtePngImage Image)> images = [];
        StringBuilder identity = new("overlay-v1:");
        identity.Append(JsonSerializer.Serialize(options, NteAppearanceJsonContext.Default.NteAppearanceOptions));
        foreach (NteOverlayElement element in options.Elements)
        {
            if (!element.Enabled || element.Opacity == 0)
                continue;
            string source = element.Source is "slogan" or "logo"
                ? NteLauncherHtmlAssets.FindImageUrl(html, element.Source, pageUrl) : element.Source;
            byte[]? nativeImage = null;
            launcherImages?.TryGetValue(element.Source, out nativeImage);
            if (string.IsNullOrEmpty(source) && nativeImage == null)
                continue;
            try
            {
                byte[] bytes = nativeImage ?? await resources.ReadBytesAsync(source, token).ConfigureAwait(false);
                NtePngImage image = await Task.Run(() => NtePngImage.Load(bytes), token).ConfigureAwait(false);
                images.Add((element, image));
                identity.Append(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UriFormatException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "Cannot prepare decorative image {Id}.", element.Id);
            }
        }
        if (images.Count == 0)
            return null;

        return await cache.GetOrCreateGeneratedAsync(identity.ToString(), ".png", (output, cancellation) =>
            Task.Run(() =>
            {
                NtePngImage canvas = NtePngImage.Create(options.CanvasWidth, options.CanvasHeight);
                foreach ((NteOverlayElement element, NtePngImage image) in images)
                    canvas.Draw(image, element.X, element.Y, element.Width, element.Height, element.Opacity, cancellation);
                cancellation.ThrowIfCancellationRequested();
                canvas.Write(output);
            }, cancellation), token).ConfigureAwait(false);
    }
}
