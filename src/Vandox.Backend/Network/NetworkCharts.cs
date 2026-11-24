using Vandox.Backend.History;

namespace Vandox.Backend.Network;

/// <summary>
/// The two charts of the network view with the sources and the notes.
/// </summary>
public sealed class NetworkCharts
{
    #region Properties

    /// <summary>
    /// Gets the sources of the range, by name.
    /// </summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>
    /// Gets the source shown; empty when the range has no source.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets the length of a step.
    /// </summary>
    public TimeSpan Step { get; init; }

    /// <summary>
    /// Gets the chart of the connection states; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? States { get; init; }

    /// <summary>
    /// Gets the chart of the sockets per program; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? Programs { get; init; }

    /// <summary>
    /// Gets the notes of the charts.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    #endregion // Properties
}