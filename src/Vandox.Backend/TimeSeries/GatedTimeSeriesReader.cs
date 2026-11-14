using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.TimeSeries;

/// <summary>
/// Reads the time-series data through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedTimeSeriesReader : ITimeSeriesReader
{
    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly ITimeSeriesReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;
#pragma warning restore S4487

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedTimeSeriesReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedTimeSeriesReader(ITimeSeriesReader inner, WebReaderGate gate)
    {
        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region ITimeSeriesReader

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<LinePage> RawValuesAsync(RawValueQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<LinePage> RollupStepsAsync(RollupStepQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentGap>> GapsInRangeAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<DateTimeOffset?> EarliestUnfoldedAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> ServiceChangesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<IReadOnlyList<ServiceStateRow>> PreviousServiceStatesAsync(IReadOnlyList<string> units, DateTimeOffset before, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<IReadOnlyList<BootTime>> BootTimesAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<IReadOnlyList<GapRecordRow>> GapRecordsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<IReadOnlyList<KernelEventRow>> KernelEventsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    public Task<IReadOnlyList<BackupRunRow>> BackupRunsAsync(TimeRangeQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    #endregion // ITimeSeriesReader
}