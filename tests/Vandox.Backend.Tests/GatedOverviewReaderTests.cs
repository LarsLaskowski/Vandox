using Vandox.Backend.Logs;
using Vandox.Backend.Overview;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedOverviewReader"/>
/// </summary>
[TestClass]
public class GatedOverviewReaderTests
{
    #region Fields

    /// <summary>
    /// The bound the calls pass.
    /// </summary>
    private static readonly DateTimeOffset _until = new(2026, 10, 1, 10, 1, 30, TimeSpan.Zero);

    /// <summary>
    /// The query of the latest values.
    /// </summary>
    private static readonly LatestValueQuery _valueQuery = new()
                                                           {
                                                               Series = [new SeriesKey("system", "mem_available_bytes")],
                                                               Window = TimeSpan.FromMinutes(10),
                                                               MaxLabelSets = 16,
                                                               Until = _until
                                                           };

    /// <summary>
    /// The query of the service states.
    /// </summary>
    private static readonly LatestServiceQuery _serviceQuery = new()
                                                               {
                                                                   Retirement = TimeSpan.FromHours(2),
                                                                   Limit = 256,
                                                                   Until = _until
                                                               };

    /// <summary>
    /// The member names in the order of <see cref="Call"/>.
    /// </summary>
    private static readonly string[] _memberNames = [
                                                        nameof(IOverviewReader.FreshnessAsync),
                                                        nameof(IOverviewReader.LatestValuesAsync),
                                                        nameof(IOverviewReader.LatestMariaDbStatusAsync),
                                                        nameof(IOverviewReader.LatestServiceStatesAsync)
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
    /// Each of the four calls takes one slot of the gate while it runs, so a second call waits; each passes its argument and token to the inner reader, returns its answer and frees the slot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task GatedOverviewReaderTakesOneGateSlotPerCall()
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
    /// Adds a problem when a check is violated.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="violated">Whether the check is violated</param>
    /// <param name="text">The problem</param>
    private static void AddProblem(List<string> problems, bool violated, string text)
    {
        if (violated)
        {
            problems.Add(text);
        }
    }

    /// <summary>
    /// Calls a member of the gated reader.
    /// </summary>
    /// <param name="reader">The gated reader</param>
    /// <param name="member">The index of the member</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    private static async Task<object?> Call(GatedOverviewReader reader, int member, CancellationToken cancellationToken)
    {
        return member switch
               {
                   0 => await reader.FreshnessAsync(OverviewView.LiveScanLimit, _until, cancellationToken),
                   1 => await reader.LatestValuesAsync(_valueQuery, cancellationToken),
                   2 => await reader.LatestMariaDbStatusAsync(_until, cancellationToken),
                   _ => await reader.LatestServiceStatesAsync(_serviceQuery, cancellationToken)
               };
    }

    /// <summary>
    /// Gets the argument the inner reader records for a member.
    /// </summary>
    /// <param name="member">The index of the member</param>
    /// <returns>The argument</returns>
    private static object Argument(int member)
    {
        return member switch
               {
                   0 => (OverviewView.LiveScanLimit, _until),
                   1 => _valueQuery,
                   2 => _until,
                   _ => _serviceQuery
               };
    }

    /// <summary>
    /// Gets the answer the inner reader gives for a member.
    /// </summary>
    /// <param name="inner">The inner reader</param>
    /// <param name="member">The index of the member</param>
    /// <returns>The answer</returns>
    private static object? Answer(FakeOverviewReader inner, int member)
    {
        return member switch
               {
                   0 => inner.Freshness,
                   1 => inner.Values,
                   2 => inner.MariaDb,
                   _ => inner.Services
               };
    }

    /// <summary>
    /// Creates an inner reader with distinct answers that holds its calls until a task completes.
    /// </summary>
    /// <param name="hold">The task the calls wait for</param>
    /// <returns>The inner reader</returns>
    private static FakeOverviewReader HeldReader(Task hold)
    {
        return new FakeOverviewReader
               {
                   Hold = hold,
                   Freshness = new AgentFreshness
                               {
                                   AgentId = "agent-1"
                               },
                   Values = [new LatestSeries()],
                   MariaDb = new MariaDbLatest
                             {
                                 Availability = "up"
                             },
                   Services = new ServiceStatePage
                              {
                                  Total = 3
                              }
               };
    }

    /// <summary>
    /// Checks one member: a held first call takes the only slot, a second call waits, both reach the inner reader with their argument and token, return its answer, and the slot is free afterwards.
    /// </summary>
    /// <param name="member">The index of the member</param>
    /// <param name="problems">Receives the problems found</param>
    /// <returns>A task that completes when the member is checked</returns>
    private async Task CheckMemberAsync(int member, List<string> problems)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var inner = HeldReader(release.Task);
        using var gate = new WebReaderGate(1);
        using var secondSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var reader = new GatedOverviewReader(inner, gate);
        var first = Call(reader, member, TestContext.CancellationToken);
        var entry = inner.WaitForEntryAsync(TestContext.CancellationToken);

        await Task.WhenAny(entry, first);

        var entered = entry.IsCompletedSuccessfully && entry.Result;
        var second = Call(reader, member, secondSource.Token);
        var whileHeld = inner.Calls.Count;

        release.SetResult();

        var firstAnswer = await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        var secondAnswer = await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        var free = await gate.RunAsync(() => Task.FromResult(true), TestContext.CancellationToken);
        var name = _memberNames[member];
        var calls = inner.Calls;

        AddProblem(problems, entered == false, $"{name}: the first call never reached the inner reader");
        AddProblem(problems, whileHeld != 1, $"{name}: the inner reader sees {whileHeld} calls while the slot is taken, not 1");
        AddProblem(problems, inner.MaxInFlight != 1, $"{name}: {inner.MaxInFlight} calls in flight at once");
        AddProblem(problems, calls.Count != 2, $"{name}: {calls.Count} calls reached the inner reader, not 2");
        AddProblem(problems, calls.Any(call => call.Name != name), $"{name}: a call reached another member of the inner reader");
        AddProblem(problems, calls.Any(call => Equals(call.Argument, Argument(member)) == false), $"{name}: the argument does not reach the inner reader");
        AddProblem(problems, calls.Count == 2 && calls[1].Token != secondSource.Token, $"{name}: the token of the call does not reach the inner reader");
        AddProblem(problems, ReferenceEquals(firstAnswer, Answer(inner, member)) == false || ReferenceEquals(secondAnswer, Answer(inner, member)) == false, $"{name}: the answer of the inner reader does not reach the caller");
        AddProblem(problems, free == false, $"{name}: the slot is not free after both calls ended");
    }

    #endregion // Methods
}