using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed record NteSwitchResource(string Path, long Size, string Md5,
    NteResListEntry? Resource = null, NtePakInfo? Archive = null, NtePakEntry? Entry = null);

internal static class NteRegionSwitchManifest
{
    internal static byte[] SelectTags(byte[] remoteManifest, string installedManifest)
    {
        XmlDocument remote = NteOfficialPatcherState.ParseXml(Encoding.UTF8.GetString(remoteManifest));
        XmlDocument installed = NteOfficialPatcherState.ParseXml(installedManifest);
        HashSet<string> tags = new(StringComparer.Ordinal);
        foreach (XmlElement node in installed.SelectNodes("//BaseVersion")!) tags.Add(node.GetAttribute("tag"));
        foreach (XmlElement node in remote.DocumentElement!.SelectNodes("BaseVersion")!)
            if (!tags.Contains(node.GetAttribute("tag"))) remote.DocumentElement.RemoveChild(node);
        return Encoding.UTF8.GetBytes(remote.OuterXml);
    }

    internal static Dictionary<string, NteSwitchResource> Read(byte[] xml)
    {
        NteResListParser manifest = NteResListParser.Parse(xml);
        Dictionary<string, NteSwitchResource> files = new(StringComparer.OrdinalIgnoreCase);
        void Add(NteSwitchResource file)
        {
            if (!file.Path.StartsWith("Client/", StringComparison.OrdinalIgnoreCase) || file.Path.Replace('\\', '/').Split('/').Any(part => part is "." or "..") || file.Size < 0 ||
                file.Md5.Length != 32 || file.Md5.Any(c => !char.IsAsciiHexDigit(c)) || !files.TryAdd(file.Path, file))
                throw new InvalidDataException("Invalid or duplicate game resource in the region manifest.");
            // Lexical validation is independent from the installation location.
            _ = NteGameInstaller.ResolveResourcePath(Path.GetTempPath(), file.Path);
        }
        foreach (NteResListEntry resource in manifest.Resources.Concat(manifest.BaseVersionResources))
            Add(new(resource.Filename, resource.Filesize, resource.Md5.ToLowerInvariant(), resource));
        foreach (NtePakInfo pak in manifest.Paks.Concat(manifest.BaseVersionPaks))
            foreach (NtePakEntry entry in pak.Entries)
            {
                if (entry.Offset < 0 || entry.Size < 0 || entry.Offset > pak.Filesize || entry.Size > pak.Filesize - entry.Offset)
                    throw new InvalidDataException("Invalid archive entry bounds.");
                Add(new(entry.Name, entry.Size, entry.Md5.ToLowerInvariant(), Archive: pak, Entry: entry));
            }
        return files;
    }
}
