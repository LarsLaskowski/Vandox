using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LoginService"/>
/// </summary>
[TestClass]
public partial class LoginServiceTests
{
    #region Constants

    private const string Accepted = LoginServiceFixture.Accepted;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A locked client gets the throttled outcome without a check, also with the right password; another client is checked.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginServiceRefusesLockedClientWithoutVerifying()
    {
        // Arrange
        using var fixture = new LoginServiceFixture();

        for (var count = 0; count < LoginThrottle.MaxFailures; count++)
        {
            fixture.Throttle.RecordFailure("192.0.2.1");
        }

        var locked = LoginServiceFixture.NewContext("192.0.2.1");
        var other = LoginServiceFixture.NewContext("192.0.2.2");

        // Act
        var lockedOutcome = await fixture.Service.SignInAsync(locked.Context, Accepted, TestContext.CancellationToken);
        var callsAfterLocked = fixture.Verifier.Calls;
        var otherOutcome = await fixture.Service.SignInAsync(other.Context, Accepted, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(LoginOutcome.Throttled, lockedOutcome, "the locked client is throttled");
        Assert.AreEqual(0, callsAfterLocked, "the password of a locked client is not checked");
        Assert.IsEmpty(locked.Authentication.SignIns, "the locked client is not signed in");
        Assert.AreEqual(LoginOutcome.SignedIn, otherOutcome, "another client is not affected");
        Assert.AreEqual(1, fixture.Verifier.Calls, "the other client is checked");
    }

    /// <summary>
    /// The right password signs in with a new session claim and clears the failures of the client.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginServiceSignsInWithNewSessionClaim()
    {
        // Arrange
        using var fixture = new LoginServiceFixture();
        var first = LoginServiceFixture.NewContext("192.0.2.1");
        var second = LoginServiceFixture.NewContext("192.0.2.1");

        for (var count = 0; count < 4; count++)
        {
            fixture.Throttle.RecordFailure("192.0.2.1");
        }

        // Act
        var firstOutcome = await fixture.Service.SignInAsync(first.Context, Accepted, TestContext.CancellationToken);
        var secondOutcome = await fixture.Service.SignInAsync(second.Context, Accepted, TestContext.CancellationToken);
        var afterSuccess = fixture.Throttle.RecordFailure("192.0.2.1");

        // Assert
        Assert.AreEqual(LoginOutcome.SignedIn, firstOutcome, "first outcome");
        Assert.AreEqual(LoginOutcome.SignedIn, secondOutcome, "second outcome");
        Assert.HasCount(1, first.Authentication.SignIns, "one sign-in");

        var (scheme, principal, properties) = first.Authentication.SignIns[0];

        var session = principal.FindFirst(SessionStore.SessionClaimType)?.Value;

        Assert.IsTrue(scheme is null or "Cookies", "the cookie scheme");
        Assert.IsNotNull(session, "the session claim");
        Assert.IsTrue(SessionId().IsMatch(session), $"43 base64url characters: {session}");
        Assert.AreNotEqual(session, second.Authentication.SignIns[0].Principal.FindFirst(SessionStore.SessionClaimType)?.Value, "every sign-in has its own session");
        Assert.AreEqual("admin", principal.Identity?.Name, "the user name");
        Assert.IsFalse(properties?.IsPersistent ?? false, "the cookie is not persistent");
        Assert.AreEqual(1, afterSuccess.Failures, "the successes cleared the failures of the client");
        Assert.Contains("login succeeded 192.0.2.1", fixture.Logger.Messages, "the sign-in is logged");
    }

    /// <summary>
    /// A wrong password, a missing one and one of 1,025 characters are failures, the last two without a check, and the fifth failure locks the client.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginServiceCountsWrongAndOverlongPasswords()
    {
        // Arrange
        var overlong = new string('p', PasswordHash.MaxPasswordLength + 1);
        using var fixture = new LoginServiceFixture(overlong);
        var outcomes = new List<LoginOutcome>();
        var lockedAfter = new List<bool>();

        // Act
        foreach (var password in new[] { "a wrong password", overlong, null, "another wrong one", "and a last wrong one" })
        {
            outcomes.Add(await fixture.Service.SignInAsync(LoginServiceFixture.NewContext("192.0.2.1").Context, password, TestContext.CancellationToken));
            lockedAfter.Add(fixture.Throttle.IsLockedOut("192.0.2.1"));
        }

        // Assert
        Assert.IsTrue(outcomes.All(outcome => outcome == LoginOutcome.WrongPassword), "every attempt is a wrong password");
        Assert.AreSequenceEqual([false, false, false, false, true], lockedAfter.ToArray(), "locked after each attempt");
        Assert.AreEqual(3, fixture.Verifier.Calls, "the overlong and the missing password are not checked");
        Assert.AreEqual(5, fixture.Logger.Messages.Count(message => message.StartsWith("login failed ", StringComparison.Ordinal)), "five failures are logged");
    }

    /// <summary>
    /// Two attempts never run the check at the same time, and an attempt that waits more than 5 seconds is throttled without a recorded failure.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginServiceVerifiesOneAttemptAtATime()
    {
        // Arrange
        using var fixture = new LoginServiceFixture();
        var running = LoginServiceFixture.NewContext("192.0.2.1");
        var waiting = LoginServiceFixture.NewContext("192.0.2.2");

        fixture.Verifier.BlockUntilReleased();

        var first = Task.Run(() => fixture.Service.SignInAsync(running.Context, Accepted, TestContext.CancellationToken), TestContext.CancellationToken);

        await fixture.Verifier.WaitUntilEnteredAsync(TestContext.CancellationToken);

        var second = Task.Run(() => fixture.Service.SignInAsync(waiting.Context, "a wrong password", TestContext.CancellationToken), TestContext.CancellationToken);

        // Act
        await AdvanceUntilCompletedAsync(fixture.Clock, second);

        var secondOutcome = await second;

        fixture.Verifier.Release();

        var firstOutcome = await first;

        // Assert
        Assert.AreEqual(LoginOutcome.Throttled, secondOutcome, "the attempt that waited more than 5 seconds");
        Assert.AreEqual(LoginOutcome.SignedIn, firstOutcome, "the running check ends normally");
        Assert.AreEqual(1, fixture.Verifier.Calls, "the waiting attempt never ran the check");
        Assert.AreEqual(1, fixture.Verifier.MaxConcurrent, "never two checks at the same time");
        Assert.AreEqual(1, fixture.Throttle.RecordFailure("192.0.2.2").Failures, "the throttled attempt recorded no failure");
    }

    /// <summary>
    /// After a waiter timed out and another one was cancelled, the gate is free again for the next attempts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LoginServiceGateStaysUsableAfterTimedOutAndCancelledWaits()
    {
        // Arrange
        using var fixture = new LoginServiceFixture();
        var running = LoginServiceFixture.NewContext("192.0.2.1");
        var timedOut = LoginServiceFixture.NewContext("192.0.2.2");
        var cancelled = LoginServiceFixture.NewContext("192.0.2.3");

        using var cancel = new CancellationTokenSource();

        fixture.Verifier.BlockUntilReleased();

        var first = Task.Run(() => fixture.Service.SignInAsync(running.Context, Accepted, TestContext.CancellationToken), TestContext.CancellationToken);

        await fixture.Verifier.WaitUntilEnteredAsync(TestContext.CancellationToken);

        var timedOutTask = Task.Run(() => fixture.Service.SignInAsync(timedOut.Context, "a wrong password", TestContext.CancellationToken), TestContext.CancellationToken);
        var cancelledTask = Task.Run(() => fixture.Service.SignInAsync(cancelled.Context, "a wrong password", cancel.Token), TestContext.CancellationToken);

        // Act
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelledTask, "cancelled waiter");
        await AdvanceUntilCompletedAsync(fixture.Clock, timedOutTask);

        var timedOutOutcome = await timedOutTask;

        fixture.Verifier.Release();
        await first;

        var callsBefore = fixture.Verifier.Calls;
        var next = await Task.Run(() => fixture.Service.SignInAsync(LoginServiceFixture.NewContext("192.0.2.4").Context, Accepted, TestContext.CancellationToken), TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        var callsAfterNext = fixture.Verifier.Calls;
        var last = await Task.Run(() => fixture.Service.SignInAsync(LoginServiceFixture.NewContext("192.0.2.5").Context, Accepted, TestContext.CancellationToken), TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(LoginOutcome.Throttled, timedOutOutcome, "the waiter that timed out");
        Assert.AreEqual(callsBefore + 1, callsAfterNext, "the next attempt ran the check");
        Assert.AreEqual(callsBefore + 2, fixture.Verifier.Calls, "the attempt after it ran the check as well");
        Assert.AreNotEqual(LoginOutcome.Throttled, next, "the next attempt is not throttled");
        Assert.AreNotEqual(LoginOutcome.Throttled, last, "the attempt after it is not throttled");
        Assert.AreEqual(1, fixture.Throttle.RecordFailure("192.0.2.2").Failures, "the waiter that timed out recorded no failure");
        Assert.AreEqual(1, fixture.Throttle.RecordFailure("192.0.2.3").Failures, "the cancelled waiter recorded no failure");
    }

    /// <summary>
    /// When a failure locks the client and the forwarded address was not applied, the hint is logged once with the lock, and in no other case.
    /// </summary>
    /// <param name="forwardedFor">Whether the requests carry <c>X-Forwarded-For</c></param>
    /// <param name="originalFor">Whether the requests carry <c>X-Original-For</c></param>
    /// <param name="failures">The number of wrong passwords</param>
    /// <param name="hints">The expected number of hints</param>
    /// <param name="locks">The expected number of lock lines</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(true, false, 5, 1, 1)]
    [DataRow(true, true, 5, 0, 1)]
    [DataRow(false, false, 5, 0, 1)]
    [DataRow(true, false, 4, 0, 0)]
    public async Task LoginServiceHintsAtIgnoredForwardedForWhenLocking(bool forwardedFor, bool originalFor, int failures, int hints, int locks)
    {
        // Arrange
        using var fixture = new LoginServiceFixture();
        var headers = new Dictionary<string, string>();

        if (forwardedFor)
        {
            headers["X-Forwarded-For"] = "198.51.100.7";
        }

        if (originalFor)
        {
            headers["X-Original-For"] = "10.0.0.1";
        }

        // Act
        for (var count = 0; count < failures; count++)
        {
            await fixture.Service.SignInAsync(LoginServiceFixture.NewContext("192.0.2.1", headers).Context, "a wrong password", TestContext.CancellationToken);
        }

        // Assert
        Assert.AreEqual(hints, fixture.Logger.Messages.Count(message => message.StartsWith("forwarded header ignored", StringComparison.Ordinal)), "hints");
        Assert.AreEqual(locks, fixture.Logger.Messages.Count(message => IsClientLock(message)), "lock lines");
    }

    /// <summary>
    /// Tells whether a log message announces the lock of one client.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns><c>true</c> for <c>login locked</c> and <c>false</c> for any other message, <c>login locked for all clients</c> included</returns>
    private static bool IsClientLock(string message)
    {
        if (message.StartsWith("login locked for all", StringComparison.Ordinal))
        {
            return false;
        }

        return message.StartsWith("login locked ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Advances the clock by the verification wait until the task has completed.
    /// </summary>
    /// <param name="clock">The clock</param>
    /// <param name="task">The task</param>
    /// <returns>A task that completes when the task has completed</returns>
    private async Task AdvanceUntilCompletedAsync(FakeTimeProvider clock, Task task)
    {
        var rounds = 0;

        while (task.Status < TaskStatus.RanToCompletion)
        {
            clock.Advance(LoginService.VerificationWait);
            await Task.Yield();
            rounds++;

            if (rounds % 50 == 0)
            {
                await Task.Delay(1, TestContext.CancellationToken);
            }
        }
    }

    #endregion // Methods

    #region Patterns

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex SessionId();

    #endregion // Patterns
}