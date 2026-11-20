using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using Nte.Login;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

// Keep UserData intact when installing official SDK and fallback-launcher components.
internal sealed class NteOfficialLauncherInstaller(HttpClient http)
{
    private const string BilibiliSdkDirectory = "channelSdk/bilibili/";
    private const string BilibiliSdkLibrary = BilibiliSdkDirectory + "PCGameSDK.dll";
    private const string BilibiliSdkPlatform = BilibiliSdkDirectory + "BLPlatform64/PCGamePlatform.exe";

    internal static bool IsBilibiliSdkPath(string path) =>
        path.Replace('\\', '/').TrimStart('/').StartsWith(BilibiliSdkDirectory, StringComparison.OrdinalIgnoreCase);

    internal async Task InstallAsync(string root, NteRegionConfig region,
        Action<long, long, int, int>? progress, CancellationToken token)
    {
        NteSharedInstallation.EnsureGamesClosed(root);
        NteLauncherResourceClient client = new(http, region.ZoneHomePageUrl);
        string ini = await client.ReadTextAsync(region.LauncherVersionIniUrl, token).ConfigureAwait(false);
        string url = NteLauncherManifest.ParseFileListUrl(ini, region.LauncherVersionIniUrl);
        NteLauncherManifest manifest =
            NteLauncherManifest.Parse(await client.ReadTextAsync(url, token).ConfigureAwait(false), url);
        string launcherExecutable = NteRegionDefinition.From(region.HostRegion).LauncherExecutable;
        List<NteLauncherFile> files = manifest.Files.Where(file => region.IsBilibili || !IsBilibiliSdkPath(file.Path)).ToList();
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (NteLauncherFile file in files)
        {
            string relative = file.Path.TrimStart('/');
            if (relative.Split('/').Any(part => part is "" or "." or "..") ||
                relative.StartsWith("UserData/", StringComparison.OrdinalIgnoreCase) || !paths.Add(relative))
                throw new InvalidDataException("Invalid or duplicate path in the official platform inventory.");
        }

        if (!paths.Contains(launcherExecutable))
            throw new InvalidDataException("The official login platform is missing from its verified inventory.");
        if (region.IsBilibili && (!paths.Contains(BilibiliSdkLibrary) || !paths.Contains(BilibiliSdkPlatform)))
            throw new InvalidDataException("The official platform inventory is missing required Bilibili SDK components.");

        List<(string Active, string Cached)> prepared = [];
        bool validChannelConfig = !region.IsBilibili;
        long total = files.Sum(file => file.Size), done = 0;
        int count = 0;
        foreach (NteLauncherFile file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = file.Path.TrimStart('/');
            string destination = region.LauncherDirectoryName + "/" + relative;
            string active = NteSharedInstallation.ManagedPath(root, destination);
            string verified = active;
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
                verified = cached;
            }

            if (region.IsBilibili && relative.Equals("Config/Config.ini", StringComparison.OrdinalIgnoreCase))
                validChannelConfig = NteConfigProvider.IsBilibiliLauncherConfig(verified);

            done += file.Size;
            progress?.Invoke(done, total, ++count, files.Count);
        }

        if (!validChannelConfig)
            throw new InvalidDataException("The official platform inventory has no verified Bilibili launcher configuration.");

        NteSharedInstallation.EnsureGamesClosed(root);
        foreach ((string active, string cached) in prepared)
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(active)!);
            File.Move(cached, active, true);
        }
    }
}
