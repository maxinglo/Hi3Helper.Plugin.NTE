// HDIFF13 decoding follows libHDiffPatch/HPatch/patch.c and patch_private.h:
// https://github.com/sisong/HDiffPatch (MIT).
// Copyright (c) 2012-2018 HouSisong
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Bounded-memory decoder for the uncompressed HDIFF13 patches served by the CN launcher.</summary>
internal static class NteHdiffPatch
{
    internal static void Apply(string oldPath, string patchPath, string outputPath, long expectedSize,
        CancellationToken token, Action<long, long>? progress = null)
    {
        try { ApplyCore(oldPath, patchPath, outputPath, expectedSize, token, progress); }
        catch (OverflowException ex) { throw new InvalidDataException("HDIFF13 integer overflow.", ex); }
    }

    private static void ApplyCore(string oldPath, string patchPath, string outputPath, long expectedSize,
        CancellationToken token, Action<long, long>? progress)
    {
        token.ThrowIfCancellationRequested();
        using FileStream patch = File.OpenRead(patchPath);
        Span<byte> magic = stackalloc byte[9];
        patch.ReadExactly(magic);
        if (!magic.SequenceEqual("HDIFF13&\0"u8))
            throw new NotSupportedException("The official patch uses an unsupported HDiff format or compressor.");
        long newSize = ReadUInt(patch), oldSize = ReadUInt(patch), coverCount = ReadUInt(patch);
        long[] sizes = new long[4];
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = ReadUInt(patch);
            if (ReadUInt(patch) != 0) throw new NotSupportedException("Compressed HDIFF13 sections are not supported.");
        }
        long offset = patch.Position;
        long end = offset;
        foreach (long size in sizes) end = checked(end + size);
        if (end != patch.Length || newSize != expectedSize || coverCount > sizes[0] / 3)
            throw new InvalidDataException("Invalid HDIFF13 section sizes.");

        using FileStream old = new(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
        if (old.Length != oldSize) throw new InvalidDataException("HDIFF13 source size does not match.");
        using Section covers = new(patchPath, offset, sizes[0]); offset += sizes[0];
        using Section controls = new(patchPath, offset, sizes[1]); offset += sizes[1];
        using Section codes = new(patchPath, offset, sizes[2]); offset += sizes[2];
        using Section literals = new(patchPath, offset, sizes[3]);
        RleDecoder rle = new(controls, codes);
        using FileStream output = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        byte[] delta = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            void Copy(Stream source, long count, bool add)
            {
                if (count < 0 || count > newSize - output.Position) throw new InvalidDataException("HDIFF13 output exceeds its target.");
                while (count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int length = (int)Math.Min(count, buffer.Length);
                    Span<byte> bytes = buffer.AsSpan(0, length);
                    source.ReadExactly(bytes);
                    rle.Decode(bytes, delta, add);
                    output.Write(bytes);
                    count -= length;
                    progress?.Invoke(output.Position, newSize);
                }
            }

            long oldEnd = 0;
            for (long i = 0; i < coverCount; i++)
            {
                token.ThrowIfCancellationRequested();
                long distance = ReadUInt(covers, 1, out int sign);
                long oldPosition = sign == 0 ? checked(oldEnd + distance) : checked(oldEnd - distance);
                long gap = ReadUInt(covers), length = ReadUInt(covers);
                if (oldPosition < 0 || oldPosition > oldSize || length > oldSize - oldPosition)
                    throw new InvalidDataException("HDIFF13 cover escapes the source file.");
                Copy(literals, gap, false);
                old.Position = oldPosition;
                Copy(old, length, true);
                oldEnd = checked(oldPosition + length);
            }
            Copy(literals, newSize - output.Position, false);
            if (covers.Remaining != 0 || literals.Remaining != 0 || !rle.Finished)
                throw new InvalidDataException("HDIFF13 contains incomplete or trailing data.");
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            ArrayPool<byte>.Shared.Return(delta);
        }
    }

    private static long ReadUInt(Stream stream) => ReadUInt(stream, 0, out _);
    private static long ReadUInt(Stream stream, int tagBits, out int tag)
    {
        int first = stream.ReadByte();
        if (first < 0) throw new InvalidDataException("Truncated HDIFF13 integer.");
        tag = first >> (8 - tagBits);
        long value = first & ((1 << (7 - tagBits)) - 1);
        bool more = (first & (1 << (7 - tagBits))) != 0;
        while (more)
        {
            int next = stream.ReadByte();
            if (next < 0 || value > (long.MaxValue >> 7)) throw new InvalidDataException("Invalid HDIFF13 integer.");
            value = (value << 7) | (uint)(next & 127);
            more = (next & 128) != 0;
        }
        return value;
    }

    private sealed class RleDecoder(Section controls, Section codes)
    {
        private long _remaining;
        private int _kind, _value;
        internal bool Finished => _remaining == 0 && controls.Remaining == 0 && codes.Remaining == 0;

        internal void Decode(Span<byte> bytes, byte[] delta, bool add)
        {
            while (!bytes.IsEmpty)
            {
                if (_remaining == 0)
                {
                    _remaining = checked(ReadUInt(controls, 2, out _kind) + 1);
                    _value = _kind switch { 0 => 0, 1 => 255, 2 => codes.ReadByte(), _ => 0 };
                    if (_value < 0) throw new InvalidDataException("Truncated HDIFF13 RLE value.");
                }
                int count = (int)Math.Min(_remaining, bytes.Length);
                if (_kind == 3)
                {
                    if (add)
                    {
                        codes.ReadExactly(delta.AsSpan(0, count));
                        for (int i = 0; i < count; i++) bytes[i] = unchecked((byte)(bytes[i] + delta[i]));
                    }
                    else codes.Skip(count);
                }
                else if (add && _value != 0)
                    for (int i = 0; i < count; i++) bytes[i] = unchecked((byte)(bytes[i] + _value));
                bytes = bytes[count..];
                _remaining -= count;
            }
        }
    }

    private sealed class Section : Stream
    {
        private readonly FileStream _file;
        internal long Remaining { get; private set; }
        internal Section(string path, long start, long size)
        {
            _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            _file.Position = start;
            Remaining = size;
        }
        internal void Skip(long size)
        {
            if (size < 0 || size > Remaining) throw new InvalidDataException("Truncated HDIFF13 section.");
            _file.Position += size;
            Remaining -= size;
        }
        public override int ReadByte()
        {
            if (Remaining == 0) return -1;
            int result = _file.ReadByte();
            if (result >= 0) Remaining--;
            return result;
        }
        public override int Read(Span<byte> buffer)
        {
            int read = _file.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            Remaining -= read;
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
