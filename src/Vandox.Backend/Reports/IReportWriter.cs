using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Receives the three parts of a forensics report run: the summary, one section per outage, and the notes.
/// </summary>
public interface IReportWriter
{
    #region Methods

    /// <summary>
    /// Writes the head, the summary, the patterns, the consumers and the heading of the outages.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <returns>A task that completes when the part is written</returns>
    Task WriteSummaryAsync(ForensicsSummary summary);

    /// <summary>
    /// Writes the section of one outage.
    /// </summary>
    /// <param name="stored">The stored incident</param>
    /// <param name="activities">The activities before the outage</param>
    /// <returns>A task that completes when the section is written</returns>
    Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities);

    /// <summary>
    /// Writes the notes.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <param name="listed">The number of incidents of the second pass</param>
    /// <returns>A task that completes when the notes are written</returns>
    Task WriteNotesAsync(ForensicsSummary summary, long listed);

    #endregion // Methods
}