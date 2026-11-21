namespace Vandox.Storage;

/// <summary>
/// The buckets of one tier a fold step adds to, with the statements that merge them into the tables of the tier.
/// </summary>
internal sealed class ProgramRollupTier
{
    #region Fields

    private readonly long _width;
    private readonly long _horizon;
    private readonly bool _hourly;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramRollupTier"/> class.
    /// </summary>
    /// <param name="width">The width of a bucket in nanoseconds</param>
    /// <param name="horizon">The first bucket start the tier keeps</param>
    /// <param name="hourly">Whether the tier is the hourly one</param>
    internal ProgramRollupTier(long width, long horizon, bool hourly)
    {
        _width = width;
        _horizon = horizon;
        _hourly = hourly;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the buckets this step adds to.
    /// </summary>
    internal Dictionary<SourceBucketKey, ProgramBucket> Buckets { get; } = [];

    /// <summary>
    /// Gets the statement that reads the number of named programs of a bucket.
    /// </summary>
    internal string ReadNamed => _hourly ? RetentionQueries.ReadNamedHourly : RetentionQueries.ReadNamedFiveMinutes;

    /// <summary>
    /// Gets the statement that looks for the row of a program.
    /// </summary>
    internal string HasProgram => _hourly ? RetentionQueries.HasProgramHourly : RetentionQueries.HasProgramFiveMinutes;

    /// <summary>
    /// Gets the statement that merges a program row.
    /// </summary>
    internal string UpsertProgram => _hourly ? RetentionQueries.UpsertProgramHourly : RetentionQueries.UpsertProgramFiveMinutes;

    /// <summary>
    /// Gets the statement that merges a total.
    /// </summary>
    internal string UpsertTotal => _hourly ? RetentionQueries.UpsertProgramTotalHourly : RetentionQueries.UpsertProgramTotalFiveMinutes;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Gets the bucket of a snapshot of a source, creating it on first use.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time of the snapshot</param>
    /// <returns>The bucket, or <c>null</c> when it is past the period of the tier</returns>
    internal ProgramBucket? BucketOf(string source, long capturedAt)
    {
        var start = RollupFolder.Floor(capturedAt, _width);

        if (start < _horizon)
        {
            return null;
        }

        var key = new SourceBucketKey(source, start);

        if (Buckets.TryGetValue(key, out var bucket) == false)
        {
            bucket = new ProgramBucket();
            Buckets[key] = bucket;
        }

        return bucket;
    }

    #endregion // Methods
}