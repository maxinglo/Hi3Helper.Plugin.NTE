using Hi3Helper.Plugin.NTE.Management.Config;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LoginSettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteLoginSettings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void HelloIsExperimentalOffByDefaultAndChangesOnlyAfterLauncherApply()
    {
        string path = Path.Combine(root, "login.json");
        NteLoginSettings settings = new(path);
        var entry = Assert.Single(settings.GetSection().Entries);
        Assert.Equal(NteLoginSettings.HelloKey, entry.Key);
        Assert.Equal(bool.FalseString, entry.Value);
        settings.SetHello(true);
        Assert.False(File.Exists(path));
        Assert.False(NteLoginPreferences.ReadExperimentalHello(path));
        settings.Apply();
        Assert.True(NteLoginPreferences.ReadExperimentalHello(path));
        settings.SetHello(false);
        settings.Apply();
        Assert.False(NteLoginPreferences.ReadExperimentalHello(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void RegionsKeepIndependentHelloPreferencesAndBothDefaultOff()
    {
        Assert.NotEqual(NteLoginPreferences.DefaultPath, NteLoginPreferences.GlobalPath);
        Assert.Equal(NteLoginPreferences.DefaultPath, NteLoginPreferences.GetPath(false));
        Assert.Equal(NteLoginPreferences.GlobalPath, NteLoginPreferences.GetPath(true));
        string cnPath = Path.Combine(root, "login.json"), globalPath = Path.Combine(root, "Global", "login.json");
        NteLoginSettings cn = new(cnPath), global = new(globalPath);
        Assert.Equal(bool.FalseString, Assert.Single(cn.GetSection().Entries).Value);
        Assert.Equal(bool.FalseString, Assert.Single(global.GetSection().Entries).Value);
        cn.SetHello(true);
        cn.Apply();
        Assert.True(NteLoginPreferences.ReadExperimentalHello(cnPath));
        Assert.False(NteLoginPreferences.ReadExperimentalHello(globalPath));
        global.SetHello(true);
        global.Apply();
        cn.SetHello(false);
        cn.Apply();
        Assert.False(NteLoginPreferences.ReadExperimentalHello(cnPath));
        Assert.True(NteLoginPreferences.ReadExperimentalHello(globalPath));
    }

    [Fact]
    public void UnknownPreferenceFormatDoesNotSilentlyDisableAProtectedPolicy()
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "login.json");
        File.WriteAllText(path, "{\"format\":99,\"experimentalWindowsHello\":true}");
        Assert.Throws<InvalidDataException>(() => NteLoginPreferences.ReadExperimentalHello(path));
        Assert.Contains("99", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
