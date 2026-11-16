using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.History;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S4487

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
        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IHistoryReader

    /// <inheritdoc/>
    public Task<StoredMetricList> StoredMetricsAsync(int limit, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<SnapshotStamp?> SnapshotStampAsync(SnapshotQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<StoredRecord?> SnapshotAsync(SnapshotStamp stamp, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // IHistoryReader
}

#pragma warning restore RH2003, S4487