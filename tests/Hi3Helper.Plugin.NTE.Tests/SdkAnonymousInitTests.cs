using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkAnonymousInitTests
{
    private const string Secret = "0123456789abcdef9876543210fedcba";
    private static JsonDocument Configuration() => JsonDocument.Parse("""
        {"init":{"channel":{"channelAppId":"12345","channelAppKey":"0123456789abcdef9876543210fedcba"}},
         "meta":{"version":"1.0.0","packageName":"fixture","channelId":9}}
        """);

    [Fact]
    public async Task AnonymousSuccessDoesNotRecordResponseValuesOrTransmitTheKey()
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new(HttpStatusCode.OK,
            """{"code":0,"result":{"loginOptionConfig":[1,2,1000001],"internalToken":"private-response-token"}}""");
        using HttpClient client = new(handler);
        string output = JsonSerializer.Serialize(await ProbeAnonymousInit.SendAsync(client, config.RootElement, TestContext.Current.CancellationToken));
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.True(report.RootElement.GetProperty("initialized").GetBoolean());
        Assert.False(report.RootElement.GetProperty("accountRequestSent").GetBoolean());
        Assert.Equal(new[] { 1, 2 }, report.RootElement.GetProperty("loginOptions").EnumerateArray().Select(x => x.GetInt32()));
        Assert.DoesNotContain(Secret, output);
        Assert.DoesNotContain("private-response-token", output);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(ProbeAnonymousInit.Endpoint, handler.Uri);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.DoesNotContain(Secret, handler.Body);
        Assert.DoesNotContain("cellphone=", handler.Body);
        Assert.DoesNotContain("captcha=", handler.Body);
        Assert.DoesNotContain("password=", handler.Body);
        Assert.Contains("sdkVersion=5.159.0", handler.Body);
        Assert.Contains("sign=", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailureCannotBecomeInitializationSuccess(HttpStatusCode status)
    {
        using JsonDocument config = Configuration();
        using CaptureHandler handler = new(status, """{"code":0,"result":{}}""");
        using HttpClient client = new(handler);
        using JsonDocument report = JsonDocument.Parse(JsonSerializer.Serialize(
            await ProbeAnonymousInit.SendAsync(client, config.RootElement, TestContext.Current.CancellationToken)));
        Assert.False(report.RootElement.GetProperty("initialized").GetBoolean());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingConfigurationMakesNoRequest()
    {
        using JsonDocument config = JsonDocument.Parse("{}");
        using CaptureHandler handler = new(HttpStatusCode.OK, "{}");
        using HttpClient client = new(handler);
        using JsonDocument report = JsonDocument.Parse(JsonSerializer.Serialize(
            await ProbeAnonymousInit.SendAsync(client, config.RootElement, TestContext.Current.CancellationToken)));
        Assert.False(report.RootElement.GetProperty("attempted").GetBoolean());
        Assert.False(report.RootElement.GetProperty("initialized").GetBoolean());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void RequestSigningUsesStableFieldOrderingAndAnOwnedAnonymousDeviceId()
    {
        using JsonDocument config = Configuration();
        var first = ProbeAnonymousInit.BuildParameters(config.RootElement, DateTimeOffset.FromUnixTimeSeconds(1234567890), new string('a', 32));
        var repeated = ProbeAnonymousInit.BuildParameters(config.RootElement, DateTimeOffset.FromUnixTimeSeconds(1234567890), new string('a', 32));
        var differentDevice = ProbeAnonymousInit.BuildParameters(config.RootElement, DateTimeOffset.FromUnixTimeSeconds(1234567890), new string('b', 32));
        Assert.Equal(first["sign"], repeated["sign"]);
        Assert.NotEqual(first["sign"], differentDevice["sign"]);
        Assert.Equal("1234567890", first["t"]);
        Assert.Equal(32, first["sign"].Length);
        Assert.Equal(new string('a', 32), first["deviceId"]);
        Assert.False(first.ContainsKey("appKey"));
        Assert.Throws<InvalidDataException>(() => ProbeAnonymousInit.BuildParameters(config.RootElement, DateTimeOffset.UtcNow, "an-untrusted-device-identifier"));
    }

    private sealed class CaptureHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri; Method = request.Method;
            Body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
