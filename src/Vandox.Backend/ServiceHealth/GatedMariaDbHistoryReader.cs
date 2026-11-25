using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

#pragma warning disable S4487, RH2003 // Skeleton: removed with the implementation
/// <summary>
/// Reads the MariaDB history through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedMariaDbHistoryReader : IMariaDbHistoryReader
{
    #region Fields

    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly IMariaDbHistoryReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedMariaDbHistoryReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedMariaDbHistoryReader(IMariaDbHistoryReader inner, WebReaderGate gate)
    {
        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IMariaDbHistoryReader

    /// <inheritdoc/>
    public Task<MariaDbStatusResult> MariaDbStatusAsync(MariaDbStatusQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ThreadSnapshotStamp>> ThreadSnapshotsAsync(ThreadSnapshotQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<ThreadSnapshotStamp?> ClosestThreadSnapshotAsync(DateTimeOffset at, TimeSpan window, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<ThreadSnapshot?> ThreadSnapshotAsync(long recordId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // IMariaDbHistoryReader
}
#pragma warning restore S4487, RH2003 // Skeleton: removed with the implementation