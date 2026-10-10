using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Reads and writes an <see cref="IPEndPoint"/> as <c>address:port</c>; an empty text is no endpoint.
/// </summary>
public sealed class IpEndPointJsonConverter : JsonConverter<IPEndPoint>
{
    #region JsonConverter

    /// <inheritdoc />
    public override IPEndPoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? string.Empty;

        if (text.Length == 0)
        {
            return null;
        }

        return IpJson.TryParseEndpoint(text, out var endpoint) ? endpoint : throw new JsonException("invalid IP endpoint");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }

    #endregion // JsonConverter
}