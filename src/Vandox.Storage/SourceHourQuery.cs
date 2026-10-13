namespace Vandox.Storage;

/// <summary>
/// The query for the hours in which a source has lines.
/// </summary>
public sealed class SourceHourQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the largest number of hours.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}