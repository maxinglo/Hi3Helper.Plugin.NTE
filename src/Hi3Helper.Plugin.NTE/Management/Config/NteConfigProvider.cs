using Hi3Helper.Plugin.Core.Management;
using System.IO;
using System;

namespace Hi3Helper.Plugin.NTE.Management.Config;

public static class NteConfigProvider
{
    internal static NteRegionConfig? DetectInstalledRegion(string root)
    {
        string marker = Path.Combine(root, ".collapse_nte_config");
        if (File.Exists(marker))
        {
            foreach (string line in File.ReadLines(marker))
                if (line.StartsWith("AppId=", StringComparison.Ordinal))
                    return line[6..].Trim() switch { "3000001" => Global, "1289" => CN, _ => throw new InvalidDataException("Unknown installed NTE region.") };
            return CN; // Existing China installations predate the application identifier.
        }
        if (File.Exists(Path.Combine(root, Global.SdkDirectory, "config.xml"))) return Global;
        if (File.Exists(Path.Combine(root, CN.SdkDirectory, "config.xml"))) return CN;
        return null;
    }

    public static string SwiperScriptUrl { get; } = "https://static.games.wanmei.com/public/commonData/gamesData/gameSwiper/yh-gameSwiper.js";
    public static string LauncherVersionIniUrl { get; } = "https://yhcdn1.wmupd.com/hd/publish_ob/launcher/Version.ini";

    public static string OfficialSiteUrl { get; } = "https://yh.wanmei.com/";
    public static string LauncherHtmlUrl { get; } = "https://yh.wanmei.com/launcher/launcher_ob.html?expand=1";
    public static string[] ConfigXmlUrls { get; } = [
        "https://yhcdn1.wmupd.com/clientRes/publish_PC/Version/Windows/config.xml",
        "https://yhcdn2.wmupd.com/clientRes/publish_PC/Version/Windows/config.xml"
    ];

    // ── 下载系统相关常量 ──

    /// <summary>默认分支名称</summary>
    public static string DefaultBranchName { get; } = "publish_PC";

    /// <summary>默认 appId</summary>
    public static string DefaultAppId { get; } = "1289";

    /// <summary>CDN 资源基础 URL 列表</summary>
    public static string[] GameResBaseUrls { get; } = [
        "https://yhcdn1.wmupd.com/clientRes",
        "https://yhcdn2.wmupd.com/clientRes"
    ];

    /// <summary>
    /// ResList.bin.zip 下载 URL 模板。
    /// {0} = branchName, {1} = gameVersion
    /// </summary>
    public static string ResListUrlTemplate { get; } =
        "https://yhcdn1.wmupd.com/clientRes/{0}/Version/Windows/version/{1}/ResList.bin.zip";

    /// <summary>ResList.bin.zip 备用 CDN URL 模板</summary>
    public static string ResListFallbackUrlTemplate { get; } =
        "https://yhcdn2.wmupd.com/clientRes/{0}/Version/Windows/version/{1}/ResList.bin.zip";

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

    public static string GameExecutableRelativePath { get; } =
        Path.Combine("Client", "WindowsNoEditor", "HT", "Binaries", "Win64", "HTGame.exe");

    public static string RequiredStartArgument { get; } =
        "/Game/LoginAndCreate/Map/Updater/Updater_P -SAVEWINPOS=1";

    public static NteRegionConfig CN { get; } = new(
        ProfileName: "NteCN",
        ZoneName: "中国大陆",
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
        AppId = "3000001",
        LauncherVersionIniUrl = "https://ntecdn1.perfectworld.com/hd/publish_PC/launcher/Version.ini",
        LauncherDirectoryName = "NTEGlobal",
        DefaultBranch = "PC_140",
        ResourceUrls = ["https://ntecdn1.perfectworld.com/clientRes", "https://ntecdn2.perfectworld.com/clientRes"]
    };
}

