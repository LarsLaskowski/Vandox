using Vandox.Backend.Logs;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="WebReaderGate"/>
/// </summary>
[TestClass]
public class WebReaderGateTests
{
    #region Fields

    private static readonly int[] _results = [1, 1, 1];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// With two slots and three blocked calls two run at once, and the third starts after one ends.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WebReaderGateRunsAtMostTheGivenNumberOfCallsAtOnce()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var inFlight = 0;
        var maxInFlight = 0;

        using var gate = new WebReaderGate(2);

        async Task<int> CallAsync()
        {
            var now = Interlocked.Increment(ref inFlight);

            InterlockedMax(ref maxInFlight, now);

            if (Interlocked.Increment(ref started) == 2)
            {
                twoRunning.TrySetResult();
            }

            await release.Task;

            Interlocked.Decrement(ref inFlight);

            return 1;
        }

        // Act
        var calls = Enumerable.Range(0, 3).Select(_ => gate.RunAsync(CallAsync, TestContext.CancellationToken)).ToArray();

        await twoRunning.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var whileBlocked = Volatile.Read(ref started);

        release.SetResult();

        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2, whileBlocked, "two of the three calls started while they are blocked");
        Assert.AreEqual(3, Volatile.Read(ref started), "the third call ran after a slot was free");
        Assert.AreEqual(2, Volatile.Read(ref maxInFlight), "never more than two calls in flight");
        Assert.AreSequenceEqual(_results, results, "the result of each call reaches its caller");
    }

    /// <summary>
    /// A call that waits for a slot ends with its token and never starts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WebReaderGateWaitEndsWithTheToken()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        using var gate = new WebReaderGate(1);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var first = gate.RunAsync(async () =>
                                  {
                                      Interlocked.Increment(ref started);
                                      firstRunning.TrySetResult();
                                      await release.Task;

                                      return 1;
                                  },
                                  TestContext.CancellationToken);

        await firstRunning.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var second = gate.RunAsync(() =>
                                   {
                                       Interlocked.Increment(ref started);

                                       return Task.FromResult(2);
                                   },
                                   waiting.Token);

        // Act
        await waiting.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => second);

        release.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(exception, "the waiting call ended with an OperationCanceledException");
        Assert.AreEqual(1, Volatile.Read(ref started), "only the first call started");
    }

    /// <summary>
    /// The slot is free again after a call failed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task WebReaderGateReleasesTheSlotAfterAFailure()
    {
        // Arrange
        using var gate = new WebReaderGate(1);

        // Act
        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => gate.RunAsync<int>(() => throw new InvalidOperationException("scripted"), TestContext.CancellationToken));
        var next = await gate.RunAsync(() => Task.FromResult(7), TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("scripted", failure.Message, "the failure reaches the caller");
        Assert.AreEqual(7, next, "the next call ran after the failure");
    }

    /// <summary>
    /// A slot count outside 1 to 3 is refused.
    /// </summary>
    /// <param name="slots">The slot count</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(4)]
    public void WebReaderGateRefusesAnInvalidSlotCount(int slots)
    {
        // Act
        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WebReaderGate(slots));

        // Assert
        Assert.IsNotNull(exception, $"{slots} slots are refused");
    }

    /// <summary>
    /// Raises a value to a new maximum.
    /// </summary>
    /// <param name="target">The value</param>
    /// <param name="candidate">The candidate</param>
    private static void InterlockedMax(ref int target, int candidate)
    {
        int current;

        do
        {
            current = Volatile.Read(ref target);
        }
        while (candidate > current && Interlocked.CompareExchange(ref target, candidate, current) != current);
    }

    #endregion // Methods
}