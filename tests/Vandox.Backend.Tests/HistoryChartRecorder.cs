using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Records what the two callbacks of a <see cref="Vandox.Backend.Components.Charts.HistoryChartEvents"/> object receive.
/// </summary>
internal sealed class HistoryChartRecorder
{
    #region Properties

    /// <summary>
    /// Gets the selections that were reported.
    /// </summary>
    internal List<HistoryRange> Ranges { get; } = [];

    /// <summary>
    /// Gets the clicks that were reported.
    /// </summary>
    internal List<HistoryPick> Picks { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Records a selection.
    /// </summary>
    /// <param name="range">The selection</param>
    /// <returns>A completed task</returns>
    internal Task SelectedAsync(HistoryRange range)
    {
        Ranges.Add(range);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records a click.
    /// </summary>
    /// <param name="pick">The click</param>
    /// <returns>A completed task</returns>
    internal Task PickedAsync(HistoryPick pick)
    {
        Picks.Add(pick);

        return Task.CompletedTask;
    }

    #endregion // Methods
}