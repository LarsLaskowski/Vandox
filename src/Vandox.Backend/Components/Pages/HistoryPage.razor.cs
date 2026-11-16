using Microsoft.AspNetCore.Components;

using Vandox.Storage;

namespace Vandox.Backend.Components.Pages;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S1144, S4487, CA1065

/// <summary>
/// The page <c>/history</c> of the web UI: chosen metrics over a chosen range, and the snapshots of a moment.
/// </summary>
public sealed partial class HistoryPage : ComponentBase, IDisposable
{
    #region Properties

    /// <summary>
    /// Gets or sets the time-series service.
    /// </summary>
    [Inject]
    private TimeSeriesService TimeSeries { get; set; } = null!;

    /// <summary>
    /// Gets or sets the history reader.
    /// </summary>
    [Inject]
    private IHistoryReader Reader { get; set; } = null!;

    /// <summary>
    /// Gets or sets the clock.
    /// </summary>
    [Inject]
    private TimeProvider Clock { get; set; } = null!;

    /// <summary>
    /// Gets or sets the navigation manager.
    /// </summary>
    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    /// <summary>
    /// Gets or sets the logger.
    /// </summary>
    [Inject]
    private ILogger<HistoryPage> Logger { get; set; } = null!;

    #endregion // Properties

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    #endregion // IDisposable
}

#pragma warning restore RH2003, S1144, S4487, CA1065