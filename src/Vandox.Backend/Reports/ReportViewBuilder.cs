using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325, S4487, S2372

/// <summary>
/// Collects a report run into a <see cref="ReportDocument"/>: the summary, the outages of one page and the notes.
/// </summary>
public sealed class ReportViewBuilder : IReportWriter
{
    #region Fields

    private readonly ReportText _text;

    private readonly long _firstOutage;

    private readonly int _maxOutages;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportViewBuilder"/> class.
    /// </summary>
    /// <param name="text">The labels of the language</param>
    /// <param name="firstOutage">The zero-based index of the first outage to keep, 0 or more</param>
    /// <param name="maxOutages">The number of outages to keep, 1 or more</param>
    public ReportViewBuilder(ReportText text, long firstOutage, int maxOutages)
    {
        _text = text;
        _firstOutage = firstOutage;
        _maxOutages = maxOutages;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the document the run built.
    /// </summary>
    public ReportDocument Document => throw new NotImplementedException();

    #endregion // Properties

    #region IReportWriter

    /// <inheritdoc />
    public Task WriteSummaryAsync(ForensicsSummary summary)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task WriteNotesAsync(ForensicsSummary summary, long listed)
    {
        throw new NotImplementedException();
    }

    #endregion // IReportWriter
}