using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Serializes a payload to UTF-8 JSON in a bounded buffer.
/// </summary>
internal static class PayloadJson
{
    #region Methods

    /// <summary>
    /// Serializes a payload exactly as <see cref="PayloadRegistry.Options"/> does.
    /// </summary>
    /// <param name="payload">The payload</param>
    /// <param name="maxBytes">The most UTF-8 bytes of the JSON</param>
    /// <returns>The JSON, or <c>null</c> when it is longer than <paramref name="maxBytes"/></returns>
    internal static byte[]? Serialize(IPayload payload, int maxBytes)
    {
        var buffer = Write(payload, maxBytes);

        return buffer.Exceeded ? null : buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Checks whether the JSON of a payload is not longer than a limit, without keeping it.
    /// </summary>
    /// <param name="payload">The payload</param>
    /// <param name="maxBytes">The most UTF-8 bytes of the JSON</param>
    /// <returns><c>true</c> when the JSON fits</returns>
    internal static bool Fits(IPayload payload, int maxBytes)
    {
        if (Write(payload, maxBytes).Exceeded)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Writes a payload into a bounded buffer with the writer options that match <see cref="PayloadRegistry.Options"/>.
    /// </summary>
    /// <param name="payload">The payload</param>
    /// <param name="maxBytes">The most bytes kept</param>
    /// <returns>The buffer</returns>
    private static BoundedBufferWriter Write(IPayload payload, int maxBytes)
    {
        var options = PayloadRegistry.Options;
        var buffer = new BoundedBufferWriter(maxBytes);
        var writerOptions = new JsonWriterOptions
                            {
                                Encoder = options.Encoder,
                                Indented = options.WriteIndented,
                                MaxDepth = options.MaxDepth
                            };

        using var writer = new Utf8JsonWriter(buffer, writerOptions);

        JsonSerializer.Serialize(writer, payload, payload.GetType(), options);
        writer.Flush();

        return buffer;
    }

    #endregion // Methods
}