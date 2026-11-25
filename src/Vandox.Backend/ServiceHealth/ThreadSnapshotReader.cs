using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// Reads the section Long-running queries.
/// </summary>
public sealed class ThreadSnapshotReader
{
    #region Fields

    /// <summary>
    /// The reader of the thread snapshots.
    /// </summary>
    private readonly IMariaDbHistoryReader _reader;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ThreadSnapshotReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the thread snapshots</param>
    public ThreadSnapshotReader(IMariaDbHistoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the panel of a range: the list of the snapshots and the snapshot of the moment, else the latest one.
    /// </summary>
    /// <param name="range">The range</param>
    /// <param name="at">The moment of the address, <see langword="null"/> for the latest snapshot</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the panel</returns>
    public async Task<ThreadPanel> ReadAsync(HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        var stamps = await _reader.ThreadSnapshotsAsync(new ThreadSnapshotQuery
                                                        {
                                                            From = range.Start,
                                                            To = range.End,
                                                            Limit = ThreadSnapshotQuery.MaxLimit
                                                        },
                                                        cancellationToken).ConfigureAwait(false);
        var latest = stamps.Count > 0 ? stamps[0] : null;
        var stamp = at is { } moment ? await _reader.ClosestThreadSnapshotAsync(moment, HistoryView.MomentWindow, cancellationToken).ConfigureAwait(false) : latest;
        var shown = stamp is null ? null : await _reader.ThreadSnapshotAsync(stamp.RecordId, cancellationToken).ConfigureAwait(false);

        return ThreadSnapshotView.Build(stamps, shown, at);
    }

    #endregion // Methods
}