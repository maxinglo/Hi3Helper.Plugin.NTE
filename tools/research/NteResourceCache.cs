using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Development-only resource preservation helper. Never compiled into the release plugin.</summary>
internal sealed class NteResourceCache(string? directory)
{
    internal static string ConfigurationPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CollapseLauncher", "Plugins", "NTE", "downloads.json");

    internal static NteResourceCache FromConfiguration()
    {
        if (!File.Exists(ConfigurationPath)) return new(null);
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(ConfigurationPath));
        string? root = config.RootElement.GetProperty("cacheDirectory").GetString();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new InvalidDataException("NTE resource cache requires an absolute directory path.");
        return new(Path.GetFullPath(root));
    }

    internal string? GetPath(string md5, long size)
    {
        if (directory == null) return null;
        if (md5.Length != 32 || size < 0) throw new InvalidDataException("Invalid cache resource identity.");
        foreach (char value in md5) if (!char.IsAsciiHexDigit(value)) throw new InvalidDataException("Invalid cache MD5.");
        string hash = md5.ToLowerInvariant();
        return Path.Combine(directory, hash[..2], hash + "." + size);
    }

    internal async Task StoreAsync(string source, string md5, long size, CancellationToken token)
    {
        string? cached = GetPath(md5, size);
        if (cached == null) return;
        if (File.Exists(cached) && new FileInfo(cached).Length == size) return;
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        string temporary = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await CopyVerifiedAsync(source, temporary, md5, size, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, cached, true);
        }
        finally { File.Delete(temporary); }
    }

    internal async Task<bool> TryRestoreAsync(string output, string md5, long size, Action<long>? progress, CancellationToken token)
    {
        string? cached = GetPath(md5, size);
        if (cached == null || !File.Exists(cached) || new FileInfo(cached).Length != size) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        string temporary = output + ".cache-" + Guid.NewGuid().ToString("N") + ".tmp";
        long reported = 0;
        try
        {
            await CopyVerifiedAsync(cached, temporary, md5, size,
                bytes => { reported += bytes; progress?.Invoke(bytes); }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output, true);
            File.Delete(output + ".tmp");
            File.Delete(output + ".tmp.identity");
            return true;
        }
        catch (InvalidDataException)
        {
            if (reported != 0) progress?.Invoke(-reported);
            // Only an identity-checked path inside the cache is removed; never remove the installed file.
            File.Delete(cached);
            return false;
        }
        finally { File.Delete(temporary); }
    }

    private static async Task CopyVerifiedAsync(string source, string destination, string md5, long size,
        Action<long>? progress, CancellationToken token)
    {
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != size) throw new InvalidDataException("Cache source size does not match the resource manifest.");
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long copied = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                copied += read;
                progress?.Invoke(read);
            }
            token.ThrowIfCancellationRequested();
            if (copied != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(md5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cached resource hash does not match the manifest.");
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
