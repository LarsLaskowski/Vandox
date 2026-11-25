using System.Diagnostics.CodeAnalysis;

using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// Counts the calls of the panel reader to the store.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class CountingHistoryReader : IHistoryReader
{
    #region Fields

    private readonly IHistoryReader _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingHistoryReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to count</param>
    public CountingHistoryReader(IHistoryReader inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of calls.
    /// </summary>
    public int Calls { get; private set; }

    /// <summary>
    /// Gets the number of decoded snapshots.
    /// </summary>
    public int Snapshots { get; private set; }

    #endregion // Properties

    #region IHistoryReader

    /// <inheritdoc/>
    public Task<StoredMetricList> StoredMetricsAsync(int limit, CancellationToken cancellationToken)
    {
        Calls++;

        return _inner.StoredMetricsAsync(limit, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SnapshotStamp?> SnapshotStampAsync(SnapshotQuery query, CancellationToken cancellationToken)
    {
        Calls++;

        return _inner.SnapshotStampAsync(query, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SnapshotStamp?> LatestSnapshotStampAsync(LatestSnapshotQuery query, CancellationToken cancellationToken)
    {
        Calls++;

        return _inner.LatestSnapshotStampAsync(query, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<StoredRecord?> SnapshotAsync(SnapshotStamp stamp, CancellationToken cancellationToken)
    {
        Calls++;
        Snapshots++;

        return _inner.SnapshotAsync(stamp, cancellationToken);
    }

    #endregion // IHistoryReader
}