using Nte.LoginHost;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class PatcherLaunchEnvironmentTests
{
    [Theory]
    [InlineData("NTELauncher", "1289")]
    [InlineData("NTEGlobal", "3000001")]
    [InlineData("NTETW", "2000013")]
    public void MatchingInstalledConfigurationProvidesTheObservedPublicPaths(string launcherDirectoryName,
        string applicationId)
    {
        string root = Path.Combine(Path.GetTempPath(), "NtePatcher-路径-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, launcherDirectoryName, "ResFilesM", applicationId, "PatcherConfig");
        string userData = Path.Combine(root, launcherDirectoryName, "UserData");
        try
        {
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(userData);
            File.WriteAllText(Path.Combine(config, "PatcherConfig.json"), "{\"appId\":\"" + applicationId + "\"}");
            File.WriteAllText(Path.Combine(config, "PatcherPath.json"), "{}");
            Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
            {
                ["_PATCHER_CONFIG_PATH_"] = "stale",
                ["_PATCHER_RES_PATH_"] = root
            };

            Assert.True(PatcherLaunchEnvironment.TryApply(environment, root, applicationId, launcherDirectoryName));
            Assert.Equal(config + Path.DirectorySeparatorChar, environment["_PATCHER_CONFIG_PATH_"]);
            Assert.Equal(userData + Path.DirectorySeparatorChar, environment["_PATCHER_USERDATA_PATH_"]);
            Assert.Equal(Path.Combine(userData, "Log", "Patcher", "log", "Game"), environment["_PATCHER_LOG_PATH_"]);
            Assert.Equal(root, environment["_PATCHER_RES_PATH_"]);
            Assert.False(Directory.Exists(environment["_PATCHER_LOG_PATH_"]));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("NTEGlobal", "1289")]
    [InlineData("NTELauncher", "3000001")]
    [InlineData("../NTEGlobal", "3000001")]
    public void GlobalPatcherNeverFallsBackToAnotherRegionOrDirectory(string directory, string applicationId)
    {
        string root = Path.Combine(Path.GetTempPath(), "NtePatcher-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "NTEGlobal", "ResFilesM", "3000001", "PatcherConfig");
        try
        {
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(Path.Combine(root, "NTEGlobal", "UserData"));
            File.WriteAllText(Path.Combine(config, "PatcherConfig.json"), "{\"appId\":\"3000001\"}");
            File.WriteAllText(Path.Combine(config, "PatcherPath.json"), "{}");
            Dictionary<string, string?> environment = new()
            {
                ["_PATCHER_CONFIG_PATH_"] = "stale", ["_PATCHER_USERDATA_PATH_"] = "stale",
                ["_PATCHER_LOG_PATH_"] = "stale"
            };
            Assert.False(PatcherLaunchEnvironment.TryApply(environment, root, applicationId, directory));
            Assert.Empty(environment);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("{\"appId\":\"1290\"}", "1289")]
    [InlineData("{\"appId\":\"1289\"}", "../1289")]
    [InlineData("{broken", "1289")]
    [InlineData("{}", "1289")]
    public void UnrecognizedConfigurationDoesNotProvideStaleOrGuessedPaths(string json, string applicationId)
    {
        string root = Path.Combine(Path.GetTempPath(), "NtePatcher-" + Guid.NewGuid().ToString("N"));
        string config = Path.Combine(root, "NTELauncher", "ResFilesM", "1289", "PatcherConfig");
        try
        {
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(Path.Combine(root, "NTELauncher", "UserData"));
            File.WriteAllText(Path.Combine(config, "PatcherConfig.json"), json);
            File.WriteAllText(Path.Combine(config, "PatcherPath.json"), "{}");
            Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase)
            {
                ["_PATCHER_CONFIG_PATH_"] = "stale",
                ["_PATCHER_USERDATA_PATH_"] = "stale",
                ["_PATCHER_LOG_PATH_"] = "stale",
                ["_PATCHER_RES_PATH_"] = root
            };

            Assert.False(PatcherLaunchEnvironment.TryApply(environment, root, applicationId));
            Assert.DoesNotContain("_PATCHER_CONFIG_PATH_", environment.Keys);
            Assert.DoesNotContain("_PATCHER_USERDATA_PATH_", environment.Keys);
            Assert.DoesNotContain("_PATCHER_LOG_PATH_", environment.Keys);
            Assert.Equal(root, environment["_PATCHER_RES_PATH_"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
