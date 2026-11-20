using System.Security.Cryptography;

namespace Nte.Login;

internal sealed class NteCnLaunchException(string key) : InvalidOperationException(key)
{
    internal string Key { get; } = key;
}

// Each launcher/bridge pair has a separately observed local IPC contract.
// A game resource version alone cannot identify the installed launcher SDK.
internal sealed record NteCnSdkCompatibility(string LauncherSha256, string BridgeSha256, NteSdkProtocol Protocol)
{
    // PC service protocol, independent of the bridge's local IPC version.
    internal const string ServiceSdkVersion = "5.159.0";
    internal string ProtocolVersion => Protocol.Version;
    internal NteHostRegion Region { get; private init; } = NteHostRegion.CN;
    internal string ConfigurationName => Region == NteHostRegion.Bilibili
        ? "oneSdkConfig_bilibili.config" : NteSdkRegistration.CnConfigurationName;
    internal string? InstalledLauncherDirectory { get; private init; }

    private static readonly NteCnSdkCompatibility[] Profiles =
    [
        new("3CF58ECBE4CD33A83E741C12437CA9BB67678D166548C5972579C1266F250D9F",
            "4101B035406BECDA3EACBF0B2DABD7F43D9DF3F9294906706FC504623E7CD13B", NteSdkProtocol.Cn208),
        // Official launcher 1.0.7.1102 (ce6bd117), verified 2026-11-05.
        new("AF1C6D0DFF6B466E9ED0E1E1565DC26F72C4A1149151A53E749832226AB4FB1F",
            "AC9D9551810264594082BBD1704BCA588220DA087F4F8FB8600C41F0135E38C4", NteSdkProtocol.Cn209),
        new("57EB32714731CA58639D0087375410CFBA8393248D924526ADB779A695A940F1",
            "AC9D9551810264594082BBD1704BCA588220DA087F4F8FB8600C41F0135E38C4", NteSdkProtocol.Cn209)
        { Region = NteHostRegion.Bilibili }
    ];

    internal static NteCnSdkCompatibility Resolve(string launcherSha256, string bridgeSha256)
        => Profiles.SingleOrDefault(p => p.LauncherSha256.Equals(launcherSha256, StringComparison.OrdinalIgnoreCase) &&
            p.BridgeSha256.Equals(bridgeSha256, StringComparison.OrdinalIgnoreCase)) ?? throw new NteCnLaunchException("CnUnsupportedSdk");

    internal static Task<(NteCnSdkCompatibility Profile, string BridgePath)> LoadAsync(string gameRoot)
        => LoadAsync(gameRoot, NteHostRegion.CN);

    internal static async Task<(NteCnSdkCompatibility Profile, string BridgePath)> LoadAsync(string gameRoot,
        NteHostRegion region, string? installedLauncherDirectory = null)
    {
        if (region is not (NteHostRegion.CN or NteHostRegion.Bilibili) ||
            (installedLauncherDirectory != null && region != NteHostRegion.Bilibili))
            throw new NteCnLaunchException("CnUnsupportedSdk");

        NteRegionDefinition definition = NteRegionDefinition.From(region);
        string executable = Path.Combine(gameRoot, NteRegionDefinition.GameExecutableRelativePath);
        string launcherDirectory = Path.GetFullPath(installedLauncherDirectory ?? definition.LauncherDirectoryFor(gameRoot),
            Path.GetFullPath(gameRoot));
        string launcher = Path.Combine(launcherDirectory, definition.LauncherExecutable);
        foreach (string file in new[] { executable, launcher })
            if (!File.Exists(file)) throw new NteCnLaunchException("CnFilesMissing");

        const string bridgeName = "WPGameClientSDK_64.dll";
        string bridge = Path.Combine(launcherDirectory, bridgeName);
        if (region == NteHostRegion.CN && !File.Exists(bridge))
        {
            string pointer = Path.Combine(Path.GetDirectoryName(executable)!, "GameLauncher.config");
            if (!File.Exists(pointer) || new FileInfo(pointer).Length > 32768)
                throw new NteCnLaunchException("CnFilesMissing");
            bridge = Path.GetFullPath(File.ReadAllText(pointer).Trim());
        }
        if (!Path.GetFileName(bridge).Equals(bridgeName, StringComparison.OrdinalIgnoreCase) || !File.Exists(bridge))
            throw new NteCnLaunchException("CnFilesMissing");
        NteCnSdkCompatibility profile = Resolve(await HashAsync(launcher), await HashAsync(bridge));
        if (profile.Region != region) throw new NteCnLaunchException("CnUnsupportedSdk");
        return (profile with { InstalledLauncherDirectory = launcherDirectory }, bridge);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
