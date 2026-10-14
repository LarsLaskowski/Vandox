using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;

namespace Vandox.Backend.Components;

/// <summary>
/// The root component: the page shell of every page.
/// </summary>
public sealed partial class App
{
    #region Properties

    /// <summary>
    /// Gets or sets the request of the page.
    /// </summary>
    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    /// <summary>
    /// Gets the render mode of the page: interactive, except for a page that is excluded from interactive routing; <c>null</c> for a page without a live connection.
    /// </summary>
    private IComponentRenderMode? PageRenderMode => HttpContext is { } context && context.AcceptsInteractiveRouting() ? RenderMode.InteractiveServer : null;

    #endregion // Properties
}