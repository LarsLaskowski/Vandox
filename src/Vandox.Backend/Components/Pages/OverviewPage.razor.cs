using Microsoft.AspNetCore.Components;

using Vandox.Backend.Overview;
using Vandox.Backend.Security;
using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S1144, S2325
/// <summary>
/// The page <c>/</c> of the web UI: the overview of the monitored server.
/// </summary>
public sealed partial class OverviewPage : ComponentBase, IAsyncDisposable
{
    #region Properties

    /// <summary>
    /// Gets or sets the overview reader.
    /// </summary>
    [Inject]
    private IOverviewReader Reader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the incident browser.
    /// </summary>
    [Inject]
    private IIncidentBrowser Incidents { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the connection of the circuit.
    /// </summary>
    [Inject]
    private CircuitConnection Connection { get; set; } = null!;

    /// <summary>
    /// Gets or sets the activity of the circuit.
    /// </summary>
    [Inject]
    private SessionActivity Activity { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<OverviewPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region ComponentBase

    /// <inheritdoc/>
    protected override void OnAfterRender(bool firstRender)
    {
        throw new NotImplementedException();
    }

    #endregion // ComponentBase

    #region IAsyncDisposable

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // IAsyncDisposable
}
#pragma warning restore RH2003, S1144, S2325