using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed record NteIntegrityIssue(string Path, string Reason, long ExpectedSize, string ExpectedMd5,
    long? ActualSize, string? ActualMd5, string? Error);

internal sealed class NteIntegrityReport
{
    public int SchemaVersion { get; init; } = 1;
    public required string GameRoot { get; init; }
    public required string Version { get; init; }
    public int TotalFiles { get; init; }
    public long TotalBytes { get; init; }
    public int CheckedFiles { get; set; }
    public long HashedBytes { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset FinishedUtc { get; set; }
    public List<NteIntegrityIssue> Issues { get; } = [];
    public List<string> RepairedFiles { get; } = [];
    public string? RepairError { get; set; }
    public bool Healthy => Completed && Issues.Count == 0;
}

internal delegate void NteIntegrityProgress(long processedBytes, long totalBytes, int checkedFiles, int totalFiles, string path);

internal static class NteGameIntegrity
{
    internal static async Task<NteIntegrityReport> CheckAsync(NteResListParser manifest, string gameRoot,
        NteIntegrityProgress? progress, CancellationToken token)
    {
        string root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = NteGameInstaller.BuildDownloadTasks(manifest, root, false, false, token);
        if (files.Count == 0 || string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("Integrity checking requires a non-empty, versioned resource manifest.");
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!paths.Add(file.OutputPath) || file.TotalSize < 0 || file.ExpectedMd5.Length != 32)
                throw new InvalidDataException("Invalid or duplicate integrity manifest entry.");
            foreach (char character in file.ExpectedMd5)
                if (!char.IsAsciiHexDigit(character)) throw new InvalidDataException("Invalid manifest MD5.");
        }
        NteIntegrityReport report = new() { GameRoot = root, Version = manifest.Version,
            TotalFiles = files.Count, TotalBytes = manifest.GetTotalExtractedSize() };
        long processed = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(root, file.OutputPath);
                progress?.Invoke(processed, report.TotalBytes, report.CheckedFiles, report.TotalFiles, relative);
                FileInfo info = new(file.OutputPath);
                long? length = info.Exists ? info.Length : null;
                string? hash = null;
                string? reason = null;
                string? error = null;
                if (!info.Exists) reason = "missing";
                else if (info.Length != file.TotalSize) reason = "size-mismatch";
                else
                {
                    DateTime beforeWrite = info.LastWriteTimeUtc;
                    try
                    {
                        await using FileStream stream = new(file.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                        long readBytes = 0;
                        int read;
                        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                        {
                            md5.AppendData(buffer, 0, read);
                            readBytes += read;
                            report.HashedBytes += read;
                            progress?.Invoke(processed + Math.Min(readBytes, file.TotalSize), report.TotalBytes,
                                report.CheckedFiles, report.TotalFiles, relative);
                        }
                        hash = Convert.ToHexString(md5.GetHashAndReset());
                        info.Refresh();
                        if (info.Length != length || info.LastWriteTimeUtc != beforeWrite) reason = "changed-during-check";
                        else if (!hash.Equals(file.ExpectedMd5, StringComparison.OrdinalIgnoreCase)) reason = "hash-mismatch";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        reason = "unreadable";
                        error = ex.Message;
                    }
                }
                if (reason != null) report.Issues.Add(new(relative, reason, file.TotalSize, file.ExpectedMd5, length, hash, error));
                report.CheckedFiles++;
                processed += file.TotalSize;
                progress?.Invoke(processed, report.TotalBytes, report.CheckedFiles, report.TotalFiles, relative);
            }
            token.ThrowIfCancellationRequested();
            report.Completed = true;
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return report; }
        finally
        {
            report.FinishedUtc = DateTimeOffset.UtcNow;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void SaveReport(NteIntegrityReport report, string reportPath)
    {
        string path = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, NteIntegrityJsonContext.Default.NteIntegrityReport));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NteIntegrityReport))]
internal partial class NteIntegrityJsonContext : JsonSerializerContext;

internal static class NteGameOperation
{
    internal static FileStream Acquire(string gameRoot)
    {
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
        string directory = Path.Combine(Path.GetTempPath(), "Collapse.NTE.Operations");
        Directory.CreateDirectory(directory);
        try
        {
            return new(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex) { throw new IOException("Another NTE update or integrity check is using this game directory.", ex); }
    }
}
