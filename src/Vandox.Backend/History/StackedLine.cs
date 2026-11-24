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
        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // Methods
}