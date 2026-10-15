using Microsoft.AspNetCore.Components;

using Vandox.Backend.Reports;

namespace Vandox.Backend.Components.Pages;

#pragma warning disable S1144, S4487

/// <summary>
/// The page <c>/report</c> of the web UI.
/// </summary>
public sealed partial class ReportPage : ComponentBase, IDisposable
{
    #region Properties

    /// <summary>
    /// Gets or sets the report service.
    /// </summary>
    [Inject]
    private ReportService Reports { get; set; } = null!;

    /// <summary>
    /// Gets or sets the navigation manager.
    /// </summary>
    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<ReportPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    #endregion // IDisposable
}