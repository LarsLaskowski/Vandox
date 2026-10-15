using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Turns the three parts of a report run into blocks, in the words of one language.
/// </summary>
internal sealed class ReportComposer
{
    #region Fields

    private readonly ReportText _text;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportComposer"/> class.
    /// </summary>
    /// <param name="text">The labels of the language</param>
    internal ReportComposer(ReportText text)
    {
        _text = text;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Composes the head, the summary, the patterns, the consumers and the heading of the outages.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <returns>The blocks</returns>
    internal IReadOnlyList<ReportBlock> Summary(ForensicsSummary summary)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Composes the section of one outage.
    /// </summary>
    /// <param name="stored">The stored incident</param>
    /// <param name="activities">The activities before the outage</param>
    /// <returns>The blocks</returns>
    internal IReadOnlyList<ReportBlock> Incident(StoredIncident stored, IncidentActivities activities)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Composes the notes.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <param name="listed">The number of incidents of the second pass</param>
    /// <returns>The blocks</returns>
    internal IReadOnlyList<ReportBlock> Notes(ForensicsSummary summary, long listed)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}