using Nte.LoginHost;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class CachedBackdropPixelsTests
{
    [Theory]
    [InlineData(1, 1, 3)]
    [InlineData(3, 2, 5)]
    [InlineData(9, 7, 2)]
    public void CachedBlurMatchesClampedReferenceAndIsOpaque(int width, int height, int radius)
    {
        byte[] source = new byte[width * height * 4]; new Random(812).NextBytes(source);
        byte[] actual = source.ToArray(), expected = source.ToArray();
        for (int pass = 0; pass < 3; pass++) { expected = SlowBox(expected, width, height, radius, true); expected = SlowBox(expected, width, height, radius, false); }
        CachedBackdropPixels.BlurAndGrain(actual, width, height, radius);
        // Noise is at most two levels and is the same for all three channels.
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            for (int channel = 0; channel < 3; channel++) Assert.InRange((int)actual[4 * pixel + channel] - expected[4 * pixel + channel], -2, 2);
            Assert.Equal((byte)255, actual[4 * pixel + 3]);
        }
    }

    [Fact]
    public void AUniformSceneKeepsItsColorAndBlurHasNoTransparentBorder()
    {
        byte[] source = Enumerable.Repeat(new byte[] { 40, 80, 120, 0 }, 80).SelectMany(x => x).ToArray();
        CachedBackdropPixels.BlurAndGrain(source, 10, 8, 24);
        for (int pixel = 0; pixel < 80; pixel++)
        {
            Assert.InRange(source[4 * pixel], (byte)38, (byte)42); Assert.InRange(source[4 * pixel + 1], (byte)78, (byte)82);
            Assert.InRange(source[4 * pixel + 2], (byte)118, (byte)122); Assert.Equal((byte)255, source[4 * pixel + 3]);
        }
    }

    private static byte[] SlowBox(byte[] source, int width, int height, int radius, bool horizontal)
    {
        byte[] output = new byte[source.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) for (int channel = 0; channel < 3; channel++)
        {
            int sum = 0;
            for (int delta = -radius; delta <= radius; delta++)
                sum += source[4 * (Math.Clamp(horizontal ? y : y + delta, 0, height - 1) * width + Math.Clamp(horizontal ? x + delta : x, 0, width - 1)) + channel];
            output[4 * (y * width + x) + channel] = (byte)((sum + radius) / (2 * radius + 1));
        }
        return output;
    }
}
