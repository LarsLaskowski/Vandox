using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

using Vandox.Backend.Ingest;

namespace Vandox.Backend.Hosting;

#pragma warning disable S4487

/// <summary>
/// Keeps the two listeners apart: a request on the ingest listener never reaches the web UI. The ingest API does not exist yet, so
/// the ingest listener answers 404.
/// </summary>
internal sealed class PortRoutingMiddleware
{
    #region Fields

    private readonly RequestDelegate _next;

    /// <summary>
    /// The ingest endpoint, called for <c>POST /v1/batches</c> on an ingest connection.
    /// </summary>
    private readonly IngestEndpoint _ingest;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PortRoutingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline</param>
    /// <param name="ingest">The ingest endpoint</param>
    public PortRoutingMiddleware(RequestDelegate next, IngestEndpoint ingest)
    {
        _next = next;
        _ingest = ingest;
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