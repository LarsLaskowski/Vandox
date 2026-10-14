namespace Vandox.Storage;

/// <summary>
/// One page of log lines.
/// </summary>
public sealed class LogPage
{
    #region Properties

    /// <summary>
    /// Gets or sets the lines of the page.
    /// </summary>
    public IReadOnlyList<StoredRecord> Lines { get; set; } = [];

    /// <summary>
    /// Gets or sets the position to continue from; <c>null</c> when the range is exhausted.
    /// </summary>
    public LogPosition? Next { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the scan bound ended the call before the page was full.
    /// </summary>
    public bool ScanLimitReached { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the index check ran out of its time budget, so the page comes from the scan.
    /// </summary>
    public bool IndexCheckStopped { get; set; }

    #endregion // Properties
}