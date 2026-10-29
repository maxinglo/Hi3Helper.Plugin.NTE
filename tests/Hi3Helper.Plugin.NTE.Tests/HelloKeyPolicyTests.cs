using System.Security.Cryptography;
using System.Runtime.Versioning;
using Nte.LoginHost.Accounts;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class HelloKeyPolicyTests
{
    [Fact]
    public void PassportNameIsScopedToTheWindowsUserAndOneAccount()
    {
        Guid a = Guid.Parse("00000000-0000-0000-0000-000000000001"), b = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Assert.Equal("S-1-5-21-1//Collapse.NTE/LoginHost/00000000000000000000000000000001", HelloKeyPolicy.Name("S-1-5-21-1", a));
        Assert.NotEqual(HelloKeyPolicy.Name("S-1-5-21-1", a), HelloKeyPolicy.Name("S-1-5-21-1", b));
        Assert.NotEqual(HelloKeyPolicy.Name("S-1-5-21-1", a), HelloKeyPolicy.Name("S-1-5-21-2", a));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void PassportCreationAvoidsUnsupportedPersistenceAndRequiresGesture()
    {
        if (!OperatingSystem.IsWindows()) return;
        CngKeyCreationParameters options = HelloKeyPolicy.CreationParameters(1);
        Assert.Null(options.KeyUsage); Assert.Null(options.ExportPolicy);
        Assert.Equal("Microsoft Passport Key Storage Provider", options.Provider!.Provider);
        Assert.All(options.Parameters, property => Assert.Equal(CngPropertyOptions.None, property.Options));
        Assert.Equal(1, BitConverter.ToInt32(options.Parameters.Single(p => p.Name == "NgcCacheType").GetValue()!));
        Assert.Equal(0, BitConverter.ToInt32(options.Parameters.Single(p => p.Name == "Key Usage").GetValue()!) & 8);
        Assert.Throws<InvalidOperationException>(() => HelloKeyPolicy.CreationParameters(0));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void NativeAccountCipherRoundTripsOneBlockAndRejectsInvalidCiphertext()
    {
        if (!OperatingSystem.IsWindows()) return;
        CngKeyCreationParameters options = new() { KeyUsage = CngKeyUsages.Decryption };
        options.Parameters.Add(new("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        using CngKey key = CngKey.Create(CngAlgorithm.Rsa, null, options);
        byte[] plain = RandomNumberGenerator.GetBytes(32), encrypted = HelloKeyCipher.Encrypt(key, plain);
        try
        {
            Assert.Equal(256, encrypted.Length);
            Assert.Equal(plain, HelloKeyCipher.Decrypt(key, encrypted));
            Assert.Throws<CryptographicException>(() => HelloKeyCipher.Decrypt(key, new byte[256]));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
