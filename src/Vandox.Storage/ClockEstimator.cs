namespace Vandox.Storage;

/// <summary>
/// Estimates an agent's clock offset from clock bounds.
/// </summary>
public static class ClockEstimator
{
    #region Constants

    private const long NanosecondsPerTick = 100;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Gets the lower bound of agent clock minus backend clock a record gives.
    /// </summary>
    /// <param name="capturedAt">The capture time.</param>
    /// <param name="receivedAt">The receive time.</param>
    /// <returns>The difference in nanoseconds, saturated to the range of a long.</returns>
    public static long BoundOf(DateTimeOffset capturedAt, DateTimeOffset receivedAt)
    {
        return Saturate((Int128)(capturedAt.UtcTicks - receivedAt.UtcTicks) * NanosecondsPerTick);
    }

    /// <summary>
    /// Gets the offset estimate.
    /// </summary>
    /// <param name="countedBuckets">The buckets of counted samples, oldest first.</param>
    /// <param name="runBuckets">The buckets of run-timed records, oldest first.</param>
    /// <returns>The offset in nanoseconds.</returns>
    public static long Estimate(IReadOnlyList<ClockSample> countedBuckets, IReadOnlyList<ClockSample> runBuckets)
    {
        if (countedBuckets.Count > 0)
        {
            return WindowMaximum(countedBuckets);
        }

        return runBuckets.Count > 0 ? Math.Max(0, WindowMaximum(runBuckets)) : 0;
    }

    /// <summary>
    /// Decides whether a counted sample measures the offset: its bound is not more than the run allowance below the offset in effect, or the previous counted
    /// reading lies <see cref="TimelineLimits.ConfirmSpacing"/> (30 seconds) to <see cref="TimelineLimits.SampleWindow"/> before it in capture and in receive time and within the
    /// run allowance of its bound.
    /// </summary>
    /// <param name="candidate">The sample</param>
    /// <param name="previous">The previous counted reading, if any</param>
    /// <param name="offsetInEffectNs">The offset in effect in nanoseconds</param>
    /// <returns><see langword="true"/> when the sample measures</returns>
    public static bool Measures(ClockReading candidate, ClockReading? previous, long offsetInEffectNs)
    {
        var allowance = (Int128)TimelineLimits.RunAllowance.Ticks * NanosecondsPerTick;

        if (candidate.BoundNs >= (Int128)offsetInEffectNs - allowance)
        {
            return true;
        }

        if (previous is null)
        {
            return false;
        }

        var earlier = previous.Value;

        return IsSpaced(candidate.CapturedAt, earlier.CapturedAt) && IsSpaced(candidate.ReceivedAt, earlier.ReceivedAt) && Int128.Abs((Int128)candidate.BoundNs - earlier.BoundNs) <= allowance;
    }

    /// <summary>
    /// Gets the bucket a receive time belongs to.
    /// </summary>
    /// <param name="receivedAt">The receive time.</param>
    /// <returns>The receive time floored to the bucket width.</returns>
    public static DateTimeOffset BucketOf(DateTimeOffset receivedAt)
    {
        var width = TimelineLimits.SampleBucket.Ticks;
        var ticks = receivedAt.UtcTicks;
        var floored = ticks - (((ticks % width) + width) % width);

        return new DateTimeOffset(floored, TimeSpan.Zero);
    }

    /// <summary>
    /// Gets a value indicating whether an instant lies the confirmation spacing to the sample window after another one.
    /// </summary>
    /// <param name="later">The later instant</param>
    /// <param name="earlier">The earlier instant</param>
    /// <returns><see langword="true"/> when the distance is within the spacing and the window</returns>
    private static bool IsSpaced(DateTimeOffset later, DateTimeOffset earlier)
    {
        var distance = (Int128)later.UtcTicks - earlier.UtcTicks;

        return distance >= TimelineLimits.ConfirmSpacing.Ticks && distance <= TimelineLimits.SampleWindow.Ticks;
    }

    /// <summary>
    /// Gets the largest bound of the buckets within the sample window before the newest bucket.
    /// </summary>
    /// <param name="buckets">The buckets, not empty.</param>
    /// <returns>The largest bound in nanoseconds.</returns>
    private static long WindowMaximum(IReadOnlyList<ClockSample> buckets)
    {
        var newest = buckets.Max(sample => sample.Bucket.UtcTicks);
        var from = newest - TimelineLimits.SampleWindow.Ticks;

        return buckets.Where(sample => sample.Bucket.UtcTicks >= from)
                      .Max(sample => sample.BoundNs);
    }

    /// <summary>
    /// Saturates a difference to the range of a long.
    /// </summary>
    /// <param name="value">The difference.</param>
    /// <returns>The value within <see cref="long.MinValue"/> and <see cref="long.MaxValue"/>.</returns>
    private static long Saturate(Int128 value)
    {
        if (value > long.MaxValue)
        {
            return long.MaxValue;
        }

        return value < long.MinValue ? long.MinValue : (long)value;
    }

    #endregion // Methods
}