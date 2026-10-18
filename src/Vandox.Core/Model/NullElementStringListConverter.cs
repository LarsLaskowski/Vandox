using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Reads a JSON list of strings in which a <c>null</c> element reads as an empty string.
/// </summary>
internal sealed class NullElementStringListConverter : JsonConverter<List<string>>
{
    #region JsonConverter

    /// <inheritdoc />
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("The token is not the start of an array.");
        }

        var list = new List<string>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            list.Add(reader.TokenType == JsonTokenType.Null ? string.Empty : reader.GetString() ?? string.Empty);
        }

        return list;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }

    #endregion // JsonConverter
}