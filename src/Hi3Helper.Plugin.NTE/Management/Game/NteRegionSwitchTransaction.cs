using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed class NteRegionSwitchAction
{
    public string Path { get; set; } = "";
    public string NewObject { get; set; } = "";
    public string Backup { get; set; } = "";
    public string NewMd5 { get; set; } = "";
    public string OldMd5 { get; set; } = "";
    public long NewSize { get; set; }
    public long OldSize { get; set; }
    public bool HadOriginal { get; set; }
    public bool RemoveOnly { get; set; }
}

internal sealed class NteRegionSwitchJournal
{
    public int Format { get; set; } = 1;
    public string Id { get; set; } = "";
    public string SourceAppId { get; set; } = "";
    public string TargetAppId { get; set; } = "";
    public bool HadState { get; set; }
    public bool Committed { get; set; }
    public List<NteRegionSwitchAction> Actions { get; set; } = [];
}

// The journal is flushed before each rename. Recovery infers completed moves
// from the backup and active file identities, including crashes between renames.
internal sealed class NteRegionSwitchTransaction
{
    private readonly string root;
    private readonly NteRegionSwitchJournal journal;
    private string TransactionDirectory => NteSharedInstallation.ManagedPath(root, NteSharedInstallation.DirectoryName + "/transactions/" + journal.Id);
    private string OriginalState => Path.Combine(TransactionDirectory, "original-state.json");
    private NteRegionSwitchTransaction(string root, NteRegionSwitchJournal journal) { this.root = root; this.journal = journal; }

    internal static NteRegionSwitchTransaction Begin(string root, string sourceAppId, string targetAppId)
    {
        if (NteSharedInstallation.HasPendingSwitch(root)) throw new IOException("Recover the previous server switch first.");
        NteRegionSwitchJournal journal = new() { Id = Guid.NewGuid().ToString("N"), SourceAppId = sourceAppId, TargetAppId = targetAppId,
            HadState = File.Exists(NteSharedInstallation.StatePath(root)) };
        NteRegionSwitchTransaction transaction = new(root, journal);
        transaction.Validate();
        Directory.CreateDirectory(transaction.TransactionDirectory);
        if (journal.HadState) File.Copy(NteSharedInstallation.StatePath(root), transaction.OriginalState);
        transaction.Save();
        return transaction;
    }

    internal async Task ReplaceAsync(string relative, string? preparedObject, long size, string md5, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateActivePath(relative);
        string active = NteSharedInstallation.ManagedPath(root, relative);
        string? prepared = preparedObject == null ? null : NteSharedInstallation.ManagedPath(root, Path.GetRelativePath(root, preparedObject));
        if (prepared != null && !prepared.StartsWith(NteSharedInstallation.ManagedPath(root, NteSharedInstallation.DirectoryName) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Switch inputs must be staged in the managed object store.");
        FileInfo original = new(active);
        NteRegionSwitchAction action = new()
        {
            Path = relative, NewObject = prepared == null ? "" : Path.GetRelativePath(root, prepared), RemoveOnly = prepared == null,
            Backup = Path.GetRelativePath(root, Path.Combine(TransactionDirectory, "backups", journal.Actions.Count.ToString("D6"))),
            NewSize = size, NewMd5 = md5, HadOriginal = original.Exists, OldSize = original.Exists ? original.Length : 0
        };
        if (original.Exists)
        {
            await using FileStream stream = File.OpenRead(active);
            action.OldMd5 = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, token).ConfigureAwait(false));
        }
        if (prepared != null && !await MatchesAsync(prepared, size, md5, token).ConfigureAwait(false)) throw new InvalidDataException("Staged region file failed verification.");
        journal.Actions.Add(action);
        Save();
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        if (action.HadOriginal)
        {
            string backup = NteSharedInstallation.ManagedPath(root, action.Backup);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Move(active, backup);
        }
        if (prepared != null) File.Move(prepared, active);
    }

    internal void Commit(NteSharedInstallationState state)
    {
        if (state.ActiveAppId != journal.TargetAppId) throw new InvalidDataException("Switch state targets another region.");
        NteSharedInstallation.Save(root, state);
        journal.Committed = true;
        Save();
        FinishCommitted();
    }

    internal void Rollback()
    {
        if (journal.Committed) { FinishCommitted(); return; }
        foreach (NteRegionSwitchAction action in Enumerable.Reverse(journal.Actions))
        {
            string active = NteSharedInstallation.ManagedPath(root, action.Path);
            string backup = NteSharedInstallation.ManagedPath(root, action.Backup);
            if (action.RemoveOnly)
            {
                if (File.Exists(backup))
                {
                    if (File.Exists(active)) throw new IOException("A deactivated file was recreated outside the switch; recovery preserved both copies.");
                    Directory.CreateDirectory(Path.GetDirectoryName(active)!);
                    File.Move(backup, active);
                }
                else if (action.HadOriginal && !MatchesAsync(active, action.OldSize, action.OldMd5, CancellationToken.None).GetAwaiter().GetResult())
                    throw new IOException("An original region backup is missing. Recovery preserved the remaining files.");
                continue;
            }
            string prepared = NteSharedInstallation.ManagedPath(root, action.NewObject);
            if (File.Exists(backup) || !action.HadOriginal)
            {
                if (File.Exists(active))
                {
                    if (!MatchesAsync(active, action.NewSize, action.NewMd5, CancellationToken.None).GetAwaiter().GetResult())
                        throw new IOException("An active file changed outside the switch. Recovery preserved all copies for inspection.");
                    Directory.CreateDirectory(Path.GetDirectoryName(prepared)!);
                    File.Move(active, prepared, true);
                }
                if (File.Exists(backup))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(active)!);
                    File.Move(backup, active);
                }
            }
            else if (action.HadOriginal && !MatchesAsync(active, action.OldSize, action.OldMd5, CancellationToken.None).GetAwaiter().GetResult())
                throw new IOException("An original region backup is missing. Recovery preserved the remaining files.");
        }
        if (journal.HadState)
        {
            if (!File.Exists(OriginalState)) throw new IOException("The original shared state backup is missing.");
            NteSharedInstallation.WriteAtomic(NteSharedInstallation.StatePath(root), File.ReadAllBytes(OriginalState));
        }
        else if (File.Exists(NteSharedInstallation.StatePath(root))) File.Delete(NteSharedInstallation.StatePath(root));
        FinishJournal();
    }

    internal static bool Recover(string root)
    {
        string path = NteSharedInstallation.JournalPath(root);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length is < 2 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid server switch journal size.");
        NteRegionSwitchJournal journal = JsonSerializer.Deserialize(File.ReadAllBytes(path),
            NteRegionSwitchJournalJsonContext.Default.NteRegionSwitchJournal) ?? throw new InvalidDataException("Missing switch journal.");
        NteRegionSwitchTransaction transaction = new(root, journal);
        transaction.Validate();
        if (journal.Committed) transaction.FinishCommitted();
        else transaction.Rollback();
        return true;
    }

    private void FinishCommitted()
    {
        foreach (NteRegionSwitchAction action in journal.Actions.Where(action => action.HadOriginal))
        {
            string backup = NteSharedInstallation.ManagedPath(root, action.Backup);
            if (!File.Exists(backup)) continue;
            string cached = NteSharedInstallation.ObjectPath(root, action.Path, action.OldMd5, action.OldSize);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.Move(backup, cached, true);
        }
        FinishJournal();
    }

    private void FinishJournal()
    {
        // This recursively removes only this journal's validated transaction directory.
        string directory = TransactionDirectory;
        File.Delete(NteSharedInstallation.JournalPath(root));
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private void Save() => NteSharedInstallation.WriteAtomic(NteSharedInstallation.JournalPath(root),
        JsonSerializer.SerializeToUtf8Bytes(journal, NteRegionSwitchJournalJsonContext.Default.NteRegionSwitchJournal));

    private void Validate()
    {
        if (journal.Format != 1 || !Guid.TryParseExact(journal.Id, "N", out _) ||
            !NteSharedInstallation.IsOverseas(journal.SourceAppId) || !NteSharedInstallation.IsOverseas(journal.TargetAppId))
            throw new InvalidDataException("Invalid server switch journal.");
        foreach (NteRegionSwitchAction action in journal.Actions)
        {
            ValidateActivePath(action.Path);
            string backup = NteSharedInstallation.ManagedPath(root, action.Backup);
            string prepared = action.RemoveOnly ? TransactionDirectory : NteSharedInstallation.ManagedPath(root, action.NewObject);
            if (!backup.StartsWith(TransactionDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !prepared.StartsWith(NteSharedInstallation.ManagedPath(root, NteSharedInstallation.DirectoryName) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                action.NewSize < 0 || action.OldSize < 0 || !action.RemoveOnly && !ValidMd5(action.NewMd5) || action.HadOriginal && !ValidMd5(action.OldMd5))
                throw new InvalidDataException("Invalid file action in server switch journal.");
        }
    }

    private static bool ValidMd5(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);
    private static void ValidateActivePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':') || normalized.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException("Invalid active switch path.");
        if (normalized.Equals(".collapse_nte_config", StringComparison.Ordinal) || normalized.StartsWith("Client/", StringComparison.OrdinalIgnoreCase)) return;
        foreach (string launcher in new[] { "NTETW/", "NTEGlobal/" })
        {
            if (!normalized.StartsWith(launcher, StringComparison.OrdinalIgnoreCase)) continue;
            string relative = normalized[launcher.Length..];
            if (!relative.StartsWith("UserData/", StringComparison.OrdinalIgnoreCase) ||
                relative is "UserData/Patcher/PatcherSDK/config.xml" or "UserData/Patcher/PatcherSDK/ResList.xml") return;
        }
        throw new InvalidDataException("Server switching cannot replace account or unrelated installation data.");
    }

    internal static async Task<bool> MatchesAsync(string path, long size, string md5, CancellationToken token)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != size) return false;
        long written = info.LastWriteTimeUtc.Ticks, created = info.CreationTimeUtc.Ticks;
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actual = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, token).ConfigureAwait(false));
        info.Refresh();
        return info.Exists && info.Length == size && info.LastWriteTimeUtc.Ticks == written && info.CreationTimeUtc.Ticks == created && actual.Equals(md5, StringComparison.OrdinalIgnoreCase);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NteRegionSwitchJournal))]
internal partial class NteRegionSwitchJournalJsonContext : JsonSerializerContext;
