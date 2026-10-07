using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Vandox.Backend;

/// <summary>
/// The health endpoint of the web listener: GET and HEAD <c>/healthz</c>, 200 when the database answers in time, 503
/// otherwise. It never returns an error text.
/// </summary>
internal static class HealthEndpoint
{
    #region Methods

    /// <summary>
    /// Maps <c>/healthz</c>.
    /// </summary>
    /// <param name="endpoints">The route builder</param>
    /// <param name="checker">The shared database check</param>
    /// <param name="logger">Receives the failures of the check</param>
    internal static void Map(IEndpointRouteBuilder endpoints, PingChecker checker, ILogger logger)
    {
        endpoints.MapMethods("/healthz", ["GET", "HEAD"], context => HandleAsync(context, checker, logger));
    }

    /// <summary>
    /// Answers one health request.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="checker">The shared database check</param>
    /// <param name="logger">Receives the failures of the check</param>
    /// <returns>A task that completes when the response is written</returns>
    private static async Task HandleAsync(HttpContext context, PingChecker checker, ILogger logger)
    {
        var headers = context.Response.Headers;

        headers.ContentType = "text/plain; charset=utf-8";
        headers.CacheControl = "no-store";
        headers.XContentTypeOptions = "nosniff";

        try
        {
            await checker.CheckAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.HealthCheckFailed(exception, exception.GetType().Name);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync("unavailable\n", CancellationToken.None).ConfigureAwait(false);

            return;
        }

        await context.Response.WriteAsync("ok\n", CancellationToken.None).ConfigureAwait(false);
    }

    #endregion // Methods
}