using Microsoft.AspNetCore.Components;

using Vandox.Backend.Reports;

namespace Vandox.Backend.Components.Reports;

/// <summary>
/// Shows a report document as headings, paragraphs, lists and tables.
/// </summary>
public sealed partial class ReportContent : ComponentBase
{
    #region Properties

    /// <summary>
    /// Gets or sets the document to show.
    /// </summary>
    [Parameter]
    [EditorRequired]
    public ReportDocument Document { get; set; } = null!;

    #endregion // Properties
}