namespace Vandox.Storage;

/// <summary>
/// A read of the newest state of each service unit.
/// </summary>
public sealed class LatestServiceQuery
{
    #region Properties

    /// <summary>
    /// Gets the age after which a unit that reports no more is left out.
    /// </summary>
    public TimeSpan Retirement { get; init; }

    /// <summary>
    /// Gets the maximum number of states returned.
    /// </summary>
    public int Limit { get; init; }

    /// <summary>
    /// Gets the latest capture time that counts.
    /// </summary>
    public DateTimeOffset Until { get; init; }

    #endregion // Properties
}