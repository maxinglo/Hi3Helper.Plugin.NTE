using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal sealed class NteSocialIconProcessor(NteLauncherResourceClient resources, NteMediaCache cache)
{
    internal async Task<NteSocialMediaAsset> PrepareAsync(NteSocialMediaSource source, CancellationToken token)
    {
        byte[] bytes = await resources.ReadBytesAsync(source.IconUrl, token).ConfigureAwait(false);
        NtePngImage image = await Task.Run(() => NtePngImage.Load(bytes), token).ConfigureAwait(false);
        int frameHeight = Math.Max(1, (int)Math.Round(image.Width * source.FrameAspectRatio));
        bool hasHoverSprite = image.Height >= frameHeight * 2;
        NtePngImage normal = hasHoverSprite ? image.Crop(0, 0, image.Width, frameHeight) : image;
        NtePngImage hover = hasHoverSprite
            ? image.Crop(0, image.Height - frameHeight, image.Width, frameHeight)
            : normal;
        byte[]? hoverBytes = source.HoverIconUrl is { Length: > 0 }
            ? await resources.ReadBytesAsync(source.HoverIconUrl, token).ConfigureAwait(false)
            : null;
        if (hoverBytes != null) hover = await Task.Run(() => NtePngImage.Load(hoverBytes), token).ConfigureAwait(false);
        string key = "social-v2:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ":" + frameHeight + ":" +
                     (hoverBytes == null ? "sprite" : Convert.ToHexStringLower(SHA256.HashData(hoverBytes)));
        string normalPath = await cache.GetOrCreateGeneratedAsync(key + ":normal", ".png",
            (output, cancellation) => Task.Run(() => normal.Write(output), cancellation), token).ConfigureAwait(false);
        string hoverPath = hasHoverSprite || hoverBytes != null
            ? await cache.GetOrCreateGeneratedAsync(key + ":hover", ".png",
                    (output, cancellation) => Task.Run(() => hover.Write(output), cancellation), token)
                .ConfigureAwait(false)
            : normalPath;
        return new NteSocialMediaAsset(source.Description, source.ClickUrl, normalPath, hoverPath, source.QrUrl);
    }
}
