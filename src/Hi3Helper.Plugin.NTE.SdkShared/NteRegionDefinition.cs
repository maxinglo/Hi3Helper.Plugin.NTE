using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nte.Login;

// Public installation identity shared by the native plugin and LoginHost.
// SDK image/layout profiles are kept separately from these stable identities.
internal sealed class NteRegionDefinition
{
    internal const string CnAppId = "1289", GlobalAppId = "3000001", TaiwanAppId = "2000013";
    internal const string BilibiliProfileName = "NteBilibili";
    internal const string CnLauncherDirectory = "NTELauncher", GlobalLauncherDirectory = "NTEGlobal", TaiwanLauncherDirectory = "NTETW";
    internal const string BilibiliLauncherDirectory = "NTELauncherBili", AlternateCnLauncherDirectory = "NTELauncherCN";
    internal const string LauncherConfigPath = "Config/Config.ini";
    internal const string BilibiliLauncherVersionPath = "/hd/publish_ob_B/launcher/Version.ini";
    internal const string RequiredGameArguments = "/Game/LoginAndCreate/Map/Updater/Updater_P -SAVEWINPOS=1";
    internal const string PatcherSdkRelativePath = "UserData/Patcher/PatcherSDK";
    internal static string GameExecutableRelativePath { get; } =
        Path.Combine("Client", "WindowsNoEditor", "HT", "Binaries", "Win64", "HTGame.exe");
    internal static NteRegionDefinition CN { get; } = new(NteHostRegion.CN, CnAppId, CnLauncherDirectory, "NTEGame.exe", 1);
    internal static NteRegionDefinition Bilibili { get; } =
        new(NteHostRegion.Bilibili, CnAppId, CnLauncherDirectory, CN.LauncherExecutable, CN.LoginNotification);
    internal static NteRegionDefinition Global { get; } = new(NteHostRegion.Global, GlobalAppId, GlobalLauncherDirectory, "NTEGlobalGame.exe", 100);
    internal static NteRegionDefinition Taiwan { get; } = new(NteHostRegion.Taiwan, TaiwanAppId, TaiwanLauncherDirectory, "NTETWGame.exe", 100);

    internal NteHostRegion Region { get; }
    internal string AppId { get; }
    internal int ApplicationId => int.Parse(AppId, CultureInfo.InvariantCulture);
    internal string LauncherDirectory { get; }
    internal string LauncherExecutable { get; }
    internal uint LoginNotification { get; }
    internal bool IsOverseas => Region is NteHostRegion.Global or NteHostRegion.Taiwan;
    internal static readonly string[] MainlandLauncherDirectories =
        [CnLauncherDirectory, BilibiliLauncherDirectory, AlternateCnLauncherDirectory];

    internal string LauncherDirectoryFor(string root)
    {
        if (IsOverseas) return LauncherDirectory;
        bool stockIsBilibili = IsStockBilibili(root);
        if (Region == NteHostRegion.CN)
            return stockIsBilibili ? AlternateCnLauncherDirectory : CnLauncherDirectory;
        if (stockIsBilibili) return CnLauncherDirectory;
        string stock = Path.Combine(root, CnLauncherDirectory);
        return Directory.Exists(Path.Combine(root, BilibiliLauncherDirectory)) ||
            File.Exists(Path.Combine(stock, LauncherConfigPath)) ||
            File.Exists(Path.Combine(stock, PatcherSdkRelativePath, "config.xml")) ||
            File.Exists(Path.Combine(root, ".collapse_nte_config"))
            ? BilibiliLauncherDirectory : CnLauncherDirectory;
    }

    internal static bool IsStockBilibili(string root)
    {
        string config = Path.Combine(root, CnLauncherDirectory, LauncherConfigPath);
        return File.Exists(config) ? IsBilibiliConfiguration(config) :
            File.Exists(Path.Combine(root, ".collapse_nte_config")) &&
            File.ReadLines(Path.Combine(root, ".collapse_nte_config")).Contains("Region=Bilibili", StringComparer.Ordinal);
    }

    internal static bool IsBilibiliConfiguration(string config)
    {
        if (!File.Exists(config)) return false;
        foreach (string line in File.ReadLines(config))
        {
            int separator = line.IndexOf('=');
            if (separator < 0) continue;
            string key = line[..separator].Trim();
            if (!key.Equals("VersionInfoFileURL", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("BackupVersionURL", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(line[(separator + 1)..].Trim().Trim('"'), UriKind.Absolute, out Uri? url) &&
                url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort && url.UserInfo.Length == 0 &&
                url.Host is "yhcdn1.wmupd.com" or "yhcdn2.wmupd.com" &&
                url.AbsolutePath == BilibiliLauncherVersionPath) return true;
        }
        return false;
    }

    private NteRegionDefinition(NteHostRegion region, string appId, string launcher, string executable, uint notification)
    { Region = region; AppId = appId; LauncherDirectory = launcher; LauncherExecutable = executable; LoginNotification = notification; }

    internal static NteRegionDefinition From(NteHostRegion region) => region switch
    {
        NteHostRegion.CN => CN, NteHostRegion.Bilibili => Bilibili,
        NteHostRegion.Global => Global, NteHostRegion.Taiwan => Taiwan,
        _ => throw new ArgumentException("Unknown NTE region.", nameof(region))
    };
    internal static NteRegionDefinition FromLauncherDirectory(string directory) => directory switch
    {
        CnLauncherDirectory or AlternateCnLauncherDirectory => CN, BilibiliLauncherDirectory => Bilibili,
        GlobalLauncherDirectory => Global, TaiwanLauncherDirectory => Taiwan,
        _ => throw new ArgumentException("Unknown NTE launcher directory.", nameof(directory))
    };
    internal static bool IsSupportedAppId(string appId) => appId is CnAppId or GlobalAppId or TaiwanAppId;
}
