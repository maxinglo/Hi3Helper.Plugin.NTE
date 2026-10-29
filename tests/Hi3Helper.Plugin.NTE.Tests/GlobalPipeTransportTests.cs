using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalPipeTransportTests
{
    [Fact]
    public async Task ConcurrentMetadataCannotSplitUserInfoAndLogin()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        string name = "Collapse.NTE.PipeFixture." + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token));
        NteGlobalPipeTransport transport = new(server);
        using JsonDocument response = JsonDocument.Parse("""{"code":0,"result":{"uid":123,"token":"fixture","loginType":2}}""");
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 2);
        Task<List<uint>> reading = ReadCommandsAsync(client, 48, deadline.Token);
        List<Task> sending = [];
        for (int i = 0; i < 16; i++)
        {
            sending.Add(transport.SendLoginAsync(identity, deadline.Token));
            sending.Add(transport.SendLanguageAsync("zh-TW", deadline.Token));
        }
        await Task.WhenAll(sending);
        List<uint> commands = await reading;
        Assert.Equal(16, commands.Count(value => value == 1546));
        Assert.Equal(16, commands.Count(value => value == 1539));
        for (int i = 0; i < commands.Count; i++)
            if (commands[i] == 1539) Assert.Equal(1538u, commands[++i]);
    }

    [Fact]
    public async Task CancelledWriteDoesNotConsumeTheNextReply()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        string name = "Collapse.NTE.PipeFixture." + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(deadline.Token), client.ConnectAsync(deadline.Token));
        NteGlobalPipeTransport transport = new(server);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendLanguageAsync("en", cancelled.Token));
        Task<List<uint>> reading = ReadCommandsAsync(client, 1, deadline.Token);
        await transport.SendLanguageAsync("ja", deadline.Token);
        Assert.Equal([1546u], await reading);
    }

    private static async Task<List<uint>> ReadCommandsAsync(Stream stream, int count, CancellationToken token)
    {
        List<uint> result = [];
        for (int i = 0; i < count; i++)
        {
            byte[] header = new byte[12];
            await stream.ReadExactlyAsync(header, token);
            Assert.Equal(0x6B647331u, BinaryPrimitives.ReadUInt32LittleEndian(header));
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            Assert.InRange(length, 17, 65536);
            byte[] body = new byte[length - 12];
            await stream.ReadExactlyAsync(body, token);
            result.Add(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        }
        return result;
    }
}
