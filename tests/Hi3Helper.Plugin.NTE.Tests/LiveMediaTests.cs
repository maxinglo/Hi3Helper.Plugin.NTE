using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LiveMediaTests
{
    [Fact(Explicit = true)]
    public async Task CurrentCdn_ProducesVideoDecorationsAndCroppedSocialIcons()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "Hi3Helper.Plugin.NTE.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        string directory = Path.Combine(repository.FullName, "artifacts", "research", "live-media-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        NteCNLauncherApiMedia media = new(NteConfigProvider.LauncherVersionIniUrl, NteConfigProvider.LauncherHtmlUrl,
            new HttpClient(), Path.Combine(directory, "cache"), () => new NteAppearanceOptions());
        NteCNLauncherApiNews news = new(new HttpClient(), Path.Combine(directory, "icons"));
        try
        {
            await media.InitializeMediaAsync(TestContext.Current.CancellationToken);
            using PluginDisposableMemory<LauncherPathEntry> backgrounds =
                PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetBackgroundEntries);
            using PluginDisposableMemory<LauncherPathEntry> overlays =
                PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetLogoOverlayEntries);
            Assert.True(backgrounds.Length >= 2, "The live manifest should expose a video and its static fallback.");
            Assert.Equal(backgrounds.Length, overlays.Length);
            string? staticImage = null;
            string? overlay = null;
            for (int i = 0; i < backgrounds.Length; i++)
            {
                using LauncherPathEntry background = backgrounds[i];
                using LauncherPathEntry decoration = overlays[i];
                string path = Assert.IsType<string>(background.Path);
                Assert.True(Path.IsPathFullyQualified(path));
                await using FileStream file = File.OpenRead(path);
                byte[] signature = new byte[8];
                await file.ReadExactlyAsync(signature, TestContext.Current.CancellationToken);
                if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    Assert.Equal("ftyp"u8.ToArray(), signature[4..8]);
                else
                {
                    Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, signature);
                    staticImage = path;
                }
                overlay = Assert.IsType<string>(decoration.Path);
                Assert.True(File.Exists(overlay));
            }
            Assert.NotNull(staticImage);
            Assert.NotNull(overlay);
            NtePngImage preview = NtePngImage.Load(await File.ReadAllBytesAsync(staticImage, TestContext.Current.CancellationToken));
            NtePngImage foreground = NtePngImage.Load(await File.ReadAllBytesAsync(overlay, TestContext.Current.CancellationToken));
            string packPath = Assert.Single(Directory.GetFiles(Path.Combine(directory, "cache"), "*.dat"));
            byte[] title = NteQtResourceImage.ReadPng(await File.ReadAllBytesAsync(packPath, TestContext.Current.CancellationToken), "res/res/1289/main/slogan.png");
            await File.WriteAllBytesAsync(Path.Combine(directory, "native-slogan.png"), title, TestContext.Current.CancellationToken);
            NtePngImage expectedOverlay = NtePngImage.Create(1920, 1080);
            expectedOverlay.Draw(NtePngImage.Load(title), 160, 385, 438, 126, 1, TestContext.Current.CancellationToken);
            Assert.Equal(expectedOverlay.Pixels, foreground.Pixels);
            preview.Draw(foreground, 0, 0, preview.Width, preview.Height, 1, TestContext.Current.CancellationToken);
            using (FileStream output = File.Create(Path.Combine(directory, "background-with-overlay.png")))
                preview.Write(output);

            await news.InitializeNewsAsync(TestContext.Current.CancellationToken);
            using PluginDisposableMemory<LauncherSocialMediaEntry> socials =
                PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(news.GetSocialMediaEntries);
            Assert.Equal(7, socials.Length);
            NtePngImage iconsPreview = TestImages.Solid(socials.Length * 64, 128, [40, 44, 55, 255]);
            for (int i = 0; i < socials.Length; i++)
            {
                using LauncherSocialMediaEntry entry = socials[i];
                Assert.NotEqual(entry.IconPath, entry.IconHoverPath);
                NtePngImage normal = NtePngImage.Load(await File.ReadAllBytesAsync(entry.IconPath!, TestContext.Current.CancellationToken));
                NtePngImage hover = NtePngImage.Load(await File.ReadAllBytesAsync(entry.IconHoverPath!, TestContext.Current.CancellationToken));
                Assert.Equal((35, 42), (normal.Width, normal.Height));
                Assert.Equal((35, 42), (hover.Width, hover.Height));
                iconsPreview.Draw(normal, i * 64 + 14, 8, 35, 42, 1, TestContext.Current.CancellationToken);
                iconsPreview.Draw(hover, i * 64 + 14, 72, 35, 42, 1, TestContext.Current.CancellationToken);
            }
            using FileStream iconOutput = File.Create(Path.Combine(directory, "social-icons-preview.png"));
            iconsPreview.Write(iconOutput);
        }
        finally
        {
            news.Dispose();
            media.Dispose();
        }
    }
}
