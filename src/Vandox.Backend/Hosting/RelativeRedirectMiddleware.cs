using Microsoft.AspNetCore.Http;

namespace Vandox.Backend.Hosting;

/// <summary>
/// Turns a redirect to the request's own scheme and host into a redirect to the path alone, so the browser stays on the address it used.
/// Behind a reverse proxy the service sees <c>http</c> (or an inner host name) where the browser used <c>https</c>; an absolute
/// <c>Location</c> built from that would leave the page's origin, which <c>form-action 'self'</c> blocks after a form post.
/// </summary>
public sealed class RelativeRedirectMiddleware
{
    #region Fields

    private readonly RequestDelegate _next;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RelativeRedirectMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline</param>
    public RelativeRedirectMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Returns the <c>Location</c> to send: the path, query and fragment when <paramref name="location"/> is an absolute address of the
    /// request's own scheme and host, otherwise <paramref name="location"/> unchanged.
    /// </summary>
    /// <param name="scheme">The scheme of the request as the service sees it</param>
    /// <param name="host">The <c>Host</c> of the request</param>
    /// <param name="location">The <c>Location</c> the response carries</param>
    /// <returns>The <c>Location</c> to send</returns>
    public static string RelativeLocation(string scheme, HostString host, string location)
    {
        if (host.HasValue
            && Uri.TryCreate(location, UriKind.Absolute, out var target)
            && Uri.TryCreate($"{scheme}://{host.ToUriComponent()}/", UriKind.Absolute, out var origin)
            && Uri.Compare(target, origin, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            var path = target.PathAndQuery + target.Fragment;

            // "//host" or "/\host" would be read by the browser as an address of another host.
            var otherHost = path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/\\", StringComparison.Ordinal);

            return otherHost ? location : path;
        }

        return location;
    }

    /// <summary>
    /// Rewrites the <c>Location</c> of the response when it starts and passes the request on.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the request is handled</returns>
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
                                    {
                                        Rewrite(context);

                                        return Task.CompletedTask;
                                    });

        return _next(context);
    }

    /// <summary>
    /// Replaces a single <c>Location</c> value by its relative form.
    /// </summary>
    /// <param name="context">The request</param>
    internal static void Rewrite(HttpContext context)
    {
        var headers = context.Response.Headers;

        if (headers.Location.Count != 1)
        {
            return;
        }

        var location = headers.Location.ToString();
        var relative = RelativeLocation(context.Request.Scheme, context.Request.Host, location);

        if (relative != location)
        {
            headers.Location = relative;
        }
    }

    #endregion // Methods
}