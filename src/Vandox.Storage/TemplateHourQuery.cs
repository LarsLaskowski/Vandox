namespace Vandox.Storage;

/// <summary>
/// Selects the hourly counts of one template for the hours that start in the range.
/// </summary>
public sealed class TemplateHourQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the template; positive.
    /// </summary>
    public long TemplateId { get; set; }

    /// <summary>
    /// Gets or sets the start of the range; required.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range; required, after <see cref="From"/>.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the most hours returned: 1 to <see cref="StorageLimits.MaxQueryLimit"/>.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}