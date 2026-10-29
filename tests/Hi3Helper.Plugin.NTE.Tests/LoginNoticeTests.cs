using Nte.Login;
using Hi3Helper.Plugin.NTE.Management.Config;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LoginNoticeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteNoticeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void OnlyExplicitAcknowledgementPersistsAndMalformedOrOldNoticeIsShownAgain()
    {
        string path = Path.Combine(root, "notice.json");
        NteLoginNotice notice = new(path);
        Assert.False(notice.IsAcknowledged());
        string settings = Path.Combine(root, "launch.json");
        NteLaunchPreferences.Update(settings, officialLauncher: true);
        Assert.False(notice.IsAcknowledged());
        notice.Acknowledge();
        Assert.True(new NteLoginNotice(path).IsAcknowledged());
        foreach (string invalid in new[] { "{", "[]", "{\"acknowledgedRevision\":0}", "{\"acknowledgedRevision\":\"1\"}" })
        {
            File.WriteAllText(path, invalid);
            Assert.False(notice.IsAcknowledged());
        }
    }

    [Fact]
    public void OfficialChoiceAndGraphicsUpdatesPreserveEachOtherAndRemainPerRegion()
    {
        string cn = Path.Combine(root, "cn", "launch.json"), global = Path.Combine(root, "global", "launch.json");
        NteLaunchSettings settings = new(cn);
        settings.SetValue("dx11");
        settings.Apply();
        _ = settings.GetSection(); // A settings page can already be open during the switch.
        NteLaunchPreferences.Update(cn, officialLauncher: true);
        settings.Apply();
        Assert.True(settings.UseOfficialLauncher);
        Assert.Equal("dx11", settings.Load());
        Assert.Equal(("default", false), NteLaunchPreferences.Read(global));
        settings.SetOfficialLauncher(false);
        settings.Apply();
        Assert.False(settings.UseOfficialLauncher);
        Assert.Equal("dx11", settings.Load());
        File.WriteAllText(cn, "{\"graphicsApi\":\"dx12\"}");
        Assert.Equal(("dx12", false), NteLaunchPreferences.Read(cn));
    }

    [Theory]
    [InlineData(false, "NTELauncher", "NTEGame.exe")]
    [InlineData(true, "NTEGlobal", "NTEGlobalGame.exe")]
    public void OfficialLaunchRequiresTheSelectedInstallAndKeepsArgumentsOutsideAShell(bool global, string directory, string file)
    {
        Assert.Throws<FileNotFoundException>(() => NteOfficialLaunchPlan.Create(root, global));
        string platform = Path.Combine(root, directory);
        Directory.CreateDirectory(platform);
        File.WriteAllBytes(Path.Combine(platform, file), []);
        var plan = NteOfficialLaunchPlan.Create(root, global);
        var start = plan.CreateStartInfo("-dx11 -log=\"a b&c\"");
        Assert.Equal(Path.Combine(platform, file), start.FileName);
        Assert.Equal(platform, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal("/launcher /directly /autoplay -dx11 -log=\"a b&c\"", start.Arguments);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
