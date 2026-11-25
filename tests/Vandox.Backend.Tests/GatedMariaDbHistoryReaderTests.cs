using Vandox.Backend.Logs;
using Vandox.Backend.ServiceHealth;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedMariaDbHistoryReader"/>
/// </summary>
[TestClass]
public class GatedMariaDbHistoryReaderTests
{
    #region Fields

    private static readonly MariaDbStatusQuery _statusQuery = new()
                                                              {
                                                                  From = HistorySamples.Base,
                                                                  To = HistorySamples.Base.AddHours(2),
                                                                  Step = TimeSpan.FromMinutes(5)
                                                              };

    private static readonly ThreadSnapshotQuery _stampQuery = new()
                                                              {
                                                                  From = HistorySamples.Base,
                                                                  To = HistorySamples.Base.AddHours(2),
                                                                  Limit = ThreadSnapshotQuery.MaxLimit
                                                              };

    private static readonly (DateTimeOffset At, TimeSpan Window) _closestArgument = (HistorySamples.Base.AddHours(1), TimeSpan.FromMinutes(10));

    private static readonly MariaDbStatusResult _status = ServicesSamples.StatusAnswer(_statusQuery);

    private static readonly ThreadSnapshotStamp _stamp = ServicesSamples.Stamp(7, HistorySamples.Base.AddHours(1), 30, 11, 60);

    private static readonly IReadOnlyList<ThreadSnapshotStamp> _stamps = [_stamp];

    private static readonly ThreadSnapshot _snapshot = ServicesSamples.Snapshot(HistorySamples.Base.AddHours(1), 30, 11, 19, ServicesSamples.Thread(4, 60, "SELECT 4"));

    private static readonly string[] _memberNames = [
                                                        nameof(IMariaDbHistoryReader.MariaDbStatusAsync),
                                                        nameof(IMariaDbHistoryReader.ThreadSnapshotsAsync),
                                                        nameof(IMariaDbHistoryReader.ClosestThreadSnapshotAsync),
                                                        nameof(IMariaDbHistoryReader.ThreadSnapshotAsync)
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
    [TestCategory("AC9")]
    public async Task GatedMariaDbHistoryReaderTakesOneSlotPerCall()
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
    /// A call that fails frees its slot and passes the failure on, for each of the four calls.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task GatedMariaDbHistoryReaderFreesTheSlotAfterAFailure()
    {
        // Arrange
        var problems = new List<string>();

        // Act
        for (var member = 0; member < _memberNames.Length; member++)
        {
            using var inner = new FakeMariaDbHistoryReader
                              {
                                  Failure = new StoreException("the store failed")
                              };
            using var gate = new WebReaderGate(1);

            var reader = new GatedMariaDbHistoryReader(inner, gate);
            var thrown = await Assert.ThrowsExactlyAsync<StoreException>(() => Call(reader, member, TestContext.CancellationToken), "the failure reaches the caller");
            var free = await gate.RunAsync(() => Task.FromResult(true), TestContext.CancellationToken);

            AddProblem(problems, thrown.Message != "the store failed", $"{_memberNames[member]}: the failure itself");
            AddProblem(problems, free == false, $"{_memberNames[member]}: the slot is not free after the failure");
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
    private static async Task<object?> Call(GatedMariaDbHistoryReader reader, int member, CancellationToken cancellationToken)
    {
        return member switch
               {
                   0 => await reader.MariaDbStatusAsync(_statusQuery, cancellationToken),
                   1 => await reader.ThreadSnapshotsAsync(_stampQuery, cancellationToken),
                   2 => await reader.ClosestThreadSnapshotAsync(_closestArgument.At, _closestArgument.Window, cancellationToken),
                   _ => await reader.ThreadSnapshotAsync(7, cancellationToken)
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
                   0 => _statusQuery,
                   1 => _stampQuery,
                   2 => _closestArgument,
                   _ => 7L
               };
    }

    /// <summary>
    /// Gets the answer the inner reader gives for a member.
    /// </summary>
    /// <param name="member">The index of the member</param>
    /// <returns>The answer</returns>
    private static object Answer(int member)
    {
        return member switch
               {
                   0 => _status,
                   1 => _stamps,
                   2 => _stamp,
                   _ => _snapshot
               };
    }

    /// <summary>
    /// Creates an inner reader with distinct answers that holds its calls until a task completes.
    /// </summary>
    /// <param name="hold">The task the calls wait for</param>
    /// <returns>The inner reader</returns>
    private static FakeMariaDbHistoryReader HeldReader(Task hold)
    {
        var reader = new FakeMariaDbHistoryReader
                     {
                         Hold = hold,
                         Status = _status,
                         Closest = _stamp
                     };

        reader.Stamps.Add(_stamp);
        reader.Snapshots[7] = _snapshot;

        return reader;
    }

    /// <summary>
    /// Tells whether an answer is the one the inner reader holds: the same object, or for the list of stamps the same stamps.
    /// </summary>
    /// <param name="answer">The answer of the gated reader</param>
    /// <param name="expected">The answer of the inner reader</param>
    /// <returns><c>true</c> when the answer is the expected one</returns>
    private static bool SameAnswer(object? answer, object expected)
    {
        if (expected is IReadOnlyList<ThreadSnapshotStamp> stamps && answer is IReadOnlyList<ThreadSnapshotStamp> given)
        {
            return given.Count == stamps.Count && given.Zip(stamps).All(pair => ReferenceEquals(pair.First, pair.Second));
        }

        return ReferenceEquals(answer, expected);
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

        var reader = new GatedMariaDbHistoryReader(inner, gate);
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
        var expected = Answer(member);

        AddProblem(problems, entered == false, $"{name}: the first call never reached the inner reader");
        AddProblem(problems, whileHeld != 1, $"{name}: the inner reader sees {whileHeld} calls while the slot is taken, not 1");
        AddProblem(problems, inner.MaxInFlight != 1, $"{name}: {inner.MaxInFlight} calls in flight at once");
        AddProblem(problems, calls.Count != 2, $"{name}: {calls.Count} calls reached the inner reader, not 2");
        AddProblem(problems, calls.Any(call => call.Name != name), $"{name}: a call reached another member of the inner reader");
        AddProblem(problems, calls.Any(call => Equals(call.Argument, Argument(member)) == false), $"{name}: the argument does not reach the inner reader");
        AddProblem(problems, calls.Count == 2 && calls[1].Token != secondSource.Token, $"{name}: the token of the call does not reach the inner reader");
        AddProblem(problems, SameAnswer(firstAnswer, expected) == false || SameAnswer(secondAnswer, expected) == false, $"{name}: the answer of the inner reader does not reach the caller");
        AddProblem(problems, free == false, $"{name}: the slot is not free after both calls ended");
    }

    #endregion // Methods
}