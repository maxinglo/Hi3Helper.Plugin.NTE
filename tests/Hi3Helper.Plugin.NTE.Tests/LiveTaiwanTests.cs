using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Hi3Helper.Plugin.NTE.Management.Config;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LiveTaiwanTests
{
    [Fact(Explicit = true)]
    public async Task CurrentTaiwanCdnProvidesActivitiesNewsSocialImagesAndLocalizedTitle()
    {
        DirectoryInfo? repository=new(AppContext.BaseDirectory);
        while(repository!=null&&!File.Exists(Path.Combine(repository.FullName,"Hi3Helper.Plugin.NTE.slnx"))) repository=repository.Parent;
        Assert.NotNull(repository);
        string directory=Path.Combine(repository.FullName,"artifacts","research","live-taiwan-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(directory);
        using HttpClient client=new(new HttpClientHandler{AllowAutoRedirect=true,UseProxy=false}){Timeout=TimeSpan.FromSeconds(90)};
        NteCNLauncherApiNews news=new(client,Path.Combine(directory,"icons"),NteConfigProvider.Taiwan);
        NteCNLauncherApiMedia media=new(NteConfigProvider.Taiwan.LauncherVersionIniUrl,NteConfigProvider.Taiwan.LauncherPageUrl,client,Path.Combine(directory,"media"),()=>new NteAppearanceOptions{DynamicBackground=false},NteConfigProvider.Taiwan);
        try
        {
            await news.InitializeNewsAsync(TestContext.Current.CancellationToken);
            using var carousel=PluginDisposableMemoryExtension.ToManagedSpan<LauncherCarouselEntry>(news.GetCarouselEntries);
            using var entries=PluginDisposableMemoryExtension.ToManagedSpan<LauncherNewsEntry>(news.GetNewsEntries);
            using var socials=PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(news.GetSocialMediaEntries);
            Assert.True(carousel.Length>0,"Taiwan activities must come from the current CMS fragment."); Assert.True(entries.Length>0,"Taiwan news fragments must produce entries."); Assert.Equal(7,socials.Length);
            NtePngImage iconPreview=TestImages.Solid(7*64,128,[40,44,55,255]);
            for(int i=0;i<socials.Length;i++)
            {
                using LauncherSocialMediaEntry entry=socials[i]; Assert.True(File.Exists(entry.IconPath)); Assert.True(File.Exists(entry.IconHoverPath)); Assert.NotEqual(entry.IconPath,entry.IconHoverPath);
                var normal=NtePngImage.Load(await File.ReadAllBytesAsync(entry.IconPath!,TestContext.Current.CancellationToken)); var hover=NtePngImage.Load(await File.ReadAllBytesAsync(entry.IconHoverPath!,TestContext.Current.CancellationToken));
                iconPreview.Draw(normal,i*64,0,56,56,1,TestContext.Current.CancellationToken); iconPreview.Draw(hover,i*64,64,56,56,1,TestContext.Current.CancellationToken);
            }
            using(FileStream image=File.Create(Path.Combine(directory,"social-icons.png"))) iconPreview.Write(image);
            await media.InitializeMediaAsync(TestContext.Current.CancellationToken);
            using var backgrounds=PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetBackgroundEntries);
            using var overlays=PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(media.GetLogoOverlayEntries);
            Assert.True(backgrounds.Length>0); Assert.Equal(backgrounds.Length,overlays.Length);
            string pack=Assert.Single(Directory.GetFiles(Path.Combine(directory,"media"),"*.dat"));
            byte[] title=NteQtResourceImage.ReadPng(await File.ReadAllBytesAsync(pack,TestContext.Current.CancellationToken),"res/res/2000013/image_lang/zh-tw/main/slogan.png");
            await File.WriteAllBytesAsync(Path.Combine(directory,"taiwan-title.png"),title,TestContext.Current.CancellationToken);
            for(int i=0;i<overlays.Length;i++){using LauncherPathEntry overlay=overlays[i]; Assert.True(File.Exists(overlay.Path));}
            await File.WriteAllTextAsync(Path.Combine(directory,"result.json"),JsonSerializer.Serialize(new{passed=true,region="Taiwan",carouselCount=carousel.Length,newsCount=entries.Length,socialCount=socials.Length,backgroundCount=backgrounds.Length,overlayCount=overlays.Length,titleFromTaiwanResource=true,accountRequestsSent=false,gameAcceptanceEvidence="not-observed"},new JsonSerializerOptions{WriteIndented=true}),TestContext.Current.CancellationToken);
        }
        finally{media.Dispose();news.Dispose();}
    }
}
