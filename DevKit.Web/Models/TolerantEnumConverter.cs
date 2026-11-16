using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevKit.Web.Models;

/// <summary>
/// Reads and writes a settings choice by name. A value this build does not know, as a later DevKit may save, reads as
/// the type's default rather than making all of a person's settings unreadable; the stored value itself is only
/// replaced when the person changes that setting (saves write only what changed, see ClientSettingsStore).
/// </summary>
public sealed class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String when Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var named) && Enum.IsDefined(named) => named,
            JsonTokenType.Number when reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(TEnum), number) => (TEnum)Enum.ToObject(typeof(TEnum), number),
            JsonTokenType.String or JsonTokenType.Number => default,
            _ => throw new JsonException($"A {typeof(TEnum).Name} setting must be a name or a number.")
        };

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
