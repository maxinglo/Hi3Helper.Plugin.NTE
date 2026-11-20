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

    internal async Task<bool> EnsurePresentForLaunchAsync(string root, NteRegionConfig region, CancellationToken token)
    {
        bool TryUseInstalled(out bool offerShortcuts)
        {
            string directory = region.LauncherDirectoryFor(root);
            string path = Path.Combine(root, directory, NteRegionDefinition.From(region.HostRegion).LauncherExecutable);
            offerShortcuts = File.Exists(Path.Combine(root, directory, NteOfficialLaunchPlan.PendingShortcutOffer));
            return File.Exists(path) && (!offerShortcuts || IsReady(root, region, directory));
        }

        if (TryUseInstalled(out bool existingOffer)) return existingOffer;
        using FileStream operation = NteGameOperation.Acquire(root);
        if (TryUseInstalled(out existingOffer)) return existingOffer;
        NteOfficialPatcherState.EnsureOfficialLauncherClosed(root, region);
        await InstallAsync(root, region, null, token).ConfigureAwait(false);
        if (!TryUseInstalled(out bool newOffer)) throw new InvalidDataException("The official launcher installation is incomplete.");
        return newOffer;
    }

    internal static bool IsReady(string root, NteRegionConfig region, string directory)
    {
        string launcher = Path.Combine(root, directory);
        string config = Path.Combine(launcher, NteRegionDefinition.LauncherConfigPath);
        return File.Exists(config) && NteConfigProvider.IsBilibiliLauncherConfig(config) == region.IsBilibili &&
            File.Exists(Path.Combine(launcher, NteRegionDefinition.From(region.HostRegion).LauncherExecutable)) &&
            File.Exists(Path.Combine(launcher, "WPGameClientSDK_64.dll")) &&
            (!region.IsBilibili || File.Exists(Path.Combine(launcher, BilibiliSdkLibrary)) &&
                File.Exists(Path.Combine(launcher, BilibiliSdkPlatform)));
    }

    private async Task<NteLauncherManifest> ReadInventoryAsync(NteRegionConfig region, CancellationToken token)
    {
        NteLauncherResourceClient client = new(http, region.ZoneHomePageUrl);
        string ini = await client.ReadTextAsync(region.LauncherVersionIniUrl, token).ConfigureAwait(false);
        string url = NteLauncherManifest.ParseFileListUrl(ini, region.LauncherVersionIniUrl);
        NteLauncherManifest manifest =
            NteLauncherManifest.Parse(await client.ReadTextAsync(url, token).ConfigureAwait(false), url);
        string launcherExecutable = NteRegionDefinition.From(region.HostRegion).LauncherExecutable;
        if (!region.IsBilibili) manifest.Files.RemoveAll(file => IsBilibiliSdkPath(file.Path));
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (NteLauncherFile file in manifest.Files)
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
        return manifest;
    }

    internal async Task<long> GetRemainingSizeAsync(string root, NteRegionConfig region, CancellationToken token)
    {
        NteLauncherManifest manifest = await ReadInventoryAsync(region, token).ConfigureAwait(false);
        string launcher = region.LauncherDirectoryFor(root);
        long remaining = 0;
        foreach (NteLauncherFile file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();
            string destination = launcher + "/" + file.Path.TrimStart('/');
            string active = NteSharedInstallation.ManagedPath(root, destination);
            string cached = NteSharedInstallation.ObjectPath(root, destination, file.Md5, file.Size);
            if (!await NteRegionSwitchTransaction.MatchesAsync(active, file.Size, file.Md5, token).ConfigureAwait(false) &&
                !await NteRegionSwitchTransaction.MatchesAsync(cached, file.Size, file.Md5, token).ConfigureAwait(false))
                remaining = checked(remaining + file.Size);
        }
        return remaining;
    }

    internal async Task InstallAsync(string root, NteRegionConfig region,
        Action<long, long, int, int>? progress, CancellationToken token)
    {
        NteSharedInstallation.EnsureGamesClosed(root);
        NteLauncherManifest manifest = await ReadInventoryAsync(region, token).ConfigureAwait(false);
        NteLauncherResourceClient client = new(http, region.ZoneHomePageUrl);
        string launcher = region.LauncherDirectoryFor(root);
        string executable = Path.Combine(root, launcher, NteRegionDefinition.From(region.HostRegion).LauncherExecutable);
        bool newLauncher = !File.Exists(executable);
        bool alternate = !region.IsOverseas && launcher != NteRegionDefinition.CnLauncherDirectory;
        List<NteLauncherFile> files = manifest.Files;

        List<(string Active, string Cached)> prepared = [];
        bool validChannelConfig = region.IsOverseas || !region.IsBilibili && !alternate;
        long total = files.Sum(file => file.Size), done = 0;
        int count = 0;
        foreach (NteLauncherFile file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = file.Path.TrimStart('/');
            string destination = launcher + "/" + relative;
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
                        string? stock = alternate ? NteSharedInstallation.ManagedPath(root,
                            NteRegionDefinition.CnLauncherDirectory + "/" + relative) : null;
                        if (stock != null && await NteRegionSwitchTransaction.MatchesAsync(stock, file.Size, file.Md5, token)
                                .ConfigureAwait(false))
                        {
                            await using FileStream input = File.OpenRead(stock);
                            await using FileStream output = File.Create(temporary);
                            await input.CopyToAsync(output, token).ConfigureAwait(false);
                        }
                        else
                        {
                            await using FileStream output = new(temporary, FileMode.Create, FileAccess.ReadWrite,
                                FileShare.None, 65536, FileOptions.Asynchronous);
                            await client.ExtractAssetAsync(manifest.GetArchiveUrl(file.Path), output, token)
                                .ConfigureAwait(false);
                        }
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

            if (!region.IsOverseas && (region.IsBilibili || alternate) &&
                relative.Equals(NteRegionDefinition.LauncherConfigPath, StringComparison.OrdinalIgnoreCase))
                validChannelConfig = NteConfigProvider.IsBilibiliLauncherConfig(verified) == region.IsBilibili;

            done += file.Size;
            progress?.Invoke(done, total, ++count, files.Count);
        }

        if (!validChannelConfig)
            throw new InvalidDataException("The official platform inventory has no verified channel launcher configuration.");

        NteSharedInstallation.EnsureGamesClosed(root);
        if (newLauncher)
        {
            NteOfficialPatcherState.SeedLauncherMetadata(root, region);
            NteSharedInstallation.WriteAtomic(NteSharedInstallation.ManagedPath(root,
                launcher + "/" + NteOfficialLaunchPlan.PendingShortcutOffer), []);
        }
        // Publish the configuration after the SDK and the entry point last, so a new launcher is
        // only discoverable once its files are installed.
        foreach ((string active, string cached) in prepared.OrderBy(file =>
                     file.Active.Equals(executable, StringComparison.OrdinalIgnoreCase) ? 2 :
                     file.Active.EndsWith(Path.Combine("Config", "Config.ini"), StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(active)!);
            File.Move(cached, active, true);
        }
    }
}
