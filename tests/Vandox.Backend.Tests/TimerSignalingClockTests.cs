using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="TimerSignalingClock" />.
/// </summary>
[TestClass]
public sealed class TimerSignalingClockTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the test context.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Tests

    /// <summary>
    /// The wait completes with the timer that reaches the count, and not before; a wait for a count that is reached already completes at once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task TimerSignalingClockWaitCompletesAtCountOfMatchingTimers()
    {
        // Arrange
        var clock = new TimerSignalingClock();
        var due = TimeSpan.FromSeconds(5);
        using var first = clock.CreateTimer(_ =>
                                            {
                                            },
                                            null,
                                            due,
                                            Timeout.InfiniteTimeSpan);

        var wait = clock.WaitForTimersAsync(due, 2, TestContext.CancellationToken);

        // Act
        var beforeSecond = wait.IsCompleted;

        using var second = clock.CreateTimer(_ =>
                                             {
                                             },
                                             null,
                                             due,
                                             Timeout.InfiniteTimeSpan);

        await wait.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        var reached = clock.WaitForTimersAsync(due, 2, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(beforeSecond, "one timer is not two");
        Assert.IsTrue(wait.IsCompletedSuccessfully, "the second timer completes the wait");
        Assert.IsTrue(reached.IsCompletedSuccessfully, "a count that is reached already completes at once");
    }

    /// <summary>
    /// Timers of another due time do not count, and a wait honours its token.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC3")]
    public async Task TimerSignalingClockWaitIgnoresTimersOfOtherDueTime()
    {
        // Arrange
        var clock = new TimerSignalingClock();
        var due = TimeSpan.FromSeconds(5);
        using var other = clock.CreateTimer(_ =>
                                            {
                                            },
                                            null,
                                            TimeSpan.FromSeconds(30),
                                            Timeout.InfiniteTimeSpan);
        using var infinite = clock.CreateTimer(_ =>
                                               {
                                               },
                                               null,
                                               Timeout.InfiniteTimeSpan,
                                               Timeout.InfiniteTimeSpan);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var wait = clock.WaitForTimersAsync(due, 1, cancel.Token);

        // Act
        var beforeMatching = wait.IsCompleted;

        using var matching = clock.CreateTimer(_ =>
                                               {
                                               },
                                               null,
                                               due,
                                               Timeout.InfiniteTimeSpan);

        await wait.WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        var never = clock.WaitForTimersAsync(due, 2, cancel.Token);

        await cancel.CancelAsync();

        // Assert
        Assert.IsFalse(beforeMatching, "timers of other due times do not count");
        Assert.IsTrue(wait.IsCompletedSuccessfully, "the matching timer completes the wait");
        await Assert.ThrowsAsync<OperationCanceledException>(() => never, "a wait that is cancelled");
    }

    #endregion // Tests
}