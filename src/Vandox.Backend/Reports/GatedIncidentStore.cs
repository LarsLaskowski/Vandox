using Vandox.Backend.Logs;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325, S4487

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
        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IIncidentStore

    /// <inheritdoc />
    public Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // IIncidentStore
}