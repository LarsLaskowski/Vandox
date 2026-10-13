using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="SessionStore"/>
/// </summary>
[TestClass]
public class SessionStoreTests
{
    #region Methods

    /// <summary>
    /// A stored ticket is retrieved by the session claim, and a session without activity for an hour is gone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionStoreExpiresIdleSessions()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var store = new SessionStore(clock);
        var ticket = NewTicket(out var id);

        // Act
        var key = await store.StoreAsync(ticket);

        clock.Advance(TimeSpan.FromMinutes(59));

        var retrieved = await store.RetrieveAsync(key);

        clock.Advance(TimeSpan.FromMinutes(59));

        var extended = await store.RetrieveAsync(key);

        clock.Advance(TimeSpan.FromMinutes(61));

        var expired = await store.RetrieveAsync(key);
        var unknown = await store.RetrieveAsync("unknown-key");

        // Assert
        Assert.AreEqual(id, key, "the key is the session claim");
        Assert.IsNotNull(retrieved, "retrieved by the key");
        Assert.AreEqual(id, retrieved.Principal.FindFirstValue(SessionStore.SessionClaimType), "the ticket of the session");
        Assert.IsNotNull(extended, "activity extends the idle time");
        Assert.IsNull(expired, "no activity for 61 minutes ends the session");
        Assert.IsNull(unknown, "an unknown key");
        Assert.AreEqual(0, store.Count, "an ended session is forgotten");
    }

    /// <summary>
    /// Activity every 50 minutes keeps a session until 12 hours after it was stored, not longer.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionStoreEndsSessionAfterAbsoluteLifetime()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var store = new SessionStore(clock);
        var key = await store.StoreAsync(NewTicket(out _));
        var active = new List<bool>();

        // Act
        for (var count = 0; count < 14; count++)
        {
            clock.Advance(TimeSpan.FromMinutes(50));
            active.Add(await store.RetrieveAsync(key) is not null);
        }

        clock.Advance(TimeSpan.FromMinutes(30));

        var afterLifetime = await store.RetrieveAsync(key);

        // Assert
        Assert.IsTrue(active.All(value => value), "still valid at 11 hours 40 minutes with activity every 50 minutes");
        Assert.IsNull(afterLifetime, "ended at 12 hours 10 minutes despite activity");
    }

    /// <summary>
    /// Removing a session ends it at once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionStoreRemoveEndsSession()
    {
        // Arrange
        var store = new SessionStore(new FakeTimeProvider());
        var ticket = NewTicket(out _);
        var key = await store.StoreAsync(ticket);

        // Act
        await store.RemoveAsync(key);

        var retrieved = await store.RetrieveAsync(key);

        // Assert
        Assert.IsNull(retrieved, "the session is gone");
        Assert.IsFalse(store.IsActive(ticket.Principal), "the user has no active session");
        Assert.IsFalse(store.Touch(ticket.Principal), "the user cannot be touched");
        Assert.AreEqual(0, store.Count, "no session left");
    }

    /// <summary>
    /// A 17th session removes the oldest one.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionStoreBoundsSessions()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var store = new SessionStore(clock);
        var keys = new List<string>();

        // Act
        for (var count = 0; count < SessionStore.MaxSessions + 1; count++)
        {
            keys.Add(await store.StoreAsync(NewTicket(out _)));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // Assert
        Assert.AreEqual(SessionStore.MaxSessions, store.Count, "at most 16 sessions");
        Assert.IsNull(await store.RetrieveAsync(keys[0]), "the oldest session was removed");
        Assert.IsNotNull(await store.RetrieveAsync(keys[1]), "the second oldest session stays");
        Assert.IsNotNull(await store.RetrieveAsync(keys[^1]), "the newest session stays");
    }

    /// <summary>
    /// A ticket without the session claim cannot be stored.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionStoreRefusesTicketWithoutSessionClaim()
    {
        // Arrange
        var store = new SessionStore(new FakeTimeProvider());
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test")), "test");

        // Act
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.StoreAsync(ticket), "ticket without claim");

        // Assert
        Assert.AreEqual(0, store.Count, "nothing was stored");
    }

    /// <summary>
    /// Touching a session extends its idle time, asking whether it is active does not, and both are false for a user without the claim.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SessionStoreIsActiveDoesNotExtendSession()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var store = new SessionStore(clock);
        var asked = NewTicket(out _);
        var touched = NewTicket(out _);

        await store.StoreAsync(asked);
        await store.StoreAsync(touched);

        // Act
        clock.Advance(TimeSpan.FromMinutes(40));

        var askedActive = store.IsActive(asked.Principal);
        var touchedActive = store.Touch(touched.Principal);

        clock.Advance(TimeSpan.FromMinutes(30));

        var askedLater = store.IsActive(asked.Principal);
        var touchedLater = store.IsActive(touched.Principal);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        // Assert
        Assert.IsTrue(askedActive, "active after 40 minutes");
        Assert.IsTrue(touchedActive, "touched after 40 minutes");
        Assert.IsFalse(askedLater, "IsActive did not extend: idle for 70 minutes");
        Assert.IsTrue(touchedLater, "Touch extended: idle for 30 minutes");
        Assert.IsFalse(store.IsActive(anonymous), "an anonymous user is not active");
        Assert.IsFalse(store.Touch(anonymous), "an anonymous user cannot be touched");
    }

    /// <summary>
    /// Creates a ticket with a new session claim.
    /// </summary>
    /// <param name="id">The session identifier</param>
    /// <returns>The ticket</returns>
    private static AuthenticationTicket NewTicket(out string id)
    {
        id = SessionStore.NewSessionId();

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin"), new Claim(SessionStore.SessionClaimType, id)], "test");

        return new AuthenticationTicket(new ClaimsPrincipal(identity), "test");
    }

    #endregion // Methods
}