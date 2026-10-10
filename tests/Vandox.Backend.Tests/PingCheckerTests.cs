using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="PingChecker"/>
/// </summary>
[TestClass]
public class PingCheckerTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A successful ping completes the check, and the next check pings again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PingCheckerPassesWhenPingSucceeds()
    {
        // Arrange
        var count = 0;
        var checker = new PingChecker(_ =>
                                      {
                                          count++;

                                          return Task.CompletedTask;
                                      },
                                      TimeSpan.FromSeconds(5));

        // Act
        await checker.CheckAsync(TestContext.CancellationToken);
        await checker.CheckAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, count, "one ping per check when they do not overlap");
    }

    /// <summary>
    /// A failing ping fails the check with its exception.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PingCheckerFailsWhenPingFails()
    {
        // Arrange
        var checker = new PingChecker(_ => Task.FromException(new InvalidOperationException("down")), TimeSpan.FromSeconds(5));

        // Act and Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => checker.CheckAsync(TestContext.CancellationToken), "failing ping");
    }

    /// <summary>
    /// Checks that arrive while a ping runs share it, and a ping that never answers times out every waiting check without
    /// stacking more pings.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PingCheckerSharesRunningPingAndTimesOut()
    {
        // Arrange
        var count = 0;
        var checker = new PingChecker(async token =>
                                      {
                                          count++;
                                          await Task.Delay(Timeout.Infinite, token);
                                      },
                                      TimeSpan.FromMilliseconds(200));

        // Act
        var first = checker.CheckAsync(TestContext.CancellationToken);
        var second = checker.CheckAsync(TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => first, "first waiter");
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => second, "second waiter");

        // Assert
        Assert.AreEqual(1, count, "the running ping is shared");
    }

    /// <summary>
    /// A cancelled request stops waiting.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PingCheckerStopsWaitingWhenRequestIsCancelled()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        var checker = new PingChecker(token => Task.Delay(Timeout.Infinite, token), TimeSpan.FromSeconds(30));
        var waiting = checker.CheckAsync(cancelled.Token);

        // Act
        await cancelled.CancelAsync();

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting, "cancelled waiter");
    }

    #endregion // Methods
}