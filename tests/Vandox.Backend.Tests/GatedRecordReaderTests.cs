using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedRecordReader"/>
/// </summary>
[TestClass]
public class GatedRecordReaderTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A read goes through the gate: with one slot the inner reader sees one query at a time, gets the query and the token, and its answer reaches the caller.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedRecordReaderRunsThroughTheGate()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var at = new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var inner = new ScriptedRecordReader
                    {
                        Before = (_, token) => release.Task.WaitAsync(token)
                    };

        inner.AddMetric("proc.meminfo", at, "mem_available_bytes", 5);

        using var gate = new WebReaderGate(1);

        var reader = new GatedRecordReader(inner, gate);
        var query = new RecordQuery
                    {
                        Kind = RecordKind.Metric,
                        Source = "proc.meminfo",
                        Name = "mem_available_bytes",
                        From = at,
                        To = at.AddHours(1),
                        Limit = 10
                    };

        // Act
        var first = reader.RecordsAsync(query, TestContext.CancellationToken);
        var second = reader.RecordsAsync(query, TestContext.CancellationToken);

        await inner.WaitForCallsAsync(1);

        var whileBlocked = inner.Queries.Count;

        release.SetResult();

        var firstPage = await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1, whileBlocked, "the inner reader sees one of the two calls while the slot is taken");
        Assert.AreEqual(1, inner.MaxInFlight, "never more than one call in flight");
        Assert.HasCount(2, inner.Queries, "the second call ran after the slot was free");
        Assert.AreEqual("proc.meminfo", inner.Queries[0].Source, "the source reaches the inner reader");
        Assert.AreEqual("mem_available_bytes", inner.Queries[0].Name, "the name reaches the inner reader");
        Assert.HasCount(1, firstPage, "the answer of the inner reader reaches the caller");
    }

    /// <summary>
    /// A call that waits for the slot ends with its token and never reaches the inner reader.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task GatedRecordReaderWaitEndsWithTheToken()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedRecordReader
                    {
                        Before = (_, token) => release.Task.WaitAsync(token)
                    };

        using var gate = new WebReaderGate(1);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var reader = new GatedRecordReader(inner, gate);
        var query = new RecordQuery
                    {
                        Kind = RecordKind.ProcessSnapshot,
                        From = StorableTime.Min,
                        To = StorableTime.Max,
                        Limit = 1
                    };
        var first = reader.RecordsAsync(query, TestContext.CancellationToken);

        await inner.WaitForCallsAsync(1);

        var second = reader.RecordsAsync(query, waiting.Token);

        // Act
        await waiting.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => second);

        release.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(exception, "the waiting call ended with an OperationCanceledException");
        Assert.HasCount(1, inner.Queries, "the inner reader was called once, by the first call");
    }

    #endregion // Methods
}