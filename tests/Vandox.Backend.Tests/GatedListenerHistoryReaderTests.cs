using Vandox.Backend.Logs;
using Vandox.Backend.Reports;
using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedListenerHistoryReader"/>
/// </summary>
[TestClass]
public class GatedListenerHistoryReaderTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Both reads wait for the only slot of the gate, then reach the inner reader with their query and token, and its answers reach the caller.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedListenerHistoryReaderReadsThroughTheGate()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedListenerHistoryReader();
        var query = new ListenerFindingQuery
                    {
                        Kind = ListenerFindingKind.AgentStart,
                        Limit = 7
                    };

        using var gate = new WebReaderGate(1);

        var reader = new GatedListenerHistoryReader(inner, gate);
        var holder = gate.RunAsync(async () =>
                                   {
                                       await hold.Task;

                                       return 0;
                                   },
                                   TestContext.CancellationToken);
        var tallies = reader.ListenerTalliesAsync(TestContext.CancellationToken);
        var findings = reader.ListenerFindingsAsync(query, TestContext.CancellationToken);

        // Act
        var callsWhileHeld = inner.TallyCalls + inner.FindingQueries.Count;

        hold.SetResult();
        await holder;

        var tallyPage = await tallies;
        var findingPage = await findings;

        // Assert
        Assert.AreEqual(0, callsWhileHeld, "the inner reader sees no call while the slot is taken");
        Assert.AreEqual(1, inner.TallyCalls, "the read of the tallies reached the inner reader");
        Assert.AreEqual(TestContext.CancellationToken, inner.TallyTokens.Single(), "the token reaches the inner reader for the tallies");
        Assert.HasCount(1, inner.FindingQueries, "the read of the findings reached the inner reader");
        Assert.AreEqual(ListenerFindingKind.AgentStart, inner.FindingQueries[0].Kind, "the kind of the query reaches the inner reader");
        Assert.AreEqual(7, inner.FindingQueries[0].Limit, "the limit of the query reaches the inner reader");
        Assert.AreEqual(TestContext.CancellationToken, inner.FindingTokens.Single(), "the token reaches the inner reader for the findings");
        Assert.AreSame(inner.Tallies, tallyPage, "the tallies reach the caller");
        Assert.AreSame(inner.StartFindings, findingPage, "the findings reach the caller");
    }

    /// <summary>
    /// A failing read reaches the caller and releases its slot, so the next read of the only slot is answered.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedListenerHistoryReaderReleasesTheSlotOnFailure()
    {
        // Arrange
        var inner = new ScriptedListenerHistoryReader
                    {
                        Failure = new InvalidOperationException("scripted")
                    };

        using var gate = new WebReaderGate(1);

        var reader = new GatedListenerHistoryReader(inner, gate);

        // Act
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ListenerTalliesAsync(TestContext.CancellationToken));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ListenerFindingsAsync(new ListenerFindingQuery(), TestContext.CancellationToken));

        // Assert
        Assert.AreEqual("scripted", first.Message, "the failure of the first read reaches the caller");
        Assert.AreEqual("scripted", second.Message, "the second read got the slot and reached the inner reader");
        Assert.AreEqual(1, inner.TallyCalls, "the first read reached the inner reader");
        Assert.HasCount(1, inner.FindingQueries, "the second read reached the inner reader");
    }

    /// <summary>
    /// A missing inner reader or gate is refused.
    /// </summary>
    [TestMethod]
    public void GatedListenerHistoryReaderRefusesMissingArguments()
    {
        // Arrange
        using var gate = new WebReaderGate(1);

        // Act
        var noInner = Assert.Throws<ArgumentNullException>(() => new GatedListenerHistoryReader(null!, gate));
        var noGate = Assert.Throws<ArgumentNullException>(() => new GatedListenerHistoryReader(new ScriptedListenerHistoryReader(), null!));

        // Assert
        Assert.AreEqual("inner", noInner.ParamName, "the inner reader is required");
        Assert.AreEqual("gate", noGate.ParamName, "the gate is required");
    }

    #endregion // Methods
}