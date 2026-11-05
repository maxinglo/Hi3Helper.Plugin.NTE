using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class CnSdkCompatibilityTests
{
    private const string OldLauncher = "3CF58ECBE4CD33A83E741C12437CA9BB67678D166548C5972579C1266F250D9F";
    private const string OldBridge = "4101B035406BECDA3EACBF0B2DABD7F43D9DF3F9294906706FC504623E7CD13B";
    private const string NewLauncher = "AF1C6D0DFF6B466E9ED0E1E1565DC26F72C4A1149151A53E749832226AB4FB1F";
    private const string NewBridge = "AC9D9551810264594082BBD1704BCA588220DA087F4F8FB8600C41F0135E38C4";

    [Fact]
    public void VerifiedPairs_SelectTheirObservedProtocolVersion()
    {
        Assert.Equal("2.0.8.0", NteCnSdkCompatibility.Resolve(OldLauncher, OldBridge).ProtocolVersion);
        Assert.Equal("2.0.9.0", NteCnSdkCompatibility.Resolve(NewLauncher.ToLowerInvariant(), NewBridge).ProtocolVersion);
        Assert.Same(NteSdkProtocol.Cn208, NteCnSdkCompatibility.Resolve(OldLauncher, OldBridge).Protocol);
        Assert.Same(NteSdkProtocol.Cn209, NteCnSdkCompatibility.Resolve(NewLauncher, NewBridge).Protocol);
    }

    [Theory]
    [InlineData(OldLauncher, NewBridge)]
    [InlineData(NewLauncher, OldBridge)]
    [InlineData(NewLauncher, "unknown")]
    [InlineData("unknown", NewBridge)]
    public void UnknownOrMixedImages_AreRejectedWithAnActionableFailure(string launcher, string bridge)
        => Assert.Equal("CnUnsupportedSdk", Assert.Throws<NteCnLaunchException>(() => NteCnSdkCompatibility.Resolve(launcher, bridge)).Key);

    [Fact]
    public async Task MissingInstallation_IsReportedAsMissingFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "NteMissingSdk", Guid.NewGuid().ToString("N"));
        Assert.Equal("CnFilesMissing", (await Assert.ThrowsAsync<NteCnLaunchException>(() => NteCnSdkCompatibility.LoadAsync(root))).Key);
    }

    [Fact]
    public void FailureReport_DoesNotPersistExceptionTextOrData()
    {
        string root = Path.Combine(Path.GetTempPath(), "NteStartupDiagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            Exception error = new InvalidDataException("private-account@example.invalid token=private-token");
            error.Data["credential"] = "private-secret";
            string path = Path.Combine(root, "startup.json");
            NteHostStartupDiagnostics.TryWrite(path, error, "CnUnsupportedSdk");
            string report = File.ReadAllText(path);
            Assert.Contains("CnUnsupportedSdk", report);
            Assert.Contains("InvalidDataException", report);
            Assert.DoesNotContain("private-", report);
            Assert.DoesNotContain("credential", report);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
