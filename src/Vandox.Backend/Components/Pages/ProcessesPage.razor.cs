using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// The page <c>/processes</c> of the web UI: memory and CPU per program over a chosen range, and the top processes of a moment.
/// </summary>
public sealed partial class ProcessesPage : ComponentBase, IDisposable
{
    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        // Skeleton: nothing is held yet.
    }

    #endregion // IDisposable
}