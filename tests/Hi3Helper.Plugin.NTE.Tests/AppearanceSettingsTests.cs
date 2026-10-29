using Hi3Helper.Plugin.NTE.Management.Config;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class AppearanceSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NteAppearanceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SettingsPageEdits_PersistOnlyAfterApply()
    {
        string path = Path.Combine(_directory, "appearance.json");
        NteAppearanceStore store = new(path);
        var page = store.GetPage();
        Assert.Contains(page.Sections.SelectMany(section => section.Entries), entry => entry.Key == "element.slogan.width");
        store.SetValue("dynamicBackground", "False");
        store.SetValue("element.slogan.x", "210.5");
        store.SetValue("element.slogan.width", "400");
        Assert.False(File.Exists(path));
        store.Apply();
        NteAppearanceOptions reloaded = new NteAppearanceStore(path).Load();
        Assert.False(reloaded.DynamicBackground);
        Assert.Equal(210.5, reloaded.Elements.Single(item => item.Id == "slogan").X);
        Assert.Equal(400, reloaded.Elements.Single(item => item.Id == "slogan").Width);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void InvalidDimensions_DoNotOverwriteSavedSettings()
    {
        NteAppearanceStore store = new(Path.Combine(_directory, "appearance.json"));
        store.Apply();
        store.SetValue("canvasWidth", "999999");
        Assert.Throws<InvalidDataException>(store.Apply);
        Assert.Equal(1920, new NteAppearanceStore(Path.Combine(_directory, "appearance.json")).Load().CanvasWidth);
    }

    [Fact]
    public void UnknownOrNonFiniteValues_AreRejected()
    {
        NteAppearanceStore store = new(Path.Combine(_directory, "appearance.json"));
        Assert.Throws<ArgumentException>(() => store.SetValue("unrecognized", "true"));
        Assert.Throws<ArgumentException>(() => store.SetValue("element.slogan.x", "NaN"));
    }

    [Fact]
    public void MalformedConfig_FallsBackWithoutDestroyingUserFile()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "appearance.json");
        File.WriteAllText(path, "invalid JSON");
        Assert.True(new NteAppearanceStore(path).Load().OverlayEnabled);
        Assert.Equal("invalid JSON", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
