using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Overview;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S4487
/// <summary>
/// Reads the overview data through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedOverviewReader : IOverviewReader
{
    #region Fields

    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly IOverviewReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedOverviewReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedOverviewReader(IOverviewReader inner, WebReaderGate gate)
    {
        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IOverviewReader

    /// <inheritdoc/>
    public Task<AgentFreshness> FreshnessAsync(int liveScanLimit, DateTimeOffset until, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<LatestSeries>> LatestValuesAsync(LatestValueQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<MariaDbLatest?> LatestMariaDbStatusAsync(DateTimeOffset until, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<ServiceStatePage> LatestServiceStatesAsync(LatestServiceQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // IOverviewReader
}
#pragma warning restore RH2003, S4487