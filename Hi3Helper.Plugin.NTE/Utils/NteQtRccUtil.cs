using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Utils;

internal static class NteQtRccUtil
{

    private const ushort ExQtRccDirectoryFlag = 0x2;
    private const ushort ExQtRccCompressedFlag = 0x1;

    public static async Task<int> BuildImageSequenceZipFromRccPayloadAsync(string payloadFilePath, Stream zipOutputStream, CancellationToken token)
    {
        await using FileStream payloadStream = new(payloadFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        List<RccFileEntry> frameEntries = await ExtractJpegEntriesFromQtRccAsync(payloadStream, token);
        if (frameEntries.Count == 0)
        {
            return 0;
        }

        using ZipArchive archive = new(zipOutputStream, ZipArchiveMode.Create, leaveOpen: true);
        int frameIndex = 0;

        foreach (RccFileEntry frameEntry in frameEntries)
        {
            ZipArchiveEntry entry = archive.CreateEntry($"frame_{frameIndex:D4}.jpg", CompressionLevel.NoCompression);
            await using Stream entryStream = entry.Open();
            await CopyRangeAsync(payloadStream, entryStream, frameEntry.DataStartOffset, frameEntry.DataLength, token);
            frameIndex++;
        }

        return frameIndex;
    }

    private static async Task<List<RccFileEntry>> ExtractJpegEntriesFromQtRccAsync(Stream stream, CancellationToken token)
    {
        QtRccHeader header = await ReadQtRccHeaderAsync(stream, token);
        long treeEnd = stream.Length;
        if (header.DataOffset > header.TreeOffset)
        {
            treeEnd = Math.Min(treeEnd, header.DataOffset);
        }
        if (header.NameOffset > header.TreeOffset)
        {
            treeEnd = Math.Min(treeEnd, header.NameOffset);
        }

        if (treeEnd <= header.TreeOffset)
        {
            throw new InvalidDataException("Qt RCC tree section is invalid.");
        }

        int nodeSize = header.Version >= 3 ? 22 : 14;
        int nodeCount = (int)((treeEnd - header.TreeOffset) / nodeSize);
        if (nodeCount <= 0)
        {
            throw new InvalidDataException("Qt RCC tree section does not contain any nodes.");
        }

        List<RccFileEntry> entries = [];
        Stack<(int NodeIndex, string ParentPath)> stack = new();
        stack.Push((0, string.Empty));

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            (int nodeIndex, string parentPath) = stack.Pop();
            QtRccNode node = await ReadQtRccNodeAsync(stream, header, nodeIndex, token);
            string nodeName = await ReadQtRccNameAsync(stream, header, node.NameOffset, token);
            string currentPath = string.IsNullOrEmpty(parentPath)
                ? nodeName
                : $"{parentPath}/{nodeName}";

            if (node.IsDirectory)
            {
                for (int i = node.ChildCount - 1; i >= 0; i--)
                {
                    int childIndex = node.FirstChildIndex + i;
                    if ((uint)childIndex >= (uint)nodeCount)
                    {
                        throw new InvalidDataException("Qt RCC child node index is outside tree range.");
                    }

                    stack.Push((childIndex, currentPath));
                }

                continue;
            }

            if ((node.Flags & ExQtRccCompressedFlag) != 0)
            {
                continue;
            }

            string lowerPath = currentPath.ToLowerInvariant();
            if (!lowerPath.EndsWith(".jpg", StringComparison.Ordinal) &&
                !lowerPath.EndsWith(".jpeg", StringComparison.Ordinal) &&
                !lowerPath.EndsWith(".jfif", StringComparison.Ordinal))
            {
                continue;
            }

            RccFileEntry fileEntry = await ReadQtRccFileEntryAsync(stream, header, node.DataOffset, token);
            entries.Add(fileEntry);
        }

        return entries;
    }

    private static async Task<QtRccHeader> ReadQtRccHeaderAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[20];

        stream.Seek(0, SeekOrigin.Begin);
        await ReadExactAsync(stream, header, token);

        if (header[0] != (byte)'q' || header[1] != (byte)'r' || header[2] != (byte)'e' || header[3] != (byte)'s')
        {
            throw new InvalidDataException("yh.dat payload is not a valid Qt RCC file (missing qres header).");
        }

        uint version = ReadUInt32BigEndian(header.AsSpan(4, 4));
        long treeOffset = ReadUInt32BigEndian(header.AsSpan(8, 4));
        long dataOffset = ReadUInt32BigEndian(header.AsSpan(12, 4));
        long nameOffset = ReadUInt32BigEndian(header.AsSpan(16, 4));

        if (version is < 1 or > 3)
        {
            throw new InvalidDataException($"Qt RCC version '{version}' is not supported.");
        }

        if (treeOffset >= stream.Length || dataOffset >= stream.Length || nameOffset >= stream.Length)
        {
            throw new InvalidDataException("Qt RCC header offsets are outside payload bounds.");
        }

        return new QtRccHeader(version, treeOffset, dataOffset, nameOffset);
    }

    private static async Task<QtRccNode> ReadQtRccNodeAsync(Stream stream, QtRccHeader header, int nodeIndex, CancellationToken token)
    {
        int nodeSize = header.Version >= 3 ? 22 : 14;
        long offset = header.TreeOffset + (long)nodeIndex * nodeSize;
        byte[] raw = new byte[nodeSize];

        stream.Seek(offset, SeekOrigin.Begin);
        await ReadExactAsync(stream, raw, token);

        uint nameOffset = ReadUInt32BigEndian(raw.AsSpan(0, 4));
        ushort flags = ReadUInt16BigEndian(raw.AsSpan(4, 2));
        uint payloadA = ReadUInt32BigEndian(raw.AsSpan(6, 4));
        uint payloadB = ReadUInt32BigEndian(raw.AsSpan(10, 4));

        bool isDirectory = (flags & ExQtRccDirectoryFlag) != 0;
        if (isDirectory)
        {
            return new QtRccNode(nameOffset, flags, true, (int)payloadA, (int)payloadB, 0);
        }

        return new QtRccNode(nameOffset, flags, false, 0, 0, payloadB);
    }

    private static async Task<string> ReadQtRccNameAsync(Stream stream, QtRccHeader header, uint relativeNameOffset, CancellationToken token)
    {
        long offset = header.NameOffset + relativeNameOffset;
        if (offset < 0 || offset + 6 > stream.Length)
        {
            throw new InvalidDataException("Qt RCC name offset is outside payload bounds.");
        }

        byte[] prefix = new byte[6];
        stream.Seek(offset, SeekOrigin.Begin);
        await ReadExactAsync(stream, prefix, token);

        int nameLength = ReadUInt16BigEndian(prefix.AsSpan(0, 2));
        byte[] nameData = new byte[nameLength * 2];
        await ReadExactAsync(stream, nameData, token);

        char[] chars = new char[nameLength];
        for (int i = 0; i < nameLength; i++)
        {
            chars[i] = (char)ReadUInt16BigEndian(nameData.AsSpan(i * 2, 2));
        }

        return new string(chars);
    }

    private static async Task<RccFileEntry> ReadQtRccFileEntryAsync(Stream stream, QtRccHeader header, uint relativeDataOffset, CancellationToken token)
    {
        long offset = header.DataOffset + relativeDataOffset;
        if (offset < 0 || offset + 4 > stream.Length)
        {
            throw new InvalidDataException("Qt RCC data offset is outside payload bounds.");
        }

        byte[] lengthRaw = new byte[4];
        stream.Seek(offset, SeekOrigin.Begin);
        await ReadExactAsync(stream, lengthRaw, token);

        long dataLength = ReadUInt32BigEndian(lengthRaw);
        long dataStart = offset + 4;
        if (dataLength <= 0 || dataStart + dataLength > stream.Length)
        {
            throw new InvalidDataException("Qt RCC file entry has invalid data length.");
        }

        return new RccFileEntry(dataStart, dataLength);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int readOffset = 0;
        while (readOffset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(readOffset, buffer.Length - readOffset), token);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading Qt RCC payload.");
            }

            readOffset += read;
        }
    }

    private static ushort ReadUInt16BigEndian(ReadOnlySpan<byte> data)
        => (ushort)((data[0] << 8) | data[1]);

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data)
        => ((uint)data[0] << 24) |
           ((uint)data[1] << 16) |
           ((uint)data[2] << 8) |
           data[3];

    private static uint ReadUInt32BigEndian(byte[] data)
        => ReadUInt32BigEndian(data.AsSpan());

    private static async Task CopyRangeAsync(Stream source,
        Stream destination,
        long startOffset,
        long length,
        CancellationToken token)
    {
        const int bufferSize = 65536;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            source.Seek(startOffset, SeekOrigin.Begin);

            long remaining = length;
            while (remaining > 0)
            {
                int readSize = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, readSize), token);
                if (read == 0)
                {
                    throw new EndOfStreamException("Unexpected end of RCC payload while copying JPEG frame.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private readonly record struct QtRccHeader(uint Version, long TreeOffset, long DataOffset, long NameOffset);

    private readonly record struct QtRccNode(uint NameOffset,
        ushort Flags,
        bool IsDirectory,
        int ChildCount,
        int FirstChildIndex,
        uint DataOffset);

    private readonly record struct RccFileEntry(long DataStartOffset, long DataLength);
}

