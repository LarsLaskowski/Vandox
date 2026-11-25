using Vandox.Backend.History;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Reads the panel of the listeners.
/// </summary>
public sealed class NetworkListenerReader
{
    #region Fields

    /// <summary>
    /// The distance of the comparison snapshot from the shown one and the width of its window.
    /// </summary>
    private static readonly TimeSpan _comparisonWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// The reader of the snapshots.
    /// </summary>
    private readonly IHistoryReader _reader;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkListenerReader"/> class.
    /// </summary>
    /// <param name="reader">The reader of the snapshots</param>
    public NetworkListenerReader(IHistoryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        _reader = reader;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the panel of a source, a range and a moment.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="range">The range</param>
    /// <param name="at">The moment, <see langword="null"/> for the latest snapshot in the range</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the panel</returns>
    public async Task<NetworkListenerView> ReadAsync(string source, HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);

        var shown = await ReadShownAsync(source, range, at, cancellationToken).ConfigureAwait(false);

        if (shown is null)
        {
            return NetworkListenerView.Empty(at is null ? NetworkView.NoSnapshotInRangeText : NetworkView.NoSnapshotAtText);
        }

        StoredRecord? earlier = null;

        if (shown.Record.Data is ConnectionSnapshot { Inventory: not null })
        {
            earlier = await ReadComparisonAsync(source, shown.Record.CapturedAt, cancellationToken).ConfigureAwait(false);
        }

        return NetworkListenerView.Build(shown, earlier);
    }

    /// <summary>
    /// Reads the snapshot to show: the latest of the range, or the closest to a moment.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="range">The range</param>
    /// <param name="at">The moment, if any</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the record, or <see langword="null"/> when there is none</returns>
    private async Task<StoredRecord?> ReadShownAsync(string source, HistoryRange range, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        if (at is not null)
        {
            var closest = await new HistoryMomentReader(_reader).ClosestAsync(RecordKind.ConnectionSnapshot, source, at.Value, cancellationToken).ConfigureAwait(false);

            return closest?.Record;
        }

        var stamp = await _reader.LatestSnapshotStampAsync(new LatestSnapshotQuery
                                                           {
                                                               Kind = RecordKind.ConnectionSnapshot,
                                                               Source = source,
                                                               From = range.Start,
                                                               To = range.End
                                                           },
                                                           cancellationToken).ConfigureAwait(false);

        return stamp is null ? null : await _reader.SnapshotAsync(stamp, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the comparison snapshot: the latest of the source from 48 up to 24 hours before the shown one.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="shown">The capture time of the shown snapshot</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the record, or <see langword="null"/> when there is none</returns>
    private async Task<StoredRecord?> ReadComparisonAsync(string source, DateTimeOffset shown, CancellationToken cancellationToken)
    {
        var stamp = await _reader.SnapshotStampAsync(new SnapshotQuery
                                                     {
                                                         Kind = RecordKind.ConnectionSnapshot,
                                                         Source = source,
                                                         At = shown - _comparisonWindow,
                                                         Window = _comparisonWindow,
                                                         Side = SnapshotSide.AtOrBefore
                                                     },
                                                     cancellationToken).ConfigureAwait(false);

        return stamp is null ? null : await _reader.SnapshotAsync(stamp, cancellationToken).ConfigureAwait(false);
    }

    #endregion // Methods
}