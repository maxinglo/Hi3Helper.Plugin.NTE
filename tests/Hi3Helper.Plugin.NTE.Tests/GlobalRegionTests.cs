using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Hi3Helper.Plugin.NTE.Localization;
using Hi3Helper.Plugin.NTE.Management.Api;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Hi3Helper.Plugin.NTE.Management.PresetConfig;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalRegionTests
{
    [Theory]
    [InlineData("zh_CN", "zh-cn", "cn")]
    [InlineData("zh_TW", "zh-tw", "tc")]
    [InlineData("zh-Hant-HK", "zh-tw", "tc")]
    [InlineData("zh-HK", "zh-tw", "tc")]
    [InlineData("en_US", "en", "en")]
    [InlineData("ja_JP", "ja", "jp")]
    [InlineData("ko-KR", "ko", "kr")]
    [InlineData("de-DE", "de", "de")]
    [InlineData("fr_FR", "fr", "fr")]
    [InlineData("ru-RU", "ru", "ru")]
    [InlineData("es-ES", "es", "es")]
    [InlineData("pt-BR", "en", "en")]
    public void InternationalResources_UseOfficialLanguagePaths(string locale, string language, string suffix)
    {
        Assert.Equal(language, NteGlobalLocalization.ResolveLanguage(locale));
        Assert.EndsWith("launcher_ob2_" + suffix + ".html", NteGlobalLocalization.GetPageUrl(locale));
        Assert.Equal("res/res/3000001/image_lang/" + language + "/main/slogan.png", NteGlobalLocalization.GetArtworkPath(locale));
    }

    [Theory]
    [InlineData("1289")]
    [InlineData("3000001")]
    public void ManifestEncryption_IsBoundToApplication(string appId)
    {
        byte[] source = Encoding.UTF8.GetBytes("<ResList version=\"1.4.10\"><Res filename=\"Client/game.pak\" filesize=\"4\" md5=\"abcd\"/></ResList>");
        byte[] encrypted = NteOfficialPatcherState.CreateLocalManifest(source, appId);
        Assert.Equal(NteOfficialPatcherState.ParseXml(Encoding.UTF8.GetString(source)).OuterXml,
            NteOfficialPatcherState.ParseXml(Encoding.UTF8.GetString(NteResListDecryptor.Decrypt(encrypted, appId))).OuterXml);
        string other = appId == "1289" ? "3000001" : "1289";
        Assert.Throws<CryptographicException>(() => NteResListDecryptor.Decrypt(encrypted, other));
    }

    [Fact]
    public async Task InternationalDiscovery_DoesNotRequestChinaEndpoints()
    {
        using GlobalManifestHandler handler = new();
        using HttpClient client = new(handler);
        NteCNGameManager manager = new(client, NteConfigProvider.Global);
        try
        {
            Assert.Equal(0, await manager.InitAsyncInner(true, TestContext.Current.CancellationToken));
            Assert.Equal("3000001", manager.AppId);
            Assert.Equal("PC_140", manager.BranchName);
            Assert.All(handler.Requests, url => Assert.StartsWith("https://ntecdn1.perfectworld.com/", url));
            Assert.Contains(handler.Requests, url => url.Contains("3000001/PatcherConfig/PatcherConfig.json", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => NtePatcherConfig.Parse(GlobalManifestHandler.Config));
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public void InternationalPreset_HasIndependentManagerAndAllOfficialLanguages()
    {
        NteCNPresetConfig cn = new(), global = new(NteConfigProvider.Global);
        Assert.NotSame(cn.GameManager, global.GameManager);
        Assert.Equal("3000001", Assert.IsType<NteCNGameManager>(global.GameManager).AppId);
        Assert.Equal(9, global.SupportedLanguages.Count);
        Assert.Contains("zh-TW", global.SupportedLanguages);
        Assert.Equal("NTE-Global", global.LauncherGameDirectoryName);
    }

    [Fact]
    public void InternationalCarousel_SelectsTheRequestedLanguage()
    {
        const string json = """{"lb1_en":[{"title":"English","bigpic":"https://example.com/en.jpg"}],"lb1_jp":[{"title":"日本語","bigpic":"https://example.com/ja.jpg"}]}""";
        var japanese = Assert.Single(NteCNLauncherApiNews.ParseCarouselItems(json, "lb1_jp"));
        Assert.Equal("日本語", japanese.Item1);
        Assert.Empty(NteCNLauncherApiNews.ParseCarouselItems(json, "lb1_cn"));
    }

    private sealed class GlobalManifestHandler : HttpMessageHandler
    {
        internal const string Config = """{"branchName":"PC_140","appId":"3000001","gameResUrl":["https://ntecdn1.perfectworld.com/clientRes"]}""";
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            string payload = request.RequestUri.AbsolutePath switch
            {
                "/hd/publish_PC/launcher/Version.ini" => "FileListURL=https://ntecdn1.perfectworld.com/launcher/1/AllFiles.xml",
                "/launcher/1/AllFiles.xml" => """<All_Files><File Path="/ResFilesM/3000001/PatcherConfig/PatcherConfig.json"/></All_Files>""",
                "/launcher/1/ResFilesM/3000001/PatcherConfig/PatcherConfig.json.zip" => Config,
                "/clientRes/PC_140/Version/Windows/config.xml" => "<config><ResVersion>1.4.10</ResVersion></config>",
                _ => throw new InvalidOperationException("Unexpected endpoint " + url)
            };
            HttpContent content;
            if (url.EndsWith(".zip", StringComparison.Ordinal))
            {
                using MemoryStream bytes = new();
                using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, true))
                using (StreamWriter writer = new(zip.CreateEntry("PatcherConfig.json").Open())) writer.Write(payload);
                content = new ByteArrayContent(bytes.ToArray());
            }
            else content = new StringContent(payload);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
