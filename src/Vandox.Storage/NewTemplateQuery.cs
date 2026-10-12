namespace Vandox.Storage;

/// <summary>
/// Selects the templates first seen in the half-open time range [From, To).
/// </summary>
public sealed class NewTemplateQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the source; empty for every source.
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
    /// Gets or sets the most templates returned: 1 to <see cref="StorageLimits.MaxQueryLimit"/>.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}