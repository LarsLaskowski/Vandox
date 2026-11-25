using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// Builds the lines of the stacked charts of the processes and network views.
/// </summary>
public static class StackedLine
{
    #region Methods

    /// <summary>
    /// Builds one line of a stacked chart.
    /// </summary>
    /// <param name="label">The name</param>
    /// <param name="index">The place of the line</param>
    /// <param name="points">The points of a program, <c>null</c> for the other programs</param>
    /// <param name="gaps">The steps without a folded snapshot</param>
    /// <param name="factor">The divisor of the values</param>
    /// <param name="others">The averages of the other programs</param>
    /// <returns>The line</returns>
    public static HistoryLine Build(string label, int index, IReadOnlyList<ProgramRollupPoint?>? points, IReadOnlyList<bool> gaps, double factor, IReadOnlyList<double?>? others)
    {
        ArgumentNullException.ThrowIfNull(gaps);

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