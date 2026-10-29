using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalAuthenticationTests
{
    private static NteGlobalApplicationConfiguration Configuration() => new(
        Encoding.ASCII.GetBytes("test-application-key-material-length-40!!"), "com.hottagames.nte",
        new("https://mapi.pwsdk.com"), "global-sg");

    [Fact]
    public void AuthenticationAndTheGameReceiveTheSamePrivateDeviceContext()
    {
        using var config = Configuration();
        using NteGlobalAuthentication api = new(config, "1.4.10");
        using JsonDocument metadata = JsonDocument.Parse(Uri.UnescapeDataString(api.ClientData));
        var plan = new NteGlobalLaunchPlan(Path.GetFullPath("fixture-root"),
            Path.GetFullPath("fixture-root/HTGame.exe"), "-dx11");
        var start = plan.CreateStartInfo("Collapse.NTE.Global.Test", api.DeviceId, new Dictionary<string, string>
        {
            ["ArcGameAppId"] = "3000001", ["ArcProcessId"] = "12345", ["ArcMoblieNotify"] = "49152",
            ["anchor_w"] = "100", ["anchor_v"] = "200", ["ArcMId"] = "Local.Fixture.Map",
            ["ArcMMId"] = "Local.Fixture.Mutex"
        });
        Assert.Equal(api.DeviceId, metadata.RootElement.GetProperty("udid").GetString());
        Assert.Equal(api.DeviceId, metadata.RootElement.GetProperty("ndid").GetString());
        Assert.Equal(api.DeviceId, start.Environment["ArcDummyMoblieDeviceId"]);
        Assert.DoesNotContain(api.DeviceId, start.Arguments);
    }

    [Fact]
    public async Task InitializationUsesOnlyGlobalServiceAndUrlEncodedWindowsMetadata()
    {
        using var config = Configuration();
        using var handler = new Handler(async request =>
        {
            Assert.Equal("https://mapi.pwsdk.com/c/api/config/getV2", request.RequestUri!.AbsoluteUri);
            string metadata = Uri.UnescapeDataString(Assert.Single(request.Headers.GetValues("X-Client-Data")));
            using JsonDocument document = JsonDocument.Parse(metadata);
            Assert.Equal(4, document.RootElement.GetProperty("osType").GetInt32());
            Assert.Equal("zh-tw", document.RootElement.GetProperty("appLanguage").GetString());
            string body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("appId=3000001", body);
            Assert.DoesNotContain("appKey", body);
            Assert.DoesNotContain("uid=", body);
            Assert.DoesNotContain("token=", body);
            return "{\"code\":0,\"result\":{\"loginTypes\":[8,1,2]}}";
        });
        using HttpClient http = new(handler);
        using NteGlobalAuthentication api = new(config, "1.4.10", http, "zh-tw");
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(api.Initialized);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectedInitializationDoesNotEnableAuthentication()
    {
        using var config = Configuration();
        using var handler = new Handler(_ => Task.FromResult("{\"code\":10001,\"message\":\"private diagnostic\"}"));
        using HttpClient http = new(handler);
        using NteGlobalAuthentication api = new(config, "1.4.10", http);
        var error = await Assert.ThrowsAsync<NteGlobalAuthenticationException>(() =>
            api.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ServiceRejected", error.Key);
        Assert.DoesNotContain("private diagnostic", error.Message);
        Assert.False(api.Initialized);
    }

    [Fact]
    public async Task EmailDeliveryIsExplicitEncryptedAndHasAnAmbiguousRequestCooldown()
    {
        using var config = Configuration();
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getV2")) return "{\"code\":0,\"result\":{}}";
            Assert.Equal("/c/api/email/captcha/get", request.RequestUri.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("example.test", body);
            string field = body.Split('&').Single(p => p.StartsWith("email="))[6..];
            byte[] encrypted = Convert.FromBase64String(Uri.UnescapeDataString(field.Replace('+', ' ')));
            using Aes aes = Aes.Create();
            aes.Key = config.ApplicationKey.AsSpan(0, 16).ToArray();
            Assert.Equal("tester@example.test", Encoding.UTF8.GetString(aes.DecryptEcb(encrypted, PaddingMode.PKCS7)));
            throw new HttpRequestException("Ambiguous response");
        });
        using HttpClient http = new(handler);
        using NteGlobalAuthentication api = new(config, "1.4.10", http);
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            api.SendEmailCaptchaAsync("tester@example.test", TestContext.Current.CancellationToken));
        Assert.True(api.CaptchaCooldown > TimeSpan.Zero);
        var cooldown = await Assert.ThrowsAsync<NteGlobalAuthenticationException>(() =>
            api.SendEmailCaptchaAsync("tester@example.test", TestContext.Current.CancellationToken));
        Assert.Equal("EmailCooldown", cooldown.Key);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void LoginNotificationUsesGlobalJsonCommandAndDoesNotUseMainlandPacket()
    {
        using JsonDocument response =
            JsonDocument.Parse(
                "{\"code\":0,\"result\":{\"uid\":\"fixture-user\",\"token\":\"fixture-token\",\"loginType\":73,\"extra\":true}}");
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 2);
        byte[] packet = NteGlobalLoginPacket.Encode(identity, 1337);
        Assert.Equal(1538u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal(packet.Length, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)));
        for (int i = 12; i < packet.Length; i++) packet[i] ^= unchecked((byte)1337);
        int length = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12));
        Assert.Equal(packet.Length - 17, length);
        Assert.Equal(0, packet[^1]);
        using JsonDocument json = JsonDocument.Parse(packet.AsMemory(16, length));
        JsonElement result = json.RootElement.GetProperty("resultObj");
        Assert.Equal("fixture-user", result.GetProperty("uid").GetString());
        Assert.Equal("fixture-token", result.GetProperty("token").GetString());
        Assert.Equal(73, result.GetProperty("loginType").GetInt32());
        Assert.Equal(2, identity.Provider);
        Assert.Equal(3, result.EnumerateObject().Count());
        Assert.DoesNotContain("fixture-user", identity.ToString());
    }

    [Fact]
    public void AServiceWithoutLoginTypeUsesTheOfficialJsonDefaultInsteadOfTheBrowserProvider()
    {
        using JsonDocument response = JsonDocument.Parse("""{"code":0,"result":{"uid":42,"token":"fixture-token"}}""");
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 2);
        Assert.Equal(0, identity.LoginType);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("\"2\"")]
    [InlineData("null")]
    public void AnInvalidServiceLoginTypeCannotBeReplacedWithTheSelectedProvider(string type)
    {
        using JsonDocument response =
            JsonDocument.Parse("{\"code\":0,\"result\":{\"uid\":42,\"token\":\"fixture-token\",\"loginType\":" + type +
                               "}}");
        Assert.Throws<InvalidDataException>(() => NteGlobalIdentity.FromService(response.RootElement, 2));
    }

    [Fact]
    public async Task NumericPcUserIdIsNormalizedAfterTheOfficialProviderExchange()
    {
        using var config = Configuration();
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getV2")) return "{\"code\":0,\"result\":{}}";
            Assert.Equal("/c/api/user/third/login", request.RequestUri.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("thirdType=2", body);
            Assert.Contains("thirdAuthToken=fixture-provider-token", body);
            Assert.DoesNotContain("fixture-browser-token", body);
            Assert.DoesNotContain("uid=", body);
            return "{\"code\":0,\"result\":{\"uid\":9007199254740993,\"token\":\"fixture-verified-token\"}}";
        });
        using HttpClient http = new(handler);
        using NteGlobalAuthentication api = new(config, "1.4.10", http);
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        using JsonDocument callback =
            JsonDocument.Parse(
                "{\"uid\":9007199254740993,\"token\":\"fixture-browser-token\",\"thirdReqParams\":{\"thirdType\":2,\"thirdAuthToken\":\"fixture-provider-token\"}}");
        using NteGlobalIdentity identity =
            await api.CompleteBrowserLoginAsync(callback.RootElement, 2, TestContext.Current.CancellationToken);
        Assert.Equal("9007199254740993", NteGlobalIdentity.ReadUserId(identity.Result.GetProperty("uid")));
        Assert.Equal("fixture-verified-token", identity.Result.GetProperty("token").GetString());
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("null")]
    public void UnsupportedUserIdCannotCreateAnIdentity(string userId)
    {
        using JsonDocument response =
            JsonDocument.Parse("{\"code\":0,\"result\":{\"uid\":" + userId + ",\"token\":\"fixture-token\"}}");
        Assert.Throws<InvalidDataException>(() => NteGlobalIdentity.FromService(response.RootElement, 2));
    }

    [Fact]
    public async Task BrowserSuccessStillRequiresTheOfficialProviderExchange()
    {
        using var config = Configuration();
        using var handler = new Handler(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("getV2")
            ? "{\"code\":0,\"result\":{}}"
            : "{\"code\":10001}"));
        using HttpClient http = new(handler);
        using NteGlobalAuthentication api = new(config, "1.4.10", http);
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        using JsonDocument callback =
            JsonDocument.Parse(
                "{\"uid\":42,\"token\":\"fixture-browser-token\",\"thirdReqParams\":{\"thirdType\":2,\"thirdAuthToken\":\"fixture-provider-token\"}}");
        var rejected = await Assert.ThrowsAsync<NteGlobalAuthenticationException>(() =>
            api.CompleteBrowserLoginAsync(callback.RootElement, 2, TestContext.Current.CancellationToken));
        Assert.Equal("ServiceRejected", rejected.Key);
        Assert.Equal(10001, Assert.Single(rejected.Arguments));
    }

    [Fact]
    public async Task RepeatedGameLoginRevalidatesThePcSessionWithoutBrowserCredentials()
    {
        using var config = Configuration();
        using var handler = new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("getV2")) return "{\"code\":0,\"result\":{}}";
            Assert.Equal("/c/api/user/token/login", request.RequestUri.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("uid=42", body);
            Assert.Contains("token=fixture-pc-token", body);
            Assert.DoesNotContain("thirdAuthToken", body);
            return "{\"code\":0,\"result\":{\"uid\":42,\"token\":\"fixture-refreshed-token\"}}";
        });
        using HttpClient http = new(handler);
        using NteGlobalAuthentication api = new(config, "1.4.10", http);
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        using JsonDocument initial =
            JsonDocument.Parse("{\"code\":0,\"result\":{\"uid\":42,\"token\":\"fixture-pc-token\"}}");
        using NteGlobalIdentity saved = NteGlobalIdentity.FromService(initial.RootElement, 2);
        using NteGlobalIdentity restored = await api.RestoreSessionAsync(saved, TestContext.Current.CancellationToken);
        Assert.Equal("fixture-refreshed-token", restored.Result.GetProperty("token").GetString());
        Assert.Equal(2, restored.Provider);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("{\"code\":1,\"result\":{\"uid\":\"fixture\",\"token\":\"fixture\"}}")]
    [InlineData("{\"code\":0,\"result\":{\"uid\":\"\",\"token\":\"fixture\"}}")]
    public void InvalidServiceIdentityCannotCreateASuccessPacket(string text)
    {
        using JsonDocument response = JsonDocument.Parse(text);
        Assert.ThrowsAny<Exception>(() => NteGlobalIdentity.FromService(response.RootElement, 2));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-TW")]
    [InlineData("ja-JP")]
    [InlineData("pt-BR")]
    public void LauncherLanguageSurvivesTheDetachedHandoff(string language)
    {
        NteHostLaunchOptions request = new(Path.GetFullPath("global language fixture"), Region: NteHostRegion.Global,
            Language: language);
        Assert.Equal(language, NteHostLaunchOptions.Parse(request.ToArguments()).Language);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken token)
        {
            Calls++;
            return new(HttpStatusCode.OK)
                { Content = new StringContent(await respond(request), Encoding.UTF8, "application/json") };
        }
    }
}
