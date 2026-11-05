using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Reuse hashes within one installer session; full integrity checks always read the file again.</summary>
internal sealed class NteFileVerificationCache
{
    private sealed record Entry(long Size, long Written, long Created, string Actual);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _hashReads;
    internal int HashReads => Volatile.Read(ref _hashReads);

    internal void RememberVerified(string path, long size, string expected)
    {
        FileInfo info = new(path);
        if (info.Exists && info.Length == size)
            _entries[path] = new(size, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, expected);
    }

    internal bool IsVerified(string path, long size, string expected)
        => KnownValidity(path, size, expected) == true;

    internal bool? KnownValidity(string path, long size, string expected)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != size) return false;
        return _entries.TryGetValue(path, out Entry? entry) && entry.Size == size &&
            entry.Written == info.LastWriteTimeUtc.Ticks && entry.Created == info.CreationTimeUtc.Ticks
            ? entry.Actual.Equals(expected, StringComparison.OrdinalIgnoreCase) : null;
    }

    internal bool Verify(string path, long size, string expected, CancellationToken token, Action<long>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        FileInfo info = new(path);
        if (!info.Exists || info.Length != size) return false;
        long written = info.LastWriteTimeUtc.Ticks;
        long created = info.CreationTimeUtc.Ticks;
        if (_entries.TryGetValue(path, out Entry? cached) && cached.Size == size && cached.Written == written &&
            cached.Created == created)
        {
            progress?.Invoke(size);
            return cached.Actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        Interlocked.Increment(ref _hashReads);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long processed = 0;
        string actual;
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                processed += read;
                progress?.Invoke(processed);
            }
            token.ThrowIfCancellationRequested();
            actual = Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
        info.Refresh();
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != written || info.CreationTimeUtc.Ticks != created)
            return false;
        bool valid = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        _entries[path] = new(size, written, created, actual);
        return valid;
    }
}
