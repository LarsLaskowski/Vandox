using System.Globalization;

using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// Builds the section Long-running queries from the snapshot stamps and one snapshot.
/// </summary>
public static class ThreadSnapshotView
{
    #region Constants

    /// <summary>
    /// The most snapshots of the list.
    /// </summary>
    private const int MaxStamps = ThreadSnapshotQuery.MaxLimit - 1;

    /// <summary>
    /// The most threads of the table.
    /// </summary>
    private const int MaxThreads = ThreadSnapshot.MaxRows - 1;

    /// <summary>
    /// The most characters of a statement.
    /// </summary>
    private const int StatementChars = 2048;

    /// <summary>
    /// The most runs of a statement.
    /// </summary>
    private const int StatementRuns = 256;

    private const string NoneText = "none";

    #endregion // Constants

    #region Fields

    private static readonly string[] _columns = ["Id", "User", "Host", "Database", "Command", "State", "Time", "Statement"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Builds the panel.
    /// </summary>
    /// <param name="stamps">The snapshots of the range, newest first</param>
    /// <param name="shown">The snapshot shown, <see langword="null"/> for none</param>
    /// <param name="at">The moment of the address, <see langword="null"/> for none</param>
    /// <returns>The panel</returns>
    public static ThreadPanel Build(IReadOnlyList<ThreadSnapshotStamp> stamps, ThreadSnapshot? shown, DateTimeOffset? at)
    {
        ArgumentNullException.ThrowIfNull(stamps);

        var rows = stamps.Take(MaxStamps).Select(StampRow).ToList();
        var more = stamps.Count > MaxStamps ? ServicesView.OlderSnapshotsText : string.Empty;

        if (shown is null)
        {
            return new ThreadPanel
                   {
                       Stamps = rows,
                       More = more,
                       Notes = [stamps.Count == 0 || at is null ? ServicesView.NoSnapshotInRangeText : ServicesView.NoSnapshotAtText]
                   };
        }

        var heading = HistoryView.Fill(ServicesView.ThreadsAtFormat, ServicesView.FormatTime(shown.CapturedAt));

        return new ThreadPanel
               {
                   Stamps = rows,
                   More = more,
                   Heading = heading,
                   Threads = ThreadTable(shown, heading),
                   Sleeping = shown.Sleeping > 0 ? HistoryView.Fill(ServicesView.SleepingFormat, shown.Sleeping.ToString(CultureInfo.InvariantCulture)) : string.Empty
               };
    }

    /// <summary>
    /// Builds the row of a snapshot of the list.
    /// </summary>
    /// <param name="stamp">The stamp</param>
    /// <returns>The row</returns>
    private static ThreadStampRow StampRow(ThreadSnapshotStamp stamp)
    {
        return new ThreadStampRow
               {
                   At = stamp.CapturedAt,
                   Time = ServicesView.FormatTime(stamp.CapturedAt),
                   Threads = stamp.Threads.ToString(CultureInfo.InvariantCulture),
                   Running = stamp.Running.ToString(CultureInfo.InvariantCulture),
                   Longest = stamp.LongestSeconds is { } seconds ? ServicesView.Seconds((ulong)Math.Max(seconds, 0)) : NoneText
               };
    }

    /// <summary>
    /// Builds the table of the threads of a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot</param>
    /// <param name="heading">The heading of the table</param>
    /// <returns>The table</returns>
    private static SnapshotTableView ThreadTable(ThreadSnapshot snapshot, string heading)
    {
        var more = snapshot.Rows.Count > MaxThreads ? Math.Max(snapshot.Running - MaxThreads, 1) : 0;

        return new SnapshotTableView
               {
                   Heading = heading,
                   Columns = _columns,
                   Rows = [.. snapshot.Rows.Take(MaxThreads).Select(ThreadRow)],
                   More = more > 0 ? HistoryView.Fill(ServicesView.MoreThreadsFormat, more.ToString(CultureInfo.InvariantCulture)) : string.Empty
               };
    }

    /// <summary>
    /// Builds the row of a thread.
    /// </summary>
    /// <param name="thread">The thread</param>
    /// <returns>The row</returns>
    private static SnapshotRowView ThreadRow(MariaDbThread thread)
    {
        return SnapshotCells.Row(SnapshotCells.Plain(thread.Id),
                                 SnapshotCells.Runs(thread.User),
                                 SnapshotCells.Runs(thread.Host),
                                 SnapshotCells.Runs(thread.Db),
                                 SnapshotCells.Runs(thread.Command),
                                 SnapshotCells.Runs(thread.State),
                                 SnapshotCells.Plain(ServicesView.Seconds(thread.TimeSeconds)),
                                 Statement(thread));
    }

    /// <summary>
    /// Builds the cell of a statement: its display, capped, and the mark when the agent shortened it.
    /// </summary>
    /// <param name="thread">The thread</param>
    /// <returns>The runs</returns>
    private static IReadOnlyList<TextRun> Statement(MariaDbThread thread)
    {
        var runs = LogText.Runs(thread.Info, StatementChars, StatementRuns);

        return thread.Truncated ? [.. runs, new TextRun($" {ServicesView.ShortenedText}", false)] : runs;
    }

    #endregion // Methods
}