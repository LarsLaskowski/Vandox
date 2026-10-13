using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Security;

/// <summary>
/// Wires the login of the web UI: cookie authentication, sessions, antiforgery, form limits and the sign-out endpoint.
/// </summary>
internal static class WebAuthentication
{
    #region Constants

    /// <summary>
    /// The name of the session cookie.
    /// </summary>
    internal const string CookieName = "vandox_session";

    /// <summary>
    /// The name of the antiforgery cookie.
    /// </summary>
    internal const string AntiforgeryCookieName = "vandox_csrf";

    /// <summary>
    /// The path of the login page.
    /// </summary>
    internal const string LoginPath = "/login";

    /// <summary>
    /// The path of the sign-out endpoint.
    /// </summary>
    internal const string LogoutPath = "/logout";

    /// <summary>
    /// The longest form value in characters.
    /// </summary>
    internal const int MaxFormValueLength = 16384;

    /// <summary>
    /// The most form values.
    /// </summary>
    internal const int MaxFormValueCount = 64;

    /// <summary>
    /// The longest multipart body in bytes; also the memory buffer threshold.
    /// </summary>
    internal const int MaxMultipartBodyLength = 65536;

    private const string BlazorPath = "/_blazor";

    private const string LoggerCategory = "login";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Registers the services of the login.
    /// </summary>
    /// <param name="services">The services</param>
    /// <param name="verifier">The password check</param>
    /// <param name="timeProvider">The clock</param>
    /// <param name="loggerFactory">The logger factory</param>
    internal static void AddServices(IServiceCollection services, IPasswordVerifier verifier, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        var sessions = new SessionStore(timeProvider);
        var throttle = new LoginThrottle(timeProvider);

        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => ConfigureCookie(options, sessions, timeProvider));
        services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SessionRevalidatingStateProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, SessionCircuitHandler>());
        services.AddAntiforgery(options =>
                                {
                                    options.HeaderName = null;
                                    options.Cookie.Name = AntiforgeryCookieName;
                                    options.Cookie.HttpOnly = true;
                                    options.Cookie.SameSite = SameSiteMode.Strict;
                                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                                    options.Cookie.Path = "/";
                                });
        services.Configure<FormOptions>(options =>
                                        {
                                            options.ValueLengthLimit = MaxFormValueLength;
                                            options.ValueCountLimit = MaxFormValueCount;
                                            options.MultipartBodyLengthLimit = MaxMultipartBodyLength;
                                            options.MemoryBufferThreshold = MaxMultipartBodyLength;
                                        });
        services.AddSingleton(sessions);
        services.AddSingleton(throttle);
        services.AddSingleton(verifier);
        services.AddSingleton(_ => new LoginService(verifier, throttle, timeProvider, loggerFactory.CreateLogger(LoggerCategory)));
    }

    /// <summary>
    /// Maps <c>POST /logout</c>.
    /// </summary>
    /// <param name="endpoints">The endpoints</param>
    internal static void MapLogout(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(LogoutPath, LogoutAsync).WithMetadata(RequiredAntiforgery.Instance).AllowAnonymous();
    }

    /// <summary>
    /// Signs out; answers <c>400</c> unless the antiforgery check of the middleware passed.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="loginService">The login service</param>
    /// <returns>A task that completes when the response is written</returns>
    internal static async Task LogoutAsync(HttpContext context, LoginService loginService)
    {
        var feature = context.Features.Get<IAntiforgeryValidationFeature>();

        if (feature is { IsValid: true })
        {
            await loginService.SignOutAsync(context).ConfigureAwait(false);
            context.Response.StatusCode = StatusCodes.Status303SeeOther;
            context.Response.Headers.Location = LoginPath;

            return;
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }

    /// <summary>
    /// Redirects a page load to the login page and answers every other request with <c>401</c>.
    /// </summary>
    /// <param name="context">The redirect event</param>
    /// <returns>A task that completes when the response is set</returns>
    internal static Task RedirectToLoginAsync(RedirectContext<CookieAuthenticationOptions> context)
    {
        var request = context.Request;
        var safeMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

        if (safeMethod && request.Path.StartsWithSegments(BlazorPath))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else if (safeMethod)
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = LoginPath;
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets the options of the session cookie.
    /// </summary>
    /// <param name="options">The options</param>
    /// <param name="sessions">The sessions</param>
    /// <param name="timeProvider">The clock</param>
    private static void ConfigureCookie(CookieAuthenticationOptions options, SessionStore sessions, TimeProvider timeProvider)
    {
        options.Cookie.Name = CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = SessionStore.AbsoluteLifetime;
        options.SlidingExpiration = false;
        options.SessionStore = sessions;
        options.TimeProvider = timeProvider;
        options.Events.OnRedirectToLogin = RedirectToLoginAsync;
        options.Events.OnRedirectToAccessDenied = context =>
                                                  {
                                                      context.Response.StatusCode = StatusCodes.Status403Forbidden;

                                                      return Task.CompletedTask;
                                                  };
    }

    #endregion // Methods
}