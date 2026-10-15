using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An incident store over another one that can start to answer with the first incidents only, as a store whose incidents were removed does.
/// </summary>
internal sealed class ShrinkableIncidentStore : IIncidentStore
{
    #region Fields

    private readonly IIncidentStore _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ShrinkableIncidentStore"/> class.
    /// </summary>
    /// <param name="inner">The store that holds the incidents</param>
    internal ShrinkableIncidentStore(IIncidentStore inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets or sets the highest incident ID that is still answered; <see cref="long.MaxValue"/> for all.
    /// </summary>
    internal long LastId { get; set; } = long.MaxValue;

    #endregion // Properties

    #region IIncidentStore

    /// <inheritdoc />
    public Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        return _inner.SaveIncidentAsync(incident, savedAt, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        var incidents = await _inner.IncidentsAsync(query, cancellationToken);

        return incidents.Where(stored => stored.Id <= LastId).ToList();
    }

    /// <inheritdoc />
    public Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        return _inner.CountIncidentsAsync(cancellationToken);
    }

    #endregion // IIncidentStore
}