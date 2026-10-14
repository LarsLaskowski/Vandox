#pragma warning disable RH2003, S2325

using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Reads the incidents through the shared gate of the web views; saving a note does not wait for a slot.
/// </summary>
public sealed class GatedIncidentBrowser : IIncidentBrowser
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedIncidentBrowser"/> class.
    /// </summary>
    /// <param name="inner">The browser to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedIncidentBrowser(IIncidentBrowser inner, WebReaderGate gate)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region IIncidentBrowser

    /// <inheritdoc />
    public Task<IReadOnlyList<IncidentSummary>> IncidentSummariesAsync(IncidentListQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<StoredIncident?> IncidentAsync(long id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IncidentNotePage> IncidentNotesAsync(long incidentId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IncidentNoteOutcome> SaveIncidentNoteAsync(IncidentNoteEdit edit, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // IIncidentBrowser
}