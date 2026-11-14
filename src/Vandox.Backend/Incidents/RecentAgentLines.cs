using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// The IDs of the non-imported log lines captured within the last two minutes of the lines read, so that the origin of a kill is judged per kill without remembering every line.
/// </summary>
internal sealed class RecentAgentLines
{
    #region Constants

    /// <summary>
    /// The span of capture times that is held.
    /// </summary>
    private static readonly TimeSpan _span = TimeSpan.FromMinutes(2);

    #endregion // Constants

    #region Fields

    private readonly Queue<(long Id, DateTimeOffset CapturedAt)> _queue = new();
    private readonly HashSet<long> _ids = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of lines held.
    /// </summary>
    internal int Count => _ids.Count;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a line; only the ID of a line that is not imported is kept.
    /// </summary>
    /// <param name="record">The stored line</param>
    internal void Add(StoredRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Record.Origin != RecordOrigin.Import && _ids.Add(record.Id))
        {
            _queue.Enqueue((record.Id, record.Record.CapturedAt));
        }
    }

    /// <summary>
    /// Forgets the lines captured before a point in time.
    /// </summary>
    /// <param name="capturedAt">The point in time; lines captured earlier than two minutes before it are forgotten</param>
    internal void ForgetBefore(DateTimeOffset capturedAt)
    {
        var limit = capturedAt - _span;

        while (_queue.TryPeek(out var oldest) && oldest.CapturedAt < limit)
        {
            _queue.Dequeue();
            _ids.Remove(oldest.Id);
        }
    }

    /// <summary>
    /// Determines whether one of the record IDs belongs to a held line.
    /// </summary>
    /// <param name="recordIds">The record IDs of a kill</param>
    /// <returns><c>true</c> when one of the IDs is held</returns>
    internal bool ContainsAny(IReadOnlyList<long> recordIds)
    {
        ArgumentNullException.ThrowIfNull(recordIds);

        return recordIds.Any(_ids.Contains);
    }

    #endregion // Methods
}