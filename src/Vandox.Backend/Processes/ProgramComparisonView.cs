using System.Globalization;

using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Backend.Overview;

namespace Vandox.Backend.Processes;

/// <summary>
/// The comparison of the programs of one source at two points.
/// </summary>
public sealed class ProgramComparisonView
{
    #region Constants

    private const string Absent = "--";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the text that replaces the table, for example when a point has no snapshot; empty when the table is shown.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets the heading: both points and the source.
    /// </summary>
    public string Heading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the closing line.
    /// </summary>
    public string Closing { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note on an incomplete snapshot; empty when there is none.
    /// </summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table.
    /// </summary>
    public SnapshotTableView Table { get; init; } = new();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Compares the programs of two snapshots.
    /// </summary>
    /// <param name="earlier">The earlier snapshot</param>
    /// <param name="later">The later snapshot</param>
    /// <returns>The comparison</returns>
    public static ProgramComparisonView Compare(ProgramFigures earlier, ProgramFigures later)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(later);

        var figures = earlier.Programs.Values.Concat(later.Programs.Values).ToList();
        var pss = figures.Count > 0 && figures.All(figure => figure.Pss is not null);
        var complete = earlier.ListsEveryProgram && later.ListsEveryProgram;
        var kind = pss ? "PSS" : "RSS";
        var rows = earlier.Programs.Keys.Union(later.Programs.Keys, StringComparer.Ordinal).Select(name => BuildRow(name, Find(earlier, name), Find(later, name), pss, complete)).OrderByDescending(row => row.Change).ThenBy(row => row.Name, StringComparer.Ordinal).ToList();
        var left = rows.Skip(HistoryView.ListRows).ToList();

        return new ProgramComparisonView
               {
                   Heading = $"{HistoryChartView.FormatTime(earlier.CapturedAt)} UTC to {HistoryChartView.FormatTime(later.CapturedAt)} UTC ({LogText.Escape(earlier.Source, HistoryView.LabelChars)})",
                   Closing = ClosingOf(left, complete),
                   Note = complete ? string.Empty : ProcessesView.IncompleteComparisonNote,
                   Table = new SnapshotTableView
                           {
                               Columns = ["Program", $"Memory ({kind}) earlier", $"Memory ({kind}) later", "Change", "CPU % earlier", "CPU % later", "State"],
                               Rows = [.. rows.Take(HistoryView.ListRows).Select(row => row.Row)]
                           }
               };
    }

    /// <summary>
    /// Gives a comparison that only says why there is none.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The comparison</returns>
    public static ProgramComparisonView Missing(string text)
    {
        return new ProgramComparisonView
               {
                   Text = text
               };
    }

    /// <summary>
    /// Finds a program of a snapshot.
    /// </summary>
    /// <param name="figures">The snapshot</param>
    /// <param name="name">The program</param>
    /// <returns>The figure, or <see langword="null"/> when the snapshot does not name the program</returns>
    private static ProgramFigure? Find(ProgramFigures figures, string name)
    {
        return figures.Programs.TryGetValue(name, out var figure) ? figure : null;
    }

    /// <summary>
    /// Builds the closing line of the rows left out.
    /// </summary>
    /// <param name="left">The rows left out</param>
    /// <param name="complete">Whether both snapshots list every program</param>
    /// <returns>The line, empty when no row is left out</returns>
    private static string ClosingOf(List<(string Name, double Change, string State, SnapshotRowView Row)> left, bool complete)
    {
        if (left.Count == 0)
        {
            return string.Empty;
        }

        if (complete == false)
        {
            return string.Create(CultureInfo.InvariantCulture, $"and {left.Count} more programs.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"and {left.Count} more programs ({left.Count(row => row.State == "new")} new, {left.Count(row => row.State == "gone")} gone).");
    }

    /// <summary>
    /// Builds the row of a program.
    /// </summary>
    /// <param name="name">The program</param>
    /// <param name="earlier">The figure at the earlier point</param>
    /// <param name="later">The figure at the later point</param>
    /// <param name="pss">Whether the memory is PSS</param>
    /// <param name="complete">Whether both snapshots list every program</param>
    /// <returns>The row</returns>
    private static (string Name, double Change, string State, SnapshotRowView Row) BuildRow(string name, ProgramFigure? earlier, ProgramFigure? later, bool pss, bool complete)
    {
        var before = earlier is { } first ? Memory(first, pss) : (double?)null;
        var after = later is { } second ? Memory(second, pss) : (double?)null;
        var change = after.GetValueOrDefault() - before.GetValueOrDefault();
        var state = complete ? StateOf(earlier, later) : string.Empty;

        return (name,
                change,
                state,
                new SnapshotRowView
                {
                    Cells = [
                                LogText.Runs(name, HistoryView.TextChars, HistoryView.TextRuns),
                                Plain(before is null ? Absent : OverviewText.Size(before.Value)),
                                Plain(after is null ? Absent : OverviewText.Size(after.Value)),
                                Plain(Signed(change)),
                                Plain(earlier is null ? Absent : Cpu(earlier.Value.Cpu)),
                                Plain(later is null ? Absent : Cpu(later.Value.Cpu)),
                                Plain(state)
                            ]
                });
    }

    /// <summary>
    /// Gives the state of a program: new, gone or none.
    /// </summary>
    /// <param name="earlier">The figure at the earlier point</param>
    /// <param name="later">The figure at the later point</param>
    /// <returns>The state</returns>
    private static string StateOf(ProgramFigure? earlier, ProgramFigure? later)
    {
        if (earlier is null)
        {
            return "new";
        }

        return later is null ? "gone" : string.Empty;
    }

    /// <summary>
    /// Gives the memory of a program.
    /// </summary>
    /// <param name="figure">The figure</param>
    /// <param name="pss">Whether the memory is PSS</param>
    /// <returns>The bytes</returns>
    private static double Memory(ProgramFigure figure, bool pss)
    {
        return pss ? figure.Pss.GetValueOrDefault() : figure.Rss;
    }

    /// <summary>
    /// Writes a change in binary units with its sign.
    /// </summary>
    /// <param name="change">The change in bytes</param>
    /// <returns>The text</returns>
    private static string Signed(double change)
    {
        if (change > 0)
        {
            return "+" + OverviewText.Size(change);
        }

        return change < 0 ? "-" + OverviewText.Size(-change) : OverviewText.Size(0);
    }

    /// <summary>
    /// Writes a CPU percentage with one decimal.
    /// </summary>
    /// <param name="percent">The percentage</param>
    /// <returns>The text</returns>
    private static string Cpu(double percent)
    {
        return percent.ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the runs of a text of the page.
    /// </summary>
    /// <param name="value">The text</param>
    /// <returns>The runs</returns>
    private static IReadOnlyList<TextRun> Plain(string value)
    {
        return [new TextRun(value, false)];
    }

    #endregion // Methods
}