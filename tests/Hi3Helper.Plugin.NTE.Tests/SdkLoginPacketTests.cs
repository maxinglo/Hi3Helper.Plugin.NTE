using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkLoginPacketTests
{
    private static ProbeIdentityEnvelope Identity(ProbeIdentityStage stage, bool accepted = true) =>
        ProbeIdentityEnvelope.Parse(stage, 200, Encoding.UTF8.GetBytes(stage == ProbeIdentityStage.Game
            ? (accepted ? """{"code":0,"uid":"game-fixture","token":"game-secret-fixture"}""" : """{"code":1}""")
            : """{"code":0,"result":{"userId":"channel-fixture","token":"channel-secret-fixture"}}"""));

    [Fact]
    public void FullPacketMatchesBothNativeProducerAndReceiverFieldOffsets()
    {
        using var game = Identity(ProbeIdentityStage.Game);
        using var channel = Identity(ProbeIdentityStage.Channel);
        using var info = JsonDocument.Parse("""{"fixture":true}""");
        const int host = 0x1234;
        using var packet = ProbeLoginPacket.Encode(game, channel, 4, 9, info.RootElement, host);
        byte[] wire = packet.CopyEncodedBytes();
        Assert.Equal(0x594, wire.Length);
        Assert.Equal(new byte[] { 0x31, 0x73, 0x64, 0x6B, 2, 0, 0, 0, 0x94, 5, 0, 0 }, wire[..12]);
        // Model the SDK receiver's body decoder independently of the encoder.
        byte[] plain = wire.ToArray();
        for (int i = 12; i < plain.Length; i++) plain[i] ^= host & 0xff;
        static string Field(byte[] value, int offset, int capacity) => Encoding.UTF8.GetString(value.AsSpan(offset, capacity)).Split('\0')[0];
        Assert.Equal("game-fixture", Field(plain, 12, 64));
        Assert.Equal("game-secret-fixture", Field(plain, 76, 128));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(204)));
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(208)));
        Assert.Equal("channel-fixture", Field(plain, 212, 64));
        Assert.Equal("channel-secret-fixture", Field(plain, 276, 128));
        Assert.Equal("""{"fixture":true}""", Field(plain, 404, 1024));
        Assert.Equal(0, plain[^1]);
        Assert.NotEqual("game-fixture", Field(wire, 12, 64));
        Assert.DoesNotContain("game-secret-fixture", JsonSerializer.Serialize(packet.Summary) + packet);
        packet.Dispose();
        Assert.Throws<ObjectDisposedException>(() => packet.CopyEncodedBytes());
    }

    [Fact]
    public void RejectedOrSwappedIdentityStagesCannotBuildAPacket()
    {
        using var game = Identity(ProbeIdentityStage.Game);
        using var rejected = Identity(ProbeIdentityStage.Game, false);
        using var channel = Identity(ProbeIdentityStage.Channel);
        using var info = JsonDocument.Parse("{}");
        Assert.Throws<InvalidOperationException>(() => ProbeLoginPacket.Encode(rejected, channel, 4, 9, info.RootElement, 100));
        Assert.Throws<InvalidOperationException>(() => ProbeLoginPacket.Encode(channel, game, 4, 9, info.RootElement, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeLoginPacket.Encode(game, channel, -1, 9, info.RootElement, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeLoginPacket.Encode(game, channel, 4, 9, info.RootElement, 0));
    }

    [Fact]
    public void UserInformationMustFitIncludingItsTerminator()
    {
        using var game = Identity(ProbeIdentityStage.Game);
        using var channel = Identity(ProbeIdentityStage.Channel);
        using var exact = JsonDocument.Parse("{\"value\":\"" + new string('x', 1011) + "\"}");
        using var tooLong = JsonDocument.Parse("{\"value\":\"" + new string('x', 1012) + "\"}");
        Assert.Equal(1023, Encoding.UTF8.GetByteCount(exact.RootElement.GetRawText()));
        using var packet = ProbeLoginPacket.Encode(game, channel, 4, 9, exact.RootElement, 100);
        Assert.Throws<InvalidDataException>(() => ProbeLoginPacket.Encode(game, channel, 4, 9, tooLong.RootElement, 100));
    }
}
