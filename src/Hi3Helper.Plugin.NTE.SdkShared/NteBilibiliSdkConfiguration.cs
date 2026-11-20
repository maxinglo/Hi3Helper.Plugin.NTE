using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nte.Login;

// The installed launcher's configuration reader joins nested property names
// with underscores. The SDK receives these four resolved channel properties
// and the game's installed version in a UTF-8 object.
internal static class NteBilibiliSdkConfiguration
{
    // The official Bilibili installer writes this attribution to media.txt.
    // The launcher update inventory does not distribute the installer marker.
    private const int InstallerSubchannel = 6372;
    private static readonly (string Source, string Destination)[] Fields =
    [
        ("init_channel_appid", "gameId"),
        ("init_channel_app_key", "appKey"),
        ("init_channel_cpid", "merchantId"),
        ("init_channel_server_id", "serverId")
    ];

    internal static int ReadSubchannel(string launcherDirectory)
    {
        string path = Path.Combine(launcherDirectory, "media.txt");
        if (!File.Exists(path)) return InstallerSubchannel;
        if (new FileInfo(path).Length > 4096)
            throw new NteCnLaunchException("BilibiliSdkInitFailed");
        const string prefix = "cpsid:";
        int subchannel = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (subchannel != 0 || !int.TryParse(line.AsSpan(prefix.Length).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out subchannel) || subchannel <= 0)
                throw new NteCnLaunchException("BilibiliSdkInitFailed");
        }
        return subchannel > 0 ? subchannel : throw new NteCnLaunchException("BilibiliSdkInitFailed");
    }

    internal static byte[] Encode(JsonElement config, string installedVersion)
    {
        if (!Regex.IsMatch(installedVersion, @"\A[0-9]+(?:\.[0-9]+){1,3}\z"))
            throw new InvalidDataException("Bilibili SDK requires the installed game version.");
        Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);
        Collect(config, "", values);

        using MemoryStream buffer = new(512);
        try
        {
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartObject();
                foreach ((string source, string destination) in Fields)
                {
                    if (!values.TryGetValue(source, out JsonElement value))
                        throw new InvalidDataException("Bilibili SDK channel configuration is incomplete.");
                    if (value.ValueKind == JsonValueKind.String && !value.ValueEquals(""))
                    {
                        writer.WritePropertyName(destination);
                        value.WriteTo(writer);
                    }
                    else if (source != "init_channel_app_key" && value.ValueKind == JsonValueKind.Number &&
                        value.TryGetUInt64(out ulong number))
                    {
                        writer.WriteString(destination, number.ToString(CultureInfo.InvariantCulture));
                    }
                    else throw new InvalidDataException("Bilibili SDK channel configuration has an invalid field.");
                }
                writer.WriteString("gameVersion", installedVersion);
                writer.WriteEndObject();
            }
            buffer.WriteByte(0);
            return buffer.ToArray();
        }
        finally
        {
            if (buffer.TryGetBuffer(out ArraySegment<byte> bytes)) CryptographicOperations.ZeroMemory(bytes.AsSpan());
        }
    }

    private static void Collect(JsonElement source, string prefix, Dictionary<string, JsonElement> values)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty property in source.EnumerateObject())
        {
            string path = prefix + property.Name;
            if (Fields.Any(field => field.Source == path) && !values.TryAdd(path, property.Value))
                throw new InvalidDataException("Ambiguous Bilibili SDK channel configuration.");
            if (Fields.Any(field => field.Source.StartsWith(path + "_", StringComparison.Ordinal)))
                Collect(property.Value, path + "_", values);
        }
    }
}
