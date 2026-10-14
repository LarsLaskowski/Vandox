#pragma warning disable RH2003, S2325

using Vandox.Backend.Logs;

namespace Vandox.Backend.Incidents;

/// <summary>
/// The limits, the fixed texts and the formats of the incidents view.
/// </summary>
public static class IncidentsView
{
    #region Constants

    /// <summary>
    /// The number of incidents on a page of the list.
    /// </summary>
    public const int PageIncidents = 20;

    /// <summary>
    /// The most process snapshots of the memory chart.
    /// </summary>
    public const int MaxChartSnapshots = 300;

    /// <summary>
    /// The most connection snapshots of the connection chart.
    /// </summary>
    public const int MaxChartConnections = 300;

    /// <summary>
    /// The most points of a metric series.
    /// </summary>
    public const int MaxMetricPoints = 1000;

    /// <summary>
    /// The most programs the memory chart tracks separately.
    /// </summary>
    public const int MaxChartPrograms = 4096;

    /// <summary>
    /// The number of programs the memory chart shows besides the others.
    /// </summary>
    public const int ChartPrograms = 5;

    /// <summary>
    /// The cap of a program name in a chart label.
    /// </summary>
    public const int MaxLabelChars = 64;

    /// <summary>
    /// The cap of a timeline text.
    /// </summary>
    public const int MaxTimelineTextChars = 512;

    /// <summary>
    /// The title of the list page.
    /// </summary>
    public const string ListTitle = "Incidents";

    /// <summary>
    /// The text shown while a page reads.
    /// </summary>
    public const string Loading = "Loading…";

    /// <summary>
    /// The text of an empty list.
    /// </summary>
    public const string EmptyList = "No incidents are stored. Run vandoxd incidents after an import.";

    /// <summary>
    /// The link to the next page.
    /// </summary>
    public const string OlderIncidents = "Older incidents";

    /// <summary>
    /// The link back to the first page.
    /// </summary>
    public const string NewestIncidents = "Newest incidents";

    /// <summary>
    /// The text shown after the last page.
    /// </summary>
    public const string NoOlderIncidents = "No older incidents.";

    /// <summary>
    /// The text shown when the list runs into the deadline.
    /// </summary>
    public const string ListDeadline = "Reading the incidents took longer than 10 seconds and was stopped.";

    /// <summary>
    /// The text shown when the list fails.
    /// </summary>
    public const string ListFailure = "The incidents could not be read. The service log has the details.";

    /// <summary>
    /// The text shown for an unknown incident.
    /// </summary>
    public const string UnknownIncident = "This incident does not exist. It may have been merged into another one by vandoxd incidents.";

    /// <summary>
    /// The text shown when the window read runs into the deadline.
    /// </summary>
    public const string WindowDeadline = "Reading the data of the window took longer than 10 seconds and was stopped.";

    /// <summary>
    /// The text shown when the window read fails.
    /// </summary>
    public const string WindowFailure = "The data of the window could not be read. The service log has the details.";

    /// <summary>
    /// The text shown when the window holds no data.
    /// </summary>
    public const string NoWindowData = "No snapshot or metric was stored in this window.";

    /// <summary>
    /// The format of the text shown for a cut chart.
    /// </summary>
    public const string ChartCut = "The chart shows the first {0} snapshots, up to {1} UTC.";

    /// <summary>
    /// The title of the memory chart.
    /// </summary>
    public const string MemoryChartTitle = "Memory per program (MiB, stacked)";

    /// <summary>
    /// The title of the available memory and swap chart.
    /// </summary>
    public const string MemorySwapChartTitle = "Available memory and swap (MiB)";

    /// <summary>
    /// The title of the connection chart.
    /// </summary>
    public const string ConnectionsChartTitle = "Connections";

    /// <summary>
    /// The label of the summed other programs.
    /// </summary>
    public const string OtherPrograms = "Other programs";

    /// <summary>
    /// The label of the available memory series.
    /// </summary>
    public const string AvailableMemory = "Available memory";

    /// <summary>
    /// The label of the swap series.
    /// </summary>
    public const string SwapUsed = "Swap used";

    /// <summary>
    /// The label of the socket series.
    /// </summary>
    public const string Sockets = "Sockets";

    /// <summary>
    /// The format of the label of a moved note.
    /// </summary>
    public const string WrittenFor = "Written for incident #{0}";

    /// <summary>
    /// The label of the save button of a note.
    /// </summary>
    public const string SaveNote = "Save note";

    /// <summary>
    /// The text shown when more notes exist than are read.
    /// </summary>
    public const string NotesHidden = "Further notes of merged incidents exist and are not shown.";

    /// <summary>
    /// The text of an unknown growth or backup state.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The text of no growth.
    /// </summary>
    public const string NoGrowth = "none";

    /// <summary>
    /// The text of a running backup.
    /// </summary>
    public const string BackupRunning = "running";

    /// <summary>
    /// The text of a backup that does not run.
    /// </summary>
    public const string BackupNotRunning = "not running";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The time before a moment that the logs link starts at.
    /// </summary>
    public static readonly TimeSpan LogsBefore = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The time after a moment that the logs link ends at.
    /// </summary>
    public static readonly TimeSpan LogsAfter = TimeSpan.FromMinutes(5);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Gets the address of the logs around a moment.
    /// </summary>
    /// <param name="moment">The moment</param>
    /// <returns>The relative address</returns>
    public static string LogsHref(DateTimeOffset moment)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the address of the page of an incident.
    /// </summary>
    /// <param name="id">The number</param>
    /// <returns>The relative address</returns>
    public static string IncidentHref(long id)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the address a note form posts to.
    /// </summary>
    /// <param name="id">The number of the incident</param>
    /// <returns>The relative address</returns>
    public static string NoteAction(long id)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Formats a duration as hours, minutes and seconds; a negative one is shown as zero.
    /// </summary>
    /// <param name="span">The duration</param>
    /// <returns>The text</returns>
    public static string Duration(TimeSpan span)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Formats an amount of memory in MiB.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <returns>The text</returns>
    public static string Mib(ulong bytes)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Formats a growth of memory in MiB.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <returns>The text</returns>
    public static string Growth(ulong bytes)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}