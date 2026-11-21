using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

#pragma warning disable RH2003 // Skeleton: removed with the implementation
/// <summary>
/// Builds the stacked chart of a per-program rollup result.
/// </summary>
public static class ProcessesChartView
{
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
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003 // Skeleton: removed with the implementation