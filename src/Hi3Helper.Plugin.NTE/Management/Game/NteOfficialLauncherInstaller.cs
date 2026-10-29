using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

// Taiwan currently delegates authentication to the official platform. Install
// its verified components as part of installation/repair, leaving UserData alone.
internal sealed class NteOfficialLauncherInstaller(HttpClient http)
{
    internal async Task InstallAsync(string root, NteRegionConfig region,
        Action<long, long, int, int>? progress, CancellationToken token)
    {
        NteSharedInstallation.EnsureGamesClosed(root);
        NteLauncherResourceClient client = new(http, region.ZoneHomePageUrl);
        string ini = await client.ReadTextAsync(region.LauncherVersionIniUrl, token).ConfigureAwait(false);
        string url = NteLauncherManifest.ParseFileListUrl(ini, region.LauncherVersionIniUrl);
        NteLauncherManifest manifest =
            NteLauncherManifest.Parse(await client.ReadTextAsync(url, token).ConfigureAwait(false), url);
        if (!manifest.Files.Any(file =>
                file.Path.TrimStart('/').Equals(region.LauncherDirectoryName + "Game.exe",
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The official login platform is missing from its verified inventory.");
        List<(string Active, string Cached)> prepared = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long total = manifest.Files.Sum(file => file.Size), done = 0;
        int count = 0;
        foreach (NteLauncherFile file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            string relative = file.Path.TrimStart('/').Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "." or "..") ||
                relative.StartsWith("UserData/", StringComparison.OrdinalIgnoreCase) || !paths.Add(relative))
                throw new InvalidDataException("Invalid or duplicate path in the official platform inventory.");
            string destination = region.LauncherDirectoryName + "/" + relative;
            string active = NteSharedInstallation.ManagedPath(root, destination);
            if (!await NteRegionSwitchTransaction.MatchesAsync(active, file.Size, file.Md5, token)
                    .ConfigureAwait(false))
            {
                string cached = NteSharedInstallation.ObjectPath(root, destination, file.Md5, file.Size);
                if (!await NteRegionSwitchTransaction.MatchesAsync(cached, file.Size, file.Md5, token)
                        .ConfigureAwait(false))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
                    string temporary = cached + ".launcher.tmp";
                    try
                    {
                        await using (FileStream output = new(temporary, FileMode.Create, FileAccess.ReadWrite,
                                         FileShare.None, 65536, FileOptions.Asynchronous))
                            await client.ExtractAssetAsync(manifest.GetArchiveUrl(file.Path), output, token)
                                .ConfigureAwait(false);
                        if (!await NteRegionSwitchTransaction.MatchesAsync(temporary, file.Size, file.Md5, token)
                                .ConfigureAwait(false))
                            throw new InvalidDataException("An official platform file failed verification.");
                        File.Move(temporary, cached, true);
                    }
                    finally
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                }

                prepared.Add((active, cached));
            }

            done += file.Size;
            progress?.Invoke(done, total, ++count, manifest.Files.Count);
        }

        NteSharedInstallation.EnsureGamesClosed(root);
        foreach ((string active, string cached) in prepared)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(active)!);
            File.Move(cached, active, true);
        }
    }
}
