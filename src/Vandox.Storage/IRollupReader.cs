namespace Vandox.Storage;

/// <summary>
/// Reads rollup buckets.
/// </summary>
public interface IRollupReader
{
    #region Methods

    /// <summary>
    /// Returns the rollup buckets of one series in a range, oldest first.
    /// </summary>
    /// <param name="query">The series, tier and range</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the buckets</returns>
    Task<IReadOnlyList<RollupBucket>> RollupsAsync(RollupQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}