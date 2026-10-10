namespace Vandox.Storage;

/// <summary>
/// Selects log lines that contain every term of the text in the half-open time range [From, To).
/// </summary>
public sealed class LogSearch
{
    #region Properties

    /// <summary>
    /// Gets or sets the search text: literal terms, no operators.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source; optional.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the range; required.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range; required, after <see cref="From"/>.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the most lines returned: 1 to <see cref="StorageLimits.MaxQueryLimit"/>.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}