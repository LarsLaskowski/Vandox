namespace Vandox.Storage;

/// <summary>
/// The largest clock bound of one bucket of receive time.
/// </summary>
/// <param name="Bucket">The receive time floored to the bucket width.</param>
/// <param name="BoundNs">The bucket's largest lower bound of agent clock minus backend clock in nanoseconds.</param>
public readonly record struct ClockSample(DateTimeOffset Bucket, long BoundNs);