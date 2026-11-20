using Hi3Helper.Plugin.Core.Management;
using System.IO;
using System;
using Nte.Login;
using System.Linq;
using System.Collections.Generic;

namespace Hi3Helper.Plugin.NTE.Management.Config;

public static class NteConfigProvider
{
    private const string BilibiliLauncherVersionPath = "/hd/publish_ob_B/launcher/Version.ini";

    internal static NteRegionConfig? DetectInstalledRegion(string root)
    {
        string launcherConfig = Path.Combine(root, NteRegionDefinition.CnLauncherDirectory, "Config", "Config.ini");
        string marker = Path.Combine(root, ".collapse_nte_config");
        if (File.Exists(marker))
        {
            string? appId = null;
            string? region = null;
            foreach (string line in File.ReadLines(marker))
            {
                if (line.StartsWith("AppId=", StringComparison.Ordinal))
                    appId ??= line[6..].Trim();
                if (line.StartsWith("Region=", StringComparison.Ordinal))
                    region ??= line[7..].Trim();
            }
            if (region != null)
                return region == nameof(NteHostRegion.Bilibili) && appId == Bilibili.AppId
                    ? Bilibili : throw new InvalidDataException("Unknown installed NTE region.");

            // Legacy mainland markers had no independent region identifier.
            NteRegionConfig legacy = appId == null ? CN : GetRegion(appId);
            return legacy == CN && IsBilibiliLauncherConfig(launcherConfig) ? Bilibili : legacy;
        }

        if (File.Exists(Path.Combine(root, Global.SdkDirectory, "config.xml"))) return Global;
        if (File.Exists(Path.Combine(root, Taiwan.SdkDirectory, "config.xml"))) return Taiwan;
        if (File.Exists(launcherConfig)) return IsBilibiliLauncherConfig(launcherConfig) ? Bilibili : CN;
        if (File.Exists(Path.Combine(root, CN.SdkDirectory, "config.xml"))) return CN;
        return null;
    }

    internal static bool IsBilibiliLauncherConfig(string config)
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

    internal static NteRegionConfig GetRegion(string appId) => appId switch
    {
        NteRegionDefinition.CnAppId => CN,
        NteRegionDefinition.GlobalAppId => Global,
        NteRegionDefinition.TaiwanAppId => Taiwan,
        _ => throw new InvalidDataException("Unknown installed NTE region.")
    };

    public static string SwiperScriptUrl { get; } =
        "https://static.games.wanmei.com/public/commonData/gamesData/gameSwiper/yh-gameSwiper.js";

    public static string LauncherVersionIniUrl { get; } = "https://yhcdn1.wmupd.com/hd/publish_ob/launcher/Version.ini";

    public static string OfficialSiteUrl { get; } = "https://yh.wanmei.com/";
    public static string LauncherHtmlUrl { get; } = "https://yh.wanmei.com/launcher/launcher_ob.html?expand=1";

    public static string[] ConfigXmlUrls => GameResBaseUrls.Select(root =>
        $"{root}/{DefaultBranchName}/Version/Windows/config.xml").ToArray();

    // ── 下载系统相关常量 ──

    /// <summary>默认分支名称</summary>
    public static string DefaultBranchName { get; } = "publish_PC";

    /// <summary>默认 appId</summary>
    public static string DefaultAppId => NteRegionDefinition.CnAppId;

    /// <summary>CDN 资源基础 URL 列表</summary>
    public static string[] GameResBaseUrls { get; } =
    [
        "https://yhcdn1.wmupd.com/clientRes",
        "https://yhcdn2.wmupd.com/clientRes"
    ];

    /// <summary>
    /// ResList.bin.zip 下载 URL 模板。
    /// {0} = branchName, {1} = gameVersion
    /// </summary>
    public static string ResListUrlTemplate => GameResBaseUrls[0] + "/{0}/Version/Windows/version/{1}/ResList.bin.zip";

    /// <summary>ResList.bin.zip 备用 CDN URL 模板</summary>
    public static string ResListFallbackUrlTemplate => GameResBaseUrls[1] + "/{0}/Version/Windows/version/{1}/ResList.bin.zip";

    /// <summary>构建 ResList.bin.zip 下载 URL</summary>
    public static string BuildResListUrl(string branchName, string gameVersion, int cdnIndex = 0)
    {
        string template = cdnIndex == 0 ? ResListUrlTemplate : ResListFallbackUrlTemplate;
        return string.Format(template, branchName, gameVersion);
    }

    /// <summary>
    /// 构建资源文件 CDN 下载 URL。
    /// 格式: {cdnBase}/{branchName}/Res/{md5[0]}/{md5}.{filesize}
    /// </summary>
    public static string BuildResourceUrl(string cdnBaseUrl, string branchName, string md5, long filesize)
    {
        return $"{cdnBaseUrl.TrimEnd('/')}/{branchName}/Res/{md5[0]}/{md5}.{filesize}";
    }

    public static string GameExecutableRelativePath => NteRegionDefinition.GameExecutableRelativePath;

    public static string RequiredStartArgument => NteRegionDefinition.RequiredGameArguments;

    public static NteRegionConfig CN { get; } = new(
        ProfileName: "NteCN",
        ZoneName: "中国大陆官服",
        ZoneLogoUrl: "https://cdn.collapselauncher.com/cl-cdn/inhouse-plugin/nte/logo.png",
        ZonePosterUrl: "https://cdn.collapselauncher.com/cl-cdn/inhouse-plugin/nte/poster.png",
        ZoneHomePageUrl: "https://yh.wanmei.com/",
        GameExecutableName: GameExecutableRelativePath,
        LauncherGameDirectoryName: "Neverness to Everness",
        GameAppDataPath: @"%AppData%\NTE",
        GameLogFileName: "NTE.log",
        GameVendorName: "完美世界",
        GameRegistryKeyName: @"SOFTWARE\PerfectWorld\NTE",
        GameMainLanguage: "zh-CN",
        ReleaseChannel: GameReleaseChannel.Public,
        SupportedLanguages: ["zh-CN"]
    );

    public static NteRegionConfig Bilibili { get; } = CN with
    {
        HostRegion = NteHostRegion.Bilibili,
        ProfileName = NteRegionDefinition.BilibiliProfileName,
        ZoneName = "Bilibili 服",
        LauncherGameDirectoryName = "Neverness to Everness (Bilibili)",
        AppId = NteRegionDefinition.Bilibili.AppId,
        LauncherDirectoryName = NteRegionDefinition.CnLauncherDirectory,
        LauncherVersionIniUrl = "https://yhcdn1.wmupd.com" + BilibiliLauncherVersionPath
    };

    public static NteRegionConfig Global { get; } = new(
        ProfileName: "NteGlobal",
        ZoneName: "国际服",
        ZoneLogoUrl: CN.ZoneLogoUrl,
        ZonePosterUrl: CN.ZonePosterUrl,
        ZoneHomePageUrl: "https://nte.perfectworld.com/",
        GameExecutableName: GameExecutableRelativePath,
        LauncherGameDirectoryName: "NTE-Global",
        GameAppDataPath: @"%LocalAppData%\HT\Saved",
        GameLogFileName: "HT.log",
        GameVendorName: "N2E",
        GameRegistryKeyName: @"SOFTWARE\NTEGlobal",
        GameMainLanguage: "en-US",
        ReleaseChannel: GameReleaseChannel.Public,
        SupportedLanguages: ["zh-CN", "zh-TW", "en-US", "ja-JP", "ko-KR", "de-DE", "fr-FR", "ru-RU", "es-ES"])
    {
        HostRegion = NteHostRegion.Global,
        AppId = NteRegionDefinition.Global.AppId,
        LauncherVersionIniUrl = "https://ntecdn1.perfectworld.com/hd/publish_PC/launcher/Version.ini",
        LauncherDirectoryName = NteRegionDefinition.Global.LauncherDirectory,
        DefaultBranch = "PC_140",
        ResourceUrls = ["https://ntecdn1.perfectworld.com/clientRes", "https://ntecdn2.perfectworld.com/clientRes"]
    };

    public static NteRegionConfig Taiwan { get; } = new(
        ProfileName: "NteTaiwan",
        ZoneName: "台港澳",
        ZoneLogoUrl: CN.ZoneLogoUrl,
        ZonePosterUrl: CN.ZonePosterUrl,
        ZoneHomePageUrl: "https://nte.iwplay.com.tw/",
        GameExecutableName: GameExecutableRelativePath,
        LauncherGameDirectoryName: "NTE-Taiwan",
        GameAppDataPath: @"%LocalAppData%\HT",
        GameLogFileName: "HT.log",
        GameVendorName: "Iwplay",
        GameRegistryKeyName: @"SOFTWARE\NTETW",
        GameMainLanguage: "zh-TW",
        ReleaseChannel: GameReleaseChannel.Public,
        SupportedLanguages: ["zh-TW"])
    {
        HostRegion = NteHostRegion.Taiwan,
        AppId = NteRegionDefinition.Taiwan.AppId,
        LauncherVersionIniUrl = "https://patchnte1.iwplay.com.tw/hd/OBpublish_PC/launcher/Version.ini",
        LauncherDirectoryName = NteRegionDefinition.Taiwan.LauncherDirectory,
        LauncherPageUrl = "https://nte.iwplay.com.tw/platform/index.html",
        DefaultBranch = "PC_140",
        ResourceUrls = ["https://patchnte1.iwplay.com.tw/clientRes", "https://patchnte2.iwplay.com.tw/clientRes"]
    };

    internal static IReadOnlyList<NteRegionConfig> AllRegions { get; } = [CN, Global, Taiwan, Bilibili];
}
