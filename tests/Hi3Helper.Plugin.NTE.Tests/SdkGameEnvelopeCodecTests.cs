using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkGameEnvelopeCodecTests
{
    [Fact]
    public void WireBodyIsRawBase64OfPkcs7AesJsonAndUsesTheGameApplicationKey()
    {
        byte[] json = "{\"appId\":\"1289\",\"device\":\"{\\\"name\\\":\\\"fixture\\\"}\"}"u8.ToArray();
        using ProbeGameEnvelopeCodec codec = new("fedcba9876543210"u8);
        byte[] wire = codec.Encode(json);
        Assert.DoesNotContain((byte)'&', wire);
        Assert.DoesNotContain((byte)'%', wire);
        Assert.DoesNotContain((byte)'{', wire);
        using Aes aes = Aes.Create(); aes.Key = "fedcba9876543210"u8.ToArray();
        byte[] plain = aes.DecryptEcb(Convert.FromBase64String(Encoding.ASCII.GetString(wire)), PaddingMode.PKCS7);
        Assert.Equal(json, plain);
        Assert.Equal(json, codec.Decode(wire));
    }

    [Fact]
    public void PlainJsonMalformedCipherAndDisposedKeysAreRejected()
    {
        using ProbeGameEnvelopeCodec codec = new("fedcba9876543210"u8);
        Assert.Throws<FormatException>(() => codec.Decode("{\"code\":0}"u8));
        Assert.Throws<InvalidDataException>(() => codec.Decode("AA=="u8));
        Assert.Throws<InvalidDataException>(() => new ProbeGameEnvelopeCodec("short"u8));
        codec.Dispose();
        Assert.Throws<ObjectDisposedException>(() => codec.Encode("{}"u8));
        Assert.Throws<ObjectDisposedException>(() => codec.Decode("AAAA"u8));
    }
}
