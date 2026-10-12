namespace Vandox.Core.Signatures;

/// <summary>
/// One detected event: the lines of one known event type that count as one.
/// </summary>
public sealed class DetectedEvent
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the signature.
    /// </summary>
    public string SignatureId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the capture time of the first line.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Gets or sets the first non-empty host of the lines, or empty.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text fields.
    /// </summary>
    public IReadOnlyDictionary<string, string> Texts { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the number fields.
    /// </summary>
    public IReadOnlyDictionary<string, long> Numbers { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the record IDs of the lines, at most the first 256.
    /// </summary>
    public IReadOnlyList<long> RecordIds { get; set; } = Array.AsReadOnly(Array.Empty<long>());

    /// <summary>
    /// Gets or sets the number of lines.
    /// </summary>
    public long LineCount { get; set; }

    #endregion // Properties
}