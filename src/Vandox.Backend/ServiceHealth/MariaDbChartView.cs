using Vandox.Backend.History;
using Vandox.Storage;

namespace Vandox.Backend.ServiceHealth;

#pragma warning disable RH2003 // Skeleton: removed with the implementation
/// <summary>
/// Builds the section MariaDB from the metric series and the status read.
/// </summary>
public static class MariaDbChartView
{
    #region Methods

    /// <summary>
    /// Builds the section.
    /// </summary>
    /// <param name="series">The result of the metric query</param>
    /// <param name="status">The result of the status read</param>
    /// <param name="range">The range</param>
    /// <param name="snapshots">The period the status records are kept, <see langword="null"/> for unlimited</param>
    /// <param name="now">The current time</param>
    /// <returns>The section</returns>
    public static MariaDbCharts Build(TimeSeriesResult series, MariaDbStatusResult status, HistoryRange range, TimeSpan? snapshots, DateTimeOffset now)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003 // Skeleton: removed with the implementation