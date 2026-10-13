namespace Vandox.Storage;

/// <summary>
/// The filters and the page of a log exploration.
/// </summary>
public sealed class LogFilter
{
    #region Properties

    /// <summary>
    /// Gets or sets the search text; empty applies only the other filters.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the exact source; empty for any.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the exact program; empty for any.
    /// </summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the highest priority (least severe) to return; lines without a priority are excluded when set.
    /// </summary>
    public byte? MaxPriority { get; set; }

    /// <summary>
    /// Gets or sets the ID of a template whose lines are returned; 0 for none.
    /// </summary>
    public long TemplateId { get; set; }

    /// <summary>
    /// Gets or sets the start of the range, inclusive.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range, exclusive.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the position after which the page continues.
    /// </summary>
    public LogPosition? After { get; set; }

    /// <summary>
    /// Gets or sets the largest number of lines of the page.
    /// </summary>
    public int Limit { get; set; }

    /// <summary>
    /// Gets or sets the largest number of log lines one call examines.
    /// </summary>
    public int ScanLimit { get; set; }

    #endregion // Properties
}