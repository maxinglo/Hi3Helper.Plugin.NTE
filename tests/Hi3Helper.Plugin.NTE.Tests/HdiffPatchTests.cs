using System.Text;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class HdiffPatchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteHdiffTests", Guid.NewGuid().ToString("N"));
    public HdiffPatchTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Decoder_AppliesTheFixtureGeneratedAndVerifiedByUpstreamHdiffzAndHpatchz()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hdiff");
        byte[] old = File.ReadAllBytes(Path.Combine(fixture, "upstream.old"));
        NteHdiffPatch.Apply(Path.Combine(fixture, "upstream.old"), Path.Combine(fixture, "upstream.hdiff"),
            Path.Combine(root, "upstream-output"), 32768, TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllBytes(Path.Combine(fixture, "upstream.new")), File.ReadAllBytes(Path.Combine(root, "upstream-output")));
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(fixture, "upstream.old")));
    }

    [Fact]
    public void Decoder_HandlesAllFourRleKindsAndRunsAcrossBufferBoundaries()
    {
        byte[] old = Enumerable.Repeat((byte)42, 2_500_000).ToArray();
        byte[] next = old.ToArray();
        for (int i = 0; i < next.Length; i++) next[i] = unchecked((byte)(old[i] +
            (i < 1_100_000 ? 0 : i < 1_200_000 ? 255 : i < 1_400_000 ? 17 : (i % 2 + 1))));
        Check(old, next, [(0, 0, next.Length)]);
    }

    [Fact]
    public void Decoder_HandlesLiteralGapsAndBackwardSourceCovers()
    {
        byte[] old = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        byte[] next = Enumerable.Repeat((byte)99, 100).ToArray();
        old.AsSpan(120, 20).CopyTo(next.AsSpan(10));
        old.AsSpan(2, 16).CopyTo(next.AsSpan(40));
        next[41]++;
        Check(old, next, [(120, 10, 20), (2, 40, 16)]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    public void Decoder_HandlesEmptySourcesAndEntirelyLiteralFiles(int size)
        => Check([], Enumerable.Repeat((byte)71, size).ToArray(), []);

    [Theory]
    [InlineData("truncate")]
    [InlineData("trailing")]
    [InlineData("target-size")]
    public void Decoder_RejectsInvalidSectionsWithoutChangingTheSource(string corruption)
    {
        byte[] old = "original"u8.ToArray(), next = "modified"u8.ToArray();
        byte[] patch = HdiffFixtures.Create(old, next, [(0, 0, 8)]);
        if (corruption == "truncate") patch = patch[..^1];
        if (corruption == "trailing") patch = [.. patch, 0];
        File.WriteAllBytes(Path.Combine(root, "old"), old);
        File.WriteAllBytes(Path.Combine(root, "patch"), patch);
        Assert.Throws<InvalidDataException>(() => NteHdiffPatch.Apply(Path.Combine(root, "old"), Path.Combine(root, "patch"),
            Path.Combine(root, "output"), corruption == "target-size" ? 7 : 8, TestContext.Current.CancellationToken));
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(root, "old")));
    }

    [Fact]
    public void Decoder_RejectsUnsupportedCompression()
    {
        File.WriteAllBytes(Path.Combine(root, "old"), []);
        File.WriteAllBytes(Path.Combine(root, "patch"), "HDIFF13&zlib\0"u8.ToArray());
        Assert.Throws<NotSupportedException>(() => NteHdiffPatch.Apply(Path.Combine(root, "old"), Path.Combine(root, "patch"),
            Path.Combine(root, "output"), 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Decoder_CanBeCancelledDuringLargeOutputAndNeverWritesTheSource()
    {
        byte[] old = new byte[3_000_000], next = Enumerable.Repeat((byte)1, old.Length).ToArray();
        File.WriteAllBytes(Path.Combine(root, "old"), old);
        File.WriteAllBytes(Path.Combine(root, "patch"), HdiffFixtures.Create(old, next, [(0, 0, old.Length)]));
        using CancellationTokenSource cancel = new();
        Assert.Throws<OperationCanceledException>(() => NteHdiffPatch.Apply(Path.Combine(root, "old"), Path.Combine(root, "patch"),
            Path.Combine(root, "output"), next.Length, cancel.Token, (_, _) => cancel.Cancel()));
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(root, "old")));
    }

    private void Check(byte[] old, byte[] next, (long Old, long New, long Length)[] covers)
    {
        File.WriteAllBytes(Path.Combine(root, "old"), old);
        File.WriteAllBytes(Path.Combine(root, "patch"), HdiffFixtures.Create(old, next, covers));
        NteHdiffPatch.Apply(Path.Combine(root, "old"), Path.Combine(root, "patch"), Path.Combine(root, "output"),
            next.Length, TestContext.Current.CancellationToken);
        Assert.Equal(next, File.ReadAllBytes(Path.Combine(root, "output")));
        Assert.Equal(old, File.ReadAllBytes(Path.Combine(root, "old")));
    }
    public void Dispose() => Directory.Delete(root, true);
}

internal static class HdiffFixtures
{
    internal static byte[] Create(byte[] old, byte[] next, (long Old, long New, long Length)[] covers)
    {
        List<byte> positions = [], controls = [], codes = [], literals = [];
        byte[] delta = new byte[next.Length];
        long oldEnd = 0, newEnd = 0;
        foreach (var cover in covers)
        {
            long distance = cover.Old - oldEnd;
            UInt(positions, Math.Abs(distance), 1, distance < 0 ? 1 : 0);
            UInt(positions, cover.New - newEnd);
            UInt(positions, cover.Length);
            literals.AddRange(next.AsSpan((int)newEnd, (int)(cover.New - newEnd)).ToArray());
            for (int i = 0; i < cover.Length; i++) delta[(int)cover.New + i] = unchecked((byte)(next[(int)cover.New + i] - old[(int)cover.Old + i]));
            oldEnd = cover.Old + cover.Length;
            newEnd = cover.New + cover.Length;
        }
        literals.AddRange(next.AsSpan((int)newEnd).ToArray());
        int position = 0;
        while (position < delta.Length)
        {
            int start = position;
            byte value = delta[position++];
            while (position < delta.Length && delta[position] == value) position++;
            if (position - start > 1 || value is 0 or 255)
            {
                UInt(controls, position - start - 1, 2, value == 0 ? 0 : value == 255 ? 1 : 2);
                if (value is not (0 or 255)) codes.Add(value);
            }
            else
            {
                while (position < delta.Length && delta[position] is not (0 or 255) &&
                    (position + 1 == delta.Length || delta[position] != delta[position + 1])) position++;
                UInt(controls, position - start - 1, 2, 3);
                codes.AddRange(delta.AsSpan(start, position - start).ToArray());
            }
        }
        List<byte> result = [.. Encoding.ASCII.GetBytes("HDIFF13&\0")];
        UInt(result, next.Length); UInt(result, old.Length); UInt(result, covers.Length);
        foreach (var section in new[] { positions, controls, codes, literals }) { UInt(result, section.Count); UInt(result, 0); }
        result.AddRange(positions); result.AddRange(controls); result.AddRange(codes); result.AddRange(literals);
        return [.. result];
    }

    private static void UInt(List<byte> output, long value, int tagBits = 0, int tag = 0)
    {
        List<byte> low = [];
        while (value > (1 << (7 - tagBits)) - 1) { low.Add((byte)(value & 127)); value >>= 7; }
        output.Add((byte)((int)value | (tag << (8 - tagBits)) | (low.Count == 0 ? 0 : 1 << (7 - tagBits))));
        for (int i = low.Count - 1; i >= 0; i--) output.Add((byte)(low[i] | (i == 0 ? 0 : 128)));
    }
}
