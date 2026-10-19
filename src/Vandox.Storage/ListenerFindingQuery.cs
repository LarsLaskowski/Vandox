using Vandox.Core.Reports;

namespace Vandox.Storage;

/// <summary>
/// The query for the latest listener findings of one kind.
/// </summary>
public sealed class ListenerFindingQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the kind of the findings.
    /// </summary>
    public ListenerFindingKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the largest number of findings, 1 to <see cref="ListenerReport.MaxFindings"/>.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}