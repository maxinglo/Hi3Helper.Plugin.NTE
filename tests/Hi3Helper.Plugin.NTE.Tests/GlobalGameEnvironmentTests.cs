using System.Diagnostics;
using System.Text;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalGameEnvironmentTests
{
    [Fact]
    public void InstalledGlobalBootstrapReplacesInheritedRegionAndStaysOutOfArguments()
    {
        const string fixtureKey = "synthetic-application-material";
        using var configuration = new NteGlobalApplicationConfiguration(Encoding.UTF8.GetBytes(fixtureKey),
            "com.hottagames.nte", new("https://mapi.pwsdk.com"), "global-sg",
            new Dictionary<string, string> { ["chatCoreDomain"] = "fixture.invalid" });
        ProcessStartInfo start = new("fixture.exe", "-dx11");
        start.Environment["GAME_APPID"] = "1289";
        start.Environment["GAME_AREA"] = "cn";
        start.Environment["GAME_APPKEY"] = "old-synthetic-material";
        start.Environment["chatTCPDomain"] = "old.invalid";
        NteGlobalGameEnvironment.Apply(start, configuration);
        Assert.Equal("3000001", start.Environment["GAME_APPID"]);
        Assert.Equal("global-sg", start.Environment["GAME_AREA"]);
        Assert.Equal(fixtureKey, start.Environment["GAME_APPKEY"]);
        Assert.Equal("fixture.invalid", start.Environment["chatCoreDomain"]);
        Assert.False(start.Environment.ContainsKey("chatTCPDomain"));
        Assert.DoesNotContain(fixtureKey, start.Arguments);
        Assert.DoesNotContain(fixtureKey, configuration.ToString());
        NteGlobalGameEnvironment.Clear(start);
        Assert.All(NteGlobalGameEnvironment.Keys, key => Assert.False(start.Environment.ContainsKey(key)));
        Assert.Equal("-dx11", start.Arguments);
    }
}
