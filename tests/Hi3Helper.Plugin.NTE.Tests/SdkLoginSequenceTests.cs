using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkLoginSequenceTests
{
    private static ProbeIdentityEnvelope Channel() => ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Channel, 200,
        """{"code":0,"result":{"userId":"fixture-channel","token":"fixture-channel-token"}}"""u8);
    private static ProbeIdentityEnvelope Game(object extra) => ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200,
        JsonSerializer.SerializeToUtf8Bytes(new { code = 0, uid = "fixture-game", token = "fixture-game-token", extraOne = extra }));

    [Fact]
    public void ExtraOnePrecedesLoginWithItsOwnLengthAndTerminator()
    {
        using var channel = Channel();
        using var game = Game(new { fixturePrivate = "private-user-information" });
        using var sequence = ProbeLoginSequence.EncodeCurrentBridge(game, channel, 4, 9, 0x1234);
        byte[][] packets = sequence.CopyEncodedPackets();
        Assert.Equal(new uint[] { 0x40D, 2 }, packets.Select(p => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4))));
        foreach (byte[] packet in packets)
            for (int i = 12; i < packet.Length; i++) packet[i] ^= 0x34;
        int length = BinaryPrimitives.ReadInt32LittleEndian(packets[0].AsSpan(12));
        Assert.Equal(length + 17, packets[0].Length);
        Assert.Equal(0, packets[0][16 + length]);
        using var extra = JsonDocument.Parse(packets[0].AsMemory(16, length));
        Assert.Equal("private-user-information", extra.RootElement.GetProperty("fixturePrivate").GetString());
        Assert.Equal(game.CopyExtraInformation(), packets[1].AsSpan(404, length).ToArray());
        string diagnostic = JsonSerializer.Serialize(game.Summary) + JsonSerializer.Serialize(sequence.Summary) + sequence;
        Assert.DoesNotContain("private-user-information", diagnostic);
        sequence.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sequence.CopyEncodedPackets());
    }

    [Fact]
    public void ExtendedInformationSurvivesBeyondTheLegacyCapacity()
    {
        using var channel = Channel();
        using var game = Game(new { fixture = new string('x', 2000) });
        using var sequence = ProbeLoginSequence.EncodeCurrentBridge(game, channel, 4, 9, 100);
        byte[][] packets = sequence.CopyEncodedPackets();
        Assert.True(packets[0].Length > 2000);
        foreach (byte[] packet in packets)
            for (int i = 12; i < packet.Length; i++) packet[i] ^= 100;
        Assert.True(BinaryPrimitives.ReadInt32LittleEndian(packets[0].AsSpan(12)) > 1024);
        Assert.All(packets[1].Skip(404), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData("""{"code":0,"uid":"fixture","token":"secret","extraOne":"not-an-object"}""")]
    [InlineData("""{"code":0,"uid":"fixture","token":"secret","extraOne":{},"extraOne":{}}""")]
    public void InvalidExtendedInformationCannotSupplyGameIdentity(string response)
    {
        using var parsed = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200, Encoding.UTF8.GetBytes(response));
        Assert.False(parsed.IdentityAvailable);
        Assert.Throws<InvalidOperationException>(() => parsed.CopyExtraInformation());
    }
}
