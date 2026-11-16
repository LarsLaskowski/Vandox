using Vandox.Backend.Overview;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="OverviewRefresher"/>
/// </summary>
[TestClass]
public class OverviewRefresherTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Gets the start time of the clock.
    /// </summary>
    private static DateTimeOffset Start => new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Start runs one refresh at once, and the timer runs one every 30 seconds after it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task RefresherRefreshesAtStartAndEveryThirtySeconds()
    {
        // Arrange
        var clock = new TimerSignalingClock(Start);
        var faults = new List<Exception>();

        using var probe = new RefreshProbe();
        await using var refresher = new OverviewRefresher(clock, new CircuitConnection(), probe.RunAsync, faults.Add);

        // Act
        refresher.Start();

        var first = await probe.WaitForStartAsync(TestContext.CancellationToken);

        await clock.WaitForTimersAsync(OverviewView.RefreshInterval, 1, TestContext.CancellationToken);

        clock.Advance(OverviewView.RefreshInterval - TimeSpan.FromSeconds(1));

        var beforeInterval = probe.Starts;

        clock.Advance(TimeSpan.FromSeconds(1));

        var second = await probe.WaitForStartAsync(TestContext.CancellationToken);

        clock.Advance(OverviewView.RefreshInterval);

        var third = await probe.WaitForStartAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(first, "Start runs a refresh at once");
        Assert.AreEqual(1, beforeInterval, "no further refresh before 30 seconds have passed");
        Assert.IsTrue(second, "a refresh runs after 30 seconds");
        Assert.IsTrue(third, "a refresh runs after 60 seconds");
        Assert.AreEqual(3, probe.Starts, "one refresh at the start and one per 30 seconds");
        Assert.IsEmpty(faults, "no refresh failed");
    }

    /// <summary>
    /// A refresh that is due while one runs, also one that ignores its cancelled deadline, is skipped; the next one after it ends runs.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task RefresherSkipsDueRefreshWhileOneRuns()
    {
        // Arrange
        var clock = new TimerSignalingClock(Start);
        var faults = new List<Exception>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var probe = new RefreshProbe
                          {
                              Hold = release.Task
                          };
        await using var refresher = new OverviewRefresher(clock, new CircuitConnection(), probe.RunAsync, faults.Add);

        refresher.Start();
        await probe.WaitForStartAsync(TestContext.CancellationToken);
        await clock.WaitForTimersAsync(OverviewView.RefreshInterval, 1, TestContext.CancellationToken);

        // Act
        clock.Advance(OverviewView.RefreshInterval);

        var startsWhileRunning = probe.Starts;

        release.SetResult();
        await probe.WaitForEndAsync(TestContext.CancellationToken);
        clock.Advance(OverviewView.RefreshInterval);

        var next = await probe.WaitForStartAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, startsWhileRunning, "the refresh due while one runs is skipped");
        Assert.IsTrue(next, "the next refresh after the running one ended runs");
        Assert.AreEqual(2, probe.Starts, "the skipped refresh is not made up");
        Assert.AreEqual(1, probe.MaxInFlight, "refreshes never overlap");
    }

    /// <summary>
    /// While the connection is down a due refresh starts no read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task RefresherStartsNoRefreshWhileDisconnected()
    {
        // Arrange
        var clock = new TimerSignalingClock(Start);
        var connection = new CircuitConnection();
        var faults = new List<Exception>();

        using var probe = new RefreshProbe();
        await using var refresher = new OverviewRefresher(clock, connection, probe.RunAsync, faults.Add);

        refresher.Start();
        await probe.WaitForEndAsync(TestContext.CancellationToken);
        await clock.WaitForTimersAsync(OverviewView.RefreshInterval, 1, TestContext.CancellationToken);
        await connection.OnConnectionDownAsync(null!, TestContext.CancellationToken);

        // Act
        clock.Advance(OverviewView.RefreshInterval);

        var afterFirstTick = probe.Starts;

        clock.Advance(OverviewView.RefreshInterval);

        // Assert
        Assert.AreEqual(1, afterFirstTick, "no refresh while the connection is down");
        Assert.AreEqual(1, probe.Starts, "no refresh on any tick while the connection is down");
    }

    /// <summary>
    /// When the connection is restored a refresh runs at once, without waiting for the timer.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task RefresherRefreshesWhenConnectionIsRestored()
    {
        // Arrange
        var clock = new TimerSignalingClock(Start);
        var connection = new CircuitConnection();
        var faults = new List<Exception>();

        using var probe = new RefreshProbe();
        await using var refresher = new OverviewRefresher(clock, connection, probe.RunAsync, faults.Add);

        refresher.Start();
        await probe.WaitForStartAsync(TestContext.CancellationToken);
        await probe.WaitForEndAsync(TestContext.CancellationToken);
        await connection.OnConnectionDownAsync(null!, TestContext.CancellationToken);

        // Act
        await connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);

        var restored = await probe.WaitForStartAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(restored, "a refresh runs when the connection is restored");
        Assert.AreEqual(2, probe.Starts, "the refresh at the start and the one at the restore");
        Assert.IsEmpty(faults, "no refresh failed");
    }

    /// <summary>
    /// A refresh that runs longer than 10 seconds on the clock gets its token cancelled, and the cancellation is no fault.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task RefresherCancelsRefreshAfterTenSeconds()
    {
        // Arrange
        var clock = new TimerSignalingClock(Start);
        var faults = new List<Exception>();

        using var probe = new RefreshProbe
                          {
                              WaitForCancellation = true
                          };
        await using var refresher = new OverviewRefresher(clock, new CircuitConnection(), probe.RunAsync, faults.Add);

        refresher.Start();
        await probe.WaitForStartAsync(TestContext.CancellationToken);
        await clock.WaitForTimersAsync(OverviewView.RefreshDeadline, 1, TestContext.CancellationToken);

        // Act
        clock.Advance(OverviewView.RefreshDeadline - TimeSpan.FromSeconds(1));

        var cancelledEarly = probe.Tokens[0].IsCancellationRequested;

        clock.Advance(TimeSpan.FromSeconds(1));

        var ended = await probe.WaitForEndAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(cancelledEarly, "the refresh is not cancelled before 10 seconds");
        Assert.IsTrue(ended, "the refresh ends after 10 seconds");
        Assert.IsTrue(probe.Tokens[0].IsCancellationRequested, "the token of the refresh is cancelled at the deadline");
        Assert.IsEmpty(faults, "the cancellation at the deadline is no fault");
    }

    /// <summary>
    /// Disposing cancels the running refresh, waits for it to end and stops the timer.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task RefresherCancelsRunningRefreshOnDispose()
    {
        // Arrange
        var clock = new TimerSignalingClock(Start);
        var faults = new List<Exception>();

        using var probe = new RefreshProbe
                          {
                              WaitForCancellation = true
                          };
        var refresher = new OverviewRefresher(clock, new CircuitConnection(), probe.RunAsync, faults.Add);

        refresher.Start();
        await probe.WaitForStartAsync(TestContext.CancellationToken);
        await clock.WaitForTimersAsync(OverviewView.RefreshInterval, 1, TestContext.CancellationToken);

        // Act
        await refresher.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var endsAtDispose = probe.Ends;

        clock.Advance(OverviewView.RefreshInterval);

        // Assert
        Assert.IsTrue(probe.Tokens[0].IsCancellationRequested, "disposing cancels the running refresh");
        Assert.AreEqual(1, endsAtDispose, "disposing waits for the running refresh to end");
        Assert.AreEqual(1, probe.Starts, "no refresh runs after disposing");
        Assert.IsEmpty(faults, "the cancellation at disposing is no fault");
    }

    /// <summary>
    /// After disposing, a restored connection starts no refresh, because the refresher no longer listens to it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task RefresherIgnoresRestoredAfterDispose()
    {
        // Arrange
        var clock = new TimerSignalingClock(Start);
        var connection = new CircuitConnection();
        var faults = new List<Exception>();

        using var probe = new RefreshProbe();
        var refresher = new OverviewRefresher(clock, connection, probe.RunAsync, faults.Add);

        refresher.Start();
        await probe.WaitForEndAsync(TestContext.CancellationToken);
        await refresher.DisposeAsync();

        // Act
        await connection.OnConnectionDownAsync(null!, TestContext.CancellationToken);
        await connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, probe.Starts, "a restore after disposing starts no refresh");
        Assert.IsEmpty(faults, "no refresh failed");
    }

    #endregion // Methods
}