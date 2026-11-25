using Vandox.Backend.History;

namespace Vandox.Backend.ServiceHealth;

/// <summary>
/// The section MariaDB: the notes, the summary, the memory budget and the charts.
/// </summary>
public sealed class MariaDbCharts
{
    #region Properties

    /// <summary>
    /// Gets the notes of the section, in order.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Gets the summary line of the availability.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the line of the memory budget.
    /// </summary>
    public string Budget { get; init; } = string.Empty;

    /// <summary>
    /// Gets the line that says the theoretical maximum exceeds the RAM; empty when it does not apply.
    /// </summary>
    public string BudgetExceeded { get; init; } = string.Empty;

    /// <summary>
    /// Gets the chart of the availability; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? Availability { get; init; }

    /// <summary>
    /// Gets the chart of the memory budget; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? Memory { get; init; }

    /// <summary>
    /// Gets the chart of the connections; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? Connections { get; init; }

    /// <summary>
    /// Gets the chart of the queries per second; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? Queries { get; init; }

    /// <summary>
    /// Gets the chart of the InnoDB buffer pool; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? BufferPool { get; init; }

    #endregion // Properties
}