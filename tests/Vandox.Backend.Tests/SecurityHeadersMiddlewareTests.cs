using Microsoft.AspNetCore.Http;

using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="SecurityHeadersMiddleware"/>
/// </summary>
[TestClass]
public class SecurityHeadersMiddlewareTests
{
    #region Methods

    /// <summary>
    /// The middleware sets the four headers before it calls the rest of the pipeline, and calls it exactly once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SecurityHeadersMiddlewareSetsHeadersAndCallsNext()
    {
        // Arrange
        var calls = 0;
        var policyInNext = string.Empty;
        var typeOptionsInNext = string.Empty;
        var referrerInNext = string.Empty;
        var frameInNext = string.Empty;
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(next =>
                                                       {
                                                           calls++;
                                                           policyInNext = next.Response.Headers.ContentSecurityPolicy.ToString();
                                                           typeOptionsInNext = next.Response.Headers.XContentTypeOptions.ToString();
                                                           referrerInNext = next.Response.Headers["Referrer-Policy"].ToString();
                                                           frameInNext = next.Response.Headers.XFrameOptions.ToString();

                                                           return Task.CompletedTask;
                                                       });

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.AreEqual(1, calls, "the next delegate is called exactly once");
        Assert.AreEqual(SecurityHeadersMiddleware.ContentSecurityPolicy, policyInNext, "policy is set before the next delegate runs");
        Assert.AreEqual("nosniff", typeOptionsInNext, "content type options are set before the next delegate runs");
        Assert.AreEqual("no-referrer", referrerInNext, "referrer policy is set before the next delegate runs");
        Assert.AreEqual("DENY", frameInNext, "frame options are set before the next delegate runs");
        Assert.AreEqual(SecurityHeadersMiddleware.ContentSecurityPolicy, context.Response.Headers.ContentSecurityPolicy.ToString(), "policy after the request");
    }

    /// <summary>
    /// A value that is already present is replaced, so each header ends with exactly one value.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SecurityHeadersMiddlewareReplacesExistingHeaders()
    {
        // Arrange
        var context = new DefaultHttpContext();
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        context.Response.Headers.XFrameOptions = "SAMEORIGIN";
        context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'self'";
        context.Response.Headers.XContentTypeOptions = "other";
        context.Response.Headers["Referrer-Policy"] = "origin";

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        AssertSingle("DENY", context.Response.Headers.XFrameOptions, "frame options");
        AssertSingle(SecurityHeadersMiddleware.ContentSecurityPolicy, context.Response.Headers.ContentSecurityPolicy, "policy");
        AssertSingle("nosniff", context.Response.Headers.XContentTypeOptions, "content type options");
        AssertSingle("no-referrer", context.Response.Headers["Referrer-Policy"], "referrer policy");
    }

    /// <summary>
    /// Asserts that a header has exactly one value.
    /// </summary>
    /// <param name="expected">The expected value</param>
    /// <param name="actual">The values of the header</param>
    /// <param name="message">The assertion message</param>
    private static void AssertSingle(string expected, Microsoft.Extensions.Primitives.StringValues actual, string message)
    {
        Assert.HasCount(1, actual, $"{message}: number of values");
        Assert.AreEqual(expected, actual[0], message);
    }

    #endregion // Methods
}