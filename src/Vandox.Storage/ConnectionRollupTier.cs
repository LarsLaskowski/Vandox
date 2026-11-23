namespace Vandox.Storage;

/// <summary>
/// The connection buckets of one tier a fold step adds to.
/// </summary>
internal sealed class ConnectionRollupTier
{
    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    private readonly long _horizon;

    // Skeleton: removed with the implementation
#pragma warning restore S4487

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionRollupTier"/> class.
    /// </summary>
    /// <param name="width">The width of a bucket in nanoseconds</param>
    /// <param name="horizon">The first bucket start the tier keeps</param>
    /// <param name="hourly">Whether the tier is the hourly one</param>
    internal ConnectionRollupTier(long width, long horizon, bool hourly)
    {
        Width = width;
        _horizon = horizon;
        Hourly = hourly;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the buckets this step adds to.
    /// </summary>
    internal Dictionary<SourceBucketKey, ConnectionBucket> Buckets { get; } = [];

    // Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
    /// <summary>
    /// Gets the most addresses a bucket of the tier names.
    /// </summary>
    internal int AddressCap => throw new NotImplementedException();

    /// <summary>
    /// Gets the addresses an ended bucket of the tier keeps after the trim.
    /// </summary>
    internal int KeptAddresses => throw new NotImplementedException();

    // Skeleton: removed with the implementation
#pragma warning restore S2325, RH2003

    /// <summary>
    /// Gets the width of a bucket in nanoseconds.
    /// </summary>
    internal long Width { get; }

    /// <summary>
    /// Gets a value indicating whether the tier is the hourly one.
    /// </summary>
    internal bool Hourly { get; }

    #endregion // Properties

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
    /// <summary>
    /// Gets the bucket of a snapshot of a source, creating it on first use.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time of the snapshot</param>
    /// <returns>The bucket, or <c>null</c> when it is past the period of the tier</returns>
    internal ConnectionBucket? BucketOf(string source, long capturedAt)
    {
        throw new NotImplementedException();
    }

    // Skeleton: removed with the implementation
#pragma warning restore S2325, RH2003

    #endregion // Methods
}