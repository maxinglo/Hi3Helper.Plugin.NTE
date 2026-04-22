using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>
/// 解密 ResList.bin / lastdiff.bin 文件。
/// 文件格式：12字节魔术头 + 4字节明文大小(LE) + AES-128-CBC加密数据(zlib压缩)
/// </summary>
internal static class NteResListDecryptor
{
    private static ReadOnlySpan<byte> Magic => "PatcherXML0\0"u8;

    private static readonly byte[] Key = "1289@Patcher0000"u8.ToArray();
    private static readonly byte[] Iv  = "PatcherSDK000000"u8.ToArray();

    private const int MagicLength = 12;
    private const int HeaderLength = 16; // 12 bytes magic + 4 bytes plainSize

    /// <summary>
    /// 解密并解压缩 ResList.bin 数据。
    /// </summary>
    /// <param name="data">原始加密文件字节</param>
    /// <returns>解密后的 XML 字节</returns>
    public static byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        // 检查魔术头
        ReadOnlySpan<byte> magic = data[..MagicLength];
        if (!magic.SequenceEqual(Magic))
        {
            // 旧格式：未加密，直接返回
            return data.ToArray();
        }

        // 读取明文大小（4字节小端）
        uint plainSize = BitConverter.ToUInt32(data[MagicLength..HeaderLength]);

        // 密文从第16字节开始
        byte[] ciphertext = data[HeaderLength..].ToArray();

        // AES-128-CBC 解密
        byte[] decrypted;
        using (Aes aes = Aes.Create())
        {
            aes.Key = Key;
            aes.IV = Iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using ICryptoTransform decryptor = aes.CreateDecryptor();
            decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }

        // zlib 解压
        byte[] decompressed = ZlibDecompress(decrypted, plainSize);
        return decompressed;
    }

    /// <summary>
    /// 从流中读取并解密 ResList.bin。
    /// </summary>
    public static byte[] Decrypt(Stream stream)
    {
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return Decrypt(ms.ToArray().AsSpan());
    }

    private static byte[] ZlibDecompress(byte[] data, uint expectedSize)
    {
        // 尝试标准 zlib 解压（带 zlib 头）
        try
        {
            using MemoryStream input = new(data);
            using ZLibStream zlibStream = new(input, CompressionMode.Decompress);
            using MemoryStream output = new((int)expectedSize);
            zlibStream.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            // 回退：尝试 raw deflate
        }

        try
        {
            using MemoryStream input = new(data);
            using DeflateStream deflateStream = new(input, CompressionMode.Decompress);
            using MemoryStream output = new((int)expectedSize);
            deflateStream.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            // 解压失败，返回解密后的原始字节
            return data;
        }
    }
}
