using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// Reads the snapshots of a moment, one kind after the other, so that one decoded snapshot is held at a time.
/// </summary>
public sealed class HistoryMomentReader
{
    #region Fields

    /// <summary>
    /// The reader of the snapshots.
    /// </summary>
    private readonly IHistoryReader _reader;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryMomentReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the snapshots</param>
    public HistoryMomentReader(IHistoryReader reader)
    {
        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Picks the closer of the stamps on both sides of a moment: the nearer one, the one at or before the moment on equal distance.
    /// </summary>
    /// <param name="atOrBefore">The stamp at or before the moment</param>
    /// <param name="after">The stamp after the moment</param>
    /// <param name="at">The moment</param>
    /// <returns>The stamp, or <see langword="null"/> when both are missing</returns>
    public static SnapshotStamp? Closest(SnapshotStamp? atOrBefore, SnapshotStamp? after, DateTimeOffset at)
    {
        if (atOrBefore is null || after is null)
        {
            return atOrBefore ?? after;
        }

        return after.CapturedAt - at < at - atOrBefore.CapturedAt ? after : atOrBefore;
    }

    /// <summary>
    /// Reads the process and the connection snapshot closest to a moment.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the panel of the moment</returns>
    public Task<HistoryMoment> ReadAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        return ReadMomentAsync(at, cancellationToken);
    }

#pragma warning disable RH2003, S2325 // Skeleton: removed with the implementation
    /// <summary>
    /// Reads the record of the snapshot of a kind closest to a moment, of one source or of any.
    /// </summary>
    /// <param name="kind">The snapshot kind</param>
    /// <param name="source">The source; <see langword="null"/> is any source</param>
    /// <param name="at">The moment</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the record, or <see langword="null"/> when there is none</returns>
    public Task<ClosestSnapshot?> ClosestAsync(string kind, string? source, DateTimeOffset at, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003, S2325 // Skeleton: removed with the implementation

    /// <summary>
    /// Builds a stamp query.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <param name="kind">The snapshot kind</param>
    /// <param name="side">The side of the moment</param>
    /// <returns>The query</returns>
    private static SnapshotQuery Query(DateTimeOffset at, string kind, SnapshotSide side)
    {
        return new SnapshotQuery
               {
                   Kind = kind,
                   At = at,
                   Window = HistoryView.MomentWindow,
                   Side = side
               };
    }

    /// <summary>
    /// Reads the process snapshot, then the connection snapshot of a moment.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the moment</returns>
    private async Task<HistoryMoment> ReadMomentAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (process, before) = await ReadKindAsync(at, RecordKind.ProcessSnapshot, ProcessSnapshotView.FromProcesses, cancellationToken).ConfigureAwait(false);
        var (connection, _) = await ReadKindAsync(at, RecordKind.ConnectionSnapshot, ConnectionSnapshotView.FromConnections, cancellationToken).ConfigureAwait(false);

        return new HistoryMoment
               {
                   At = at,
                   Process = process,
                   ProcessBefore = process is null ? null : before,
                   Connection = connection
               };
    }

    /// <summary>
    /// Reads the stamps on both sides of a moment and the record of the closest one only, and builds the view of the record so that the record is not held beyond this read.
    /// </summary>
    /// <typeparam name="TView">The type of the view</typeparam>
    /// <param name="at">The moment</param>
    /// <param name="kind">The snapshot kind</param>
    /// <param name="build">Builds the view of the record</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the view, or <see langword="null"/> when there is none, and the capture time of the stamp before the moment when the record shown is the one after it</returns>
    private async Task<(TView? View, DateTimeOffset? Before)> ReadKindAsync<TView>(DateTimeOffset at, string kind, Func<StoredRecord, TView> build, CancellationToken cancellationToken)
        where TView : class
    {
        var before = await _reader.SnapshotStampAsync(Query(at, kind, SnapshotSide.AtOrBefore), cancellationToken).ConfigureAwait(false);
        var after = await _reader.SnapshotStampAsync(Query(at, kind, SnapshotSide.After), cancellationToken).ConfigureAwait(false);
        var closest = Closest(before, after, at);

        if (closest is null)
        {
            return (null, null);
        }

        var record = await _reader.SnapshotAsync(closest, cancellationToken).ConfigureAwait(false);

        if (record is null)
        {
            return (null, null);
        }

        return (build(record), ReferenceEquals(closest, after) ? before?.CapturedAt : null);
    }

    #endregion // Methods
}