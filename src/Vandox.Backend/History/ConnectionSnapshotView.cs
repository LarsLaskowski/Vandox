using System.Globalization;

using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// The panel of a connection snapshot.
/// </summary>
public sealed class ConnectionSnapshotView
{
    #region Properties

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the source as text runs.
    /// </summary>
    public IReadOnlyList<TextRun> Source { get; init; } = [];

    /// <summary>
    /// Gets the table of the counts per protocol and state.
    /// </summary>
    public SnapshotTableView States { get; init; } = new();

    /// <summary>
    /// Gets the processes table.
    /// </summary>
    public SnapshotTableView Processes { get; init; } = new();

    /// <summary>
    /// Gets the remote addresses table.
    /// </summary>
    public SnapshotTableView Remotes { get; init; } = new();

    /// <summary>
    /// Gets the note on incomplete counts; empty when there is none.
    /// </summary>
    public string Note { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the panel of a connection snapshot record.
    /// </summary>
    /// <param name="record">The stored snapshot record</param>
    /// <returns>The view</returns>
    public static ConnectionSnapshotView FromConnections(StoredRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Record.Data is not ConnectionSnapshot snapshot)
        {
            throw new ArgumentException("The record is no connection snapshot.", nameof(record));
        }

        var states = (snapshot.States ?? []).OrderBy(state => state.Proto, StringComparer.Ordinal).ThenBy(state => state.State, StringComparer.Ordinal).ToList();
        var processes = (snapshot.Processes ?? []).OrderByDescending(process => process.Count).ThenBy(process => process.Pid).ToList();
        var remotes = (snapshot.Remotes ?? []).Select(remote => (Address: remote.Addr?.ToString() ?? string.Empty, remote.Count)).OrderByDescending(remote => remote.Count).ThenBy(remote => remote.Address, StringComparer.Ordinal).ToList();

        return new ConnectionSnapshotView
               {
                   CapturedAt = record.Record.CapturedAt,
                   Source = SnapshotCells.Runs(record.Record.Source),
                   States = new SnapshotTableView
                            {
                                Heading = "Connection states",
                                Columns = ["Protocol", "State", "Connections"],
                                Rows = [.. states.Select(state => SnapshotCells.Row(SnapshotCells.Runs(state.Proto), SnapshotCells.Runs(state.State), SnapshotCells.Plain(state.Count)))]
                            },
                   Processes = new SnapshotTableView
                               {
                                   Heading = "Processes with the most connections",
                                   Columns = ["PID", "Command", "Connections"],
                                   Rows = [.. processes.Take(HistoryView.ListRows).Select(process => SnapshotCells.Row(SnapshotCells.Plain(process.Pid), SnapshotCells.Runs(process.Command), SnapshotCells.Plain(process.Count)))],
                                   More = More(processes.Count)
                               },
                   Remotes = new SnapshotTableView
                             {
                                 Heading = snapshot.AggregatesOnly ? HistoryView.RemotesCountsOnlyHeading : "Remote addresses with the most connections",
                                 Columns = ["Address", "Connections"],
                                 Rows = [.. remotes.Take(HistoryView.ListRows).Select(remote => SnapshotCells.Row(SnapshotCells.Runs(remote.Address), SnapshotCells.Plain(remote.Count)))],
                                 More = More(remotes.Count)
                             },
                   Note = snapshot.AggregatesIncomplete ? HistoryView.IncompleteConnectionsText : string.Empty
               };
    }

    /// <summary>
    /// Builds the text of the rows beyond the listed ones.
    /// </summary>
    /// <param name="count">The number of entries</param>
    /// <returns>The text, empty when nothing lies beyond the rows</returns>
    private static string More(int count)
    {
        return count > HistoryView.ListRows ? string.Create(CultureInfo.InvariantCulture, $"and {count - HistoryView.ListRows} more") : string.Empty;
    }

    #endregion // Methods
}