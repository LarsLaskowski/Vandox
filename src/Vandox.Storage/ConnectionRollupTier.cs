namespace Vandox.Storage;

/// <summary>
/// The connection buckets of one tier a fold step adds to, with the statements that merge them into the tables of the tier.
/// </summary>
internal sealed class ConnectionRollupTier
{
    #region Fields

    private readonly long _horizon;

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

    /// <summary>
    /// Gets the most addresses a bucket of the tier names.
    /// </summary>
    internal int AddressCap => Hourly ? StorageLimits.MaxAddressesHourly : StorageLimits.MaxAddressesFiveMinutes;

    /// <summary>
    /// Gets the addresses an ended bucket of the tier keeps after the trim.
    /// </summary>
    internal int KeptAddresses => Hourly ? StorageLimits.KeptAddressesHourly : StorageLimits.KeptAddressesFiveMinutes;

    /// <summary>
    /// Gets the width of a bucket in nanoseconds.
    /// </summary>
    internal long Width { get; }

    /// <summary>
    /// Gets a value indicating whether the tier is the hourly one.
    /// </summary>
    internal bool Hourly { get; }

    /// <summary>
    /// Gets the statement that reads the stored program and address counts of a bucket.
    /// </summary>
    internal string ReadCounts => Hourly ? ConnectionRollupSql.ReadCountsHourly : ConnectionRollupSql.ReadCountsFiveMinutes;

    /// <summary>
    /// Gets the statement that looks for the row of a state, program or address.
    /// </summary>
    internal string HasItem => Hourly ? ConnectionRollupSql.HasItemHourly : ConnectionRollupSql.HasItemFiveMinutes;

    /// <summary>
    /// Gets the statement that merges a state, program or address row.
    /// </summary>
    internal string UpsertItem => Hourly ? ConnectionRollupSql.UpsertItemHourly : ConnectionRollupSql.UpsertItemFiveMinutes;

    /// <summary>
    /// Gets the statement that merges a total.
    /// </summary>
    internal string UpsertTotal => Hourly ? ConnectionRollupSql.UpsertTotalHourly : ConnectionRollupSql.UpsertTotalFiveMinutes;

    /// <summary>
    /// Gets the statement that reads the oldest buckets above the kept address count.
    /// </summary>
    internal string ReadTrim => Hourly ? ConnectionRollupSql.ReadTrimHourly : ConnectionRollupSql.ReadTrimFiveMinutes;

    /// <summary>
    /// Gets the statement that deletes the addresses of a bucket outside its top addresses.
    /// </summary>
    internal string DeleteTrimmed => Hourly ? ConnectionRollupSql.DeleteTrimmedHourly : ConnectionRollupSql.DeleteTrimmedFiveMinutes;

    /// <summary>
    /// Gets the statement that records the trim of a bucket.
    /// </summary>
    internal string SetTrimmed => Hourly ? ConnectionRollupSql.SetTrimmedHourly : ConnectionRollupSql.SetTrimmedFiveMinutes;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gets the bucket of a snapshot of a source, creating it on first use.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time of the snapshot</param>
    /// <returns>The bucket, or <c>null</c> when it is past the period of the tier</returns>
    internal ConnectionBucket? BucketOf(string source, long capturedAt)
    {
        var start = RollupFolder.Floor(capturedAt, Width);

        if (start < _horizon)
        {
            return null;
        }

        var key = new SourceBucketKey(source, start);

        if (Buckets.TryGetValue(key, out var bucket) == false)
        {
            bucket = new ConnectionBucket();
            Buckets[key] = bucket;
        }

        return bucket;
    }

    #endregion // Methods
}