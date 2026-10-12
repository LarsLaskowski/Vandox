using Vandox.Core.Incidents;

namespace Vandox.Storage;

/// <summary>
/// Stores and reads reconstructed incidents.
/// </summary>
public interface IIncidentStore
{
    #region Methods

    /// <summary>
    /// Saves an incident: creates it, updates the stored incident it overlaps, or leaves it unchanged. Stored incidents the
    /// incident also overlaps are merged into the oldest.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="savedAt">The time of the save</param>
    /// <param name="cancellationToken">Cancels the save</param>
    /// <returns>A task that returns the outcome</returns>
    /// <exception cref="ArgumentNullException">The incident is <c>null</c></exception>
    /// <exception cref="InvalidIncidentException">The incident breaks a rule</exception>
    Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the stored incidents that overlap a range, ordered by start and ID.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the incidents</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the number of stored incidents.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the number</returns>
    Task<long> CountIncidentsAsync(CancellationToken cancellationToken);

    #endregion // Methods
}