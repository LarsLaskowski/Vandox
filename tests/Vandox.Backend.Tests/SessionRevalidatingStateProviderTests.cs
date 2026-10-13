using System.Reflection;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="SessionRevalidatingStateProvider"/>
/// </summary>
[TestClass]
public class SessionRevalidatingStateProviderTests
{
    #region Methods

    /// <summary>
    /// The state is valid for an active session and not after sign-out, after expiry or for a user without the claim, and it is checked every 30 seconds.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionRevalidatingStateProviderValidatesSession()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var sessions = new SessionStore(clock);
        var provider = new SessionRevalidatingStateProvider(NullLoggerFactory.Instance, sessions);
        var active = NewTicket();
        var signedOut = NewTicket();
        var withoutClaim = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"));

        await sessions.StoreAsync(active);

        var signedOutKey = await sessions.StoreAsync(signedOut);

        // Act
        var validWhileActive = provider.IsValid(new AuthenticationState(active.Principal));

        await sessions.RemoveAsync(signedOutKey);

        var validAfterSignOut = provider.IsValid(new AuthenticationState(signedOut.Principal));

        clock.Advance(TimeSpan.FromMinutes(61));

        var validAfterExpiry = provider.IsValid(new AuthenticationState(active.Principal));
        var interval = (TimeSpan)typeof(SessionRevalidatingStateProvider).GetProperty("RevalidationInterval", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(provider)!;

        // Assert
        Assert.IsTrue(validWhileActive, "valid for an active session");
        Assert.IsFalse(validAfterSignOut, "invalid after sign-out");
        Assert.IsFalse(validAfterExpiry, "invalid after expiry");
        Assert.IsFalse(provider.IsValid(new AuthenticationState(withoutClaim)), "invalid for a user without the claim");
        Assert.AreEqual(TimeSpan.FromSeconds(30), SessionRevalidatingStateProvider.Interval, "interval constant");
        Assert.AreEqual(TimeSpan.FromSeconds(30), interval, "the state is revalidated every 30 seconds");
    }

    /// <summary>
    /// Creates a ticket with a new session claim.
    /// </summary>
    /// <returns>The ticket</returns>
    private static AuthenticationTicket NewTicket()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin"), new Claim(SessionStore.SessionClaimType, SessionStore.NewSessionId())], "test");

        return new AuthenticationTicket(new ClaimsPrincipal(identity), "test");
    }

    #endregion // Methods
}