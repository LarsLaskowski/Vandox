using Vandox.Backend.Components.Charts;

namespace Vandox.Backend.Incidents;

/// <summary>
/// The data of the charts of an incident window.
/// </summary>
public sealed class IncidentWindowData
{
    #region Properties

    /// <summary>
    /// Gets the source of the memory snapshots.
    /// </summary>
    public string MemorySource { get; init; } = string.Empty;

    /// <summary>
    /// Gets the source of the connection snapshots.
    /// </summary>
    public string ConnectionsSource { get; init; } = string.Empty;

    /// <summary>
    /// Gets the memory per program, stacked; <c>null</c> without a snapshot.
    /// </summary>
    public ChartData? Memory { get; init; }

    /// <summary>
    /// Gets the available memory and the swap; <c>null</c> without a point.
    /// </summary>
    public ChartData? MemoryAndSwap { get; init; }

    /// <summary>
    /// Gets the connections; <c>null</c> without a snapshot.
    /// </summary>
    public ChartData? Connections { get; init; }

    /// <summary>
    /// Gets the time the memory chart was cut at, or <c>null</c> when it holds every snapshot.
    /// </summary>
    public DateTimeOffset? MemoryCutAt { get; init; }

    /// <summary>
    /// Gets the time the connection chart was cut at, or <c>null</c> when it holds every snapshot.
    /// </summary>
    public DateTimeOffset? ConnectionsCutAt { get; init; }

    #endregion // Properties
}