namespace Vandox.Storage;

/// <summary>
/// Where the next removal step continues.
/// </summary>
public sealed class RemovalCursor
{
    #region Properties

    /// <summary>
    /// Gets the record kind.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the capture time reached.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the record ID reached.
    /// </summary>
    public long Id { get; init; }

    #endregion // Properties
}