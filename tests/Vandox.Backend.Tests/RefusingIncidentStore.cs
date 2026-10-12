using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An incident store that passes everything to another store, except that it refuses the n-th save as an invalid incident.
/// </summary>
internal sealed class RefusingIncidentStore : IIncidentStore
{
    #region Fields

    private readonly IIncidentStore _inner;

    private readonly int _refused;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RefusingIncidentStore"/> class.
    /// </summary>
    /// <param name="inner">The store that handles the other calls</param>
    /// <param name="refused">The number of the save that is refused, counted from 1</param>
    internal RefusingIncidentStore(IIncidentStore inner, int refused)
    {
        _inner = inner;
        _refused = refused;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of saves that were asked, refused ones included.
    /// </summary>
    internal int Saves { get; private set; }

    #endregion // Properties

    #region IIncidentStore

    /// <inheritdoc />
    public Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        Saves++;

        if (Saves == _refused)
        {
            throw new InvalidIncidentException("store: invalid incident: refused by the test double");
        }

        return _inner.SaveIncidentAsync(incident, savedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        return _inner.IncidentsAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        return _inner.CountIncidentsAsync(cancellationToken);
    }

    #endregion // IIncidentStore
}