using Vandox.Core.Incidents;

namespace Vandox.Storage;

/// <summary>
/// Browses the stored incidents and keeps their notes.
/// </summary>
public interface IIncidentBrowser
{
    #region Methods

    /// <summary>
    /// Returns a page of the incident list, newest first.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the summaries</returns>
    /// <exception cref="ArgumentNullException">The query is <c>null</c></exception>
    /// <exception cref="InvalidQueryException">The query breaks a rule</exception>
    Task<IReadOnlyList<IncidentSummary>> IncidentSummariesAsync(IncidentListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns one incident.
    /// </summary>
    /// <param name="id">The number</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the incident, or <c>null</c> when none has the number</returns>
    /// <exception cref="InvalidQueryException">The number is not positive</exception>
    Task<StoredIncident?> IncidentAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the notes of one incident.
    /// </summary>
    /// <param name="incidentId">The number of the incident</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the notes</returns>
    /// <exception cref="InvalidQueryException">The number is not positive</exception>
    Task<IncidentNotePage> IncidentNotesAsync(long incidentId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves, replaces or deletes a note.
    /// </summary>
    /// <param name="edit">The edit</param>
    /// <param name="cancellationToken">Cancels the save</param>
    /// <returns>A task that returns the outcome</returns>
    /// <exception cref="ArgumentNullException">The edit is <c>null</c></exception>
    /// <exception cref="InvalidNoteException">The edit breaks a rule</exception>
    Task<IncidentNoteOutcome> SaveIncidentNoteAsync(IncidentNoteEdit edit, CancellationToken cancellationToken);

    #endregion // Methods
}