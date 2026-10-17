using System.Net;
using System.Text.RegularExpressions;

using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for the login page, through the real service
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Linux)]
public partial class LoginTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The login page is a plain server-rendered form with the antiforgery token, without the framework script and live connection, and it carries the security headers.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginPageRendersStaticFormWithoutLiveConnection()
    {
        // Arrange
        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);

        using var client = new LoginTestClient(fixture.WebPort);

        // Act
        using var response = await client.GetAsync("/login", TestContext.CancellationToken);

        var html = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        var fields = LoginTestClient.HiddenFields(html);
        var password = PasswordInput().Match(html);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "status");
        Assert.Contains("<form method=\"post\"", html, "a form that posts");
        Assert.IsNotEmpty(fields.GetValueOrDefault(LoginTestClient.TokenField, string.Empty), "hidden antiforgery token");
        Assert.AreEqual("login", fields.GetValueOrDefault(LoginTestClient.HandlerField), "the handler of the form");
        Assert.IsTrue(password.Success, "a password input");
        Assert.Contains("autocomplete=\"current-password\"", password.Value, "autocomplete of the password input");
        Assert.DoesNotContain("_framework/blazor.web.js", html, "no framework script");
        Assert.DoesNotContain("<!--Blazor:", html, "no live connection marker");
        Assert.IsEmpty(WebSourceScanner.FindInMarkup(html), "rule ids found in the served page");
        Assert.AreSequenceEqual([SecurityHeadersMiddleware.ContentSecurityPolicy], HeaderValues(response, "Content-Security-Policy"), "exactly one policy");
        Assert.AreSequenceEqual([SecurityHeadersMiddleware.ContentTypeOptions], HeaderValues(response, "X-Content-Type-Options"), "exactly one content type options value");
        Assert.AreSequenceEqual([SecurityHeadersMiddleware.ReferrerPolicy], HeaderValues(response, "Referrer-Policy"), "exactly one referrer policy");
        Assert.AreSequenceEqual([SecurityHeadersMiddleware.FrameOptions], HeaderValues(response, "X-Frame-Options"), "exactly one frame options value");
    }

    /// <summary>
    /// A wrong password shows the login page again with status 401 and the message, and the page does not contain the password.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginShowsWrongPasswordWithoutEchoingIt()
    {
        // Arrange
        const string sent = "wrong-password-marker-7";

        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);

        using var client = new LoginTestClient(fixture.WebPort);

        // Act
        using var response = await client.PostLoginAsync(sent, TestContext.CancellationToken);

        var html = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "status");
        Assert.Contains("The password is not correct.", html, "the message");
        Assert.Contains("<form method=\"post\"", html, "the form is shown again");
        Assert.DoesNotContain(sent, html, "the password is not echoed");
        Assert.IsNull(LoginTestClient.SetCookie(response, LoginTestClient.SessionCookie), "no session");
    }

    /// <summary>
    /// The sign-in and the signed-in visits of the login page redirect to the path <c>/</c> alone, whatever host the request names, and
    /// a second post of the same form (its token issued before the sign-in) also goes home instead of being refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginRedirectsToRelativeHomeAlsoForSecondPost()
    {
        // Arrange
        var host = new Dictionary<string, string>
                   {
                       ["Host"] = "vandox.home.example"
                   };

        using var fixture = new BackendFixture();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var run = await fixture.StartServiceAsync(stop.Token, TestContext.CancellationToken);

        using var client = new LoginTestClient(fixture.WebPort);

        var fields = await client.ReadFormFieldsAsync("/login", TestContext.CancellationToken, host);

        fields[LoginTestClient.PasswordField] = BackendFixture.Password;

        // Act
        using var first = await client.PostFormAsync("/login", fields, TestContext.CancellationToken, host);
        using var second = await client.PostFormAsync("/login", fields, TestContext.CancellationToken, host);
        using var page = await client.GetAsync("/login", TestContext.CancellationToken, host);

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(HttpStatusCode.Found, first.StatusCode, "the sign-in redirects");
        Assert.AreEqual("/", first.Headers.Location?.OriginalString, "the sign-in redirects to the path alone");
        Assert.IsTrue(client.HasSession, "the client holds a session cookie");
        Assert.AreEqual(HttpStatusCode.Found, second.StatusCode, "the second post redirects instead of answering 400");
        Assert.AreEqual("/", second.Headers.Location?.OriginalString, "the second post goes home");
        Assert.AreEqual(HttpStatusCode.Found, page.StatusCode, "the signed-in login page redirects");
        Assert.AreEqual("/", page.Headers.Location?.OriginalString, "the signed-in login page goes home by path");
    }

    /// <summary>
    /// Reads the values of a response header.
    /// </summary>
    /// <param name="response">The response</param>
    /// <param name="name">The name of the header</param>
    /// <returns>The values, empty when the header is absent</returns>
    private static string[] HeaderValues(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.ToArray() : [];
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("<input\\b[^>]*\\btype=\"password\"[^>]*>")]
    private static partial Regex PasswordInput();

    #endregion // Patterns
}