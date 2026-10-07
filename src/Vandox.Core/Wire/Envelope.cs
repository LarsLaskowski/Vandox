using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vandox.Core.Wire;

/// <summary>
/// The decoded form of a record line: origin and receive time are not part of the wire format.
/// </summary>
internal sealed class Envelope
{
    #region Properties

    /// <summary>
    /// Gets or sets the kind of the payload.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sequence number.
    /// </summary>
    [JsonPropertyName("seq")]
    public ulong Seq { get; set; }

    /// <summary>
    /// Gets or sets the capture time.
    /// </summary>
    [JsonPropertyName("captured_at")]
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Gets or sets the payload document.
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }

    #endregion // Properties
}