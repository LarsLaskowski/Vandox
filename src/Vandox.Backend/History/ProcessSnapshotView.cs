using System.Globalization;

using Vandox.Backend.Logs;
using Vandox.Backend.Overview;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// The panel of a process snapshot.
/// </summary>
public sealed class ProcessSnapshotView
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
    /// Gets the programs table.
    /// </summary>
    public SnapshotTableView Programs { get; init; } = new();

    /// <summary>
    /// Gets the processes table.
    /// </summary>
    public SnapshotTableView Processes { get; init; } = new();

    /// <summary>
    /// Gets the text on the listed and the total number of processes; empty when the list is complete.
    /// </summary>
    public string ListedOfTotal { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note on an incomplete snapshot; empty when there is none.
    /// </summary>
    public string Note { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the panel of a process snapshot record.
    /// </summary>
    /// <param name="record">The stored snapshot record</param>
    /// <returns>The view</returns>
    public static ProcessSnapshotView FromProcesses(StoredRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Record.Data is not ProcessSnapshot snapshot)
        {
            throw new ArgumentException("The record is no process snapshot.", nameof(record));
        }

        var programs = snapshot.Programs ?? [];
        var processes = snapshot.Processes ?? [];
        var programPss = programs.Count > 0 && programs.All(program => program.PssBytes is not null);
        var processPss = processes.Count > 0 && processes.All(process => process.PssBytes is not null);
        var programRows = programs.OrderByDescending(program => Memory(program.PssBytes, program.RssBytes, programPss)).ThenBy(program => program.Program, StringComparer.Ordinal).ToList();
        var processRows = processes.OrderByDescending(process => Memory(process.PssBytes, process.RssBytes, processPss)).ThenBy(process => process.Pid).ToList();

        return new ProcessSnapshotView
               {
                   CapturedAt = record.Record.CapturedAt,
                   Source = LogText.Runs(record.Record.Source, HistoryView.TextChars, HistoryView.TextRuns),
                   Programs = new SnapshotTableView
                              {
                                  Heading = "Programs",
                                  Columns = ["Program", "Processes", "CPU %", programPss ? "Memory (PSS)" : "Memory (RSS)"],
                                  Rows = [.. programRows.Take(HistoryView.ListRows).Select(program => SnapshotCells.Row(SnapshotCells.Runs(program.Program), SnapshotCells.Plain(program.Count), Cpu(program.CpuPercent), SnapshotCells.Plain(OverviewText.Size(Memory(program.PssBytes, program.RssBytes, programPss)))))],
                                  More = More(programRows.Count, "programs")
                              },
                   Processes = new SnapshotTableView
                               {
                                   Heading = "Processes",
                                   Columns = ["PID", "User", "Command", "CPU %", processPss ? "Memory (PSS)" : "Memory (RSS)"],
                                   Rows = [.. processRows.Take(HistoryView.ListRows).Select(process => SnapshotCells.Row(SnapshotCells.Plain(process.Pid), SnapshotCells.Runs(process.User), SnapshotCells.Runs(process.Command), Cpu(process.CpuPercent), SnapshotCells.Plain(OverviewText.Size(Memory(process.PssBytes, process.RssBytes, processPss)))))],
                                   More = More(processRows.Count, "processes")
                               },
                   ListedOfTotal = snapshot.Total is { } total && total > processes.Count ? string.Format(CultureInfo.InvariantCulture, "{0} of {1} processes are listed.", processes.Count, total) : string.Empty,
                   Note = snapshot.Complete == false && snapshot.Total is null ? HistoryView.IncompleteProcessesText : string.Empty
               };
    }

    /// <summary>
    /// Gives the memory of an entry: the PSS when its list uses it, else the RSS.
    /// </summary>
    /// <param name="pss">The PSS</param>
    /// <param name="rss">The RSS</param>
    /// <param name="usePss">Whether the list is measured by PSS</param>
    /// <returns>The bytes</returns>
    private static ulong Memory(ulong? pss, ulong rss, bool usePss)
    {
        return usePss ? pss.GetValueOrDefault() : rss;
    }

    /// <summary>
    /// Builds the text of the rows beyond the listed ones.
    /// </summary>
    /// <param name="count">The number of entries</param>
    /// <param name="noun">The plural noun</param>
    /// <returns>The text, empty when nothing lies beyond the rows</returns>
    private static string More(int count, string noun)
    {
        return count > HistoryView.ListRows ? string.Create(CultureInfo.InvariantCulture, $"and {count - HistoryView.ListRows} more {noun}") : string.Empty;
    }

    /// <summary>
    /// Builds the runs of a CPU percentage with one decimal.
    /// </summary>
    /// <param name="percent">The percentage</param>
    /// <returns>The runs</returns>
    private static IReadOnlyList<TextRun> Cpu(double percent)
    {
        return SnapshotCells.Plain(percent.ToString("F1", CultureInfo.InvariantCulture));
    }

    #endregion // Methods
}