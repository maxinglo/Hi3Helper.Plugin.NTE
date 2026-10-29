using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.NTE.Localization;
using Hi3Helper.Plugin.NTE.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

[Collection("Plugin locale")]
public sealed class LiveGlobalTests
{
    [Fact(Explicit = true)]
    public async Task CurrentInternationalCdn_DecryptsManifestDownloadsSamplesAndLocalizesArtwork()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Hi3Helper.Plugin.NTE.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "research", "live-global-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(120) };
        NteCNGameManager manager = new(client, NteConfigProvider.Global);
        NteCNGameInstaller installer = new(manager, client);
        NtePlugin plugin = new();
        try
        {
            NteResListParser manifest = await installer.GetIntegrityManifestAsync(TestContext.Current.CancellationToken);
            Assert.Equal("3000001", manager.AppId);
            Assert.NotEmpty(manifest.Resources);
            Assert.NotEmpty(manifest.Paks);
            Assert.All(manager.GameResBaseUrls, url => Assert.Contains("perfectworld.com", url));
            var tasks = NteCNGameInstaller.BuildDownloadTasks(manifest, Path.Combine(directory, "plan"), false, false, TestContext.Current.CancellationToken);
            Assert.NotEmpty(tasks);
            string releaseHash = NteOfficialPatcherState.ParseXml(manager.ReleaseConfigXml!).SelectSingleNode("config/Extra/listHash")!.InnerText;
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest-summary.json"), JsonSerializer.Serialize(new
            {
                manifest.Version, installBytes = manifest.GetTotalExtractedSize(), files = tasks.Count,
                smallestResources = manifest.Resources.OrderBy(r => r.Filesize).Take(6).Select(r => new { r.Filename, r.Filesize, r.HasBlocks }),
                largestResource = manifest.Resources.Max(r => r.Filesize)
            }, new JsonSerializerOptions { WriteIndented = true }), TestContext.Current.CancellationToken);

            NteResListEntry resource = manifest.Resources.Where(r => r.Filesize > 0).OrderBy(r => r.Filesize).First();
            Assert.True(resource.Filesize < 64_000_000, "The live test only downloads a small sample resource.");
            string resourcePath = Path.Combine(directory, "sample-resource.bin");
            if (resource.HasBlocks)
                await installer.DownloadBlockedResourceAsync(manager.GameResBaseUrls, manager.BranchName, resource, resourcePath, TestContext.Current.CancellationToken);
            else
                await installer.DownloadResourceFileAsync(manager.GameResBaseUrls, manager.BranchName, resource.Md5, resource.Filesize, resourcePath, TestContext.Current.CancellationToken);
            Assert.Equal(resource.Md5, Convert.ToHexStringLower(MD5.HashData(await File.ReadAllBytesAsync(resourcePath, TestContext.Current.CancellationToken))));
            var (pak, entry) = manifest.Paks.SelectMany(p => p.Entries.Select(e => (p, e))).Where(pair => pair.e.Size is > 0 and < 1_000_000).OrderBy(pair => pair.e.Size).First();
            string entryPath = Path.Combine(directory, "sample-range.bin");
            await installer.DownloadPakEntryAsync(manager.GameResBaseUrls, manager.BranchName, pak, entry, entryPath, TestContext.Current.CancellationToken);
            Assert.Equal(entry.Md5, Convert.ToHexStringLower(MD5.HashData(await File.ReadAllBytesAsync(entryPath, TestContext.Current.CancellationToken))));

            List<object> locales = [];
            foreach (string locale in new[] { "en-US", "zh-TW" })
            {
                plugin.SetPluginLocaleId(locale);
                NteCNLauncherApiMedia media = new(NteConfigProvider.Global.LauncherVersionIniUrl, NteGlobalLocalization.GetPageUrl(locale),
                    client, Path.Combine(directory, "media"), () => new NteAppearanceOptions(), NteConfigProvider.Global);
                NteCNLauncherApiNews news = new(client, Path.Combine(directory, "icons", locale), NteConfigProvider.Global);
                await media.InitializeMediaAsync(TestContext.Current.CancellationToken);
                using PluginDisposableMemory<LauncherPathEntry> backgrounds = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetBackgroundEntries);
                using PluginDisposableMemory<LauncherPathEntry> overlays = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetLogoOverlayEntries);
                Assert.True(backgrounds.Length >= 2);
                Assert.Equal(backgrounds.Length, overlays.Length);
                using LauncherPathEntry overlay = overlays[0];
                NtePngImage actual = NtePngImage.Load(await File.ReadAllBytesAsync(overlay.Path!, TestContext.Current.CancellationToken));
                string pack = Assert.Single(Directory.GetFiles(Path.Combine(directory, "media"), "*.dat"));
                byte[] bytes = await File.ReadAllBytesAsync(pack, TestContext.Current.CancellationToken);
                byte[] slogan = NteQtResourceImage.ReadPng(bytes, NteGlobalLocalization.GetArtworkPath(locale));
                foreach (string language in NteConfigProvider.Global.SupportedLanguages)
                    Assert.NotEmpty(NteQtResourceImage.ReadPng(bytes, NteGlobalLocalization.GetArtworkPath(language)));
                NtePngImage expected = NtePngImage.Create(1920, 1080);
                expected.Draw(NtePngImage.Load(slogan), 160, 385, 438, 126, 1, TestContext.Current.CancellationToken);
                Assert.Equal(expected.Pixels, actual.Pixels);
                await File.WriteAllBytesAsync(Path.Combine(directory, "slogan-" + locale + ".png"), slogan, TestContext.Current.CancellationToken);
                await news.InitializeNewsAsync(TestContext.Current.CancellationToken);
                using PluginDisposableMemory<LauncherCarouselEntry> carousel = PluginDisposableMemoryExtension.ToManagedSpan<LauncherCarouselEntry>(news.GetCarouselEntries);
                using PluginDisposableMemory<LauncherNewsEntry> newsItems = PluginDisposableMemoryExtension.ToManagedSpan<LauncherNewsEntry>(news.GetNewsEntries);
                using PluginDisposableMemory<LauncherSocialMediaEntry> socials = PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(news.GetSocialMediaEntries);
                Assert.True(carousel.Length > 0);
                Assert.True(newsItems.Length > 0);
                Assert.True(socials.Length > 0);
                locales.Add(new { locale, backgrounds = backgrounds.Length, overlays = overlays.Length, carousel = carousel.Length, news = newsItems.Length, socials = socials.Length, titleSha256 = Convert.ToHexString(SHA256.HashData(slogan)) });
            }
            string json = JsonSerializer.Serialize(new { passed = true, fixture = true, manifest.Version, manager.AppId, manager.BranchName,
                resourceCount = manifest.Resources.Count, pakCount = manifest.Paks.Count, installBytes = manifest.GetTotalExtractedSize(),
                largestResource = manifest.Resources.Max(r => r.Filesize), sampleResource = resource.Filename, sampleRange = entry.Name,
                releaseHash, locales, gameInstalled = false, gameAcceptanceEvidence = "not-observed" }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), json, TestContext.Current.CancellationToken);
        }
        finally { plugin.SetPluginLocaleId("en-US"); installer.Dispose(); manager.Dispose(); }
    }
}
