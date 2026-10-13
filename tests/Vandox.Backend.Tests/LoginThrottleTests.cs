using System.Net;

using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LoginThrottle"/>
/// </summary>
[TestClass]
public class LoginThrottleTests
{
    #region Methods

    /// <summary>
    /// The fifth failure within 15 minutes locks the client, the first four do not, and another client is not affected.
    /// </summary>
    [TestMethod]
    public void LoginThrottleLocksClientAfterFiveFailures()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var throttle = new LoginThrottle(clock);
        var results = new List<LoginFailure>();
        var lockedAfter = new List<bool>();

        // Act
        for (var count = 0; count < LoginThrottle.MaxFailures; count++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            results.Add(throttle.RecordFailure("192.0.2.1"));
            lockedAfter.Add(throttle.IsLockedOut("192.0.2.1"));
        }

        // Assert
        Assert.AreSequenceEqual([1, 2, 3, 4, 5], results.Select(result => result.Failures).ToArray(), "failures counted");
        Assert.AreSequenceEqual([false, false, false, false, true], lockedAfter.ToArray(), "locked after each failure");
        Assert.IsTrue(results.Take(4).All(result => result.ClientLockedUntil is null), "failures 1 to 4 start no lock");
        Assert.AreEqual(clock.GetUtcNow() + LoginThrottle.LockoutDuration, results[4].ClientLockedUntil, "the fifth failure locks for 15 minutes from now");
        Assert.IsFalse(throttle.IsLockedOut("192.0.2.2"), "another client is not locked");
        Assert.IsFalse(throttle.IsLockedOut("never seen"), "an unknown client is not locked");
    }

    /// <summary>
    /// A locked client stays locked until 15 minutes after the fifth failure and is unlocked after that.
    /// </summary>
    [TestMethod]
    public void LoginThrottleUnlocksAfterLockoutDuration()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var throttle = new LoginThrottle(clock);

        for (var count = 0; count < LoginThrottle.MaxFailures; count++)
        {
            throttle.RecordFailure("192.0.2.1");
        }

        // Act
        clock.Advance(LoginThrottle.LockoutDuration - TimeSpan.FromSeconds(1));

        var justBefore = throttle.IsLockedOut("192.0.2.1");

        clock.Advance(TimeSpan.FromSeconds(2));

        var justAfter = throttle.IsLockedOut("192.0.2.1");
        var next = throttle.RecordFailure("192.0.2.1");

        // Assert
        Assert.IsTrue(justBefore, "locked until 15 minutes after the fifth failure");
        Assert.IsFalse(justAfter, "unlocked after that");
        Assert.AreEqual(1, next.Failures, "the old failures no longer count");
        Assert.IsNull(next.ClientLockedUntil, "one new failure locks nobody");
    }

    /// <summary>
    /// Failures older than 15 minutes do not count, and a success clears the count of the client.
    /// </summary>
    [TestMethod]
    public void LoginThrottleForgetsFailuresOutsideWindowAndAfterSuccess()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var throttle = new LoginThrottle(clock);

        // Act
        for (var count = 0; count < 4; count++)
        {
            throttle.RecordFailure("192.0.2.1");
        }

        clock.Advance(TimeSpan.FromMinutes(16));

        LoginFailure last = default;

        for (var count = 0; count < 4; count++)
        {
            last = throttle.RecordFailure("192.0.2.1");
        }

        var lockedAfterWindow = throttle.IsLockedOut("192.0.2.1");

        throttle.RecordSuccess("192.0.2.1");

        LoginFailure afterSuccess = default;

        for (var count = 0; count < 4; count++)
        {
            afterSuccess = throttle.RecordFailure("192.0.2.1");
        }

        // Assert
        Assert.AreEqual(4, last.Failures, "only the failures inside the window count");
        Assert.IsFalse(lockedAfterWindow, "four and four failures 16 minutes apart do not lock");
        Assert.AreEqual(4, afterSuccess.Failures, "a success clears the failures");
        Assert.IsFalse(throttle.IsLockedOut("192.0.2.1"), "four failures after a success do not lock");
    }

    /// <summary>
    /// 100 failures of 100 different clients within an hour lock every client, a new one included, until the oldest failure is an hour old.
    /// </summary>
    [TestMethod]
    public void LoginThrottleLocksEveryoneAfterHundredFailures()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var throttle = new LoginThrottle(clock);
        var results = new List<LoginFailure>();

        // Act
        for (var count = 0; count < LoginThrottle.MaxGlobalFailures; count++)
        {
            results.Add(throttle.RecordFailure($"client-{count}"));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        var lockedNew = throttle.IsLockedOut("a new client");
        var lockedFirst = throttle.IsLockedOut("client-0");

        clock.Advance(TimeSpan.FromMinutes(58));

        var lockedBeforeHour = throttle.IsLockedOut("a new client");

        clock.Advance(TimeSpan.FromMinutes(2));

        var lockedAfterHour = throttle.IsLockedOut("a new client");

        // Assert
        Assert.IsTrue(results.Take(99).All(result => result.AllLockedUntil is null), "99 failures lock nobody");
        Assert.IsNotNull(results[99].AllLockedUntil, "the 100th failure locks everyone");
        Assert.IsTrue(lockedNew, "a client without failures is locked");
        Assert.IsTrue(lockedFirst, "a client with one failure is locked");
        Assert.IsTrue(lockedBeforeHour, "still locked before the oldest failure is an hour old");
        Assert.IsFalse(lockedAfterHour, "unlocked once the oldest failure is an hour old");
    }

    /// <summary>
    /// 4,097 clients keep at most 4,096 tracked, and the one with the oldest last failure is forgotten.
    /// </summary>
    [TestMethod]
    public void LoginThrottleBoundsTrackedClients()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var throttle = new LoginThrottle(clock);

        for (var count = 0; count < 4; count++)
        {
            throttle.RecordFailure("oldest");
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        // Act
        for (var count = 1; count < LoginThrottle.MaxClients; count++)
        {
            throttle.RecordFailure($"client-{count}");
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        var full = throttle.TrackedClients;

        throttle.RecordFailure("one more");
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var afterOverflow = throttle.TrackedClients;
        var second = throttle.RecordFailure("client-1");
        var forgotten = throttle.RecordFailure("oldest");

        // Assert
        Assert.AreEqual(LoginThrottle.MaxClients, full, "the table is full");
        Assert.AreEqual(LoginThrottle.MaxClients, afterOverflow, "a 4,097th client does not grow the table");
        Assert.AreEqual(2, second.Failures, "the client with the second oldest failure is still tracked");
        Assert.AreEqual(1, forgotten.Failures, "the client with the oldest last failure was forgotten");
        Assert.AreEqual(LoginThrottle.MaxClients, throttle.TrackedClients, "the table stays bounded");
    }

    /// <summary>
    /// The client key is the IPv4 address, the IPv4 form of a mapped address, or the /64 network of an IPv6 address.
    /// </summary>
    /// <param name="address">The address, <c>null</c> for none</param>
    /// <param name="expected">The expected key</param>
    [TestMethod]
    [DataRow(null, "unknown")]
    [DataRow("192.0.2.10", "192.0.2.10")]
    [DataRow("::ffff:192.0.2.10", "192.0.2.10")]
    [DataRow("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [DataRow("2001:db8:1:2:ffff::1", "2001:db8:1:2::/64")]
    [DataRow("fe80::1%3", "fe80::/64")]
    public void LoginThrottleClientKeyGroupsIpv6ByNetwork(string? address, string expected)
    {
        // Arrange
        var parsed = address is null ? null : IPAddress.Parse(address);

        // Act
        var key = LoginThrottle.ClientKey(parsed);

        // Assert
        Assert.AreEqual(expected, key, "client key");
    }

    #endregion // Methods
}