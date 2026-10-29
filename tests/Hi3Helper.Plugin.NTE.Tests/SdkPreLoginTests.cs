using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkPreLoginTests
{
    [Fact]
    public void NativeKeySlicesAndSha256Base64MatchIndependentVector()
    {
        Assert.Equal("12789abfghpqrstu", Encoding.ASCII.GetString(ProbePreLoginProtocol.DeriveKey("0123456789abcdefghijklmnopqrstuv"u8)));
        SortedDictionary<string, string> fields = new(StringComparer.Ordinal)
        { ["timestamp"] = "1720000000123", ["userId"] = "fixture-user", ["deviceName"] = "WindowsPc", ["appId"] = "1289" };
        Assert.Equal("KXKrjZK6318Jx1L9cxVddGwe8BMvfugJ8kd6Ikbb9cY=", ProbePreLoginProtocol.Sign(fields, "abcdefghijklmnop"u8));
        fields["sign"] = "already-signed";
        Assert.Throws<InvalidDataException>(() => ProbePreLoginProtocol.Sign(fields, "abcdefghijklmnop"u8));
        Assert.Throws<InvalidDataException>(() => ProbePreLoginProtocol.DeriveKey("short"u8));
    }

    [Fact]
    public void RegistrationDerivesFullApplicationKeyWithoutReusingItsOneLoginTail()
    {
        byte[] frame = Registration(3456);
        byte[] key = ProbePreLoginProtocol.KeyFromRegistration(frame, 3456);
        Assert.Equal("12789abfghpqrstu", Encoding.ASCII.GetString(key));
        Assert.NotEqual("ghijklmnopqrstuv", Encoding.ASCII.GetString(key));
        Assert.Throws<InvalidDataException>(() => ProbePreLoginProtocol.KeyFromRegistration(frame, 3457));
        frame[8] = 0;
        Assert.Throws<InvalidDataException>(() => ProbePreLoginProtocol.KeyFromRegistration(frame, 3456));
    }

    [Fact]
    public void QueryRequiresExactFrameTypeAndStaticPayloadAndPreservesSignedContext()
    {
        byte[] frame = Request(1234, -200);
        Assert.Equal(-200, ProbePreLoginRequest.Parse(frame, 1234).ContextId);
        Assert.Throws<InvalidDataException>(() => ProbePreLoginRequest.Parse(frame, 1235));
        Assert.Throws<InvalidDataException>(() => ProbePreLoginRequest.Parse(frame.AsSpan(0, 531), 1234));
        frame[12] ^= 3;
        Assert.Equal(2, ProbePreLoginRequest.ReadQueryType(frame, 1234));
        Assert.Throws<InvalidDataException>(() => ProbePreLoginRequest.Parse(frame, 1234));
        frame = Request(1234, -200); frame[40] ^= 1;
        Assert.Throws<InvalidDataException>(() => ProbePreLoginRequest.Parse(frame, 1234));
    }

    [Theory]
    [InlineData(0, 0, 0)] // Real-name step may be required; never replace it with allowed.
    [InlineData(1, 0, 1)]
    [InlineData(2, 1, 0)]
    public void NativeQueryWrapperPreservesRestrictionsRawBodyAndEachRequestSequence(int status, int realUser, int bannedType)
    {
        byte[] raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { code = 0, message = "实名或限制提示", result = new { status, realUser, bannedType, userId = "fixture-user", age = 13 } }));
        using ProbePreLoginResponse response = ProbePreLoginResponse.Parse(raw);
        Assert.Equal(status, response.Status);
        foreach (int context in new[] { 11, 29 })
        {
            byte[] reply = response.Encode(new(context), 5678);
            Assert.Equal(0x6B647331u, BinaryPrimitives.ReadUInt32LittleEndian(reply));
            Assert.Equal(49, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4)));
            Assert.Equal(raw.Length + 0x422, reply.Length);
            Assert.Equal(reply.Length, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(8)));
            for (int i = 12; i < reply.Length; i++) reply[i] ^= unchecked((byte)5678);
            Assert.Equal(raw.Length + 0x411, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(12)));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(16)));
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(20)));
            Assert.Equal(context, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(24)));
            Assert.Equal("实名或限制提示", Encoding.Unicode.GetString(reply, 28, 1024).Split('\0')[0]);
            Assert.Equal(raw, reply.AsSpan(0x41C, raw.Length).ToArray());
            Assert.All(reply.AsSpan(0x41C + raw.Length).ToArray(), value => Assert.Equal(0, value));
        }
        string summary = JsonSerializer.Serialize(response.Summary);
        Assert.DoesNotContain("fixture-user", summary);
        Assert.DoesNotContain("实名", summary);
        Assert.DoesNotContain("age", summary);
        response.Dispose();
        Assert.Throws<ObjectDisposedException>(() => response.Encode(new(1), 10));
    }

    [Fact]
    public void RejectedServiceResponseKeepsItsCodeAndNeverBecomesSuccess()
    {
        using ProbePreLoginResponse response = ProbePreLoginResponse.Parse("{\"code\":10005,\"message\":\"fixture-echo-secret\"}"u8);
        Assert.Equal(10005, response.Code);
        Assert.Null(response.Status);
        byte[] reply = response.Encode(new(5), 256);
        Assert.Equal(10005, BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(20)));
        Assert.DoesNotContain("fixture-echo-secret", JsonSerializer.Serialize(response.Summary));
    }

    [Fact]
    public void NativeNullRestrictionIsPreservedAndReturnedIdentityMustMatchTheRequest()
    {
        byte[] raw = "{\"code\":0,\"message\":\"\",\"requestIp\":\"fixture-ip\",\"result\":{\"appId\":1289,\"userId\":\"fixture-user\",\"status\":0,\"realUser\":1,\"bannedType\":null}}"u8.ToArray();
        using ProbePreLoginResponse response = ProbePreLoginResponse.Parse(raw, "fixture-user"u8);
        Assert.Null(response.BannedType);
        Assert.Equal(raw, response.Encode(new(42), 256).AsSpan(0x41C, raw.Length).ToArray());
        Assert.Throws<InvalidDataException>(() => ProbePreLoginResponse.Parse(raw, "other-user"u8));
        string diagnostics = JsonSerializer.Serialize(ProbePreLoginResponse.DescribeContract(raw));
        Assert.DoesNotContain("fixture-user", diagnostics); Assert.DoesNotContain("fixture-ip", diagnostics);
        Assert.Contains("bannedType", diagnostics); Assert.Contains("Null", diagnostics);
    }

    [Theory]
    [InlineData("{\"code\":0}")]
    [InlineData("{\"code\":0,\"result\":{\"status\":1}}")]
    [InlineData("{\"code\":1,\"code\":0,\"result\":{\"status\":2,\"realUser\":1,\"bannedType\":0}}")]
    [InlineData("{\"code\":0,\"result\":{\"status\":0,\"status\":2,\"realUser\":1,\"bannedType\":0}}")]
    public void IncompleteOrAmbiguousSuccessCannotProduceCallback(string raw) =>
        Assert.Throws<InvalidDataException>(() => ProbePreLoginResponse.Parse(Encoding.UTF8.GetBytes(raw)));

    [Fact]
    public async Task HttpsFormUsesGameUidOwnedDeviceAndIndependentSignatureWithoutTokensOrAutomaticRetries()
    {
        List<object> events = [];
        using Capture handler = new(); using HttpClient http = new(handler);
        using ProbePreLoginClient client = new("abcdefghijklmnop"u8, new string('a', 32), "1.4.17", events.Add, http);
        Assert.Throws<InvalidOperationException>(() => client.BuildParameters(DateTimeOffset.UtcNow));
        using ProbeIdentityEnvelope identity = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200,
            "{\"code\":0,\"uid\":\"fixture-game-user\",\"token\":\"fixture-game-token\"}"u8);
        client.SetIdentity(identity);
        using ProbePreLoginResponse result = await client.QueryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count);
        Assert.Equal("fixture-game-user", handler.Fields["userId"]);
        Assert.Equal(handler.Fields["udid"], handler.Fields["ndid"]);
        Assert.Equal("1.4.17", handler.Fields["appVersion"]);
        Assert.Equal("2", handler.Fields["version"]);
        Assert.Equal(13, handler.Fields["timestamp"].Length);
        Assert.DoesNotContain(handler.Fields.Keys, key => key.Contains("token", StringComparison.OrdinalIgnoreCase));
        string signature = handler.Fields["sign"]; handler.Fields.Remove("sign");
        Assert.Equal(signature, ProbePreLoginProtocol.Sign(handler.Fields, "abcdefghijklmnop"u8));
        string report = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("fixture-game-user", report); Assert.DoesNotContain("fixture-game-token", report);
        Assert.DoesNotContain(new string('a', 32), report); Assert.DoesNotContain(signature, report);
        handler.Status = HttpStatusCode.Redirect;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.QueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Count);
        handler.Oversized = true; handler.Status = HttpStatusCode.OK;
        await Assert.ThrowsAsync<InvalidDataException>(() => client.QueryAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, handler.Count);
    }

    private sealed class Capture : HttpMessageHandler
    {
        internal int Count;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal bool Oversized;
        internal SortedDictionary<string, string> Fields = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Assert.Equal(ProbePreLoginClient.Endpoint, request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            Assert.False(request.Headers.Contains("appId"));
            string form = await request.Content.ReadAsStringAsync(cancellationToken);
            Fields = new(StringComparer.Ordinal);
            foreach (string pair in form.Split('&'))
            {
                string[] parts = pair.Split('=', 2);
                Fields.Add(WebUtility.UrlDecode(parts[0]), WebUtility.UrlDecode(parts[1]));
            }
            return new(Status) { Content = new StringContent(Oversized ? new string('x', 65536) :
                "{\"code\":0,\"message\":\"\",\"result\":{\"appId\":1289,\"userId\":\"fixture-game-user\",\"status\":1,\"realUser\":0,\"bannedType\":1}}") };
        }
    }

    private static byte[] Request(int clientId, int context)
    {
        byte[] frame = new byte[0x214];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x6B647331);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), frame.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), 1);
        "{}"u8.CopyTo(frame.AsSpan(16));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0x210), context);
        for (int i = 12; i < frame.Length; i++) frame[i] ^= unchecked((byte)clientId);
        return frame;
    }
    private static byte[] Registration(int clientId)
    {
        byte[] frame = new byte[1244];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x6B647331);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), frame.Length);
        Encoding.Unicode.GetBytes("1289\0").CopyTo(frame, 16);
        Encoding.Unicode.GetBytes("0123456789abcdefghijklmnopqrstuv\0").CopyTo(frame, 12 + 0x104);
        Encoding.Unicode.GetBytes("oneSdkConfig.config\0").CopyTo(frame, 12 + 0x204);
        Encoding.Unicode.GetBytes("2.0.8.0\0").CopyTo(frame, 12 + 0x40C);
        for (int i = 12; i < frame.Length; i++) frame[i] ^= unchecked((byte)clientId);
        return frame;
    }
}
