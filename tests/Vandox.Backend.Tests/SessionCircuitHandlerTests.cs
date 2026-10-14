using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="SessionCircuitHandler"/>
/// </summary>
[TestClass]
public class SessionCircuitHandlerTests
{
    #region Methods

    /// <summary>
    /// The activity of an active session reaches the next handler once and extends the idle time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionCircuitHandlerPassesActivityOfActiveSession()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var sessions = new SessionStore(clock);
        var ticket = NewTicket();

        await sessions.StoreAsync(ticket);

        var handler = new SessionCircuitHandler(new FixedAuthenticationStateProvider(ticket.Principal), sessions);
        var calls = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
                                                           {
                                                               calls++;

                                                               return Task.CompletedTask;
                                                           });

        // Act
        clock.Advance(TimeSpan.FromMinutes(40));
        await inbound(null!);
        clock.Advance(TimeSpan.FromMinutes(40));

        // Assert
        Assert.AreEqual(1, calls, "the next handler ran once");
        Assert.IsTrue(sessions.IsActive(ticket.Principal), "the activity extended the idle time: 80 minutes since the sign-in, 40 since the activity");
    }

    /// <summary>
    /// After the session ended, and for an anonymous user, the activity throws and does not reach the next handler.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionCircuitHandlerEndsCircuitAfterSessionEnded()
    {
        // Arrange
        var sessions = new SessionStore(new FakeTimeProvider());
        var ticket = NewTicket();
        var key = await sessions.StoreAsync(ticket);
        var calls = 0;
        Task Next(Microsoft.AspNetCore.Components.Server.Circuits.CircuitInboundActivityContext context)
        {
            calls++;

            return Task.CompletedTask;
        }

        var ended = new SessionCircuitHandler(new FixedAuthenticationStateProvider(ticket.Principal), sessions).CreateInboundActivityHandler(Next);
        var anonymous = new SessionCircuitHandler(new FixedAuthenticationStateProvider(new ClaimsPrincipal(new ClaimsIdentity())), sessions).CreateInboundActivityHandler(Next);

        await sessions.RemoveAsync(key);

        // Act
        await Assert.ThrowsExactlyAsync<SessionEndedException>(() => ended(null!), "ended session");
        await Assert.ThrowsExactlyAsync<SessionEndedException>(() => anonymous(null!), "anonymous user");

        // Assert
        Assert.AreEqual(0, calls, "the next handler did not run");
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