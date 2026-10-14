using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Logs;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ActionRun"/>
/// </summary>
[TestClass]
public class ActionRunTests
{
    #region Methods

    /// <summary>
    /// A run that never started has an ended token, and the deadline ends a started run only when it passes.
    /// </summary>
    [TestMethod]
    public void ActionRunTokenEndsAtTheDeadline()
    {
        // Arrange
        var clock = new FakeTimeProvider();
        using var run = new ActionRun(clock);

        var beforeStart = run.Token.IsCancellationRequested;

        // Act
        run.Start();

        var justBefore = run.Token.IsCancellationRequested;

        clock.Advance(LogsView.Deadline - TimeSpan.FromMilliseconds(1));

        var beforeDeadline = run.Token.IsCancellationRequested;

        clock.Advance(TimeSpan.FromMilliseconds(1));

        // Assert
        Assert.IsTrue(beforeStart, "a run that never started has an ended token");
        Assert.IsFalse(justBefore, "the token is open after the start");
        Assert.IsFalse(beforeDeadline, "the token is open before the deadline");
        Assert.IsTrue(run.Token.IsCancellationRequested, "the token ends at the deadline");
        Assert.IsTrue(run.DeadlineExceeded, "the deadline ended the action");
        Assert.IsFalse(run.Superseded, "the action was not superseded");
    }

    /// <summary>
    /// A superseded run has an ended token, and the end is not reported as the deadline.
    /// </summary>
    [TestMethod]
    public void ActionRunCancelSupersedesTheRun()
    {
        // Arrange
        using var run = new ActionRun(new FakeTimeProvider());

        run.Start();

        // Act
        run.Cancel();

        // Assert
        Assert.IsTrue(run.Superseded, "the run is superseded");
        Assert.IsTrue(run.Token.IsCancellationRequested, "the token ends");
        Assert.IsFalse(run.DeadlineExceeded, "a superseded run did not run into the deadline");
    }

    /// <summary>
    /// A run that was superseded while it waited never starts its deadline.
    /// </summary>
    [TestMethod]
    public void ActionRunStartAfterCancelKeepsTheTokenEnded()
    {
        // Arrange
        using var run = new ActionRun(new FakeTimeProvider());

        run.Cancel();

        // Act
        run.Start();

        // Assert
        Assert.IsTrue(run.Superseded, "the run is superseded");
        Assert.IsTrue(run.Token.IsCancellationRequested, "the token stays ended");
    }

    /// <summary>
    /// A disposed run does not start, is not superseded by a late cancel and completes its finish task once; a second dispose changes nothing.
    /// </summary>
    [TestMethod]
    public void ActionRunDisposeIsFinalAndRepeatable()
    {
        // Arrange
        var run = new ActionRun(new FakeTimeProvider());

        // Act
        run.Dispose();
        run.Start();
        run.Cancel();
        run.Dispose();

        // Assert
        Assert.IsTrue(run.Finished.IsCompletedSuccessfully, "the finish task completed");
        Assert.IsFalse(run.Superseded, "a cancel after the dispose does not supersede");
        Assert.IsTrue(run.Token.IsCancellationRequested, "a disposed run does not start");
    }

    #endregion // Methods
}