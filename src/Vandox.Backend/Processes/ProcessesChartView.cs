using Vandox.Backend.History;
using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

/// <summary>
/// Builds the stacked chart of a per-program rollup result.
/// </summary>
public static class ProcessesChartView
{
    #region Constants

    /// <summary>
    /// The number of bytes of a MiB.
    /// </summary>
    private const double Mebibyte = 1048576.0;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds the stacked chart of a result: the programs in rank order, then the other programs.
    /// </summary>
    /// <param name="result">The result of the read</param>
    /// <param name="measure">The measure of the read</param>
    /// <param name="range">The range drawn</param>
    /// <param name="moment">The moment, if any</param>
    /// <returns>The view</returns>
    public static HistoryChartView Build(ProgramRollupResult result, ProgramMeasure measure, HistoryRange range, DateTimeOffset? moment)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(range);

        var memory = measure == ProgramMeasure.Memory;
        var factor = memory ? Mebibyte : 1.0;
        var steps = result.Times.Count;
        var gaps = Enumerable.Range(0, steps).Select(step => step >= result.Others.Count || result.Others[step] is null).ToArray();
        var lines = new List<HistoryLine>();

        foreach (var program in result.Programs)
        {
            lines.Add(BuildLine(program.Program, lines.Count, [.. Enumerable.Range(0, steps).Select(step => gaps[step] || step >= program.Points.Count ? null : program.Points[step])], gaps, factor));
        }

        lines.Add(BuildLine(ProcessesView.OtherPrograms, lines.Count, null, gaps, factor, result.Others));

        return new HistoryChartView
               {
                   Times = result.Times,
                   Lines = lines,
                   Axes = [Axis(measure, result.MemoryKey)],
                   Notes = memory && result.MemoryKey != ProgramMemoryKey.Pss ? [ProcessesView.RssNote] : [],
                   Moment = moment is not null && moment.Value >= range.Start && moment.Value <= range.End ? moment : null,
                   Stacked = true
               };
    }

    /// <summary>
    /// Builds the notes of the charts from the memory result.
    /// </summary>
    /// <param name="memory">The memory result</param>
    /// <param name="sources">The sources of the range</param>
    /// <param name="range">The range</param>
    /// <param name="stepLength">The length of a step</param>
    /// <returns>The notes</returns>
    public static IReadOnlyList<string> Notes(ProgramRollupResult memory, ProgramSourceList sources, HistoryRange range, TimeSpan stepLength)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(range);

        if (memory.Times.Count == 0)
        {
            return [ProcessesView.NoDataText];
        }

        var notes = new List<string>();

        if (sources.More)
        {
            notes.Add(ProcessesView.SourceListCutText);
        }

        if (memory.StartsBeforeBuckets)
        {
            notes.Add(ProcessesView.StartsBeforeBucketsText);
        }

        if (memory.Incomplete.Any(count => count > 0))
        {
            notes.Add(ProcessesView.IncompleteSnapshotsText);
        }

        if (memory.EarliestUnfolded is { } unfolded && unfolded < range.End)
        {
            notes.Add(HistoryView.Fill(ProcessesView.NotFoldedFormat, HistoryChartView.FormatTime(unfolded)));
        }

        notes.Add(HistoryView.Fill(HistoryView.StepNoteFormat, HistoryView.StepText(stepLength)));

        return notes;
    }

    /// <summary>
    /// Gives the label of the axis.
    /// </summary>
    /// <param name="measure">The measure</param>
    /// <param name="key">The memory figure used</param>
    /// <returns>The label</returns>
    private static string Axis(ProgramMeasure measure, ProgramMemoryKey? key)
    {
        if (measure == ProgramMeasure.Cpu)
        {
            return "CPU %";
        }

        return key == ProgramMemoryKey.Pss ? "Memory (PSS), MiB" : "Memory (RSS), MiB";
    }

    /// <summary>
    /// Builds a line: a program with its extremes, or the other programs with their average only.
    /// </summary>
    /// <param name="label">The name</param>
    /// <param name="index">The place of the line</param>
    /// <param name="points">The points of a program, <c>null</c> for the other programs</param>
    /// <param name="gaps">The steps without a folded snapshot</param>
    /// <param name="factor">The divisor of the values</param>
    /// <param name="others">The averages of the other programs</param>
    /// <returns>The line</returns>
    private static HistoryLine BuildLine(string label, int index, ProgramRollupPoint?[]? points, bool[] gaps, double factor, IReadOnlyList<double?>? others = null)
    {
        return new HistoryLine
               {
                   Label = LogText.Escape(label, HistoryView.LabelChars),
                   Scale = 0,
                   Color = index % HistoryChartView.ColorCount,
                   Dashed = index >= HistoryChartView.ColorCount,
                   Values = points is null ? [.. gaps.Select((gap, step) => gap ? null : others?[step] / factor)] : [.. points.Select(point => point?.Average / factor)],
                   Bridged = [.. gaps.Select(_ => false)],
                   Minimums = points is null ? [] : [.. points.Select(point => point?.Minimum / factor)],
                   Maximums = points is null ? [] : [.. points.Select(point => point?.Maximum / factor)]
               };
    }

    #endregion // Methods
}