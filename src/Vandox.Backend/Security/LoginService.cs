using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Security;

#pragma warning disable RH2003, S2325

/// <summary>
/// Checks the password of a sign-in, applies the failed-sign-in limits and signs the session in or out.
/// </summary>
internal sealed class LoginService
{
    #region Fields

    /// <summary>
    /// How long an attempt waits for the running password check.
    /// </summary>
    internal static readonly TimeSpan VerificationWait = TimeSpan.FromSeconds(5);

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
        throw new NotImplementedException();
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
    internal Task<LoginOutcome> SignInAsync(HttpContext context, string? password, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Ends the session of the request.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the session is ended</returns>
    internal Task SignOutAsync(HttpContext context)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}