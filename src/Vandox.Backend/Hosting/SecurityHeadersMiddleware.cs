using Microsoft.AspNetCore.Http;

namespace Vandox.Backend.Hosting;

/// <summary>
/// Sets the security headers on every response of the web listener.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    #region Constants

    /// <summary>
    /// The value of the <c>Content-Security-Policy</c> header: only the own origin may be used.
    /// </summary>
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>
    /// The value of the <c>X-Content-Type-Options</c> header.
    /// </summary>
    public const string ContentTypeOptions = "nosniff";

    /// <summary>
    /// The value of the <c>Referrer-Policy</c> header.
    /// </summary>
    public const string ReferrerPolicy = "no-referrer";

    /// <summary>
    /// The value of the <c>X-Frame-Options</c> header.
    /// </summary>
    public const string FrameOptions = "DENY";

    #endregion // Constants

    #region Fields

    private readonly RequestDelegate _next;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityHeadersMiddleware"/> class.
    /// </summary>
    /// <param name="next">The rest of the pipeline</param>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Sets the security headers and passes the request on.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the request is handled</returns>
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = ContentTypeOptions;
        headers.XFrameOptions = FrameOptions;
        headers["Referrer-Policy"] = ReferrerPolicy;

        return _next(context);
    }

    #endregion // Methods
}