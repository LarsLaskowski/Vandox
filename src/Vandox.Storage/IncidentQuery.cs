namespace Vandox.Storage;

/// <summary>
/// Selects the incidents that overlap the half-open range [From, To).
/// </summary>
public sealed class IncidentQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the most incidents returned.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}