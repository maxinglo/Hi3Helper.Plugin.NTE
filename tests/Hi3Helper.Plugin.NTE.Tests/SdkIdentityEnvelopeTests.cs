using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkIdentityEnvelopeTests
{
    [Fact]
    public void ChannelAndGameEnvelopesUseDifferentNativeContracts()
    {
        byte[] channel = Encoding.UTF8.GetBytes("""{"code":0,"result":{"userId":18446744073709551615,"token":"channel-fixture-token"}}""");
        byte[] game = Encoding.UTF8.GetBytes("""{"code":0,"msg":"ok","uid":"game-fixture-user","token":"game-fixture-token"}""");
        using var validChannel = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Channel, 200, channel);
        using var validGame = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, game);
        using var wrongChannel = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Channel, 200, game);
        using var wrongGame = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, channel);
        Assert.True(validChannel.IdentityAvailable);
        Assert.True(validGame.IdentityAvailable);
        Assert.False(wrongChannel.IdentityAvailable);
        Assert.False(wrongGame.IdentityAvailable);
        Assert.Equal("18446744073709551615", Encoding.UTF8.GetString(validChannel.CopyIdentity().Uid));
        Assert.Equal("game-fixture-user", Encoding.UTF8.GetString(validGame.CopyIdentity().Uid));
    }

    [Theory]
    [InlineData(302, """{"code":0,"uid":"fixture","token":"secret"}""")]
    [InlineData(500, """{"code":0,"uid":"fixture","token":"secret"}""")]
    [InlineData(200, """{"code":1001,"uid":"fixture","token":"secret"}""")]
    [InlineData(200, """{"code":"0","uid":"fixture","token":"secret"}""")]
    [InlineData(200, """{"code":0,"uid":"fixture","token":""}""")]
    [InlineData(200, """{"code":0,"uid":"fixture","token":"first","token":"second"}""")]
    [InlineData(200, """{"code":9,"code":0,"uid":"fixture","token":"secret"}""")]
    [InlineData(200, """{"code":0,"uid":"fixture","token":"a\u0000b"}""")]
    [InlineData(200, "[]")]
    [InlineData(200, "<html>service error</html>")]
    public void FailureOrAmbiguousResponseCannotSupplyIdentity(int status, string body)
    {
        using var result = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, status, Encoding.UTF8.GetBytes(body));
        Assert.False(result.IdentityAvailable);
        Assert.Throws<InvalidOperationException>(() => result.CopyIdentity());
    }

    [Fact]
    public void NativeCapacityCountsUtf8BytesAndReservesTerminators()
    {
        byte[] Response(string token) => JsonSerializer.SerializeToUtf8Bytes(new { code = 0, uid = "fixture", token });
        using var fits = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, Response(new string('x', 127)));
        using var full = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, Response(new string('x', 128)));
        using var unicodeFits = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, Response(new string('界', 42)));
        using var unicodeTooLong = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, Response(new string('界', 43)));
        Assert.True(fits.IdentityAvailable);
        Assert.False(full.IdentityAvailable);
        Assert.True(unicodeFits.IdentityAvailable);
        Assert.False(unicodeTooLong.IdentityAvailable);
    }

    [Fact]
    public void SummariesAndDisposedObjectsCannotExposeCredentials()
    {
        using var result = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200,
            Encoding.UTF8.GetBytes("""{"code":0,"uid":"private-fixture-user","token":"private-fixture-token"}"""));
        string diagnostic = JsonSerializer.Serialize(result.Summary) + result;
        Assert.DoesNotContain("private-fixture-user", diagnostic);
        Assert.DoesNotContain("private-fixture-token", diagnostic);
        using JsonDocument summary = JsonDocument.Parse(JsonSerializer.Serialize(result.Summary));
        Assert.False(summary.RootElement.GetProperty("gameSessionAccepted").GetBoolean());
        result.Dispose();
        Assert.False(result.IdentityAvailable);
        Assert.Throws<ObjectDisposedException>(() => result.CopyIdentity());
    }
}
