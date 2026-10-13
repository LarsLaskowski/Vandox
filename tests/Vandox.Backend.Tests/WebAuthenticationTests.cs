using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="WebAuthentication"/>
/// </summary>
[TestClass]
public class WebAuthenticationTests
{
    #region Methods

    /// <summary>
    /// Only the load of a page is redirected to the login page, without a query; every other request and the live connection get 401.
    /// </summary>
    /// <param name="method">The method</param>
    /// <param name="target">The path and query</param>
    /// <param name="status">The expected status</param>
    /// <param name="location">The expected <c>Location</c>, empty for none</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("GET", "/", 302, "/login")]
    [DataRow("HEAD", "/", 302, "/login")]
    [DataRow("GET", "/nothing-here?next=%2Fsecret", 302, "/login")]
    [DataRow("POST", "/", 401, "")]
    [DataRow("POST", "/logout", 401, "")]
    [DataRow("GET", "/_blazor", 401, "")]
    [DataRow("POST", "/_blazor/negotiate?negotiateVersion=1", 401, "")]
    public async Task WebAuthenticationRedirectsOnlyPageLoadsToLogin(string method, string target, int status, string location)
    {
        // Arrange
        var parts = target.Split('?', 2);
        var context = new DefaultHttpContext();

        context.Request.Method = method;
        context.Request.Path = parts[0];
        context.Request.QueryString = parts.Length > 1 ? new QueryString("?" + parts[1]) : QueryString.Empty;

        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var redirect = new RedirectContext<CookieAuthenticationOptions>(context, scheme, new CookieAuthenticationOptions(), new AuthenticationProperties(), "/login?ReturnUrl=%2F");

        // Act
        await WebAuthentication.RedirectToLoginAsync(redirect);

        // Assert
        Assert.AreEqual(status, context.Response.StatusCode, "status");
        Assert.AreEqual(location, context.Response.Headers.Location.ToString(), "location");
    }

    /// <summary>
    /// The form options are set explicitly so that no form part is ever buffered to a file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WebAuthenticationConfiguresFormLimits()
    {
        // Arrange
        using var verifier = new ScriptedPasswordVerifier("password");
        var services = new ServiceCollection();

        services.AddLogging();
        WebAuthentication.AddServices(services, verifier, new FakeTimeProvider(), NullLoggerFactory.Instance);

        await using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<FormOptions>>().Value;

        // Assert
        Assert.AreEqual(16384, options.ValueLengthLimit, "the length of a value");
        Assert.AreEqual(64, options.ValueCountLimit, "the number of values");
        Assert.AreEqual(65536, options.MultipartBodyLengthLimit, "the size of a multipart body");
        Assert.AreEqual(65536, options.MemoryBufferThreshold, "nothing is buffered to a file");
    }

    /// <summary>
    /// A signed-in user who is refused gets 403 and no redirect.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WebAuthenticationAnswersAccessDeniedWith403()
    {
        // Arrange
        using var verifier = new ScriptedPasswordVerifier("password");
        var services = new ServiceCollection();

        services.AddLogging();
        WebAuthentication.AddServices(services, verifier, new FakeTimeProvider(), NullLoggerFactory.Instance);

        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var context = new DefaultHttpContext();
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var denied = new RedirectContext<CookieAuthenticationOptions>(context, scheme, options, new AuthenticationProperties(), "/AccessDenied");

        // Act
        await options.Events.OnRedirectToAccessDenied(denied);

        // Assert
        Assert.AreEqual(403, context.Response.StatusCode, "status");
        Assert.AreEqual(string.Empty, context.Response.Headers.Location.ToString(), "no redirect");
    }

    /// <summary>
    /// <c>POST /logout</c> is mapped with antiforgery validation required, and the endpoint is open to a request without a session.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WebAuthenticationMapsLogoutWithAntiforgeryMetadata()
    {
        // Arrange
        await using var app = WebApplication.CreateSlimBuilder().Build();

        // Act
        WebAuthentication.MapLogout(app);

        // Assert
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Where(endpoint => endpoint.RoutePattern.RawText == "/logout").ToArray();

        Assert.HasCount(1, endpoints, "one endpoint for /logout");
        Assert.AreSequenceEqual(["POST"], endpoints[0].Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.ToArray(), "only POST");
        Assert.IsNotNull(endpoints[0].Metadata.GetMetadata<IAntiforgeryMetadata>(), "antiforgery metadata");
        Assert.IsTrue(endpoints[0].Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation, "the token is required");
        Assert.IsNotNull(endpoints[0].Metadata.GetMetadata<IAllowAnonymous>(), "reachable without a session");
    }

    /// <summary>
    /// The logout handler answers 400 and does not sign out unless the antiforgery check passed.
    /// </summary>
    /// <param name="feature">The antiforgery result: <c>none</c>, <c>invalid</c> or <c>valid</c></param>
    /// <param name="status">The expected status</param>
    /// <param name="signedOut">Whether the user is expected to be signed out</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("none", 400, false)]
    [DataRow("invalid", 400, false)]
    [DataRow("valid", 303, true)]
    public async Task WebAuthenticationLogoutRefusesMissingOrInvalidAntiforgeryFeature(string feature, int status, bool signedOut)
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var verifier = new ScriptedPasswordVerifier("password");
        var login = new LoginService(verifier, new LoginThrottle(clock), clock, NullLogger.Instance);
        var authentication = new RecordingAuthenticationService();
        var context = new DefaultHttpContext
                      {
                          RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider()
                      };

        if (feature != "none")
        {
            context.Features.Set<IAntiforgeryValidationFeature>(new FixedAntiforgeryValidationFeature(feature == "valid"));
        }

        // Act
        await WebAuthentication.LogoutAsync(context, login);

        // Assert
        Assert.AreEqual(status, context.Response.StatusCode, "status");
        Assert.HasCount(signedOut ? 1 : 0, authentication.SignOuts, "sign-outs");
        Assert.AreEqual(signedOut ? "/login" : string.Empty, context.Response.Headers.Location.ToString(), "location");
    }

    #endregion // Methods
}