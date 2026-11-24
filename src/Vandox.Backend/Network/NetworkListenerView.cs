using System.Globalization;

using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// The panel of the listeners of a connection snapshot, with the listeners new against an older snapshot marked.
/// </summary>
public sealed class NetworkListenerView
{
    #region Constants

    /// <summary>
    /// The text of the state of a new listener.
    /// </summary>
    private const string NewState = "new";

    /// <summary>
    /// The text of an owner without a process.
    /// </summary>
    private const string NoOwner = "no owner";

    /// <summary>
    /// The text of a socket without a user ID.
    /// </summary>
    private const string UnknownUid = "unknown";

    /// <summary>
    /// The end of the text of a socket user ID that is the lowest of several.
    /// </summary>
    private const string LowestOfSeveral = " (lowest of several)";

    /// <summary>
    /// The inventory state of an inventory with the privileged ports only.
    /// </summary>
    private const string Privileged = "privileged";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the capture time of the shown snapshot; <see langword="null"/> when there is none.
    /// </summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>
    /// Gets the source of the shown snapshot.
    /// </summary>
    public IReadOnlyList<TextRun> SourceText { get; init; } = [];

    /// <summary>
    /// Gets the line above the table: the new listeners or the reason none is marked; empty for none.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the notes of the panel.
    /// </summary>
    public IReadOnlyList<string> PanelNotes { get; init; } = [];

    /// <summary>
    /// Gets the table of the listeners.
    /// </summary>
    public SnapshotTableView Table { get; init; } = new();

    /// <summary>
    /// Gets the text shown instead of the panel; empty when the panel is shown.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the panel of a connection snapshot against an older one.
    /// </summary>
    /// <param name="shown">The shown snapshot</param>
    /// <param name="earlier">The comparison snapshot, if any</param>
    /// <returns>The panel</returns>
    public static NetworkListenerView Build(StoredRecord shown, StoredRecord? earlier)
    {
        ArgumentNullException.ThrowIfNull(shown);

        if (shown.Record.Data is not ConnectionSnapshot snapshot)
        {
            throw new ArgumentException("The record is no connection snapshot.", nameof(shown));
        }

        var listeners = snapshot.Listeners ?? [];
        var notes = new List<string>();
        var summary = string.Empty;
        var marked = new HashSet<Listener>(ReferenceEqualityComparer.Instance);
        var earlierSnapshot = earlier?.Record.Data as ConnectionSnapshot;

        if (snapshot.Inventory is null || (earlierSnapshot is not null && earlierSnapshot.Inventory is null))
        {
            notes.Add(NetworkView.NotComparedText);
        }
        else if (earlierSnapshot is null)
        {
            summary = NetworkView.NoComparisonText;
        }
        else
        {
            var comparison = ListenerComparer.Compare(earlierSnapshot, snapshot, listeners.Count);

            marked.UnionWith(comparison.NewListeners);

            switch (comparison.Outcome)
            {
                case ListenerOutcome.NotComparable:
                    notes.Add(NetworkView.NotComparableText);
                    break;

                case ListenerOutcome.PrivilegedIncomplete:
                    notes.Add(NetworkView.PrivilegedIncompleteText);
                    break;

                default:
                    summary = Summarize(comparison, earlier!.Record.CapturedAt);
                    break;
            }
        }

        if (snapshot.Inventory == Privileged)
        {
            notes.Add(NetworkView.PrivilegedOnlyText);
        }

        var ordered = listeners.Where(marked.Contains).Concat(listeners.Where(listener => marked.Contains(listener) == false)).ToList();

        return new NetworkListenerView
               {
                   CapturedAt = shown.Record.CapturedAt,
                   SourceText = SnapshotCells.Runs(shown.Record.Source),
                   Summary = summary,
                   PanelNotes = notes,
                   Table = new SnapshotTableView
                           {
                               Heading = NetworkView.ListenersTitle,
                               Columns = ["State", "Protocol", "Endpoint", "Owner", "Socket UID"],
                               Rows = [.. ordered.Take(NetworkView.ListenerRows).Select(listener => Row(listener, marked.Contains(listener)))],
                               More = ordered.Count > NetworkView.ListenerRows ? HistoryView.Fill(NetworkView.MoreListenersFormat, (ordered.Count - NetworkView.ListenerRows).ToString(CultureInfo.InvariantCulture)) : string.Empty
                           }
               };
    }

    /// <summary>
    /// Builds the panel of a text that replaces it.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The panel</returns>
    public static NetworkListenerView Empty(string text)
    {
        return new NetworkListenerView
               {
                   Text = text
               };
    }

    /// <summary>
    /// Writes the line above the table.
    /// </summary>
    /// <param name="comparison">The comparison</param>
    /// <param name="since">The capture time of the comparison snapshot</param>
    /// <returns>The line</returns>
    private static string Summarize(ListenerComparison comparison, DateTimeOffset since)
    {
        var time = HistoryChartView.FormatTime(since);
        var count = comparison.NewListeners.Count;
        var line = count switch
                   {
                       0 => $"No new listener since {time} UTC",
                       1 => $"1 new listener since {time} UTC",
                       _ => string.Create(CultureInfo.InvariantCulture, $"{count} new listeners since {time} UTC")
                   };

        return comparison.Outcome == ListenerOutcome.PrivilegedOnly ? $"{line} {NetworkView.PrivilegedOnlySuffix}" : line + ".";
    }

    /// <summary>
    /// Builds the row of a listener.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <param name="isNew">Whether the listener is marked new</param>
    /// <returns>The row</returns>
    private static SnapshotRowView Row(Listener listener, bool isNew)
    {
        return SnapshotCells.Row(isNew ? SnapshotCells.Plain(NewState) : [],
                                 SnapshotCells.Runs(listener.Proto),
                                 SnapshotCells.Plain(listener.Local?.ToString() ?? string.Empty),
                                 Owner(listener),
                                 SocketUid(listener));
    }

    /// <summary>
    /// Builds the owner cell: the process ID and the command, or no owner.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <returns>The runs</returns>
    private static IReadOnlyList<TextRun> Owner(Listener listener)
    {
        if (listener.Pid <= 0)
        {
            return SnapshotCells.Plain(NoOwner);
        }

        return [.. SnapshotCells.Plain(listener.Pid), .. SnapshotCells.Plain(" "), .. SnapshotCells.Runs(listener.Command)];
    }

    /// <summary>
    /// Builds the socket UID cell: the lowest user ID with its user name, ending in a note when it is the lowest of several.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <returns>The runs</returns>
    private static IReadOnlyList<TextRun> SocketUid(Listener listener)
    {
        if (listener.Uid is not { } uid)
        {
            return SnapshotCells.Plain(UnknownUid);
        }

        var runs = new List<TextRun>(SnapshotCells.Plain(uid));

        if (listener.User.Length > 0)
        {
            runs.AddRange(SnapshotCells.Plain(" "));
            runs.AddRange(SnapshotCells.Runs(listener.User));
        }

        if (listener.MixedUids)
        {
            runs.AddRange(SnapshotCells.Plain(LowestOfSeveral));
        }

        return runs;
    }

    #endregion // Methods
}