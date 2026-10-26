namespace Vandox.Storage;

#pragma warning disable RH2003, S2325
/// <summary>
/// Estimates an agent's clock offset from clock bounds.
/// </summary>
public static class ClockEstimator
{
    #region Methods

    /// <summary>
    /// Gets the lower bound of agent clock minus backend clock a record gives.
    /// </summary>
    /// <param name="capturedAt">The capture time.</param>
    /// <param name="receivedAt">The receive time.</param>
    /// <returns>The difference in nanoseconds, saturated to the range of a long.</returns>
    public static long BoundOf(DateTimeOffset capturedAt, DateTimeOffset receivedAt)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the offset estimate.
    /// </summary>
    /// <param name="countedBuckets">The buckets of counted samples, oldest first.</param>
    /// <param name="runBuckets">The buckets of run-timed records, oldest first.</param>
    /// <returns>The offset in nanoseconds.</returns>
    public static long Estimate(IReadOnlyList<ClockSample> countedBuckets, IReadOnlyList<ClockSample> runBuckets)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the bucket a receive time belongs to.
    /// </summary>
    /// <param name="receivedAt">The receive time.</param>
    /// <returns>The receive time floored to the bucket width.</returns>
    public static DateTimeOffset BucketOf(DateTimeOffset receivedAt)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}