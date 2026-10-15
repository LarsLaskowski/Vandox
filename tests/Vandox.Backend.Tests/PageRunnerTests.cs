using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="PageRunner"/>
/// </summary>
[TestClass]
public class PageRunnerTests
{
    #region Constants

    private const string DeadlineText = "deadline text";

    private const string FailureText = "failure text";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A failure of the store shows the failure text and is logged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PageRunnerStoreFailureShowsTextAndLogs()
    {
        // Arrange
        var shown = new List<string>();
        var logger = new ListLogger();
        using var runner = new PageRunner(new FakeTimeProvider(), logger, DeadlineText, FailureText, shown.Add);

        // Act
        await runner.RunAsync(_ => throw new StoreException("store: broken"));
        await runner.RunAsync(_ => throw new InvalidOperationException("broken"));

        // Assert
        Assert.AreSequenceEqual([FailureText, FailureText], shown, "the failure text twice");
        Assert.HasCount(2, logger.Messages, "both failures are logged");
    }

    /// <summary>
    /// A refused query shows the rule without the prefix and is not logged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PageRunnerRefusedQueryShowsTheRuleWithoutLogging()
    {
        // Arrange
        var shown = new List<string>();
        var logger = new ListLogger();
        using var runner = new PageRunner(new FakeTimeProvider(), logger, DeadlineText, FailureText, shown.Add);

        // Act
        await runner.RunAsync(_ => throw new InvalidQueryException("store: invalid query: from must be before to"));

        // Assert
        Assert.AreSequenceEqual([LogsView.Refused(new InvalidQueryException("store: invalid query: from must be before to"))], shown, "the text of the refusal");
        Assert.IsEmpty(logger.Messages, "a refusal is not logged");
    }

    /// <summary>
    /// The deadline shows its text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PageRunnerDeadlineShowsTheDeadlineText()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var shown = new List<string>();
        using var runner = new PageRunner(clock, new ListLogger(), DeadlineText, FailureText, shown.Add);

        // Act
        await runner.RunAsync(async token =>
                              {
                                  clock.Advance(LogsView.Deadline);
                                  await Task.Delay(Timeout.Infinite, token);
                              });

        // Assert
        Assert.AreSequenceEqual([DeadlineText], shown, "the deadline text");
    }

    /// <summary>
    /// A runner with its own deadline lets the action run past the deadline of the logs, ends it at the given deadline and shows the deadline text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PageRunnerUsesTheGivenDeadline()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        var deadline = TimeSpan.FromMinutes(2);
        var shown = new List<string>();
        var endedAtTheDeadlineOfTheLogs = true;
        var endedJustBefore = true;
        using var runner = new PageRunner(clock,
                                          _ =>
                                          {
                                          },
                                          deadline,
                                          DeadlineText,
                                          FailureText,
                                          shown.Add);

        // Act
        await runner.RunAsync(async token =>
                              {
                                  clock.Advance(LogsView.Deadline);
                                  endedAtTheDeadlineOfTheLogs = token.IsCancellationRequested;
                                  clock.Advance(deadline - LogsView.Deadline - TimeSpan.FromMilliseconds(1));
                                  endedJustBefore = token.IsCancellationRequested;
                                  clock.Advance(TimeSpan.FromMilliseconds(1));
                                  await Task.Delay(Timeout.Infinite, token);
                              });

        // Assert
        Assert.IsFalse(endedAtTheDeadlineOfTheLogs, "the action is still open at the deadline of the logs");
        Assert.IsFalse(endedJustBefore, "the action is open just before the given deadline");
        Assert.AreSequenceEqual([DeadlineText], shown, "the deadline text at the given deadline");
    }

    /// <summary>
    /// An action that is cancelled by a newer one shows nothing, not even when it ends with a failure of the store, and the newer action runs after it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PageRunnerSupersededActionEndsSilently()
    {
        // Arrange
        var shown = new List<string>();
        var logger = new ListLogger();
        var order = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new PageRunner(new FakeTimeProvider(), logger, DeadlineText, FailureText, shown.Add);

        var first = runner.RunAsync(async _ =>
                                    {
                                        entered.SetResult();
                                        await release.Task;
                                        order.Add("first");

                                        throw new StoreException("store: late failure");
                                    });

        await entered.Task.WaitAsync(TestContext.CancellationToken);

        // Act
        var second = runner.RunAsync(_ =>
                                     {
                                         order.Add("second");

                                         return Task.CompletedTask;
                                     });

        release.SetResult();

        await Task.WhenAll(first, second).WaitAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["first", "second"], order, "the newer action starts after the older one has ended");
        Assert.IsEmpty(shown, "a superseded action shows nothing");
        Assert.IsEmpty(logger.Messages, "a superseded action logs nothing");
    }

    /// <summary>
    /// An action that is superseded while it waits for the running one never runs.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PageRunnerActionSupersededWhileWaitingNeverRuns()
    {
        // Arrange
        var order = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new PageRunner(new FakeTimeProvider(),
                                          new ListLogger(),
                                          DeadlineText,
                                          FailureText,
                                          _ =>
                                          {
                                          });

        var first = runner.RunAsync(async _ =>
                                    {
                                        entered.SetResult();
                                        await release.Task;
                                        order.Add("first");
                                    });

        await entered.Task.WaitAsync(TestContext.CancellationToken);

        // Act
        var second = runner.RunAsync(_ =>
                                     {
                                         order.Add("second");

                                         return Task.CompletedTask;
                                     });
        var third = runner.RunAsync(_ =>
                                    {
                                        order.Add("third");

                                        return Task.CompletedTask;
                                    });

        release.SetResult();

        await Task.WhenAll(first, second, third).WaitAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["first", "third"], order, "the middle action was superseded before it started");
    }

    /// <summary>
    /// Cancelling the current action ends it silently.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task PageRunnerCancelCurrentEndsTheRunningActionSilently()
    {
        // Arrange
        var shown = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runner = new PageRunner(new FakeTimeProvider(), new ListLogger(), DeadlineText, FailureText, shown.Add);

        var running = runner.RunAsync(async token =>
                                      {
                                          entered.SetResult();
                                          await Task.Delay(Timeout.Infinite, token);
                                      });

        await entered.Task.WaitAsync(TestContext.CancellationToken);

        // Act
        runner.CancelCurrent();

        await running.WaitAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(shown, "an action ended by the page shows nothing");
    }

    #endregion // Methods
}