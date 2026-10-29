using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed class NteSharedProfile
{
    public string Version { get; set; } = "";
    public string Branch { get; set; } = "";
    public string ManifestXml { get; set; } = "";
    public string ReleaseXml { get; set; } = "";
}

internal sealed class NteVerifiedResource
{
    public string Md5 { get; set; } = "";
    public long Size { get; set; }
    public long Written { get; set; }
    public long Created { get; set; }
}

internal sealed class NteSharedInstallationState
{
    public int Format { get; set; } = 1;
    public string ActiveAppId { get; set; } = "";
    public Dictionary<string, NteSharedProfile> Profiles { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, NteVerifiedResource> VerifiedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class NteSharedInstallationIndex
{
    public int Format { get; set; } = 1;
    public Dictionary<string, string> Roots { get; set; } = new(StringComparer.Ordinal);
}

internal static class NteSharedInstallation
{
    internal const string DirectoryName = ".collapse-nte";
    internal static string StatePath(string root) => ManagedPath(root, DirectoryName + "/state.json");
    internal static string JournalPath(string root) => ManagedPath(root, DirectoryName + "/switch.json");
    internal static bool HasPendingSwitch(string root) => File.Exists(JournalPath(root));
    internal static string IndexPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CollapseLauncher", "Plugins", "NTE", "shared-installations.json");

    internal static NteSharedInstallationState? Load(string root)
    {
        string path = StatePath(root);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is < 2 or > 16 * 1024 * 1024)
            throw new InvalidDataException("Invalid shared installation state size.");
        NteSharedInstallationState state = JsonSerializer.Deserialize(File.ReadAllBytes(path),
            NteSharedInstallationJsonContext.Default.NteSharedInstallationState)
            ?? throw new InvalidDataException("Missing shared installation state.");
        if (state.Format != 1 || !IsOverseas(state.ActiveAppId) || state.Profiles.Count is < 1 or > 2 ||
            !state.Profiles.ContainsKey(state.ActiveAppId) || state.Profiles.Keys.Any(id => !IsOverseas(id)))
            throw new InvalidDataException("Unsupported shared installation state.");
        return state;
    }

    internal static bool IsOverseas(string appId) => appId is "2000013" or "3000001";
    internal static bool Contains(string root, NteRegionConfig region) => region.IsOverseas && Load(root)?.Profiles.ContainsKey(region.AppId) == true;
    internal static bool NeedsActivation(string root, NteRegionConfig region) => region.IsOverseas &&
        (HasPendingSwitch(root) || Load(root) is { } state && state.Profiles.ContainsKey(region.AppId) && state.ActiveAppId != region.AppId);

    internal static string ObjectPath(string root, string relativePath, string md5, long size)
    {
        if (md5.Length != 32 || md5.Any(c => !char.IsAsciiHexDigit(c)) || size < 0)
            throw new InvalidDataException("Invalid resource identity.");
        string pathIdentity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.ToLowerInvariant())))[..16];
        return ManagedPath(root, $"{DirectoryName}/objects/{md5[..2].ToLowerInvariant()}/{md5.ToLowerInvariant()}-{pathIdentity}.{size}");
    }

    // Resolve every component before mutation. Reparse points would make a
    // lexical containment check insufficient for a shared update or rollback.
    internal static string ManagedPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new InvalidDataException("Invalid managed installation path.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Managed path escapes the installation.");
        string? current = path;
        while (current != null && current.Length >= fullRoot.Length)
        {
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Shared installations cannot mutate a reparse point.");
            current = Path.GetDirectoryName(current);
        }
        return path;
    }

    internal static void Save(string root, NteSharedInstallationState state) => WriteAtomic(StatePath(root),
        JsonSerializer.SerializeToUtf8Bytes(state, NteSharedInstallationJsonContext.Default.NteSharedInstallationState));

    internal static void WriteAtomic(string path, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool MatchesStamp(string root, string relative, long size, string md5, NteSharedInstallationState? state)
    {
        if (state == null || !state.VerifiedFiles.TryGetValue(relative, out NteVerifiedResource? stamp)) return false;
        FileInfo info = new(ManagedPath(root, relative));
        return info.Exists && info.Length == size && stamp.Size == size && stamp.Md5.Equals(md5, StringComparison.OrdinalIgnoreCase)
            && stamp.Written == info.LastWriteTimeUtc.Ticks && stamp.Created == info.CreationTimeUtc.Ticks;
    }

    internal static NteVerifiedResource Stamp(string path, long size, string md5)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length != size) throw new IOException("Verified resource changed before activation.");
        return new() { Size = size, Md5 = md5, Written = info.LastWriteTimeUtc.Ticks, Created = info.CreationTimeUtc.Ticks };
    }

    internal static void EnsureGamesClosed(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTE server switching requires Windows.");
        foreach (string name in new[] { "HTGame", "NTEGlobalGame", "NTEGlobalUpdate", "NTEGlobalLauncher", "NTETWGame", "NTETWUpdate", "NTETWLauncher" })
        {
            Process[] running = Process.GetProcessesByName(name);
            try
            {
                foreach (Process process in running)
                {
                    string candidate = name == "HTGame" ? NteConfigProvider.GameExecutableRelativePath :
                        (name.StartsWith("NTETW", StringComparison.Ordinal) ? "NTETW/" : "NTEGlobal/") + name + ".exe";
                    string expected = Path.Combine(root, candidate);
                    if (NteProcessImage.Matches(process.Id, expected))
                        throw new IOException("Close this installation's game and both official launchers before switching servers.");
                }
            }
            finally { foreach (Process process in running) process.Dispose(); }
        }
    }

    internal static string? FindRoot(NteRegionConfig region, string? indexPath = null)
    {
        if (!region.IsOverseas) return null;
        string path = indexPath ?? IndexPath;
        if (!File.Exists(path)) return null;
        NteSharedInstallationIndex index = ReadIndex(path);
        if (!index.Roots.TryGetValue(region.AppId, out string? root) || !System.IO.Directory.Exists(root)) return null;
        return Contains(root, region) && File.Exists(Path.Combine(root, region.GameExecutableName)) ? root : null;
    }

    internal static void Register(string root, NteSharedInstallationState state, string? indexPath = null)
    {
        string path = indexPath ?? IndexPath;
        using Mutex gate = new(false, @"Local\Collapse.NTE.SharedInstallationIndex");
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(10)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Another NTE installation registration is active.");
        try
        {
            NteSharedInstallationIndex index = File.Exists(path) ? ReadIndex(path) : new();
            foreach (string id in index.Roots.Where(pair => string.Equals(pair.Value, root, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
                if (!state.Profiles.ContainsKey(id)) index.Roots.Remove(id);
            foreach (string appId in state.Profiles.Keys) index.Roots[appId] = Path.GetFullPath(root);
            WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(index, NteSharedInstallationJsonContext.Default.NteSharedInstallationIndex));
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static void RemoveProfile(string root, NteRegionConfig region, string? indexPath = null)
    {
        NteSharedInstallationState state = Load(root) ?? throw new InvalidDataException("Missing shared installation state.");
        if (HasPendingSwitch(root)) throw new IOException("Recover the pending server switch before removing a region.");
        if (state.ActiveAppId == region.AppId) throw new IOException("Activate the remaining region before removing the active region.");
        if (!state.Profiles.TryGetValue(region.AppId, out NteSharedProfile? removed)) return;
        var retained = state.Profiles.Where(pair => pair.Key != region.AppId)
            .SelectMany(pair => NteRegionSwitchManifest.Read(Encoding.UTF8.GetBytes(pair.Value.ManifestXml)).Values)
            .Select(file => ObjectPath(root, file.Path, file.Md5, file.Size)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (NteSwitchResource file in NteRegionSwitchManifest.Read(Encoding.UTF8.GetBytes(removed.ManifestXml)).Values)
        {
            string cached = ObjectPath(root, file.Path, file.Md5, file.Size);
            if (!retained.Contains(cached) && File.Exists(cached)) File.Delete(cached);
        }
        state.Profiles.Remove(region.AppId);
        Save(root, state);
        Register(root, state, indexPath);
    }

    internal static void RecordUpdate(string root, NteRegionConfig region, string releaseXml, byte[] manifest)
    {
        NteSharedInstallationState? state = Load(root);
        if (state == null) return;
        if (state.ActiveAppId != region.AppId || HasPendingSwitch(root)) throw new IOException("The shared installation is not active for this update.");
        state.Profiles[region.AppId] = new() { Version = NteGameManager.ParseReleaseVersion(releaseXml).ToString(), Branch = region.DefaultBranch,
            ManifestXml = Encoding.UTF8.GetString(manifest), ReleaseXml = releaseXml };
        state.VerifiedFiles.Clear();
        Save(root, state);
    }

    internal static void PreserveReferencedVersion(string outputPath, CancellationToken token)
    {
        if (!File.Exists(outputPath)) return;
        string? root = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        while (root != null && !File.Exists(Path.Combine(root, DirectoryName, "state.json"))) root = Path.GetDirectoryName(root);
        if (root == null) return;
        string relative = Path.GetRelativePath(root, outputPath).Replace('\\', '/');
        if (!relative.StartsWith("Client/", StringComparison.OrdinalIgnoreCase)) return;
        NteSharedInstallationState state = Load(root)!;
        foreach (var profile in state.Profiles.Where(pair => pair.Key != state.ActiveAppId))
        {
            var files = NteRegionSwitchManifest.Read(Encoding.UTF8.GetBytes(profile.Value.ManifestXml));
            if (!files.TryGetValue(relative, out NteSwitchResource? required)) continue;
            if (!MatchesStamp(root, relative, required.Size, required.Md5, state) &&
                !NteRegionSwitchTransaction.MatchesAsync(outputPath, required.Size, required.Md5, token).GetAwaiter().GetResult()) continue;
            string cached = ObjectPath(root, relative, required.Md5, required.Size);
            if (NteRegionSwitchTransaction.MatchesAsync(cached, required.Size, required.Md5, token).GetAwaiter().GetResult()) continue;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            string temporary = cached + ".preserve.tmp";
            try
            {
                File.Copy(outputPath, temporary, true);
                if (!NteRegionSwitchTransaction.MatchesAsync(temporary, required.Size, required.Md5, token).GetAwaiter().GetResult())
                    throw new IOException("The shared file changed while preserving the other region's version.");
                File.Move(temporary, cached, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static NteSharedInstallationIndex ReadIndex(string path)
    {
        if (new FileInfo(path).Length is < 2 or > 65536) throw new InvalidDataException("Invalid shared installation index.");
        NteSharedInstallationIndex index = JsonSerializer.Deserialize(File.ReadAllBytes(path),
            NteSharedInstallationJsonContext.Default.NteSharedInstallationIndex) ?? throw new InvalidDataException("Missing installation index.");
        if (index.Format != 1 || index.Roots.Keys.Any(id => !IsOverseas(id))) throw new InvalidDataException("Invalid installation index format.");
        return index;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NteSharedInstallationState))]
[JsonSerializable(typeof(NteSharedInstallationIndex))]
internal partial class NteSharedInstallationJsonContext : JsonSerializerContext;
