using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

using Vandox.Backend.Hosting;

namespace Vandox.Backend.Security;

/// <summary>
/// Checks the password of a sign-in, applies the failed-sign-in limits and signs the session in or out.
/// </summary>
internal sealed class LoginService : IDisposable
{
    #region Constants

    /// <summary>
    /// The name of the one user.
    /// </summary>
    private const string UserName = "admin";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// How long an attempt waits for the running password check.
    /// </summary>
    internal static readonly TimeSpan VerificationWait = TimeSpan.FromSeconds(5);

    private readonly IPasswordVerifier _verifier;
    private readonly LoginThrottle _throttle;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginService"/> class.
    /// </summary>
    /// <param name="verifier">The password check</param>
    /// <param name="throttle">The failed-sign-in limits</param>
    /// <param name="timeProvider">The clock</param>
    /// <param name="logger">The logger</param>
    public LoginService(IPasswordVerifier verifier, LoginThrottle throttle, TimeProvider timeProvider, ILogger logger)
    {
        _verifier = verifier;
        _throttle = throttle;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Checks a password and signs the request in when it is right.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="password">The password; <c>null</c> when the form had none</param>
    /// <param name="cancellationToken">Cancels the attempt</param>
    /// <returns>The outcome; an <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled</returns>
    internal async Task<LoginOutcome> SignInAsync(HttpContext context, string? password, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(VerificationWait, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return LoginOutcome.Throttled;
        }

        try
        {
            return await CheckAsync(context, password).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the session of the request.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the session is ended</returns>
    internal async Task SignOutAsync(HttpContext context)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);

        var client = ClientText(context);

        _logger.LoggedOut(client);
    }

    /// <summary>
    /// Signs in a new session.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the cookie is set</returns>
    private static Task SignInUserAsync(HttpContext context)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, UserName), new Claim(SessionStore.SessionClaimType, SessionStore.NewSessionId())], CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties
                         {
                             IsPersistent = false
                         };

        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
    }

    /// <summary>
    /// Returns the address of the client as text.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>The address, or <c>unknown</c></returns>
    private static string ClientText(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? LoginThrottle.UnknownClient;
    }

    /// <summary>
    /// Formats a time as UTC in RFC 3339.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text</returns>
    private static string FormatUtc(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks one attempt while the gate is held.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="password">The password; <c>null</c> when the form had none</param>
    /// <returns>The outcome</returns>
    private async Task<LoginOutcome> CheckAsync(HttpContext context, string? password)
    {
        var key = LoginThrottle.ClientKey(context.Connection.RemoteIpAddress);

        if (_throttle.IsLockedOut(key))
        {
            return LoginOutcome.Throttled;
        }

        if (password is not null && password.Length <= PasswordHash.MaxPasswordLength && _verifier.Verify(password))
        {
            _throttle.RecordSuccess(key);
            await SignInUserAsync(context).ConfigureAwait(false);

            var client = ClientText(context);

            _logger.LoginSucceeded(client);

            return LoginOutcome.SignedIn;
        }

        RecordFailure(context, key);

        return LoginOutcome.WrongPassword;
    }

    /// <summary>
    /// Records a failed attempt and logs it and the locks it started.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="key">The key of the client</param>
    private void RecordFailure(HttpContext context, string key)
    {
        var client = ClientText(context);
        var failure = _throttle.RecordFailure(key);

        _logger.LoginFailed(client, failure.Failures);

        if (failure.ClientLockedUntil is { } clientUntil)
        {
            var until = FormatUtc(clientUntil);

            _logger.LoginLocked(client, until);

            if (context.Request.Headers.ContainsKey("X-Forwarded-For") && StringValues.IsNullOrEmpty(context.Request.Headers["X-Original-For"]))
            {
                _logger.ForwardedHeaderIgnored(client);
            }
        }

        if (failure.AllLockedUntil is { } allUntil)
        {
            var until = FormatUtc(allUntil);

            _logger.LoginLockedForAll(until);
        }
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        _gate.Dispose();
    }

    #endregion // IDisposable
}