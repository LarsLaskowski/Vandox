using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Reads the incidents through the shared gate of the web views; saving a note does not wait for a slot.
/// </summary>
public sealed class GatedIncidentBrowser : IIncidentBrowser
{
    #region Fields

    private readonly IIncidentBrowser _inner;

    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedIncidentBrowser"/> class.
    /// </summary>
    /// <param name="inner">The browser to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedIncidentBrowser(IIncidentBrowser inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IIncidentBrowser

    /// <inheritdoc />
    public Task<IReadOnlyList<IncidentSummary>> IncidentSummariesAsync(IncidentListQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.IncidentSummariesAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<StoredIncident?> IncidentAsync(long id, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.IncidentAsync(id, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IncidentNotePage> IncidentNotesAsync(long incidentId, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.IncidentNotesAsync(incidentId, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IncidentNoteOutcome> SaveIncidentNoteAsync(IncidentNoteEdit edit, CancellationToken cancellationToken)
    {
        return _inner.SaveIncidentNoteAsync(edit, cancellationToken);
    }

    #endregion // IIncidentBrowser
}