namespace Vandox.Backend.Reports;

/// <summary>
/// The report as the page shows it: the summary, the outages of one page and the notes.
/// </summary>
public sealed class ReportDocument
{
    #region Properties

    /// <summary>
    /// Gets the language of the report.
    /// </summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>
    /// Gets the blocks of the summary.
    /// </summary>
    public IReadOnlyList<ReportBlock> Summary { get; internal set; } = [];

    /// <summary>
    /// Gets the blocks of each outage kept.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ReportBlock>> Outages { get; internal set; } = [];

    /// <summary>
    /// Gets the blocks of the notes.
    /// </summary>
    public IReadOnlyList<ReportBlock> Notes { get; internal set; } = [];

    /// <summary>
    /// Gets the zero-based index of the first outage kept.
    /// </summary>
    public long FirstOutage { get; init; }

    /// <summary>
    /// Gets the number of outages of the second pass.
    /// </summary>
    public long OutagesListed { get; internal set; }

    #endregion // Properties
}