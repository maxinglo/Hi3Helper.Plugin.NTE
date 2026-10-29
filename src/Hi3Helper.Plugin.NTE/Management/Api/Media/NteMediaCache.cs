using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Api.Media;

internal sealed class NteMediaCache(string directory)
{
    internal Task<string> GetOrCreateAsync(string archiveUrl,
        Func<Stream, CancellationToken, Task> writePayload, CancellationToken token)
    {
        Uri uri = new(archiveUrl);
        string resourcePath = uri.AbsolutePath;
        if (resourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            resourcePath = resourcePath[..^4];
        string extension = Path.GetExtension(resourcePath).ToLowerInvariant();
        return GetOrCreateGeneratedAsync(archiveUrl, extension, writePayload, token);
    }

    internal async Task<string> GetOrCreateGeneratedAsync(string cacheKey, string extension,
        Func<Stream, CancellationToken, Task> writePayload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (extension.Length > 16 || extension.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("Invalid cache extension.", nameof(extension));
        string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey)));
        string path = Path.GetFullPath(Path.Combine(directory, key + extension));
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await writePayload(output, token).ConfigureAwait(false);
                if (output.Length == 0)
                    throw new InvalidDataException("Background resource is empty.");
                await output.FlushAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
