using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

// Reads one public PNG by its exact RCC path. It does not decode SDK config,
// enumerate credentials, load Qt, or execute any official component.
internal static class NteQtResourceImage
{
    internal static byte[] ReadPng(byte[] archive, string resourcePath)
    {
        const int limit = 16 * 1024 * 1024;
        if (archive.Length < 20 || archive.Length > 128 * 1024 * 1024 || !archive.AsSpan(0, 4).SequenceEqual("qres"u8))
            throw new InvalidDataException("Invalid public Qt resource archive.");
        uint version = U32(4);
        if (version is < 1 or > 3) throw new InvalidDataException("Unsupported Qt resource version.");
        int tree = Checked(U32(8)), data = Checked(U32(12)), names = Checked(U32(16)), nodeSize = version >= 3 ? 22 : 14;
        int treeEnd = archive.Length;
        if (data > tree) treeEnd = Math.Min(treeEnd, data);
        if (names > tree) treeEnd = Math.Min(treeEnd, names);
        int nodeCount = (treeEnd - tree) / nodeSize;
        if (nodeCount < 1 || nodeCount > 1_000_000) throw new InvalidDataException("Invalid Qt resource tree.");
        int current = 0;
        string[] parts = resourcePath.Trim('/').Split('/');
        int firstPart = 0;
        string rootName = Name(0);
        // Official external packs name their root ("res"), while standard
        // RCC generators may use an anonymous root. Both forms are supported.
        if (rootName.Length > 0)
        {
            if (parts[0] != rootName) throw new InvalidDataException("Public resource root does not match.");
            firstPart = 1;
        }
        for (int partIndex = firstPart; partIndex < parts.Length; partIndex++)
        {
            string part = parts[partIndex];
            int parent = Node(current);
            if ((U16(parent + 4) & 2) == 0) throw new InvalidDataException("Resource path is not a directory.");
            int count = Checked(U32(parent + 6)), first = Checked(U32(parent + 10));
            if (first > nodeCount || count > nodeCount - first) throw new InvalidDataException("Invalid Qt resource children.");
            int found = -1;
            for (int i = first; i < first + count; i++)
            {
                if (Name(i) == part) { found = i; break; }
            }
            if (found < 0) throw new InvalidDataException("Public resource is missing from the archive.");
            current = found;
        }
        int file = Node(current), flags = U16(file + 4);
        if ((flags & 2) != 0 || (flags & ~1) != 0) throw new InvalidDataException("Unsupported public resource format.");
        int record = Checked((long)data + U32(file + 10)), packed = Checked(U32(record));
        if (packed > limit) throw new InvalidDataException("Public PNG size bound.");
        int payload = Checked((long)record + 4); Bound(payload, packed);
        byte[] result;
        if ((flags & 1) == 0) result = archive.AsSpan(payload, packed).ToArray();
        else
        {
            if (packed < 4) throw new InvalidDataException("Invalid compressed resource.");
            uint expanded = U32(payload);
            if (expanded > limit) throw new InvalidDataException("Public PNG size bound.");
            int length = (int)expanded;
            result = new byte[length];
            using MemoryStream input = new(archive, payload + 4, packed - 4, false);
            using ZLibStream inflater = new(input, CompressionMode.Decompress);
            inflater.ReadExactly(result);
            if (inflater.ReadByte() != -1) throw new InvalidDataException("Compressed PNG length mismatch.");
        }
        if (result.Length < 8 || !result.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Public resource is not a PNG.");
        return result;

        int Node(int index) => index < 0 || index >= nodeCount ? throw new InvalidDataException("Invalid resource node.") : Checked((long)tree + (long)index * nodeSize);
        string Name(int index)
        {
            int name = Checked((long)names + U32(Node(index))), length = U16(name);
            int start = Checked((long)name + 6), bytes = checked(length * 2); Bound(start, bytes);
            return Encoding.BigEndianUnicode.GetString(archive, start, bytes);
        }
        ushort U16(int offset) { Bound(offset, 2); return BinaryPrimitives.ReadUInt16BigEndian(archive.AsSpan(offset, 2)); }
        uint U32(int offset) { Bound(offset, 4); return BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(offset, 4)); }
        int Checked(long value) => value < 0 || value > archive.Length ? throw new InvalidDataException("Resource offset bound.") : (int)value;
        void Bound(int offset, int length) { if (offset < 0 || length < 0 || offset > archive.Length - length) throw new InvalidDataException("Resource range bound."); }
    }
}
