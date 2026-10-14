namespace Vandox.Storage;

/// <summary>
/// A log line with its neighbours.
/// </summary>
public sealed class LogContext
{
    #region Properties

    /// <summary>
    /// Gets or sets the line.
    /// </summary>
    public StoredRecord Line { get; set; } = new();

    /// <summary>
    /// Gets or sets the lines before it, in time order.
    /// </summary>
    public IReadOnlyList<StoredRecord> Before { get; set; } = [];

    /// <summary>
    /// Gets or sets the lines after it, in time order.
    /// </summary>
    public IReadOnlyList<StoredRecord> After { get; set; } = [];

    #endregion // Properties
}