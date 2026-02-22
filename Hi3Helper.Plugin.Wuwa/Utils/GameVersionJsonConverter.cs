using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hi3Helper.Plugin.Core.Management;

namespace Hi3Helper.Plugin.Wuwa.Utils;

/// <summary>
/// Concrete (non-generic) JSON converter for <see cref="GameVersion"/> that the
/// System.Text.Json source generator can discover and instantiate (avoids SYSLIB1220).
/// </summary>
public sealed class GameVersionJsonConverter : JsonConverter<GameVersion>
{
    public override GameVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (GameVersion.TryParse(reader.ValueSpan, null, out GameVersion result))
        {
            return result;
        }

        throw new JsonException($"The JSON value could not be converted to {nameof(GameVersion)}.");
    }

    public override void Write(Utf8JsonWriter writer, GameVersion value, JsonSerializerOptions options)
    {
        Span<byte> buffer = stackalloc byte[256];
        if (value.TryFormat(buffer, out int written, ReadOnlySpan<char>.Empty, null))
        {
            writer.WriteStringValue(buffer[..written]);
        }
    }
}
