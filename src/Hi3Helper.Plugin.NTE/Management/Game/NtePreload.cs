using Hi3Helper.Plugin.Core.Management;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Xml;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed record NtePreloadRelease(string BranchName, GameVersion Version)
{
    internal static NtePreloadRelease? Parse(string xml, GameVersion currentRelease)
    {
        XmlElement? root = NteOfficialPatcherState.ParseXml(xml).DocumentElement;
        if (!string.Equals(root?["PreReleaseStatus"]?.InnerText.Trim(), "start", StringComparison.OrdinalIgnoreCase))
            return null;
        string branch = root?["PreReleaseBranch"]?.InnerText.Trim() ?? "";
        string version = root?["PreReleaseVersion"]?.InnerText.Trim() ?? "";
        if (branch.Length == 0 || branch is "." or ".." ||
            branch.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')) ||
            !GameVersion.TryParse(version, out GameVersion parsed) || parsed <= currentRelease)
            return null;
        return new(branch, parsed);
    }
}

internal sealed record NtePatchEntry(string OldMd5, long OldSize, string NewMd5, long NewSize,
    NteResListEntry Resource);

/// <summary>Only a diff list authenticated by the release configuration can select patch downloads.</summary>
internal sealed class NtePatchList
{
    private readonly Dictionary<string, List<NtePatchEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);

    internal static NtePatchList Parse(byte[]? bytes, string? releaseXml)
    {
        NtePatchList result = new();
        if (bytes == null || releaseXml == null) return result;
        string? hash = NteOfficialPatcherState.ParseXml(releaseXml).SelectSingleNode("config/Extra/diffHash")?.InnerText;
        if (string.IsNullOrWhiteSpace(hash)) return result;
        ValidateHash(bytes, hash);
        XmlDocument document = NteOfficialPatcherState.ParseXml(System.Text.Encoding.UTF8.GetString(bytes));
        if (document.DocumentElement?.Name != "PatchList") throw new InvalidDataException("Invalid official diff list.");
        foreach (XmlElement node in document.SelectNodes("/PatchList/Patch | /PatchList/Section/Patch")!)
        {
            var old = ParseIdentity(node.GetAttribute("oldfile"));
            var target = ParseIdentity(node.GetAttribute("newfile"));
            var patch = ParseIdentity(node.GetAttribute("patch"));
            NteResListEntry resource = new() { Md5 = patch.Md5, Filesize = patch.Size };
            long end = 0;
            foreach (XmlElement block in node.SelectNodes("Block")!)
            {
                long start = long.Parse(block.GetAttribute("start"), CultureInfo.InvariantCulture);
                long size = long.Parse(block.GetAttribute("size"), CultureInfo.InvariantCulture);
                string md5 = block.GetAttribute("md5");
                if (start != end || size <= 0 || size > patch.Size - end || !IsMd5(md5))
                    throw new InvalidDataException("Invalid diff resource block.");
                resource.Blocks.Add(new() { Index = resource.Blocks.Count, Start = start, Size = size, Md5 = md5 });
                end += size;
            }
            if (end != 0 && end != patch.Size) throw new InvalidDataException("Incomplete diff resource blocks.");
            string key = Identity(target.Md5, target.Size);
            if (!result._entries.TryGetValue(key, out List<NtePatchEntry>? entries)) result._entries.Add(key, entries = []);
            entries.Add(new(old.Md5, old.Size, target.Md5, target.Size, resource));
        }
        return result;
    }

    internal NtePatchEntry? Find(NteGameInstaller.DownloadTask target, NteGameInstaller.DownloadTask? previous)
        => previous != null && _entries.TryGetValue(Identity(target.ExpectedMd5, target.TotalSize), out var entries)
            ? entries.FirstOrDefault(p => p.OldSize == previous.TotalSize &&
                p.OldMd5.Equals(previous.ExpectedMd5, StringComparison.OrdinalIgnoreCase) && p.Resource.Filesize < target.TotalSize)
            : null;

    internal static void ValidateHash(byte[] bytes, string? expected)
    {
        if (expected == null || !IsMd5(expected) ||
            !Convert.ToHexString(MD5.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Official resource manifest checksum does not match the release configuration.");
    }

    private static (string Md5, long Size) ParseIdentity(string value)
    {
        int separator = value.LastIndexOf('.');
        if (separator != 32 || !IsMd5(value[..separator]) ||
            !long.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long size) || size < 0)
            throw new InvalidDataException("Invalid official diff resource identity.");
        return (value[..separator], size);
    }

    internal static bool IsMd5(string value) => value.Length == 32 && value.All(char.IsAsciiHexDigit);
    internal static string Identity(string md5, long size) => md5.ToLowerInvariant() + "." + size.ToString(CultureInfo.InvariantCulture);
}

internal sealed record NtePreloadPlan(string BranchName, GameVersion Version, NteResListParser Manifest, NtePatchList Patches);

internal static class NtePreloadCache
{
    internal static string PathFor(string gameRoot, string appId, string md5, long size)
    {
        if (appId is not ("1289" or "3000001" or "2000013") || !NtePatchList.IsMd5(md5) || size < 0)
            throw new InvalidDataException("Invalid preload cache identity.");
        // Content identity also permits reuse if the released version differs from the preview version.
        return NteGameInstaller.ResolveResourcePath(gameRoot,
            ".collapse_nte_preload/" + appId + "/" + NtePatchList.Identity(md5, size));
    }
}
