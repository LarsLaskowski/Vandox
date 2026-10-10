using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Reads a JSON list of objects in which a <c>null</c> element reads as an element with every field at its zero value.
/// </summary>
/// <typeparam name="TItem">The element type.</typeparam>
internal sealed class NullElementListConverter<TItem> : JsonConverter<List<TItem>>
    where TItem : class, new()
{
    #region JsonConverter

    /// <inheritdoc />
    public override List<TItem>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("The token is not the start of an array.");
        }

        var list = new List<TItem>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                list.Add(new TItem());
            }
            else
            {
                list.Add(JsonSerializer.Deserialize<TItem>(ref reader, options) ?? new TItem());
            }
        }

        return list;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<TItem> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndArray();
    }

    #endregion // JsonConverter
}