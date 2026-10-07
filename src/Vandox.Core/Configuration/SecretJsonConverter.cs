using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Configuration;

/// <summary>
/// Writes a <see cref="Secret"/> as the redacted text; reading is not supported.
/// </summary>
public sealed class SecretJsonConverter : JsonConverter<Secret>
{
    #region JsonConverter

    /// <inheritdoc />
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException("a secret is never read from JSON");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Secret.RedactedText);
    }

    #endregion // JsonConverter
}