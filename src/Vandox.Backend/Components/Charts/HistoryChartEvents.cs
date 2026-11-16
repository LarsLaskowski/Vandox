using Microsoft.JSInterop;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Charts;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Receives the drag selection and the click of the history chart from the browser; only instance methods of the reference handed to the browser are invoked.
/// </summary>
public sealed class HistoryChartEvents
{
    #region Fields

    /// <summary>
    /// Called with a selected range.
    /// </summary>
    private readonly Func<HistoryRange, Task> _selected;

    /// <summary>
    /// Called with a click.
    /// </summary>
    private readonly Func<HistoryPick, Task> _picked;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryChartEvents"/> class.
    /// </summary>
    /// <param name="selected">Called with a selected range</param>
    /// <param name="picked">Called with a click</param>
    public HistoryChartEvents(Func<HistoryRange, Task> selected, Func<HistoryPick, Task> picked)
    {
        _selected = selected;
        _picked = picked;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Receives a drag selection in Unix seconds; a value that is not valid is dropped.
    /// </summary>
    /// <param name="startSeconds">The start in Unix seconds</param>
    /// <param name="endSeconds">The end in Unix seconds</param>
    /// <returns>A task that completes when the selection is handled</returns>
    [JSInvokable]
    public Task Select(double startSeconds, double endSeconds)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Receives a click in Unix seconds with its tolerance in seconds; a value that is not valid is dropped.
    /// </summary>
    /// <param name="atSeconds">The time under the pointer in Unix seconds</param>
    /// <param name="toleranceSeconds">The tolerance in seconds</param>
    /// <returns>A task that completes when the click is handled</returns>
    [JSInvokable]
    public Task Pick(double atSeconds, double toleranceSeconds)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Makes both callbacks do nothing from now on.
    /// </summary>
    public void Stop()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003, S2325, S4487