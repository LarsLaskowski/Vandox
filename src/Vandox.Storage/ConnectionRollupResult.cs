namespace Vandox.Storage;

/// <summary>
/// The result of a connection rollup read per state and program.
/// </summary>
public sealed class ConnectionRollupResult
{
    #region Properties

    /// <summary>
    /// Gets the start of each step.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Times { get; init; } = [];

    /// <summary>
    /// Gets the lines of the connection states.
    /// </summary>
    public IReadOnlyList<ConnectionRollupLine> States { get; init; } = [];

    /// <summary>
    /// Gets the lines of the top programs.
    /// </summary>
    public IReadOnlyList<ConnectionRollupLine> Programs { get; init; } = [];

    /// <summary>
    /// Gets the average of all other programs per step, <c>null</c> for a step without snapshots.
    /// </summary>
    public IReadOnlyList<double?> Others { get; init; } = [];

    /// <summary>
    /// Gets the number of incomplete snapshots per step.
    /// </summary>
    public IReadOnlyList<int> Incomplete { get; init; } = [];

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