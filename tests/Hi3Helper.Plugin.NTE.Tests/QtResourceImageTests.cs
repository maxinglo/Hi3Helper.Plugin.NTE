using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class QtResourceImageTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ReadsOnlyTheNamedPublicPng(bool compressed, bool namedRoot)
    {
        byte[] png = TestImages.Encode(TestImages.Solid(3, 2, [255, 128, 0, 255]));
        byte[] archive = QtResourceFixture.Create(png, compressed, namedRoot);
        Assert.Equal(png, NteQtResourceImage.ReadPng(archive, "res/res/1289/main/slogan.png"));
        Assert.Throws<InvalidDataException>(() => NteQtResourceImage.ReadPng(archive, "res/res/1289/main/missing.png"));
        Assert.Throws<InvalidDataException>(() => NteQtResourceImage.ReadPng(archive, "res/res/1289/main"));
    }

    [Fact]
    public void RejectsTruncatedArchivesAndNonPngPayloads()
    {
        Assert.Throws<InvalidDataException>(() => NteQtResourceImage.ReadPng("qres"u8.ToArray(), "res"));
        byte[] archive = QtResourceFixture.Create("not-an-image"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => NteQtResourceImage.ReadPng(archive, "res/res/1289/main/slogan.png"));
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(8), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => NteQtResourceImage.ReadPng(archive, "res"));
    }

    [Fact]
    public void RejectsOversizedExpandedPayloadBeforeAllocatingIt()
    {
        byte[] archive = QtResourceFixture.Create(new byte[1000], true);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(28), 128 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => NteQtResourceImage.ReadPng(archive, "res/res/1289/main/slogan.png"));
    }
}

internal static class QtResourceFixture
{
    internal static byte[] Create(byte[] content, bool compressed = false, bool namedRoot = false)
    {
        string[] names = namedRoot ? ["res", "res", "1289", "main", "slogan.png"] : ["", "res", "res", "1289", "main", "slogan.png"];
        byte[] payload = content;
        if (compressed)
        {
            using MemoryStream packed = new();
            byte[] length = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)content.Length); packed.Write(length);
            using (ZLibStream compressor = new(packed, CompressionLevel.Optimal, true)) compressor.Write(content);
            payload = packed.ToArray();
        }
        using MemoryStream nameTable = new();
        List<int> offsets = [];
        foreach (string name in names)
        {
            offsets.Add((int)nameTable.Length); byte[] header = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)name.Length);
            nameTable.Write(header); nameTable.Write(Encoding.BigEndianUnicode.GetBytes(name));
        }
        int data = 24, namesOffset = data + 4 + payload.Length, tree = namesOffset + (int)nameTable.Length;
        byte[] archive = new byte[tree + names.Length * 22]; "qres"u8.CopyTo(archive);
        void U32(int at, int value) => BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(at), (uint)value);
        U32(4, 3); U32(8, tree); U32(12, data); U32(16, namesOffset);
        U32(data, payload.Length); payload.CopyTo(archive, data + 4); nameTable.ToArray().CopyTo(archive, namesOffset);
        for (int i = 0; i < names.Length; i++)
        {
            int at = tree + i * 22; U32(at, offsets[i]);
            bool directory = i < names.Length - 1;
            BinaryPrimitives.WriteUInt16BigEndian(archive.AsSpan(at + 4), (ushort)(directory ? 2 : compressed ? 1 : 0));
            if (directory) { U32(at + 6, 1); U32(at + 10, i + 1); }
        }
        return archive;
    }
}
