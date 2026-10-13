using Microsoft.AspNetCore.Components;

using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

#pragma warning disable RH2003, S2325

/// <summary>
/// The page <c>/logs</c> of the web UI.
/// </summary>
public sealed partial class LogsPage : ComponentBase, IDisposable
{
    #region Properties

    /// <summary>
    /// Gets or sets the log explorer.
    /// </summary>
    [Inject]
    private ILogExplorer Explorer { get; set; } = null!;

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
    private ILogger<LogsPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region ComponentBase

    /// <inheritdoc />
    protected override Task OnParametersSetAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // ComponentBase

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    #endregion // IDisposable
}