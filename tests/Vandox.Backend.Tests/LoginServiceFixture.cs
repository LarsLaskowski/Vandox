using System.Net;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// A <see cref="LoginService"/> over a <see cref="ScriptedPasswordVerifier"/>, a throttle and a fake clock, and request contexts for it.
/// </summary>
internal sealed class LoginServiceFixture : IDisposable
{
    #region Constants

    /// <summary>
    /// The password the verifier accepts unless another one is given.
    /// </summary>
    internal const string Accepted = "the-accepted-password";

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginServiceFixture"/> class.
    /// </summary>
    /// <param name="accepted">The password the verifier accepts</param>
    internal LoginServiceFixture(string accepted = Accepted)
    {
        Verifier = new ScriptedPasswordVerifier(accepted);
        Throttle = new LoginThrottle(Clock);
        Service = new LoginService(Verifier, Throttle, Clock, Logger);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the clock.
    /// </summary>
    internal FakeTimeProvider Clock { get; } = new();

    /// <summary>
    /// Gets the verifier.
    /// </summary>
    internal ScriptedPasswordVerifier Verifier { get; }

    /// <summary>
    /// Gets the throttle.
    /// </summary>
    internal LoginThrottle Throttle { get; }

    /// <summary>
    /// Gets the logger.
    /// </summary>
    internal ListLogger Logger { get; } = new();

    /// <summary>
    /// Gets the service.
    /// </summary>
    internal LoginService Service { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates a request context.
    /// </summary>
    /// <param name="address">The address of the peer</param>
    /// <param name="headers">The request headers, <c>null</c> for none</param>
    /// <returns>The context and the authentication service that records its sign-ins</returns>
    internal static (DefaultHttpContext Context, RecordingAuthenticationService Authentication) NewContext(string address, IReadOnlyDictionary<string, string>? headers = null)
    {
        var authentication = new RecordingAuthenticationService();
        var context = new DefaultHttpContext
                      {
                          RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication).BuildServiceProvider()
                      };

        context.Connection.RemoteIpAddress = IPAddress.Parse(address);

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            context.Request.Headers[name] = value;
        }

        return (context, authentication);
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Verifier.Dispose();
    }

    #endregion // IDisposable
}