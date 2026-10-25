using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ConnectionCap"/>
/// </summary>
[TestClass]
public class ConnectionCapTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Connections beyond the cap are aborted without reaching the next delegate, and a slot that a finished connection frees is usable again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapAbortsTheConnectionBeyondTheCapAndReusesFreedSlots()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var cap = new ConnectionCap(2);
        var wrapped = cap.Wrap(_ =>
                               {
                                   Interlocked.Increment(ref entered);

                                   return release.Task;
                               });
        var first = new AbortRecordingConnection();
        var second = new AbortRecordingConnection();
        var third = new AbortRecordingConnection();
        var fourth = new AbortRecordingConnection();

        // Act
        var firstRun = wrapped(first);
        var secondRun = wrapped(second);

        await wrapped(third).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        var enteredWhileFull = Volatile.Read(ref entered);

        release.SetResult();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        await wrapped(fourth).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, enteredWhileFull, "only two connections reach the next delegate");
        Assert.AreEqual(0, first.Aborts, "the first connection is served");
        Assert.AreEqual(0, second.Aborts, "the second connection is served");
        Assert.AreEqual(1, third.Aborts, "the third connection is aborted");
        Assert.AreEqual(0, fourth.Aborts, "a freed slot serves the next connection");
        Assert.AreEqual(3, Volatile.Read(ref entered), "the fourth connection reaches the next delegate");
    }

    /// <summary>
    /// A connection whose handler throws still frees its slot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ConnectionCapFreesTheSlotWhenTheHandlerThrows()
    {
        // Arrange
        var cap = new ConnectionCap(1);
        var wrapped = cap.Wrap(_ => throw new InvalidOperationException("handler failed"));
        var next = cap.Wrap(_ => Task.CompletedTask);
        var failing = new AbortRecordingConnection();
        var later = new AbortRecordingConnection();

        // Act
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => wrapped(failing), "the failure of the handler passes through");
        await next(later).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, failing.Aborts, "the failing connection is not aborted by the cap");
        Assert.AreEqual(0, later.Aborts, "the slot is free again");
    }

    #endregion // Methods
}