using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/network</c> of the web UI: connections per state and sockets per program over a chosen range, the top remote addresses and the listeners of a snapshot.
/// </summary>
public sealed partial class NetworkPage : ComponentBase, IDisposable
{
    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        // Skeleton: removed with the implementation
    }

    #endregion // IDisposable
}