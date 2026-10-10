namespace Vandox.Storage;

/// <summary>
/// Selects records of one kind in the half-open time range [From, To).
/// </summary>
public sealed class RecordQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the kind; required.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source; optional.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the metric name; optional, only with the kind metric and a source.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the range; required.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range; required, after <see cref="From"/>.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the most records returned: 1 to <see cref="StorageLimits.MaxQueryLimit"/>.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}