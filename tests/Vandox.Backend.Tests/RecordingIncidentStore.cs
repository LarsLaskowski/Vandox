using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An incident store that passes every call to another store and keeps a copy of every incident query.
/// </summary>
internal sealed class RecordingIncidentStore : IIncidentStore
{
    #region Fields

    private readonly IIncidentStore _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingIncidentStore"/> class.
    /// </summary>
    /// <param name="inner">The store that answers the calls</param>
    internal RecordingIncidentStore(IIncidentStore inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a copy of every incident query in the order it was asked.
    /// </summary>
    internal List<IncidentQuery> Queries { get; } = [];

    #endregion // Properties

    #region IIncidentStore

    /// <inheritdoc />
    public Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        return _inner.SaveIncidentAsync(incident, savedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(new IncidentQuery
                    {
                        From = query.From,
                        To = query.To,
                        Limit = query.Limit
                    });

        return _inner.IncidentsAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        return _inner.CountIncidentsAsync(cancellationToken);
    }

    #endregion // IIncidentStore
}