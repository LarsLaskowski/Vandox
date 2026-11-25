using Vandox.Backend.Logs;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedServiceHistoryReader"/>
/// </summary>
[TestClass]
public class GatedServiceHistoryReaderTests
{
    #region Fields

    private static readonly ServiceAvailabilityQuery _query = new()
                                                              {
                                                                  From = HistorySamples.Base,
                                                                  To = HistorySamples.Base.AddDays(1)
                                                              };

    private static readonly ServiceAvailabilityResult _result = ServicesSamples.Availability(ServicesSamples.Unit(ServicesSamples.Nginx, TimeSpan.FromDays(1), TimeSpan.Zero, TimeSpan.Zero));

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A call takes one slot of the gate while it runs, so a second call waits; both reach the inner reader with their query and token, return its answer and free the slot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task GatedServiceHistoryReaderTakesOneSlotPerCall()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var inner = new FakeServiceHistoryReader
                          {
                              Hold = release.Task,
                              Result = _result
                          };
        using var gate = new WebReaderGate(1);
        using var secondSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var reader = new GatedServiceHistoryReader(inner, gate);
        var first = reader.ServiceAvailabilityAsync(_query, TestContext.CancellationToken);
        var entry = inner.WaitForEntryAsync(TestContext.CancellationToken);

        await Task.WhenAny(entry, first);

        var entered = entry.IsCompletedSuccessfully && entry.Result;
        var second = reader.ServiceAvailabilityAsync(_query, secondSource.Token);
        var whileHeld = inner.Calls.Count;

        // Act
        release.SetResult();

        var firstAnswer = await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        var secondAnswer = await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        var free = await gate.RunAsync(() => Task.FromResult(true), TestContext.CancellationToken);

        // Assert
        var calls = inner.Calls;

        Assert.IsTrue(entered, "the first call reached the inner reader");
        Assert.AreEqual(1, whileHeld, "the inner reader sees one call while the slot is taken");
        Assert.AreEqual(1, inner.MaxInFlight, "one call in flight at once");
        Assert.HasCount(2, calls, "both calls reached the inner reader");
        Assert.IsTrue(calls.All(call => ReferenceEquals(call.Argument, _query)), "the query reaches the inner reader");
        Assert.AreEqual(TestContext.CancellationToken, calls[0].Token, "the token of the first call reaches the inner reader");
        Assert.AreEqual(secondSource.Token, calls[1].Token, "the token of the second call reaches the inner reader");
        Assert.AreSame(_result, firstAnswer, "the answer of the first call");
        Assert.AreSame(_result, secondAnswer, "the answer of the second call");
        Assert.IsTrue(free, "the slot is free after both calls ended");
    }

    /// <summary>
    /// A call that fails frees its slot and passes the failure on.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task GatedServiceHistoryReaderFreesTheSlotAfterAFailure()
    {
        // Arrange
        using var inner = new FakeServiceHistoryReader
                          {
                              Failure = new StoreException("the store failed")
                          };
        using var gate = new WebReaderGate(1);

        var reader = new GatedServiceHistoryReader(inner, gate);

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<StoreException>(() => reader.ServiceAvailabilityAsync(_query, TestContext.CancellationToken), "the failure reaches the caller");
        var free = await gate.RunAsync(() => Task.FromResult(true), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("the store failed", thrown.Message, "the failure itself");
        Assert.IsTrue(free, "the slot is free after the failure");
    }

    #endregion // Methods
}