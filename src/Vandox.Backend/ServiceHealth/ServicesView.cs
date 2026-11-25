using System.Globalization;

using Vandox.Backend.History;
using Vandox.Backend.Overview;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// The texts of the services view and the helpers that write its figures.
/// </summary>
public static class ServicesView
{
    #region Constants

    /// <summary>
    /// The heading of the page.
    /// </summary>
    public const string Heading = "Services and MariaDB";

    /// <summary>
    /// The heading of the section Services.
    /// </summary>
    public const string ServicesHeading = "Services";

    /// <summary>
    /// The heading of the list of the restarts.
    /// </summary>
    public const string RestartsHeading = "Service restarts";

    /// <summary>
    /// The heading of the section MariaDB.
    /// </summary>
    public const string MariaDbHeading = "MariaDB";

    /// <summary>
    /// The heading of the chart of the availability.
    /// </summary>
    public const string AvailabilityHeading = "Availability";

    /// <summary>
    /// The heading of the memory budget.
    /// </summary>
    public const string MemoryHeading = "Memory budget";

    /// <summary>
    /// The heading of the chart of the connections.
    /// </summary>
    public const string ConnectionsHeading = "Connections";

    /// <summary>
    /// The heading of the chart of the queries per second.
    /// </summary>
    public const string QueriesHeading = "Queries per second";

    /// <summary>
    /// The heading of the chart of the InnoDB buffer pool.
    /// </summary>
    public const string BufferPoolHeading = "InnoDB buffer pool";

    /// <summary>
    /// The heading of the section Long-running queries.
    /// </summary>
    public const string ThreadsHeading = "Long-running queries";

    /// <summary>
    /// The heading of the list of the thread snapshots.
    /// </summary>
    public const string SnapshotsHeading = "Snapshots";

    /// <summary>
    /// The text of a section until its action ends.
    /// </summary>
    public const string LoadingText = "Loading…";

    /// <summary>
    /// The note on the gaps of the systemd collector (E3).
    /// </summary>
    public const string SystemdGapsText = "The systemd collector recorded gaps in this range (it failed, or it refused a unit's state); that time counts as unknown for every unit.";

    /// <summary>
    /// The format of the note on restarts that are not counted (E5).
    /// </summary>
    public const string RestartsNotCountedFormat = "Restarts after {0} UTC are not counted.";

    /// <summary>
    /// The text of a range without service state (E6).
    /// </summary>
    public const string NoServiceStateText = "No service state is stored in this range.";

    /// <summary>
    /// The text of a range without MariaDB data (E6).
    /// </summary>
    public const string NoMariaDbDataText = "No MariaDB data is stored in this range.";

    /// <summary>
    /// The format of the note on the retention of the status records (E7).
    /// </summary>
    public const string RetentionFormat = "MariaDB's status is kept for {0}; availability, memory in use and connections begin at {1} UTC.";

    /// <summary>
    /// The text of a range without thread snapshot (E10).
    /// </summary>
    public const string NoSnapshotInRangeText = "No thread snapshot is stored in this range.";

    /// <summary>
    /// The text of a moment without thread snapshot (E10).
    /// </summary>
    public const string NoSnapshotAtText = "No thread snapshot was stored within 10 minutes of this moment.";

    /// <summary>
    /// The text of a services read past the deadline (E12).
    /// </summary>
    public const string ServicesDeadlineText = "Reading the services took longer than 10 seconds and was stopped. Narrow the range.";

    /// <summary>
    /// The text of a MariaDB read past the deadline (E12).
    /// </summary>
    public const string MariaDbDeadlineText = "Reading MariaDB's data took longer than 10 seconds and was stopped. Narrow the range.";

    /// <summary>
    /// The text of a thread snapshot read past the deadline (E12).
    /// </summary>
    public const string ThreadsDeadlineText = "Reading the thread snapshot took longer than 10 seconds and was stopped.";

    /// <summary>
    /// The text of a failed services read (E13).
    /// </summary>
    public const string ServicesFailedText = "The services could not be read. The service log has the details.";

    /// <summary>
    /// The text of a failed MariaDB read (E13).
    /// </summary>
    public const string MariaDbFailedText = "MariaDB's data could not be read. The service log has the details.";

    /// <summary>
    /// The text of a failed thread snapshot read (E13).
    /// </summary>
    public const string ThreadsFailedText = "The thread snapshot could not be read. The service log has the details.";

    /// <summary>
    /// The format of the line of the units beyond the evaluated ones (E14).
    /// </summary>
    public const string MoreUnitsFormat = "{0} more units are not evaluated.";

    /// <summary>
    /// The format of the line of the threads beyond the listed ones (E14).
    /// </summary>
    public const string MoreThreadsFormat = "and {0} more threads";

    /// <summary>
    /// The text on the snapshots beyond the listed ones (E14).
    /// </summary>
    public const string OlderSnapshotsText = "Older snapshots are not listed; narrow the range.";

    /// <summary>
    /// The format of the note on gaps that are not read (E15).
    /// </summary>
    public const string GapsNotReadFormat = "Gaps after {0} UTC are not read; the time after it counts as unknown.";

    /// <summary>
    /// The note on when the agent records threads (AC8).
    /// </summary>
    public const string AgentNoteText = "The agent records MariaDB's threads only in a run in which more than 10 threads are running or the server is under memory pressure.";

    /// <summary>
    /// The format of the heading of the thread table.
    /// </summary>
    public const string ThreadsAtFormat = "Threads of {0} UTC";

    /// <summary>
    /// The format of the line on the sleeping connections.
    /// </summary>
    public const string SleepingFormat = "{0} sleeping connections recorded, not listed.";

    /// <summary>
    /// The text after a statement the agent shortened.
    /// </summary>
    public const string ShortenedText = "[statement shortened by the agent]";

    /// <summary>
    /// The text of a time of 36,500 days or more.
    /// </summary>
    private const string CappedSecondsText = "36500 d 0 h or more";

    private const int PerMille = 1000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Writes the share of a part of a time rounded down to one decimal, so that 100.0 % needs no time lost.
    /// </summary>
    /// <param name="part">The part</param>
    /// <param name="total">The total</param>
    /// <returns>The text</returns>
    public static string ShareDown(TimeSpan part, TimeSpan total)
    {
        var tenths = total.Ticks <= 0 || part.Ticks <= 0 ? 0 : (long)((Int128)part.Ticks * PerMille / total.Ticks);

        tenths = Math.Min(tenths, PerMille);

        return string.Create(CultureInfo.InvariantCulture, $"{tenths / 10}.{tenths % 10} %");
    }

    /// <summary>
    /// Writes a number of seconds of a thread, capped at <see cref="Vandox.Storage.ThreadSnapshot.MaxSeconds"/>.
    /// </summary>
    /// <param name="seconds">The seconds</param>
    /// <returns>The text</returns>
    public static string Seconds(ulong seconds)
    {
        return seconds >= (ulong)ThreadSnapshot.MaxSeconds ? CappedSecondsText : OverviewText.Age(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>
    /// Formats a time as <c>yyyy-MM-dd HH:mm:ss</c>, UTC.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text</returns>
    internal static string FormatTime(DateTimeOffset time)
    {
        return HistoryChartView.FormatTime(time);
    }

    #endregion // Methods
}