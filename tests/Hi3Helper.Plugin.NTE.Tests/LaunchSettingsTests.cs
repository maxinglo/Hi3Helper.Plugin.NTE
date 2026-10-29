using Hi3Helper.Plugin.NTE.Management.Config;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LaunchSettingsTests
{
    [Theory]
    [InlineData(null, "dx11", "-dx11")]
    [InlineData("-dx12 -windowed", "dx11", "-windowed -dx11")]
    [InlineData("-D3D11 -dx11 -log", "dx12", "-log -dx12")]
    [InlineData("-log -dx12", "default", "-log -dx12")]
    [InlineData("-log=\"C:/a b/file.txt\" -dx12", "dx11", "-log=\"C:/a b/file.txt\" -dx11")]
    public void GraphicsSelection_ReplacesConflictingFlagsAndPreservesOtherArguments(string? input, string api, string expected)
    {
        string? arguments = NteLaunchSettings.ApplyGraphicsApi(input, api);
        Assert.Equal(expected, arguments);
        NteHostLaunchOptions host = new(Path.GetTempPath(), arguments);
        Assert.StartsWith(NteHostLaunchOptions.RequiredGameArguments, host.GameArguments);
        Assert.EndsWith(expected, NteHostLaunchOptions.Parse(host.ToArguments()).GameArguments);
    }

    [Fact]
    public void GraphicsSelection_PersistsPerProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "NteLaunchSettingsTests", Guid.NewGuid().ToString("N"));
        try
        {
            NteLaunchSettings settings = new(Path.Combine(root, "launch.json"));
            Assert.Equal("default", settings.Load());
            settings.SetValue("dx11"); settings.Apply();
            Assert.Equal("-dx11", settings.BuildArguments(null));
            Assert.Throws<ArgumentException>(() => settings.SetValue("unknown"));
            Assert.NotEqual(NteLaunchSettings.GetPath("NteCN"), NteLaunchSettings.GetPath("NteGlobal"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
