using Vandox.Backend.Logs;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Reads the incidents through the shared gate of the web views; saving is refused.
/// </summary>
public sealed class GatedIncidentStore : IIncidentStore
{
    #region Fields

    private readonly IIncidentStore _inner;

    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedIncidentStore"/> class.
    /// </summary>
    /// <param name="inner">The store to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedIncidentStore(IIncidentStore inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IIncidentStore

    /// <inheritdoc />
    public Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        return Task.FromException<IncidentSaveResult>(new NotSupportedException("the web does not save incidents"));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.IncidentsAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.CountIncidentsAsync(cancellationToken), cancellationToken);
    }

    #endregion // IIncidentStore
}