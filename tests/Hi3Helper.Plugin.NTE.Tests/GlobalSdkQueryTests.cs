using System.Buffers.Binary;
using System.Text.Json;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalSdkQueryTests
{
    private const string IpService = """
        {"code":0,"result":{"city":"FixtureCity","country":"FixtureCountry","countryAbbr":"ZZ",
         "countryCode":"fixture-code","ip":"192.0.2.1","province":"FixtureProvince"}}
        """;
    [Theory]
    [InlineData(104u, 1540u, "/c/api/ip/getData")]
    [InlineData(113u, 1531u, "/c/api/ip/platform")]
    public void OfficialMetadataReachesTheMatchingBridgeCallback(uint notification, uint command, string path)
    {
        Assert.True(NteGlobalSdkQuery.TryCreate(notification, 37, out var request));
        Assert.Equal(path, request.ServicePath);
        using JsonDocument reply = JsonDocument.Parse(notification == 104 ? IpService :
            """{"code":0,"result":{"platform":"fixture-platform"}}""");
        byte[] packet = NteGlobalPacket.EncodeQuery(request, reply.RootElement, 1337);
        Assert.Equal(command, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        for (int i = 12; i < packet.Length; i++) packet[i] ^= unchecked((byte)1337);
        int size = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(12));
        Assert.Equal(packet.Length - 17, size); Assert.Equal(0, packet[^1]);
        using JsonDocument envelope = JsonDocument.Parse(packet.AsMemory(16, size));
        Assert.Equal(37, envelope.RootElement.GetProperty("contextId").GetInt32());
        string result = envelope.RootElement.GetProperty("result").GetString()!;
        if (notification == 113)
        {
            Assert.Equal("fixture-platform", result);
            Assert.Equal("success", envelope.RootElement.GetProperty("message").GetString());
            Assert.Equal(0, envelope.RootElement.GetProperty("code").GetInt32());
        }
        else
        {
            using JsonDocument location = JsonDocument.Parse(result);
            Assert.Equal(6, location.RootElement.EnumerateObject().Count());
            Assert.Equal("192.0.2.1", location.RootElement.GetProperty("IP").GetString());
            Assert.Equal("FixtureCountry", location.RootElement.GetProperty("Country").GetString());
            Assert.Equal("ZZ", location.RootElement.GetProperty("CountryAbbr").GetString());
            Assert.Equal("fixture-code", location.RootElement.GetProperty("CountryCode").GetString());
            Assert.Equal("FixtureCity", location.RootElement.GetProperty("City").GetString());
            Assert.Equal("FixtureProvince", location.RootElement.GetProperty("Province").GetString());
            Assert.False(envelope.RootElement.TryGetProperty("code", out _));
            Assert.False(location.RootElement.TryGetProperty("result", out _));
        }
    }
    [Theory]
    [InlineData("{}")][InlineData("{\"platform\":null}")][InlineData("{\"platform\":4}")]
    public void IncompletePlatformDataCannotBecomeASuccessfulCallback(string result)
    {
        using JsonDocument response = JsonDocument.Parse("{\"code\":0,\"result\":" + result + "}");
        Assert.Throws<InvalidDataException>(() => NteGlobalPacket.EncodeQuery(new(113, 1), response.RootElement, 1337));
    }
    [Fact]
    public void IncompleteLocationDataCannotBecomeASuccessfulCallback()
    {
        using JsonDocument response = JsonDocument.Parse("""{"code":0,"result":{"ip":"192.0.2.1"}}""");
        Assert.Throws<InvalidDataException>(() => NteGlobalPacket.EncodeQuery(new(104, 1), response.RootElement, 1337));
    }
    [Theory]
    [InlineData(100u, 1)][InlineData(110u, 1)][InlineData(104u, -1)]
    public void UnsupportedNotificationsAndInvalidContextsAreNotMetadataQueries(uint command, int context)
        => Assert.False(NteGlobalSdkQuery.TryCreate(command, context, out _));
    [Fact]
    public void AServiceRejectionCannotBecomeSuccessfulSdkMetadata()
    {
        using JsonDocument response = JsonDocument.Parse("{\"code\":10001}");
        var error = Assert.Throws<NteGlobalAuthenticationException>(() => NteGlobalPacket.EncodeQuery(new(113, 1), response.RootElement, 1337));
        Assert.Equal("ServiceRejected", error.Key);
    }
    [Fact]
    public void UserInfoCacheIsFilledBeforeLoginWithStringUserIdsWithoutChangingTheServiceIdentity()
    {
        using JsonDocument response = JsonDocument.Parse("{\"code\":0,\"result\":{\"uid\":9007199254740993,\"token\":\"fixture-token\",\"extra\":true}}");
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 2);
        byte[][] packets = NteGlobalLoginPacket.EncodeSequence(identity, 1337);
        Assert.Equal(2, packets.Length);
        Assert.Equal(1539u, BinaryPrimitives.ReadUInt32LittleEndian(packets[0].AsSpan(4)));
        Assert.Equal(1538u, BinaryPrimitives.ReadUInt32LittleEndian(packets[1].AsSpan(4)));
        foreach (byte[] packet in packets) for (int i = 12; i < packet.Length; i++) packet[i] ^= unchecked((byte)1337);
        using JsonDocument information = JsonDocument.Parse(packets[0].AsMemory(16, packets[0].Length - 17));
        using JsonDocument original = JsonDocument.Parse(information.RootElement.GetProperty("result").GetString()!);
        Assert.Equal("9007199254740993", original.RootElement.GetProperty("uid").GetString());
        Assert.Equal(9007199254740993UL, identity.Result.GetProperty("uid").GetUInt64());
        Assert.True(original.RootElement.GetProperty("extra").GetBoolean());
        using JsonDocument login = JsonDocument.Parse(packets[1].AsMemory(16, packets[1].Length - 17));
        Assert.Equal("9007199254740993", login.RootElement.GetProperty("resultObj").GetProperty("uid").GetString());
        Assert.Equal("success", login.RootElement.GetProperty("message").GetString());
        Assert.False(login.RootElement.TryGetProperty("contextId", out _));
    }

    [Fact]
    public void LinkedIdsAreNormalizedAndServerRestrictionsArePreserved()
    {
        using JsonDocument response = JsonDocument.Parse("""
            {"code":0,"result":{"uid":42,"token":"fixture-token","isAdult":false,
             "thirdUsers":[{"uid":18446744073709551615,"thirdType":2,"extra":true}],
             "guardianBlockMsg":"fixture restriction"}}
            """);
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 2);
        using JsonDocument info = JsonDocument.Parse(NteGlobalUserInfo.Encode(identity));
        Assert.Equal("18446744073709551615", info.RootElement.GetProperty("thirdUsers")[0].GetProperty("uid").GetString());
        Assert.True(info.RootElement.GetProperty("thirdUsers")[0].GetProperty("extra").GetBoolean());
        Assert.False(info.RootElement.GetProperty("isAdult").GetBoolean());
        Assert.Equal("fixture restriction", info.RootElement.GetProperty("guardianBlockMsg").GetString());
        Assert.Equal(JsonValueKind.Number, identity.Result.GetProperty("thirdUsers")[0].GetProperty("uid").ValueKind);
    }

    [Fact]
    public void AbsentRestrictionFieldsAreNotFabricated()
    {
        using JsonDocument response = JsonDocument.Parse("""{"code":0,"result":{"uid":42,"token":"fixture-token"}}""");
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 2);
        using JsonDocument info = JsonDocument.Parse(NteGlobalUserInfo.Encode(identity));
        Assert.False(info.RootElement.TryGetProperty("isAdult", out _));
        Assert.False(info.RootElement.TryGetProperty("ageCountryCode", out _));
    }
}
