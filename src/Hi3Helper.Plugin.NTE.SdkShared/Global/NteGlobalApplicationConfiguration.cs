using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nte.Login;

// The installed image is read as data only. No vendor executable or private
// function is loaded. Static application material never enters diagnostics.
internal sealed class NteGlobalApplicationConfiguration : IDisposable
{
    internal const string VerifiedImageSha256 = "0153111179C82EE2AA984B2DDB33648C692824A97A80AD104D522C742CF4E49B";
    internal byte[] ApplicationKey { get; }
    internal string PackageName { get; }
    internal Uri Service { get; }
    internal string GameArea { get; }
    internal IReadOnlyDictionary<string, string> GameServices { get; }
    internal NteGlobalApplicationConfiguration(byte[] key, string packageName, Uri service, string gameArea,
        IReadOnlyDictionary<string, string>? gameServices = null)
    {
        ApplicationKey = key.ToArray(); PackageName = packageName; Service = service; GameArea = gameArea;
        GameServices = gameServices?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) ?? [];
    }

    internal static NteGlobalApplicationConfiguration Load(string root)
    {
        string platform = Path.Combine(root, "NTEGlobal");
        byte[] image = File.ReadAllBytes(Path.Combine(platform, "NTEGlobalGame.exe"));
        byte[]? key = null, appKey = null, resource = null;
        try
        {
            if (Convert.ToHexString(SHA256.HashData(image)) != VerifiedImageSha256)
                throw new NteGlobalLaunchException("GlobalSdkUnsupported");
            using PEReader pe = new(new MemoryStream(image, false));
            const int keyRva = 0xF9AE90;
            var section = pe.PEHeaders.SectionHeaders.Single(s => keyRva >= s.VirtualAddress && keyRva + 24 <= s.VirtualAddress + s.SizeOfRawData);
            key = Convert.FromBase64String(Encoding.ASCII.GetString(image, section.PointerToRawData + keyRva - section.VirtualAddress, 24));
            resource = ProbeBootConfig.ReadStaticResource(Path.Combine(platform, "ResData", "3000001.dat"), "/3000001/globalsdkconfig.json");
            using JsonDocument json = JsonDocument.Parse(resource);
            JsonElement config = json.RootElement.GetProperty("base");
            string Decode(string name)
            {
                byte[] cipher = Convert.FromBase64String(config.GetProperty(name).GetString()!);
                byte[]? plain = null;
                try { using Aes aes = Aes.Create(); aes.Key = key; plain = aes.DecryptEcb(cipher, PaddingMode.PKCS7); return Encoding.UTF8.GetString(plain); }
                finally { CryptographicOperations.ZeroMemory(cipher); if (plain != null) CryptographicOperations.ZeroMemory(plain); }
            }
            if (Decode("APPID") != "3000001" || Decode("SdkDomain") != "https://mapi.pwsdk.com" || Decode("PackageName") != "com.hottagames.nte")
                throw new InvalidDataException("Unexpected global public configuration.");
            appKey = Encoding.UTF8.GetBytes(Decode("APPKEY"));
            if (appKey.Length != 40) throw new InvalidDataException("Unsupported global application configuration.");
            string area = Decode("area");
            if (area != "global-sg") throw new InvalidDataException("Unsupported global game area.");
            Dictionary<string, string> services = new()
            {
                ["chatCoreDomain"] = Decode("chatSdk_coreDomain"),
                ["chatTCPDomain"] = Decode("imSdk_tcpDomain"),
                ["chatIMApiDomain"] = Decode("imSdk_imapiDomain")
            };
            if (services.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('\0')))
                throw new InvalidDataException("Unsupported global game service configuration.");
            return new(appKey, "com.hottagames.nte", new("https://mapi.pwsdk.com"), area, services);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
            if (key != null) CryptographicOperations.ZeroMemory(key);
            if (appKey != null) CryptographicOperations.ZeroMemory(appKey);
            if (resource != null) CryptographicOperations.ZeroMemory(resource);
        }
    }
    internal string DecodeServiceValue(string value)
    {
        byte[] cipher = Convert.FromBase64String(value);
        byte[]? plain = null;
        try { using Aes aes = Aes.Create(); aes.Key = ApplicationKey.AsSpan(0, 16).ToArray(); plain = aes.DecryptEcb(cipher, PaddingMode.PKCS7); return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(cipher); if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }
    internal string EncodeEmail(string email)
    {
        byte[] plain = Encoding.UTF8.GetBytes(email.Trim());
        try
        {
            using Aes aes = Aes.Create(); aes.Key = ApplicationKey.AsSpan(0, 16).ToArray();
            byte[] cipher = aes.EncryptEcb(plain, PaddingMode.PKCS7);
            try { return Convert.ToBase64String(cipher); }
            finally { CryptographicOperations.ZeroMemory(cipher); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(ApplicationKey);
    public override string ToString() => "Global application configuration [key omitted]";
}
