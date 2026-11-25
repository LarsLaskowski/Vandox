using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// Reads the history data through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedHistoryReader : IHistoryReader
{
    #region Fields

    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly IHistoryReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedHistoryReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedHistoryReader(IHistoryReader inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IHistoryReader

    /// <inheritdoc/>
    public Task<StoredMetricList> StoredMetricsAsync(int limit, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.StoredMetricsAsync(limit, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SnapshotStamp?> SnapshotStampAsync(SnapshotQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.SnapshotStampAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SnapshotStamp?> LatestSnapshotStampAsync(LatestSnapshotQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.LatestSnapshotStampAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<StoredRecord?> SnapshotAsync(SnapshotStamp stamp, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.SnapshotAsync(stamp, cancellationToken), cancellationToken);
    }

    #endregion // IHistoryReader
}