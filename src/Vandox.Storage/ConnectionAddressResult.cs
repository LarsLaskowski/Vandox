namespace Vandox.Storage;

/// <summary>
/// The result of a connection rollup read per remote address.
/// </summary>
public sealed class ConnectionAddressResult
{
    #region Properties

    /// <summary>
    /// Gets the start of each step.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Times { get; init; } = [];

    /// <summary>
    /// Gets whether each step has no snapshots.
    /// </summary>
    public IReadOnlyList<bool> Gaps { get; init; } = [];

    /// <summary>
    /// Gets the top addresses.
    /// </summary>
    public IReadOnlyList<ConnectionAddress> Addresses { get; init; } = [];

    /// <summary>
    /// Gets the number of further addresses the range names.
    /// </summary>
    public long Further { get; init; }

    /// <summary>
    /// Gets the number of incomplete snapshots per step.
    /// </summary>
    public IReadOnlyList<int> Incomplete { get; init; } = [];

    /// <summary>
    /// Gets the number of snapshots per step whose addresses the bucket cap dropped.
    /// </summary>
    public IReadOnlyList<int> Capped { get; init; } = [];

    /// <summary>
    /// Gets the earliest capture time of the source's connection snapshots not yet folded.
    /// </summary>
    public DateTimeOffset? EarliestUnfolded { get; init; }

    /// <summary>
    /// Gets whether the range starts before the oldest bucket of the tier.
    /// </summary>
    public bool StartsBeforeBuckets { get; init; }

    #endregion // Properties
}