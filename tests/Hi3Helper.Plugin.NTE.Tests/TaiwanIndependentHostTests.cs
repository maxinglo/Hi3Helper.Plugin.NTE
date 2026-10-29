using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nte.Login;
using Nte.LoginHost;
using Nte.LoginHost.Accounts;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class TaiwanIndependentHostTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteTaiwanHost-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static NteOverseasRegion Taiwan => NteOverseasRegion.Taiwan;
    private static NteOverseasApplicationConfiguration Configuration() => new(
        Encoding.ASCII.GetBytes("fixture-application-material-40-characters"), Taiwan.PackageName, Taiwan.Service,
        Taiwan.Area, region: Taiwan);
    private void Put(string path, string content = "synthetic; never executed")
    {
        string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, content);
    }
    private void Installation()
    {
        foreach (string path in new[] { "NTETW/NTETWGame.exe", "NTETW/WPGameClientSDK_64.dll", "NTETW/ResData/2000013.dat",
                     "Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe", "Client/WindowsNoEditor/HT/Binaries/Win64/WPGlobalSDK_64.dll" }) Put(path);
        Put("NTETW/Config/Config.ini", "[General]\nGameID=2000013\n");
        Put(".collapse_nte_config", "GameVersion=1.4.6\nAppId=2000013\n");
    }
    private static Dictionary<string, string> EnvironmentValues(string appId = "2000013") => new()
    {
        ["ArcGameAppId"] = appId, ["ArcProcessId"] = "1234", ["ArcMoblieNotify"] = "49152", ["anchor_w"] = "10",
        ["anchor_v"] = "20", ["ArcMId"] = "Local.Fixture.Map", ["ArcMMId"] = "Local.Fixture.Mutex"
    };
    private static NteGlobalIdentity Identity(NteOverseasRegion region)
    {
        using JsonDocument json = JsonDocument.Parse("""{"code":0,"result":{"uid":"same-fixture-user","token":"synthetic-token","loginType":37}}""");
        return NteGlobalIdentity.FromService(json.RootElement, 2, region);
    }

    [Fact]
    public void TaiwanLaunchStartsOnlyTheSelectedGameAndSuppliesTaiwanBootstrap()
    {
        Installation();
        var plan = NteOverseasLaunchPlan.Create(new(root, "-dx11 -windowed", Region: NteHostRegion.Taiwan));
        ProcessStartInfo start = plan.CreateStartInfo("Collapse.NTE.Taiwan.Test", "0123456789abcdef0123456789abcdef", EnvironmentValues());
        Assert.EndsWith(Path.Combine("Client", "WindowsNoEditor", "HT", "Binaries", "Win64", "HTGame.exe"), start.FileName);
        Assert.False(start.UseShellExecute); Assert.Contains("-dx11 -windowed", start.Arguments);
        Assert.DoesNotContain("saveddirsuffix=Global", start.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("2000013", start.Environment["ArcGameAppId"]); Assert.Equal("2000013", start.Environment["ArcDataIndex"]);
        Assert.Equal(Path.Combine(root, "NTETW", "WPGameClientSDK"), start.Environment["ArcGameSDKSharePath"]);
        Assert.Equal(Path.Combine(root, "NTETW"), start.Environment["GamePlatformPath"]);
        using var configuration = Configuration(); NteGlobalGameEnvironment.Apply(start, configuration);
        Assert.Equal("2000013", start.Environment["GAME_APPID"]); Assert.Equal("iwplay", start.Environment["GAME_AREA"]);
        NteGlobalGameEnvironment.Clear(start); Assert.False(start.Environment.ContainsKey("GAME_APPKEY"));
        Assert.Throws<ArgumentException>(() => plan.CreateStartInfo("Collapse.NTE.Taiwan.Test", "0123456789abcdef0123456789abcdef", EnvironmentValues("3000001")));
    }

    [Fact]
    public void TaiwanRejectsAnInactiveSharedRootAndAnotherRegionsSaveSuffix()
    {
        Installation(); Put(".collapse_nte_config", "AppId=3000001\n");
        Assert.Throws<NteGlobalLaunchException>(() => NteOverseasLaunchPlan.Create(new(root, Region: NteHostRegion.Taiwan)));
        Put(".collapse_nte_config", "AppId=2000013\n");
        Assert.Throws<NteGlobalLaunchException>(() => NteOverseasLaunchPlan.Create(new(root, "--saveddirsuffix=Global", Region: NteHostRegion.Taiwan)));
    }

    [Fact]
    public async Task TaiwanInitializationAndMetadataNeverUseGlobalServiceOrIdentifiers()
    {
        using var configuration = Configuration();
        using var handler = new Handler(async request =>
        {
            Assert.Equal("mapitw.playcomb.com", request.RequestUri!.Host);
            string body = await request.Content!.ReadAsStringAsync(Token);
            Assert.Contains("appId=2000013", body); Assert.DoesNotContain("3000001", body); Assert.DoesNotContain("appKey", body);
            using JsonDocument client = JsonDocument.Parse(Uri.UnescapeDataString(Assert.Single(request.Headers.GetValues("X-Client-Data"))));
            Assert.Equal(2000013, client.RootElement.GetProperty("appId").GetInt32());
            Assert.Equal("tw.com.iwplay.nte", client.RootElement.GetProperty("packageName").GetString());
            Assert.Equal("zh-tw", client.RootElement.GetProperty("appLanguage").GetString());
            return """{"code":0,"result":{"loginTypes":[7,2,1,11]}}""";
        });
        using HttpClient http = new(handler); using NteGlobalAuthentication api = new(configuration, "1.4.6", http, language: "en");
        await api.InitializeAsync(Token); Assert.True(api.Initialized);
        using JsonDocument metadata = await api.QuerySdkInfoAsync(new(104, 1), Token);
        Assert.Equal(2, handler.Calls);
        await Assert.ThrowsAsync<NteGlobalAuthenticationException>(() => api.SendEmailCaptchaAsync("fixture@example.com", Token));
        await Assert.ThrowsAsync<NteGlobalAuthenticationException>(() => api.LoginEmailAsync("fixture@example.com", "synthetic-password", true, Token));
        Assert.Equal(2, handler.Calls);
    }

    private static string BrowserConfiguration(NteOverseasApplicationConfiguration configuration, string origin = "https://static.playcomb.com")
    {
        string Encode(string value)
        {
            using Aes aes = Aes.Create(); aes.Key = configuration.ApplicationKey.AsSpan(0, 16).ToArray();
            return Convert.ToBase64String(aes.EncryptEcb(Encoding.UTF8.GetBytes(value), PaddingMode.PKCS7));
        }
        Dictionary<string, string> firebase = new();
        foreach (string field in new[] { "apiKey", "authDomain", "projectId", "storageBucket", "messagingSenderId", "appId", "measurementId", "jsUrl" }) firebase[field] = Encode("synthetic-" + field);
        return JsonSerializer.Serialize(new { code = 0, result = new { thirdAccount = new
        {
            google = new { loginUrl = Encode(origin + "/pcLogin_3.0/google.html") },
            facebook = new { loginUrl = Encode(origin + "/pcLogin_3.0/facebook.html"), appId = Encode("synthetic-facebook-app"), sdkJsUrl = Encode("https://connect.facebook.net/en_US/sdk.js") }, firebase
        } } });
    }

    [Fact]
    public async Task TaiwanBrowserContextAndTokenExchangeRemainInTheTaiwanTenant()
    {
        using var configuration = Configuration();
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getV2")) return BrowserConfiguration(configuration);
            Assert.Equal("https://mapitw.playcomb.com/c/api/user/third/login", request.RequestUri.AbsoluteUri);
            string body = await request.Content!.ReadAsStringAsync(Token);
            Assert.Contains("appId=2000013", body); Assert.Contains("thirdType=2", body);
            return """{"code":0,"result":{"uid":"fixture-user","token":"synthetic-pc-token","loginType":37}}""";
        });
        using HttpClient http = new(handler); using NteGlobalAuthentication api = new(configuration, "1.4.6", http);
        await api.InitializeAsync(Token);
        Uri uri = api.CreateBrowserLoginUri(2, "collapse-nte-0123456789abcdef0123456789abcdef");
        Assert.Equal("static.playcomb.com", uri.Host);
        string callback = uri.Query.TrimStart('?').Split('&').Single(field => field.StartsWith("callback="))[9..];
        using JsonDocument data = JsonDocument.Parse(Uri.UnescapeDataString(callback));
        Assert.Equal("2000013", data.RootElement.GetProperty("result").GetProperty("appId").GetString());
        Assert.Equal("https://i.playcomb.com", data.RootElement.GetProperty("result").GetProperty("webdomain").GetString());
        using JsonDocument browser = JsonDocument.Parse("""{"uid":"web-identity-not-used","token":"web-token-not-used","thirdReqParams":{"thirdType":2,"thirdAuthToken":"synthetic-provider-token"}}""");
        using NteGlobalIdentity identity = await api.CompleteBrowserLoginAsync(browser.RootElement, 2, Token);
        Assert.Same(Taiwan, identity.Region); Assert.Equal("synthetic-pc-token", identity.Result.GetProperty("token").GetString());
    }

    [Theory]
    [InlineData("https://static.pwsdk.com")]
    [InlineData("https://static.playcomb.com.evil.invalid")]
    public async Task TaiwanRejectsCrossPublisherAndLookalikeBrowserPages(string origin)
    {
        using var configuration = Configuration(); using var handler = new Handler(_ => Task.FromResult(BrowserConfiguration(configuration, origin)));
        using HttpClient http = new(handler); using NteGlobalAuthentication api = new(configuration, "1.4.6", http);
        await api.InitializeAsync(Token);
        Assert.Throws<InvalidDataException>(() => api.CreateBrowserLoginUri(2, "collapse-nte-0123456789abcdef0123456789abcdef"));
    }

    [Fact]
    public async Task StoredIdentitiesCannotBeDecodedOrRestoredAcrossPublishers()
    {
        using NteGlobalIdentity taiwan = Identity(Taiwan), global = Identity(NteOverseasRegion.Global);
        byte[] tw = StoredGlobalIdentity.Encode(taiwan), gl = StoredGlobalIdentity.Encode(global), key = new byte[32];
        try
        {
            Assert.Throws<InvalidDataException>(() => StoredGlobalIdentity.Decode(tw));
            Assert.Throws<InvalidDataException>(() => StoredGlobalIdentity.Decode(gl, Taiwan));
            using NteGlobalIdentity restored = StoredGlobalIdentity.Decode(tw, Taiwan); using NteGlobalIdentity clone = restored.Clone();
            Assert.Same(Taiwan, clone.Region);
            Assert.NotEqual(StoredGlobalIdentity.SubjectKey(tw, key, Taiwan), StoredGlobalIdentity.SubjectKey(gl, key));
            using var configuration = Configuration(); using var handler = new Handler(_ => throw new InvalidOperationException("No network expected"));
            using HttpClient http = new(handler); using NteGlobalAuthentication api = new(configuration, "1.4.6", http);
            await Assert.ThrowsAsync<InvalidDataException>(() => api.RestoreSessionAsync(global, Token)); Assert.Equal(0, handler.Calls);
        }
        finally { CryptographicOperations.ZeroMemory(tw); CryptographicOperations.ZeroMemory(gl); }
    }

    [Fact]
    public async Task TaiwanVaultDeviceAndRememberedSelectionAreSeparateFromGlobal()
    {
        if (!OperatingSystem.IsWindows()) return;
        OverseasAccountStore taiwan = new(Path.Combine(root, "Taiwan"), new WindowsAccountKeyProtector(), region: Taiwan);
        OverseasAccountStore global = new(Path.Combine(root, "Global"), new WindowsAccountKeyProtector());
        using NteGlobalIdentity tw = Identity(Taiwan), gl = Identity(NteOverseasRegion.Global);
        LocalAccount account = Assert.IsType<LocalAccount>(await taiwan.SaveAndDeliverAsync(tw, "Taiwan fixture", true, 0, (_, _) => Task.CompletedTask, Token));
        Assert.Equal(account.Id, (await taiwan.LastAsync(Token))!.Id); Assert.Null(await global.LastAsync(Token)); Assert.Empty(global.List());
        Assert.NotEqual(await taiwan.DeviceIdAsync(Token), await global.DeviceIdAsync(Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => taiwan.SaveAndDeliverAsync(gl, "Wrong publisher", true, 0, (_, _) => Task.CompletedTask, Token));
        Assert.Single(taiwan.List());
        Assert.NotEqual(NteLoginPreferences.GetPath(NteHostRegion.Taiwan), NteLoginPreferences.GlobalPath);
        Assert.NotEqual(NteLaunchPreferences.GetPath(NteHostRegion.Taiwan), NteLaunchPreferences.GetPath(true));
    }

    [Fact]
    public void TaiwanApplicationContextPacketDoesNotContainGlobalApplicationMetadata()
    {
        byte[][] packets = NteGlobalSdkContextPackets.Encode(1234, Taiwan);
        foreach (byte[] packet in packets) for (int i = 12; i < packet.Length; i++) packet[i] ^= unchecked((byte)1234);
        Assert.Equal(50u, BinaryPrimitives.ReadUInt32LittleEndian(packets[0].AsSpan(4)));
        string app = Encoding.ASCII.GetString(packets[0].AsSpan(0x4c, 64)).Split('\0')[0];
        string package = Encoding.ASCII.GetString(packets[0].AsSpan(12, 64)).Split('\0')[0];
        Assert.Equal("2000013", app); Assert.Equal("tw.com.iwplay.nte", package);
        Assert.Equal("www.iwplay.com.tw", Taiwan.TermsUri.Host); Assert.Equal("www.iwplay.com.tw", Taiwan.PrivacyUri.Host);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; return new(HttpStatusCode.OK) { Content = new StringContent(await respond(request), Encoding.UTF8, "application/json") };
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
