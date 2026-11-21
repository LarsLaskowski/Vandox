using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Vandox.Backend.History;

namespace Vandox.Backend.Components.Charts;

/// <summary>
/// Draws the history chart through the JavaScript module <see cref="ChartInterop.ModulePath"/>.
/// </summary>
public sealed class HistoryChartInterop : IAsyncDisposable
{
    #region Fields

    /// <summary>
    /// The JavaScript runtime.
    /// </summary>
    private readonly IJSRuntime _jsRuntime;

#pragma warning disable CA2213 // The gate never creates its wait handle; disposing it would fail a call still waiting when the interop is disposed
    /// <summary>
    /// Lets one call at a time work on the chart.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
#pragma warning restore CA2213

    /// <summary>
    /// The imported module.
    /// </summary>
    private IJSObjectReference? _module;

    /// <summary>
    /// The chart drawn.
    /// </summary>
    private IJSObjectReference? _chart;

    /// <summary>
    /// Whether the interop was disposed.
    /// </summary>
    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HistoryChartInterop"/> class.
    /// </summary>
    /// <param name="jsRuntime">The JavaScript runtime</param>
    public HistoryChartInterop(IJSRuntime jsRuntime)
    {
        ArgumentNullException.ThrowIfNull(jsRuntime);

        _jsRuntime = jsRuntime;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the width of the element that holds the chart.
    /// </summary>
    /// <param name="host">The element that holds the chart</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the width in CSS pixels</returns>
    public async Task<double> WidthAsync(ElementReference host, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var module = await ImportAsync(cancellationToken).ConfigureAwait(false);

            return module is null ? 0 : await module.InvokeAsync<double>("width", cancellationToken, [host]).ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            return 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Draws a view, replacing the chart drawn before.
    /// </summary>
    /// <param name="host">The element that holds the chart</param>
    /// <param name="view">The view to draw</param>
    /// <param name="events">The reference that receives the selection and the click</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns whether the chart was drawn</returns>
    /// <exception cref="ArgumentException">The view holds times that are not ascending, lines of another length, a value that is not finite or, when stacked, a line on another scale</exception>
    public async Task<bool> RenderAsync(ElementReference host, HistoryChartView view, DotNetObjectReference<HistoryChartEvents> events, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(events);

        Validate(view);

        var setup = new
                    {
                        axes = view.Axes,
                        stacked = view.Stacked,
                        lines = view.Lines.Select(line => new
                                                          {
                                                              label = line.Label,
                                                              scale = line.Scale,
                                                              color = line.Color,
                                                              dashed = line.Dashed,
                                                              bridges = line.Bridged.Select((bridged, index) => bridged ? index : -1).Where(index => index >= 0).ToArray(),
                                                              minimums = line.Minimums,
                                                              maximums = line.Maximums
                                                          }).ToArray(),
                        marks = view.Marks.Select(mark => new
                                                          {
                                                              kind = mark.Kind.ToString(),
                                                              at = ToSeconds(mark.At),
                                                              endsAt = mark.EndsAt is null ? (double?)null : ToSeconds(mark.EndsAt.Value)
                                                          }).ToArray(),
                        moment = view.Moment is null ? (double?)null : ToSeconds(view.Moment.Value)
                    };
        double?[][] columns = [[.. view.Times.Select(time => (double?)ToSeconds(time))], .. view.Lines.Select(line => line.Values.ToArray())];

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_disposed)
            {
                return false;
            }

            var module = await ImportAsync(cancellationToken).ConfigureAwait(false);

            if (module is null)
            {
                return false;
            }

            await DestroyChartAsync().ConfigureAwait(false);

            _chart = await module.InvokeAsync<IJSObjectReference>("createHistory", cancellationToken, [host, setup, columns, events]).ConfigureAwait(false);

            return true;
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; a later draw tries again.
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Marks the moment in the chart drawn, or removes the mark.
    /// </summary>
    /// <param name="moment">The moment, or <see langword="null"/> for none</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the mark is set</returns>
    public async Task MarkAsync(DateTimeOffset? moment, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_chart is not null && _disposed == false)
            {
                await _chart.InvokeVoidAsync("mark", cancellationToken, [moment is null ? null : ToSeconds(moment.Value)]).ConfigureAwait(false);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; there is nothing left to mark.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Converts a time to Unix seconds.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The seconds, with the fraction</returns>
    private static double ToSeconds(DateTimeOffset time)
    {
        return (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond;
    }

    /// <summary>
    /// Refuses a view the chart cannot draw; the message names the index of a line and never its label.
    /// </summary>
    /// <param name="view">The view</param>
    /// <exception cref="ArgumentException">The view is not valid</exception>
    private static void Validate(HistoryChartView view)
    {
        for (var index = 1; index < view.Times.Count; index++)
        {
            if (view.Times[index] <= view.Times[index - 1])
            {
                throw new ArgumentException("The times of the chart must ascend.", nameof(view));
            }
        }

        for (var index = 0; index < view.Lines.Count; index++)
        {
            var line = view.Lines[index];

            if (view.Stacked && line.Scale != 0)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The line {index} of the stacked chart is not on the first scale."), nameof(view));
            }

            var invalid = HasBadLength(line.Values, view.Times.Count)
                          || HasBadLength(line.Minimums, view.Times.Count, true)
                          || HasBadLength(line.Maximums, view.Times.Count, true)
                          || HasBadValue(line.Values)
                          || HasBadValue(line.Minimums)
                          || HasBadValue(line.Maximums);

            if (invalid)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The line {index} of the chart holds values that cannot be drawn."), nameof(view));
            }
        }
    }

    /// <summary>
    /// Tells whether a list has not the length of the times.
    /// </summary>
    /// <param name="values">The values</param>
    /// <param name="count">The number of times</param>
    /// <param name="mayBeEmpty">Whether an empty list is valid</param>
    /// <returns><c>true</c> when the length is wrong</returns>
    private static bool HasBadLength(IReadOnlyList<double?> values, int count, bool mayBeEmpty = false)
    {
        return values.Count != count && (mayBeEmpty == false || values.Count != 0);
    }

    /// <summary>
    /// Tells whether a list holds a value that is not finite.
    /// </summary>
    /// <param name="values">The values</param>
    /// <returns><c>true</c> when a value is NaN or infinite</returns>
    private static bool HasBadValue(IReadOnlyList<double?> values)
    {
        return values.Any(value => value is { } number && double.IsFinite(number) == false);
    }

    /// <summary>
    /// Imports the module once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the module, or <see langword="null"/> when the circuit is gone</returns>
    private async Task<IJSObjectReference?> ImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            _module ??= await _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, [ChartInterop.ModulePath]).ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; nothing can be imported.
        }

        return _module;
    }

    /// <summary>
    /// Destroys and disposes the chart drawn, if any.
    /// </summary>
    /// <returns>A task that completes when the chart is gone</returns>
    private async Task DestroyChartAsync()
    {
        var chart = _chart;

        _chart = null;

        if (chart is null)
        {
            return;
        }

        try
        {
            await chart.InvokeVoidAsync("destroy").ConfigureAwait(false);
            await chart.DisposeAsync().ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the browser has dropped the chart with it.
        }
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Waits for a call in flight; its own outcome belongs to its caller.
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            var module = _module;

            _module = null;

            await DestroyChartAsync().ConfigureAwait(false);

            if (module is not null)
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone, and the browser has dropped the chart with it.
        }
        finally
        {
            _gate.Release();
        }
    }

    #endregion // IAsyncDisposable
}