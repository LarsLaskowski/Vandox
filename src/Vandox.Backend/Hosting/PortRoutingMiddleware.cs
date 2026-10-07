using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Vandox.Backend;

/// <summary>
/// Keeps the two listeners apart: a request on the ingest listener never reaches the web UI. The ingest API does not exist yet, so
/// the ingest listener answers 404.
/// </summary>
public sealed class PortRoutingMiddleware
{
    #region Fields

    private readonly RequestDelegate _next;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PortRoutingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline</param>
    public PortRoutingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Passes the request on when it came in on the web listener, otherwise answers 404: a connection that no listener labelled is never served the web UI.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the request is handled</returns>
    public Task InvokeAsync(HttpContext context)
    {
        string? label = null;

        if (context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ListenerRoutes.ItemName, out var item) == true)
        {
            label = item as string;
        }

        if (label == ListenerRoutes.WebLabel)
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        return Task.CompletedTask;
    }

    #endregion // Methods
}