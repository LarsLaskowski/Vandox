using Vandox.Backend.Logs;
using Vandox.Backend.TimeSeries;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedTimeSeriesReader"/>
/// </summary>
[TestClass]
public class GatedTimeSeriesReaderTests
{
    #region Fields

    private static readonly DateTimeOffset _from = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    private static readonly TimeRangeQuery _range = new()
                                                    {
                                                        From = _from,
                                                        To = _from.AddHours(1),
                                                        Limit = 5
                                                    };

    private static readonly string[] _units = ["nginx.service"];

    private static readonly string[] _memberNames = [
                                                        nameof(ITimeSeriesReader.RawValuesAsync),
                                                        nameof(ITimeSeriesReader.RollupStepsAsync),
                                                        nameof(ITimeSeriesReader.GapsInRangeAsync),
                                                        nameof(ITimeSeriesReader.EarliestUnfoldedAsync),
                                                        nameof(ITimeSeriesReader.ServiceChangesAsync),
                                                        nameof(ITimeSeriesReader.PreviousServiceStatesAsync),
                                                        nameof(ITimeSeriesReader.BootTimesAsync),
                                                        nameof(ITimeSeriesReader.GapRecordsAsync),
                                                        nameof(ITimeSeriesReader.KernelEventsAsync),
                                                        nameof(ITimeSeriesReader.BackupRunsAsync)
                                                    ];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Every member of the reader waits for the only slot of the gate, holds it while the inner read runs and frees it afterwards: the second call of the same
    /// member reaches the inner reader only after the first one ended, with its own token, and a free slot is available afterwards.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task EveryCallHoldsTheGateOnlyWhileItRuns()
    {
        // Arrange
        var problems = new List<string>();

        // Act
        for (var member = 0; member < _memberNames.Length; member++)
        {
            await CheckMemberAsync(member, problems);
        }

        // Assert
        Assert.IsEmpty(problems, string.Join("; ", problems));
    }

    /// <summary>
    /// A query and its answer pass the gate unchanged: the inner reader gets the very query object, and the answer of the inner reader reaches the caller.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task GatedTimeSeriesReaderPassesQueryAndAnswer()
    {
        // Arrange
        using var inner = new ProbeTimeSeriesReader();
        using var gate = new WebReaderGate(1);

        var reader = new GatedTimeSeriesReader(inner, gate);
        var query = new RawValueQuery
                    {
                        Source = "proc.meminfo",
                        Name = "mem_available_bytes",
                        From = _from,
                        To = _from.AddHours(1),
                        MaxTimes = 10,
                        MaxLines = 3
                    };

        // Act
        var page = await reader.RawValuesAsync(query, TestContext.CancellationToken);
        var earliest = await reader.EarliestUnfoldedAsync(TestContext.CancellationToken);
        var gaps = await reader.GapsInRangeAsync(_range, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(page, "the page of the inner reader reaches the caller");
        Assert.IsNull(earliest, "the answer of the inner reader reaches the caller");
        Assert.IsNotNull(gaps, "the gaps of the inner reader reach the caller");
        Assert.AreSame(query, inner.Calls[0].Argument, "the inner reader gets the query object");
        Assert.AreSame(_range, inner.Calls[2].Argument, "the inner reader gets the range object");
    }

    /// <summary>
    /// A call that waits for the slot ends with its token, never reaches the inner reader and holds no slot: the slot is free after the holder ended.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task CancelledWaitForSlotThrowsAndHoldsNothing()
    {
        // Arrange
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var inner = new ProbeTimeSeriesReader();
        using var gate = new WebReaderGate(1);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var reader = new GatedTimeSeriesReader(inner, gate);
        var holder = gate.RunAsync(async () =>
                                   {
                                       await hold.Task;

                                       return 0;
                                   },
                                   TestContext.CancellationToken);
        var second = reader.BootTimesAsync(_range, waiting.Token);

        // Act
        await waiting.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => second);

        hold.SetResult();
        await holder;

        var free = await gate.RunAsync(() => Task.FromResult(true), TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(exception, "the waiting call ended with an OperationCanceledException");
        Assert.IsEmpty(inner.Calls, "the inner reader was never called");
        Assert.IsTrue(free, "the cancelled call holds no slot");
    }

    /// <summary>
    /// Adds a line to the problems when the condition holds.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="violated">Whether the expectation is violated</param>
    /// <param name="text">The line</param>
    private static void AddProblem(List<string> problems, bool violated, string text)
    {
        if (violated)
        {
            problems.Add(text);
        }
    }

    /// <summary>
    /// Makes one call of a member of the reader.
    /// </summary>
    /// <param name="reader">The reader</param>
    /// <param name="member">The index of the member, in the order of the interface</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>The task of the call</returns>
    private static Task Call(GatedTimeSeriesReader reader, int member, CancellationToken cancellationToken)
    {
        return member switch
               {
                   0 => reader.RawValuesAsync(new RawValueQuery(), cancellationToken),
                   1 => reader.RollupStepsAsync(new RollupStepQuery(), cancellationToken),
                   2 => reader.GapsInRangeAsync(_range, cancellationToken),
                   3 => reader.EarliestUnfoldedAsync(cancellationToken),
                   4 => reader.ServiceChangesAsync(_range, cancellationToken),
                   5 => reader.PreviousServiceStatesAsync(_units, _from, cancellationToken),
                   6 => reader.BootTimesAsync(_range, cancellationToken),
                   7 => reader.GapRecordsAsync(_range, cancellationToken),
                   8 => reader.KernelEventsAsync(_range, cancellationToken),
                   _ => reader.BackupRunsAsync(_range, cancellationToken)
               };
    }

    /// <summary>
    /// Checks that one member holds the only slot of a gate while its inner read runs and frees it afterwards.
    /// </summary>
    /// <param name="member">The index of the member</param>
    /// <param name="problems">Receives a line per violated expectation</param>
    /// <returns>A task that completes when the member was checked</returns>
    private async Task CheckMemberAsync(int member, List<string> problems)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var inner = new ProbeTimeSeriesReader
                          {
                              Hold = release.Task
                          };
        using var gate = new WebReaderGate(1);
        using var secondSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var reader = new GatedTimeSeriesReader(inner, gate);
        var first = Call(reader, member, TestContext.CancellationToken);

        await inner.WaitForEntryAsync(TestContext.CancellationToken);

        var second = Call(reader, member, secondSource.Token);
        var whileHeld = inner.Calls.Count;

        release.SetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);

        var free = await gate.RunAsync(() => Task.FromResult(true), TestContext.CancellationToken);
        var name = _memberNames[member];

        AddProblem(problems, whileHeld != 1, $"{name}: the inner reader sees {whileHeld} calls while the slot is taken, not 1");
        AddProblem(problems, inner.MaxInFlight != 1, $"{name}: {inner.MaxInFlight} calls in flight at once");
        AddProblem(problems, inner.Calls.Count != 2, $"{name}: {inner.Calls.Count} calls reached the inner reader, not 2");
        AddProblem(problems, inner.Calls.Any(call => call.Name != name), $"{name}: a call reached another member of the inner reader");
        AddProblem(problems, inner.Calls.Count == 2 && inner.Calls[1].Token != secondSource.Token, $"{name}: the token of the call does not reach the inner reader");
        AddProblem(problems, free == false, $"{name}: the slot is not free after both calls ended");
    }

    #endregion // Methods
}