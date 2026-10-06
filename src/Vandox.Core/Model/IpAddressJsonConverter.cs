using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Reads and writes an <see cref="IPAddress"/> as its text; an empty text is no address.
/// </summary>
public sealed class IpAddressJsonConverter : JsonConverter<IPAddress>
{
    #region JsonConverter

    /// <inheritdoc />
    public override IPAddress? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? string.Empty;

        if (text.Length == 0)
        {
            return null;
        }

        return IpJson.TryParseAddress(text, out var address) ? address : throw new JsonException("invalid IP address");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    #endregion // JsonConverter
}