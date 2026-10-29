using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkProbeSanitizationTests
{
    private const string AppKey = "0123456789abcdef9876543210fedcba";
    private const int ClientId = 4242;

    [Fact]
    public void StaticConfigurationDecodesWithoutRevealingPrivateValues()
    {
        using Fixture fixture = new();
        using Aes aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(AppKey[16..]);
        byte[] plaintext = """
            {"init":{"appId":"1289","appKey":"private-app-key","token":"private-token","account":"private-account",
             "baseUrl":"https://sapi.laohu.com","loginUrl":"https://sapi.laohu.com/?token=private-query",
             "channel":{"channelAppId":"10392","channelAppKey":"private-channel-key"}},
             "meta":{"platform":4,"version":"1.0.0"},"account":{"platform":"private-platform","version":"private-version"}}
            """u8.ToArray();
        fixture.Write(MakeRcc(Encoding.ASCII.GetBytes(Convert.ToBase64String(aes.EncryptEcb(plaintext, PaddingMode.PKCS7)))));
        string output = JsonSerializer.Serialize(ProbeBootConfig.Inspect(Frame(AppKey), ClientId, "1289", fixture.Root));
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.True(report.RootElement.GetProperty("decoded").GetBoolean());
        Assert.Contains("1289", output);
        Assert.Contains("init.appId", output);
        Assert.Contains("https://sapi.laohu.com/", output);
        Assert.Contains("10392", output);
        Assert.Contains("1.0.0", output);
        foreach (string forbidden in new[] { AppKey, "private-app-key", "private-token", "private-account", "private-query", "private-channel-key", "private-platform", "private-version" })
            Assert.DoesNotContain(forbidden, output);
    }

    [Fact]
    public void InvalidCiphertextReturnsOnlyAnErrorClass()
    {
        using Fixture fixture = new();
        fixture.Write(MakeRcc("invalid-private-configuration"u8.ToArray()));
        string output = JsonSerializer.Serialize(ProbeBootConfig.Inspect(Frame(AppKey), ClientId, "1289", fixture.Root));
        using JsonDocument report = JsonDocument.Parse(output);
        Assert.False(report.RootElement.GetProperty("decoded").GetBoolean());
        Assert.DoesNotContain("invalid-private-configuration", output);
        Assert.DoesNotContain(AppKey, output);
    }

    [Fact]
    public void CyclicAndTruncatedResourcesAreRejected()
    {
        using Fixture fixture = new();
        byte[] cyclic = MakeRcc("fixture"u8.ToArray());
        int tree = (int)BinaryPrimitives.ReadUInt32BigEndian(cyclic.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(cyclic.AsSpan(tree + 10), 0); // Root points back to itself.
        fixture.Write(cyclic);
        Assert.Throws<InvalidDataException>(() => ProbeBootConfig.ReadStaticResource(fixture.File, "/1289/main/oneSdkConfig.config"));
        fixture.Write(MakeRcc("fixture"u8.ToArray())[..30]);
        Assert.Throws<InvalidDataException>(() => ProbeBootConfig.ReadStaticResource(fixture.File, "/1289/main/oneSdkConfig.config"));
    }

    [Fact]
    public void CompressedLengthIsBoundedBeforeInflation()
    {
        using Fixture fixture = new();
        byte[] oversized = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(oversized, 65537);
        fixture.Write(MakeRcc(oversized, compressed: true));
        Assert.Throws<InvalidDataException>(() => ProbeBootConfig.ReadStaticResource(fixture.File, "/1289/main/oneSdkConfig.config"));
    }

    [Fact]
    public void RegistrationReportsOnlyExactPublicStaticMetadata()
    {
        HashSet<string> known = ["oneSdkConfig.config", "2.0.8.0"];
        string good = JsonSerializer.Serialize(ProbeFrameShape.Inspect(Frame(AppKey), ClientId, new HashSet<string>(), known, "1289"));
        Assert.Contains("oneSdkConfig.config", good);
        Assert.Contains("2.0.8.0", good);
        Assert.DoesNotContain(AppKey, good);
        string unknown = JsonSerializer.Serialize(ProbeFrameShape.Inspect(Frame(AppKey, "private-config-name", "secret7"), ClientId,
            new HashSet<string>(), known, "1289"));
        Assert.DoesNotContain("private-config-name", unknown);
        Assert.DoesNotContain("secret7", unknown);
    }

    private static byte[] Frame(string key, string config = "oneSdkConfig.config", string version = "2.0.8.0")
    {
        byte[] frame = new byte[1244];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x6b647331);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), (uint)frame.Length);
        void Wide(int offset, string value) => Encoding.Unicode.GetBytes(value + '\0').CopyTo(frame, 12 + offset);
        Wide(4, "1289"); Wide(0x104, key); Wide(0x204, config); Wide(0x40c, version);
        for (int i = 12; i < frame.Length; i++) frame[i] ^= unchecked((byte)ClientId);
        return frame;
    }

    private static byte[] MakeRcc(byte[] payload, bool compressed = false)
    {
        string[] names = ["", "1289", "main", "oneSdkConfig.config"];
        using MemoryStream nameTable = new();
        List<int> nameOffsets = [];
        foreach (string name in names)
        {
            nameOffsets.Add((int)nameTable.Length);
            byte[] prefix = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)name.Length);
            nameTable.Write(prefix); nameTable.Write(Encoding.BigEndianUnicode.GetBytes(name));
        }
        int payloadOffset = 24, namesOffset = 28 + payload.Length, treeOffset = namesOffset + (int)nameTable.Length;
        byte[] result = new byte[treeOffset + 4 * 22];
        "qres"u8.CopyTo(result);
        void Big32(int offset, int value) => BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset), (uint)value);
        Big32(4, 3); Big32(8, treeOffset); Big32(12, payloadOffset); Big32(16, namesOffset);
        Big32(payloadOffset, payload.Length); payload.CopyTo(result, payloadOffset + 4);
        nameTable.ToArray().CopyTo(result, namesOffset);
        for (int i = 0; i < 4; i++)
        {
            int node = treeOffset + i * 22;
            Big32(node, nameOffsets[i]);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(node + 4), (ushort)(i < 3 ? 2 : compressed ? 1 : 0));
            if (i < 3) { Big32(node + 6, 1); Big32(node + 10, i + 1); }
        }
        return result;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nte-sdk-config-test-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "NTELauncher", "ResData", "1289.dat");
        public void Write(byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(File)!); System.IO.File.WriteAllBytes(File, bytes); }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("nte-sdk-config-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("The fixture cleanup target escaped its temporary directory.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
