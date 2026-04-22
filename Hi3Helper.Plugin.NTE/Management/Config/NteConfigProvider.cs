using Hi3Helper.Plugin.Core.Management;
using System.IO;

namespace Hi3Helper.Plugin.NTE.Management.Config;

public static class NteConfigProvider
{
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
        ZoneName: "CN",
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
}

