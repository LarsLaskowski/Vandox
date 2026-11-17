using Microsoft.JSInterop;

using Vandox.Backend.History;
using Vandox.Core.Model;

namespace Vandox.Backend.Components.Charts;

/// <summary>
/// Receives the drag selection and the click of the history chart from the browser; only instance methods of the reference handed to the browser are invoked.
/// </summary>
public sealed class HistoryChartEvents
{
    #region Constants

    /// <summary>
    /// The largest tolerance of a click, in seconds.
    /// </summary>
    private const double MaxToleranceSeconds = 86400;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// Called with a selected range.
    /// </summary>
    private readonly Func<HistoryRange, Task> _selected;

    /// <summary>
    /// Called with a click.
    /// </summary>
    private readonly Func<HistoryPick, Task> _picked;

    /// <summary>
    /// Whether the callbacks do nothing.
    /// </summary>
    private volatile bool _stopped;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryChartEvents"/> class.
    /// </summary>
    /// <param name="selected">Called with a selected range</param>
    /// <param name="picked">Called with a click</param>
    public HistoryChartEvents(Func<HistoryRange, Task> selected, Func<HistoryPick, Task> picked)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(picked);

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
        var range = _stopped ? null : HistoryView.Selection(startSeconds, endSeconds);

        return range is null ? Task.CompletedTask : _selected(range);
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
        if (_stopped || double.IsFinite(atSeconds) == false || double.IsFinite(toleranceSeconds) == false || toleranceSeconds < 0 || toleranceSeconds > MaxToleranceSeconds)
        {
            return Task.CompletedTask;
        }

        // The range is checked before the conversion, so that no value outside the storable range reaches the arithmetic.
        var firstSeconds = (StorableTime.Min.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
        var lastSeconds = (StorableTime.Max.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;

        if (atSeconds < firstSeconds || atSeconds > lastSeconds)
        {
            return Task.CompletedTask;
        }

        var at = new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + (long)Math.Round(atSeconds * TimeSpan.TicksPerSecond), TimeSpan.Zero);

        var floored = DateTimeOffset.UnixEpoch.AddSeconds(Math.Floor(atSeconds));

        return StorableTime.Contains(at) && StorableTime.Contains(floored) ? _picked(new HistoryPick(at, TimeSpan.FromSeconds(toleranceSeconds))) : Task.CompletedTask;
    }

    /// <summary>
    /// Makes both callbacks do nothing from now on.
    /// </summary>
    public void Stop()
    {
        _stopped = true;
    }

    #endregion // Methods
}