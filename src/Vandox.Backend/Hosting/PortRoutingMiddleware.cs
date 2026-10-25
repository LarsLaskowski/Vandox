using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

using Vandox.Backend.Ingest;

namespace Vandox.Backend.Hosting;

/// <summary>
/// Keeps the two listeners apart: a request on the ingest listener never reaches the web UI. The ingest listener hands
/// <c>POST /v1/batches</c> to the ingest endpoint and answers 404 to every other request.
/// </summary>
internal sealed class PortRoutingMiddleware
{
    #region Constants

    /// <summary>
    /// The path of the ingest endpoint, compared ordinally.
    /// </summary>
    private const string IngestPath = "/v1/batches";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The rest of the pipeline.
    /// </summary>
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
    /// Passes the request on when it came in on the web listener, hands <c>POST /v1/batches</c> of the ingest listener to the ingest endpoint, otherwise answers 404: a connection that no listener labelled is never served the web UI.
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

        if (label == ListenerRoutes.IngestLabel && string.Equals(context.Request.Method, HttpMethods.Post, StringComparison.Ordinal) && string.Equals(context.Request.Path.Value, IngestPath, StringComparison.Ordinal))
        {
            return _ingest.HandleAsync(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;

        return Task.CompletedTask;
    }

    #endregion // Methods
}