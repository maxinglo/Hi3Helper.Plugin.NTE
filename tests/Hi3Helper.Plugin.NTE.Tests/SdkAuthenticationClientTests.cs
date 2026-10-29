using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nte.LoginHost.Accounts;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkAuthenticationClientTests
{
    private const string Phone = "13800000000", Captcha = "123456", Key = "0123456789abcdef9876543210fedcba";
    private static JsonDocument Configuration() => JsonDocument.Parse("""
        {"init":{"channel":{"channelAppId":"12345","channelAppKey":"0123456789abcdef9876543210fedcba"}},
         "meta":{"version":"1.0.0","packageName":"fixture","channelId":9,"platform":4}}
        """);

    [Fact]
    public async Task ExplicitLoginStagesKeepChannelAndGameIdentitySeparateAndNeverLogSecrets()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new(
            """{"code":0,"result":{"loginOptionConfig":[92]}}""",
            """{"code":0,"result":{"havePwd":true}}""",
            """{"code":0,"result":{}}""",
            """{"code":0,"result":{"userId":"channel-fixture-uid","token":"channel-fixture-token"}}""",
            """{"code":0,"uid":"game-fixture-uid","token":"game-fixture-token","extraOne":{"oneNewUserFlag":false}}""");
        using HttpClient http = new(handler);
        List<object> observations = [];
        using ProbeAuthenticationClient auth = new(config.RootElement, observations.Add, http, "1.4.17", "fedcba9876543210"u8);
        Assert.Empty(handler.Requests);
        await auth.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Single(handler.Requests); // Initialization never sends an SMS.
        Assert.Equal(TimeSpan.Zero, auth.SmsCooldownRemaining);
        await auth.SendSmsAsync(Phone, TestContext.Current.CancellationToken);
        Assert.InRange(auth.SmsCooldownRemaining.TotalSeconds, 50, 60);
        using ProbeIdentityEnvelope channel = await auth.LoginChannelAsync(Phone, Captcha, TestContext.Current.CancellationToken);
        using ProbeIdentityEnvelope game = await auth.ExchangeGameIdentityAsync(channel, TestContext.Current.CancellationToken);
        Assert.True(game.IdentityAvailable);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal("/m/newApi/query/whetherSetPassword", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[3].Method);
        Assert.Equal(HttpMethod.Post, handler.Requests[4].Method);
        Assert.Equal("/m/newApi/sendPhoneCaptchaWithOutLogin", handler.Requests[2].Uri.AbsolutePath);
        Assert.Equal(Phone, handler.Requests[2].Values["cellphone"]);
        Assert.Equal("18", handler.Requests[2].Values["type"]);
        var login = handler.Requests[3];
        Assert.Equal("/openApi/sms/new/login", login.Uri.AbsolutePath);
        Assert.NotEqual(Phone, login.Values["cellphone"]);
        Assert.NotEqual(Captcha, login.Values["captcha"]);
        Assert.Equal("1", login.Values["areaCodeId"]);
        foreach (string placeholder in new[] { "idfa", "adm", "mac" }) Assert.Equal("", login.Values[placeholder]);
        Assert.Equal("1.4.17", login.Values["version"]);
        Assert.Equal("fixture", login.Values["bid"]);
        Assert.Equal("WindowsPc", login.Values["deviceType"]);
        Assert.False(login.Values.ContainsKey("packageName"));
        Assert.False(login.Values.ContainsKey("appVersion"));
        Assert.Equal(13, login.Values["t"].Length);
        Assert.Equal(13, handler.Requests[0].Values["t"].Length);
        using Aes aes = Aes.Create(); aes.Key = Encoding.ASCII.GetBytes(Key[16..]);
        Assert.Equal(Phone, Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromBase64String(login.Values["cellphone"]), PaddingMode.PKCS7)));
        Assert.Equal(Captcha, Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromBase64String(login.Values["captcha"]), PaddingMode.PKCS7)));
        string sign = login.Values["sign"];
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(
            string.Concat(login.Values.Where(p => p.Key != "sign").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)) + Key))), sign);
        var exchange = handler.Requests[4];
        Assert.Equal(ProbeAuthenticationClient.OneLoginEndpoint, exchange.Uri);
        Assert.Equal("1289", exchange.AppIdHeader);
        Assert.Equal("1289", exchange.Values["appId"]);
        Assert.Equal("channel-fixture-uid", exchange.Values["channelUid"]);
        Assert.Equal("channel-fixture-token", exchange.Values["channelToken"]);
        Assert.Equal("2", exchange.Values["version"]);
        Assert.False(exchange.Values.ContainsKey("sign"));
        Assert.True(exchange.RawEncryptedBody);
        using JsonDocument extra = JsonDocument.Parse(exchange.Values["extraJson"]);
        Assert.Equal(2, extra.RootElement.EnumerateObject().Count());
        Assert.Equal("9", extra.RootElement.GetProperty("channelId").GetString());
        Assert.Equal("9", extra.RootElement.GetProperty("subchannelId").GetString());
        using JsonDocument device = JsonDocument.Parse(exchange.Values["device"]);
        Assert.Equal(handler.Requests[0].Values["deviceId"], device.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("5.159.0", device.RootElement.GetProperty("sdkVersion").GetString());
        Assert.Equal("fixture", device.RootElement.GetProperty("packageName").GetString());
        Assert.False(device.RootElement.TryGetProperty("bid", out _));
        Assert.False(device.RootElement.TryGetProperty("appId", out _));
        string report = JsonSerializer.Serialize(observations);
        foreach (string secret in new[] { Phone, Captcha, Key, "channel-fixture-uid", "channel-fixture-token", "game-fixture-uid", "game-fixture-token" })
            Assert.DoesNotContain(secret, report);
    }

    [Fact]
    public async Task FailedQueryCannotSendSmsAndStillHasCooldown()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", """{"code":0,"result":{}}""");
        using HttpClient http = new(handler);
        using ProbeAuthenticationClient auth = new(config.RootElement, _ => { }, http);
        await auth.InitializeAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() => auth.SendSmsAsync(Phone, TestContext.Current.CancellationToken));
        ProbeAuthenticationException second = await Assert.ThrowsAsync<ProbeAuthenticationException>(() => auth.SendSmsAsync(Phone, TestContext.Current.CancellationToken));
        Assert.Equal("sms-cooldown", second.Phase);
        Assert.InRange(auth.SmsCooldownRemaining.TotalSeconds, 50, 60);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExistingCodeInFreshHostQueriesTypeWithoutSendingAnotherSms()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""",
            """{"code":0,"result":{"havePwd":true}}""",
            """{"code":0,"result":{"userId":"channel-fixture-uid","token":"channel-fixture-token"}}""");
        using HttpClient http = new(handler); using ProbeAuthenticationClient auth = new(config.RootElement, _ => { }, http);
        await auth.InitializeAsync(TestContext.Current.CancellationToken);
        using ProbeIdentityEnvelope identity = await auth.LoginChannelAsync(Phone, Captcha, TestContext.Current.CancellationToken);
        Assert.True(identity.IdentityAvailable);
        Assert.Equal(3, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("sendPhoneCaptcha", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameEnvelopePreflightOmitsIdentityAndNeverSendsSms()
    {
        using JsonDocument config = Configuration(); using CaptureHandler handler = new("""{"code":17,"msg":"missing identity"}""");
        using HttpClient http = new(handler);
        using ProbeAuthenticationClient auth = new(config.RootElement, _ => { }, http, "1.4.17", "fedcba9876543210"u8);
        using JsonDocument report = JsonDocument.Parse(JsonSerializer.Serialize(await auth.InspectGameEnvelopeWithoutIdentityAsync(TestContext.Current.CancellationToken)));
        Assert.True(report.RootElement.GetProperty("decodedReply").GetBoolean());
        Assert.False(report.RootElement.GetProperty("identityAccepted").GetBoolean());
        Assert.False(report.RootElement.GetProperty("accountDataSupplied").GetBoolean());
        var request = Assert.Single(handler.Requests);
        Assert.True(request.RawEncryptedBody);
        Assert.False(request.Values.ContainsKey("channelUid"));
        Assert.False(request.Values.ContainsKey("channelToken"));
        Assert.Equal(ProbeAuthenticationClient.OneLoginEndpoint, request.Uri);
    }

    [Fact]
    public async Task GameExchangeFailureCanRetrySameInMemoryChannelWithoutRepeatingSms()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""",
            """{"code":0,"result":{"havePwd":true}}""", """{"code":0,"result":{}}""",
            """{"code":0,"result":{"userId":"channel-fixture-uid","token":"channel-fixture-token"}}""",
            """{"code":1}""", """{"code":0,"uid":"game-fixture-uid","token":"game-fixture-token"}""");
        using HttpClient http = new(handler);
        using ProbeAuthenticationClient auth = new(config.RootElement, _ => { }, http, "1.4.17", "fedcba9876543210"u8);
        await auth.InitializeAsync(TestContext.Current.CancellationToken);
        await auth.SendSmsAsync(Phone, TestContext.Current.CancellationToken);
        using ProbeIdentityEnvelope channel = await auth.LoginChannelAsync(Phone, Captcha, TestContext.Current.CancellationToken);
        handler.Status = HttpStatusCode.InternalServerError;
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() => auth.ExchangeGameIdentityAsync(channel, TestContext.Current.CancellationToken));
        Assert.True(channel.IdentityAvailable);
        handler.Status = HttpStatusCode.OK;
        using ProbeIdentityEnvelope game = await auth.ExchangeGameIdentityAsync(channel, TestContext.Current.CancellationToken);
        Assert.True(game.IdentityAvailable);
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath.Contains("sendPhoneCaptcha", StringComparison.Ordinal));
        Assert.Single(handler.Requests, request => request.Uri.AbsolutePath == "/openApi/sms/new/login");
    }

    [Fact]
    public async Task HttpFailureDiagnosticsDoNotLeakServerEchoedCredentials()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":123,"message":"Required request parameter 'appId' is missing; account=private-account; token=private-token"}""")
            { Status = HttpStatusCode.BadRequest };
        using HttpClient http = new(handler); List<object> observations = [];
        using ProbeAuthenticationClient auth = new(config.RootElement, observations.Add, http);
        ProbeAuthenticationException failure = await Assert.ThrowsAsync<ProbeAuthenticationException>(() => auth.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(400, failure.Code);
        string report = JsonSerializer.Serialize(observations);
        Assert.Contains("appId", report); Assert.Contains("123", report);
        Assert.DoesNotContain("private-account", report); Assert.DoesNotContain("private-token", report);
    }

    [Theory]
    [InlineData("true", "18")]
    [InlineData("false", "16")]
    [InlineData("1", "18")]
    [InlineData("0", "16")]
    public void SmsTypeUsesNativePasswordState(string value, string expected)
    {
        using JsonDocument json = JsonDocument.Parse("{\"result\":{\"havePwd\":" + value + "}}");
        Assert.Equal(expected, ProbeAuthenticationClient.ReadSmsType(json.RootElement));
    }

    [Fact]
    public async Task InvalidInputAndMissingInitCannotSendAnyRequest()
    {
        using JsonDocument config = Configuration(); using CaptureHandler handler = new(); using HttpClient http = new(handler);
        using ProbeAuthenticationClient auth = new(config.RootElement, _ => { }, http);
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() => auth.SendSmsAsync(Phone, TestContext.Current.CancellationToken));
        Assert.Throws<ProbeAuthenticationException>(() => auth.BuildSmsLoginParameters("not-a-phone", Captcha, "18"));
        Assert.Throws<InvalidDataException>(() => auth.BuildSmsLoginParameters(Phone, "not-a-code", "18"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RedirectFailureHasNoRetryOrIdentityExchange()
    {
        using JsonDocument config = Configuration(); using CaptureHandler handler = new("{}") { Status = HttpStatusCode.Redirect };
        using HttpClient http = new(handler); using ProbeAuthenticationClient auth = new(config.RootElement, _ => { }, http);
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() => auth.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SavedChannelIsValidatedBeforeExchangingANewGameIdentityWithItsOwnedDevice()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""",
            """{"code":0,"result":{"userId":"must-ignore-user","token":"must-ignore-token"}}""",
            """{"code":0,"uid":"new-game-user","token":"new-game-token"}""");
        using HttpClient http = new(handler); List<object> observations = [];
        using ProbeAuthenticationClient api = new(config.RootElement, observations.Add, http, "1.4.17", "fedcba9876543210"u8);
        using SavedChannelCredential saved = SavedCredential();
        using ProbeIdentityEnvelope game = await SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken);
        Assert.True(game.IdentityAvailable);
        Assert.Equal(new[] { ProbeAnonymousInit.Endpoint, ProbeAuthenticationClient.ChannelValidationEndpoint, ProbeAuthenticationClient.OneLoginEndpoint },
            handler.Requests.Select(r => r.Uri));
        var validate = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, validate.Method); Assert.Equal(ProbeAuthenticationClient.ChannelValidationEndpoint, validate.Uri);
        Assert.Equal("channel-fixture-uid", validate.Values["userId"]); Assert.Equal("channel-fixture-token", validate.Values["token"]);
        Assert.Equal("4", validate.Values["osType"]); Assert.Equal(new string('b', 32), validate.Values["deviceId"]);
        Assert.Equal("1.4.17", validate.Values["version"]); Assert.Equal("fixture", validate.Values["bid"]);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(
            string.Concat(validate.Values.Where(p => p.Key != "sign").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value)) + Key))), validate.Values["sign"]);
        var exchange = handler.Requests[2];
        Assert.Equal("channel-fixture-uid", exchange.Values["channelUid"]); Assert.Equal("channel-fixture-token", exchange.Values["channelToken"]);
        using JsonDocument device = JsonDocument.Parse(exchange.Values["device"]);
        Assert.Equal(new string('b', 32), device.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(new string('b', 32), handler.Requests[0].Values["deviceId"]);
        string report = JsonSerializer.Serialize(observations);
        foreach (string secret in new[] { "channel-fixture-uid", "channel-fixture-token", "must-ignore-user", "must-ignore-token", "new-game-user", "new-game-token", new string('b', 32) })
            Assert.DoesNotContain(secret, report);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("sms", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"code\":17}")]
    [InlineData("{\"code\":0,\"result\":{}}")]
    [InlineData("not-json")]
    public async Task FailedSavedAccountInitializationRestoresPreviousSmsSession(string failure)
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", failure,
            """{"code":0,"result":{"havePwd":true}}""", """{"code":0,"result":{}}""");
        using HttpClient http = new(handler);
        using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http, savedDeviceId: new string('a', 32));
        using SavedChannelCredential saved = SavedCredential();
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() =>
            SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal(new string('a', 32), api.DeviceId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Uri == ProbeAuthenticationClient.ChannelValidationEndpoint ||
            request.Uri.AbsolutePath.Contains("sendPhoneCaptcha", StringComparison.Ordinal));
        // Only an explicit user request sends an SMS; it uses the original
        // initialized context instead of being stuck in not-initialized.
        await api.SendSmsAsync(Phone, TestContext.Current.CancellationToken);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("/m/newApi/sendPhoneCaptchaWithOutLogin", handler.Requests[3].Uri.AbsolutePath);
        Assert.Equal(new string('a', 32), handler.Requests[3].Values["deviceId"]);
    }

    [Fact]
    public async Task FailedSavedAccountInitializationDoesNotFabricateAPreviousSmsSession()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("{\"code\":17}");
        using HttpClient http = new(handler);
        using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http, savedDeviceId: new string('a', 32));
        using SavedChannelCredential saved = SavedCredential();
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() =>
            SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken));
        ProbeAuthenticationException error = await Assert.ThrowsAsync<ProbeAuthenticationException>(() => api.SendSmsAsync(Phone, TestContext.Current.CancellationToken));
        Assert.Equal("not-initialized", error.Phase); Assert.Single(handler.Requests);
        Assert.Equal(new string('a', 32), api.DeviceId);
    }

    [Fact]
    public async Task CancelledSavedAccountInitializationRestoresPreviousSmsSession()
    {
        using JsonDocument config = Configuration(); using CancellationTokenSource cancel = new();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""",
            """{"code":0,"result":{"loginOptionConfig":[92]}}""",
            """{"code":0,"result":{"havePwd":true}}""", """{"code":0,"result":{}}""");
        using HttpClient http = new(handler);
        using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http, savedDeviceId: new string('a', 32));
        using SavedChannelCredential saved = SavedCredential();
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        handler.BeforeInitializationReply = cancel.Cancel;
        await Assert.ThrowsAnyAsync<Exception>(() => SavedChannelLogin.RestoreAsync(saved, api, _ => { }, cancel.Token));
        Assert.True(cancel.IsCancellationRequested); Assert.Equal(new string('a', 32), api.DeviceId);
        handler.BeforeInitializationReply = null;
        await api.SendSmsAsync(Phone, TestContext.Current.CancellationToken);
        Assert.Equal(new string('a', 32), handler.Requests[3].Values["deviceId"]);
    }

    [Theory]
    [InlineData("{\"code\":10004,\"message\":\"private-token\"}", "channel-validation-rejected")]
    [InlineData("{\"code\":17}", "channel-validation-rejected")]
    [InlineData("{\"code\":\"0\"}", "channel-validation-unknown-response")]
    [InlineData("{\"code\":0,\"code\":1}", "channel-validation-unknown-response")]
    [InlineData("{\"code\":0,\"result\":[]}", "channel-validation-unknown-response")]
    [InlineData("not-json", "channel-validation-unknown-response")]
    [InlineData("{\"code\":0,\"result\":{\"privacyAgreement\":\"unverified-contract\"}}", "channel-validation-official-interaction")]
    public async Task SavedChannelFailuresNeverExchangeGameIdentityOrSendSms(string response, string phase)
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", response);
        using HttpClient http = new(handler); List<object> observations = [];
        using ProbeAuthenticationClient api = new(config.RootElement, observations.Add, http, "1.4.17", "fedcba9876543210"u8);
        using SavedChannelCredential saved = SavedCredential();
        ProbeAuthenticationException ex = await Assert.ThrowsAsync<ProbeAuthenticationException>(() =>
            SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal(phase, ex.Phase); Assert.Equal(2, handler.Requests.Count); Assert.True(saved.Identity.IdentityAvailable);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(observations));
        Assert.DoesNotContain(handler.Requests, request => request.Uri == ProbeAuthenticationClient.OneLoginEndpoint || request.Uri.AbsolutePath.Contains("sms", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NetworkFailureLeavesChannelUsableAndAUserInitiatedRetryCanValidate()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", "{}", "{\"code\":0}")
            { ValidationException = new HttpRequestException("fixture transport failure") };
        using HttpClient http = new(handler); using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http);
        using SavedChannelCredential saved = SavedCredential();
        await api.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProbeChannelValidationStatus.NetworkFailure, (await api.ValidateChannelIdentityAsync(saved.Identity, TestContext.Current.CancellationToken)).Status);
        Assert.True(saved.Identity.IdentityAvailable); handler.ValidationException = null;
        Assert.Equal(ProbeChannelValidationStatus.Validated, (await api.ValidateChannelIdentityAsync(saved.Identity, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CancelledValidationCannotStartGameExchangeEvenWhenAReplyArrives()
    {
        using JsonDocument config = Configuration(); using CancellationTokenSource cancel = new();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", "{\"code\":0}")
            { BeforeValidationReply = cancel.Cancel };
        using HttpClient http = new(handler); using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http);
        using SavedChannelCredential saved = SavedCredential();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SavedChannelLogin.RestoreAsync(saved, api, _ => { }, cancel.Token));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r.Uri == ProbeAuthenticationClient.OneLoginEndpoint);
    }

    [Fact]
    public void PreLoginUsesRestoredOwnedDeviceRatherThanThePreviousAttempt()
    {
        using JsonDocument config = Configuration(); using ProbeAuthenticationClient api = new(config.RootElement, _ => { });
        using ProbePreLoginClient preLogin = new("0123456789abcdef"u8, api.DeviceId, "1.4.17", _ => { }, deviceProvider: () => api.DeviceId);
        using ProbeIdentityEnvelope game = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, "{\"code\":0,\"uid\":\"fixture-game-user\",\"token\":\"fixture-game-token\"}"u8);
        api.RestoreDeviceContext(new string('b', 32), 4, 9); preLogin.SetIdentity(game);
        var fields = preLogin.BuildParameters(DateTimeOffset.UtcNow);
        Assert.Equal(new string('b', 32), fields["udid"]); Assert.Equal(fields["udid"], fields["ndid"]);
        Assert.Throws<InvalidDataException>(() => api.RestoreDeviceContext(new string('c', 32), 4, 8));
    }

    private static SavedChannelCredential SavedCredential() => new(ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Channel, 200,
        "{\"code\":0,\"result\":{\"userId\":\"channel-fixture-uid\",\"token\":\"channel-fixture-token\"}}"u8), new string('b', 32), 4, 9);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BadRequestPreflightStillRequiresANewOfficialGameIdentity(bool exchangeAccepted)
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", "{\"code\":400}",
            exchangeAccepted ? "{\"code\":0,\"uid\":\"fresh-user\",\"token\":\"fresh-token\"}" : "{\"code\":10004}")
            { ValidationStatus = HttpStatusCode.BadRequest };
        using HttpClient http = new(handler); using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http, applicationKeyTail: "fedcba9876543210"u8);
        using SavedChannelCredential saved = SavedCredential();
        if (exchangeAccepted)
        { using ProbeIdentityEnvelope game = await SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken); Assert.True(game.IdentityAvailable); }
        else
        { Assert.Equal("game-login-rejected", (await Assert.ThrowsAsync<ProbeAuthenticationException>(() => SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken))).Phase); }
        Assert.Equal(3, handler.Requests.Count); Assert.Equal(ProbeAuthenticationClient.OneLoginEndpoint, handler.Requests[2].Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OtherHttpRejectionsDoNotStartGameIdentityExchange(HttpStatusCode status)
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", "{\"code\":400}") { ValidationStatus = status };
        using HttpClient http = new(handler); using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http);
        using SavedChannelCredential saved = SavedCredential();
        Assert.Equal("channel-validation-request-rejected", (await Assert.ThrowsAsync<ProbeAuthenticationException>(() => SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken))).Phase);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ExplicitCredentialRejectionInsideHttp400StillStopsRestoration()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new("""{"code":0,"result":{"loginOptionConfig":[92]}}""", "{\"code\":10004}") { ValidationStatus = HttpStatusCode.BadRequest };
        using HttpClient http = new(handler); using ProbeAuthenticationClient api = new(config.RootElement, _ => { }, http);
        using SavedChannelCredential saved = SavedCredential();
        ProbeAuthenticationException error = await Assert.ThrowsAsync<ProbeAuthenticationException>(() => SavedChannelLogin.RestoreAsync(saved, api, _ => { }, TestContext.Current.CancellationToken));
        Assert.Equal("channel-validation-rejected", error.Phase); Assert.Equal(10004, error.Code); Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class CaptureHandler(params string[] replies) : HttpMessageHandler
    {
        internal record Request(Uri Uri, HttpMethod Method, Dictionary<string, string> Values, string? AppIdHeader, bool RawEncryptedBody);
        internal List<Request> Requests { get; } = [];
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal HttpStatusCode? ValidationStatus { get; set; }
        internal Exception? ValidationException { get; set; }
        internal Action? BeforeValidationReply { get; set; }
        internal Action? BeforeInitializationReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : request.RequestUri!.Query.TrimStart('?');
            bool gameExchange = request.RequestUri == ProbeAuthenticationClient.OneLoginEndpoint;
            using ProbeGameEnvelopeCodec codec = new("fedcba9876543210"u8);
            Dictionary<string, string> fields;
            if (gameExchange)
            {
                byte[] json = codec.Decode(Encoding.ASCII.GetBytes(body));
                using JsonDocument envelope = JsonDocument.Parse(json);
                fields = envelope.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);
            }
            else fields = body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')), pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            Requests.Add(new(request.RequestUri!, request.Method, fields, request.Headers.TryGetValues("appId", out var values) ? values.Single() : null, gameExchange));
            if (request.RequestUri == ProbeAnonymousInit.Endpoint) BeforeInitializationReply?.Invoke();
            if (request.RequestUri == ProbeAuthenticationClient.ChannelValidationEndpoint)
            { BeforeValidationReply?.Invoke(); if (ValidationException != null) throw ValidationException; }
            string reply = replies[Requests.Count - 1];
            HttpContent content = gameExchange ? new ByteArrayContent(codec.Encode(Encoding.UTF8.GetBytes(reply))) : new StringContent(reply, Encoding.UTF8, "application/json");
            return new(request.RequestUri == ProbeAuthenticationClient.ChannelValidationEndpoint ? ValidationStatus ?? Status : Status) { Content = content };
        }
    }
}
