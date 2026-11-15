using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.TimeSeries;

/// <summary>
/// Reads the time-series data through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedTimeSeriesReader : ITimeSeriesReader
{
    #region Fields

    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly ITimeSeriesReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedTimeSeriesReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedTimeSeriesReader(ITimeSeriesReader inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region ITimeSeriesReader

    /// <inheritdoc/>
    public Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.RawValuesAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.RollupStepsAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.GapsInRangeAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.EarliestUnfoldedAsync(cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ServiceChangesAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.PreviousServiceStatesAsync(units, before, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.BootTimesAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.GapRecordsAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.KernelEventsAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.BackupRunsAsync(query, cancellationToken), cancellationToken);
    }

    #endregion // ITimeSeriesReader
}