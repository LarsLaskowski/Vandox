using System.Globalization;

using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Network;

/// <summary>
/// Builds the charts, the table and the notes of the network view from the rollups.
/// </summary>
public static class NetworkChartView
{
    #region Methods

    /// <summary>
    /// Builds the chart of the connection states.
    /// </summary>
    /// <param name="result">The rollups</param>
    /// <returns>The chart</returns>
    public static HistoryChartView StateChart(ConnectionRollupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var gaps = Gaps(result);
        var lines = new List<HistoryLine>();

        foreach (var state in result.States)
        {
            lines.Add(StackedLine.Build(state.Name, lines.Count, Points(state, gaps), gaps, 1.0, null));
        }

        return Chart(result, lines);
    }

    /// <summary>
    /// Builds the chart of the sockets per program.
    /// </summary>
    /// <param name="result">The rollups</param>
    /// <returns>The chart</returns>
    public static HistoryChartView ProgramChart(ConnectionRollupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var gaps = Gaps(result);
        var lines = new List<HistoryLine>();

        foreach (var program in result.Programs)
        {
            lines.Add(StackedLine.Build(program.Name, lines.Count, Points(program, gaps), gaps, 1.0, null));
        }

        lines.Add(StackedLine.Build(NetworkView.OtherPrograms, lines.Count, null, gaps, 1.0, result.Others));

        return Chart(result, lines);
    }

    /// <summary>
    /// Builds the table of the top remote addresses.
    /// </summary>
    /// <param name="result">The address rollups</param>
    /// <returns>The table</returns>
    public static SnapshotTableView AddressTable(ConnectionAddressResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var addresses = result.Addresses.OrderByDescending(address => address.Average).ThenBy(address => address.Address, StringComparer.Ordinal).ToList();
        var rest = Math.Max(0, addresses.Count - NetworkView.AddressRows) + result.Further;

        return new SnapshotTableView
               {
                   Heading = NetworkView.AddressesTitle,
                   Columns = ["Address", "Average connections", "Highest in one snapshot"],
                   Rows = [.. addresses.Take(NetworkView.AddressRows).Select(address => SnapshotCells.Row(SnapshotCells.Runs(address.Address), SnapshotCells.Plain(address.Average.ToString("F1", CultureInfo.InvariantCulture)), SnapshotCells.Plain(address.Maximum)))],
                   More = rest > 0 ? HistoryView.Fill(NetworkView.MoreAddressesFormat, rest.ToString(CultureInfo.InvariantCulture)) : string.Empty
               };
    }

    /// <summary>
    /// Builds the notes below the table of the top remote addresses.
    /// </summary>
    /// <param name="result">The address rollups</param>
    /// <returns>The notes</returns>
    public static IReadOnlyList<string> AddressTableNotes(ConnectionAddressResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Capped.Any(count => count > 0) ? [NetworkView.AddressNoteText, NetworkView.CapText] : [NetworkView.AddressNoteText];
    }

    /// <summary>
    /// Builds the notes of the charts.
    /// </summary>
    /// <param name="rollups">The rollups of the charts</param>
    /// <param name="sources">The sources of the range</param>
    /// <param name="range">The range</param>
    /// <param name="stepLength">The length of a step</param>
    /// <returns>The notes</returns>
    public static IReadOnlyList<string> ChartNotes(ConnectionRollupResult rollups, ProgramSourceList sources, HistoryRange range, TimeSpan stepLength)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(range);

        if (rollups.Times.Count == 0)
        {
            return [NetworkView.NoDataText];
        }

        var notes = new List<string>();

        if (sources.More)
        {
            notes.Add(Processes.ProcessesView.SourceListCutText);
        }

        if (rollups.StartsBeforeBuckets)
        {
            notes.Add(NetworkView.StartsBeforeText);
        }

        if (rollups.Incomplete.Any(count => count > 0))
        {
            notes.Add(NetworkView.IncompleteText);
        }

        if (rollups.EarliestUnfolded is { } unfolded && unfolded < range.End)
        {
            notes.Add(HistoryView.Fill(Processes.ProcessesView.NotFoldedFormat, HistoryChartView.FormatTime(unfolded)));
        }

        notes.Add(HistoryView.Fill(HistoryView.StepNoteFormat, HistoryView.StepText(stepLength)));

        return notes;
    }

    /// <summary>
    /// Finds the steps without a folded snapshot: where the other programs are missing.
    /// </summary>
    /// <param name="result">The rollups</param>
    /// <returns>A flag per step</returns>
    private static bool[] Gaps(ConnectionRollupResult result)
    {
        return [.. Enumerable.Range(0, result.Times.Count).Select(step => step >= result.Others.Count || result.Others[step] is null)];
    }

    /// <summary>
    /// Gives the points of a line per step, empty at a gap.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="gaps">The steps without a folded snapshot</param>
    /// <returns>The points</returns>
    private static ProgramRollupPoint?[] Points(ConnectionRollupLine line, bool[] gaps)
    {
        return [.. gaps.Select((gap, step) => gap || step >= line.Points.Count ? null : line.Points[step])];
    }

    /// <summary>
    /// Builds a stacked chart without moment, marks or notes of its own.
    /// </summary>
    /// <param name="result">The rollups</param>
    /// <param name="lines">The lines</param>
    /// <returns>The chart</returns>
    private static HistoryChartView Chart(ConnectionRollupResult result, List<HistoryLine> lines)
    {
        return new HistoryChartView
               {
                   Times = result.Times,
                   Lines = lines,
                   Axes = [NetworkView.AxisText],
                   Stacked = true
               };
    }

    #endregion // Methods
}