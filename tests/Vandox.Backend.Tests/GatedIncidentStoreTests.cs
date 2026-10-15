using Vandox.Backend.Logs;
using Vandox.Backend.Reports;
using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Storage;

using static Vandox.Backend.Tests.ReportSamples;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedIncidentStore"/>
/// </summary>
[TestClass]
public class GatedIncidentStoreTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A read goes through the gate: while the only slot is taken the inner store sees no call, a call that waits ends with its token, and afterwards the inner store gets the query and the token and its answer reaches the caller.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedIncidentStoreReadsThroughTheGate()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new GateProbeIncidentStore();
        var query = new IncidentQuery
                    {
                        From = StorableTime.Min,
                        To = StorableTime.Max,
                        Limit = 1
                    };

        using var gate = new WebReaderGate(1);
        using var cancelling = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var store = new GatedIncidentStore(inner, gate);
        var holder = gate.RunAsync(async () =>
                                   {
                                       await hold.Task;

                                       return 0;
                                   },
                                   TestContext.CancellationToken);
        var incidents = store.IncidentsAsync(query, TestContext.CancellationToken);
        var count = store.CountIncidentsAsync(TestContext.CancellationToken);
        var waiting = store.IncidentsAsync(query, cancelling.Token);

        // Act
        var callsWhileHeld = inner.Calls;

        await cancelling.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);

        hold.SetResult();
        await holder;

        var page = await incidents;
        var total = await count;

        // Assert
        Assert.IsNotNull(exception, "the waiting call ended with its token");
        Assert.AreEqual(0, callsWhileHeld, "the inner store sees no call while the slot is taken");
        Assert.AreEqual(2, inner.Calls, "the two calls that did not end reached the inner store");
        Assert.AreEqual(1, inner.MaxInFlight, "never more than one call in flight");
        Assert.AreSame(query, inner.Queries.Single(), "the query reaches the inner store");
        Assert.AreEqual(TestContext.CancellationToken, inner.Token, "the token reaches the inner store");
        Assert.HasCount(1, page, "the answer to the query reaches the caller");
        Assert.AreEqual(GateProbeIncidentStore.Total, total, "the count reaches the caller");
    }

    /// <summary>
    /// Saving is refused with a <see cref="NotSupportedException"/> and never reaches the inner store.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedIncidentStoreRefusesToSave()
    {
        // Arrange
        var inner = new GateProbeIncidentStore();

        using var gate = new WebReaderGate(1);

        var store = new GatedIncidentStore(inner, gate);

        // Act
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveIncidentAsync(Outage(KillAt, 1, "mariadbd"), KillAt, TestContext.CancellationToken));

        // Assert
        Assert.IsNotNull(exception, "saving is refused");
        Assert.AreEqual(0, inner.Saves, "the inner store was not asked to save");
    }

    #endregion // Methods
}