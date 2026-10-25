using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestWriteGate"/>
/// </summary>
[TestClass]
public class IngestWriteGateTests
{
    #region Constants

    private const int Entered = 0;

    private const int AfterClose = 1;

    private const int Late = 2;

    private const int Closed = 3;

    private const int Stop = 4;

    private const int StateLength = 5;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Once the gate is closed no write enters, and the close completes when the writes in flight have left.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestWriteGateClosesAtomically()
    {
        // Arrange
        var gate = new IngestWriteGate();

        Assert.IsTrue(gate.TryEnter(), "the first write enters");
        Assert.IsTrue(gate.TryEnter(), "the second write enters");

        // Act
        var closing = gate.CloseAsync(TestContext.CancellationToken);
        var enteredAfterClose = gate.TryEnter();

        gate.Exit();

        var doneAfterFirst = closing.IsCompleted;

        gate.Exit();

        await closing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(enteredAfterClose, "no write enters once the close began");
        Assert.IsFalse(doneAfterFirst, "the close waits for every write in flight");
        Assert.IsFalse(gate.TryEnter(), "no write enters after the close");
    }

    /// <summary>
    /// A gate without writes in flight closes at once, also by the synchronous close of the stop signal, and stays closed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestWriteGateCloseWithoutWritesCompletesAtOnce()
    {
        // Arrange
        var gate = new IngestWriteGate();

        // Act
        gate.Close();

        var enteredAfterClose = gate.TryEnter();

        using var cancelled = new CancellationTokenSource();

        await cancelled.CancelAsync();

        var closing = gate.CloseAsync(cancelled.Token);

        await closing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(enteredAfterClose, "no write enters after the close");
        Assert.IsTrue(closing.IsCompletedSuccessfully, "without writes in flight the close completes normally, whatever its token");
        Assert.IsFalse(gate.TryEnter(), "the gate stays closed");
    }

    /// <summary>
    /// The close of a gate with a write in flight throws OperationCanceledException when its token is cancelled while the write remains, so the drain is bounded by the deadline.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestWriteGateCloseEndsWhenItsTokenIsCancelled()
    {
        // Arrange
        var gate = new IngestWriteGate();
        using var deadline = new CancellationTokenSource();

        Assert.IsTrue(gate.TryEnter(), "the write enters");

        var closing = gate.CloseAsync(deadline.Token);

        // Act
        await deadline.CancelAsync();

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(() => closing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken));

        // Assert
        Assert.IsNotNull(failure, "the close reports the cancellation while a write remains");
        Assert.IsFalse(gate.TryEnter(), "the gate stays closed");
    }

    /// <summary>
    /// Under concurrent entries no write enters after the close returned.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IngestWriteGateAdmitsNoWriteAfterTheCloseReturned()
    {
        // Arrange
        const int workers = 4;

        var gate = new IngestWriteGate();
        var state = new int[StateLength];
        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() => Spin(gate, state), TestContext.CancellationToken)).ToArray();

        while (Volatile.Read(ref state[Entered]) < 1000)
        {
            await FailWhenEndedAsync(tasks);
        }

        // Act
        gate.Close();
        Volatile.Write(ref state[Closed], 1);

        while (Volatile.Read(ref state[AfterClose]) < 1000)
        {
            await FailWhenEndedAsync(tasks);
        }

        Volatile.Write(ref state[Stop], 1);
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken);
        await gate.CloseAsync(TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);

        var late = Volatile.Read(ref state[Late]);

        // Assert
        Assert.AreEqual(0, late, "no write entered after the close returned");
    }

    /// <summary>
    /// Enters and leaves the gate in a loop until told to stop, and counts the entries that came after the test saw the gate closed.
    /// </summary>
    /// <param name="gate">The gate</param>
    /// <param name="state">The counters and flags shared with the test</param>
    internal static void Spin(IngestWriteGate gate, int[] state)
    {
        while (Volatile.Read(ref state[Stop]) == 0)
        {
            var wasClosed = Volatile.Read(ref state[Closed]);
            var admitted = gate.TryEnter();

            Interlocked.Add(ref state[AfterClose], wasClosed);

            if (admitted)
            {
                Interlocked.Increment(ref state[Entered]);
                Interlocked.Add(ref state[Late], wasClosed);
                gate.Exit();
            }

            Thread.Yield();
        }
    }

    /// <summary>
    /// Yields, and surfaces the failure of a worker that ended although it was not told to stop.
    /// </summary>
    /// <param name="workers">The workers</param>
    /// <returns>A task that completes after the yield</returns>
    private static async Task FailWhenEndedAsync(Task[] workers)
    {
        if (Array.Exists(workers, worker => worker.IsCompleted))
        {
            await Task.WhenAll(workers);

            throw new InvalidOperationException("a worker ended before it was told to stop");
        }

        await Task.Yield();
    }

    #endregion // Methods
}