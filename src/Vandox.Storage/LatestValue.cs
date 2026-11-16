namespace Vandox.Storage;

/// <summary>
/// The newest value of one label set.
/// </summary>
public sealed class LatestValue
{
    #region Properties

    /// <summary>
    /// Gets the labels.
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public double Value { get; init; }

    #endregion // Properties
}