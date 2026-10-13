using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// Writes the forensics report as Markdown.
/// </summary>
public sealed class MarkdownReportWriter
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownReportWriter"/> class.
    /// </summary>
    /// <param name="writer">The output</param>
    /// <param name="text">The labels of the language</param>
    public MarkdownReportWriter(TextWriter writer, ReportText text)
    {
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Writes the head, the summary, the patterns, the consumers and the heading of the outages.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <returns>A task that completes when the part is written</returns>
    public Task WriteSummaryAsync(ForensicsSummary summary)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the section of one outage.
    /// </summary>
    /// <param name="stored">The stored incident</param>
    /// <param name="activities">The activities before the outage</param>
    /// <returns>A task that completes when the section is written</returns>
    public Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the notes.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <param name="listed">The number of incidents of the second pass</param>
    /// <returns>A task that completes when the notes are written</returns>
    public Task WriteNotesAsync(ForensicsSummary summary, long listed)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}