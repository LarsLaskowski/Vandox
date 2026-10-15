using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Collects a report run into a <see cref="ReportDocument"/>: the summary, the outages of one page and the notes.
/// </summary>
public sealed class ReportViewBuilder : IReportWriter
{
    #region Fields

    private readonly ReportComposer _composer;

    private readonly long _firstOutage;

    private readonly int _maxOutages;

    private readonly List<IReadOnlyList<ReportBlock>> _outages = [];

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
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(firstOutage);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutages, 1);

        _composer = new ReportComposer(text);
        _firstOutage = firstOutage;
        _maxOutages = maxOutages;
        Document = new ReportDocument
                   {
                       Language = text.Language,
                       FirstOutage = firstOutage,
                       Outages = _outages
                   };
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the document the run built.
    /// </summary>
    public ReportDocument Document { get; }

    #endregion // Properties

    #region IReportWriter

    /// <inheritdoc />
    public Task WriteSummaryAsync(ForensicsSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        Document.Summary = _composer.Summary(summary);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(activities);

        var index = Document.OutagesListed;

        if (index >= _firstOutage && index < _firstOutage + _maxOutages)
        {
            _outages.Add(_composer.Incident(stored, activities));
        }

        Document.OutagesListed = index + 1;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task WriteNotesAsync(ForensicsSummary summary, long listed)
    {
        ArgumentNullException.ThrowIfNull(summary);

        Document.Notes = _composer.Notes(summary, listed);

        return Task.CompletedTask;
    }

    #endregion // IReportWriter
}