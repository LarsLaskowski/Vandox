using Vandox.Storage;

namespace Vandox.Backend.Logs;

/// <summary>
/// Limits the explorer calls in flight across all callers, through a gate of its own or one shared with other web views.
/// </summary>
public sealed class GatedLogExplorer : ILogExplorer, IDisposable
{
    #region Fields

    private readonly ILogExplorer _inner;

    private readonly WebReaderGate _gate;

    private readonly bool _ownsGate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedLogExplorer"/> class.
    /// </summary>
    /// <param name="inner">The explorer to call</param>
    /// <param name="maxConcurrent">The number of slots, 1 to 3</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot count is not 1 to 3</exception>
    public GatedLogExplorer(ILogExplorer inner, int maxConcurrent)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _gate = new WebReaderGate(maxConcurrent);
        _ownsGate = true;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedLogExplorer"/> class that shares a gate it does not own.
    /// </summary>
    /// <param name="inner">The explorer to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedLogExplorer(ILogExplorer inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Waits for a slot with the token of the call, runs the call and gives the slot back.
    /// </summary>
    /// <typeparam name="TResult">The type of the result</typeparam>
    /// <param name="call">The call of the inner explorer</param>
    /// <param name="cancellationToken">Ends the wait for a slot</param>
    /// <returns>A task that returns the result of the call</returns>
    private Task<TResult> RunAsync<TResult>(Func<Task<TResult>> call, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(call, cancellationToken);
    }

    #endregion // Methods

    #region ILogExplorer

    /// <inheritdoc />
    public Task<LogPage> FindLogsAsync(LogFilter filter, CancellationToken cancellationToken)
    {
        return RunAsync(() => _inner.FindLogsAsync(filter, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<LogContext> LogContextAsync(LogContextQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(() => _inner.LogContextAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> LogSourcesAsync(CancellationToken cancellationToken)
    {
        return RunAsync(() => _inner.LogSourcesAsync(cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<LogTemplate?> TemplateAsync(long id, CancellationToken cancellationToken)
    {
        return RunAsync(() => _inner.TemplateAsync(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateCount>> TemplatesAsync(TemplateListQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(() => _inner.TemplatesAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DateTimeOffset>> SourceHoursAsync(SourceHourQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(() => _inner.SourceHoursAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken)
    {
        return RunAsync(() => _inner.TemplateHoursAsync(query, cancellationToken), cancellationToken);
    }

    #endregion // ILogExplorer

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsGate)
        {
            _gate.Dispose();
        }
    }

    #endregion // IDisposable
}