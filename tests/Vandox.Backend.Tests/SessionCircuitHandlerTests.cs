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
    /// Once the handler is disposed it no longer follows state changes: a change to a user whose session ended does not change the user the next event is checked against.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SessionCircuitHandlerStopsFollowingStateWhenDisposed()
    {
        // Arrange
        var sessions = new SessionStore(new FakeTimeProvider());
        var active = NewTicket();
        var ended = NewTicket();

        await sessions.StoreAsync(active);
        await sessions.RemoveAsync(await sessions.StoreAsync(ended));

        var provider = new FixedAuthenticationStateProvider(active.Principal);
        var handler = new SessionCircuitHandler(provider, sessions);
        var calls = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
                                                           {
                                                               calls++;

                                                               return Task.CompletedTask;
                                                           });

        await inbound(null!);

        handler.Dispose();
        provider.ChangeUser(ended.Principal);

        // Act
        await inbound(null!);

        // Assert
        Assert.AreEqual(2, calls, "the event after the disposal was checked against the active user, not the announced one, and reached the next handler");
    }

    /// <summary>
    /// After a change is announced, the next event is checked against the announced state, not the one the first event read: a change to a user whose session ended
    /// ends the circuit, and a change from an anonymous user to an active session lets the next event pass.
    /// </summary>
    /// <param name="firstActive">Whether the first user has an active session; the announced user has the other kind</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SessionCircuitHandlerChecksEventAgainstChangedState(bool firstActive)
    {
        // Arrange
        var sessions = new SessionStore(new FakeTimeProvider());
        var active = NewTicket();
        var other = NewTicket();

        await sessions.StoreAsync(active);
        await sessions.RemoveAsync(await sessions.StoreAsync(other));

        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var first = firstActive ? active.Principal : anonymous;
        var announced = firstActive ? other.Principal : active.Principal;
        var provider = new FixedAuthenticationStateProvider(first);
        using var handler = new SessionCircuitHandler(provider, sessions);
        var calls = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
                                                           {
                                                               calls++;

                                                               return Task.CompletedTask;
                                                           });

        if (firstActive)
        {
            await inbound(null!);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<SessionEndedException>(() => inbound(null!), "the first event of the anonymous user");
        }

        var callsBefore = calls;

        // Act
        provider.ChangeUser(announced);

        var next = inbound(null!);

        // Assert
        if (firstActive)
        {
            await Assert.ThrowsExactlyAsync<SessionEndedException>(() => next, "the event after the change to the ended session");
            Assert.AreEqual(callsBefore, calls, "the event after the change to the ended session did not reach the next handler");
        }
        else
        {
            await next;
            Assert.AreEqual(callsBefore + 1, calls, "the event after the change to the active session reached the next handler");
        }
    }

    /// <summary>
    /// Without an announced change, the first event is checked against the state the provider holds: an active session passes, an ended one ends the circuit.
    /// </summary>
    /// <param name="ended">Whether the session of the provider's user has ended</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SessionCircuitHandlerChecksFirstEventAgainstInitialState(bool ended)
    {
        // Arrange
        var sessions = new SessionStore(new FakeTimeProvider());
        var ticket = NewTicket();
        var key = await sessions.StoreAsync(ticket);

        if (ended)
        {
            await sessions.RemoveAsync(key);
        }

        using var handler = new SessionCircuitHandler(new FixedAuthenticationStateProvider(ticket.Principal), sessions);
        var calls = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
                                                           {
                                                               calls++;

                                                               return Task.CompletedTask;
                                                           });

        // Act
        var first = inbound(null!);

        // Assert
        if (ended)
        {
            await Assert.ThrowsExactlyAsync<SessionEndedException>(() => first, "the first event of the ended session ends the circuit");
            Assert.AreEqual(0, calls, "the first event of the ended session did not reach the next handler");
        }
        else
        {
            await first;
            Assert.AreEqual(1, calls, "the first event of the active session reached the next handler");
        }
    }

    /// <summary>
    /// While the provider has no state yet, its exception passes and no event reaches the next handler, also not a later one: a failed read stores nothing.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SessionCircuitHandlerWithoutKnownStateProcessesNoEvent()
    {
        // Arrange
        using var handler = new SessionCircuitHandler(new UnsetAuthenticationStateProvider(), new SessionStore(new FakeTimeProvider()));
        var calls = 0;
        var inbound = handler.CreateInboundActivityHandler(_ =>
                                                           {
                                                               calls++;

                                                               return Task.CompletedTask;
                                                           });

        // Act
        var first = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => inbound(null!), "the first event without a state");
        var second = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => inbound(null!), "the second event without a state");

        // Assert
        Assert.AreEqual("No authentication state has been set.", first.Message, "the provider's exception of the first event");
        Assert.AreEqual("No authentication state has been set.", second.Message, "the provider's exception of the second event");
        Assert.AreEqual(0, calls, "no event reached the next handler");
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