using System.Diagnostics;
using System.Text;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalLoginLaunchTests
{
    private const string FixtureDeviceId = "0123456789abcdef0123456789abcdef";

    private static Dictionary<string, string> PlatformEnvironment() => new()
    {
        ["ArcGameAppId"] = "3000001", ["ArcProcessId"] = "12345", ["ArcMoblieNotify"] = "49152",
        ["anchor_w"] = "100", ["anchor_v"] = "200", ["ArcMId"] = "Local.Fixture.Map",
        ["ArcMMId"] = "Local.Fixture.Mutex"
    };

    [Fact]
    public void ExplicitGlobalRegionSurvivesHostHandoffAndOldCallsRemainMainland()
    {
        NteHostLaunchOptions request = new(Path.GetFullPath("global fixture"), "-dx11 -windowed", true,
            ProcessPriorityClass.AboveNormal, NteHostRegion.Global);
        Assert.Equal(request, NteHostLaunchOptions.Parse(request.ToArguments()));
        Assert.Equal(NteHostRegion.CN, NteHostLaunchOptions.Parse(["--game-root", request.GameRoot]).Region);
        foreach (string value in new[] { "Unknown", "1", "global", "" })
            Assert.Throws<ArgumentException>(() =>
                NteHostLaunchOptions.Parse(["--game-root", request.GameRoot, "--region", value]));
    }

    [Fact]
    public void GlobalLaunchUsesTheSelectedInstallAndPreservesArgumentsWithoutAShell()
    {
        using Installation game = new();
        const string arguments = "-dx11 -windowed -some-value=\"a b&c\"";
        NteHostLaunchOptions options = new(game.Root, arguments, Region: NteHostRegion.Global);
        NteOverseasLaunchPlan plan = NteOverseasLaunchPlan.Create(options);
        ProcessStartInfo start =
            plan.CreateStartInfo("Collapse.NTE.Global.Test", FixtureDeviceId, PlatformEnvironment());
        Assert.Equal(plan.GameExecutable, start.FileName);
        Assert.Equal(Path.GetFullPath(Path.Combine(game.Root, "Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe")),
            plan.GameExecutable);
        Assert.Equal(Path.Combine(game.Root, "Client"), start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.Equal(NteHostLaunchOptions.RequiredGameArguments + " --saveddirsuffix=Global " + arguments,
            start.Arguments);
        Assert.Contains(arguments, start.Arguments);
        Assert.Empty(start.ArgumentList);
        Assert.Equal(Path.Combine(game.Root, "NTEGlobal", "WPGameClientSDK"), start.Environment["ArcGameSDKSharePath"]);
        Assert.Equal(game.Root, start.Environment["_PATCHER_RES_PATH_"]);
        Assert.Equal("Collapse.NTE.Global.Test", start.Environment["ArcGameP"]);
        Assert.Equal(FixtureDeviceId, start.Environment["ArcDummyMoblieDeviceId"]);
        Assert.Equal("2", start.Environment["ArcGameMobileSdkType"]);
        foreach ((string key, string value) in PlatformEnvironment()) Assert.Equal(value, start.Environment[key]);
        Assert.Equal("3000001", start.Environment["ArcDataIndex"]);
        Assert.False(start.Environment.ContainsKey("ArcGameAppAbbr"));
        Assert.Equal("4", start.Environment["channelId"]);
        Assert.Equal("11", start.Environment["subChannelId"]);
        Assert.Equal("11", start.Environment["mediaId"]);
        foreach (string name in new[] { "_PATCHER_CONFIG_PATH_", "_PATCHER_USERDATA_PATH_", "_PATCHER_LOG_PATH_" })
            Assert.False(start.Environment.ContainsKey(name));
        Assert.DoesNotContain(FixtureDeviceId, start.Arguments);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    [Fact]
    public void OnlyTheGlobalPlanSelectsTheGlobalSavedDirectory()
    {
        using Installation game = new();
        var global = NteOverseasLaunchPlan.Create(new(game.Root, Region: NteHostRegion.Global));
        Assert.Equal(NteHostLaunchOptions.RequiredGameArguments + " --saveddirsuffix=Global", global.GameArguments);
        Assert.Equal(NteHostLaunchOptions.RequiredGameArguments, new NteHostLaunchOptions(game.Root).GameArguments);
    }

    [Theory]
    [InlineData("ArcGameAppId", "1289")]
    [InlineData("ArcMId", "")]
    [InlineData("ArcProcessId", null)]
    public void AnIncompleteOrMainlandPlatformContextCannotStartGlobal(string key, string? value)
    {
        using Installation game = new();
        var context = PlatformEnvironment();
        if (value == null) context.Remove(key);
        else context[key] = value;
        var plan = NteOverseasLaunchPlan.Create(new(game.Root, Region: NteHostRegion.Global));
        Assert.Throws<ArgumentException>(() =>
            plan.CreateStartInfo("Collapse.NTE.Global.Test", FixtureDeviceId, context));
    }

    [Theory]
    [InlineData("--saveddirsuffix=CN")]
    [InlineData("-SavedDirSuffix=\"Other Region\"")]
    [InlineData("\"--saveddirsuffix=CN\"")]
    [InlineData("--saveddirsuffix Global")]
    [InlineData("--enginesaveddirsuffix=Other")]
    public void ConflictingSavedDirectoryOptionsCannotMixRegionalConfiguration(string arguments)
    {
        using Installation game = new();
        var error = Assert.Throws<NteGlobalLaunchException>(() =>
            NteOverseasLaunchPlan.Create(new(game.Root, arguments, Region: NteHostRegion.Global)));
        Assert.Equal("GlobalSaveDirectoryConflict", error.Key);
    }

    [Theory]
    [InlineData("--saveddirsuffix=Global -dx11")]
    [InlineData("-SavedDirSuffix=\"Global\" -dx12")]
    public void AnExplicitMatchingSavedDirectoryOptionRemainsCompatible(string arguments)
    {
        using Installation game = new();
        var plan = NteOverseasLaunchPlan.Create(new(game.Root, arguments, Region: NteHostRegion.Global));
        Assert.EndsWith(arguments, plan.GameArguments);
    }

    [Fact]
    public void WrongRegionAndIncompleteComponentsFailBeforeCreatingUserSettings()
    {
        using Installation game = new();
        string settings = Path.Combine(game.Root, "NTEGlobal/UserData/Config/Config.ini");
        File.WriteAllText(Path.Combine(game.Root, ".collapse_nte_config"), "AppId=1289\nGameVersion=1.0.14\n");
        Assert.Throws<NteGlobalLaunchException>(() =>
            NteOverseasLaunchPlan.Create(new(game.Root, Region: NteHostRegion.Global)));
        Assert.False(File.Exists(settings));
        File.Delete(Path.Combine(game.Root, ".collapse_nte_config"));
        File.WriteAllText(Path.Combine(game.Root, "NTEGlobal/Config/Config.ini"), "[General]\nGameID=1289\n");
        Assert.Throws<NteGlobalLaunchException>(() =>
            NteOverseasLaunchPlan.Create(new(game.Root, Region: NteHostRegion.Global)));
        File.WriteAllText(Path.Combine(game.Root, "NTEGlobal/Config/Config.ini"), "[General]\nGameID=3000001\n");
        File.Delete(Path.Combine(game.Root, "NTEGlobal/WPGameClientSDK_64.dll"));
        Assert.Throws<NteGlobalLaunchException>(() =>
            NteOverseasLaunchPlan.Create(new(game.Root, Region: NteHostRegion.Global)));
        Assert.False(File.Exists(settings));
    }

    [Fact]
    public void DirectLaunchDoesNotRequireTheOfficialBrowserOrWriteLauncherPreferences()
    {
        using Installation game = new();
        string settings = Path.Combine(game.Root, "NTEGlobal/UserData/Config/Config.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        byte[] previous = Encoding.Unicode.GetBytes("[Setting]\r\nwebLoginBySystem=0\r\nDx11=0\r\n");
        File.WriteAllBytes(settings, previous);
        NteOverseasLaunchPlan plan = NteOverseasLaunchPlan.Create(new(game.Root, "-dx11", Region: NteHostRegion.Global));
        Assert.EndsWith("HTGame.exe",
            plan.CreateStartInfo("Collapse.NTE.Global.Test", FixtureDeviceId, PlatformEnvironment()).FileName);
        Assert.Equal(previous, File.ReadAllBytes(settings));
        Assert.False(File.Exists(Path.Combine(game.Root, "NTEGlobal/GlobalLoginHelper.exe")));
        Assert.False(File.Exists(Path.Combine(game.Root, "NTEGlobal/NTEGlobalBrowser.exe")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledRegionCanBeReadFromUnicodeConfiguration(bool bigEndian)
    {
        using Installation game = new();
        File.WriteAllText(Path.Combine(game.Root, "NTEGlobal/Config/Config.ini"),
            "[General]\r\nGameID=3000001\r\n", new UnicodeEncoding(bigEndian, true));
        Assert.Equal(game.Root, NteOverseasLaunchPlan.Create(new(game.Root, Region: NteHostRegion.Global)).GameRoot);
    }

    private sealed class Installation : IDisposable
    {
        internal string Root { get; } =
            Path.Combine(Path.GetTempPath(), "NteGlobalLaunch-" + Guid.NewGuid().ToString("N"));

        internal Installation()
        {
            foreach (string file in new[]
                     {
                         "NTEGlobal/NTEGlobalGame.exe", "NTEGlobal/WPGameClientSDK_64.dll",
                         "NTEGlobal/ResData/3000001.dat",
                         "Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe",
                         "Client/WindowsNoEditor/HT/Binaries/Win64/WPGlobalSDK_64.dll"
                     })
            {
                string target = Path.Combine(Root, file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, "fixture");
            }

            Directory.CreateDirectory(Path.Combine(Root, "NTEGlobal/Config"));
            File.WriteAllText(Path.Combine(Root, "NTEGlobal/Config/Config.ini"), "[General]\nGameID=3000001\n");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
