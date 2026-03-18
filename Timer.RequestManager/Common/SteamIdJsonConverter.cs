using System;
using Newtonsoft.Json;
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Common;

/// <summary>
/// Newtonsoft.Json converter for <see cref="SteamID"/>.
/// Handles Int64/UInt64/string ↔ SteamID conversion, used by SqlSugar's internal JSON deserialization.
/// </summary>
internal sealed class SteamIdJsonConverter : JsonConverter<SteamID>
{
    public override SteamID ReadJson(JsonReader reader, Type objectType, SteamID existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        return reader.TokenType switch
        {
            JsonToken.Integer => new SteamID(Convert.ToUInt64(reader.Value)),
            JsonToken.String  => new SteamID(ulong.Parse((string)reader.Value!)),
            JsonToken.Null    => default,
            _                 => throw new JsonSerializationException($"Unexpected token {reader.TokenType} when deserializing SteamID")
        };
    }

    public override void WriteJson(JsonWriter writer, SteamID value, JsonSerializer serializer)
    {
        writer.WriteValue(value.AsPrimitive());
    }
}
