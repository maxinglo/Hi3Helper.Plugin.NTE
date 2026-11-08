using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Config;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>The installed resource list describes files independently of the user-editable version marker.</summary>
internal static class NteInstalledManifest
{
    internal static string PathFor(string root, NteRegionConfig region)
        => NteSharedInstallation.ManagedPath(root, ".collapse-nte/installed/" + region.AppId + "/ResList.xml");

    internal static void Save(string root, NteRegionConfig region, byte[] manifest)
        => NteSharedInstallation.WriteAtomic(PathFor(root, region), manifest);

    internal static async Task<NteResListParser?> LoadAsync(string root, NteRegionConfig region,
        GameVersion recorded, GameVersion release, CancellationToken token)
    {
        NteResListParser? selected = null;
        GameVersion selectedVersion = GameVersion.Empty;
        foreach (string path in new[] { Path.Combine(root, region.SdkDirectory, "ResList.xml"), PathFor(root, region) })
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) continue;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                NteResListParser manifest = NteResListParser.Parse(NteResListDecryptor.Decrypt(bytes, region.AppId));
                if (!GameVersion.TryParse(manifest.Version, out GameVersion version) || version < recorded ||
                    version > release || version == GameVersion.Empty || version < selectedVersion) continue;
                selected = manifest;
                selectedVersion = version;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                           or CryptographicException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex,
                    "[NTE Update] Cannot read installed manifest {Manifest}; using another metadata basis.",
                    Path.GetFileName(path));
            }
        }

        return selected;
    }
}
