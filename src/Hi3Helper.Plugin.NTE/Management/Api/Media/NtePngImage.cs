using StbImageSharp;
using System;
using System.IO;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal sealed class NtePngImage(int width, int height, byte[] pixels)
{
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal byte[] Pixels { get; } = pixels;

    internal static NtePngImage Create(int width, int height)
    {
        ValidateSize(width, height);
        return new NtePngImage(width, height, new byte[checked(width * height * 4)]);
    }

    internal static NtePngImage Load(byte[] data)
    {
        try
        {
            using MemoryStream input = new(data, writable: false);
            ImageInfo info = ImageInfo.FromStream(input) ?? throw new InvalidDataException("Unsupported image.");
            ValidateSize(info.Width, info.Height);
            ImageResult image = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);
            return new NtePngImage(image.Width, image.Height, image.Data);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException("Cannot decode launcher image.", ex);
        }
    }

    internal NtePngImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > Width || y + height > Height)
            throw new InvalidDataException("Sprite rectangle is outside the source image.");
        NtePngImage result = Create(width, height);
        for (int row = 0; row < height; row++)
            Pixels.AsSpan(((y + row) * Width + x) * 4, width * 4).CopyTo(result.Pixels.AsSpan(row * width * 4));
        return result;
    }

    internal void Draw(NtePngImage image, double x, double y, double width, double height, double opacity,
        CancellationToken token)
    {
        // Preserve the source aspect ratio inside the configured rectangle.
        double scale = Math.Min(width / image.Width, height / image.Height);
        double drawWidth = image.Width * scale;
        double drawHeight = image.Height * scale;
        x += (width - drawWidth) / 2;
        y += (height - drawHeight) / 2;
        int left = Math.Max(0, (int)Math.Floor(x));
        int top = Math.Max(0, (int)Math.Floor(y));
        int right = Math.Min(Width, (int)Math.Ceiling(x + drawWidth));
        int bottom = Math.Min(Height, (int)Math.Ceiling(y + drawHeight));
        for (int row = top; row < bottom; row++)
        {
            token.ThrowIfCancellationRequested();
            for (int column = left; column < right; column++)
            {
                double sx = Math.Clamp((column + 0.5 - x) / scale - 0.5, 0, image.Width - 1);
                double sy = Math.Clamp((row + 0.5 - y) / scale - 0.5, 0, image.Height - 1);
                int x0 = (int)sx;
                int y0 = (int)sy;
                int x1 = Math.Min(x0 + 1, image.Width - 1);
                int y1 = Math.Min(y0 + 1, image.Height - 1);
                double dx = sx - x0;
                double dy = sy - y0;
                int a = (y0 * image.Width + x0) * 4;
                int b = (y0 * image.Width + x1) * 4;
                int c = (y1 * image.Width + x0) * 4;
                int d = (y1 * image.Width + x1) * 4;
                double wa = (1 - dx) * (1 - dy) * image.Pixels[a + 3] / 255d;
                double wb = dx * (1 - dy) * image.Pixels[b + 3] / 255d;
                double wc = (1 - dx) * dy * image.Pixels[c + 3] / 255d;
                double wd = dx * dy * image.Pixels[d + 3] / 255d;
                double sourceAlpha = wa + wb + wc + wd;
                if (sourceAlpha <= 0)
                    continue;
                int target = (row * Width + column) * 4;
                double alpha = sourceAlpha * opacity;
                double destinationAlpha = Pixels[target + 3] / 255d;
                double combinedAlpha = alpha + destinationAlpha * (1 - alpha);
                if (combinedAlpha <= 0)
                    continue;
                for (int channel = 0; channel < 3; channel++)
                {
                    double source = (image.Pixels[a + channel] * wa + image.Pixels[b + channel] * wb +
                        image.Pixels[c + channel] * wc + image.Pixels[d + channel] * wd) / sourceAlpha;
                    Pixels[target + channel] = (byte)Math.Clamp(Math.Round(
                        (source * alpha + Pixels[target + channel] * destinationAlpha * (1 - alpha)) / combinedAlpha), 0, 255);
                }
                Pixels[target + 3] = (byte)Math.Clamp(Math.Round(combinedAlpha * 255), 0, 255);
            }
        }
    }

    internal void Write(Stream output)
        => new StbImageWriteSharp.ImageWriter().WritePng(Pixels, Width, Height,
            StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);

    private static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 16_777_216)
            throw new InvalidDataException("Launcher image exceeds the supported dimensions.");
    }
}
