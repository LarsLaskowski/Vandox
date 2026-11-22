using Vandox.Backend.History;

namespace Vandox.Backend.Processes;

/// <summary>
/// The two charts of the processes view with the sources and the notes.
/// </summary>
public sealed class ProcessesCharts
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
    /// Gets the memory chart; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? Memory { get; init; }

    /// <summary>
    /// Gets the CPU chart; <see langword="null"/> when there is no data.
    /// </summary>
    public HistoryChartView? Cpu { get; init; }

    /// <summary>
    /// Gets the note on the memory figure; empty when there is none.
    /// </summary>
    public string MemoryNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the notes of the charts.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    #endregion // Properties
}